using System.Diagnostics;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// Offline unit tests covering:
/// 1. <see cref="GameDataSafetyFence.ValidateTrustedSaveTarget"/> false-positive fixes (Black Myth: Wukong <c>b1</c>,
///    Slay the Spire <c>preferences</c>/<c>runs</c>, Subnautica <c>SNAppData</c>, Monster Hunter: World) while strictly
///    holding hard safety redlines (drive roots, Windows system roots, executables/scripts, SPAWN data root, NTFS junctions).
/// 2. <see cref="SpawnDatabase"/> Save Time Machine schema extensions (real <c>save_synced_snapshots</c>,
///    <c>save_custom_locations</c>, <c>cloud_game_key</c>, <c>sync_all_branches</c>, middle-node snapshot relinking,
///    atomic default endpoint switching, and cloud auto-sync settings).
/// </summary>
public sealed class SaveCloudKeyFenceAndSchemaEnhancementTests
{
    [Fact]
    public void GameDataSafetyFence_ValidateTrustedSaveTarget_AllowsKnownFalsePositiveGamesAndCustomPaths()
    {
        using var sandbox = new TestSandbox();
        var options = new GameDataManagementOptions();

        string wukongInstall = Path.Combine(sandbox.RootDirectory, "Games", "BlackMythWukong");
        string wukongSaveDir = Path.Combine(wukongInstall, "b1", "Saved", "SaveGames");
        Directory.CreateDirectory(wukongSaveDir);

        (bool wukongOk, string? wukongReason) = GameDataSafetyFence.ValidateTrustedSaveTarget(
            wukongSaveDir,
            wukongInstall,
            sandbox.Paths,
            options,
            isUserCustomPath: false);
        Assert.True(wukongOk, wukongReason);

        string stsInstall = Path.Combine(sandbox.RootDirectory, "Games", "SlayTheSpire");
        string stsPrefs = Path.Combine(stsInstall, "preferences");
        string stsRuns = Path.Combine(stsInstall, "runs");
        Directory.CreateDirectory(stsPrefs);
        Directory.CreateDirectory(stsRuns);

        Assert.True(GameDataSafetyFence.ValidateTrustedSaveTarget(stsPrefs, stsInstall, sandbox.Paths, options, isUserCustomPath: false).IsSafe);
        Assert.True(GameDataSafetyFence.ValidateTrustedSaveTarget(stsRuns, stsInstall, sandbox.Paths, options, isUserCustomPath: false).IsSafe);

        string subnauticaInstall = Path.Combine(sandbox.RootDirectory, "Games", "Subnautica");
        string subnauticaSaves = Path.Combine(subnauticaInstall, "SNAppData", "SavedGames");
        Directory.CreateDirectory(subnauticaSaves);
        Assert.True(GameDataSafetyFence.ValidateTrustedSaveTarget(subnauticaSaves, subnauticaInstall, sandbox.Paths, options, isUserCustomPath: false).IsSafe);

        string mhwRemote = Path.Combine(sandbox.RootDirectory, "Steam", "userdata", "12345678", "582010", "remote");
        Directory.CreateDirectory(mhwRemote);
        Assert.True(GameDataSafetyFence.ValidateTrustedSaveTarget(mhwRemote, subnauticaInstall, sandbox.Paths, options, isUserCustomPath: false).IsSafe);

        // User explicit custom single save file
        string customFile = Path.Combine(sandbox.RootDirectory, "CustomSaves", "slot0.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(customFile)!);
        File.WriteAllText(customFile, "SAVE-BYTES");
        Assert.True(GameDataSafetyFence.ValidateTrustedSaveTarget(customFile, stsInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
    }

    [Fact]
    public void GameDataSafetyFence_ValidateTrustedSaveTarget_StrictlyBlocksHardRedlinesAndNtfsJunction()
    {
        using var sandbox = new TestSandbox();
        var options = new GameDataManagementOptions();
        string dataRoot = sandbox.Paths.DataRoot;
        string gameInstall = Path.Combine(sandbox.RootDirectory, "Games", "MyGame");
        Directory.CreateDirectory(gameInstall);

        // 1. Drive root & Windows system roots
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(@"C:\", gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(Environment.GetFolderPath(Environment.SpecialFolder.Windows), gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"), gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);

        // 2. SPAWN own data root & descendants
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(dataRoot, gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(Path.Combine(dataRoot, "cas"), gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);

        // 3. Game install root itself & engine binary/data directories
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(gameInstall, gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(Path.Combine(gameInstall, "Binaries", "Win64"), gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(Path.Combine(gameInstall, "MyGame_Data"), gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);

        // 4. Executable / script extensions
        foreach (string ext in new[] { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".vbs", ".msi", ".com", ".scr", ".reg", ".sh" })
        {
            string badFile = Path.Combine(sandbox.RootDirectory, "CustomSaves", "payload" + ext);
            Assert.False(
                GameDataSafetyFence.ValidateTrustedSaveTarget(badFile, gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe,
                $"Expected extension '{ext}' to be blocked.");
        }

        // 5. Real NTFS Directory Junction (created silently via mklink /J without elevation)
        string realTarget = Path.Combine(sandbox.RootDirectory, "RealSaveTarget");
        Directory.CreateDirectory(realTarget);
        string junctionPath = Path.Combine(sandbox.RootDirectory, "SaveJunction");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{realTarget}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            Assert.NotNull(proc);
            proc!.WaitForExit(5000);
            Assert.Equal(0, proc.ExitCode);

            Assert.True(GameDataSafetyFence.HasDisallowedReparsePointOnPath(junctionPath, out string? offending));
            Assert.Equal(junctionPath, offending, ignoreCase: true);

            // Both the junction itself AND a descendant path inside the junction must be blocked!
            Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(junctionPath, gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
            Assert.False(GameDataSafetyFence.ValidateTrustedSaveTarget(Path.Combine(junctionPath, "slot0"), gameInstall, sandbox.Paths, options, isUserCustomPath: true).IsSafe);
        }
        finally
        {
            // Non-recursive removal of NTFS Junction per anti-data-loss redline!
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: false);
            }
        }
    }

    [Fact]
    public async Task SpawnDatabase_SaveEnhancements_SchemaV5_SyncedSnapshots_CustomLocations_RelinkAndEndpoints()
    {
        using var sandbox = new TestSandbox();
        SpawnDatabase db = sandbox.Database;

        // 1. Schema version stays 5 while new tables and columns are idempotently present
        Assert.Equal(5, await db.GetSchemaVersionAsync());

        string gameId = SpawnId.NewUlid();
        await db.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "Black Myth: Wukong",
            SteamAppId = 2358720,
            PlatformSource = PlatformSource.SteamLocal,
            InstallDir = Path.Combine(sandbox.RootDirectory, "Games", "Wukong"),
            ExecutableRelPath = "b1.exe",
        });

        SaveBranchRecord mainBranch = await db.EnsureDefaultSaveBranchAsync(gameId);

        // 2. CloudGameKey & SyncAllBranchesToCloud persistence
        await db.GetOrCreateSaveGameStateAsync(gameId);
        await db.UpdateSaveGameCloudKeyAsync(gameId, "steam:2358720");
        SaveGameStateRecord state = await db.GetOrCreateSaveGameStateAsync(gameId);
        Assert.Equal("steam:2358720", state.CloudGameKey);
        Assert.False(state.SyncAllBranchesToCloud);

        await db.UpsertSaveGameStateAsync(state with { SyncAllBranchesToCloud = true });
        SaveGameStateRecord? reloadedState = await db.GetSaveGameStateAsync(gameId);
        Assert.True(reloadedState!.SyncAllBranchesToCloud);

        // 3. Real per-snapshot cloud sync ledger (save_synced_snapshots)
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        var s1 = MakeSnapshot(gameId, mainBranch.BranchId, parentId: null, t0);
        var s2 = MakeSnapshot(gameId, mainBranch.BranchId, parentId: s1.SnapshotId, t0.AddMinutes(1));
        var s3 = MakeSnapshot(gameId, mainBranch.BranchId, parentId: s2.SnapshotId, t0.AddMinutes(2));

        await db.CreateSaveSnapshotWithFilesAsync(s1, [], advanceBranchHead: true, updateGameActiveHead: true);
        await db.CreateSaveSnapshotWithFilesAsync(s2, [], advanceBranchHead: true, updateGameActiveHead: true);
        await db.CreateSaveSnapshotWithFilesAsync(s3, [], advanceBranchHead: true, updateGameActiveHead: true);

        await db.MarkSnapshotsCloudSyncedAsync(gameId, "ep-1", [s1.SnapshotId, s2.SnapshotId], t0.AddMinutes(3));
        HashSet<string> syncedIds = await db.GetCloudSyncedSnapshotIdsForGameAsync(gameId);
        Assert.Contains(s1.SnapshotId, syncedIds);
        Assert.Contains(s2.SnapshotId, syncedIds);
        Assert.DoesNotContain(s3.SnapshotId, syncedIds);

        // 4. Linear middle-node deletion relinks S3.ParentSnapshotId -> S1.SnapshotId
        (bool deleted, _) = await db.DeleteSaveSnapshotAndRelinkChildrenAsync(s2.SnapshotId, s1.SnapshotId);
        Assert.True(deleted);
        Assert.Null(await db.GetSaveSnapshotByIdAsync(s2.SnapshotId));
        SaveSnapshotRecord? s3After = await db.GetSaveSnapshotByIdAsync(s3.SnapshotId);
        Assert.NotNull(s3After);
        Assert.Equal(s1.SnapshotId, s3After!.ParentSnapshotId);

        // 5. Custom save locations CRUD
        var customLoc = new SaveCustomLocationRecord
        {
            LocationId = SpawnId.NewUlid(),
            GameId = gameId,
            LocationKind = SaveCustomLocationKind.Directory,
            PathOrRegistryKey = Path.Combine(sandbox.RootDirectory, "Games", "Wukong", "b1", "Saved", "SaveGames"),
            Category = SaveFileCategory.Save,
            Label = "黑神话存档",
            CreatedAtUtc = t0,
        };
        await db.UpsertSaveCustomLocationAsync(customLoc);
        IReadOnlyList<SaveCustomLocationRecord> loadedLocs = await db.GetSaveCustomLocationsByGameAsync(gameId);
        Assert.Single(loadedLocs);
        Assert.Equal("黑神话存档", loadedLocs[0].Label);
        Assert.True(await db.DeleteSaveCustomLocationAsync(customLoc.LocationId));
        Assert.Empty(await db.GetSaveCustomLocationsByGameAsync(gameId));

        // 6. Atomic SetDefaultCloudSyncEndpointAsync & SaveSettingsDto policy persistence
        string ep1Id = SpawnId.NewUlid();
        string ep2Id = SpawnId.NewUlid();
        await db.UpsertCloudSyncEndpointAsync(new CloudSyncEndpointRecord
        {
            EndpointId = ep1Id,
            DisplayName = "EP-1",
            ProviderType = CloudSyncProviderType.LocalFolder,
            EndpointUrl = Path.Combine(sandbox.RootDirectory, "ep1"),
            IsDefault = true,
        });
        await db.UpsertCloudSyncEndpointAsync(new CloudSyncEndpointRecord
        {
            EndpointId = ep2Id,
            DisplayName = "EP-2",
            ProviderType = CloudSyncProviderType.S3,
            EndpointUrl = "https://s3.example.test",
            BucketOrRoot = "spawn-saves",
            PathStyleAddressing = true,
            IsDefault = false,
        });

        Assert.True(await db.SetDefaultCloudSyncEndpointAsync(ep2Id));
        IReadOnlyList<CloudSyncEndpointRecord> endpoints = await db.GetAllCloudSyncEndpointsAsync();
        Assert.False(endpoints.Single(e => e.EndpointId == ep1Id).IsDefault);
        Assert.True(endpoints.Single(e => e.EndpointId == ep2Id).IsDefault);
        Assert.True(endpoints.Single(e => e.EndpointId == ep2Id).PathStyleAddressing);

        await db.SaveSaveSettingsAsync(new SaveSettingsDto
        {
            MachineId = "pc-test-01",
            MachineDisplayName = "LivingRoom-PC",
            AutoCloudPushOnGameExit = true,
            CheckRemoteUpdatesBeforeLaunch = true,
        });
        SaveSettingsDto loadedSettings = await db.GetSaveSettingsAsync();
        Assert.Equal("LivingRoom-PC", loadedSettings.MachineDisplayName);
        Assert.True(loadedSettings.AutoCloudPushOnGameExit);
        Assert.True(loadedSettings.CheckRemoteUpdatesBeforeLaunch);
    }

    private static SaveSnapshotRecord MakeSnapshot(string gameId, string branchId, string? parentId, DateTimeOffset ts) =>
        new()
        {
            SnapshotId = SpawnId.NewUlid(ts),
            GameId = gameId,
            BranchId = branchId,
            ParentSnapshotId = parentId,
            ModProfileHash = string.Empty,
            TriggerKind = SaveSnapshotTriggerKind.Manual,
            FileCount = 1,
            TotalRawBytes = 128,
            TotalStoredBytes = 64,
            CreatedMachineId = "pc-test-01",
            CreatedAtUtc = ts,
        };
}
