using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Snapshots;

/// <summary>
/// Non-blocking lightweight pre-trainer save snapshot and one-click atomic rollback service
/// implementing <see cref="IPreTrainerSaveSnapshotService"/> (<c>REQ-PRG-08</c>, <c>D-25</c>).
/// </summary>
public sealed class PreTrainerSaveSnapshotService : IPreTrainerSaveSnapshotService
{
    private const int Win32ErrorSharingViolation = 32;
    private const int Win32ErrorLockViolation = 33;
    private const int MaxCopyConsistencyAttempts = 2;
    private const int MaxDirectoryWalkDepth = 32;
    private const string RestoreStageSuffix = ".spawn_stage_tmp";

    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly IGameDataManagementService _gameDataService;
    private readonly IGameLaunchService _gameLaunchService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PreTrainerSaveSnapshotService> _logger;
    private readonly Action<string, int>? _afterSingleFileCopyHook;
    private readonly long? _maxBytesOverrideForTest;

    public PreTrainerSaveSnapshotService(
        SpawnDatabase database,
        SpawnPaths paths,
        IGameDataManagementService gameDataService,
        IGameLaunchService gameLaunchService,
        TimeProvider? timeProvider = null,
        ILogger<PreTrainerSaveSnapshotService>? logger = null)
        : this(database, paths, gameDataService, gameLaunchService, timeProvider, logger, afterSingleFileCopyHook: null, maxBytesOverrideForTest: null)
    {
    }

    internal PreTrainerSaveSnapshotService(
        SpawnDatabase database,
        SpawnPaths paths,
        IGameDataManagementService gameDataService,
        IGameLaunchService gameLaunchService,
        TimeProvider? timeProvider,
        ILogger<PreTrainerSaveSnapshotService>? logger,
        Action<string, int>? afterSingleFileCopyHook,
        long? maxBytesOverrideForTest = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(gameDataService);
        ArgumentNullException.ThrowIfNull(gameLaunchService);

        _database = database;
        _paths = paths;
        _gameDataService = gameDataService;
        _gameLaunchService = gameLaunchService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PreTrainerSaveSnapshotService>.Instance;
        _afterSingleFileCopyHook = afterSingleFileCopyHook;
        _maxBytesOverrideForTest = maxBytesOverrideForTest;
    }

    public Task<PreTrainerSnapshotResult> CreateAsync(
        string gameId,
        string? trainerId,
        CancellationToken ct = default) =>
        CreateInternalAsync(
            gameId,
            trainerId,
            PreTrainerSnapshotTriggerKind.PreTrainer,
            enforceSettingsEnabled: true,
            ct);

    public async Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(
        string gameId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return [];
        }

        return await _database.GetPreTrainerSnapshotsByGameAsync(
            gameId,
            triggerKindFilter: null,
            limit: 50,
            cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<PreTrainerRestoreResult> RestoreAsync(
        string snapshotId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                Message = "快照标识不能为空。",
            };
        }

        PreTrainerSnapshotRecord? record = await _database.GetPreTrainerSnapshotByIdAsync(snapshotId, ct)
            .ConfigureAwait(false);
        if (record is null)
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                Message = $"未找到指定的存档快照：{snapshotId}",
            };
        }

        if (record.Status != PreTrainerSnapshotStatus.Completed || string.IsNullOrWhiteSpace(record.ManifestRelativePath))
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                Message = $"快照 {snapshotId} 状态为 {record.Status}，不包含可恢复的完整清单。",
            };
        }

        // Hard safety guard: never overwrite saves while the game is actively running.
        if (_gameLaunchService.GetActiveSession(record.GameId) is not null)
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                Message = "游戏当前正在运行中，已拒绝回滚存档快照，请先退出游戏后再试。",
            };
        }

        if (!PreTrainerSnapshotManifestStore.TryResolveContainedPath(
                _paths.PreTrainerSnapshotsDirectory,
                record.ManifestRelativePath,
                out string manifestFullPath)
            || !File.Exists(manifestFullPath))
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                Message = $"快照清单文件缺失或路径非法：{record.ManifestRelativePath}",
            };
        }

        PreTrainerSnapshotManifest? manifest;
        try
        {
            manifest = await PreTrainerSnapshotManifestStore.ReadManifestAsync(manifestFullPath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                Message = $"无法解析快照清单：{ex.Message}",
            };
        }

        if (manifest is null
            || !string.Equals(manifest.SnapshotId, record.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(manifest.GameId, record.GameId, StringComparison.Ordinal))
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                Message = "快照清单元数据与台账记录不一致，已中止回滚。",
            };
        }

        string snapshotDir = Path.GetDirectoryName(manifestFullPath)!;

        // Pre-validate every file payload and restore target through IGameDataManagementService safety fence BEFORE touching disk.
        var validatedFilePlans = new List<(PreTrainerSnapshotFileEntry Entry, string PayloadPath)>(manifest.Files.Count);
        foreach (var fileEntry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            if (!PreTrainerSnapshotManifestStore.TryResolveContainedPath(snapshotDir, fileEntry.SnapshotRelativePath, out string payloadPath)
                || !File.Exists(payloadPath))
            {
                return new PreTrainerRestoreResult
                {
                    Succeeded = false,
                    Message = $"快照载荷文件缺失或路径越界：{fileEntry.LogicalRelativePath}",
                };
            }

            if (IsReparsePoint(payloadPath))
            {
                return new PreTrainerRestoreResult
                {
                    Succeeded = false,
                    Message = $"快照载荷文件包含非法符号链接：{fileEntry.LogicalRelativePath}",
                };
            }

            GameDataSafetyCheckResult targetCheck = await _gameDataService
                .ValidateSaveRestoreTargetAsync(record.GameId, fileEntry.OriginalPath, ct)
                .ConfigureAwait(false);
            if (!targetCheck.IsSafe)
            {
                return new PreTrainerRestoreResult
                {
                    Succeeded = false,
                    Message = targetCheck.Reason ?? $"安全围栏已拦截非法的回滚目标路径：{fileEntry.OriginalPath}",
                };
            }

            validatedFilePlans.Add((fileEntry, payloadPath));
        }

        // Pre-validate registry snapshot payload & every HKCU restore target BEFORE touching disk or registry.
        DeterministicRegistrySnapshot? registryToRestore = null;
        if (!string.IsNullOrWhiteSpace(manifest.RegistrySnapshotRelativePath))
        {
            if (!PreTrainerSnapshotManifestStore.TryResolveContainedPath(snapshotDir, manifest.RegistrySnapshotRelativePath, out string regPayloadPath)
                || !File.Exists(regPayloadPath)
                || IsReparsePoint(regPayloadPath))
            {
                return new PreTrainerRestoreResult
                {
                    Succeeded = false,
                    Message = $"快照注册表文件缺失或路径非法：{manifest.RegistrySnapshotRelativePath}",
                };
            }

            if (!string.IsNullOrWhiteSpace(manifest.RegistrySnapshotSha256))
            {
                string actualRegSha = await PreTrainerSnapshotManifestStore.ComputeFileSha256HexAsync(regPayloadPath, ct)
                    .ConfigureAwait(false);
                if (!string.Equals(actualRegSha, manifest.RegistrySnapshotSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new PreTrainerRestoreResult
                    {
                        Succeeded = false,
                        Message = "快照注册表文件 SHA-256 校验失败，已中止回滚。",
                    };
                }
            }

            try
            {
                byte[] regBytes = await File.ReadAllBytesAsync(regPayloadPath, ct).ConfigureAwait(false);
                registryToRestore = DeterministicHkcuRegistrySerializer.Deserialize(regBytes);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                return new PreTrainerRestoreResult
                {
                    Succeeded = false,
                    Message = $"快照注册表文件解析失败：{ex.Message}",
                };
            }

            foreach (var regKey in registryToRestore.Keys)
            {
                if (!DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(regKey.KeyPath, out _, out _, out string? hkcuErr))
                {
                    return new PreTrainerRestoreResult
                    {
                        Succeeded = false,
                        Message = hkcuErr ?? $"注册表恢复目标非 HKCU 键：{regKey.KeyPath}",
                    };
                }

                GameDataSafetyCheckResult regCheck = await _gameDataService
                    .ValidateSaveRestoreTargetAsync(record.GameId, regKey.KeyPath, ct)
                    .ConfigureAwait(false);
                if (!regCheck.IsSafe)
                {
                    return new PreTrainerRestoreResult
                    {
                        Succeeded = false,
                        Message = regCheck.Reason ?? $"安全围栏已拦截非法的注册表恢复目标：{regKey.KeyPath}",
                    };
                }
            }
        }

        // Create automatic PreRestoreSafety snapshot of the current live state before overwriting anything.
        var warnings = new List<string>();
        PreTrainerSnapshotResult safetyResult = await CreateInternalAsync(
            record.GameId,
            record.TrainerId,
            PreTrainerSnapshotTriggerKind.PreRestoreSafety,
            enforceSettingsEnabled: false,
            ct).ConfigureAwait(false);

        if (safetyResult.Status == PreTrainerSnapshotStatus.Failed)
        {
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                SafetySnapshot = safetyResult.Snapshot,
                Message = $"回滚前创建 PreRestoreSafety 安全快照失败，已中止回滚以保护当前存档：{safetyResult.Message}",
            };
        }

        if (safetyResult.Status == PreTrainerSnapshotStatus.Skipped)
        {
            if (safetyResult.SkipReason == PreTrainerSnapshotSkipReason.NoSaveLocationsFound)
            {
                warnings.Add("当前现场未检测到现存存档文件，已直接执行恢复。");
            }
            else
            {
                return new PreTrainerRestoreResult
                {
                    Succeeded = false,
                    SafetySnapshot = safetyResult.Snapshot,
                    Message = $"回滚前无法备份当前存档现场（{safetyResult.SkipReason}），已中止回滚以防数据丢失：{safetyResult.Message}",
                };
            }
        }

        // Phase 1: Stage every file to <OriginalPath>.spawn_stage_tmp + Flush(true) + verify SHA-256 against manifest.
        var stagedFiles = new List<(string StagePath, string TargetPath, DateTimeOffset LastWriteUtc)>(validatedFilePlans.Count);
        try
        {
            foreach (var (entry, payloadPath) in validatedFilePlans)
            {
                ct.ThrowIfCancellationRequested();

                string targetPath = Path.GetFullPath(entry.OriginalPath);
                if (IsReparsePoint(targetPath))
                {
                    throw new IOException($"目标路径为符号链接或目录联接点，拒绝写入：{targetPath}");
                }

                string? parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                string stagePath = targetPath + RestoreStageSuffix;
                if (IsReparsePoint(stagePath))
                {
                    throw new IOException($"暂存文件路径为符号链接，拒绝写入：{stagePath}");
                }

                ClearReadOnlyIfRegularFile(stagePath);
                await StageAndVerifyFileAsync(payloadPath, stagePath, entry.SizeBytes, entry.Sha256, ct)
                    .ConfigureAwait(false);
                stagedFiles.Add((stagePath, targetPath, entry.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CleanupStagedFiles(stagedFiles);
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                SafetySnapshot = safetyResult.Snapshot,
                Message = $"回滚暂存或 SHA-256 校验失败，当前存档未被覆盖（已保留 PreRestoreSafety）：{ex.Message}",
                Warnings = warnings,
            };
        }

        // Phase 2: All staged files are flushed and verified -> batch atomic File.Move(..., overwrite: true).
        int restoredFileCount = 0;
        try
        {
            for (int i = 0; i < stagedFiles.Count; i++)
            {
                var (stagePath, targetPath, lastWriteUtc) = stagedFiles[i];
                ClearReadOnlyIfRegularFile(targetPath);
                File.Move(stagePath, targetPath, overwrite: true);
                try
                {
                    File.SetLastWriteTimeUtc(targetPath, lastWriteUtc.UtcDateTime);
                }
                catch
                {
                    // Preserving timestamp is best-effort.
                }

                restoredFileCount++;
            }
        }
        catch (Exception ex)
        {
            CleanupStagedFiles(stagedFiles);
            string safetyHint = safetyResult.Snapshot?.ManifestRelativePath is { Length: > 0 } rel
                ? $"（可从安全备份手动恢复：{Path.Combine(_paths.PreTrainerSnapshotsDirectory, rel)}）"
                : string.Empty;
            return new PreTrainerRestoreResult
            {
                Succeeded = false,
                SafetySnapshot = safetyResult.Snapshot,
                RestoredFileCount = restoredFileCount,
                Message = $"原子替换存档文件失败：{ex.Message}{safetyHint}",
                Warnings = warnings,
            };
        }

        // Phase 3: Restore validated HKCU registry keys (if present in snapshot).
        int restoredRegistryKeyCount = 0;
        if (registryToRestore is not null && registryToRestore.Keys.Count > 0)
        {
            try
            {
                restoredRegistryKeyCount = DeterministicHkcuRegistrySerializer.Restore(registryToRestore);
            }
            catch (Exception ex)
            {
                string safetyHint = safetyResult.Snapshot?.ManifestRelativePath is { Length: > 0 } rel
                    ? $"（可从安全备份手动恢复：{Path.Combine(_paths.PreTrainerSnapshotsDirectory, rel)}）"
                    : string.Empty;
                return new PreTrainerRestoreResult
                {
                    Succeeded = false,
                    SafetySnapshot = safetyResult.Snapshot,
                    RestoredFileCount = restoredFileCount,
                    RestoredRegistryKeyCount = restoredRegistryKeyCount,
                    Message = $"还原 HKCU 注册表存档失败：{ex.Message}{safetyHint}",
                    Warnings = warnings,
                };
            }
        }

        await _database.MarkPreTrainerSnapshotRestoredAsync(record.SnapshotId, _timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);

        return new PreTrainerRestoreResult
        {
            Succeeded = true,
            SafetySnapshot = safetyResult.Snapshot,
            RestoredFileCount = restoredFileCount,
            RestoredRegistryKeyCount = restoredRegistryKeyCount,
            Warnings = warnings,
            Message = $"已成功回滚 {restoredFileCount} 个存档文件"
                      + (restoredRegistryKeyCount > 0 ? $"与 {restoredRegistryKeyCount} 个 HKCU 注册表键。" : "。"),
        };
    }

    private async Task<PreTrainerSnapshotResult> CreateInternalAsync(
        string gameId,
        string? trainerId,
        PreTrainerSnapshotTriggerKind triggerKind,
        bool enforceSettingsEnabled,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return new PreTrainerSnapshotResult
            {
                Succeeded = false,
                Status = PreTrainerSnapshotStatus.Failed,
                Message = "游戏标识不能为空。",
            };
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string snapshotId = $"{now:yyyyMMddHHmmssfff}-{SpawnId.NewUlid(now)[..8]}";
        string? createdSnapshotDir = null;

        try
        {
            TrainerSettingsDto settings = await _database.GetTrainerSettingsAsync(ct).ConfigureAwait(false);
            if (enforceSettingsEnabled && !settings.CreatePreTrainerSnapshot)
            {
                return new PreTrainerSnapshotResult
                {
                    Succeeded = false,
                    Status = PreTrainerSnapshotStatus.Skipped,
                    SkipReason = PreTrainerSnapshotSkipReason.DisabledBySettings,
                    Message = "已在设置中关闭启动修改器前自动存档快照。",
                };
            }

            GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
            if (game is null)
            {
                return new PreTrainerSnapshotResult
                {
                    Succeeded = false,
                    Status = PreTrainerSnapshotStatus.Failed,
                    Message = $"未在游戏库中找到游戏记录：{gameId}",
                };
            }

            string? effectiveTrainerId = null;
            if (!string.IsNullOrWhiteSpace(trainerId))
            {
                var trainerRecord = await _database.GetTrainerByIdAsync(trainerId, ct).ConfigureAwait(false);
                effectiveTrainerId = trainerRecord is not null ? trainerId : null;
            }

            long maxBytes = _maxBytesOverrideForTest
                            ?? ((long)Math.Clamp(settings.PreTrainerSnapshotMaxSizeMb, 1, 4096) * 1024L * 1024L);
            int retainCount = triggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety
                ? 1
                : Math.Max(1, settings.PreTrainerSnapshotRetentionCount);

            IReadOnlyList<GameDataEntryDto> discovered = await _gameDataService
                .EnumerateSaveLocationsAsync(gameId, refreshOnlineRules: false, ct)
                .ConfigureAwait(false);

            var eligibleEntries = discovered
                .Where(e => e.Exists
                            && e.IsSafeToClean
                            && (e.Category == GameDataCategory.SaveData || e.Category == GameDataCategory.RegistryData))
                .ToList();

            var fsEntries = DeduplicateNestedDirectories(
                eligibleEntries.Where(e => !e.IsRegistryEntry && e.Category == GameDataCategory.SaveData).ToList());
            var regEntries = eligibleEntries
                .Where(e => e.IsRegistryEntry)
                .ToList();

            var validHkcuPaths = new List<string>();
            foreach (var regEntry in regEntries)
            {
                if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(
                        regEntry.PathOrRegistryKey,
                        out string canonicalKey,
                        out _,
                        out _))
                {
                    validHkcuPaths.Add(canonicalKey);
                }
            }

            // Pre-scan all regular files (strictly skipping ReparsePoints) and check size threshold BEFORE copying.
            var plannedFiles = new List<PlannedFileCopy>();
            long scannedBytes = 0;

            for (int locIdx = 0; locIdx < fsEntries.Count; locIdx++)
            {
                ct.ThrowIfCancellationRequested();
                string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fsEntries[locIdx].PathOrRegistryKey));
                if (IsReparsePoint(rootPath))
                {
                    continue;
                }

                string locPrefix = $"loc_{locIdx:D3}";
                if (File.Exists(rootPath))
                {
                    var fi = new FileInfo(rootPath);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    scannedBytes += fi.Length;
                    if (scannedBytes > maxBytes)
                    {
                        return await PersistSkippedAsync(
                            snapshotId,
                            gameId,
                            effectiveTrainerId,
                            triggerKind,
                            PreTrainerSnapshotSkipReason.SizeThresholdExceeded,
                            now,
                            $"存档总大小超过阈值 ({settings.PreTrainerSnapshotMaxSizeMb} MB)，已非阻塞跳过快照。",
                            ct).ConfigureAwait(false);
                    }

                    string fileName = Path.GetFileName(rootPath);
                    plannedFiles.Add(new PlannedFileCopy(
                        SourceLocationPath: rootPath,
                        SourceFilePath: rootPath,
                        LogicalRelativePath: $"{locPrefix}/{fileName}"));
                }
                else if (Directory.Exists(rootPath))
                {
                    bool thresholdExceeded = CollectRegularFilesSkippingReparsePoints(
                        rootPath,
                        rootPath,
                        locPrefix,
                        maxBytes,
                        ref scannedBytes,
                        plannedFiles,
                        depth: 0,
                        ct);

                    if (thresholdExceeded)
                    {
                        return await PersistSkippedAsync(
                            snapshotId,
                            gameId,
                            effectiveTrainerId,
                            triggerKind,
                            PreTrainerSnapshotSkipReason.SizeThresholdExceeded,
                            now,
                            $"存档总大小超过阈值 ({settings.PreTrainerSnapshotMaxSizeMb} MB)，已非阻塞跳过快照。",
                            ct).ConfigureAwait(false);
                    }
                }
            }

            // Export HKCU registry keys into deterministic UTF-8 JSON bytes in memory first.
            DeterministicRegistrySnapshot? regSnapshot = null;
            byte[]? regSnapshotBytes = null;
            string? regSnapshotSha256 = null;
            if (validHkcuPaths.Count > 0)
            {
                regSnapshot = DeterministicHkcuRegistrySerializer.ExportKeys(validHkcuPaths);
                if (regSnapshot.Keys.Count > 0)
                {
                    regSnapshotBytes = DeterministicHkcuRegistrySerializer.SerializeToUtf8Bytes(regSnapshot);
                    regSnapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(regSnapshotBytes));
                    scannedBytes += regSnapshotBytes.Length;

                    if (scannedBytes > maxBytes)
                    {
                        return await PersistSkippedAsync(
                            snapshotId,
                            gameId,
                            effectiveTrainerId,
                            triggerKind,
                            PreTrainerSnapshotSkipReason.SizeThresholdExceeded,
                            now,
                            $"存档与注册表总大小超过阈值 ({settings.PreTrainerSnapshotMaxSizeMb} MB)，已非阻塞跳过快照。",
                            ct).ConfigureAwait(false);
                    }
                }
            }

            if (plannedFiles.Count == 0 && (regSnapshot is null || regSnapshot.Keys.Count == 0))
            {
                return await PersistSkippedAsync(
                    snapshotId,
                    gameId,
                    effectiveTrainerId,
                    triggerKind,
                    PreTrainerSnapshotSkipReason.NoSaveLocationsFound,
                    now,
                    "未检测到现存可备份的存档文件或 HKCU 存档注册表键，已非阻塞跳过快照。",
                    ct).ConfigureAwait(false);
            }

            // Deterministic ordering of files inside manifest.
            plannedFiles.Sort((a, b) => Utf8LexicographicalComparer.Instance.Compare(a.LogicalRelativePath, b.LogicalRelativePath));

            Directory.CreateDirectory(_paths.PreTrainerSnapshotsDirectory);
            createdSnapshotDir = _paths.GetPreTrainerSnapshotDirectory(gameId, snapshotId, createDirectory: true);

            var manifestFiles = new List<PreTrainerSnapshotFileEntry>(plannedFiles.Count);
            long copiedFileBytes = 0;

            foreach (var planned in plannedFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (IsReparsePoint(planned.SourceFilePath))
                {
                    continue;
                }

                string snapshotRelPath = $"{PreTrainerSnapshotManifestStore.FilesDirectoryName}/{planned.LogicalRelativePath}";
                if (!PreTrainerSnapshotManifestStore.TryResolveContainedPath(createdSnapshotDir, snapshotRelPath, out string destFilePath))
                {
                    throw new InvalidOperationException($"快照内部相对路径非法：{snapshotRelPath}");
                }

                var copyOutcome = await CopyFileWithConsistencyRetryAsync(
                    planned.SourceFilePath,
                    destFilePath,
                    maxBytes - copiedFileBytes - (regSnapshotBytes?.Length ?? 0),
                    ct).ConfigureAwait(false);

                if (copyOutcome.Kind == FileCopyOutcomeKind.SharingOrLockViolation)
                {
                    SafeDeleteDirectoryWithoutFollowingReparsePoints(createdSnapshotDir, _paths.PreTrainerSnapshotsDirectory);
                    return await PersistSkippedAsync(
                        snapshotId,
                        gameId,
                        effectiveTrainerId,
                        triggerKind,
                        PreTrainerSnapshotSkipReason.FileLockedSharingViolation,
                        now,
                        $"存档文件被其他进程独占锁定（{Path.GetFileName(planned.SourceFilePath)}），已非阻塞跳过快照。",
                        ct).ConfigureAwait(false);
                }

                if (copyOutcome.Kind == FileCopyOutcomeKind.ConcurrentModification)
                {
                    SafeDeleteDirectoryWithoutFollowingReparsePoints(createdSnapshotDir, _paths.PreTrainerSnapshotsDirectory);
                    return await PersistSkippedAsync(
                        snapshotId,
                        gameId,
                        effectiveTrainerId,
                        triggerKind,
                        PreTrainerSnapshotSkipReason.ConcurrentModificationDetected,
                        now,
                        $"存档文件在读取期间持续变化（{Path.GetFileName(planned.SourceFilePath)}），重试后仍不一致，已非阻塞跳过快照。",
                        ct).ConfigureAwait(false);
                }

                if (copyOutcome.Kind == FileCopyOutcomeKind.SizeThresholdExceeded)
                {
                    SafeDeleteDirectoryWithoutFollowingReparsePoints(createdSnapshotDir, _paths.PreTrainerSnapshotsDirectory);
                    return await PersistSkippedAsync(
                        snapshotId,
                        gameId,
                        effectiveTrainerId,
                        triggerKind,
                        PreTrainerSnapshotSkipReason.SizeThresholdExceeded,
                        now,
                        $"存档文件在拷贝期间超过阈值 ({settings.PreTrainerSnapshotMaxSizeMb} MB)，已非阻塞跳过快照。",
                        ct).ConfigureAwait(false);
                }

                if (copyOutcome.Kind == FileCopyOutcomeKind.SourceDisappeared)
                {
                    continue;
                }

                copiedFileBytes += copyOutcome.BytesCopied;
                manifestFiles.Add(new PreTrainerSnapshotFileEntry
                {
                    LogicalRelativePath = planned.LogicalRelativePath,
                    SnapshotRelativePath = snapshotRelPath,
                    OriginalPath = planned.SourceFilePath,
                    SourceLocationPath = planned.SourceLocationPath,
                    SizeBytes = copyOutcome.BytesCopied,
                    LastWriteTimeUtc = copyOutcome.LastWriteTimeUtc,
                    Sha256 = copyOutcome.Sha256Hex!,
                });
            }

            string? registryRelPath = null;
            long registryBytesCount = 0;
            string[] exportedRegistryKeys = [];

            if (regSnapshot is not null && regSnapshotBytes is not null && regSnapshot.Keys.Count > 0)
            {
                registryRelPath = PreTrainerSnapshotManifestStore.RegistrySnapshotFileName;
                string regDestPath = Path.Combine(createdSnapshotDir, registryRelPath);
                await PreTrainerSnapshotManifestStore.WriteBytesAtomicAsync(regDestPath, regSnapshotBytes, ct)
                    .ConfigureAwait(false);
                registryBytesCount = regSnapshotBytes.Length;
                exportedRegistryKeys = regSnapshot.Keys.Select(k => k.KeyPath).ToArray();
            }

            if (manifestFiles.Count == 0 && exportedRegistryKeys.Length == 0)
            {
                SafeDeleteDirectoryWithoutFollowingReparsePoints(createdSnapshotDir, _paths.PreTrainerSnapshotsDirectory);
                return await PersistSkippedAsync(
                    snapshotId,
                    gameId,
                    effectiveTrainerId,
                    triggerKind,
                    PreTrainerSnapshotSkipReason.NoSaveLocationsFound,
                    now,
                    "扫描到的存档文件在拷贝前均不可用，已非阻塞跳过快照。",
                    ct).ConfigureAwait(false);
            }

            long totalSnapshotBytes = copiedFileBytes + registryBytesCount;
            var manifest = new PreTrainerSnapshotManifest
            {
                SchemaVersion = 1,
                SnapshotId = snapshotId,
                GameId = gameId,
                TrainerId = effectiveTrainerId,
                TriggerKind = triggerKind,
                CreatedAtUtc = now,
                TotalFileCount = manifestFiles.Count,
                TotalBytes = totalSnapshotBytes,
                RegistrySnapshotRelativePath = registryRelPath,
                RegistrySnapshotSha256 = regSnapshotSha256,
                RegistryKeyCount = exportedRegistryKeys.Length,
                RegistryKeys = exportedRegistryKeys,
                Files = manifestFiles,
            };

            string manifestPath = Path.Combine(createdSnapshotDir, PreTrainerSnapshotManifestStore.ManifestFileName);
            await PreTrainerSnapshotManifestStore.WriteManifestAtomicAsync(manifestPath, manifest, ct)
                .ConfigureAwait(false);

            string manifestRelPath = Path.GetRelativePath(_paths.PreTrainerSnapshotsDirectory, manifestPath);
            var completedRecord = new PreTrainerSnapshotRecord
            {
                SnapshotId = snapshotId,
                GameId = gameId,
                TrainerId = effectiveTrainerId,
                TriggerKind = triggerKind,
                Status = PreTrainerSnapshotStatus.Completed,
                SkipReason = null,
                FileCount = manifestFiles.Count,
                TotalBytes = totalSnapshotBytes,
                ManifestRelativePath = manifestRelPath,
                CreatedAtUtc = now,
            };

            await _database.UpsertPreTrainerSnapshotAsync(completedRecord, ct).ConfigureAwait(false);

            // Enforce rolling retention independently for PreTrainer (N) vs PreRestoreSafety (1).
            await EnforceRetentionAsync(gameId, triggerKind, retainCount, ct).ConfigureAwait(false);

            return new PreTrainerSnapshotResult
            {
                Succeeded = true,
                Status = PreTrainerSnapshotStatus.Completed,
                SkipReason = PreTrainerSnapshotSkipReason.None,
                Snapshot = completedRecord,
                Message = $"已创建轻量存档快照：{manifestFiles.Count} 个文件"
                          + (exportedRegistryKeys.Length > 0 ? $"，{exportedRegistryKeys.Length} 个 HKCU 注册表键。" : "。"),
            };
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(createdSnapshotDir))
            {
                SafeDeleteDirectoryWithoutFollowingReparsePoints(createdSnapshotDir, _paths.PreTrainerSnapshotsDirectory);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Pre-trainer snapshot creation failed non-blockingly for game {GameId}.", gameId);
            }

            if (!string.IsNullOrWhiteSpace(createdSnapshotDir))
            {
                SafeDeleteDirectoryWithoutFollowingReparsePoints(createdSnapshotDir, _paths.PreTrainerSnapshotsDirectory);
            }

            PreTrainerSnapshotRecord? failedRecord = null;
            try
            {
                var game = await _database.GetGameByIdAsync(gameId, CancellationToken.None).ConfigureAwait(false);
                if (game is not null)
                {
                    failedRecord = new PreTrainerSnapshotRecord
                    {
                        SnapshotId = snapshotId,
                        GameId = gameId,
                        TrainerId = null,
                        TriggerKind = triggerKind,
                        Status = PreTrainerSnapshotStatus.Failed,
                        SkipReason = null,
                        FileCount = 0,
                        TotalBytes = 0,
                        ManifestRelativePath = null,
                        CreatedAtUtc = now,
                    };
                    await _database.UpsertPreTrainerSnapshotAsync(failedRecord, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Never propagate ledger failure to block trainer launch.
            }

            return new PreTrainerSnapshotResult
            {
                Succeeded = false,
                Status = PreTrainerSnapshotStatus.Failed,
                SkipReason = PreTrainerSnapshotSkipReason.None,
                Snapshot = failedRecord,
                Message = $"轻量存档快照创建失败（未阻断修改器启动）：{ex.Message}",
            };
        }
    }

    private async Task<PreTrainerSnapshotResult> PersistSkippedAsync(
        string snapshotId,
        string gameId,
        string? trainerId,
        PreTrainerSnapshotTriggerKind triggerKind,
        PreTrainerSnapshotSkipReason reason,
        DateTimeOffset createdAtUtc,
        string message,
        CancellationToken ct)
    {
        var skippedRecord = new PreTrainerSnapshotRecord
        {
            SnapshotId = snapshotId,
            GameId = gameId,
            TrainerId = trainerId,
            TriggerKind = triggerKind,
            Status = PreTrainerSnapshotStatus.Skipped,
            SkipReason = reason.ToString(),
            FileCount = 0,
            TotalBytes = 0,
            ManifestRelativePath = null,
            CreatedAtUtc = createdAtUtc,
        };

        await _database.UpsertPreTrainerSnapshotAsync(skippedRecord, ct).ConfigureAwait(false);

        return new PreTrainerSnapshotResult
        {
            Succeeded = false,
            Status = PreTrainerSnapshotStatus.Skipped,
            SkipReason = reason,
            Snapshot = skippedRecord,
            Message = message,
        };
    }

    private async Task EnforceRetentionAsync(
        string gameId,
        PreTrainerSnapshotTriggerKind triggerKind,
        int retainCount,
        CancellationToken ct)
    {
        IReadOnlyList<PreTrainerSnapshotRecord> existing = await _database
            .GetPreTrainerSnapshotsByGameAsync(gameId, triggerKindFilter: triggerKind, limit: 1000, cancellationToken: ct)
            .ConfigureAwait(false);

        int completedKept = 0;
        int nonCompletedKept = 0;
        int nonCompletedLimit = triggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety
            ? 2
            : Math.Max(retainCount, 10);

        foreach (var snap in existing)
        {
            if (snap.Status == PreTrainerSnapshotStatus.Completed)
            {
                completedKept++;
                if (completedKept <= retainCount)
                {
                    continue;
                }
            }
            else
            {
                nonCompletedKept++;
                if (nonCompletedKept <= nonCompletedLimit)
                {
                    continue;
                }
            }

            string? snapDir = ResolveSnapshotDirectoryForRecord(snap);
            if (!string.IsNullOrWhiteSpace(snapDir))
            {
                SafeDeleteDirectoryWithoutFollowingReparsePoints(snapDir, _paths.PreTrainerSnapshotsDirectory);
            }

            await _database.DeletePreTrainerSnapshotAsync(snap.SnapshotId, ct).ConfigureAwait(false);
        }
    }

    private string? ResolveSnapshotDirectoryForRecord(PreTrainerSnapshotRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ManifestRelativePath)
            && PreTrainerSnapshotManifestStore.TryResolveContainedPath(
                _paths.PreTrainerSnapshotsDirectory,
                record.ManifestRelativePath,
                out string manifestFull))
        {
            return Path.GetDirectoryName(manifestFull);
        }

        string fallbackDir = _paths.GetPreTrainerSnapshotDirectory(record.GameId, record.SnapshotId, createDirectory: false);
        return Directory.Exists(fallbackDir) ? fallbackDir : null;
    }

    private static bool CollectRegularFilesSkippingReparsePoints(
        string locationRoot,
        string currentDir,
        string locPrefix,
        long maxBytes,
        ref long scannedBytes,
        List<PlannedFileCopy> sink,
        int depth,
        CancellationToken ct)
    {
        if (depth > MaxDirectoryWalkDepth)
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(currentDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            FileInfo fi;
            try
            {
                fi = new FileInfo(file);
                if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            scannedBytes += fi.Length;
            if (scannedBytes > maxBytes)
            {
                return true;
            }

            string relSub = Path.GetRelativePath(locationRoot, file).Replace('\\', '/');
            sink.Add(new PlannedFileCopy(
                SourceLocationPath: locationRoot,
                SourceFilePath: file,
                LogicalRelativePath: $"{locPrefix}/{relSub}"));
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(currentDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (string subDir in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (CollectRegularFilesSkippingReparsePoints(
                    locationRoot,
                    subDir,
                    locPrefix,
                    maxBytes,
                    ref scannedBytes,
                    sink,
                    depth + 1,
                    ct))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<FileCopyOutcome> CopyFileWithConsistencyRetryAsync(
        string sourcePath,
        string destinationPath,
        long remainingByteBudget,
        CancellationToken ct)
    {
        string? destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        for (int attempt = 0; attempt < MaxCopyConsistencyAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var preInfo = new FileInfo(sourcePath);
            if (!preInfo.Exists)
            {
                return new FileCopyOutcome(FileCopyOutcomeKind.SourceDisappeared, 0, default, null);
            }

            long preLength = preInfo.Length;
            DateTime preWriteUtc = preInfo.LastWriteTimeUtc;

            long bytesCopied = 0;
            string sha256Hex;

            try
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];

                await using (var src = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var dst = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: buffer.Length,
                    FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        bytesCopied += read;
                        if (bytesCopied > remainingByteBudget)
                        {
                            return new FileCopyOutcome(FileCopyOutcomeKind.SizeThresholdExceeded, bytesCopied, default, null);
                        }

                        hasher.AppendData(buffer, 0, read);
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }

                    await dst.FlushAsync(ct).ConfigureAwait(false);
                }

                sha256Hex = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }
            catch (IOException ex) when (IsSharingOrLockViolation(ex))
            {
                return new FileCopyOutcome(FileCopyOutcomeKind.SharingOrLockViolation, 0, default, null);
            }

            _afterSingleFileCopyHook?.Invoke(sourcePath, attempt);

            var postInfo = new FileInfo(sourcePath);
            postInfo.Refresh();

            bool consistent = postInfo.Exists
                              && postInfo.Length == preLength
                              && postInfo.Length == bytesCopied
                              && postInfo.LastWriteTimeUtc == preWriteUtc;

            if (consistent)
            {
                return new FileCopyOutcome(
                    FileCopyOutcomeKind.Copied,
                    bytesCopied,
                    new DateTimeOffset(postInfo.LastWriteTimeUtc, TimeSpan.Zero),
                    sha256Hex);
            }
        }

        return new FileCopyOutcome(FileCopyOutcomeKind.ConcurrentModification, 0, default, null);
    }

    private static async Task StageAndVerifyFileAsync(
        string payloadPath,
        string stagePath,
        long expectedSizeBytes,
        string expectedSha256Hex,
        CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            await using (var src = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(
                stagePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: buffer.Length,
                FileOptions.Asynchronous))
            {
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                await dst.FlushAsync(ct).ConfigureAwait(false);
                dst.Flush(flushToDisk: true);
            }

            var stagedInfo = new FileInfo(stagePath);
            if (!stagedInfo.Exists || stagedInfo.Length != expectedSizeBytes)
            {
                throw new InvalidDataException(
                    $"暂存文件大小校验失败：期望 {expectedSizeBytes} 字节，实际 {(stagedInfo.Exists ? stagedInfo.Length : -1)} 字节。");
            }

            string actualSha256 = await PreTrainerSnapshotManifestStore.ComputeFileSha256HexAsync(stagePath, ct)
                .ConfigureAwait(false);
            if (!string.Equals(actualSha256, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"暂存文件 SHA-256 校验失败：期望 {expectedSha256Hex}，实际 {actualSha256}。");
            }
        }
        catch
        {
            TryDeleteRegularFileQuietly(stagePath);
            throw;
        }
    }

    private static List<GameDataEntryDto> DeduplicateNestedDirectories(List<GameDataEntryDto> fsEntries)
    {
        var dirs = fsEntries
            .Where(e => Directory.Exists(e.PathOrRegistryKey))
            .Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e.PathOrRegistryKey)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ToList();

        var result = new List<GameDataEntryDto>(fsEntries.Count);
        foreach (var entry in fsEntries.OrderBy(e => e.PathOrRegistryKey, Utf8LexicographicalComparer.Instance))
        {
            string norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.PathOrRegistryKey));
            bool coveredByParent = dirs.Any(d =>
                !string.Equals(d, norm, StringComparison.OrdinalIgnoreCase)
                && norm.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!coveredByParent)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Safely deletes a snapshot directory strictly contained within <paramref name="allowedRootDir"/>.
    /// NEVER follows NTFS junctions or symbolic links (<see cref="FileAttributes.ReparsePoint"/>); any reparse
    /// directory encountered is unlinked non-recursively via <c>Directory.Delete(path, recursive: false)</c>.
    /// </summary>
    internal static void SafeDeleteDirectoryWithoutFollowingReparsePoints(string targetDir, string allowedRootDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(allowedRootDir))
        {
            return;
        }

        try
        {
            string normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRootDir));
            string normTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
            if (!normTarget.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Require at least <PreTrainerSnapshotsDirectory>\<GameId>\<SnapshotId> (2 segments below root).
            string rel = Path.GetRelativePath(normRoot, normTarget);
            string[] segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length < 2)
            {
                return;
            }

            if (!Directory.Exists(normTarget))
            {
                return;
            }

            DeleteDirectoryTreeNonReparse(normTarget, depth: 0);
        }
        catch
        {
            // Non-fatal cleanup failure.
        }
    }

    private static void DeleteDirectoryTreeNonReparse(string dirPath, int depth)
    {
        if (depth > MaxDirectoryWalkDepth || !Directory.Exists(dirPath))
        {
            return;
        }

        // If this directory itself is a junction/symlink, unlink ONLY the reparse point itself without recursing!
        if ((File.GetAttributes(dirPath) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(dirPath, recursive: false);
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dirPath))
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReparsePoint) == 0 && (attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }

                File.Delete(file);
            }
            catch
            {
                // Continue best-effort cleanup.
            }
        }

        foreach (string subDir in Directory.EnumerateDirectories(dirPath))
        {
            try
            {
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                {
                    // Non-recursive unlink of the reparse point: never descends into or deletes target files!
                    Directory.Delete(subDir, recursive: false);
                }
                else
                {
                    DeleteDirectoryTreeNonReparse(subDir, depth + 1);
                }
            }
            catch
            {
                // Continue best-effort cleanup.
            }
        }

        try
        {
            Directory.Delete(dirPath, recursive: false);
        }
        catch
        {
            // Ignore if OS holds a transient handle.
        }
    }

    private static bool IsSharingOrLockViolation(IOException ex)
    {
        int win32 = ex.HResult & 0xFFFF;
        return win32 is Win32ErrorSharingViolation or Win32ErrorLockViolation;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearReadOnlyIfRegularFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) == 0 && (attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // Best-effort attribute normalization.
        }
    }

    private static void CleanupStagedFiles(List<(string StagePath, string TargetPath, DateTimeOffset LastWriteUtc)> stagedFiles)
    {
        foreach (var (stagePath, _, _) in stagedFiles)
        {
            TryDeleteRegularFileQuietly(stagePath);
        }
    }

    private static void TryDeleteRegularFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failure for temp staging file.
        }
    }

    private sealed record PlannedFileCopy(
        string SourceLocationPath,
        string SourceFilePath,
        string LogicalRelativePath);

    private enum FileCopyOutcomeKind
    {
        Copied = 0,
        SharingOrLockViolation = 1,
        ConcurrentModification = 2,
        SizeThresholdExceeded = 3,
        SourceDisappeared = 4,
    }

    private readonly record struct FileCopyOutcome(
        FileCopyOutcomeKind Kind,
        long BytesCopied,
        DateTimeOffset LastWriteTimeUtc,
        string? Sha256Hex);
}
