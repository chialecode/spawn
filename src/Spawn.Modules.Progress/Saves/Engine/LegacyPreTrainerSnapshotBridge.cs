using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress.Saves.Engine;

/// <summary>
/// Idempotent bridge importing completed legacy M3 <c>pre_trainer_snapshots</c> (raw copies + <c>manifest.json</c>
/// + <c>registry_snapshot.json</c>) into the M4 FastCDC CAS store and branching save tree.
/// </summary>
public sealed class LegacyPreTrainerSnapshotBridge
{
    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly CasChunkStore _casStore;
    private readonly ILudusaviRuleResolver _ruleResolver;

    public LegacyPreTrainerSnapshotBridge(
        SpawnDatabase database,
        SpawnPaths paths,
        CasChunkStore casStore,
        ILudusaviRuleResolver ruleResolver)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(casStore);
        ArgumentNullException.ThrowIfNull(ruleResolver);
        _database = database;
        _paths = paths;
        _casStore = casStore;
        _ruleResolver = ruleResolver;
    }

    public async Task<LegacyPreTrainerImportReport> ImportForGameAsync(
        GameRecord game,
        string machineId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        IReadOnlyList<PreTrainerSnapshotRecord> legacyRecords = await _database
            .GetPreTrainerSnapshotsByGameAsync(game.GameId, triggerKindFilter: null, limit: 200, cancellationToken)
            .ConfigureAwait(false);

        if (legacyRecords.Count == 0)
        {
            return new LegacyPreTrainerImportReport(0, 0, 0, [], []);
        }

        SaveBranchRecord mainBranch = await _database
            .EnsureDefaultSaveBranchAsync(game.GameId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        SaveGameStateRecord state = await _database
            .GetOrCreateSaveGameStateAsync(game.GameId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<SaveSnapshotRecord> existingSnapshots = await _database
            .GetSaveSnapshotsByGameAsync(game.GameId, branchId: null, limit: 5, cancellationToken)
            .ConfigureAwait(false);
        bool treeWasEmpty = existingSnapshots.Count == 0;

        // Import oldest first so parent links build chronologically.
        var orderedLegacy = legacyRecords
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.SnapshotId, StringComparer.Ordinal)
            .ToList();

        int importedCount = 0;
        int alreadyImportedCount = 0;
        int skippedCount = 0;
        var importedIds = new List<string>();
        var warnings = new List<string>();

        string? parentSnapshotId = mainBranch.HeadSnapshotId;

        foreach (PreTrainerSnapshotRecord legacy in orderedLegacy)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (legacy.Status != PreTrainerSnapshotStatus.Completed
                || string.IsNullOrWhiteSpace(legacy.ManifestRelativePath))
            {
                skippedCount++;
                continue;
            }

            SaveSnapshotRecord? already = await _database
                .GetSaveSnapshotByLegacyPreTrainerIdAsync(legacy.SnapshotId, cancellationToken)
                .ConfigureAwait(false);
            if (already is not null)
            {
                alreadyImportedCount++;
                parentSnapshotId = already.SnapshotId;
                continue;
            }

            if (!PreTrainerSnapshotManifestStore.TryResolveContainedPath(
                    _paths.PreTrainerSnapshotsDirectory,
                    legacy.ManifestRelativePath,
                    out string manifestFullPath)
                || !File.Exists(manifestFullPath))
            {
                skippedCount++;
                warnings.Add($"旧版快照 {legacy.SnapshotId} 的 manifest.json 已缺失，跳过导入。");
                continue;
            }

            PreTrainerSnapshotManifest? manifest;
            try
            {
                manifest = await PreTrainerSnapshotManifestStore.ReadManifestAsync(manifestFullPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                skippedCount++;
                warnings.Add($"无法读取旧版快照 {legacy.SnapshotId} 清单：{ex.Message}");
                continue;
            }

            if (manifest is null)
            {
                skippedCount++;
                continue;
            }

            string snapshotDir = Path.GetDirectoryName(manifestFullPath)!;
            string newSnapshotId = SpawnId.NewUlid(legacy.CreatedAtUtc);
            var snapshotFiles = new List<SaveSnapshotFileRecord>(manifest.Files.Count + 1);
            long totalRawBytes = 0;
            long totalStoredBytes = 0;
            int fileCount = 0;
            int regKeyCount = 0;

            await using IAsyncDisposable casWriteScope = await _casStore
                .BeginChunkWriteScopeAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (PreTrainerSnapshotFileEntry entry in manifest.Files)
            {
                if (!PreTrainerSnapshotManifestStore.TryResolveContainedPath(
                        snapshotDir,
                        entry.SnapshotRelativePath,
                        out string payloadPath)
                    || !File.Exists(payloadPath))
                {
                    warnings.Add($"旧版快照 {legacy.SnapshotId} 缺少文件 {entry.LogicalRelativePath}，已跳过该文件。");
                    continue;
                }

                CasFileIngestResult ingest = await _casStore.StoreFileAsync(payloadPath, cancellationToken)
                    .ConfigureAwait(false);
                totalRawBytes += ingest.RawSizeBytes;
                totalStoredBytes += ingest.StoredCompressedBytes;
                fileCount++;

                string logicalPath = _ruleResolver.CanonicalizeToLogicalPath(entry.OriginalPath, game);
                snapshotFiles.Add(new SaveSnapshotFileRecord
                {
                    SnapshotId = newSnapshotId,
                    LogicalPath = logicalPath,
                    OriginalPath = entry.OriginalPath,
                    Category = SaveFileCategory.Save,
                    FileSize = ingest.RawSizeBytes,
                    LastWriteTimeUtc = entry.LastWriteTimeUtc,
                    FileBlake3 = ingest.FileBlake3,
                    ChunkHashes = ingest.ChunkHashes,
                });
            }

            if (!string.IsNullOrWhiteSpace(manifest.RegistrySnapshotRelativePath)
                && PreTrainerSnapshotManifestStore.TryResolveContainedPath(
                    snapshotDir,
                    manifest.RegistrySnapshotRelativePath,
                    out string regPayloadPath)
                && File.Exists(regPayloadPath))
            {
                byte[] regBytes = await File.ReadAllBytesAsync(regPayloadPath, cancellationToken).ConfigureAwait(false);
                CasFileIngestResult regIngest = await _casStore.StoreBytesAsync(regBytes, cancellationToken)
                    .ConfigureAwait(false);
                totalRawBytes += regIngest.RawSizeBytes;
                totalStoredBytes += regIngest.StoredCompressedBytes;
                regKeyCount = manifest.RegistryKeyCount > 0 ? manifest.RegistryKeyCount : manifest.RegistryKeys.Count;

                snapshotFiles.Add(new SaveSnapshotFileRecord
                {
                    SnapshotId = newSnapshotId,
                    LogicalPath = AntiTearFileCapture.RegistryLogicalPath,
                    OriginalPath = manifest.RegistryKeys.Count > 0 ? manifest.RegistryKeys[0] : @"HKCU\Software",
                    Category = SaveFileCategory.RegistryExport,
                    FileSize = regIngest.RawSizeBytes,
                    LastWriteTimeUtc = legacy.CreatedAtUtc,
                    FileBlake3 = regIngest.FileBlake3,
                    ChunkHashes = regIngest.ChunkHashes,
                });
            }

            if (snapshotFiles.Count == 0)
            {
                skippedCount++;
                continue;
            }

            string note = legacy.TriggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety
                ? "修改器回滚前保护快照（已导入）"
                : "修改器启动前快照（已导入）";

            var newSnapshot = new SaveSnapshotRecord
            {
                SnapshotId = newSnapshotId,
                GameId = game.GameId,
                BranchId = mainBranch.BranchId,
                ParentSnapshotId = parentSnapshotId,
                TriggerKind = SaveSnapshotTriggerKind.PreTrainerImport,
                Note = note,
                FileCount = fileCount,
                RegistryKeyCount = regKeyCount,
                TotalRawBytes = totalRawBytes,
                TotalStoredBytes = totalStoredBytes,
                CreatedMachineId = machineId,
                PlaytimeActiveSeconds = game.TotalActiveSec,
                CreatedAtUtc = legacy.CreatedAtUtc,
                IsPinned = false,
                LegacyPreTrainerSnapshotId = legacy.SnapshotId,
            };

            // Only advance main branch & active head when the tree was previously empty.
            bool advanceHead = treeWasEmpty || string.IsNullOrWhiteSpace(state.CurrentHeadSnapshotId);
            await _database.CreateSaveSnapshotWithFilesAsync(
                newSnapshot,
                snapshotFiles,
                advanceBranchHead: advanceHead,
                updateGameActiveHead: advanceHead,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            parentSnapshotId = newSnapshotId;
            importedCount++;
            importedIds.Add(newSnapshotId);
        }

        return new LegacyPreTrainerImportReport(
            ImportedCount: importedCount,
            AlreadyImportedCount: alreadyImportedCount,
            SkippedCount: skippedCount,
            ImportedSnapshotIds: importedIds,
            Warnings: warnings);
    }
}
