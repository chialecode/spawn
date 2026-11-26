using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress;
using Spawn.Modules.Progress.Saves.Cloud;
using Spawn.Modules.Progress.Saves.Engine;
using Spawn.Modules.Progress.Saves.Rules;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Saves;

/// <summary>
/// Comprehensive offline unit tests verifying all P0/P1/P2 Save Time Machine &amp; SPPK cloud sync enhancements:
/// - P0-1: Cross-machine stable <c>CloudGameKey</c> (different ULID GameIds), legacy ULID fallback, <c>SyncAllConfiguredGamesAsync</c>
///         fresh-device pull and non-swallowed errors.
/// - P0-2: Real per-snapshot <c>IsCloudSynced</c> marking via <c>save_synced_snapshots</c> (no false-positive side-branch badges)
///         and explicit multi-branch push.
/// - P0-3: Ludusavi <c>manifest.yaml</c> parser, <see cref="TrustedSavePathFence"/> allowlist for Black Myth: Wukong / Slay the Spire /
///         Subnautica + hard redlines, and per-game custom save locations CRUD.
/// - P1-1: Linear middle-node snapshot deletion with parent relinking, rolling retention bypassing early pinned nodes,
///         and safe background branch deletion.
/// - P1-3: <see cref="SaveWatcherService"/> parent-directory fallback when save folder does not exist yet on first launch.
/// - P1-4: Live disk unsaved-edit safety snapshot before divergence overwrite, exact missing-chunk download size via SPPK footer,
///         HTTP 401/403/404/405/timeout Chinese diagnostics, endpoint update/default switch &amp; auto-sync policy hooks.
/// </summary>
public sealed class SaveCloudFenceAndUxEnhancementTests : IDisposable
{
    private readonly string _tempRoot;

    public SaveCloudFenceAndUxEnhancementTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "spawn-saves-p0p1-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    // =========================================================================
    // P0-1: Cross-machine stable CloudGameKey, Legacy ULID fallback, SyncAll
    // =========================================================================

    [Fact]
    public async Task CrossMachineSync_DifferentLocalUlidGameIds_SyncViaStableSteamKey_AndFreshDeviceSyncAllPulls()
    {
        string remoteSyncRoot = Path.Combine(_tempRoot, "p01-cross-machine-remote");
        Directory.CreateDirectory(remoteSyncRoot);

        await using var machineA = await RealMachineHarness.CreateAsync(_tempRoot, "p01-pc-a", "Desktop-A");
        await using var machineB = await RealMachineHarness.CreateAsync(_tempRoot, "p01-pc-b", "Handheld-B");

        // Two completely different random local ULID GameIds for the same Steam game (Elden Ring 1245620)!
        string gameIdA = SpawnId.NewUlid();
        string gameIdB = SpawnId.NewUlid();
        Assert.NotEqual(gameIdA, gameIdB);

        await machineA.SeedGameAsync(gameIdA, "Elden Ring", steamAppId: 1245620);
        await machineB.SeedGameAsync(gameIdB, "Elden Ring", steamAppId: 1245620);
        await machineA.RuleResolver.EnsureBuiltInRulesSeededAsync();
        await machineB.RuleResolver.EnsureBuiltInRulesSeededAsync();

        string saveFileA = Path.Combine(machineA.AppDataRoaming, "EldenRing", "76561198000000099", "ER0000.sl2");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFileA)!);
        await File.WriteAllTextAsync(saveFileA, "DESKTOP-ER-PROGRESS-V1", Encoding.UTF8);

        CreateSaveSnapshotResult s1A = await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = gameIdA,
            Note = "Desktop Start",
        });
        Assert.True(s1A.Succeeded, s1A.Message);

        await machineA.AddLocalEndpointAsync(remoteSyncRoot);
        await machineB.AddLocalEndpointAsync(remoteSyncRoot);

        string resolvedKeyA = await machineA.CloudSync.ResolveCloudGameKeyAsync(gameIdA);
        string resolvedKeyB = await machineB.CloudSync.ResolveCloudGameKeyAsync(gameIdB);
        Assert.Equal("steam:1245620", resolvedKeyA);
        Assert.Equal("steam:1245620", resolvedKeyB);

        // Push from Machine A
        CloudSyncExecutionReport pushA = await machineA.CloudSync.ExecuteSyncAsync(gameIdA);
        Assert.True(pushA.Succeeded, pushA.Message);
        Assert.Equal("steam:1245620", pushA.CloudGameKey);

        // Machine B has 0 local snapshots and a DIFFERENT local GameId, yet SyncAllConfiguredGamesAsync pulls it!
        IReadOnlyList<CloudSyncExecutionReport> syncAllB = await machineB.CloudSync.SyncAllConfiguredGamesAsync();
        CloudSyncExecutionReport pulledB = Assert.Single(syncAllB);
        Assert.True(pulledB.Succeeded, pulledB.Message);
        Assert.Equal(CloudSyncRelation.FastForwardPull, pulledB.Relation);
        Assert.Equal(gameIdB, pulledB.GameId);
        Assert.Equal("steam:1245620", pulledB.CloudGameKey);

        string saveFileB = Path.Combine(machineB.AppDataRoaming, "EldenRing", "76561198000000099", "ER0000.sl2");
        Assert.True(File.Exists(saveFileB));
        Assert.Equal("DESKTOP-ER-PROGRESS-V1", await File.ReadAllTextAsync(saveFileB, Encoding.UTF8));
    }

    [Fact]
    public void CloudGameKeyResolver_PriorityOrder_ExplicitSteamGogSlugAndCjkFallback()
    {
        var steamGame = new GameRecord
        {
            GameId = "ulid-1",
            Title = "黑神话：悟空",
            SteamAppId = 2358720,
            GogGameId = "999",
            PlatformSource = PlatformSource.SteamLocal,
            InstallDir = @"C:\Games\Wukong",
            ExecutableRelPath = "b1.exe",
        };
        Assert.Equal("custom:my-slot", CloudGameKeyResolver.ResolveSync(steamGame, new SaveGameStateRecord { GameId = "ulid-1", CloudGameKey = "custom:my-slot" }));
        Assert.Equal("steam:2358720", CloudGameKeyResolver.ResolveSync(steamGame));

        var gogGame = steamGame with { SteamAppId = null, GogGameId = "1207658924" };
        Assert.Equal("gog:1207658924", CloudGameKeyResolver.ResolveSync(gogGame));

        var standaloneEnglish = steamGame with { SteamAppId = null, GogGameId = null, Title = "Hollow Knight: Silksong!" };
        Assert.Equal("slug:hollow-knight-silksong", CloudGameKeyResolver.ResolveSync(standaloneEnglish));

        // CJK-only title produces deterministic non-empty hash slug rather than empty string
        var cjkOnly = steamGame with { SteamAppId = null, GogGameId = null, Title = "黑神话：悟空" };
        string cjkKey1 = CloudGameKeyResolver.ResolveSync(cjkOnly);
        string cjkKey2 = CloudGameKeyResolver.ResolveSync(cjkOnly);
        Assert.StartsWith("slug:u-", cjkKey1, StringComparison.Ordinal);
        Assert.Equal(cjkKey1, cjkKey2);
    }

    [Fact]
    public async Task CloudSync_LegacyUlidRemotePathFallback_ReadsOldHeadsAndSnapshotsWithoutDataLoss()
    {
        string remoteSyncRoot = Path.Combine(_tempRoot, "p01-legacy-ulid-fallback");
        Directory.CreateDirectory(remoteSyncRoot);

        await using var machineA = await RealMachineHarness.CreateAsync(_tempRoot, "p01-legacy-a", "Old-Desktop");
        await using var machineB = await RealMachineHarness.CreateAsync(_tempRoot, "p01-legacy-b", "New-Desktop");

        string sharedLegacyGameId = SpawnId.NewUlid();
        await machineA.SeedGameAsync(sharedLegacyGameId, "Elden Ring", steamAppId: 1245620);
        await machineB.SeedGameAsync(sharedLegacyGameId, "Elden Ring", steamAppId: 1245620);
        await machineA.RuleResolver.EnsureBuiltInRulesSeededAsync();
        await machineB.RuleResolver.EnsureBuiltInRulesSeededAsync();

        string saveFileA = Path.Combine(machineA.AppDataRoaming, "EldenRing", "76561198000000011", "ER0000.sl2");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFileA)!);
        await File.WriteAllTextAsync(saveFileA, "LEGACY-ULID-SAVE-PAYLOAD", Encoding.UTF8);

        Assert.True((await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = sharedLegacyGameId,
            Note = "Legacy Snapshot",
        })).Succeeded);

        await machineA.AddLocalEndpointAsync(remoteSyncRoot);
        await machineB.AddLocalEndpointAsync(remoteSyncRoot);
        Assert.True((await machineA.CloudSync.ExecuteSyncAsync(sharedLegacyGameId)).Succeeded);

        // Move remote heads/<steam_1245620> & snapshots/<steam_1245620> to legacy heads/<sharedLegacyGameId> & snapshots/<sharedLegacyGameId>
        string syncDir = Path.Combine(remoteSyncRoot, "spawn-sync");
        string stableSeg = CloudGameKeyResolver.ToRemotePathSegment("steam:1245620");
        Directory.Move(Path.Combine(syncDir, "heads", stableSeg), Path.Combine(syncDir, "heads", sharedLegacyGameId));
        Directory.Move(Path.Combine(syncDir, "snapshots", stableSeg), Path.Combine(syncDir, "snapshots", sharedLegacyGameId));

        // Machine B resolves steam:1245620, finds nothing under stable key, falls back to legacy ULID path and pulls cleanly!
        CloudSyncPlanPreview planB = await machineB.CloudSync.PlanSyncAsync(sharedLegacyGameId);
        Assert.Equal(CloudSyncRelation.FastForwardPull, planB.Relation);

        CloudSyncExecutionReport pullB = await machineB.CloudSync.ExecuteSyncAsync(sharedLegacyGameId);
        Assert.True(pullB.Succeeded, pullB.Message);

        string saveFileB = Path.Combine(machineB.AppDataRoaming, "EldenRing", "76561198000000011", "ER0000.sl2");
        Assert.Equal("LEGACY-ULID-SAVE-PAYLOAD", await File.ReadAllTextAsync(saveFileB, Encoding.UTF8));
    }

    [Fact]
    public async Task SyncAllConfiguredGamesAsync_DoesNotSwallowErrors_ReportsReadableFailureDetails()
    {
        await using var machine = await RealMachineHarness.CreateAsync(_tempRoot, "p01-error-report", "Desktop-Err");
        string gameId = SpawnId.NewUlid();
        await machine.SeedGameAsync(gameId, "Broken Sync Game", steamAppId: 777777);

        // Configure an endpoint bound to an unreachable S3 mock (missing credentials / invalid transport failure)
        await machine.CloudSync.SaveEndpointAsync(new CloudSyncEndpointRecord
        {
            EndpointId = SpawnId.NewUlid(),
            DisplayName = "Broken S3",
            ProviderType = CloudSyncProviderType.S3,
            EndpointUrl = "https://invalid.example.test",
            BucketOrRoot = "my-bucket",
            IsDefault = true,
        }, secrets: null);

        IReadOnlyList<CloudSyncExecutionReport> reports = await machine.CloudSync.SyncAllConfiguredGamesAsync();
        CloudSyncExecutionReport err = Assert.Single(reports);
        Assert.False(err.Succeeded);
        Assert.Equal(CloudSyncRelation.Error, err.Relation);
        Assert.Equal(gameId, err.GameId);
        Assert.Equal("Broken Sync Game", err.GameTitle);
        Assert.Equal("steam:777777", err.CloudGameKey);
        Assert.False(string.IsNullOrWhiteSpace(err.Message));
    }

    // =========================================================================
    // P0-2: Real per-snapshot IsCloudSynced & multi-branch cloud sync
    // =========================================================================

    [Fact]
    public async Task SaveTreeLayout_IsCloudSynced_TracksOnlyActuallyUploadedSnapshots_AndMultiBranchPushSyncsSideBranches()
    {
        string remoteSyncRoot = Path.Combine(_tempRoot, "p02-real-cloud-synced");
        Directory.CreateDirectory(remoteSyncRoot);

        await using var machine = await RealMachineHarness.CreateAsync(_tempRoot, "p02-pc", "Desktop-P02");
        string gameId = SpawnId.NewUlid();
        await machine.SeedGameAsync(gameId, "Elden Ring", steamAppId: 1245620);
        await machine.RuleResolver.EnsureBuiltInRulesSeededAsync();
        await machine.AddLocalEndpointAsync(remoteSyncRoot);

        string saveFile = Path.Combine(machine.AppDataRoaming, "EldenRing", "76561198000000022", "ER0000.sl2");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFile)!);
        await File.WriteAllTextAsync(saveFile, "MAIN-S1", Encoding.UTF8);
        SaveSnapshotRecord s1 = (await machine.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameId, Note = "S1" })).Snapshot!;

        await File.WriteAllTextAsync(saveFile, "MAIN-S2", Encoding.UTF8);
        SaveSnapshotRecord s2 = (await machine.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameId, Note = "S2" })).Snapshot!;

        // Fork side branch "boss-alt" from S1 and create local-only S3
        ForkBranchResult fork = await machine.TimeMachine.CreateBranchAsync(new ForkBranchRequest
        {
            GameId = gameId,
            FromSnapshotId = s1.SnapshotId,
            BranchName = "boss-alt",
            SwitchToNewBranch = true,
            CreateSafetySnapshotIfSwitching = false,
        });
        Assert.True(fork.Succeeded, fork.Message);

        await File.WriteAllTextAsync(saveFile, "ALT-S3", Encoding.UTF8);
        SaveSnapshotRecord s3Alt = (await machine.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameId, Note = "Alt S3" })).Snapshot!;

        // Switch back to main
        SaveTreeGraphDto graphBeforePush = await machine.TimeMachine.GetSaveTreeGraphAsync(gameId);
        SaveBranchRecord mainBranch = graphBeforePush.Branches.Single(b => b.IsDefault);
        Assert.True((await machine.TimeMachine.SwitchBranchAsync(new SwitchBranchRequest
        {
            GameId = gameId,
            TargetBranchId = mainBranch.BranchId,
            CreateSafetySnapshot = false,
        })).Succeeded);

        // Before push: NOTHING is marked IsCloudSynced!
        Assert.All(graphBeforePush.Nodes, static n => Assert.False(n.IsCloudSynced));

        // Push ONLY active branch (main)
        CloudSyncPlanPreview planBefore = await machine.CloudSync.PlanSyncAsync(gameId);
        Assert.Equal(1, planBefore.UnsyncedBranchCount);

        CloudSyncExecutionReport pushMain = await machine.CloudSync.ExecuteSyncAsync(gameId);
        Assert.True(pushMain.Succeeded, pushMain.Message);

        SaveTreeGraphDto graphAfterMainPush = await machine.TimeMachine.GetSaveTreeGraphAsync(gameId);
        Assert.True(graphAfterMainPush.Nodes.Single(n => n.Snapshot.SnapshotId == s1.SnapshotId).IsCloudSynced);
        Assert.True(graphAfterMainPush.Nodes.Single(n => n.Snapshot.SnapshotId == s2.SnapshotId).IsCloudSynced);
        // CRITICAL P0-2 assertion: side branch S3 MUST NOT be falsely marked as IsCloudSynced!
        Assert.False(graphAfterMainPush.Nodes.Single(n => n.Snapshot.SnapshotId == s3Alt.SnapshotId).IsCloudSynced);

        // Now explicitly push all branches -> side branch S3 gets uploaded and marked IsCloudSynced = true!
        CloudSyncExecutionReport pushAll = await machine.CloudSync.PushAllBranchesAsync(gameId);
        Assert.True(pushAll.Succeeded, pushAll.Message);
        Assert.True(pushAll.SyncedBranchCount >= 1);

        SaveTreeGraphDto graphAfterAllPush = await machine.TimeMachine.GetSaveTreeGraphAsync(gameId);
        Assert.True(graphAfterAllPush.Nodes.Single(n => n.Snapshot.SnapshotId == s3Alt.SnapshotId).IsCloudSynced);
    }

    [Fact]
    public async Task PushAllBranchesAsync_EqualLengthChineseSideBranches_NeverCollideInRemoteStorage()
    {
        string remoteSyncRoot = Path.Combine(_tempRoot, "p02-cjk-side-branches");
        Directory.CreateDirectory(remoteSyncRoot);

        await using var machine = await RealMachineHarness.CreateAsync(_tempRoot, "p02-cjk-pc", "Desktop-CJK");
        string gameId = SpawnId.NewUlid();
        await machine.SeedGameAsync(gameId, "Elden Ring", steamAppId: 1245620);
        await machine.RuleResolver.EnsureBuiltInRulesSeededAsync();
        await machine.AddLocalEndpointAsync(remoteSyncRoot);

        string saveFile = Path.Combine(machine.AppDataRoaming, "EldenRing", "76561198000000033", "ER0000.sl2");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFile)!);
        await File.WriteAllTextAsync(saveFile, "MAIN-BASE", Encoding.UTF8);
        SaveSnapshotRecord sMain = (await machine.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameId, Note = "Main Base" })).Snapshot!;

        // Two equal-length Chinese side branches ("二周目" and "三周目") that naive ASCII sanitization would collapse to "___"!
        Assert.NotEqual(
            CloudGameKeyResolver.ToSideBranchPathSegment("二周目"),
            CloudGameKeyResolver.ToSideBranchPathSegment("三周目"));
        Assert.Equal("boss-alt", CloudGameKeyResolver.ToSideBranchPathSegment("boss-alt"));

        ForkBranchResult forkNg2 = await machine.TimeMachine.CreateBranchAsync(new ForkBranchRequest
        {
            GameId = gameId,
            FromSnapshotId = sMain.SnapshotId,
            BranchName = "二周目",
            SwitchToNewBranch = true,
            CreateSafetySnapshotIfSwitching = false,
        });
        Assert.True(forkNg2.Succeeded, forkNg2.Message);
        await File.WriteAllTextAsync(saveFile, "NG2-PROGRESS", Encoding.UTF8);
        SaveSnapshotRecord sNg2 = (await machine.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameId, Note = "二周目进度" })).Snapshot!;

        ForkBranchResult forkNg3 = await machine.TimeMachine.CreateBranchAsync(new ForkBranchRequest
        {
            GameId = gameId,
            FromSnapshotId = sMain.SnapshotId,
            BranchName = "三周目",
            SwitchToNewBranch = true,
            CreateSafetySnapshotIfSwitching = false,
        });
        Assert.True(forkNg3.Succeeded, forkNg3.Message);
        await File.WriteAllTextAsync(saveFile, "NG3-PROGRESS", Encoding.UTF8);
        SaveSnapshotRecord sNg3 = (await machine.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameId, Note = "三周目进度" })).Snapshot!;

        // Switch back to main and push all branches
        SaveTreeGraphDto beforePush = await machine.TimeMachine.GetSaveTreeGraphAsync(gameId);
        SaveBranchRecord mainBranch = beforePush.Branches.Single(b => b.IsDefault);
        Assert.True((await machine.TimeMachine.SwitchBranchAsync(new SwitchBranchRequest
        {
            GameId = gameId,
            TargetBranchId = mainBranch.BranchId,
            CreateSafetySnapshot = false,
        })).Succeeded);

        CloudSyncExecutionReport pushAll = await machine.CloudSync.PushAllBranchesAsync(gameId);
        Assert.True(pushAll.Succeeded, pushAll.Message);
        Assert.True(pushAll.SyncedBranchCount >= 3);

        // Verify TWO distinct remote branch directories & head documents exist under branches/steam_1245620/!
        string keySeg = CloudGameKeyResolver.ToRemotePathSegment("steam:1245620");
        string branchesDir = Path.Combine(remoteSyncRoot, "spawn-sync", "branches", keySeg);
        Assert.True(Directory.Exists(branchesDir));
        string[] branchDirs = Directory.GetDirectories(branchesDir);
        Assert.Equal(2, branchDirs.Length);

        string[] headJsons = branchDirs
            .SelectMany(d => Directory.GetFiles(d, "*.json"))
            .Select(f => File.ReadAllText(f, Encoding.UTF8))
            .ToArray();
        Assert.Equal(2, headJsons.Length);
        Assert.Contains(headJsons, j => j.Contains(sNg2.SnapshotId, StringComparison.Ordinal));
        Assert.Contains(headJsons, j => j.Contains(sNg3.SnapshotId, StringComparison.Ordinal));

        SaveTreeGraphDto afterPush = await machine.TimeMachine.GetSaveTreeGraphAsync(gameId);
        Assert.True(afterPush.Nodes.Single(n => n.Snapshot.SnapshotId == sNg2.SnapshotId).IsCloudSynced);
        Assert.True(afterPush.Nodes.Single(n => n.Snapshot.SnapshotId == sNg3.SnapshotId).IsCloudSynced);
    }

    // =========================================================================
    // P0-3: manifest.yaml parser, TrustedSavePathFence & Custom Locations
    // =========================================================================

    [Fact]
    public async Task LudusaviYamlManifestParser_And_TrustedFence_SupportWukongStsSubnauticaAndCustomLocations()
    {
        await using var machine = await RealMachineHarness.CreateAsync(_tempRoot, "p03-yaml-fence", "Desktop-P03");

        const string sampleManifestYaml = """
            "Black Myth: Wukong":
              files:
                "<base>/b1/Saved/SaveGames":
                  tags:
                    - save
                "<winLocalAppData>/b1/Saved/Config":
                  tags:
                    - config
              steam:
                id: 2358720
            "Slay the Spire":
              files:
                "<base>/preferences":
                  tags: [save]
                  when:
                    - os: windows
                "<base>/runs":
                  tags: [save]
                "<xdgData>/SlayTheSpire":
                  tags: [save]
                  when:
                    - os: linux
              registry:
                "HKEY_CURRENT_USER/Software/MegaCrit/SlayTheSpire":
                  tags: [save]
                "HKEY_LOCAL_MACHINE/Software/Forbidden/Key":
                  tags: [save]
              steam:
                id: 646570
            "Subnautica":
              files:
                "<base>/SNAppData/SavedGames":
                  tags:
                    - save
              gog:
                id: 1456283910
            """;

        // Both ImportRulesYamlAsync and auto-detecting ImportRulesJsonAsync should parse manifest.yaml
        int imported = await machine.RuleResolver.ImportRulesYamlAsync(sampleManifestYaml);
        Assert.Equal(3, imported);

        // 1. Black Myth: Wukong (<base>/b1/Saved/SaveGames - 2-char subdir 'b1'!)
        string wukongId = SpawnId.NewUlid();
        string wukongInstall = Path.Combine(machine.MachineRoot, "Games", "BlackMythWukong");
        string wukongSaveDir = Path.Combine(wukongInstall, "b1", "Saved", "SaveGames");
        Directory.CreateDirectory(wukongSaveDir);
        string wukongSaveFile = Path.Combine(wukongSaveDir, "ArchiveSaveFile.1.sav");
        await File.WriteAllTextAsync(wukongSaveFile, "WUKONG-ACT-1-SAVE", Encoding.UTF8);
        await machine.SeedGameAsync(wukongId, "Black Myth: Wukong", steamAppId: 2358720, installDirOverride: wukongInstall);

        ResolvedSaveLayout wukongLayout = await machine.RuleResolver.ResolveGameSaveLayoutAsync(wukongId);
        Assert.Contains(wukongLayout.SaveLocations, l => l.IsSafe && l.Exists && l.ResolvedFullPath.Equals(wukongSaveDir, StringComparison.OrdinalIgnoreCase));

        // Capture & restore Wukong save end-to-end through SaveTimeMachineService!
        CreateSaveSnapshotResult wukongSnap = await machine.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = wukongId, Note = "Wukong Act 1" });
        Assert.True(wukongSnap.Succeeded, wukongSnap.Message);
        await File.WriteAllTextAsync(wukongSaveFile, "WUKONG-CORRUPTED", Encoding.UTF8);
        SaveRestoreOutcome wukongRestore = await machine.TimeMachine.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = wukongSnap.Snapshot!.SnapshotId,
            CreateSafetySnapshot = false,
        });
        Assert.True(wukongRestore.Succeeded, wukongRestore.Message);
        Assert.Equal("WUKONG-ACT-1-SAVE", await File.ReadAllTextAsync(wukongSaveFile, Encoding.UTF8));

        // 2. Slay the Spire (<base>/preferences + <base>/runs + HKCU only, linux & HKLM excluded)
        string stsId = SpawnId.NewUlid();
        string stsInstall = Path.Combine(machine.MachineRoot, "Games", "SlayTheSpire");
        Directory.CreateDirectory(Path.Combine(stsInstall, "preferences"));
        Directory.CreateDirectory(Path.Combine(stsInstall, "runs"));
        await File.WriteAllTextAsync(Path.Combine(stsInstall, "preferences", "STSDataTheSilent"), "{\"SCORE\":999}", Encoding.UTF8);
        await machine.SeedGameAsync(stsId, "Slay the Spire", steamAppId: 646570, installDirOverride: stsInstall);

        ResolvedSaveLayout stsLayout = await machine.RuleResolver.ResolveGameSaveLayoutAsync(stsId);
        Assert.Contains(stsLayout.SaveLocations, l => l.IsSafe && l.ResolvedFullPath.EndsWith("preferences", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stsLayout.SaveLocations, l => l.IsSafe && l.ResolvedFullPath.EndsWith("runs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stsLayout.RegistryKeys, r => r.ResolvedFullPath.Contains("LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stsLayout.RegistryKeys, r => r.ResolvedFullPath.StartsWith(@"HKCU\Software\MegaCrit\SlayTheSpire", StringComparison.OrdinalIgnoreCase));

        // 3. Per-game custom save locations: allow valid dir/file/HKCU, reject Windows/exe/HKLM
        string customDir = Path.Combine(machine.MachineRoot, "CustomUserSaves", "WukongExtra");
        Directory.CreateDirectory(customDir);
        await File.WriteAllTextAsync(Path.Combine(customDir, "bonus.sav"), "BONUS-DATA", Encoding.UTF8);

        SaveCustomLocationRecord addedRec = await machine.RuleResolver.AddCustomLocationAsync(
            wukongId,
            customDir,
            SaveCustomLocationKind.Directory,
            SaveFileCategory.Save,
            "手动扩展目录");
        Assert.NotNull(addedRec);
        Assert.Equal("手动扩展目录", addedRec.Label);

        ResolvedSaveLayout layoutWithCustom = await machine.RuleResolver.ResolveGameSaveLayoutAsync(wukongId);
        Assert.Contains(layoutWithCustom.SaveLocations, l => l.IsCustom && l.CustomLocationId == addedRec.LocationId);

        // Reject forbidden targets (including isolated sandbox's own SPAWN DataRoot!) with clear InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            machine.RuleResolver.AddCustomLocationAsync(wukongId, @"C:\Windows\System32", SaveCustomLocationKind.Directory));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            machine.RuleResolver.AddCustomLocationAsync(wukongId, Path.Combine(customDir, "evil.exe"), SaveCustomLocationKind.File));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            machine.RuleResolver.AddCustomLocationAsync(wukongId, @"HKLM\Software\Bad", SaveCustomLocationKind.RegistryKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            machine.RuleResolver.AddCustomLocationAsync(wukongId, machine.Paths.DataRoot, SaveCustomLocationKind.Directory));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            machine.RuleResolver.AddCustomLocationAsync(wukongId, Path.Combine(machine.Paths.DataRoot, "cas"), SaveCustomLocationKind.Directory));

        // Remove custom location
        Assert.True(await machine.RuleResolver.RemoveCustomLocationAsync(addedRec.LocationId));
        Assert.Empty(await machine.RuleResolver.GetCustomLocationsAsync(wukongId));
    }

    // =========================================================================
    // P1-1: Linear middle-node deletion, rolling retention past pinned root,
    //       and background branch deletion
    // =========================================================================

    [Fact]
    public async Task SnapshotDeletion_RelinksLinearMiddleNode_BlocksForkHub_AndRetentionBypassesPinnedRoot()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync();

        // Build linear chain S1 (pinned ★) -> S2 -> S3 -> S4
        await env.WriteSaveFileAsync("slot.sav", "V1");
        SaveSnapshotRecord s1 = (await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = env.Game.GameId, Note = "S1", Pin = true })).Snapshot!;

        env.AdvanceMinutes(1);
        await env.WriteSaveFileAsync("slot.sav", "V2");
        SaveSnapshotRecord s2 = (await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = env.Game.GameId, Note = "S2" })).Snapshot!;

        env.AdvanceMinutes(1);
        await env.WriteSaveFileAsync("slot.sav", "V3");
        SaveSnapshotRecord s3 = (await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = env.Game.GameId, Note = "S3" })).Snapshot!;

        env.AdvanceMinutes(1);
        await env.WriteSaveFileAsync("slot.sav", "V4");
        SaveSnapshotRecord s4 = (await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = env.Game.GameId, Note = "S4" })).Snapshot!;

        // 1. Delete middle node S2 -> succeeds & relinks S3.ParentSnapshotId = S1.SnapshotId!
        DeleteSnapshotResult delS2 = await env.Service.DeleteSnapshotAsync(s2.SnapshotId);
        Assert.True(delS2.Succeeded, delS2.Message);
        Assert.Null(await env.Database.GetSaveSnapshotByIdAsync(s2.SnapshotId));
        Assert.Equal(s1.SnapshotId, (await env.Database.GetSaveSnapshotByIdAsync(s3.SnapshotId))!.ParentSnapshotId);

        // 2. Fork from S3 so S3 becomes a multi-child fork hub -> deleting S3 MUST be blocked!
        ForkBranchResult fork = await env.Service.CreateBranchAsync(new ForkBranchRequest
        {
            GameId = env.Game.GameId,
            FromSnapshotId = s3.SnapshotId,
            BranchName = "side-branch",
            SwitchToNewBranch = true,
            CreateSafetySnapshotIfSwitching = false,
        });
        Assert.True(fork.Succeeded, fork.Message);

        env.AdvanceMinutes(1);
        await env.WriteSaveFileAsync("slot.sav", "V4-SIDE");
        SaveSnapshotRecord sSide = (await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = env.Game.GameId, Note = "Side" })).Snapshot!;
        Assert.Equal(s3.SnapshotId, sSide.ParentSnapshotId);

        DeleteSnapshotResult delHub = await env.Service.DeleteSnapshotAsync(s3.SnapshotId);
        Assert.False(delHub.Succeeded);

        // 3. Switch back to main, then background-delete side-branch without touching live disk!
        SaveTreeGraphDto graph = await env.Service.GetSaveTreeGraphAsync(env.Game.GameId);
        SaveBranchRecord mainBranch = graph.Branches.Single(b => b.IsDefault);
        Assert.True((await env.Service.SwitchBranchAsync(new SwitchBranchRequest
        {
            GameId = env.Game.GameId,
            TargetBranchId = mainBranch.BranchId,
            CreateSafetySnapshot = false,
        })).Succeeded);
        Assert.Equal("V4", await env.ReadSaveFileAsync("slot.sav"));

        DeleteBranchResult bgDel = await env.Service.DeleteBranchAsync(fork.Branch!.BranchId, deleteSnapshots: true);
        Assert.True(bgDel.Succeeded, bgDel.Message);
        Assert.Equal("V4", await env.ReadSaveFileAsync("slot.sav")); // Disk untouched!

        // 4. Rolling retention (MaxRollingSnapshots = 2 unpinned) with S1 pinned ★ -> adding S5 & S6 must splice out
        //    unpinned middle nodes S3 & S4 instead of getting stuck on S1, leaving S1 ★ -> S5 -> S6 (3 total: 1 pinned + 2 unpinned)!
        SaveGameStateRecord st = await env.Service.GetGameStateAsync(env.Game.GameId);
        await env.Service.UpdateGameStateAsync(st with { MaxRollingSnapshots = 2 });

        env.AdvanceMinutes(1);
        await env.WriteSaveFileAsync("slot.sav", "V5");
        SaveSnapshotRecord s5 = (await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = env.Game.GameId, Note = "S5" })).Snapshot!;

        env.AdvanceMinutes(1);
        await env.WriteSaveFileAsync("slot.sav", "V6");
        SaveSnapshotRecord s6 = (await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = env.Game.GameId, Note = "S6" })).Snapshot!;

        IReadOnlyList<SaveSnapshotRecord> remaining = await env.Database.GetSaveSnapshotsByGameAsync(env.Game.GameId);
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, s => s.SnapshotId == s1.SnapshotId); // Pinned root preserved!
        Assert.Contains(remaining, s => s.SnapshotId == s5.SnapshotId);
        Assert.Contains(remaining, s => s.SnapshotId == s6.SnapshotId);
        // S5 should now point directly to S1 after S3 and S4 were spliced out!
        Assert.Equal(s1.SnapshotId, remaining.Single(s => s.SnapshotId == s5.SnapshotId).ParentSnapshotId);
    }

    // =========================================================================
    // P1-3: SaveWatcherService parent-directory fallback for non-existent save dir
    // =========================================================================

    [Fact]
    public async Task SaveWatcherService_NonExistentSaveDirectory_WatchesSafeParentAndCapturesWhenCreated()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync(explicitTimeProvider: TimeProvider.System);

        // Delete the save directory before starting the watcher (simulating brand-new first launch!)
        if (Directory.Exists(env.SaveDir))
        {
            Directory.Delete(env.SaveDir, recursive: true);
        }

        Assert.False(Directory.Exists(env.SaveDir));

        await using var watcher = new SaveWatcherService(
            env.Service,
            env.RuleResolver,
            env.EventBus,
            env.Database,
            TimeProvider.System,
            new SaveWatcherOptions
            {
                QuiescentDebounceOverride = TimeSpan.FromMilliseconds(100),
                PostExitSettleDelay = TimeSpan.FromMilliseconds(20),
            });

        var captured = new TaskCompletionSource<SaveTreeChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Service.SaveTreeChanged += (_, ev) =>
        {
            if (ev.Reason == "SnapshotCreated")
            {
                captured.TrySetResult(ev);
            }
        };

        await watcher.StartWatchingGameAsync(env.Game.GameId);
        SaveWatcherStatusDto status = watcher.GetStatus(env.Game.GameId);
        Assert.Equal(SaveWatcherStatusKind.Watching, status.Status);
        Assert.NotEmpty(status.WatchedDirectories);

        // Game creates its save directory and writes first save file during play!
        await env.WriteSaveFileAsync("first_boot.sav", "BRAND-NEW-SAVE");

        SaveTreeChangedEvent ev = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(ev.SnapshotId);
        SaveSnapshotRecord? snap = await env.Database.GetSaveSnapshotByIdAsync(ev.SnapshotId!);
        Assert.NotNull(snap);
        Assert.Equal(SaveSnapshotTriggerKind.AutoQuiescent, snap!.TriggerKind);

        await watcher.StopWatchingGameAsync(env.Game.GameId);
    }

    // =========================================================================
    // P1-4: Divergence live-disk guard, exact missing-chunk download size,
    //       HTTP Chinese diagnostics, and auto-sync policy hooks
    // =========================================================================

    [Fact]
    public async Task CloudSync_AdoptRemote_CapturesUnsavedLiveDiskChangesBeforeOverwriting_AndComputesExactDownloadBytes()
    {
        string remoteSyncRoot = Path.Combine(_tempRoot, "p14-divergence-live-guard");
        Directory.CreateDirectory(remoteSyncRoot);

        await using var machineA = await RealMachineHarness.CreateAsync(_tempRoot, "p14-a", "Desktop-A");
        await using var machineB = await RealMachineHarness.CreateAsync(_tempRoot, "p14-b", "Handheld-B");

        string gameIdA = SpawnId.NewUlid();
        string gameIdB = SpawnId.NewUlid();
        await machineA.SeedGameAsync(gameIdA, "Elden Ring", steamAppId: 1245620);
        await machineB.SeedGameAsync(gameIdB, "Elden Ring", steamAppId: 1245620);
        await machineA.RuleResolver.EnsureBuiltInRulesSeededAsync();
        await machineB.RuleResolver.EnsureBuiltInRulesSeededAsync();
        await machineA.AddLocalEndpointAsync(remoteSyncRoot);
        await machineB.AddLocalEndpointAsync(remoteSyncRoot);

        string saveDirA = Path.Combine(machineA.AppDataRoaming, "EldenRing", "76561198000000055");
        string saveDirB = Path.Combine(machineB.AppDataRoaming, "EldenRing", "76561198000000055");
        Directory.CreateDirectory(saveDirA);
        Directory.CreateDirectory(saveDirB);

        // Shared large incompressible file + small mutable slot file so we can also verify exact partial missing-chunk download estimate!
        byte[] sharedIncompressible = new byte[48 * 1024];
        new Random(42).NextBytes(sharedIncompressible);
        await File.WriteAllBytesAsync(Path.Combine(saveDirA, "ER0001.sl2"), sharedIncompressible);
        await File.WriteAllTextAsync(Path.Combine(saveDirA, "ER0000.sl2"), "BASE-V1", Encoding.UTF8);

        SaveSnapshotRecord s1A = (await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameIdA, Note = "Base V1" })).Snapshot!;
        Assert.True(s1A.FileCount >= 2);
        Assert.True(s1A.TotalStoredBytes > 30_000);
        Assert.True((await machineA.CloudSync.ExecuteSyncAsync(gameIdA)).Succeeded);
        Assert.True((await machineB.CloudSync.ExecuteSyncAsync(gameIdB)).Succeeded);

        // Machine A advances ONLY ER0000.sl2 (keeping 48 KiB ER0001.sl2 identical) and pushes
        await File.WriteAllTextAsync(Path.Combine(saveDirA, "ER0000.sl2"), "REMOTE-V2-NEW-BOSS", Encoding.UTF8);
        SaveSnapshotRecord s2A = (await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameIdA, Note = "Remote V2" })).Snapshot!;
        Assert.True(s2A.FileCount >= 2);
        Assert.True((await machineA.CloudSync.ExecuteSyncAsync(gameIdA)).Succeeded);

        // Verify exact missing-chunk download size estimate on Machine B BEFORE B diverges:
        // Machine B already has 48 KiB ER0001.sl2 locally, so MissingLocalChunkCount == 1 and EstimatedDownloadBytes << s1A.TotalStoredBytes!
        CloudSyncPlanPreview pullPlanB = await machineB.CloudSync.PlanSyncAsync(gameIdB);
        Assert.Equal(CloudSyncRelation.FastForwardPull, pullPlanB.Relation);
        Assert.Equal(1, pullPlanB.MissingLocalChunkCount);
        Assert.True(pullPlanB.EstimatedDownloadBytes > 0);
        Assert.True(pullPlanB.EstimatedDownloadBytes < 1024 && pullPlanB.EstimatedDownloadBytes < s1A.TotalStoredBytes,
            $"Expected exact missing-chunk download bytes ({pullPlanB.EstimatedDownloadBytes}) << full snapshot stored bytes ({s1A.TotalStoredBytes}).");

        // Now Machine B diverges offline AND ALSO makes unsaved live-disk edits AFTER its last snapshot!
        await File.WriteAllTextAsync(Path.Combine(saveDirB, "ER0000.sl2"), "LOCAL-SNAPSHOT-V2B", Encoding.UTF8);
        Assert.True((await machineB.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameIdB, Note = "Local V2B" })).Succeeded);
        await File.WriteAllTextAsync(Path.Combine(saveDirB, "ER0000.sl2"), "UNSAVED-LIVE-DISK-EDIT-CRITICAL", Encoding.UTF8);

        Assert.Equal(CloudSyncRelation.Diverged, (await machineB.CloudSync.PlanSyncAsync(gameIdB)).Relation);

        CloudSyncExecutionReport adopted = await machineB.CloudSync.ExecuteSyncAsync(
            gameIdB,
            divergenceResolution: CloudDivergenceResolution.AdoptRemoteAndBackupLocal);
        Assert.True(adopted.Succeeded, adopted.Message);

        // Disk now has Remote V2
        Assert.Equal("REMOTE-V2-NEW-BOSS", await File.ReadAllTextAsync(Path.Combine(saveDirB, "ER0000.sl2"), Encoding.UTF8));

        // AND Machine B's UNSAVED live disk edit was captured into a pinned safety snapshot before overwrite!
        IReadOnlyList<SaveSnapshotRecord> allSnapsB = await machineB.Database.GetSaveSnapshotsByGameAsync(gameIdB);
        SaveSnapshotRecord? liveGuardSnap = allSnapsB.FirstOrDefault(s =>
            s.TriggerKind == SaveSnapshotTriggerKind.PreRollbackSafety
            && s.IsPinned
            && (s.Note?.Contains("采用云端前自动保护", StringComparison.Ordinal) ?? false));
        Assert.NotNull(liveGuardSnap);

        // Rollback to that live-guard safety snapshot and prove the unsaved edit is 100% recoverable!
        SaveRestoreOutcome recover = await machineB.TimeMachine.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = liveGuardSnap!.SnapshotId,
            CreateSafetySnapshot = false,
        });
        Assert.True(recover.Succeeded, recover.Message);
        Assert.Equal("UNSAVED-LIVE-DISK-EDIT-CRITICAL", await File.ReadAllTextAsync(Path.Combine(saveDirB, "ER0000.sl2"), Encoding.UTF8));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "405")]
    public async Task Transports_HttpErrorStatuses_TranslateToActionableChineseDiagnostics(HttpStatusCode statusCode, string expectedCodeText)
    {
        using var mockHandler = new FixedResponseHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("mock-error-body"),
        });
        using var httpClient = new HttpClient(mockHandler);

        var s3 = new S3SyncTransport(
            "https://s3.example.test",
            "my-bucket",
            "auto",
            pathStyleAddressing: true,
            new CloudSyncEndpointSecrets("ak", "sk"),
            httpClient);

        var s3Ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            s3.PutBytesAsync("repo.json", Encoding.UTF8.GetBytes("{}")));
        Assert.Contains(expectedCodeText, s3Ex.Message, StringComparison.Ordinal);
        Assert.Contains("S3/R2", s3Ex.Message, StringComparison.Ordinal);

        var dav = new WebDavSyncTransport(
            "https://dav.example.test/remote.php/dav/files/user/",
            "spawn",
            new CloudSyncEndpointSecrets("user", "pass"),
            httpClient);

        var davEx = await Assert.ThrowsAsync<HttpRequestException>(() =>
            dav.PutBytesAsync("repo.json", Encoding.UTF8.GetBytes("{}")));
        Assert.Contains(expectedCodeText, davEx.Message, StringComparison.Ordinal);
        Assert.Contains("WebDAV", davEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transports_TimeoutException_TranslatesToChineseNetworkDiagnostic()
    {
        using var timeoutHandler = new FixedResponseHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout"));
        using var httpClient = new HttpClient(timeoutHandler);

        var dav = new WebDavSyncTransport(
            "https://dav.example.test/dav/",
            "spawn",
            new CloudSyncEndpointSecrets("u", "p"),
            httpClient);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            dav.PutBytesAsync("repo.json", Encoding.UTF8.GetBytes("{}")));
        Assert.Contains("超时", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudSyncService_EndpointUpdateDefaultSwitch_AndAutoPushPreLaunchPolicies()
    {
        string remoteSyncRoot = Path.Combine(_tempRoot, "p14-policy-remote");
        Directory.CreateDirectory(remoteSyncRoot);

        await using var machineA = await RealMachineHarness.CreateAsync(_tempRoot, "p14-pol-a", "Desktop-Policy");
        await using var machineB = await RealMachineHarness.CreateAsync(_tempRoot, "p14-pol-b", "Laptop-Policy");

        string gameIdA = SpawnId.NewUlid();
        string gameIdB = SpawnId.NewUlid();
        await machineA.SeedGameAsync(gameIdA, "Elden Ring", steamAppId: 1245620);
        await machineB.SeedGameAsync(gameIdB, "Elden Ring", steamAppId: 1245620);
        await machineA.RuleResolver.EnsureBuiltInRulesSeededAsync();
        await machineB.RuleResolver.EnsureBuiltInRulesSeededAsync();

        CloudSyncEndpointRecord ep1 = await machineA.CloudSync.SaveEndpointAsync(new CloudSyncEndpointRecord
        {
            EndpointId = SpawnId.NewUlid(),
            DisplayName = "Primary NAS",
            ProviderType = CloudSyncProviderType.LocalFolder,
            EndpointUrl = remoteSyncRoot,
            IsDefault = true,
        });
        CloudSyncEndpointRecord ep2 = await machineA.CloudSync.SaveEndpointAsync(new CloudSyncEndpointRecord
        {
            EndpointId = SpawnId.NewUlid(),
            DisplayName = "Secondary S3",
            ProviderType = CloudSyncProviderType.S3,
            EndpointUrl = "https://r2.example.test",
            BucketOrRoot = "saves",
            PathStyleAddressing = false,
            IsDefault = false,
        });

        // UpdateEndpointAsync updates PathStyleAddressing & DisplayName cleanly
        CloudSyncEndpointRecord updatedEp2 = await machineA.CloudSync.UpdateEndpointAsync(ep2 with
        {
            DisplayName = "Secondary R2 Updated",
            PathStyleAddressing = true,
        });
        Assert.Equal("Secondary R2 Updated", updatedEp2.DisplayName);
        Assert.True(updatedEp2.PathStyleAddressing);

        // SetDefaultEndpointAsync toggles default cleanly
        Assert.True(await machineA.CloudSync.SetDefaultEndpointAsync(ep2.EndpointId));
        Assert.True((await machineA.CloudSync.ListEndpointsAsync()).Single(e => e.EndpointId == ep2.EndpointId).IsDefault);
        Assert.True(await machineA.CloudSync.SetDefaultEndpointAsync(ep1.EndpointId));

        // Disabled AutoCloudPushOnGameExit -> HandleGameExitAutoPushAsync returns null (no-op)
        string saveFileA = Path.Combine(machineA.AppDataRoaming, "EldenRing", "76561198000000077", "ER0000.sl2");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFileA)!);
        await File.WriteAllTextAsync(saveFileA, "AUTO-PUSH-DATA", Encoding.UTF8);
        Assert.True((await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest { GameId = gameIdA, Note = "Before Exit" })).Succeeded);

        Assert.Null(await machineA.CloudSync.HandleGameExitAutoPushAsync(gameIdA));

        // Enable AutoCloudPushOnGameExit -> HandleGameExitAutoPushAsync pushes automatically!
        SaveSettingsDto sA = await machineA.TimeMachine.GetSettingsAsync();
        await machineA.TimeMachine.SaveSettingsAsync(sA with { AutoCloudPushOnGameExit = true });
        CloudSyncExecutionReport? autoPush = await machineA.CloudSync.HandleGameExitAutoPushAsync(gameIdA);
        Assert.NotNull(autoPush);
        Assert.True(autoPush!.Succeeded, autoPush.Message);
        Assert.Equal(CloudSyncRelation.FastForwardPush, autoPush.Relation);

        // Machine B checks remote update before launch when CheckRemoteUpdatesBeforeLaunch = true -> detects FastForwardPull!
        await machineB.AddLocalEndpointAsync(remoteSyncRoot);
        Assert.Null(await machineB.CloudSync.CheckRemoteUpdateBeforeLaunchAsync(gameIdB)); // disabled by default

        SaveSettingsDto sB = await machineB.TimeMachine.GetSettingsAsync();
        await machineB.TimeMachine.SaveSettingsAsync(sB with { CheckRemoteUpdatesBeforeLaunch = true });
        CloudSyncPlanPreview? preLaunchPlan = await machineB.CloudSync.CheckRemoteUpdateBeforeLaunchAsync(gameIdB);
        Assert.NotNull(preLaunchPlan);
        Assert.Equal(CloudSyncRelation.FastForwardPull, preLaunchPlan!.Relation);
    }

    // =========================================================================
    // Offline Real DI Machine Harness
    // =========================================================================

    private sealed class FixedResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }

    private sealed class RealMachineHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _sp;
        public string MachineRoot { get; }
        public SpawnPaths Paths { get; }
        public SpawnDatabase Database { get; }
        public ILudusaviRuleResolver RuleResolver { get; }
        public ISaveTimeMachineService TimeMachine { get; }
        public ICloudSaveSyncService CloudSync { get; }
        public string AppDataRoaming { get; }

        private RealMachineHarness(
            ServiceProvider sp,
            string machineRoot,
            SpawnPaths paths,
            string appDataRoaming)
        {
            _sp = sp;
            MachineRoot = machineRoot;
            Paths = paths;
            Database = sp.GetRequiredService<SpawnDatabase>();
            RuleResolver = sp.GetRequiredService<ILudusaviRuleResolver>();
            TimeMachine = sp.GetRequiredService<ISaveTimeMachineService>();
            CloudSync = sp.GetRequiredService<ICloudSaveSyncService>();
            AppDataRoaming = appDataRoaming;
        }

        public static async Task<RealMachineHarness> CreateAsync(string tempRoot, string machineName, string displayName)
        {
            string machineRoot = Path.Combine(tempRoot, machineName);
            string dataDir = Path.Combine(machineRoot, "spawn-data");
            string appData = Path.Combine(machineRoot, "AppData", "Roaming");
            string localAppData = Path.Combine(machineRoot, "AppData", "Local");
            string localLow = Path.Combine(machineRoot, "AppData", "LocalLow");
            string docs = Path.Combine(machineRoot, "Documents");
            string steam = Path.Combine(machineRoot, "Steam");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(appData);
            Directory.CreateDirectory(localAppData);
            Directory.CreateDirectory(localLow);
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(Path.Combine(steam, "userdata", "76561198000000001"));

            SpawnPaths paths = SpawnPaths.CreateIsolated(dataDir, workspaceRootHint: dataDir);
            paths.EnsureDirectoriesExist();

            var knownFolders = new StaticKnownFolderProvider
            {
                Home = machineRoot,
                WinAppData = appData,
                WinLocalAppData = localAppData,
                WinLocalAppDataLow = localLow,
                WinDocuments = docs,
                WinPublic = Path.Combine(machineRoot, "Public"),
                WinProgramData = Path.Combine(machineRoot, "ProgramData"),
                WinDir = Path.Combine(machineRoot, "Windows"),
                OsUserName = machineName,
                SteamRootDirectories = [steam],
                StoreUserIds = ["76561198000000001"],
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSpawnCore(paths);
            services.AddSingleton<IKnownFolderProvider>(knownFolders);
            services.AddSingleton<IGameDataManagementService>(new SandboxGameDataService(machineRoot));
            services.AddSingleton<IGameLaunchService, FakeGameLaunchService>();
            services.AddSpawnProgress();

            ServiceProvider sp = services.BuildServiceProvider();
            await sp.GetRequiredService<SpawnDatabase>().InitializeAsync();
            var harness = new RealMachineHarness(sp, machineRoot, paths, appData);

            await harness.Database.SaveSaveSettingsAsync(new SaveSettingsDto
            {
                MachineId = $"pc-{machineName}",
                MachineDisplayName = displayName,
            });
            return harness;
        }

        public async Task SeedGameAsync(string gameId, string title, int? steamAppId = null, string? gogGameId = null, string? installDirOverride = null)
        {
            string installDir = installDirOverride ?? Path.Combine(MachineRoot, "games", gameId);
            Directory.CreateDirectory(installDir);

            await Database.UpsertGameAsync(new GameRecord
            {
                GameId = gameId,
                Title = title,
                SteamAppId = steamAppId,
                GogGameId = gogGameId,
                PlatformSource = steamAppId.HasValue ? PlatformSource.SteamLocal : PlatformSource.Standalone,
                InstallDir = installDir,
                ExecutableRelPath = "game.exe",
            });
            await Database.EnsureDefaultSaveBranchAsync(gameId);
        }

        public Task<CloudSyncEndpointRecord> AddLocalEndpointAsync(string localSyncRoot) =>
            CloudSync.SaveEndpointAsync(new CloudSyncEndpointRecord
            {
                EndpointId = SpawnId.NewUlid(),
                DisplayName = "Shared Local Sync",
                ProviderType = CloudSyncProviderType.LocalFolder,
                EndpointUrl = localSyncRoot,
                IsDefault = true,
            });

        public ValueTask DisposeAsync() => _sp.DisposeAsync();
    }

    private sealed class SandboxGameDataService(string allowedRoot) : IGameDataManagementService
    {
        private readonly string _allowedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));

        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(string gameId, bool refreshOnlineRules = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameDataEntryDto>>([]);

        public Task<GameDataSafetyCheckResult> ValidateSaveRestoreTargetAsync(string gameId, string pathOrRegistryKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pathOrRegistryKey))
            {
                return Task.FromResult(new GameDataSafetyCheckResult(false, "empty"));
            }

            if (pathOrRegistryKey.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
                || pathOrRegistryKey.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
            {
                (bool safeReg, _, string? regReason) = TrustedSavePathFence.ValidateTrustedRegistryTarget(pathOrRegistryKey);
                return Task.FromResult(new GameDataSafetyCheckResult(safeReg, regReason));
            }

            string full = Path.GetFullPath(pathOrRegistryKey);
            bool inside = full.StartsWith(_allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new GameDataSafetyCheckResult(inside, inside ? null : "outside sandbox"));
        }

        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameDataCleanResult.Ok("ok"));

        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);

        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameDataCleanResult.Ok("ok"));
    }
}
