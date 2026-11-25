using System.Text;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// Wave 1 foundation tests for SQLite Schema v5 (Save Time Machine, branching DAG, Ludusavi rules,
/// cloud sync endpoints), deterministic Gear-Hash <see cref="FastCdcChunker"/>, and <see cref="CasChunkStore"/>.
/// </summary>
public sealed class SaveSchemaAndCasTests
{
    [Fact]
    public async Task Database_MigrationV4ToV5_IsIdempotentAndCreatesAllSevenSaveTables()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        Assert.Equal(5, await sandbox.Database.GetSchemaVersionAsync());

        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "Elden Ring",
            InstallDir = sandbox.CreateSubDirectory("games", "EldenRing"),
            ExecutableRelPath = "eldenring.exe",
            SteamAppId = 1245620,
        });

        await using (var conn = new SqliteConnection($"Data Source={sandbox.Paths.DatabaseFilePath};Pooling=False"))
        {
            await conn.OpenAsync();
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            Assert.Contains("save_branches", tables);
            Assert.Contains("save_game_state", tables);
            Assert.Contains("save_snapshots", tables);
            Assert.Contains("snapshot_files", tables);
            Assert.Contains("cas_chunks", tables);
            Assert.Contains("ludusavi_rules", tables);
            Assert.Contains("cloud_sync_endpoints", tables);

            // Remove v5 marker to verify full re-run idempotency.
            await using var delMarker = conn.CreateCommand();
            delMarker.CommandText = "DELETE FROM schema_migrations WHERE version = 5;";
            await delMarker.ExecuteNonQueryAsync();
        }

        using var reopened = new SpawnDatabase(sandbox.Paths);
        await reopened.InitializeAsync();
        Assert.Equal(5, await reopened.GetSchemaVersionAsync());
        Assert.NotNull(await reopened.GetGameByIdAsync(gameId));
    }

    [Fact]
    public async Task Database_SaveBranchesSnapshotsAndAncestors_FullDagRoundTrip()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameId = SpawnId.NewUlid();
        DateTimeOffset t0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "Hollow Knight",
            InstallDir = sandbox.CreateSubDirectory("games", "HollowKnight"),
            ExecutableRelPath = "hollow_knight.exe",
            SteamAppId = 367520,
        });

        // 1. Default branch & game state creation
        SaveBranchRecord mainBranch = await sandbox.Database.EnsureDefaultSaveBranchAsync(gameId, t0);
        Assert.Equal("main", mainBranch.Name);
        Assert.True(mainBranch.IsDefault);

        SaveGameStateRecord initialState = await sandbox.Database.GetOrCreateSaveGameStateAsync(gameId, t0);
        Assert.Equal(mainBranch.BranchId, initialState.ActiveBranchId);
        Assert.Null(initialState.CurrentHeadSnapshotId);

        // 2. Register two CAS chunks and commit 2 snapshots on main + 1 forked branch from snap1
        var cas = new CasChunkStore(sandbox.Paths, sandbox.Database);
        CasFileIngestResult fileV1 = await cas.StoreBytesAsync(Encoding.UTF8.GetBytes("save-slot-v1-content"));
        CasFileIngestResult fileV2 = await cas.StoreBytesAsync(Encoding.UTF8.GetBytes("save-slot-v2-boss-killed"));
        CasFileIngestResult fileFork = await cas.StoreBytesAsync(Encoding.UTF8.GetBytes("save-slot-bad-ending-branch"));

        string snap1Id = SpawnId.NewUlid(t0);
        await sandbox.Database.CreateSaveSnapshotWithFilesAsync(
            new SaveSnapshotRecord
            {
                SnapshotId = snap1Id,
                GameId = gameId,
                BranchId = mainBranch.BranchId,
                ParentSnapshotId = null,
                TriggerKind = SaveSnapshotTriggerKind.Manual,
                Note = "Before Boss",
                FileCount = 1,
                TotalRawBytes = fileV1.RawSizeBytes,
                TotalStoredBytes = fileV1.StoredCompressedBytes,
                CreatedMachineId = "pc-test-01",
                PlaytimeActiveSeconds = 3600,
                CreatedAtUtc = t0,
                IsPinned = true,
            },
            [
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snap1Id,
                    LogicalPath = "<winLocalAppDataLow>/Team Cherry/Hollow Knight/user1.dat",
                    OriginalPath = @"C:\Temp\user1.dat",
                    Category = SaveFileCategory.Save,
                    FileSize = fileV1.RawSizeBytes,
                    LastWriteTimeUtc = t0,
                    FileBlake3 = fileV1.FileBlake3,
                    ChunkHashes = fileV1.ChunkHashes,
                },
            ]);

        string snap2Id = SpawnId.NewUlid(t0.AddMinutes(5));
        await sandbox.Database.CreateSaveSnapshotWithFilesAsync(
            new SaveSnapshotRecord
            {
                SnapshotId = snap2Id,
                GameId = gameId,
                BranchId = mainBranch.BranchId,
                ParentSnapshotId = snap1Id,
                TriggerKind = SaveSnapshotTriggerKind.PostExit,
                Note = "After Boss",
                FileCount = 1,
                TotalRawBytes = fileV2.RawSizeBytes,
                TotalStoredBytes = fileV2.StoredCompressedBytes,
                CreatedMachineId = "pc-test-01",
                PlaytimeActiveSeconds = 4200,
                CreatedAtUtc = t0.AddMinutes(5),
            },
            [
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snap2Id,
                    LogicalPath = "<winLocalAppDataLow>/Team Cherry/Hollow Knight/user1.dat",
                    OriginalPath = @"C:\Temp\user1.dat",
                    Category = SaveFileCategory.Save,
                    FileSize = fileV2.RawSizeBytes,
                    LastWriteTimeUtc = t0.AddMinutes(5),
                    FileBlake3 = fileV2.FileBlake3,
                    ChunkHashes = fileV2.ChunkHashes,
                },
            ]);

        // Fork a new branch from snap1!
        string forkBranchId = SpawnId.NewUlid(t0.AddMinutes(10));
        await sandbox.Database.UpsertSaveBranchAsync(new SaveBranchRecord
        {
            BranchId = forkBranchId,
            GameId = gameId,
            Name = "bad-ending",
            Description = "Alternative choice fork",
            ColorHex = SaveBranchColors.PickForIndex(1),
            HeadSnapshotId = snap1Id,
            BaseSnapshotId = snap1Id,
            CreatedAtUtc = t0.AddMinutes(10),
            UpdatedAtUtc = t0.AddMinutes(10),
        });

        string snapForkId = SpawnId.NewUlid(t0.AddMinutes(12));
        await sandbox.Database.CreateSaveSnapshotWithFilesAsync(
            new SaveSnapshotRecord
            {
                SnapshotId = snapForkId,
                GameId = gameId,
                BranchId = forkBranchId,
                ParentSnapshotId = snap1Id,
                TriggerKind = SaveSnapshotTriggerKind.BranchFork,
                Note = "Bad ending route",
                FileCount = 1,
                TotalRawBytes = fileFork.RawSizeBytes,
                TotalStoredBytes = fileFork.StoredCompressedBytes,
                CreatedMachineId = "pc-test-01",
                PlaytimeActiveSeconds = 3900,
                CreatedAtUtc = t0.AddMinutes(12),
            },
            [
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snapForkId,
                    LogicalPath = "<winLocalAppDataLow>/Team Cherry/Hollow Knight/user1.dat",
                    OriginalPath = @"C:\Temp\user1.dat",
                    Category = SaveFileCategory.Save,
                    FileSize = fileFork.RawSizeBytes,
                    LastWriteTimeUtc = t0.AddMinutes(12),
                    FileBlake3 = fileFork.FileBlake3,
                    ChunkHashes = fileFork.ChunkHashes,
                },
            ]);

        // Verify DAG topology
        IReadOnlyList<SaveSnapshotRecord> childrenOfSnap1 = await sandbox.Database.GetChildSaveSnapshotsAsync(snap1Id);
        Assert.Equal(2, childrenOfSnap1.Count);
        Assert.Contains(childrenOfSnap1, s => s.SnapshotId == snap2Id);
        Assert.Contains(childrenOfSnap1, s => s.SnapshotId == snapForkId);

        IReadOnlyList<string> forkAncestors = await sandbox.Database.GetSaveSnapshotAncestorIdsAsync(snapForkId, maxDepth: 64);
        Assert.Equal([snapForkId, snap1Id], forkAncestors);

        IReadOnlyList<SaveBranchRecord> branches = await sandbox.Database.GetSaveBranchesByGameAsync(gameId);
        Assert.Equal(2, branches.Count);
        Assert.Equal("main", branches[0].Name);
        Assert.Equal(2, branches[0].SnapshotCount);
        Assert.Equal(snap2Id, branches[0].HeadSnapshotId);
        Assert.Equal("bad-ending", branches[1].Name);
        Assert.Equal(1, branches[1].SnapshotCount);
        Assert.Equal(snapForkId, branches[1].HeadSnapshotId);

        // Verify active head moved to forked branch tip
        SaveGameStateRecord? updatedState = await sandbox.Database.GetSaveGameStateAsync(gameId);
        Assert.NotNull(updatedState);
        Assert.Equal(forkBranchId, updatedState!.ActiveBranchId);
        Assert.Equal(snapForkId, updatedState.CurrentHeadSnapshotId);

        // Verify pin / note updates
        Assert.True(await sandbox.Database.UpdateSaveSnapshotNoteAsync(snapForkId, "Updated bad ending note"));
        Assert.True(await sandbox.Database.SetSaveSnapshotPinnedAsync(snapForkId, true));
        SaveSnapshotRecord? reloadedFork = await sandbox.Database.GetSaveSnapshotByIdAsync(snapForkId);
        Assert.Equal("Updated bad ending note", reloadedFork!.Note);
        Assert.True(reloadedFork.IsPinned);

        // Verify aggregate stats (including unimported legacy pre_trainer_snapshots detection)
        await sandbox.Database.UpsertPreTrainerSnapshotAsync(new PreTrainerSnapshotRecord
        {
            SnapshotId = "legacy-pt-001",
            GameId = gameId,
            TriggerKind = PreTrainerSnapshotTriggerKind.PreTrainer,
            Status = PreTrainerSnapshotStatus.Completed,
            FileCount = 1,
            TotalBytes = 512,
            ManifestRelativePath = $"{gameId}\\legacy-pt-001\\manifest.json",
            CreatedAtUtc = t0.AddHours(-1),
        });

        var statsMap = await sandbox.Database.GetSaveGameAggregateStatsAsync();
        Assert.True(statsMap.TryGetValue(gameId, out SaveGameAggregateStats? gameStats));
        Assert.Equal(2, gameStats!.BranchCount);
        Assert.Equal(3, gameStats.SnapshotCount);
        Assert.Equal(2, gameStats.PinnedCount);
        Assert.Equal(1, gameStats.UnimportedLegacyPreTrainerCount);

        // Deleting non-empty branch with deleteSnapshots=false must fail safely; true deletes and decrements chunk refcounts.
        var (deletedFalse, _) = await sandbox.Database.DeleteSaveBranchAsync(forkBranchId, deleteSnapshots: false, t0.AddHours(1));
        Assert.False(deletedFalse);

        var (deletedTrue, deletedSnaps) = await sandbox.Database.DeleteSaveBranchAsync(forkBranchId, deleteSnapshots: true, t0.AddHours(1));
        Assert.True(deletedTrue);
        Assert.Equal(1, deletedSnaps);

        CasChunkRecord? forkChunk = await sandbox.Database.GetCasChunkByHashAsync(fileFork.ChunkHashes[0]);
        Assert.NotNull(forkChunk);
        Assert.Equal(0, forkChunk!.RefCount);
        Assert.NotNull(forkChunk.LastUnreferencedAtUtc);
    }

    [Fact]
    public async Task Database_LudusaviRulesCloudEndpointsAndSettings_RoundTrip()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        DateTimeOffset now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        // 1. Ludusavi rules lookup by SteamAppId, GogId, Title, and Alias
        int inserted = await sandbox.Database.UpsertLudusaviRulesBatchAsync(
        [
            new LudusaviRuleRecord
            {
                RuleName = "ELDEN RING",
                SteamAppId = 1245620,
                Aliases = ["艾尔登法环"],
                SavePaths = ["<winAppData>/EldenRing/<storeUserId>/*.sl2"],
                ConfigPaths = ["<winAppData>/EldenRing/GraphicsConfig.xml"],
                RegistryRules = [],
                UpdatedAtUtc = now,
            },
            new LudusaviRuleRecord
            {
                RuleName = "LIMBO",
                GogId = "1724299977",
                SavePaths = ["<base>/save_game/*"],
                ConfigPaths = ["<base>/settings.txt"],
                RegistryRules = [],
                UpdatedAtUtc = now,
            },
        ]);
        Assert.Equal(2, inserted);
        Assert.Equal(2, await sandbox.Database.GetLudusaviRuleCountAsync());

        Assert.Equal("ELDEN RING", (await sandbox.Database.FindLudusaviRuleAsync(steamAppId: 1245620))?.RuleName);
        Assert.Equal("LIMBO", (await sandbox.Database.FindLudusaviRuleAsync(gogId: "1724299977"))?.RuleName);
        Assert.Equal("ELDEN RING", (await sandbox.Database.FindLudusaviRuleAsync(title: "艾尔登法环"))?.RuleName);

        // 2. Cloud sync endpoints default toggle & CRUD
        string ep1Id = SpawnId.NewUlid(now);
        string ep2Id = SpawnId.NewUlid(now.AddSeconds(1));
        await sandbox.Database.UpsertCloudSyncEndpointAsync(new CloudSyncEndpointRecord
        {
            EndpointId = ep1Id,
            DisplayName = "Local Sync Folder",
            ProviderType = CloudSyncProviderType.LocalFolder,
            EndpointUrl = sandbox.CreateSubDirectory("cloud-sync-1"),
            IsDefault = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await sandbox.Database.UpsertCloudSyncEndpointAsync(new CloudSyncEndpointRecord
        {
            EndpointId = ep2Id,
            DisplayName = "Cloudflare R2",
            ProviderType = CloudSyncProviderType.S3,
            EndpointUrl = "https://example.r2.cloudflarestorage.com",
            BucketOrRoot = "spawn-saves",
            Region = "auto",
            SecretCredentialId = "cloud-ep-" + ep2Id,
            IsDefault = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        CloudSyncEndpointRecord? defEp = await sandbox.Database.GetDefaultCloudSyncEndpointAsync();
        Assert.NotNull(defEp);
        Assert.Equal(ep2Id, defEp!.EndpointId);
        Assert.True(defEp.HasSecretConfigured);
        Assert.False((await sandbox.Database.GetCloudSyncEndpointByIdAsync(ep1Id))!.IsDefault);
        Assert.Equal(2, (await sandbox.Database.GetAllCloudSyncEndpointsAsync()).Count);
        Assert.True(await sandbox.Database.DeleteCloudSyncEndpointAsync(ep1Id));

        // 3. Global SaveSettingsDto auto-generates stable MachineId and clamps bounds
        SaveSettingsDto s1 = await sandbox.Database.GetSaveSettingsAsync();
        SaveSettingsDto s2 = await sandbox.Database.GetSaveSettingsAsync();
        Assert.False(string.IsNullOrWhiteSpace(s1.MachineId));
        Assert.Equal(s1.MachineId, s2.MachineId);
        Assert.Equal(SaveSettingsDto.DefaultQuiescentDebounceMs, s1.QuiescentDebounceMs);

        await sandbox.Database.SaveSaveSettingsAsync(s1 with
        {
            QuiescentDebounceMs = 99999,
            DefaultMaxRollingSnapshots = 50,
            IncludeConfigFilesByDefault = true,
            GcGracePeriodHours = 48,
        });
        SaveSettingsDto loaded = await sandbox.Database.GetSaveSettingsAsync();
        Assert.Equal(SaveSettingsDto.MaxQuiescentDebounceMs, loaded.QuiescentDebounceMs);
        Assert.Equal(50, loaded.DefaultMaxRollingSnapshots);
        Assert.True(loaded.IncludeConfigFilesByDefault);
        Assert.Equal(48, loaded.GcGracePeriodHours);
    }

    [Fact]
    public async Task FastCdcChunker_IsDeterministicMatchesStreamingAndResistsBoundaryShift()
    {
        // 1. Gear table invariants
        Assert.Equal(256, FastCdcChunker.GearTable.Length);
        Assert.Equal(256, FastCdcChunker.GearTable.ToArray().Distinct().Count());

        // 2. Small file (< 2 MiB) single-chunk fast path
        byte[] smallData = CreateDeterministicRandomBytes(1_500_000, seed: 42);
        var smallMemChunks = FastCdcChunker.ChunkBytes(smallData);
        Assert.Single(smallMemChunks);
        Assert.Equal(smallData.Length, smallMemChunks[0].Data.Length);

        var smallStreamChunks = new List<byte[]>();
        using (var ms = new MemoryStream(smallData))
        {
            await FastCdcChunker.ChunkStreamAsync(
                ms,
                knownTotalLength: null,
                (slice, _, _) =>
                {
                    smallStreamChunks.Add(slice.ToArray());
                    return ValueTask.CompletedTask;
                });
        }

        Assert.Single(smallStreamChunks);
        Assert.Equal(smallData, smallStreamChunks[0]);

        // 3. Large file (8 MiB): every chunk respects [MinSize, MaxSize] (except final tail) and streaming == memory
        byte[] largeData = CreateDeterministicRandomBytes(8 * 1024 * 1024, seed: 1337);
        IReadOnlyList<FastCdcChunkSlice> memChunks = FastCdcChunker.ChunkBytes(largeData);
        Assert.True(memChunks.Count >= 3, $"Expected >=3 CDC chunks for 8 MiB payload, got {memChunks.Count}.");

        for (int i = 0; i < memChunks.Count; i++)
        {
            int len = memChunks[i].Data.Length;
            Assert.True(len <= FastCdcChunker.MaxSize);
            if (i < memChunks.Count - 1)
            {
                Assert.True(len >= FastCdcChunker.MinSize);
            }
        }

        var streamHashes = new List<string>();
        using (var ms = new MemoryStream(largeData))
        {
            await FastCdcChunker.ChunkStreamAsync(
                ms,
                knownTotalLength: largeData.Length,
                (slice, _, _) =>
                {
                    streamHashes.Add(CasChunkStore.ComputeBlake3Hex(slice.Span));
                    return ValueTask.CompletedTask;
                });
        }

        var memHashes = memChunks.Select(c => CasChunkStore.ComputeBlake3Hex(c.Data.Span)).ToList();
        Assert.Equal(memHashes, streamHashes);

        // 4. Boundary-shift resistance: insert 37 bytes at offset 128 -> majority of trailing chunks stay identical!
        byte[] shifted = new byte[largeData.Length + 37];
        Buffer.BlockCopy(largeData, 0, shifted, 0, 128);
        for (int i = 0; i < 37; i++)
        {
            shifted[128 + i] = (byte)(i * 7 + 3);
        }

        Buffer.BlockCopy(largeData, 128, shifted, 128 + 37, largeData.Length - 128);

        var shiftedHashes = FastCdcChunker.ChunkBytes(shifted)
            .Select(c => CasChunkStore.ComputeBlake3Hex(c.Data.Span))
            .ToHashSet(StringComparer.Ordinal);

        int sharedCount = memHashes.Count(h => shiftedHashes.Contains(h));
        Assert.True(
            sharedCount >= memHashes.Count - 2,
            $"Expected FastCDC to preserve almost all chunks after 37-byte prefix insertion; shared {sharedCount}/{memHashes.Count}.");
    }

    [Fact]
    public async Task CasChunkStore_DeduplicatesVerifiesCorruptionAndReclaimsAfterGcGracePeriod()
    {
        await using var sandbox = new TestSandbox();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        await sandbox.Database.InitializeAsync();

        var store = new CasChunkStore(sandbox.Paths, sandbox.Database, clock);
        byte[] payload = CreateDeterministicRandomBytes(3 * 1024 * 1024, seed: 9001);

        // 1. First ingest writes new compressed chunks; second ingest deduplicates 100%
        CasFileIngestResult ingest1 = await store.StoreBytesAsync(payload);
        Assert.True(ingest1.NewChunkCount >= 1);
        Assert.Equal(0, ingest1.ReusedChunkCount);

        CasFileIngestResult ingest2 = await store.StoreBytesAsync(payload);
        Assert.Equal(ingest1.FileBlake3, ingest2.FileBlake3);
        Assert.Equal(ingest1.ChunkHashes, ingest2.ChunkHashes);
        Assert.Equal(0, ingest2.NewChunkCount);
        Assert.Equal(ingest1.ChunkHashes.Count, ingest2.ReusedChunkCount);

        // 2. Round-trip reconstruction matches original bytes & file BLAKE3
        using (var reconstructed = new MemoryStream())
        {
            long written = await store.ReconstructToStreamAsync(ingest1.ChunkHashes, reconstructed, ingest1.FileBlake3);
            Assert.Equal(payload.Length, written);
            Assert.Equal(payload, reconstructed.ToArray());
        }

        // 3. Verified compressed chunk ingest rejects hash mismatch
        string firstChunkHash = ingest1.ChunkHashes[0];
        byte[] validZst = await store.ReadChunkCompressedAsync(firstChunkHash);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.StoreVerifiedCompressedChunkAsync(new string('0', 64), validZst));

        // 4. Commit a snapshot referencing the chunks so ref_count == 1
        string gameId = SpawnId.NewUlid(clock.GetUtcNow());
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "Test RPG",
            InstallDir = sandbox.CreateSubDirectory("games", "TestRpg"),
            ExecutableRelPath = "rpg.exe",
        });
        SaveBranchRecord mainBranch = await sandbox.Database.EnsureDefaultSaveBranchAsync(gameId, clock.GetUtcNow());
        string snapId = SpawnId.NewUlid(clock.GetUtcNow());
        await sandbox.Database.CreateSaveSnapshotWithFilesAsync(
            new SaveSnapshotRecord
            {
                SnapshotId = snapId,
                GameId = gameId,
                BranchId = mainBranch.BranchId,
                FileCount = 1,
                TotalRawBytes = ingest1.RawSizeBytes,
                TotalStoredBytes = ingest1.StoredCompressedBytes,
                CreatedMachineId = "pc-gc",
                CreatedAtUtc = clock.GetUtcNow(),
            },
            [
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snapId,
                    LogicalPath = "<base>/save.bin",
                    OriginalPath = Path.Combine(sandbox.RootDirectory, "save.bin"),
                    Category = SaveFileCategory.Save,
                    FileSize = ingest1.RawSizeBytes,
                    LastWriteTimeUtc = clock.GetUtcNow(),
                    FileBlake3 = ingest1.FileBlake3,
                    ChunkHashes = ingest1.ChunkHashes,
                },
            ]);

        Assert.Equal(1, (await sandbox.Database.GetCasChunkByHashAsync(firstChunkHash))!.RefCount);

        // 5. Corruption detection: tamper a different chunk on disk -> ReadChunkRawAsync throws
        string tamperedHash = ingest1.ChunkHashes[^1];
        string tamperedPath = store.GetChunkFilePath(tamperedHash);
        byte[] origBytes = await File.ReadAllBytesAsync(tamperedPath);
        byte[] corruptedPayload;
        using (var comp = new ZstdSharp.Compressor(3))
        {
            corruptedPayload = comp.Wrap(Encoding.UTF8.GetBytes("corrupted-content")).ToArray();
        }

        await File.WriteAllBytesAsync(tamperedPath, corruptedPayload);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadChunkRawAsync(tamperedHash, verifyHash: true));
        await File.WriteAllBytesAsync(tamperedPath, origBytes);

        // 6. Delete snapshot -> ref_count drops to 0; GC respects grace period!
        var (deleted, unrefCount) = await sandbox.Database.DeleteSaveSnapshotAsync(snapId, clock.GetUtcNow());
        Assert.True(deleted);
        Assert.Equal(ingest1.ChunkHashes.Distinct().Count(), unrefCount);

        SaveGcReport gcTooEarly = await store.RunGarbageCollectionAsync(TimeSpan.FromHours(24));
        Assert.Equal(0, gcTooEarly.DeletedChunkCount);
        Assert.True(store.ChunkFileExists(firstChunkHash));

        clock.Advance(TimeSpan.FromHours(25));
        SaveGcReport gcAfterGrace = await store.RunGarbageCollectionAsync(TimeSpan.FromHours(24));
        Assert.Equal(ingest1.ChunkHashes.Distinct().Count(), gcAfterGrace.DeletedChunkCount);
        Assert.True(gcAfterGrace.ReclaimedBytes > 0);
        Assert.False(store.ChunkFileExists(firstChunkHash));
        Assert.Null(await sandbox.Database.GetCasChunkByHashAsync(firstChunkHash));
    }

    private static byte[] CreateDeterministicRandomBytes(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] buffer = new byte[length];
        rng.NextBytes(buffer);
        return buffer;
    }
}
