using Spawn.Contracts;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress.Saves.Engine;

/// <summary>
/// Outcome of an anti-tear file &amp; HKCU registry capture pass into <see cref="CasChunkStore"/>.
/// </summary>
public sealed record AntiTearCaptureOutcome
{
    public required bool Succeeded { get; init; }
    public bool Skipped { get; init; }
    public SaveSnapshotSkipReason SkipReason { get; init; } = SaveSnapshotSkipReason.None;
    public required string Message { get; init; }
    public IReadOnlyList<SaveSnapshotFileRecord> Files { get; init; } = [];
    public int FileCount { get; init; }
    public int RegistryKeyCount { get; init; }
    public long TotalRawBytes { get; init; }
    public long TotalStoredBytes { get; init; }
    public long DeduplicatedBytesSaved { get; init; }
    public int NewChunkCount { get; init; }
    public int ReusedChunkCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Anti-tear save file and <c>HKCU</c> registry capture engine:
/// <list type="bullet">
/// <item><description>Opens live files with <c>FileShare.ReadWrite | FileShare.Delete</c> (never blocks running games).</description></item>
/// <item><description>Captures pre/post <c>(Length, LastWriteTimeUtc)</c> metadata vectors and retries up to 3 times on mid-read mutation.</description></item>
/// <item><description>Skips ReparsePoints (junctions/symlinks), enforces size caps, and returns structured skips on <c>ERROR_SHARING_VIOLATION</c>.</description></item>
/// <item><description>Exports <c>HKCU</c> registry save trees via <see cref="DeterministicHkcuRegistrySerializer"/> into the same CAS store.</description></item>
/// </list>
/// </summary>
public sealed class AntiTearFileCapture
{
    public const string RegistryLogicalPath = "registry://HKCU/registry_snapshot.json";
    public const int MaxConsistencyAttempts = 3;
    private const int Win32ErrorSharingViolation = 32;
    private const int Win32ErrorLockViolation = 33;
    private const int MaxDirectoryWalkDepth = 32;

    private readonly CasChunkStore _casStore;
    private readonly ILudusaviRuleResolver _ruleResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, int>? _afterReadConsistencyTestHook;

    public AntiTearFileCapture(
        CasChunkStore casStore,
        ILudusaviRuleResolver ruleResolver,
        TimeProvider? timeProvider = null,
        Action<string, int>? afterReadConsistencyTestHook = null)
    {
        ArgumentNullException.ThrowIfNull(casStore);
        ArgumentNullException.ThrowIfNull(ruleResolver);
        _casStore = casStore;
        _ruleResolver = ruleResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _afterReadConsistencyTestHook = afterReadConsistencyTestHook;
    }

    public async Task<AntiTearCaptureOutcome> CaptureAsync(
        string snapshotId,
        GameRecord game,
        ResolvedSaveLayout layout,
        bool includeConfigFiles,
        long maxTotalBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(layout);

        var warnings = new List<string>();
        var rawCandidates = new List<ResolvedSaveLocation>();
        foreach (ResolvedSaveLocation loc in layout.SaveLocations)
        {
            if (loc.Exists && loc.IsSafe && loc.Category == SaveFileCategory.Save)
            {
                rawCandidates.Add(loc);
            }
        }

        if (includeConfigFiles)
        {
            foreach (ResolvedSaveLocation loc in layout.ConfigLocations)
            {
                if (loc.Exists && loc.IsSafe && loc.Category == SaveFileCategory.Config)
                {
                    rawCandidates.Add(loc);
                }
            }
        }

        List<ResolvedSaveLocation> deduplicatedLocations = DeduplicateNestedLocations(rawCandidates);

        // Pre-scan regular files (skipping ReparsePoints) and check size cap before ingesting into CAS.
        var plannedFiles = new List<(string FullPath, string LogicalPath, SaveFileCategory Category)>();
        var seenFullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long scannedBytes = 0;

        foreach (ResolvedSaveLocation loc in deduplicatedLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(loc.ResolvedFullPath))
            {
                continue;
            }

            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc.ResolvedFullPath));
            if (IsReparsePoint(normalizedRoot))
            {
                warnings.Add($"已跳过重解析点/符号链接路径：{normalizedRoot}");
                continue;
            }

            if (File.Exists(normalizedRoot))
            {
                var fi = new FileInfo(normalizedRoot);
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || IsIgnoredTempFile(normalizedRoot))
                {
                    continue;
                }

                if (!seenFullPaths.Add(normalizedRoot))
                {
                    continue;
                }

                scannedBytes += fi.Length;
                if (maxTotalBytes > 0 && scannedBytes > maxTotalBytes)
                {
                    return SkippedOutcome(
                        SaveSnapshotSkipReason.SizeThresholdExceeded,
                        $"存档总大小超过阈值（{FormatMegabytes(maxTotalBytes)} MB），已非阻塞跳过快照。");
                }

                string logical = _ruleResolver.CanonicalizeToLogicalPath(normalizedRoot, game);
                plannedFiles.Add((normalizedRoot, logical, loc.Category));
            }
            else if (Directory.Exists(normalizedRoot))
            {
                bool exceeded = CollectDirectoryFilesSkippingReparsePoints(
                    normalizedRoot,
                    loc.Category,
                    game,
                    maxTotalBytes,
                    ref scannedBytes,
                    seenFullPaths,
                    plannedFiles,
                    depth: 0,
                    cancellationToken);

                if (exceeded)
                {
                    return SkippedOutcome(
                        SaveSnapshotSkipReason.SizeThresholdExceeded,
                        $"存档总大小超过阈值（{FormatMegabytes(maxTotalBytes)} MB），已非阻塞跳过快照。");
                }
            }
        }

        // Export HKCU registry keys into deterministic UTF-8 JSON bytes.
        var validHkcuKeys = new List<string>();
        foreach (ResolvedSaveLocation regLoc in layout.RegistryKeys)
        {
            if (regLoc.IsSafe
                && DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(
                    regLoc.ResolvedFullPath,
                    out string canonicalKey,
                    out _,
                    out _))
            {
                validHkcuKeys.Add(canonicalKey);
            }
        }

        DeterministicRegistrySnapshot? regSnapshot = null;
        byte[]? regBytes = null;
        if (validHkcuKeys.Count > 0)
        {
            regSnapshot = DeterministicHkcuRegistrySerializer.ExportKeys(validHkcuKeys);
            if (regSnapshot.Keys.Count > 0)
            {
                regBytes = DeterministicHkcuRegistrySerializer.SerializeToUtf8Bytes(regSnapshot);
                scannedBytes += regBytes.Length;
                if (maxTotalBytes > 0 && scannedBytes > maxTotalBytes)
                {
                    return SkippedOutcome(
                        SaveSnapshotSkipReason.SizeThresholdExceeded,
                        $"存档与注册表总大小超过阈值（{FormatMegabytes(maxTotalBytes)} MB），已非阻塞跳过快照。");
                }
            }
        }

        if (plannedFiles.Count == 0 && (regSnapshot is null || regSnapshot.Keys.Count == 0))
        {
            return SkippedOutcome(
                SaveSnapshotSkipReason.NoSaveLocationsFound,
                "未检测到现存可备份的存档文件或 HKCU 注册表键。");
        }

        // Sort deterministically by UTF-8 logical path.
        plannedFiles.Sort((a, b) => Utf8LexicographicalComparer.Instance.Compare(a.LogicalPath, b.LogicalPath));

        var capturedFiles = new List<SaveSnapshotFileRecord>(plannedFiles.Count + 1);
        long totalRawBytes = 0;
        long totalStoredBytes = 0;
        long deduplicatedSavedBytes = 0;
        int newChunks = 0;
        int reusedChunks = 0;
        int fsFileCount = 0;

        foreach (var (fullPath, logicalPath, category) in plannedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(fullPath))
            {
                continue;
            }

            SingleFileCaptureResult fileRes = await CaptureFileWithConsistencyRetryAsync(
                fullPath,
                maxTotalBytes > 0 ? maxTotalBytes - totalRawBytes - (regBytes?.Length ?? 0) : long.MaxValue,
                cancellationToken).ConfigureAwait(false);

            if (fileRes.Outcome == SingleFileCaptureOutcome.SharingOrLockViolation)
            {
                return SkippedOutcome(
                    SaveSnapshotSkipReason.FileLockedSharingViolation,
                    $"存档文件正被进程独占锁定（{Path.GetFileName(fullPath)}），已跳过以避免半写损坏。");
            }

            if (fileRes.Outcome == SingleFileCaptureOutcome.ConcurrentModification)
            {
                return SkippedOutcome(
                    SaveSnapshotSkipReason.ConcurrentModificationDetected,
                    $"存档文件在捕获期间持续变动（{Path.GetFileName(fullPath)}），重试 {MaxConsistencyAttempts} 次后仍未稳定，已跳过防撕裂。");
            }

            if (fileRes.Outcome == SingleFileCaptureOutcome.SizeThresholdExceeded)
            {
                return SkippedOutcome(
                    SaveSnapshotSkipReason.SizeThresholdExceeded,
                    $"存档文件在捕获期间超过大小阈值（{FormatMegabytes(maxTotalBytes)} MB），已非阻塞跳过。");
            }

            if (fileRes.Outcome == SingleFileCaptureOutcome.Disappeared || fileRes.Ingest is null)
            {
                continue;
            }

            CasFileIngestResult ingest = fileRes.Ingest;
            totalRawBytes += ingest.RawSizeBytes;
            totalStoredBytes += ingest.StoredCompressedBytes;
            deduplicatedSavedBytes += Math.Max(0, ingest.RawSizeBytes - ingest.StoredCompressedBytes);
            newChunks += ingest.NewChunkCount;
            reusedChunks += ingest.ReusedChunkCount;
            fsFileCount++;

            capturedFiles.Add(new SaveSnapshotFileRecord
            {
                SnapshotId = snapshotId,
                LogicalPath = logicalPath,
                OriginalPath = fullPath,
                Category = category,
                FileSize = ingest.RawSizeBytes,
                LastWriteTimeUtc = fileRes.LastWriteTimeUtc,
                FileBlake3 = ingest.FileBlake3,
                ChunkHashes = ingest.ChunkHashes,
            });
        }

        int registryKeyCount = 0;
        if (regSnapshot is not null && regBytes is not null && regSnapshot.Keys.Count > 0)
        {
            CasFileIngestResult regIngest = await _casStore.StoreBytesAsync(regBytes, cancellationToken)
                .ConfigureAwait(false);
            totalRawBytes += regIngest.RawSizeBytes;
            totalStoredBytes += regIngest.StoredCompressedBytes;
            deduplicatedSavedBytes += Math.Max(0, regIngest.RawSizeBytes - regIngest.StoredCompressedBytes);
            newChunks += regIngest.NewChunkCount;
            reusedChunks += regIngest.ReusedChunkCount;
            registryKeyCount = regSnapshot.Keys.Count;

            capturedFiles.Add(new SaveSnapshotFileRecord
            {
                SnapshotId = snapshotId,
                LogicalPath = RegistryLogicalPath,
                OriginalPath = regSnapshot.Keys[0].KeyPath,
                Category = SaveFileCategory.RegistryExport,
                FileSize = regIngest.RawSizeBytes,
                LastWriteTimeUtc = _timeProvider.GetUtcNow(),
                FileBlake3 = regIngest.FileBlake3,
                ChunkHashes = regIngest.ChunkHashes,
            });
        }

        if (capturedFiles.Count == 0)
        {
            return SkippedOutcome(
                SaveSnapshotSkipReason.NoSaveLocationsFound,
                "扫描到的存档文件在捕获前均已移除，未创建快照。");
        }

        return new AntiTearCaptureOutcome
        {
            Succeeded = true,
            Skipped = false,
            Message = $"已捕获 {fsFileCount} 个文件" + (registryKeyCount > 0 ? $"与 {registryKeyCount} 个 HKCU 注册表键" : string.Empty) + "。",
            Files = capturedFiles,
            FileCount = fsFileCount,
            RegistryKeyCount = registryKeyCount,
            TotalRawBytes = totalRawBytes,
            TotalStoredBytes = totalStoredBytes,
            DeduplicatedBytesSaved = deduplicatedSavedBytes,
            NewChunkCount = newChunks,
            ReusedChunkCount = reusedChunks,
            Warnings = warnings,
        };
    }

    private async Task<SingleFileCaptureResult> CaptureFileWithConsistencyRetryAsync(
        string sourcePath,
        long remainingBudgetBytes,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxConsistencyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo preInfo;
            try
            {
                preInfo = new FileInfo(sourcePath);
                if (!preInfo.Exists)
                {
                    return new SingleFileCaptureResult(SingleFileCaptureOutcome.Disappeared, null, default);
                }

                if (preInfo.Length > remainingBudgetBytes)
                {
                    return new SingleFileCaptureResult(SingleFileCaptureOutcome.SizeThresholdExceeded, null, default);
                }
            }
            catch (FileNotFoundException)
            {
                return new SingleFileCaptureResult(SingleFileCaptureOutcome.Disappeared, null, default);
            }
            catch (DirectoryNotFoundException)
            {
                return new SingleFileCaptureResult(SingleFileCaptureOutcome.Disappeared, null, default);
            }

            long preLen = preInfo.Length;
            DateTime preWriteUtc = preInfo.LastWriteTimeUtc;

            CasFileIngestResult ingest;
            try
            {
                await using var fs = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (fs.Length > remainingBudgetBytes)
                {
                    return new SingleFileCaptureResult(SingleFileCaptureOutcome.SizeThresholdExceeded, null, default);
                }

                ingest = await _casStore.StoreStreamAsync(fs, fs.Length, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return new SingleFileCaptureResult(SingleFileCaptureOutcome.Disappeared, null, default);
            }
            catch (DirectoryNotFoundException)
            {
                return new SingleFileCaptureResult(SingleFileCaptureOutcome.Disappeared, null, default);
            }
            catch (IOException ex) when (IsSharingOrLockViolation(ex))
            {
                return new SingleFileCaptureResult(SingleFileCaptureOutcome.SharingOrLockViolation, null, default);
            }

            _afterReadConsistencyTestHook?.Invoke(sourcePath, attempt);

            FileInfo postInfo = new(sourcePath);
            if (!postInfo.Exists)
            {
                return new SingleFileCaptureResult(SingleFileCaptureOutcome.Disappeared, null, default);
            }

            if (postInfo.Length == preLen
                && postInfo.LastWriteTimeUtc == preWriteUtc
                && ingest.RawSizeBytes == preLen)
            {
                return new SingleFileCaptureResult(
                    SingleFileCaptureOutcome.Captured,
                    ingest,
                    new DateTimeOffset(DateTime.SpecifyKind(postInfo.LastWriteTimeUtc, DateTimeKind.Utc)));
            }

            // Pre/post metadata vector mismatch (torn read): retry up to MaxConsistencyAttempts.
            if (attempt == MaxConsistencyAttempts)
            {
                return new SingleFileCaptureResult(SingleFileCaptureOutcome.ConcurrentModification, null, default);
            }
        }

        return new SingleFileCaptureResult(SingleFileCaptureOutcome.ConcurrentModification, null, default);
    }

    private bool CollectDirectoryFilesSkippingReparsePoints(
        string directoryPath,
        SaveFileCategory category,
        GameRecord game,
        long maxTotalBytes,
        ref long scannedBytes,
        HashSet<string> seenFullPaths,
        List<(string FullPath, string LogicalPath, SaveFileCategory Category)> sink,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDirectoryWalkDepth || IsReparsePoint(directoryPath))
        {
            return false;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(file) || IsIgnoredTempFile(file))
            {
                continue;
            }

            string full = Path.GetFullPath(file);
            if (!seenFullPaths.Add(full))
            {
                continue;
            }

            try
            {
                var fi = new FileInfo(full);
                if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                scannedBytes += fi.Length;
                if (maxTotalBytes > 0 && scannedBytes > maxTotalBytes)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string logical = _ruleResolver.CanonicalizeToLogicalPath(full, game);
            sink.Add((full, logical, category));
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(directoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (string subDir in subDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(subDir))
            {
                continue;
            }

            string dirName = Path.GetFileName(subDir);
            if (IsIgnoredCacheSubdir(dirName))
            {
                continue;
            }

            if (CollectDirectoryFilesSkippingReparsePoints(
                    subDir,
                    category,
                    game,
                    maxTotalBytes,
                    ref scannedBytes,
                    seenFullPaths,
                    sink,
                    depth + 1,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    internal static List<ResolvedSaveLocation> DeduplicateNestedLocations(IReadOnlyList<ResolvedSaveLocation> locations)
    {
        var sorted = locations
            .Where(l => !string.IsNullOrWhiteSpace(l.ResolvedFullPath))
            .OrderBy(l => Path.TrimEndingDirectorySeparator(Path.GetFullPath(l.ResolvedFullPath)).Length)
            .ThenBy(l => l.Category) // Prefer Save over Config if identical path
            .ToList();

        var kept = new List<ResolvedSaveLocation>(sorted.Count);
        var keptNormalizedDirs = new List<string>(sorted.Count);

        foreach (ResolvedSaveLocation loc in sorted)
        {
            string norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc.ResolvedFullPath));
            bool coveredByExisting = keptNormalizedDirs.Any(existing =>
                string.Equals(norm, existing, StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith(existing + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

            if (!coveredByExisting)
            {
                kept.Add(loc);
                keptNormalizedDirs.Add(norm);
            }
        }

        return kept;
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            FileAttributes attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsIgnoredTempFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(AtomicSaveRestorer.RestoreStageSuffix, StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".spawn_tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredCacheSubdir(string dirName) =>
        dirName.Equals("ShaderCache", StringComparison.OrdinalIgnoreCase)
        || dirName.Equals("CrashDumps", StringComparison.OrdinalIgnoreCase)
        || dirName.Equals("Logs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharingOrLockViolation(IOException ex)
    {
        int win32Code = ex.HResult & 0xFFFF;
        return win32Code is Win32ErrorSharingViolation or Win32ErrorLockViolation;
    }

    private static long FormatMegabytes(long bytes) =>
        Math.Max(1, (bytes + (1024 * 1024 - 1)) / (1024 * 1024));

    private static AntiTearCaptureOutcome SkippedOutcome(SaveSnapshotSkipReason reason, string message) =>
        new()
        {
            Succeeded = false,
            Skipped = true,
            SkipReason = reason,
            Message = message,
        };

    private enum SingleFileCaptureOutcome
    {
        Captured,
        Disappeared,
        SharingOrLockViolation,
        ConcurrentModification,
        SizeThresholdExceeded,
    }

    private readonly record struct SingleFileCaptureResult(
        SingleFileCaptureOutcome Outcome,
        CasFileIngestResult? Ingest,
        DateTimeOffset LastWriteTimeUtc);
}
