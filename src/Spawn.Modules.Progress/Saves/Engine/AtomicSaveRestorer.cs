using Spawn.Contracts;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Saves.Rules;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress.Saves.Engine;

/// <summary>
/// Atomic, safety-fenced save &amp; <c>HKCU</c> registry restorer and folder exporter backed by <see cref="CasChunkStore"/>.
/// Pre-validates EVERY target path via <see cref="IGameDataManagementService.ValidateSaveRestoreTargetAsync"/>,
/// stages into <c>&lt;file&gt;.spawn_stage_tmp</c> with <c>Flush(flushToDisk: true)</c> + BLAKE3 verification,
/// and atomically replaces live files only after all staged payloads succeed.
/// </summary>
public sealed class AtomicSaveRestorer
{
    public const string RestoreStageSuffix = ".spawn_stage_tmp";

    private readonly CasChunkStore _casStore;
    private readonly ILudusaviRuleResolver _ruleResolver;
    private readonly IGameDataManagementService _gameDataService;

    public AtomicSaveRestorer(
        CasChunkStore casStore,
        ILudusaviRuleResolver ruleResolver,
        IGameDataManagementService gameDataService)
    {
        ArgumentNullException.ThrowIfNull(casStore);
        ArgumentNullException.ThrowIfNull(ruleResolver);
        ArgumentNullException.ThrowIfNull(gameDataService);
        _casStore = casStore;
        _ruleResolver = ruleResolver;
        _gameDataService = gameDataService;
    }

    /// <summary>
    /// Restores a snapshot's files (and optional <c>[config]</c> + <c>HKCU</c> registry export) to live disk.
    /// When <paramref name="outgoingHeadFiles"/> is supplied, extraneous <c>[save]</c> files from the previous HEAD
    /// that do not exist in the target snapshot are safely removed after passing the safety fence.
    /// </summary>
    public async Task<SaveRestoreOutcome> RestoreAsync(
        GameRecord game,
        SaveSnapshotRecord targetSnapshot,
        IReadOnlyList<SaveSnapshotFileRecord> targetFiles,
        bool includeConfigFiles,
        IReadOnlyList<SaveSnapshotFileRecord>? outgoingHeadFiles = null,
        SaveSnapshotRecord? safetySnapshot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        ArgumentNullException.ThrowIfNull(targetFiles);

        var warnings = new List<string>();
        var filePlans = new List<(SaveSnapshotFileRecord Record, string TargetPath)>();
        var registryEntries = new List<SaveSnapshotFileRecord>();
        ResolvedSaveLayout? cachedTrustedLayout = null;

        async Task<GameDataSafetyCheckResult> ValidateTargetWithTrustedFallbackAsync(string candidateTarget)
        {
            GameDataSafetyCheckResult safety = await _gameDataService
                .ValidateSaveRestoreTargetAsync(game.GameId, candidateTarget, cancellationToken)
                .ConfigureAwait(false);
            if (safety.IsSafe)
            {
                return safety;
            }

            if (!TrustedSavePathFence.IsHeuristicStrictFenceRejection(safety.Reason))
            {
                return safety;
            }

            cachedTrustedLayout ??= await _ruleResolver
                .ResolveGameSaveLayoutAsync(game.GameId, refreshOnlineRules: false, cancellationToken)
                .ConfigureAwait(false);

            string normCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateTarget));
            ResolvedSaveLocation? matchingTrusted = cachedTrustedLayout.Locations.FirstOrDefault(loc =>
                loc.IsSafe
                && (loc.IsCustom || loc.SourceRule == GameDataSourceRule.Ludusavi)
                && IsPathCoveredByLocation(normCandidate, loc.ResolvedFullPath));
            if (matchingTrusted is null)
            {
                return safety;
            }

            var (trustedOk, trustedReason) = TrustedSavePathFence.ValidateTrustedFilesystemTarget(
                normCandidate,
                game.InstallDir,
                isUserCustomPath: matchingTrusted.IsCustom);
            return trustedOk
                ? new GameDataSafetyCheckResult(true)
                : new GameDataSafetyCheckResult(false, trustedReason ?? safety.Reason);
        }

        // 1. Pre-validate EVERY file and registry target through safety fence BEFORE mutating disk or registry.
        foreach (SaveSnapshotFileRecord entry in targetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Category == SaveFileCategory.RegistryExport)
            {
                registryEntries.Add(entry);
                continue;
            }

            if (entry.Category == SaveFileCategory.Config && !includeConfigFiles)
            {
                continue;
            }

            string targetPath = ResolveTargetPath(entry, game);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return FailOutcome($"无法解析存档文件的还原目标路径：{entry.LogicalPath}", safetySnapshot, targetSnapshot, warnings);
            }

            if (TrustedSavePathFence.IsForbiddenExecutableFile(targetPath))
            {
                return FailOutcome($"安全围栏拦截：禁止覆写可执行文件或脚本 ({Path.GetExtension(targetPath)})。", safetySnapshot, targetSnapshot, warnings);
            }

            GameDataSafetyCheckResult safety = await ValidateTargetWithTrustedFallbackAsync(targetPath)
                .ConfigureAwait(false);
            if (!safety.IsSafe)
            {
                return FailOutcome(
                    safety.Reason ?? $"安全围栏已拦截非法的回滚目标路径：{targetPath}",
                    safetySnapshot,
                    targetSnapshot,
                    warnings);
            }

            if (AntiTearFileCapture.IsReparsePoint(targetPath)
                || TrustedSavePathFence.HasDisallowedReparsePointOnPath(targetPath, out _))
            {
                return FailOutcome(
                    $"目标路径为符号链接或目录联接点，拒绝写入：{targetPath}",
                    safetySnapshot,
                    targetSnapshot,
                    warnings);
            }

            foreach (string chunkHash in entry.ChunkHashes)
            {
                if (!_casStore.ChunkFileExists(chunkHash))
                {
                    return FailOutcome(
                        $"本地 CAS 仓库缺少分块 {chunkHash[..Math.Min(8, chunkHash.Length)]}，无法还原 {entry.LogicalPath}。",
                        safetySnapshot,
                        targetSnapshot,
                        warnings);
                }
            }

            filePlans.Add((entry, targetPath));
        }

        // Pre-validate & deserialize HKCU registry payloads before touching disk.
        var validatedRegistrySnapshots = new List<DeterministicRegistrySnapshot>();
        foreach (SaveSnapshotFileRecord regEntry in registryEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            try
            {
                await _casStore.ReconstructToStreamAsync(
                    regEntry.ChunkHashes,
                    ms,
                    regEntry.FileBlake3,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return FailOutcome(
                    $"注册表快照块校验失败：{ex.Message}",
                    safetySnapshot,
                    targetSnapshot,
                    warnings);
            }

            DeterministicRegistrySnapshot regSnap;
            try
            {
                regSnap = DeterministicHkcuRegistrySerializer.Deserialize(ms.ToArray());
            }
            catch (Exception ex)
            {
                return FailOutcome($"注册表快照 JSON 解析失败：{ex.Message}", safetySnapshot, targetSnapshot, warnings);
            }

            foreach (DeterministicRegistryRootKey rootKey in regSnap.Keys)
            {
                if (!DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(rootKey.KeyPath, out _, out _, out string? err))
                {
                    return FailOutcome(err ?? $"禁止恢复非 HKCU 注册表键：{rootKey.KeyPath}", safetySnapshot, targetSnapshot, warnings);
                }

                GameDataSafetyCheckResult regCheck = await _gameDataService
                    .ValidateSaveRestoreTargetAsync(game.GameId, rootKey.KeyPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!regCheck.IsSafe)
                {
                    return FailOutcome(
                        regCheck.Reason ?? $"安全围栏已拦截非法注册表恢复目标：{rootKey.KeyPath}",
                        safetySnapshot,
                        targetSnapshot,
                        warnings);
                }
            }

            validatedRegistrySnapshots.Add(regSnap);
        }

        // Pre-validate extraneous files from outgoing HEAD (only [save] files absent from target snapshot).
        var extraneousToDelete = new List<string>();
        if (outgoingHeadFiles is { Count: > 0 })
        {
            var targetPathSet = new HashSet<string>(
                filePlans.Select(p => Path.GetFullPath(p.TargetPath)),
                StringComparer.OrdinalIgnoreCase);

            foreach (SaveSnapshotFileRecord prev in outgoingHeadFiles)
            {
                if (prev.Category != SaveFileCategory.Save)
                {
                    continue;
                }

                string prevPath = ResolveTargetPath(prev, game);
                if (string.IsNullOrWhiteSpace(prevPath))
                {
                    continue;
                }

                string normPrev = Path.GetFullPath(prevPath);
                if (targetPathSet.Contains(normPrev)
                    || !File.Exists(normPrev)
                    || AntiTearFileCapture.IsReparsePoint(normPrev))
                {
                    continue;
                }

                GameDataSafetyCheckResult delCheck = await ValidateTargetWithTrustedFallbackAsync(normPrev)
                    .ConfigureAwait(false);
                if (delCheck.IsSafe && !TrustedSavePathFence.IsForbiddenExecutableFile(normPrev))
                {
                    extraneousToDelete.Add(normPrev);
                }
            }
        }

        // 2. Phase 1: Stage every file to <targetPath>.spawn_stage_tmp with Flush(true) & full BLAKE3 verification.
        var stagedFiles = new List<(string StagePath, string TargetPath, DateTimeOffset LastWriteUtc)>(filePlans.Count);
        try
        {
            foreach (var (record, targetPath) in filePlans)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fullTarget = Path.GetFullPath(targetPath);
                string? parentDir = Path.GetDirectoryName(fullTarget);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                string stagePath = fullTarget + RestoreStageSuffix;
                if (AntiTearFileCapture.IsReparsePoint(stagePath))
                {
                    throw new IOException($"暂存路径为符号链接，拒绝写入：{stagePath}");
                }

                ClearReadOnlyIfRegularFile(stagePath);

                await using (var fs = new FileStream(
                    stagePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous))
                {
                    await _casStore.ReconstructToStreamAsync(
                        record.ChunkHashes,
                        fs,
                        record.FileBlake3,
                        cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                    fs.Flush(flushToDisk: true);
                }

                stagedFiles.Add((stagePath, fullTarget, record.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CleanupStagedFiles(stagedFiles);
            return FailOutcome(
                $"回滚暂存或 BLAKE3 校验失败，当前存档未被覆盖：{ex.Message}",
                safetySnapshot,
                targetSnapshot,
                warnings);
        }

        // 3. Phase 2: Atomic batch File.Move for all staged files.
        int restoredFileCount = 0;
        try
        {
            foreach (var (stagePath, targetPath, lastWriteUtc) in stagedFiles)
            {
                ClearReadOnlyIfRegularFile(targetPath);
                File.Move(stagePath, targetPath, overwrite: true);
                try
                {
                    File.SetLastWriteTimeUtc(targetPath, lastWriteUtc.UtcDateTime);
                }
                catch
                {
                    // Timestamp preservation is best-effort.
                }

                restoredFileCount++;
            }
        }
        catch (Exception ex)
        {
            CleanupStagedFiles(stagedFiles);
            return new SaveRestoreOutcome
            {
                Succeeded = false,
                Message = $"原子替换存档文件失败：{ex.Message}",
                SafetySnapshot = safetySnapshot,
                TargetSnapshot = targetSnapshot,
                RestoredFileCount = restoredFileCount,
                Warnings = warnings,
            };
        }

        // 4. Phase 2b: Remove extraneous [save] files belonging solely to the outgoing branch/HEAD.
        int deletedExtraneousCount = 0;
        foreach (string stalePath in extraneousToDelete)
        {
            try
            {
                if (File.Exists(stalePath) && !AntiTearFileCapture.IsReparsePoint(stalePath))
                {
                    ClearReadOnlyIfRegularFile(stalePath);
                    File.Delete(stalePath);
                    deletedExtraneousCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"未能清理旧分支残留存档文件 {Path.GetFileName(stalePath)}：{ex.Message}");
            }
        }

        // 5. Phase 3: Restore validated HKCU registry keys.
        int restoredRegistryCount = 0;
        foreach (DeterministicRegistrySnapshot regSnap in validatedRegistrySnapshots)
        {
            try
            {
                restoredRegistryCount += DeterministicHkcuRegistrySerializer.Restore(regSnap);
            }
            catch (Exception ex)
            {
                return new SaveRestoreOutcome
                {
                    Succeeded = false,
                    Message = $"还原 HKCU 注册表存档失败：{ex.Message}",
                    SafetySnapshot = safetySnapshot,
                    TargetSnapshot = targetSnapshot,
                    RestoredFileCount = restoredFileCount,
                    RestoredRegistryKeyCount = restoredRegistryCount,
                    DeletedExtraneousFileCount = deletedExtraneousCount,
                    Warnings = warnings,
                };
            }
        }

        return new SaveRestoreOutcome
        {
            Succeeded = true,
            Message = $"已原子还原 {restoredFileCount} 个存档文件"
                      + (restoredRegistryCount > 0 ? $"与 {restoredRegistryCount} 个 HKCU 注册表键" : string.Empty)
                      + (deletedExtraneousCount > 0 ? $"（已清理 {deletedExtraneousCount} 个旧分支残留文件）" : string.Empty)
                      + "。",
            SafetySnapshot = safetySnapshot,
            TargetSnapshot = targetSnapshot,
            ActiveBranchId = targetSnapshot.BranchId,
            RestoredFileCount = restoredFileCount,
            RestoredRegistryKeyCount = restoredRegistryCount,
            DeletedExtraneousFileCount = deletedExtraneousCount,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Exports a snapshot's files and registry JSON into an arbitrary user-chosen folder without altering live game saves.
    /// </summary>
    public async Task<ExportSnapshotResult> ExportToFolderAsync(
        SaveSnapshotRecord snapshot,
        IReadOnlyList<SaveSnapshotFileRecord> files,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(files);

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return new ExportSnapshotResult(false, "导出目录不能为空。");
        }

        string fullExportRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory.Trim()));
        Directory.CreateDirectory(fullExportRoot);

        int exportedFiles = 0;
        int exportedRegKeys = 0;

        foreach (SaveSnapshotFileRecord entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Category == SaveFileCategory.RegistryExport)
            {
                string regOut = Path.Combine(fullExportRoot, PreTrainerSnapshotManifestStore.RegistrySnapshotFileName);
                await using (var fs = new FileStream(regOut, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await _casStore.ReconstructToStreamAsync(entry.ChunkHashes, fs, entry.FileBlake3, cancellationToken)
                        .ConfigureAwait(false);
                }

                byte[] bytes = await File.ReadAllBytesAsync(regOut, cancellationToken).ConfigureAwait(false);
                exportedRegKeys += DeterministicHkcuRegistrySerializer.Deserialize(bytes).Keys.Count;
                continue;
            }

            string safeRel = SanitizeLogicalPathForExport(entry.LogicalPath);
            string destPath = Path.GetFullPath(Path.Combine(fullExportRoot, safeRel));
            if (!destPath.StartsWith(fullExportRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new ExportSnapshotResult(false, $"导出路径越界：{entry.LogicalPath}");
            }

            string? parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _casStore.ReconstructToStreamAsync(entry.ChunkHashes, fs, entry.FileBlake3, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                File.SetLastWriteTimeUtc(destPath, entry.LastWriteTimeUtc.UtcDateTime);
            }
            catch
            {
                // Best-effort timestamp preservation.
            }

            exportedFiles++;
        }

        return new ExportSnapshotResult(
            Succeeded: true,
            Message: $"已导出 {exportedFiles} 个文件" + (exportedRegKeys > 0 ? $"与 {exportedRegKeys} 个注册表键" : string.Empty) + "。",
            ExportDirectory: fullExportRoot,
            ExportedFileCount: exportedFiles,
            ExportedRegistryKeyCount: exportedRegKeys);
    }

    private string ResolveTargetPath(SaveSnapshotFileRecord entry, GameRecord game)
    {
        if (!string.IsNullOrWhiteSpace(entry.LogicalPath)
            && !entry.LogicalPath.StartsWith("registry://", StringComparison.OrdinalIgnoreCase))
        {
            string expanded = _ruleResolver.ExpandLogicalPath(entry.LogicalPath, game);
            if (!string.IsNullOrWhiteSpace(expanded) && Path.IsPathFullyQualified(expanded))
            {
                return expanded;
            }
        }

        return entry.OriginalPath;
    }

    private static string SanitizeLogicalPathForExport(string logicalPath)
    {
        string normalized = logicalPath.Replace('\\', '/').TrimStart('/');
        string[] rawSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        char[] invalid = Path.GetInvalidFileNameChars();
        var cleanSegments = new List<string>(rawSegments.Length);

        foreach (string seg in rawSegments)
        {
            if (seg is "." or "..")
            {
                continue;
            }

            string cleaned = seg.Trim('<', '>');
            cleaned = string.Concat(cleaned.Select(c => c == ':' || Array.IndexOf(invalid, c) >= 0 ? '_' : c));
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                cleanSegments.Add(cleaned);
            }
        }

        return cleanSegments.Count > 0
            ? Path.Combine(cleanSegments.ToArray())
            : "save_file.bin";
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
            // Ignore transient attribute error.
        }
    }

    private static void CleanupStagedFiles(IEnumerable<(string StagePath, string TargetPath, DateTimeOffset LastWriteUtc)> staged)
    {
        foreach (var (stagePath, _, _) in staged)
        {
            try
            {
                if (File.Exists(stagePath))
                {
                    File.Delete(stagePath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static bool IsPathCoveredByLocation(string normalizedTarget, string locationPath)
    {
        if (string.IsNullOrWhiteSpace(locationPath))
        {
            return false;
        }

        try
        {
            string normLoc = Path.TrimEndingDirectorySeparator(Path.GetFullPath(locationPath));
            return string.Equals(normalizedTarget, normLoc, StringComparison.OrdinalIgnoreCase)
                   || normalizedTarget.StartsWith(normLoc + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static SaveRestoreOutcome FailOutcome(
        string message,
        SaveSnapshotRecord? safetySnapshot,
        SaveSnapshotRecord? targetSnapshot,
        IReadOnlyList<string> warnings) =>
        new()
        {
            Succeeded = false,
            Message = message,
            SafetySnapshot = safetySnapshot,
            TargetSnapshot = targetSnapshot,
            Warnings = warnings,
        };
}
