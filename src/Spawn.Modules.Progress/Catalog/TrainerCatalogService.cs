using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Full clean-room implementation of <see cref="ITrainerCatalogService"/>:
/// canonical English title waterfall, robots.txt-compliant FLiNG discovery,
/// explicit user-triggered single download, first-class local import (preserving <c>Zone.Identifier</c>),
/// unified magic/PE/Zip-Slip/manifest-byte verification, multi-EXE user selection,
/// atomic sandbox installation with pinned SHA-256, and pre-launch TOCTOU verification.
/// </summary>
public sealed class TrainerCatalogService : ITrainerCatalogService
{
    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly CanonicalTitleResolver _titleResolver;
    private readonly FlingCleanRoomDiscoveryClient _discoveryClient;
    private readonly TrainerSafeDownloader _downloader;
    private readonly TrainerArchiveExtractor _archiveExtractor;
    private readonly TrainerCatalogOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TrainerCatalogService> _logger;

    public TrainerCatalogService(
        SpawnDatabase database,
        SpawnPaths paths,
        CanonicalTitleResolver titleResolver,
        FlingCleanRoomDiscoveryClient discoveryClient,
        TrainerSafeDownloader downloader,
        TrainerArchiveExtractor archiveExtractor,
        TrainerCatalogOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<TrainerCatalogService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(titleResolver);
        ArgumentNullException.ThrowIfNull(discoveryClient);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(archiveExtractor);

        _database = database;
        _paths = paths;
        _titleResolver = titleResolver;
        _discoveryClient = discoveryClient;
        _downloader = downloader;
        _archiveExtractor = archiveExtractor;
        _options = options ?? new TrainerCatalogOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<TrainerCatalogService>.Instance;
    }

    public async Task<TrainerMatchResult> MatchAsync(
        string gameId,
        string? userSearchOverride = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return new TrainerMatchResult
            {
                GameId = gameId,
                EffectiveQuery = userSearchOverride?.Trim() ?? string.Empty,
                QuerySource = TrainerTitleSource.Unknown,
                Succeeded = false,
                Message = $"未找到游戏记录 '{gameId}'。",
            };
        }

        var (effectiveQuery, querySource, _) = await _titleResolver.ResolveAsync(game, userSearchOverride, ct).ConfigureAwait(false);
        TrainerSettingsDto settings = await _database.GetTrainerSettingsAsync(ct).ConfigureAwait(false);
        GameVersionFingerprint fingerprint = GameVersionFingerprintService.CaptureFingerprint(game);

        return await _discoveryClient.SearchAsync(
            gameId,
            effectiveQuery,
            querySource,
            includeArchive: settings.IncludeFlingArchiveInSearch,
            localFingerprint: fingerprint,
            ct).ConfigureAwait(false);
    }

    public async Task<TrainerCandidate> LoadCandidateDetailsAsync(
        string gameId,
        TrainerCandidate candidate,
        CancellationToken ct = default)
    {
        TrainerCandidateDetailsResult result = await LoadCandidateDetailsWithDiagnosticsAsync(gameId, candidate, ct)
            .ConfigureAwait(false);
        return result.Candidate;
    }

    public async Task<TrainerCandidateDetailsResult> LoadCandidateDetailsWithDiagnosticsAsync(
        string gameId,
        TrainerCandidate candidate,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(candidate);

        GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        GameVersionFingerprint? fingerprint = game is not null ? GameVersionFingerprintService.CaptureFingerprint(game) : null;

        var (enriched, challengeDetected, errMsg) = await _discoveryClient.LoadCandidateDetailsAsync(candidate, fingerprint, ct).ConfigureAwait(false);

        if (!challengeDetected && game is not null && !string.IsNullOrWhiteSpace(enriched.PageUrl))
        {
            var existingMap = await _database.GetTrainerTitleMapAsync(gameId, ct).ConfigureAwait(false);
            if (existingMap is not null)
            {
                await _database.UpsertTrainerTitleMapAsync(
                    existingMap with
                    {
                        FlingPageUrl = enriched.PageUrl,
                        UpdatedAtUtc = _timeProvider.GetUtcNow(),
                    },
                    ct).ConfigureAwait(false);
            }
        }

        return new TrainerCandidateDetailsResult
        {
            Candidate = enriched,
            Succeeded = !challengeDetected && string.IsNullOrWhiteSpace(errMsg),
            ChallengeDetected = challengeDetected,
            FallbackBrowserUrl = challengeDetected ? (enriched.PageUrl ?? candidate.PageUrl) : null,
            ErrorMessage = errMsg,
        };
    }

    public Task<TrainerImportResult> DownloadAsync(
        string gameId,
        TrainerCandidate candidate,
        IProgress<TrainerDownloadProgress>? progress = null,
        CancellationToken ct = default) =>
        DownloadWithExecutableSelectionAsync(gameId, candidate, selectedExecutableRelPath: null, progress, ct);

    public async Task<TrainerImportResult> DownloadWithExecutableSelectionAsync(
        string gameId,
        TrainerCandidate candidate,
        string? selectedExecutableRelPath,
        IProgress<TrainerDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(candidate);

        GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return Fail(TrainerImportFailureKind.NetworkError, $"未找到游戏记录 '{gameId}'。");
        }

        TrainerCandidate workingCandidate = candidate;
        TrainerAttachmentCandidate? attachment = workingCandidate.SelectedAttachment
            ?? (workingCandidate.Attachments.Count > 0 ? workingCandidate.Attachments[0] : null);

        if (attachment is null)
        {
            var fingerprint = GameVersionFingerprintService.CaptureFingerprint(game);
            var (loaded, challengeDetected, errMsg) = await _discoveryClient.LoadCandidateDetailsAsync(workingCandidate, fingerprint, ct)
                .ConfigureAwait(false);

            if (challengeDetected)
            {
                return Fail(
                    TrainerImportFailureKind.ChallengeOrHtmlFallback,
                    errMsg ?? "详情页触发验证，请在浏览器打开下载并拖放本地导入。",
                    fallbackBrowserUrl: workingCandidate.PageUrl);
            }

            workingCandidate = loaded;
            attachment = workingCandidate.SelectedAttachment
                ?? (workingCandidate.Attachments.Count > 0 ? workingCandidate.Attachments[0] : null);
            if (attachment is null)
            {
                return Fail(
                    TrainerImportFailureKind.NetworkError,
                    errMsg ?? "未在候选详情页中解析到可用的安全下载附件。",
                    fallbackBrowserUrl: workingCandidate.PageUrl);
            }
        }

        string sandboxRoot = await ResolveActiveSandboxRootAsync(ct).ConfigureAwait(false);
        string stagingDir = CreateUniqueStagingDirectory(sandboxRoot);

        try
        {
            TrainerDownloadOutcome download = await _downloader.DownloadToStagingAsync(
                attachment.DownloadUrl,
                workingCandidate.PageUrl,
                stagingDir,
                progress,
                ct).ConfigureAwait(false);

            if (!download.Succeeded || string.IsNullOrWhiteSpace(download.StagedFilePath))
            {
                return Fail(
                    download.FailureKind,
                    download.Message ?? "下载失败。",
                    fallbackBrowserUrl: download.FallbackBrowserUrl ?? workingCandidate.PageUrl,
                    win32ErrorCode: download.Win32ErrorCode);
            }

            string preferredExeFileName = DerivePreferredExeFileName(
                download.SuggestedFileName,
                attachment.FileName,
                workingCandidate.Title);

            return await CompleteStagedVerificationAndInstallAsync(
                game: game,
                sandboxRoot: sandboxRoot,
                stagingDir: stagingDir,
                stagedPayloadPath: download.StagedFilePath,
                preferredExeFileName: preferredExeFileName,
                provider: workingCandidate.Provider,
                displayName: workingCandidate.Title,
                versionLabel: attachment.VersionLabel ?? workingCandidate.VersionLabel,
                sourcePageUrl: workingCandidate.PageUrl,
                sourceUpdatedAt: attachment.UpdatedDateText ?? workingCandidate.UpdatedDateText,
                parsedHotkeys: workingCandidate.PreviewHotkeys,
                selectedExecutableRelPath: selectedExecutableRelPath,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectorySafely(stagingDir);
        }
    }

    public Task<TrainerImportResult> ImportLocalAsync(
        string gameId,
        string userSelectedPath,
        CancellationToken ct = default) =>
        ImportLocalWithExecutableSelectionAsync(gameId, userSelectedPath, selectedExecutableRelPath: null, ct);

    public async Task<TrainerImportResult> ImportLocalReplacingAsync(
        string gameId,
        string userSelectedPath,
        string? replaceTrainerId,
        bool inheritManualHotkeys = true,
        string? selectedExecutableRelPath = null,
        CancellationToken ct = default)
    {
        List<TrainerHotkeyBindingRecord> inheritedManualHotkeys = [];
        TrainerCatalogRecord? oldTrainer = null;
        if (!string.IsNullOrWhiteSpace(replaceTrainerId))
        {
            oldTrainer = await _database.GetTrainerByIdAsync(replaceTrainerId, ct).ConfigureAwait(false);
            if (oldTrainer is not null
                && string.Equals(oldTrainer.GameId, gameId, StringComparison.OrdinalIgnoreCase)
                && inheritManualHotkeys)
            {
                IReadOnlyList<TrainerHotkeyBindingRecord> oldBindings = await _database
                    .GetTrainerHotkeyBindingsAsync(oldTrainer.TrainerId, ct)
                    .ConfigureAwait(false);
                inheritedManualHotkeys = oldBindings
                    .Where(static b => b.EntryId.StartsWith("manual-", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static b => b.SortOrder)
                    .ToList();
            }
        }

        // Import & verify the replacement BEFORE removing the previous trainer so failures never leave the game unbound.
        TrainerImportResult importResult = await ImportLocalWithExecutableSelectionAsync(
            gameId,
            userSelectedPath,
            selectedExecutableRelPath,
            ct).ConfigureAwait(false);

        if (!importResult.Succeeded || importResult.Trainer is null)
        {
            return importResult;
        }

        string newTrainerId = importResult.Trainer.TrainerId;
        IReadOnlyList<TrainerHotkeyBindingRecord> finalHotkeys = importResult.Hotkeys;

        if (inheritedManualHotkeys.Count > 0)
        {
            var merged = new List<TrainerHotkeyBindingRecord>(importResult.Hotkeys.Count + inheritedManualHotkeys.Count);
            merged.AddRange(importResult.Hotkeys);
            var existingIds = new HashSet<string>(merged.Select(static h => h.EntryId), StringComparer.OrdinalIgnoreCase);

            foreach (TrainerHotkeyBindingRecord manual in inheritedManualHotkeys)
            {
                string nextEntryId = existingIds.Add(manual.EntryId)
                    ? manual.EntryId
                    : $"manual-{SpawnId.NewUlid()}";
                merged.Add(manual with
                {
                    TrainerId = newTrainerId,
                    EntryId = nextEntryId,
                    ProxyState = false,
                    SortOrder = merged.Count,
                });
            }

            await _database.ReplaceTrainerHotkeyBindingsAsync(newTrainerId, merged, ct).ConfigureAwait(false);
            finalHotkeys = merged;
        }

        if (oldTrainer is not null
            && !string.Equals(oldTrainer.TrainerId, newTrainerId, StringComparison.OrdinalIgnoreCase))
        {
            await RemoveAsync(oldTrainer.TrainerId, ct).ConfigureAwait(false);
        }

        string replaceSuffix = oldTrainer is not null
            ? (inheritedManualHotkeys.Count > 0
                ? $"（已覆盖旧版本并继承 {inheritedManualHotkeys.Count} 条手动热键）"
                : "（已覆盖旧版本）")
            : string.Empty;

        return importResult with
        {
            Hotkeys = finalHotkeys,
            Message = $"{importResult.Message}{replaceSuffix}",
        };
    }

    public async Task<TrainerImportResult> ImportLocalWithExecutableSelectionAsync(
        string gameId,
        string userSelectedPath,
        string? selectedExecutableRelPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        if (string.IsNullOrWhiteSpace(userSelectedPath))
        {
            return Fail(TrainerImportFailureKind.InvalidMagicOrFormat, "请选择有效的本地修改器文件。");
        }

        GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return Fail(TrainerImportFailureKind.InvalidMagicOrFormat, $"未找到游戏记录 '{gameId}'。");
        }

        string fullSourcePath;
        try
        {
            fullSourcePath = Path.GetFullPath(userSelectedPath.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            return Fail(TrainerImportFailureKind.InvalidMagicOrFormat, $"无效的本地文件路径：{ex.Message}");
        }

        if (!File.Exists(fullSourcePath))
        {
            return Fail(TrainerImportFailureKind.InvalidMagicOrFormat, $"本地文件不存在：{fullSourcePath}");
        }

        FileAttributes sourceAttrs;
        long sourceLength;
        try
        {
            sourceAttrs = File.GetAttributes(fullSourcePath);
            if ((sourceAttrs & FileAttributes.ReparsePoint) != 0)
            {
                return Fail(TrainerImportFailureKind.ZipSlipRejected, "拒绝导入符号链接或重解析点文件。");
            }

            sourceLength = new FileInfo(fullSourcePath).Length;
        }
        catch (Exception ex)
        {
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
            return Fail(
                kind != TrainerImportFailureKind.None ? kind : TrainerImportFailureKind.InvalidMagicOrFormat,
                $"无法读取本地文件：{ex.Message}",
                win32ErrorCode: win32);
        }

        if (sourceLength <= 0)
        {
            return Fail(TrainerImportFailureKind.InvalidMagicOrFormat, "本地文件为空。");
        }

        if (sourceLength > _options.MaxDownloadBytes)
        {
            return Fail(
                TrainerImportFailureKind.SizeLimitExceeded,
                $"本地文件体积 ({sourceLength} 字节) 超过 {_options.MaxDownloadBytes} 字节上限。");
        }

        string sandboxRoot = await ResolveActiveSandboxRootAsync(ct).ConfigureAwait(false);
        string stagingDir = CreateUniqueStagingDirectory(sandboxRoot);

        try
        {
            string sourceFileName = Path.GetFileName(fullSourcePath);
            string stagedPayloadPath = Path.Combine(stagingDir, sourceFileName);

            // File.Copy invokes CopyFileEx on NTFS, preserving :Zone.Identifier ADS without ever stripping MOTW.
            try
            {
                File.Copy(fullSourcePath, stagedPayloadPath, overwrite: true);
            }
            catch (Exception ex)
            {
                int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
                var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
                return Fail(
                    kind != TrainerImportFailureKind.None ? kind : TrainerImportFailureKind.InvalidMagicOrFormat,
                    kind == TrainerImportFailureKind.AntivirusQuarantined
                        ? $"导入本地文件时触发杀毒软件拦截/隔离（Win32 {win32}）。"
                        : $"复制本地文件至沙箱暂存区失败：{ex.Message}",
                    win32ErrorCode: win32);
            }

            string displayName = Path.GetFileNameWithoutExtension(sourceFileName);
            return await CompleteStagedVerificationAndInstallAsync(
                game: game,
                sandboxRoot: sandboxRoot,
                stagingDir: stagingDir,
                stagedPayloadPath: stagedPayloadPath,
                preferredExeFileName: sourceFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? sourceFileName
                    : displayName + ".exe",
                provider: TrainerProviderKind.LocalImport,
                displayName: displayName,
                versionLabel: null,
                sourcePageUrl: null,
                sourceUpdatedAt: null,
                parsedHotkeys: [],
                selectedExecutableRelPath: selectedExecutableRelPath,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectorySafely(stagingDir);
        }
    }

    public async Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);

        TrainerCatalogRecord? trainer = await _database.GetTrainerByIdAsync(trainerId, ct).ConfigureAwait(false);
        if (trainer is null)
        {
            return new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = false,
                FileExists = false,
                HashMatches = false,
                IsInsideSandbox = false,
                FailureMessage = $"未找到修改器记录 '{trainerId}'。",
            };
        }

        string sandboxRoot = await ResolveActiveSandboxRootAsync(ct).ConfigureAwait(false);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot));
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

        string fullExePath;
        try
        {
            fullExePath = Path.GetFullPath(Path.Combine(normalizedRoot, trainer.SandboxRelativePath));
        }
        catch (Exception ex)
        {
            return new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = false,
                FileExists = false,
                HashMatches = false,
                IsInsideSandbox = false,
                ExpectedSha256 = trainer.FileSha256,
                FailureMessage = $"沙箱路径无效：{ex.Message}",
            };
        }

        bool isInsideSandbox = fullExePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !HasReparsePointOnPath(normalizedRoot, fullExePath);

        if (!isInsideSandbox)
        {
            return new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = false,
                FileExists = File.Exists(fullExePath),
                HashMatches = false,
                IsInsideSandbox = false,
                ExpectedSha256 = trainer.FileSha256,
                FailureMessage = "修改器可执行路径越界沙箱根目录或包含重解析点，已拒绝启动。",
            };
        }

        if (!File.Exists(fullExePath))
        {
            return new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = false,
                FileExists = false,
                HashMatches = false,
                IsInsideSandbox = true,
                ExpectedSha256 = trainer.FileSha256,
                FailureMessage = "修改器文件已缺失（可能被杀毒软件隔离或外部删除），请检查隔离区或重新导入。",
            };
        }

        string actualSha256;
        try
        {
            actualSha256 = await PeBinaryInspector.ComputeSha256HexAsync(fullExePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            return new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = false,
                FileExists = true,
                HashMatches = false,
                IsInsideSandbox = true,
                ExpectedSha256 = trainer.FileSha256,
                Win32ErrorCode = win32,
                FailureMessage = win32 is PeBinaryInspector.ErrorVirusInfected or PeBinaryInspector.ErrorVirusDeleted
                    ? $"启动前复核时文件被杀毒软件阻止或隔离（Win32 {win32}）。"
                    : $"启动前复核 SHA-256 读取失败：{ex.Message}",
            };
        }

        bool hashMatches = string.Equals(actualSha256, trainer.FileSha256, StringComparison.OrdinalIgnoreCase);
        if (!hashMatches)
        {
            return new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = false,
                FileExists = true,
                HashMatches = false,
                IsInsideSandbox = true,
                ExpectedSha256 = trainer.FileSha256,
                ActualSha256 = actualSha256,
                FailureMessage = "修改器文件 SHA-256 钉扎复核不匹配（TOCTOU 篡改检测），已拒绝启动，请重新导入。",
            };
        }

        var (launchPath, _) = PeBinaryInspector.TryResolveLaunchPath(fullExePath);

        // Non-blocking game version drift check.
        bool gameChanged = false;
        string? gameNotice = null;
        GameRecord? game = await _database.GetGameByIdAsync(trainer.GameId, ct).ConfigureAwait(false);
        if (game is not null)
        {
            var currentFp = GameVersionFingerprintService.CaptureFingerprint(game);
            (gameChanged, gameNotice) = GameVersionFingerprintService.DetectGameUpdateSinceBinding(trainer, currentFp);
        }

        return new TrainerIntegrityReport
        {
            TrainerId = trainerId,
            IsValid = true,
            FileExists = true,
            HashMatches = true,
            IsInsideSandbox = true,
            ExpectedSha256 = trainer.FileSha256,
            ActualSha256 = actualSha256,
            ResolvedExecutablePath = launchPath,
            GameFilesChangedSinceBinding = gameChanged,
            GameUpdateNotice = gameNotice,
        };
    }

    public async Task RemoveAsync(string trainerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trainerId))
        {
            return;
        }

        TrainerCatalogRecord? existing = await _database.GetTrainerByIdAsync(trainerId, ct).ConfigureAwait(false);
        await _database.DeleteTrainerAsync(trainerId, ct).ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        try
        {
            string sandboxRoot = await ResolveActiveSandboxRootAsync(ct).ConfigureAwait(false);
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot));
            string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            string trainerDir = Path.GetFullPath(Path.Combine(normalizedRoot, SanitizeSegment(existing.GameId), SanitizeSegment(existing.TrainerId)));

            if (trainerDir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(trainerDir))
            {
                TryDeleteDirectorySafely(trainerDir);
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not clean sandbox directory for removed trainer {TrainerId}.", trainerId);
            }
        }
    }

    public Task<IReadOnlyList<TrainerCatalogRecord>> ListForGameAsync(string gameId, CancellationToken ct = default) =>
        _database.GetTrainersByGameIdAsync(gameId, ct);

    public Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
        _database.GetTrainerHotkeyBindingsAsync(trainerId, ct);

    public Task SaveHotkeyBindingsAsync(
        string trainerId,
        IReadOnlyList<TrainerHotkeyBindingRecord> bindings,
        CancellationToken ct = default) =>
        _database.ReplaceTrainerHotkeyBindingsAsync(trainerId, bindings, ct);

    private async Task<TrainerImportResult> CompleteStagedVerificationAndInstallAsync(
        GameRecord game,
        string sandboxRoot,
        string stagingDir,
        string stagedPayloadPath,
        string preferredExeFileName,
        TrainerProviderKind provider,
        string displayName,
        string? versionLabel,
        string? sourcePageUrl,
        string? sourceUpdatedAt,
        IReadOnlyList<TrainerHotkeyBindingRecord> parsedHotkeys,
        string? selectedExecutableRelPath,
        CancellationToken ct)
    {
        TrainerPayloadFormat format;
        try
        {
            format = PeBinaryInspector.SniffMagicFormat(stagedPayloadPath);
        }
        catch (Exception ex)
        {
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
            return Fail(
                kind != TrainerImportFailureKind.None ? kind : TrainerImportFailureKind.InvalidMagicOrFormat,
                $"嗅探文件魔数失败：{ex.Message}",
                win32ErrorCode: win32);
        }

        if (format == TrainerPayloadFormat.Unknown)
        {
            return Fail(
                TrainerImportFailureKind.InvalidMagicOrFormat,
                "文件魔数校验失败：仅接受标准 PE (.exe) 或 .zip / .rar / .7z 压缩包。");
        }

        string stagedContentDir = Path.Combine(stagingDir, "content");
        Directory.CreateDirectory(stagedContentDir);

        if (format == TrainerPayloadFormat.RawPeExecutable)
        {
            string safeExeName = SanitizeExeFileName(preferredExeFileName);
            string targetStagedExe = Path.Combine(stagedContentDir, safeExeName);
            // Same-volume File.Move preserves :Zone.Identifier ADS intact.
            File.Move(stagedPayloadPath, targetStagedExe, overwrite: true);
        }
        else
        {
            ArchiveExtractionOutcome extracted = await _archiveExtractor.ExtractAndVerifyAsync(
                stagedPayloadPath,
                format,
                stagedContentDir,
                ct).ConfigureAwait(false);

            if (!extracted.Succeeded)
            {
                return Fail(
                    extracted.FailureKind,
                    extracted.Message ?? "压缩包安全校验或解压失败。",
                    fallbackBrowserUrl: sourcePageUrl,
                    win32ErrorCode: extracted.Win32ErrorCode);
            }
        }

        // Discover and structurally validate every .exe inside stagedContentDir via PEReader.
        var validExes = new List<(string RelativePath, string FullPath, PeInspectionSummary Summary)>();
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.None,
            };

            foreach (string exePath in Directory.EnumerateFiles(stagedContentDir, "*.exe", opts))
            {
                if ((File.GetAttributes(exePath) & FileAttributes.ReparsePoint) != 0)
                {
                    return Fail(TrainerImportFailureKind.ZipSlipRejected, "解压产物中包含重解析点可执行文件，已拒绝。");
                }

                PeInspectionSummary summary = PeBinaryInspector.InspectExecutable(exePath);
                if (summary.IsValidPe)
                {
                    string rel = Path.GetRelativePath(stagedContentDir, exePath);
                    validExes.Add((rel, exePath, summary));
                }
            }
        }
        catch (Exception ex)
        {
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
            return Fail(
                kind != TrainerImportFailureKind.None ? kind : TrainerImportFailureKind.InvalidMagicOrFormat,
                kind == TrainerImportFailureKind.AntivirusQuarantined
                    ? $"校验 PE 文件头时触发杀毒软件拦截/隔离（Win32 {win32}）。"
                    : $"校验可执行文件失败：{ex.Message}",
                win32ErrorCode: win32);
        }

        if (validExes.Count == 0)
        {
            return Fail(
                TrainerImportFailureKind.InvalidMagicOrFormat,
                "未检测到结构有效的 Windows PE (.exe) 可执行文件。");
        }

        validExes.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));

        (string RelativePath, string FullPath, PeInspectionSummary Summary) chosen;
        if (validExes.Count == 1)
        {
            chosen = validExes[0];
        }
        else
        {
            string? normalizedSelection = string.IsNullOrWhiteSpace(selectedExecutableRelPath)
                ? null
                : selectedExecutableRelPath.Trim().Replace('/', Path.DirectorySeparatorChar);

            int idx = normalizedSelection is not null
                ? validExes.FindIndex(e => string.Equals(
                    e.RelativePath.Replace('/', Path.DirectorySeparatorChar),
                    normalizedSelection,
                    StringComparison.OrdinalIgnoreCase))
                : -1;

            if (idx < 0)
            {
                var choices = validExes
                    .Select(e => new TrainerExecutableChoice
                    {
                        RelativePath = e.RelativePath,
                        FileSize = e.Summary.FileSize,
                        PeMachine = e.Summary.Machine,
                        RequiresAdmin = e.Summary.RequiresAdmin,
                    })
                    .ToList();

                return new TrainerImportResult
                {
                    Succeeded = false,
                    FailureKind = TrainerImportFailureKind.MultipleExecutablesSelectionRequired,
                    Message = $"检测到 {choices.Count} 个可执行文件，请手动选择主修改器程序（辅助 EXE 绝不自动执行）。",
                    FallbackBrowserUrl = sourcePageUrl,
                    CandidateExecutables = choices,
                };
            }

            chosen = validExes[idx];
        }

        // Atomically commit stagedContentDir -> <SandboxRoot>\<GameId>\<TrainerId>\ (same volume atomic rename).
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string trainerId = SpawnId.NewUlid(now);
        string gameSandboxDir = Path.Combine(sandboxRoot, SanitizeSegment(game.GameId));
        Directory.CreateDirectory(gameSandboxDir);
        string finalTrainerDir = Path.Combine(gameSandboxDir, SanitizeSegment(trainerId));

        try
        {
            Directory.Move(stagedContentDir, finalTrainerDir);
        }
        catch (Exception ex)
        {
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
            return Fail(
                kind != TrainerImportFailureKind.None ? kind : TrainerImportFailureKind.InvalidMagicOrFormat,
                $"原子移入沙箱目录失败：{ex.Message}",
                win32ErrorCode: win32);
        }

        string finalExePath = Path.GetFullPath(Path.Combine(finalTrainerDir, chosen.RelativePath));
        string pinnedSha256;
        try
        {
            pinnedSha256 = await PeBinaryInspector.ComputeSha256HexAsync(finalExePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDeleteDirectorySafely(finalTrainerDir);
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
            return Fail(
                kind != TrainerImportFailureKind.None ? kind : TrainerImportFailureKind.AntivirusQuarantined,
                $"计算钉扎 SHA-256 失败：{ex.Message}",
                win32ErrorCode: win32);
        }

        string sandboxRelativePath = Path.GetRelativePath(sandboxRoot, finalExePath);
        GameVersionFingerprint fingerprint = GameVersionFingerprintService.CaptureFingerprint(game);
        TrainerParseMode parseMode = parsedHotkeys.Count > 0
            ? TrainerParseMode.Structured
            : TrainerParseMode.FreeHosted;

        var record = new TrainerCatalogRecord
        {
            TrainerId = trainerId,
            GameId = game.GameId,
            Provider = provider,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(finalExePath) : displayName.Trim(),
            VersionLabel = versionLabel,
            SourcePageUrl = sourcePageUrl,
            SourceUpdatedAt = sourceUpdatedAt,
            SandboxRelativePath = sandboxRelativePath,
            FileSha256 = pinnedSha256,
            FileSize = chosen.Summary.FileSize,
            PeMachine = chosen.Summary.Machine,
            RequiresAdmin = chosen.Summary.RequiresAdmin,
            AuthenticodeStatus = chosen.Summary.AuthenticodeStatus,
            ParseMode = parseMode,
            BoundBuildId = fingerprint.SteamBuildId,
            BoundExeSize = fingerprint.ExeSize,
            BoundExeLastWriteUtc = fingerprint.ExeLastWriteUtc,
            ImportedAtUtc = now,
        };

        await _database.UpsertTrainerAsync(record, ct).ConfigureAwait(false);

        List<TrainerHotkeyBindingRecord> persistedHotkeys = [];
        if (parsedHotkeys.Count > 0)
        {
            persistedHotkeys = parsedHotkeys
                .Select((hk, i) => hk with
                {
                    TrainerId = trainerId,
                    SortOrder = i,
                })
                .ToList();
            await _database.ReplaceTrainerHotkeyBindingsAsync(trainerId, persistedHotkeys, ct).ConfigureAwait(false);
        }

        return new TrainerImportResult
        {
            Succeeded = true,
            FailureKind = TrainerImportFailureKind.None,
            Message = parseMode == TrainerParseMode.Structured
                ? $"修改器已安全导入并解析 {persistedHotkeys.Count} 项热键，SHA-256 已钉扎。"
                : "修改器已安全导入（自由托管模式），SHA-256 已钉扎。",
            Trainer = record,
            Hotkeys = persistedHotkeys,
        };
    }

    private async Task<string> ResolveActiveSandboxRootAsync(CancellationToken ct)
    {
        TrainerSettingsDto settings = await _database.GetTrainerSettingsAsync(ct).ConfigureAwait(false);
        string sandboxRoot = _paths.ResolveTrainerSandboxDirectory(settings.CustomSandboxRoot, requirePureAscii: false);
        Directory.CreateDirectory(sandboxRoot);
        return sandboxRoot;
    }

    private static string CreateUniqueStagingDirectory(string sandboxRoot)
    {
        string stagingRoot = Path.Combine(sandboxRoot, ".staging");
        string unique = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unique);
        return unique;
    }

    private static string DerivePreferredExeFileName(string? suggestedName, string? attachmentName, string? title)
    {
        foreach (string? candidate in new[] { suggestedName, attachmentName, title })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string cleaned = SanitizeExeFileName(candidate);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        return "trainer.exe";
    }

    private static string SanitizeExeFileName(string rawName)
    {
        string name = Path.GetFileName(rawName.Trim());
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 || c == ':' ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "trainer";
        }

        if (!cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(cleaned);
            cleaned = (string.IsNullOrWhiteSpace(stem) ? "trainer" : stem) + ".exe";
        }

        return cleaned;
    }

    private static string SanitizeSegment(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = raw.Select(c => c == ':' || Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "default" : cleaned;
    }

    private static bool HasReparsePointOnPath(string sandboxRoot, string fullTargetPath)
    {
        try
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot));
            string rel = Path.GetRelativePath(normalizedRoot, fullTargetPath);
            string current = normalizedRoot;
            foreach (string seg in rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, seg);
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static void TryDeleteDirectorySafely(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath))
            {
                return;
            }

            if ((File.GetAttributes(dirPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(dirPath, recursive: false);
                return;
            }

            // Delete safely without ever following nested ReparsePoints.
            foreach (string entry in Directory.EnumerateFileSystemEntries(dirPath))
            {
                FileAttributes attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        TryDeleteDirectorySafely(entry);
                    }
                }
                else
                {
                    File.Delete(entry);
                }
            }

            Directory.Delete(dirPath, recursive: false);
        }
        catch
        {
            // Best-effort cleanup of staging/sandbox directory.
        }
    }

    private static TrainerImportResult Fail(
        TrainerImportFailureKind kind,
        string message,
        string? fallbackBrowserUrl = null,
        int? win32ErrorCode = null) =>
        new()
        {
            Succeeded = false,
            FailureKind = kind,
            Message = message,
            FallbackBrowserUrl = fallbackBrowserUrl,
            Win32ErrorCode = win32ErrorCode,
        };
}
