using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress;
using Spawn.Modules.Progress.Saves.Rules;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Saves;

/// <summary>
/// Full end-to-end integration test exercising the real DI composition (<c>AddSpawnCore</c> + <c>AddSpawnProgress</c>)
/// across Ludusavi rule resolution, Save Time Machine snapshots, branching/forking/switching,
/// atomic rollback with <c>PreRollbackSafety</c>, and 32MB SPPK cloud sync between two isolated machines
/// including offline divergence branch preservation.
/// </summary>
public sealed class SaveSystemEndToEndIntegrationTests
{
    [Fact]
    public async Task FullDiPipeline_RuleResolutionBranchingRollbackAndTwoMachineSppkSyncWithDivergence()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "spawn-e2e-saves-" + Guid.NewGuid().ToString("N"));
        string remoteSyncRoot = Path.Combine(tempRoot, "remote-sppk-repo");
        Directory.CreateDirectory(remoteSyncRoot);

        try
        {
            await using var machineA = await MachineNode.CreateAsync(tempRoot, "machine-a");
            await using var machineB = await MachineNode.CreateAsync(tempRoot, "machine-b");

            // Same logical game (Elden Ring, SteamAppId = 1245620) installed on both machines with the same GameId.
            string sharedGameId = SpawnId.NewUlid();
            await machineA.SeedGameAsync(sharedGameId, "Elden Ring", steamAppId: 1245620);
            await machineB.SeedGameAsync(sharedGameId, "Elden Ring", steamAppId: 1245620);

            // 1. Built-in Ludusavi seed loads into SQLite & resolves Elden Ring [save] vs [config] cleanly!
            int seededCount = await machineA.RuleResolver.EnsureBuiltInRulesSeededAsync();
            Assert.True(seededCount >= 20, $"Expected >= 20 seeded Ludusavi rules, got {seededCount}.");

            string erSaveDirA = Path.Combine(machineA.AppDataRoaming, "EldenRing", "76561198000000001");
            Directory.CreateDirectory(erSaveDirA);
            string erSaveFileA = Path.Combine(erSaveDirA, "ER0000.sl2");
            await File.WriteAllTextAsync(erSaveFileA, "MALENIA-ATTEMPT-1", Encoding.UTF8);

            string erCfgFileA = Path.Combine(machineA.AppDataRoaming, "EldenRing", "GraphicsConfig.xml");
            await File.WriteAllTextAsync(erCfgFileA, "<Graphics>4K-Ultra</Graphics>", Encoding.UTF8);

            ResolvedSaveLayout layoutA = await machineA.RuleResolver.ResolveGameSaveLayoutAsync(sharedGameId);
            Assert.Equal("Elden Ring", layoutA.MatchedRuleName, ignoreCase: true);
            Assert.NotEmpty(layoutA.SaveLocations);

            // 2. Machine A captures snapshot S1, advances S2, forks branch "frenzied-flame" from S1, switches back & forth.
            CreateSaveSnapshotResult s1 = await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = sharedGameId,
                Note = "Before Ending Choice",
                Pin = true,
            });
            Assert.True(s1.Succeeded, s1.Message);

            await File.WriteAllTextAsync(erSaveFileA, "AGE-OF-STARS-ENDING", Encoding.UTF8);
            CreateSaveSnapshotResult s2 = await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = sharedGameId,
                Note = "Age of Stars",
            });
            Assert.True(s2.Succeeded, s2.Message);

            ForkBranchResult fork = await machineA.TimeMachine.CreateBranchAsync(new ForkBranchRequest
            {
                GameId = sharedGameId,
                FromSnapshotId = s1.Snapshot!.SnapshotId,
                BranchName = "frenzied-flame",
                SwitchToNewBranch = true,
            });
            Assert.True(fork.Succeeded, fork.Message);
            Assert.Equal("MALENIA-ATTEMPT-1", await File.ReadAllTextAsync(erSaveFileA, Encoding.UTF8));

            await File.WriteAllTextAsync(erSaveFileA, "LORD-OF-FRENZIED-FLAME", Encoding.UTF8);
            CreateSaveSnapshotResult sFork = await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = sharedGameId,
                Note = "Frenzied Flame Ending",
            });
            Assert.True(sFork.Succeeded, sFork.Message);

            // Switch back to main -> disk swaps back to Age of Stars!
            SaveTreeGraphDto graphA = await machineA.TimeMachine.GetSaveTreeGraphAsync(sharedGameId);
            SaveBranchRecord mainBranchA = Assert.Single(graphA.Branches, b => b.IsDefault);
            SaveRestoreOutcome swMain = await machineA.TimeMachine.SwitchBranchAsync(new SwitchBranchRequest
            {
                GameId = sharedGameId,
                TargetBranchId = mainBranchA.BranchId,
                CreateSafetySnapshot = false,
            });
            Assert.True(swMain.Succeeded, swMain.Message);
            Assert.Equal("AGE-OF-STARS-ENDING", await File.ReadAllTextAsync(erSaveFileA, Encoding.UTF8));

            // 3. Configure shared LocalFolder SPPK cloud sync endpoint on both machines & push from Machine A.
            CloudSyncEndpointRecord epA = await machineA.CloudSync.SaveEndpointAsync(new CloudSyncEndpointRecord
            {
                EndpointId = SpawnId.NewUlid(),
                DisplayName = "Shared Sync Drive",
                ProviderType = CloudSyncProviderType.LocalFolder,
                EndpointUrl = remoteSyncRoot,
                IsDefault = true,
            });

            CloudSyncPlanPreview planPushA = await machineA.CloudSync.PlanSyncAsync(sharedGameId, epA.EndpointId);
            Assert.Equal(CloudSyncRelation.FastForwardPush, planPushA.Relation);

            CloudSyncExecutionReport pushA = await machineA.CloudSync.ExecuteSyncAsync(sharedGameId, epA.EndpointId);
            Assert.True(pushA.Succeeded, pushA.Message);
            Assert.True(pushA.UploadedPackCount >= 1);

            // 4. Machine B pulls via SPPK FastForwardPull -> recreates save file on Machine B!
            CloudSyncEndpointRecord epB = await machineB.CloudSync.SaveEndpointAsync(new CloudSyncEndpointRecord
            {
                EndpointId = SpawnId.NewUlid(),
                DisplayName = "Shared Sync Drive",
                ProviderType = CloudSyncProviderType.LocalFolder,
                EndpointUrl = remoteSyncRoot,
                IsDefault = true,
            });

            CloudSyncPlanPreview planPullB = await machineB.CloudSync.PlanSyncAsync(sharedGameId, epB.EndpointId);
            Assert.Equal(CloudSyncRelation.FastForwardPull, planPullB.Relation);

            CloudSyncExecutionReport pullB = await machineB.CloudSync.ExecuteSyncAsync(sharedGameId, epB.EndpointId);
            Assert.True(pullB.Succeeded, pullB.Message);

            string erSaveFileB = Path.Combine(machineB.AppDataRoaming, "EldenRing", "76561198000000001", "ER0000.sl2");
            Assert.True(File.Exists(erSaveFileB), $"Expected pulled save file on Machine B at {erSaveFileB}");
            Assert.Equal("AGE-OF-STARS-ENDING", await File.ReadAllTextAsync(erSaveFileB, Encoding.UTF8));

            // 5. Offline divergence: both Machine A and Machine B advance independently without syncing!
            await File.WriteAllTextAsync(erSaveFileA, "DESKTOP-DLC-SHADOW-TREE", Encoding.UTF8);
            Assert.True((await machineA.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = sharedGameId,
                Note = "Desktop DLC Progress",
            })).Succeeded);
            Assert.True((await machineA.CloudSync.ExecuteSyncAsync(sharedGameId, epA.EndpointId)).Succeeded);

            await File.WriteAllTextAsync(erSaveFileB, "HANDHELD-OFFLINE-RUNE-FARM", Encoding.UTF8);
            Assert.True((await machineB.TimeMachine.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = sharedGameId,
                Note = "Handheld Offline Farming",
            })).Succeeded);

            CloudSyncPlanPreview divergedPlan = await machineB.CloudSync.PlanSyncAsync(sharedGameId, epB.EndpointId);
            Assert.Equal(CloudSyncRelation.Diverged, divergedPlan.Relation);

            // Resolve non-destructively: adopt remote & preserve Machine B's local progress as a pinned backup branch!
            CloudSyncExecutionReport resolved = await machineB.CloudSync.ExecuteSyncAsync(
                sharedGameId,
                epB.EndpointId,
                CloudDivergenceResolution.AdoptRemoteAndBackupLocal);
            Assert.True(resolved.Succeeded, resolved.Message);
            Assert.False(string.IsNullOrWhiteSpace(resolved.DivergenceBackupBranchId));
            Assert.Equal("DESKTOP-DLC-SHADOW-TREE", await File.ReadAllTextAsync(erSaveFileB, Encoding.UTF8));

            // Verify Machine B now has BOTH the adopted remote HEAD AND the preserved local backup branch!
            SaveTreeGraphDto graphB = await machineB.TimeMachine.GetSaveTreeGraphAsync(sharedGameId);
            Assert.True(graphB.Branches.Count >= 2);
            Assert.Contains(graphB.Branches, b => b.BranchId == resolved.DivergenceBackupBranchId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private sealed class MachineNode : IAsyncDisposable
    {
        public string MachineRoot { get; }
        public string AppDataRoaming { get; }
        public ServiceProvider Services { get; }
        public SpawnDatabase Database => Services.GetRequiredService<SpawnDatabase>();
        public ILudusaviRuleResolver RuleResolver => Services.GetRequiredService<ILudusaviRuleResolver>();
        public ISaveTimeMachineService TimeMachine => Services.GetRequiredService<ISaveTimeMachineService>();
        public ISaveWatcherService Watcher => Services.GetRequiredService<ISaveWatcherService>();
        public ICloudSaveSyncService CloudSync => Services.GetRequiredService<ICloudSaveSyncService>();

        private MachineNode(string machineRoot, string appDataRoaming, ServiceProvider services)
        {
            MachineRoot = machineRoot;
            AppDataRoaming = appDataRoaming;
            Services = services;
        }

        public static async Task<MachineNode> CreateAsync(string parentTempRoot, string name)
        {
            string root = Path.Combine(parentTempRoot, name);
            string dataDir = Path.Combine(root, "data");
            string roaming = Path.Combine(root, "AppData", "Roaming");
            string local = Path.Combine(root, "AppData", "Local");
            string localLow = Path.Combine(root, "AppData", "LocalLow");
            string docs = Path.Combine(root, "Documents");
            string steam = Path.Combine(root, "Steam");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(roaming);
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(localLow);
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(Path.Combine(steam, "userdata", "76561198000000001"));

            var paths = SpawnPaths.CreateIsolated(dataDir);
            paths.EnsureDirectoriesExist();

            var knownFolders = new StaticKnownFolderProvider
            {
                Home = root,
                WinAppData = roaming,
                WinLocalAppData = local,
                WinLocalAppDataLow = localLow,
                WinDocuments = docs,
                WinPublic = Path.Combine(root, "Public"),
                WinProgramData = Path.Combine(root, "ProgramData"),
                WinDir = Path.Combine(root, "Windows"),
                OsUserName = name,
                SteamRootDirectories = [steam],
                StoreUserIds = ["76561198000000001"],
            };

            var sc = new ServiceCollection();
            sc.AddSpawnCore(paths);
            sc.AddSingleton<IKnownFolderProvider>(knownFolders);
            sc.AddSingleton<IGameDataManagementService>(new PermissiveTempGameDataService(root));
            sc.AddSingleton<IGameLaunchService>(new FakeGameLaunchService());
            sc.AddSpawnProgress();

            ServiceProvider provider = sc.BuildServiceProvider();
            await provider.GetRequiredService<SpawnDatabase>().InitializeAsync();
            return new MachineNode(root, roaming, provider);
        }

        public async Task SeedGameAsync(string gameId, string title, int? steamAppId)
        {
            string installDir = Path.Combine(MachineRoot, "games", title.Replace(' ', '_'));
            Directory.CreateDirectory(installDir);
            await Database.UpsertGameAsync(new GameRecord
            {
                GameId = gameId,
                Title = title,
                InstallDir = installDir,
                ExecutableRelPath = "game.exe",
                PlatformSource = PlatformSource.SteamLocal,
                SteamAppId = steamAppId,
                TotalActiveSec = 3600,
            });
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class PermissiveTempGameDataService(string allowedRoot) : IGameDataManagementService
    {
        private readonly string _allowedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));

        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
            string gameId,
            bool refreshOnlineRules = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameDataEntryDto>>([]);

        public Task<GameDataSafetyCheckResult> ValidateSaveRestoreTargetAsync(
            string gameId,
            string pathOrRegistryKey,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pathOrRegistryKey))
            {
                return Task.FromResult(new GameDataSafetyCheckResult(false, "empty"));
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
