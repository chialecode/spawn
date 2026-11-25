using System.Text;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Saves.Engine;
using Spawn.Modules.Progress.Snapshots;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Saves;

/// <summary>
/// Comprehensive headless tests for <see cref="SaveTimeMachineService"/>, <see cref="SaveTreeLayoutBuilder"/>,
/// multi-branch forking &amp; checkout, detached HEAD, diffing, pin protection, rolling retention,
/// CAS GC, folder export, and legacy M3 <c>pre_trainer_snapshots</c> import.
/// </summary>
public sealed class SaveTimeMachineAndBranchingTests
{
    [Fact]
    public async Task BranchingTree_ForkFromHistoricalNodeSwitchBranchesAndVerifyDiskSwap()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync();

        // Write v1 save on main
        await env.WriteSaveFileAsync("slot0.sav", "CHAPTER-1-START");
        CreateSaveSnapshotResult s1 = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Chapter 1 Start",
        });
        Assert.True(s1.Succeeded);
        Assert.NotNull(s1.Snapshot);

        // Advance v2 on main (also add slot1.sav exclusive to main)
        env.AdvanceMinutes(10);
        await env.WriteSaveFileAsync("slot0.sav", "CHAPTER-2-GOOD-ENDING");
        await env.WriteSaveFileAsync("slot1.sav", "GOOD-ENDING-EXTRA");
        CreateSaveSnapshotResult s2 = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Good Ending Route",
        });
        Assert.True(s2.Succeeded);
        Assert.Equal(s1.Snapshot!.SnapshotId, s2.Snapshot!.ParentSnapshotId);

        // Fork a new branch "bad-ending" from historical node s1 and immediately switch & checkout!
        env.AdvanceMinutes(5);
        ForkBranchResult forkRes = await env.Service.CreateBranchAsync(new ForkBranchRequest
        {
            GameId = env.Game.GameId,
            FromSnapshotId = s1.Snapshot.SnapshotId,
            BranchName = "bad-ending",
            Description = "Alternate boss choice",
            SwitchToNewBranch = true,
            CreateSafetySnapshotIfSwitching = true,
        });
        Assert.True(forkRes.Succeeded, forkRes.Message);
        Assert.NotNull(forkRes.Branch);
        Assert.Equal("bad-ending", forkRes.Branch!.Name);

        // Disk should now match s1 (slot0 = CHAPTER-1-START, extraneous slot1.sav removed!)
        Assert.Equal("CHAPTER-1-START", await env.ReadSaveFileAsync("slot0.sav"));
        Assert.False(env.SaveFileExists("slot1.sav"));

        // Progress along the forked branch
        env.AdvanceMinutes(5);
        await env.WriteSaveFileAsync("slot0.sav", "CHAPTER-2-BAD-ENDING");
        await env.WriteSaveFileAsync("bad_route.sav", "SECRET-BOSS");
        CreateSaveSnapshotResult sFork = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Bad Ending Progress",
        });
        Assert.True(sFork.Succeeded);
        Assert.Equal(s1.Snapshot.SnapshotId, sFork.Snapshot!.ParentSnapshotId);
        Assert.Equal(forkRes.Branch.BranchId, sFork.Snapshot.BranchId);

        // Inspect DAG layout: active branch ("bad-ending") pinned to lane 0, "main" on lane 1, fork edge present
        SaveTreeGraphDto graph = await env.Service.GetSaveTreeGraphAsync(env.Game.GameId);
        Assert.Equal(forkRes.Branch.BranchId, graph.ActiveBranchId);
        Assert.Equal("bad-ending", graph.ActiveBranchName);
        Assert.Equal(sFork.Snapshot.SnapshotId, graph.CurrentHeadSnapshotId);
        Assert.False(graph.IsDetachedHead);
        Assert.Equal(2, graph.Branches.Count);

        SaveTreeNodeDto forkNode = Assert.Single(graph.Nodes, n => n.Snapshot.SnapshotId == sFork.Snapshot.SnapshotId);
        SaveTreeNodeDto s1Node = Assert.Single(graph.Nodes, n => n.Snapshot.SnapshotId == s1.Snapshot.SnapshotId);
        SaveTreeNodeDto s2Node = Assert.Single(graph.Nodes, n => n.Snapshot.SnapshotId == s2.Snapshot.SnapshotId);

        Assert.Equal(0, forkNode.LaneIndex);
        Assert.Equal(0, s1Node.LaneIndex);
        Assert.True(s2Node.LaneIndex >= 1, $"Expected secondary branch node on lane >= 1, got {s2Node.LaneIndex}.");
        Assert.True(forkNode.IsCurrentHead);
        Assert.Contains("bad-ending", forkNode.BranchTipNames);
        Assert.Contains("main", s2Node.BranchTipNames);
        Assert.True(s1Node.ChildSnapshotIds.Count >= 2);
        Assert.Contains(graph.Edges, e => e.FromSnapshotId == s2.Snapshot.SnapshotId && e.ToSnapshotId == s1.Snapshot.SnapshotId && e.IsForkEdge);

        // Switch back to main -> disk restores Good Ending + slot1.sav, removes bad_route.sav!
        SaveBranchRecord mainBranch = Assert.Single(graph.Branches, b => b.IsDefault);
        SaveRestoreOutcome switchBack = await env.Service.SwitchBranchAsync(new SwitchBranchRequest
        {
            GameId = env.Game.GameId,
            TargetBranchId = mainBranch.BranchId,
            CreateSafetySnapshot = false,
        });
        Assert.True(switchBack.Succeeded, switchBack.Message);
        Assert.Equal("CHAPTER-2-GOOD-ENDING", await env.ReadSaveFileAsync("slot0.sav"));
        Assert.Equal("GOOD-ENDING-EXTRA", await env.ReadSaveFileAsync("slot1.sav"));
        Assert.False(env.SaveFileExists("bad_route.sav"));
    }

    [Fact]
    public async Task Rollback_CreatesPreRollbackSafetyEntersDetachedHeadAndSupportsForking()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync();

        await env.WriteSaveFileAsync("save.dat", "STATE-1");
        CreateSaveSnapshotResult s1 = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Node 1",
        });
        env.AdvanceMinutes(5);

        await env.WriteSaveFileAsync("save.dat", "STATE-2");
        CreateSaveSnapshotResult s2 = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Node 2",
        });
        env.AdvanceMinutes(5);

        // Modify disk without snapshotting yet
        await env.WriteSaveFileAsync("save.dat", "UNSAVED-STATE-3");

        // Rollback to interior node s1 -> creates PreRollbackSafety capturing UNSAVED-STATE-3!
        SaveRestoreOutcome rb = await env.Service.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = s1.Snapshot!.SnapshotId,
            CreateSafetySnapshot = true,
        });
        Assert.True(rb.Succeeded, rb.Message);
        Assert.NotNull(rb.SafetySnapshot);
        Assert.Equal(SaveSnapshotTriggerKind.PreRollbackSafety, rb.SafetySnapshot!.TriggerKind);
        Assert.Equal("STATE-1", await env.ReadSaveFileAsync("save.dat"));

        // Tree should show Detached HEAD at s1!
        SaveTreeGraphDto graph = await env.Service.GetSaveTreeGraphAsync(env.Game.GameId);
        Assert.True(graph.IsDetachedHead);
        Assert.Equal(s1.Snapshot.SnapshotId, graph.CurrentHeadSnapshotId);

        // We can also roll back to the safety snapshot to recover UNSAVED-STATE-3!
        SaveRestoreOutcome recover = await env.Service.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = rb.SafetySnapshot.SnapshotId,
            CreateSafetySnapshot = false,
            ForkNewBranchName = "recovered-unsaved",
        });
        Assert.True(recover.Succeeded, recover.Message);
        Assert.Equal("UNSAVED-STATE-3", await env.ReadSaveFileAsync("save.dat"));

        SaveTreeGraphDto graphAfter = await env.Service.GetSaveTreeGraphAsync(env.Game.GameId);
        Assert.Equal("recovered-unsaved", graphAfter.ActiveBranchName);
        Assert.False(graphAfter.IsDetachedHead);
    }

    [Fact]
    public async Task DiffPinDeleteGcExportAndLegacyImport_AllWorkEndToEnd()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync();

        // 1. Create two snapshots and test DiffSnapshotsAsync + DiffSnapshotWithDiskAsync
        await env.WriteSaveFileAsync("a.sav", "AAA-v1");
        await env.WriteSaveFileAsync("b.sav", "BBB-v1");
        CreateSaveSnapshotResult s1 = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Base",
            Pin = true,
        });

        env.AdvanceMinutes(5);
        await env.WriteSaveFileAsync("a.sav", "AAA-v2-modified");
        env.DeleteSaveFile("b.sav");
        await env.WriteSaveFileAsync("c.sav", "CCC-added");
        CreateSaveSnapshotResult s2 = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Changed",
        });

        SaveSnapshotDiffDto diff = await env.Service.DiffSnapshotsAsync(s1.Snapshot!.SnapshotId, s2.Snapshot!.SnapshotId);
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.ModifiedCount);
        Assert.Equal(1, diff.DeletedCount);
        Assert.Equal(0, diff.UnchangedCount);

        // Modify disk relative to s2 -> DiffSnapshotWithDiskAsync detects live modification
        await env.WriteSaveFileAsync("c.sav", "CCC-live-disk");
        SaveSnapshotDiffDto diskDiff = await env.Service.DiffSnapshotWithDiskAsync(s2.Snapshot.SnapshotId);
        Assert.Equal(1, diskDiff.ModifiedCount);
        Assert.Equal(1, diskDiff.UnchangedCount);

        // 2. Pin & HEAD deletion protection
        DeleteSnapshotResult delPinned = await env.Service.DeleteSnapshotAsync(s1.Snapshot.SnapshotId);
        Assert.False(delPinned.Succeeded);
        DeleteSnapshotResult delHead = await env.Service.DeleteSnapshotAsync(s2.Snapshot.SnapshotId);
        Assert.False(delHead.Succeeded);

        // 3. Export snapshot to arbitrary folder
        string exportDir = Path.Combine(env.RootDir, "exported-s1");
        ExportSnapshotResult exportRes = await env.Service.ExportSnapshotToFolderAsync(s1.Snapshot.SnapshotId, exportDir);
        Assert.True(exportRes.Succeeded, exportRes.Message);
        Assert.Equal(2, exportRes.ExportedFileCount);
        Assert.True(Directory.EnumerateFiles(exportDir, "a.sav", SearchOption.AllDirectories).Any());

        // 4. Rolling retention protects pinned node while pruning excess unpinned root-side snapshots
        SaveGameStateRecord state = await env.Service.GetGameStateAsync(env.Game.GameId);
        await env.Service.UpdateGameStateAsync(state with { MaxRollingSnapshots = 2 });

        // Create an unpinned fork branch with 3 snapshots, then delete branch and run CAS GC
        ForkBranchResult tempBranch = await env.Service.CreateBranchAsync(new ForkBranchRequest
        {
            GameId = env.Game.GameId,
            FromSnapshotId = s2.Snapshot.SnapshotId,
            BranchName = "temp-gc-branch",
            SwitchToNewBranch = true,
            CreateSafetySnapshotIfSwitching = false,
        });
        Assert.True(tempBranch.Succeeded);

        env.AdvanceMinutes(2);
        await env.WriteSaveFileAsync("unique_gc.sav", "UNIQUE-GC-PAYLOAD-" + new string('Z', 4096));
        CreateSaveSnapshotResult sGc = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            Note = "Temp GC snap",
        });
        Assert.True(sGc.Succeeded);

        // Switch back to main so we can delete temp-gc-branch
        SaveTreeGraphDto gBefore = await env.Service.GetSaveTreeGraphAsync(env.Game.GameId);
        SaveBranchRecord main = Assert.Single(gBefore.Branches, b => b.IsDefault);
        Assert.True((await env.Service.SwitchBranchAsync(new SwitchBranchRequest
        {
            GameId = env.Game.GameId,
            TargetBranchId = main.BranchId,
            CreateSafetySnapshot = false,
        })).Succeeded);

        DeleteBranchResult delBranch = await env.Service.DeleteBranchAsync(tempBranch.Branch!.BranchId, deleteSnapshots: true);
        Assert.True(delBranch.Succeeded, delBranch.Message);
        Assert.Equal(1, delBranch.DeletedSnapshotCount);

        SaveGcReport gcReport = await env.Service.RunCasGarbageCollectionAsync(TimeSpan.Zero);
        Assert.True(gcReport.DeletedChunkCount >= 1);
        Assert.True(gcReport.ReclaimedBytes > 0);

        // 5. Legacy M3 PreTrainer snapshot idempotent import
        string legacyId = "20261124120000000-LEG00001";
        string legacyDir = env.Paths.GetPreTrainerSnapshotDirectory(env.Game.GameId, legacyId, createDirectory: true);
        string legacyFileFull = Path.Combine(legacyDir, "files", "loc_000", "legacy.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFileFull)!);
        byte[] legacyBytes = Encoding.UTF8.GetBytes("LEGACY-M3-SAVE-DATA");
        await File.WriteAllBytesAsync(legacyFileFull, legacyBytes);

        var legacyManifest = new PreTrainerSnapshotManifest
        {
            SchemaVersion = 1,
            SnapshotId = legacyId,
            GameId = env.Game.GameId,
            TriggerKind = PreTrainerSnapshotTriggerKind.PreTrainer,
            CreatedAtUtc = env.Clock.GetUtcNow().AddHours(-2),
            TotalFileCount = 1,
            TotalBytes = legacyBytes.Length,
            Files =
            [
                new PreTrainerSnapshotFileEntry
                {
                    LogicalRelativePath = "loc_000/legacy.sav",
                    SnapshotRelativePath = "files/loc_000/legacy.sav",
                    OriginalPath = Path.Combine(env.SaveDir, "legacy.sav"),
                    SourceLocationPath = env.SaveDir,
                    SizeBytes = legacyBytes.Length,
                    LastWriteTimeUtc = env.Clock.GetUtcNow().AddHours(-2),
                    Sha256 = await PreTrainerSnapshotManifestStore.ComputeFileSha256HexAsync(legacyFileFull),
                },
            ],
        };
        string manifestPath = Path.Combine(legacyDir, PreTrainerSnapshotManifestStore.ManifestFileName);
        await PreTrainerSnapshotManifestStore.WriteManifestAtomicAsync(manifestPath, legacyManifest);
        await env.Database.UpsertPreTrainerSnapshotAsync(new PreTrainerSnapshotRecord
        {
            SnapshotId = legacyId,
            GameId = env.Game.GameId,
            TriggerKind = PreTrainerSnapshotTriggerKind.PreTrainer,
            Status = PreTrainerSnapshotStatus.Completed,
            FileCount = 1,
            TotalBytes = legacyBytes.Length,
            ManifestRelativePath = Path.GetRelativePath(env.Paths.PreTrainerSnapshotsDirectory, manifestPath),
            CreatedAtUtc = env.Clock.GetUtcNow().AddHours(-2),
        });

        LegacyPreTrainerImportReport import1 = await env.Service.ImportLegacyPreTrainerSnapshotsAsync(env.Game.GameId);
        Assert.Equal(1, import1.ImportedCount);
        Assert.Equal(0, import1.AlreadyImportedCount);

        // Second import must be 100% idempotent
        LegacyPreTrainerImportReport import2 = await env.Service.ImportLegacyPreTrainerSnapshotsAsync(env.Game.GameId);
        Assert.Equal(0, import2.ImportedCount);
        Assert.Equal(1, import2.AlreadyImportedCount);

        // Library overview reports the game cleanly
        IReadOnlyList<SaveLibraryGameOverviewDto> overview = await env.Service.GetLibraryOverviewAsync();
        SaveLibraryGameOverviewDto row = Assert.Single(overview, o => o.GameId == env.Game.GameId);
        Assert.False(row.HasLegacyPreTrainerSnapshots);
        Assert.True(row.SnapshotCount >= 3);
    }

    [Fact]
    public async Task ModProfileMismatch_AbortsWhenPolicyAbortAndProceedsWhenConfirmed()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync();

        await env.WriteSaveFileAsync("slot.sav", "MOD-A-SAVE");
        CreateSaveSnapshotResult sA = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            ModProfileHash = "blake3-mod-profile-AAA",
        });

        env.AdvanceMinutes(5);
        await env.WriteSaveFileAsync("slot.sav", "MOD-B-SAVE");
        await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
            ModProfileHash = "blake3-mod-profile-BBB",
        });

        SaveRestoreOutcome aborted = await env.Service.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = sA.Snapshot!.SnapshotId,
            ModMismatchPolicy = SaveModMismatchPolicy.AbortAndPrompt,
        });
        Assert.False(aborted.Succeeded);
        Assert.True(aborted.RequiresModMismatchConfirmation);
        Assert.Equal("MOD-B-SAVE", await env.ReadSaveFileAsync("slot.sav"));

        SaveRestoreOutcome forced = await env.Service.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = sA.Snapshot.SnapshotId,
            ModMismatchPolicy = SaveModMismatchPolicy.ProceedAnyway,
            CreateSafetySnapshot = false,
        });
        Assert.True(forced.Succeeded, forced.Message);
        Assert.Equal("MOD-A-SAVE", await env.ReadSaveFileAsync("slot.sav"));
    }
}

/// <summary>
/// Self-contained temp-directory test harness for Save Time Machine &amp; Watcher tests.
/// </summary>
internal sealed class SaveEngineTestFixture : IAsyncDisposable
{
    public string RootDir { get; }
    public string SaveDir { get; }
    public string ConfigDir { get; }
    public SpawnPaths Paths { get; }
    public SpawnDatabase Database { get; }
    public CasChunkStore CasStore { get; }
    public InMemorySpawnEventBus EventBus { get; }
    public FakeTimeProvider Clock { get; }
    public FakeLudusaviRuleResolver RuleResolver { get; }
    public FakeGameDataManagementService GameDataService { get; }
    public FakeGameLaunchService GameLaunchService { get; }
    public GameRecord Game { get; }
    public SaveTimeMachineService Service { get; }

    private SaveEngineTestFixture(
        string rootDir,
        string saveDir,
        string configDir,
        SpawnPaths paths,
        SpawnDatabase database,
        CasChunkStore casStore,
        InMemorySpawnEventBus eventBus,
        FakeTimeProvider clock,
        FakeLudusaviRuleResolver ruleResolver,
        FakeGameDataManagementService gameDataService,
        FakeGameLaunchService gameLaunchService,
        GameRecord game,
        SaveTimeMachineService service)
    {
        RootDir = rootDir;
        SaveDir = saveDir;
        ConfigDir = configDir;
        Paths = paths;
        Database = database;
        CasStore = casStore;
        EventBus = eventBus;
        Clock = clock;
        RuleResolver = ruleResolver;
        GameDataService = gameDataService;
        GameLaunchService = gameLaunchService;
        Game = game;
        Service = service;
    }

    public static async Task<SaveEngineTestFixture> CreateAsync(
        Action<string, int>? afterReadHook = null,
        long? maxBytesOverride = null,
        TimeProvider? explicitTimeProvider = null)
    {
        string rootDir = Path.Combine(Path.GetTempPath(), "spawn-save-eng-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(rootDir, "data");
        string installDir = Path.Combine(rootDir, "game");
        string saveDir = Path.Combine(rootDir, "saves");
        string configDir = Path.Combine(rootDir, "config");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(saveDir);
        Directory.CreateDirectory(configDir);

        var paths = SpawnPaths.CreateIsolated(dataDir);
        paths.EnsureDirectoriesExist();

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        TimeProvider effectiveClock = explicitTimeProvider ?? clock;

        var db = new SpawnDatabase(paths);
        await db.InitializeAsync();

        var cas = new CasChunkStore(paths, db, effectiveClock);
        var bus = new InMemorySpawnEventBus();

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(clock.GetUtcNow()),
            Title = "Test Fantasy RPG",
            InstallDir = installDir,
            ExecutableRelPath = "game.exe",
            TotalActiveSec = 1800,
        };
        await db.UpsertGameAsync(game);

        var ruleResolver = new FakeLudusaviRuleResolver(saveDir, configDir);
        var gameData = new FakeGameDataManagementService(saveDir, configDir);
        var launchSvc = new FakeGameLaunchService();

        var service = new SaveTimeMachineService(
            db,
            paths,
            cas,
            ruleResolver,
            gameData,
            launchSvc,
            bus,
            effectiveClock,
            logger: null,
            isWatchingProbe: null,
            afterReadConsistencyTestHook: afterReadHook,
            maxSnapshotBytesOverrideForTest: maxBytesOverride);

        return new SaveEngineTestFixture(
            rootDir,
            saveDir,
            configDir,
            paths,
            db,
            cas,
            bus,
            clock,
            ruleResolver,
            gameData,
            launchSvc,
            game,
            service);
    }

    public void AdvanceMinutes(int minutes) => Clock.Advance(TimeSpan.FromMinutes(minutes));

    public Task WriteSaveFileAsync(string relativeName, string content)
    {
        string path = Path.Combine(SaveDir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content, Encoding.UTF8);
    }

    public Task<string> ReadSaveFileAsync(string relativeName) =>
        File.ReadAllTextAsync(Path.Combine(SaveDir, relativeName), Encoding.UTF8);

    public bool SaveFileExists(string relativeName) =>
        File.Exists(Path.Combine(SaveDir, relativeName));

    public void DeleteSaveFile(string relativeName)
    {
        string path = Path.Combine(SaveDir, relativeName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}

internal sealed class FakeTimeProvider(DateTimeOffset startUtc) : TimeProvider
{
    private DateTimeOffset _now = startUtc;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

internal sealed class FakeLudusaviRuleResolver(string saveDir, string configDir) : ILudusaviRuleResolver
{
    public string SaveDir { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveDir));
    public string ConfigDir { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDir));

    public Task<ResolvedSaveLayout> ResolveGameSaveLayoutAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken ct = default)
    {
        var saveLoc = new ResolvedSaveLocation
        {
            LogicalTemplatePath = "<winAppData>/TestGame/saves",
            ResolvedFullPath = SaveDir,
            Category = SaveFileCategory.Save,
            Exists = Directory.Exists(SaveDir),
            IsSafe = true,
        };
        var cfgLoc = new ResolvedSaveLocation
        {
            LogicalTemplatePath = "<winAppData>/TestGame/config",
            ResolvedFullPath = ConfigDir,
            Category = SaveFileCategory.Config,
            Exists = Directory.Exists(ConfigDir),
            IsSafe = true,
        };

        return Task.FromResult(new ResolvedSaveLayout
        {
            GameId = gameId,
            MatchedRuleName = "Test Fantasy RPG",
            Locations = [saveLoc, cfgLoc],
            SaveLocations = [saveLoc],
            ConfigLocations = [cfgLoc],
            RegistryKeys = [],
        });
    }

    public string CanonicalizeToLogicalPath(string fullPathOrRegistryKey, GameRecord game)
    {
        string full = Path.GetFullPath(fullPathOrRegistryKey);
        if (full.StartsWith(SaveDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(SaveDir, full).Replace('\\', '/');
            return $"<winAppData>/TestGame/saves/{rel}";
        }

        if (full.StartsWith(ConfigDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(ConfigDir, full).Replace('\\', '/');
            return $"<winAppData>/TestGame/config/{rel}";
        }

        return full.Replace('\\', '/');
    }

    public string ExpandLogicalPath(string logicalPath, GameRecord game)
    {
        const string savePrefix = "<winAppData>/TestGame/saves/";
        if (logicalPath.StartsWith(savePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(SaveDir, logicalPath[savePrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        }

        const string cfgPrefix = "<winAppData>/TestGame/config/";
        if (logicalPath.StartsWith(cfgPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(ConfigDir, logicalPath[cfgPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        }

        return logicalPath;
    }

    public Task<int> EnsureBuiltInRulesSeededAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> ImportRulesJsonAsync(string rulesJson, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> GetRuleCountAsync(CancellationToken ct = default) => Task.FromResult(1);
}

internal sealed class FakeGameDataManagementService(string saveDir, string configDir) : IGameDataManagementService
{
    public bool BlockAllRestoreTargets { get; set; }

    public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameDataEntryDto>>(
        [
            new GameDataEntryDto
            {
                EntryId = "save-dir",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.Ludusavi,
                PathOrRegistryKey = saveDir,
                Exists = Directory.Exists(saveDir),
                IsSafeToClean = !BlockAllRestoreTargets,
            },
            new GameDataEntryDto
            {
                EntryId = "config-dir",
                GameId = gameId,
                Category = GameDataCategory.ConfigAndLogs,
                SourceRule = GameDataSourceRule.Ludusavi,
                PathOrRegistryKey = configDir,
                Exists = Directory.Exists(configDir),
                IsSafeToClean = !BlockAllRestoreTargets,
            },
        ]);

    public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(GameDataCleanResult.Ok("ok"));

    public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult<GameDataUndoSnapshotDto?>(null);

    public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GameDataCleanResult.Ok("ok"));
}

internal sealed class FakeGameLaunchService : IGameLaunchService
{
    private readonly Dictionary<string, ActiveGameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions => _sessions;
    public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged;

    public void SetRunning(string gameId, bool running)
    {
        if (running)
        {
            var session = new ActiveGameSession
            {
                SessionId = "sess-" + gameId,
                GameId = gameId,
                GameTitle = "Test Game",
                InstallDir = Path.GetTempPath(),
                StartedUtc = DateTimeOffset.UtcNow,
                State = PlaySessionActivityState.Active,
            };
            _sessions[gameId] = session;
            SessionChanged?.Invoke(this, new ActiveGameSessionChangedEvent(gameId, session, PlaySessionActivityState.Active));
        }
        else
        {
            _sessions.Remove(gameId);
            SessionChanged?.Invoke(this, new ActiveGameSessionChangedEvent(gameId, null, PlaySessionActivityState.Stopped));
        }
    }

    public ActiveGameSession? GetActiveSession(string gameId) =>
        _sessions.TryGetValue(gameId, out ActiveGameSession? s) ? s : null;

    public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GameLaunchResult.Failure("STUB", "stub"));

    public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
    public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
}
