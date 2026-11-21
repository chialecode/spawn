using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress.Snapshots;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Snapshots;

/// <summary>
/// M3-A-14 automated verification: Pre-trainer lightweight save snapshots — atomic <c>manifest.json</c>,
/// deterministic HKCU registry JSON, ReparsePoint skipping, non-blocking 256MB threshold / sharing &amp; lock violation
/// skips, copy consistency retry, rolling retention (PreTrainer N + isolated PreRestoreSafety 1), running-game guard,
/// safety-fence validation, and two-phase staged SHA-256 verified rollback.
/// </summary>
public sealed class PreTrainerSaveSnapshotServiceTests
{
    [Fact]
    public async Task Snapshots_CreateAndRestore_WritesAtomicManifestCreatesPreRestoreSafetyAndRestoresExactFileAndRegistryHashes()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string regSubRoot = $@"Software\SPAWN-Test\{Guid.NewGuid():N}";
        string gameRegSubKey = $@"{regSubRoot}\TestStudio\SnapshotAdventure";
        string gameRegFull = $@"HKCU\{gameRegSubKey}";

        try
        {
            var clock = new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
            string installDir = sandbox.CreateSubDirectory("games", "SnapshotAdventure");
            string saveDir = sandbox.CreateSubDirectory("saves", "SnapshotAdventure");
            string nestedDir = Directory.CreateDirectory(Path.Combine(saveDir, "profile1")).FullName;

            string slot0Path = Path.Combine(saveDir, "slot0.sav");
            string prefsPath = Path.Combine(nestedDir, "prefs.dat");
            string readOnlyPath = Path.Combine(saveDir, "readonly.sav");

            byte[] slot0Original = "ORIGINAL_SLOT0_GOLD_100_HP_50"u8.ToArray();
            byte[] prefsOriginal = [0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB, 0xCC];
            byte[] readOnlyOriginal = "ORIGINAL_READONLY_FLAG"u8.ToArray();

            await File.WriteAllBytesAsync(slot0Path, slot0Original);
            await File.WriteAllBytesAsync(prefsPath, prefsOriginal);
            await File.WriteAllBytesAsync(readOnlyPath, readOnlyOriginal);
            File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);

            string expectedSlot0Sha = Convert.ToHexStringLower(SHA256.HashData(slot0Original));
            string expectedPrefsSha = Convert.ToHexStringLower(SHA256.HashData(prefsOriginal));
            string expectedReadOnlySha = Convert.ToHexStringLower(SHA256.HashData(readOnlyOriginal));

            using (var reg = Registry.CurrentUser.CreateSubKey(gameRegSubKey, writable: true)!)
            {
                reg.SetValue("Gold", 100, RegistryValueKind.DWord);
                reg.SetValue("HeroName", "OriginalHero", RegistryValueKind.String);
                reg.SetValue("Checksum", new byte[] { 0x10, 0x20, 0x30 }, RegistryValueKind.Binary);
            }

            string expectedRegSha = DeterministicHkcuRegistrySerializer.ComputeSha256Hex(
                DeterministicHkcuRegistrySerializer.ExportKeys([gameRegFull]));

            string gameId = SpawnId.NewUlid(clock.GetUtcNow());
            string trainerId = SpawnId.NewUlid(clock.GetUtcNow());
            await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId, installDir);

            var gameDataStub = new StubSnapshotGameDataService(_ =>
            [
                new GameDataEntryDto
                {
                    EntryId = "save-dir",
                    GameId = gameId,
                    Category = GameDataCategory.SaveData,
                    SourceRule = GameDataSourceRule.NativeRule,
                    PathOrRegistryKey = saveDir,
                    Exists = Directory.Exists(saveDir),
                    IsSafeToClean = true,
                },
                new GameDataEntryDto
                {
                    EntryId = "reg-key",
                    GameId = gameId,
                    Category = GameDataCategory.RegistryData,
                    SourceRule = GameDataSourceRule.UnityEngine,
                    PathOrRegistryKey = gameRegFull,
                    Exists = true,
                    IsSafeToClean = true,
                },
            ]);
            var launchStub = new StubSnapshotGameLaunchService();
            var service = new PreTrainerSaveSnapshotService(sandbox.Database, sandbox.Paths, gameDataStub, launchStub, clock);

            // 1. Create PreTrainer snapshot.
            var createResult = await service.CreateAsync(gameId, trainerId);

            Assert.True(createResult.Succeeded, createResult.Message);
            Assert.Equal(PreTrainerSnapshotStatus.Completed, createResult.Status);
            Assert.Equal(PreTrainerSnapshotSkipReason.None, createResult.SkipReason);
            Assert.NotNull(createResult.Snapshot);
            Assert.Equal(trainerId, createResult.Snapshot!.TrainerId);
            Assert.Equal(PreTrainerSnapshotTriggerKind.PreTrainer, createResult.Snapshot.TriggerKind);
            Assert.Equal(3, createResult.Snapshot.FileCount);
            Assert.True(createResult.Snapshot.TotalBytes > slot0Original.Length + prefsOriginal.Length + readOnlyOriginal.Length);
            Assert.False(string.IsNullOrWhiteSpace(createResult.Snapshot.ManifestRelativePath));

            string manifestPath = Path.Combine(sandbox.Paths.PreTrainerSnapshotsDirectory, createResult.Snapshot.ManifestRelativePath!);
            string snapshotDir = Path.GetDirectoryName(manifestPath)!;
            Assert.True(File.Exists(manifestPath));
            Assert.True(File.Exists(Path.Combine(snapshotDir, "registry_snapshot.json")));
            Assert.Empty(Directory.EnumerateFiles(snapshotDir, "*.tmp"));

            var manifest = await PreTrainerSnapshotManifestStore.ReadManifestAsync(manifestPath);
            Assert.NotNull(manifest);
            Assert.Equal(3, manifest!.Files.Count);
            Assert.Equal(expectedRegSha, manifest.RegistrySnapshotSha256);
            Assert.Contains(manifest.Files, f => f.OriginalPath.Equals(slot0Path, StringComparison.OrdinalIgnoreCase) && f.Sha256 == expectedSlot0Sha);
            Assert.Contains(manifest.Files, f => f.OriginalPath.Equals(prefsPath, StringComparison.OrdinalIgnoreCase) && f.Sha256 == expectedPrefsSha);
            Assert.Contains(manifest.Files, f => f.OriginalPath.Equals(readOnlyPath, StringComparison.OrdinalIgnoreCase) && f.Sha256 == expectedReadOnlySha);

            // 2. Corrupt saves, delete nested save file, and mutate HKCU registry after trainer launch.
            await File.WriteAllBytesAsync(slot0Path, "CORRUPTED_GOLD_999999999"u8.ToArray());
            File.Delete(prefsPath);
            File.SetAttributes(readOnlyPath, FileAttributes.Normal);
            await File.WriteAllBytesAsync(readOnlyPath, "CORRUPTED_READONLY"u8.ToArray());
            File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);

            using (var reg = Registry.CurrentUser.OpenSubKey(gameRegSubKey, writable: true)!)
            {
                reg.SetValue("Gold", 999999, RegistryValueKind.DWord);
                reg.SetValue("TrainerInjectedFlag", 1, RegistryValueKind.DWord);
            }

            // 3. Execute one-click RestoreAsync.
            clock.Advance(TimeSpan.FromMinutes(3));
            var restoreResult = await service.RestoreAsync(createResult.Snapshot.SnapshotId);

            Assert.True(restoreResult.Succeeded, restoreResult.Message);
            Assert.Equal(3, restoreResult.RestoredFileCount);
            Assert.Equal(1, restoreResult.RestoredRegistryKeyCount);
            Assert.NotNull(restoreResult.SafetySnapshot);
            Assert.Equal(PreTrainerSnapshotTriggerKind.PreRestoreSafety, restoreResult.SafetySnapshot!.TriggerKind);
            Assert.Equal(PreTrainerSnapshotStatus.Completed, restoreResult.SafetySnapshot.Status);

            // Zero leftover .spawn_stage_tmp files after commit.
            Assert.Empty(Directory.EnumerateFiles(saveDir, "*.spawn_stage_tmp", SearchOption.AllDirectories));

            // Every file and HKCU registry export matches original SHA-256 identically!
            Assert.Equal(expectedSlot0Sha, await PreTrainerSnapshotManifestStore.ComputeFileSha256HexAsync(slot0Path));
            Assert.Equal(expectedPrefsSha, await PreTrainerSnapshotManifestStore.ComputeFileSha256HexAsync(prefsPath));
            Assert.Equal(expectedReadOnlySha, await PreTrainerSnapshotManifestStore.ComputeFileSha256HexAsync(readOnlyPath));

            string restoredRegSha = DeterministicHkcuRegistrySerializer.ComputeSha256Hex(
                DeterministicHkcuRegistrySerializer.ExportKeys([gameRegFull]));
            Assert.Equal(expectedRegSha, restoredRegSha);

            // Ledger restored_at_utc updated and both PreTrainer + PreRestoreSafety are visible in ListAsync.
            var refreshed = await sandbox.Database.GetPreTrainerSnapshotByIdAsync(createResult.Snapshot.SnapshotId);
            Assert.NotNull(refreshed!.RestoredAtUtc);
            Assert.Equal(clock.GetUtcNow(), refreshed.RestoredAtUtc);

            var listed = await service.ListAsync(gameId);
            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, s => s.TriggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety);
            Assert.Contains(listed, s => s.SnapshotId == createResult.Snapshot.SnapshotId);
        }
        finally
        {
            SnapshotTestSandbox.CleanupTemporaryHkcuKey(regSubRoot);
        }
    }

    [Fact]
    public async Task Snapshots_ExceedsDefault256MbThreshold_NonBlockingSkippedAndPersisted()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "HugeSaveGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "HugeSaveGame");
        string hugeSavePath = Path.Combine(saveDir, "giant_world.sav");

        // Create a 257 MB logical file instantly via SetLength without copying 257 MB.
        await using (var fs = new FileStream(hugeSavePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(257L * 1024L * 1024L);
        }

        string gameId = SpawnId.NewUlid();
        await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId: null, installDir);

        // Verify default TrainerSettingsDto threshold is 256 MB.
        var settings = await sandbox.Database.GetTrainerSettingsAsync();
        Assert.Equal(256, settings.PreTrainerSnapshotMaxSizeMb);

        var gameDataStub = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "huge-save",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = true,
                IsSafeToClean = true,
            },
        ]);
        var service = new PreTrainerSaveSnapshotService(
            sandbox.Database,
            sandbox.Paths,
            gameDataStub,
            new StubSnapshotGameLaunchService());

        var result = await service.CreateAsync(gameId, trainerId: null);

        Assert.False(result.Succeeded);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, result.Status);
        Assert.Equal(PreTrainerSnapshotSkipReason.SizeThresholdExceeded, result.SkipReason);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, result.Snapshot!.Status);
        Assert.Equal(PreTrainerSnapshotSkipReason.SizeThresholdExceeded.ToString(), result.Snapshot.SkipReason);

        var persisted = await service.ListAsync(gameId);
        Assert.Single(persisted);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, persisted[0].Status);
        Assert.Equal("SizeThresholdExceeded", persisted[0].SkipReason);

        // No partial snapshot directory created for a pre-scan size-threshold skip.
        string gameSnapRoot = Path.Combine(sandbox.Paths.PreTrainerSnapshotsDirectory, gameId);
        Assert.False(Directory.Exists(gameSnapRoot));
    }

    [Fact]
    public async Task Snapshots_SharingViolationAndLockViolation_NonBlockingSkippedAndPersisted()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "LockedSaveGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "LockedSaveGame");
        string lockedFile = Path.Combine(saveDir, "active.sav");
        await File.WriteAllBytesAsync(lockedFile, "LOCKED_SAVE_CONTENT_1234567890"u8.ToArray());

        string gameId = SpawnId.NewUlid();
        await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId: null, installDir);

        var gameDataStub = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "locked-save",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = true,
                IsSafeToClean = true,
            },
        ]);
        var clock = new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 13, 0, 0, TimeSpan.Zero));
        var service = new PreTrainerSaveSnapshotService(
            sandbox.Database,
            sandbox.Paths,
            gameDataStub,
            new StubSnapshotGameLaunchService(),
            clock);

        // 1. ERROR_SHARING_VIOLATION (32) via FileShare.None.
        PreTrainerSnapshotResult sharingResult;
        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            sharingResult = await service.CreateAsync(gameId, trainerId: null);
        }

        Assert.False(sharingResult.Succeeded);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, sharingResult.Status);
        Assert.Equal(PreTrainerSnapshotSkipReason.FileLockedSharingViolation, sharingResult.SkipReason);
        Assert.NotNull(sharingResult.Snapshot);

        // Partial directory must be cleaned up.
        string partialDir1 = sandbox.Paths.GetPreTrainerSnapshotDirectory(gameId, sharingResult.Snapshot!.SnapshotId, createDirectory: false);
        Assert.False(Directory.Exists(partialDir1));

        // 2. ERROR_LOCK_VIOLATION (33) via byte-range lock on an open FileShare.ReadWrite handle.
        clock.Advance(TimeSpan.FromMinutes(1));
        PreTrainerSnapshotResult rangeLockResult;
        await using (var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            lockStream.Lock(0, lockStream.Length);
            try
            {
                rangeLockResult = await service.CreateAsync(gameId, trainerId: null);
            }
            finally
            {
                lockStream.Unlock(0, lockStream.Length);
            }
        }

        Assert.False(rangeLockResult.Succeeded);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, rangeLockResult.Status);
        Assert.Equal(PreTrainerSnapshotSkipReason.FileLockedSharingViolation, rangeLockResult.SkipReason);
        Assert.NotNull(rangeLockResult.Snapshot);

        var persisted = await service.ListAsync(gameId);
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, r =>
        {
            Assert.Equal(PreTrainerSnapshotStatus.Skipped, r.Status);
            Assert.Equal("FileLockedSharingViolation", r.SkipReason);
        });
    }

    [Fact]
    public async Task Snapshots_ConcurrentModificationDuringCopy_RetriesOnceThenSkipsIfStillMutating()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "MutatingSaveGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "MutatingSaveGame");
        string savePath = Path.Combine(saveDir, "slot.sav");
        await File.WriteAllBytesAsync(savePath, "INITIAL_V1"u8.ToArray());

        string gameId = SpawnId.NewUlid();
        await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId: null, installDir);

        var gameDataStub = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "mutating-save",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = true,
                IsSafeToClean = true,
            },
        ]);

        // Case 1: Mutates on attempt 0 only -> retry on attempt 1 succeeds with updated consistent content!
        int singleMutateCalls = 0;
        byte[] retryFinalBytes = "UPDATED_CONSISTENT_V2_LONGER"u8.ToArray();
        var serviceRetrySucceeds = new PreTrainerSaveSnapshotService(
            sandbox.Database,
            sandbox.Paths,
            gameDataStub,
            new StubSnapshotGameLaunchService(),
            new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 14, 0, 0, TimeSpan.Zero)),
            logger: null,
            afterSingleFileCopyHook: (path, attempt) =>
            {
                singleMutateCalls++;
                if (attempt == 0)
                {
                    File.WriteAllBytes(path, retryFinalBytes);
                }
            });

        var retryOk = await serviceRetrySucceeds.CreateAsync(gameId, trainerId: null);
        Assert.Equal(2, singleMutateCalls);
        Assert.True(retryOk.Succeeded, retryOk.Message);
        Assert.Equal(PreTrainerSnapshotStatus.Completed, retryOk.Status);

        string manifestPath = Path.Combine(sandbox.Paths.PreTrainerSnapshotsDirectory, retryOk.Snapshot!.ManifestRelativePath!);
        var manifest = await PreTrainerSnapshotManifestStore.ReadManifestAsync(manifestPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(retryFinalBytes)), manifest!.Files[0].Sha256);

        // Case 2: Mutates on EVERY attempt (0 and 1) -> non-blocking Skipped (ConcurrentModificationDetected).
        int alwaysMutateCalls = 0;
        var serviceAlwaysMutates = new PreTrainerSaveSnapshotService(
            sandbox.Database,
            sandbox.Paths,
            gameDataStub,
            new StubSnapshotGameLaunchService(),
            new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 14, 5, 0, TimeSpan.Zero)),
            logger: null,
            afterSingleFileCopyHook: (path, attempt) =>
            {
                alwaysMutateCalls++;
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"MUTATION_ATTEMPT_{attempt}_{new string('X', attempt + 10)}"));
            });

        var skipped = await serviceAlwaysMutates.CreateAsync(gameId, trainerId: null);
        Assert.Equal(2, alwaysMutateCalls);
        Assert.False(skipped.Succeeded);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, skipped.Status);
        Assert.Equal(PreTrainerSnapshotSkipReason.ConcurrentModificationDetected, skipped.SkipReason);
        Assert.NotNull(skipped.Snapshot);
        Assert.False(Directory.Exists(sandbox.Paths.GetPreTrainerSnapshotDirectory(gameId, skipped.Snapshot!.SnapshotId, createDirectory: false)));
    }

    [Fact]
    public async Task Snapshots_SkipsReparsePointsAndRollingRetentionPreservesQuotaAndNeverFollowsJunctions()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "RollingGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "RollingGame");
        string externalSecretDir = sandbox.CreateSubDirectory("external_secret");
        string externalPreciousDir = sandbox.CreateSubDirectory("external_precious");

        string saveFile = Path.Combine(saveDir, "progress.sav");
        string secretFile = Path.Combine(externalSecretDir, "do_not_copy.txt");
        string preciousFile = Path.Combine(externalPreciousDir, "must_survive_prune.txt");

        await File.WriteAllTextAsync(saveFile, "GEN_0", Encoding.UTF8);
        await File.WriteAllTextAsync(secretFile, "SECRET_OUTSIDE_JUNCTION", Encoding.UTF8);
        await File.WriteAllTextAsync(preciousFile, "PRECIOUS_JUNCTION_TARGET", Encoding.UTF8);

        // Plant a real NTFS Directory Junction inside saveDir pointing to externalSecretDir.
        string saveJunction = Path.Combine(saveDir, "linked_secret");
        bool junctionCreated = SnapshotTestSandbox.TryCreateDirectoryJunction(saveJunction, externalSecretDir);
        Assert.True(junctionCreated, "NTFS directory junction creation via mklink /J should succeed on Windows.");

        string gameId = SpawnId.NewUlid();
        await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId: null, installDir);

        // Configure retention count = 3 (supported presets: 3 / 5 / 10).
        await sandbox.Database.SaveTrainerSettingsAsync(new TrainerSettingsDto
        {
            PreTrainerSnapshotRetentionCount = 3,
        });

        var gameDataStub = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "rolling-save",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = true,
                IsSafeToClean = true,
            },
        ]);
        var clock = new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 15, 0, 0, TimeSpan.Zero));
        var service = new PreTrainerSaveSnapshotService(
            sandbox.Database,
            sandbox.Paths,
            gameDataStub,
            new StubSnapshotGameLaunchService(),
            clock);

        // Create snapshot #1 and verify ReparsePoint junction inside saveDir was strictly skipped!
        var snap1 = await service.CreateAsync(gameId, trainerId: null);
        Assert.True(snap1.Succeeded);
        Assert.Equal(1, snap1.Snapshot!.FileCount);
        string snap1Dir = sandbox.Paths.GetPreTrainerSnapshotDirectory(gameId, snap1.Snapshot.SnapshotId, createDirectory: false);
        Assert.Empty(Directory.EnumerateFiles(snap1Dir, "do_not_copy.txt", SearchOption.AllDirectories));

        // Plant an NTFS Directory Junction INSIDE snap1Dir pointing to externalPreciousDir to verify rolling pruning
        // deletes ONLY snap1Dir itself and NEVER follows ReparsePoints to delete external target files!
        string plantedJunctionInOldSnap = Path.Combine(snap1Dir, "trap_junction");
        Assert.True(SnapshotTestSandbox.TryCreateDirectoryJunction(plantedJunctionInOldSnap, externalPreciousDir));

        // Create snapshots #2 and #3 (total = 3 PreTrainer snapshots).
        clock.Advance(TimeSpan.FromMinutes(1));
        await File.WriteAllTextAsync(saveFile, "GEN_1", Encoding.UTF8);
        var snap2 = await service.CreateAsync(gameId, trainerId: null);
        Assert.True(snap2.Succeeded);

        clock.Advance(TimeSpan.FromMinutes(1));
        await File.WriteAllTextAsync(saveFile, "GEN_2", Encoding.UTF8);
        var snap3 = await service.CreateAsync(gameId, trainerId: null);
        Assert.True(snap3.Succeeded);

        // Trigger RestoreAsync on snap2 -> creates PreRestoreSafety #1 (must NOT count against PreTrainer quota of 3!).
        clock.Advance(TimeSpan.FromMinutes(1));
        var restore1 = await service.RestoreAsync(snap2.Snapshot!.SnapshotId);
        Assert.True(restore1.Succeeded);
        Assert.NotNull(restore1.SafetySnapshot);

        // Now create PreTrainer snapshot #4 -> snap1 (oldest PreTrainer) must be pruned, while PreRestoreSafety #1 stays!
        clock.Advance(TimeSpan.FromMinutes(1));
        await File.WriteAllTextAsync(saveFile, "GEN_3", Encoding.UTF8);
        var snap4 = await service.CreateAsync(gameId, trainerId: null);
        Assert.True(snap4.Succeeded);

        Assert.False(Directory.Exists(snap1Dir));
        Assert.Null(await sandbox.Database.GetPreTrainerSnapshotByIdAsync(snap1.Snapshot.SnapshotId));
        // Precious file behind the planted junction MUST remain completely untouched!
        Assert.True(File.Exists(preciousFile));
        Assert.Equal("PRECIOUS_JUNCTION_TARGET", await File.ReadAllTextAsync(preciousFile));

        var afterSnap4 = await service.ListAsync(gameId);
        Assert.Equal(3, afterSnap4.Count(s => s.TriggerKind == PreTrainerSnapshotTriggerKind.PreTrainer));
        Assert.Equal(1, afterSnap4.Count(s => s.TriggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety));

        // Trigger a 2nd restore -> PreRestoreSafety retains ONLY the newest 1 safety snapshot, never evicting PreTrainer snapshots!
        string firstSafetyId = restore1.SafetySnapshot!.SnapshotId;
        string firstSafetyDir = sandbox.Paths.GetPreTrainerSnapshotDirectory(gameId, firstSafetyId, createDirectory: false);
        clock.Advance(TimeSpan.FromMinutes(1));
        var restore2 = await service.RestoreAsync(snap3.Snapshot!.SnapshotId);
        Assert.True(restore2.Succeeded);
        Assert.NotNull(restore2.SafetySnapshot);
        Assert.NotEqual(firstSafetyId, restore2.SafetySnapshot!.SnapshotId);

        Assert.False(Directory.Exists(firstSafetyDir));
        Assert.Null(await sandbox.Database.GetPreTrainerSnapshotByIdAsync(firstSafetyId));

        var finalList = await service.ListAsync(gameId);
        Assert.Equal(3, finalList.Count(s => s.TriggerKind == PreTrainerSnapshotTriggerKind.PreTrainer));
        Assert.Equal(1, finalList.Count(s => s.TriggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety));
        Assert.Equal(restore2.SafetySnapshot.SnapshotId, finalList.Single(s => s.TriggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety).SnapshotId);
    }

    [Fact]
    public async Task Snapshots_RestoreGuards_RejectsRunningGameUnsafeTargetAndCorruptedPayloadBeforeTouchingLiveSaves()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "GuardedGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "GuardedGame");
        string savePath = Path.Combine(saveDir, "slot0.sav");
        await File.WriteAllTextAsync(savePath, "ORIGINAL_SAVE", Encoding.UTF8);

        string gameId = SpawnId.NewUlid();
        await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId: null, installDir);

        bool blockAllTargets = false;
        var gameDataStub = new StubSnapshotGameDataService(
            _ =>
            [
                new GameDataEntryDto
                {
                    EntryId = "guarded-save",
                    GameId = gameId,
                    Category = GameDataCategory.SaveData,
                    SourceRule = GameDataSourceRule.NativeRule,
                    PathOrRegistryKey = saveDir,
                    Exists = true,
                    IsSafeToClean = true,
                },
            ],
            customValidator: (_, path) =>
                blockAllTargets
                    ? new GameDataSafetyCheckResult(false, $"安全围栏模拟拦截：{path}")
                    : null);

        var launchStub = new StubSnapshotGameLaunchService();
        var clock = new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 16, 0, 0, TimeSpan.Zero));
        var service = new PreTrainerSaveSnapshotService(sandbox.Database, sandbox.Paths, gameDataStub, launchStub, clock);

        var snap = await service.CreateAsync(gameId, trainerId: null);
        Assert.True(snap.Succeeded);

        // Mutate live save after snapshot.
        await File.WriteAllTextAsync(savePath, "LIVE_MODIFIED_SAVE", Encoding.UTF8);

        // 1. Game currently running -> RestoreAsync MUST refuse without touching files or creating PreRestoreSafety.
        launchStub.SetRunning(new ActiveGameSession
        {
            SessionId = SpawnId.NewUlid(),
            GameId = gameId,
            GameTitle = "GuardedGame",
            InstallDir = installDir,
            AntiCheatTier = AntiCheatTier.Tier0,
            State = PlaySessionActivityState.Active,
            RootProcessId = Environment.ProcessId,
            StartedUtc = clock.GetUtcNow(),
        });

        var runningRestore = await service.RestoreAsync(snap.Snapshot!.SnapshotId);
        Assert.False(runningRestore.Succeeded);
        Assert.Null(runningRestore.SafetySnapshot);
        Assert.Equal("LIVE_MODIFIED_SAVE", await File.ReadAllTextAsync(savePath));
        Assert.Single(await service.ListAsync(gameId));

        launchStub.ClearRunning(gameId);

        // 2. Safety fence rejects target -> RestoreAsync MUST refuse BEFORE touching files or creating PreRestoreSafety.
        blockAllTargets = true;
        var blockedRestore = await service.RestoreAsync(snap.Snapshot.SnapshotId);
        Assert.False(blockedRestore.Succeeded);
        Assert.Null(blockedRestore.SafetySnapshot);
        Assert.Equal("LIVE_MODIFIED_SAVE", await File.ReadAllTextAsync(savePath));
        Assert.Single(await service.ListAsync(gameId));

        blockAllTargets = false;

        // 3. Tamper with stored snapshot payload -> staging SHA-256 verification fails, cleans up .spawn_stage_tmp,
        //    and leaves live save file completely intact!
        string manifestPath = Path.Combine(sandbox.Paths.PreTrainerSnapshotsDirectory, snap.Snapshot.ManifestRelativePath!);
        var manifest = await PreTrainerSnapshotManifestStore.ReadManifestAsync(manifestPath);
        string payloadFull = Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest!.Files[0].SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(payloadFull, "TAMPERED_BYTE_CONTENT_SAME_OR_DIFF", Encoding.UTF8);

        clock.Advance(TimeSpan.FromMinutes(1));
        var tamperedRestore = await service.RestoreAsync(snap.Snapshot.SnapshotId);
        Assert.False(tamperedRestore.Succeeded);
        Assert.NotNull(tamperedRestore.SafetySnapshot);
        Assert.Equal("LIVE_MODIFIED_SAVE", await File.ReadAllTextAsync(savePath));
        Assert.Empty(Directory.EnumerateFiles(saveDir, "*.spawn_stage_tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Snapshots_DisabledBySettings_SkipsCreateAsync_ButRestoreStillCreatesPreRestoreSafety()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "ToggleSettingGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "ToggleSettingGame");
        string savePath = Path.Combine(saveDir, "slot.sav");
        await File.WriteAllTextAsync(savePath, "BEFORE_DISABLE", Encoding.UTF8);

        string gameId = SpawnId.NewUlid();
        await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId: null, installDir);

        var gameDataStub = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "toggle-save",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = true,
                IsSafeToClean = true,
            },
        ]);
        var clock = new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 17, 0, 0, TimeSpan.Zero));
        var service = new PreTrainerSaveSnapshotService(sandbox.Database, sandbox.Paths, gameDataStub, new StubSnapshotGameLaunchService(), clock);

        // Create one snapshot while enabled.
        var initialSnap = await service.CreateAsync(gameId, trainerId: null);
        Assert.True(initialSnap.Succeeded);

        // Disable automatic pre-trainer snapshots in settings.
        await sandbox.Database.SaveTrainerSettingsAsync(new TrainerSettingsDto
        {
            CreatePreTrainerSnapshot = false,
        });

        clock.Advance(TimeSpan.FromMinutes(1));
        var disabledCreate = await service.CreateAsync(gameId, trainerId: null);
        Assert.False(disabledCreate.Succeeded);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, disabledCreate.Status);
        Assert.Equal(PreTrainerSnapshotSkipReason.DisabledBySettings, disabledCreate.SkipReason);

        // Mutate save and restore -> PreRestoreSafety MUST still be created even when CreatePreTrainerSnapshot == false!
        await File.WriteAllTextAsync(savePath, "MUTATED_AFTER_DISABLE", Encoding.UTF8);
        clock.Advance(TimeSpan.FromMinutes(1));
        var restoreRes = await service.RestoreAsync(initialSnap.Snapshot!.SnapshotId);
        Assert.True(restoreRes.Succeeded);
        Assert.NotNull(restoreRes.SafetySnapshot);
        Assert.Equal(PreTrainerSnapshotTriggerKind.PreRestoreSafety, restoreRes.SafetySnapshot!.TriggerKind);
        Assert.Equal("BEFORE_DISABLE", await File.ReadAllTextAsync(savePath));
    }

    [Fact]
    public async Task Snapshots_SharedReadWriteOpenHandleSucceeds_AndRestoreRecreatesCompletelyDeletedSaveDirectory()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "SharedAndDeletedGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "SharedAndDeletedGame");
        string savePath = Path.Combine(saveDir, "slot0.sav");
        byte[] originalBytes = "SHARED_HANDLE_ORIGINAL_PAYLOAD"u8.ToArray();
        await File.WriteAllBytesAsync(savePath, originalBytes);
        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(originalBytes));

        string gameId = SpawnId.NewUlid();
        await SeedGameAndTrainerAsync(sandbox.Database, gameId, trainerId: null, installDir);

        var gameDataStub = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "shared-save",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = Directory.Exists(saveDir),
                IsSafeToClean = true,
            },
        ]);
        var clock = new ManualSnapshotTimeProvider(new DateTimeOffset(2026, 11, 21, 18, 0, 0, TimeSpan.Zero));
        var service = new PreTrainerSaveSnapshotService(sandbox.Database, sandbox.Paths, gameDataStub, new StubSnapshotGameLaunchService(), clock);

        // 1. Open save file with non-exclusive ReadWrite handle -> snapshot MUST succeed via FileShare.ReadWrite | FileShare.Delete!
        PreTrainerSnapshotResult snapResult;
        await using (new FileStream(savePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            snapResult = await service.CreateAsync(gameId, trainerId: null);
        }

        Assert.True(snapResult.Succeeded, snapResult.Message);
        Assert.Equal(PreTrainerSnapshotStatus.Completed, snapResult.Status);

        // 2. Delete the ENTIRE save directory before restoring -> RestoreAsync must recreate directory and restore exact SHA-256!
        Directory.Delete(saveDir, recursive: true);
        Assert.False(Directory.Exists(saveDir));

        clock.Advance(TimeSpan.FromMinutes(2));
        var restoreRes = await service.RestoreAsync(snapResult.Snapshot!.SnapshotId);

        Assert.True(restoreRes.Succeeded, restoreRes.Message);
        Assert.Equal(1, restoreRes.RestoredFileCount);
        Assert.NotEmpty(restoreRes.Warnings);
        Assert.True(File.Exists(savePath));
        Assert.Equal(expectedSha, await PreTrainerSnapshotManifestStore.ComputeFileSha256HexAsync(savePath));
    }

    private static async Task SeedGameAndTrainerAsync(
        SpawnDatabase db,
        string gameId,
        string? trainerId,
        string installDir)
    {
        await db.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = Path.GetFileName(installDir),
            InstallDir = installDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.Standalone,
            AntiCheatTier = AntiCheatTier.Tier0,
        });

        if (!string.IsNullOrWhiteSpace(trainerId))
        {
            await db.UpsertTrainerAsync(new TrainerCatalogRecord
            {
                TrainerId = trainerId,
                GameId = gameId,
                Provider = TrainerProviderKind.LocalImport,
                DisplayName = "Synthetic Trainer",
                SandboxRelativePath = $"{gameId}\\{trainerId}\\trainer.exe",
                FileSha256 = new string('a', 64),
            });
        }
    }
}
