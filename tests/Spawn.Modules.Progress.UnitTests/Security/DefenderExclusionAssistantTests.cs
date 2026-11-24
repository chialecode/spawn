using Microsoft.Extensions.DependencyInjection;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress.Runtime;
using Spawn.Modules.Progress.Security;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Security;

public sealed class DefenderExclusionAssistantTests
{
    [Fact]
    public async Task ScopeEnforcement_RejectsGameDirsBroadRootsDriveRootsTraversalAndNonAscii_ZeroUacCalls()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string gameDir = Path.Combine(harness.RootDir, "Games", "EldenRing");
        Directory.CreateDirectory(gameDir);

        await harness.Database.UpsertGameAsync(new GameRecord
        {
            GameId = "game-scope-test",
            Title = "Elden Ring",
            InstallDir = gameDir,
            ExecutableRelPath = "eldenring.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        });

        var probe = new FakeDefenderProbe(new DefenderEnvironmentProbeSnapshot
        {
            CanReadExclusionPaths = true,
            CurrentExclusionPaths = [],
        });
        var executor = new FakeElevatedDefenderExecutor();
        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] forbiddenPaths =
        [
            gameDir,
            Path.Combine(gameDir, "SubFolder"),
            Path.Combine(harness.RootDir, "Games"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Downloads", "Trainers"),
            @"C:\",
            Path.Combine(harness.Paths.TrainerSandboxDirectory, "..", "Snapshots"),
            harness.Paths.DataRoot,
            harness.Paths.VaultDirectory,
            Path.Combine(harness.RootDir, "ArbitraryOtherFolder"),
        ];

        foreach (string forbidden in forbiddenPaths)
        {
            DefenderExclusionStatusReport inspect = await assistant.InspectStatusAsync(forbidden);
            Assert.Equal(DefenderExclusionStatus.Failed, inspect.Status);
            Assert.False(inspect.IsExcluded);

            DefenderExclusionStatusReport request = await assistant.RequestExclusionAsync(forbidden);
            Assert.Equal(DefenderExclusionStatus.Failed, request.Status);
            Assert.False(request.IsExcluded);
        }

        // Custom pure-ASCII sandbox validator must reject non-ASCII paths and game directories.
        string nonAscii = Path.Combine(harness.RootDir, "修改器沙箱");
        Assert.False(DefenderExclusionAssistant.TryValidateCustomAsciiSandboxRoot(nonAscii, harness.Paths, [gameDir], out _, out string? nonAsciiReason));
        Assert.Contains("ASCII", nonAsciiReason, StringComparison.OrdinalIgnoreCase);

        Assert.False(DefenderExclusionAssistant.TryValidateCustomAsciiSandboxRoot(gameDir, harness.Paths, [gameDir], out _, out _));

        // Zero UAC / WMI execution attempts on any rejected path!
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task AlreadyExcluded_ReturnsAlreadyExcludedWithoutPromptingUac()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string sandboxRoot = harness.Paths.TrainerSandboxDirectory;

        var probe = new FakeDefenderProbe(new DefenderEnvironmentProbeSnapshot
        {
            CanReadExclusionPaths = true,
            CurrentExclusionPaths = [sandboxRoot + Path.DirectorySeparatorChar],
            SmartAppControlState = SmartAppControlMode.Off,
        });
        var executor = new FakeElevatedDefenderExecutor();
        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);

        DefenderExclusionStatusReport inspected = await assistant.InspectStatusAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.AlreadyExcluded, inspected.Status);
        Assert.True(inspected.IsExcluded);

        DefenderExclusionStatusReport requested = await assistant.RequestExclusionAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.AlreadyExcluded, requested.Status);
        Assert.True(requested.IsExcluded);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task RequestExclusion_SucceedsOnlyAfterMandatoryReadbackVerification_SupportsConfiguredAsciiCustomRoot()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string customSandbox = Path.Combine(harness.RootDir, "CustomAsciiSandbox", "Trainers");

        Assert.True(DefenderExclusionAssistant.TryValidateCustomAsciiSandboxRoot(customSandbox, harness.Paths, [], out string normalizedCustom, out _));
        await harness.Database.SaveTrainerSettingsAsync(new TrainerSettingsDto
        {
            CustomSandboxRoot = normalizedCustom,
        });

        bool excludedOnSystem = false;
        var probe = new FakeDefenderProbe(() => new DefenderEnvironmentProbeSnapshot
        {
            CanReadExclusionPaths = true,
            CurrentExclusionPaths = excludedOnSystem ? [normalizedCustom] : [],
            TamperProtectionExclusionsProtected = false,
            IsTamperProtectionEnabled = false,
            SmartAppControlState = SmartAppControlMode.Off,
        });

        var executor = new FakeElevatedDefenderExecutor(path =>
        {
            excludedOnSystem = true;
            return new TrainerAddDefenderExclusionResponse
            {
                Succeeded = true,
                VerifiedByReadback = true,
                Status = DefenderExclusionStatus.AddedAndVerified,
                WmiReturnValue = 0,
            };
        });

        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);

        DefenderExclusionStatusReport report = await assistant.RequestExclusionAsync(normalizedCustom);
        Assert.Equal(DefenderExclusionStatus.AddedAndVerified, report.Status);
        Assert.True(report.IsExcluded);
        Assert.Equal(normalizedCustom, report.SandboxPath);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(normalizedCustom, executor.LastRequestedPath);
    }

    [Fact]
    public async Task TamperProtection_PreReadTpExclusions1_BlocksWithoutWastingUacAndProvidesManualGuidance()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string sandboxRoot = harness.Paths.TrainerSandboxDirectory;

        var probe = new FakeDefenderProbe(new DefenderEnvironmentProbeSnapshot
        {
            TamperProtectionExclusionsProtected = true,
            IsTamperProtectionEnabled = true,
            CanReadExclusionPaths = true,
            CurrentExclusionPaths = [],
        });
        var executor = new FakeElevatedDefenderExecutor();
        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);

        DefenderExclusionStatusReport inspected = await assistant.InspectStatusAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.TamperProtectionBlocked, inspected.Status);
        Assert.True(inspected.IsTamperProtectionEnabled);
        Assert.Equal(DefenderExclusionAssistant.DefenderExclusionsSettingsUri, inspected.GuidanceUrl);

        DefenderExclusionStatusReport requested = await assistant.RequestExclusionAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.TamperProtectionBlocked, requested.Status);
        Assert.False(requested.IsExcluded);
        Assert.True(requested.IsTamperProtectionEnabled);
        Assert.Equal(DefenderExclusionAssistant.DefenderExclusionsSettingsUri, requested.GuidanceUrl);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task TamperProtection_SilentDropReadbackMissing_DetectsAndDegradesToTamperProtectionBlocked()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string sandboxRoot = harness.Paths.TrainerSandboxDirectory;

        // Pre-probe could not tell TPExclusions in advance, then WMI returned 0 but post-readback is missing!
        var probe = new FakeDefenderProbe(new DefenderEnvironmentProbeSnapshot
        {
            TamperProtectionExclusionsProtected = null,
            IsTamperProtectionEnabled = true,
            CanReadExclusionPaths = true,
            CurrentExclusionPaths = [],
        });

        var executor = new FakeElevatedDefenderExecutor(_ => new TrainerAddDefenderExclusionResponse
        {
            Succeeded = true,
            VerifiedByReadback = true, // Even if worker claimed success, host post-readback catches missing path!
            Status = DefenderExclusionStatus.AddedAndVerified,
            WmiReturnValue = 0,
        });

        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);

        DefenderExclusionStatusReport report = await assistant.RequestExclusionAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.TamperProtectionBlocked, report.Status);
        Assert.False(report.IsExcluded);
        Assert.True(report.IsTamperProtectionEnabled);
        Assert.Equal(DefenderExclusionAssistant.DefenderExclusionsSettingsUri, report.GuidanceUrl);
        Assert.Equal(1, executor.CallCount);

        // Also verify when worker itself returns WmiReturnValue=0 + VerifiedByReadback=false (Event 5013 silent drop).
        var silentDropExecutor = new FakeElevatedDefenderExecutor(_ => new TrainerAddDefenderExclusionResponse
        {
            Succeeded = false,
            VerifiedByReadback = false,
            Status = DefenderExclusionStatus.VerificationReadbackFailed,
            WmiReturnValue = 0,
            ErrorMessage = "Missing on readback",
        });
        var assistant2 = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, silentDropExecutor);
        DefenderExclusionStatusReport report2 = await assistant2.RequestExclusionAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.TamperProtectionBlocked, report2.Status);
        Assert.False(report2.IsExcluded);
    }

    [Fact]
    public async Task ThirdPartyAntivirusPassiveMode_DegradesCleanlyWithoutUac()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string sandboxRoot = harness.Paths.TrainerSandboxDirectory;

        var probe = new FakeDefenderProbe(new DefenderEnvironmentProbeSnapshot
        {
            ManagedDefenderProductType = 6,
            IsThirdPartyPassiveMode = true,
            IsDefenderServiceAvailable = true,
            CanReadExclusionPaths = false,
        });
        var executor = new FakeElevatedDefenderExecutor();
        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);

        DefenderExclusionStatusReport report = await assistant.RequestExclusionAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode, report.Status);
        Assert.True(report.IsDefenderPassiveMode);
        Assert.False(report.IsExcluded);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task SmartAppControlEnforced_ExplainsTransparentlyWithMicrosoftLink_NeverAttemptsDisableOrUac()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string sandboxRoot = harness.Paths.TrainerSandboxDirectory;

        var probe = new FakeDefenderProbe(new DefenderEnvironmentProbeSnapshot
        {
            SmartAppControlState = SmartAppControlMode.Enforced,
            IsDefenderServiceAvailable = true,
            CanReadExclusionPaths = true,
            CurrentExclusionPaths = [],
        });
        var executor = new FakeElevatedDefenderExecutor();
        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);

        DefenderExclusionStatusReport inspect = await assistant.InspectStatusAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.SmartAppControlNotice, inspect.Status);
        Assert.True(inspect.IsSmartAppControlEnforced);
        Assert.Equal(DefenderExclusionAssistant.SmartAppControlLearnMoreUrl, inspect.GuidanceUrl);

        DefenderExclusionStatusReport request = await assistant.RequestExclusionAsync(sandboxRoot);
        Assert.Equal(DefenderExclusionStatus.SmartAppControlNotice, request.Status);
        Assert.True(request.IsSmartAppControlEnforced);
        Assert.False(request.IsExcluded);
        Assert.Equal(DefenderExclusionAssistant.SmartAppControlLearnMoreUrl, request.GuidanceUrl);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task UserCancelsUac_ReturnsUacCancelledByUserWithoutRetry()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string sandboxRoot = harness.Paths.TrainerSandboxDirectory;

        var probe = new FakeDefenderProbe(new DefenderEnvironmentProbeSnapshot
        {
            CanReadExclusionPaths = false,
            SmartAppControlState = SmartAppControlMode.Off,
            TamperProtectionExclusionsProtected = false,
        });
        var executor = new FakeElevatedDefenderExecutor(_ => new TrainerAddDefenderExclusionResponse
        {
            Succeeded = false,
            VerifiedByReadback = false,
            Status = DefenderExclusionStatus.UacCancelledByUser,
            ErrorMessage = "User cancelled UAC (1223).",
        });

        var assistant = new DefenderExclusionAssistant(harness.Paths, harness.Database, probe, executor);
        DefenderExclusionStatusReport report = await assistant.RequestExclusionAsync(sandboxRoot);

        Assert.Equal(DefenderExclusionStatus.UacCancelledByUser, report.Status);
        Assert.False(report.IsExcluded);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task SandboxMigration_MovesTrainerSubfoldersWhileSkippingStagingAndPreservingFiles()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string oldRoot = harness.Paths.TrainerSandboxDirectory;
        string newRoot = Path.Combine(harness.RootDir, "MigratedAscii", "Trainers");

        string trainerDir = Path.Combine(oldRoot, "game-01", "trainer-01");
        Directory.CreateDirectory(trainerDir);
        await File.WriteAllTextAsync(Path.Combine(trainerDir, "stub.txt"), "pinned-content");

        string stagingDir = Path.Combine(oldRoot, ".staging", "tmp-run");
        Directory.CreateDirectory(stagingDir);
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "partial.tmp"), "ignore");

        var (succeeded, movedCount, _) = await DefenderExclusionAssistant.MigrateSandboxContentsAsync(oldRoot, newRoot);

        Assert.True(succeeded);
        Assert.Equal(1, movedCount);
        Assert.True(File.Exists(Path.Combine(newRoot, "game-01", "trainer-01", "stub.txt")));
        Assert.Equal("pinned-content", await File.ReadAllTextAsync(Path.Combine(newRoot, "game-01", "trainer-01", "stub.txt")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, ".staging")));
    }

    [Fact]
    public async Task SandboxMigration_RejectsNonEmptyUserDestinationAndLeavesBothRootsUntouched()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string oldRoot = harness.Paths.TrainerSandboxDirectory;
        string userDir = Path.Combine(harness.RootDir, "UserTools");

        string trainerFile = Path.Combine(oldRoot, "game-01", "trainer-01", "stub.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(trainerFile)!);
        await File.WriteAllTextAsync(trainerFile, "pinned-content");

        string personalFile = Path.Combine(userDir, "MyScripts", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(personalFile)!);
        await File.WriteAllTextAsync(personalFile, "personal");

        var (succeeded, movedCount, message) = await DefenderExclusionAssistant.MigrateSandboxContentsAsync(oldRoot, userDir);

        Assert.False(succeeded);
        Assert.Equal(0, movedCount);
        Assert.Contains("空目录", message, StringComparison.Ordinal);
        Assert.True(File.Exists(trainerFile));
        Assert.Equal("personal", await File.ReadAllTextAsync(personalFile));
        Assert.False(Directory.Exists(Path.Combine(userDir, "game-01")));
    }

    [Fact]
    public async Task SandboxMigration_ToSpawnOwnedRootNeverOverwritesCollisionsAndKeepsSourceInPlace()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();
        string customRoot = Path.Combine(harness.RootDir, "CustomAscii", "Trainers");
        string defaultRoot = harness.Paths.TrainerSandboxDirectory;

        string movable = Path.Combine(customRoot, "game-01", "trainer-01", "stub.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(movable)!);
        await File.WriteAllTextAsync(movable, "movable");

        string colliding = Path.Combine(customRoot, "game-02", "trainer-02", "stub.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(colliding)!);
        await File.WriteAllTextAsync(colliding, "custom-copy");
        string existingInDefault = Path.Combine(defaultRoot, "game-02", "trainer-02", "stub.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existingInDefault)!);
        await File.WriteAllTextAsync(existingInDefault, "default-copy");

        var (succeeded, movedCount, message) = await DefenderExclusionAssistant.MigrateSandboxContentsAsync(
            customRoot, defaultRoot, destinationIsSpawnOwnedDefaultRoot: true);

        Assert.True(succeeded);
        Assert.Equal(1, movedCount);
        Assert.Equal("movable", await File.ReadAllTextAsync(Path.Combine(defaultRoot, "game-01", "trainer-01", "stub.txt")));
        Assert.False(Directory.Exists(Path.Combine(customRoot, "game-01")));
        Assert.Equal("default-copy", await File.ReadAllTextAsync(existingInDefault));
        Assert.Equal("custom-copy", await File.ReadAllTextAsync(colliding));
        Assert.Contains("game-02", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddSpawnProgress_RegistersFullServiceGraphWithZeroCircularDependencies()
    {
        await using var harness = await SecurityTestHarness.CreateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new SingleInstanceCoordinator(harness.Paths));
        services.AddSpawnCore(harness.Paths);
        services.AddSingleton<IGameLaunchService>(new StubGameLaunchService());
        services.AddSingleton<IGameDataManagementService>(new StubGameDataService());
        services.AddSpawnProgress();

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(sp.GetRequiredService<ITrainerCatalogService>());
        Assert.NotNull(sp.GetRequiredService<ITrainerAudioController>());
        Assert.NotNull(sp.GetRequiredService<IPreTrainerSaveSnapshotService>());
        Assert.NotNull(sp.GetRequiredService<ITrainerBackend>());
        Assert.NotNull(sp.GetRequiredService<ITrainerRuntimeService>());
        Assert.NotNull(sp.GetRequiredService<IDefenderExclusionAssistant>());

        var hooks = sp.GetServices<ISensitiveWorkerShutdownHook>().ToList();
        Assert.NotEmpty(hooks);
    }

    private sealed class FakeDefenderProbe : IDefenderEnvironmentProbe
    {
        private readonly Func<DefenderEnvironmentProbeSnapshot> _factory;

        public FakeDefenderProbe(DefenderEnvironmentProbeSnapshot snapshot)
            : this(() => snapshot)
        {
        }

        public FakeDefenderProbe(Func<DefenderEnvironmentProbeSnapshot> factory) => _factory = factory;

        public Task<DefenderEnvironmentProbeSnapshot> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(_factory());
    }

    private sealed class FakeElevatedDefenderExecutor : IElevatedDefenderExecutor
    {
        private readonly Func<string, TrainerAddDefenderExclusionResponse> _handler;

        public int CallCount { get; private set; }
        public string? LastRequestedPath { get; private set; }

        public FakeElevatedDefenderExecutor(Func<string, TrainerAddDefenderExclusionResponse>? handler = null)
        {
            _handler = handler ?? (_ => new TrainerAddDefenderExclusionResponse
            {
                Succeeded = true,
                VerifiedByReadback = true,
                Status = DefenderExclusionStatus.AddedAndVerified,
                WmiReturnValue = 0,
            });
        }

        public Task<TrainerAddDefenderExclusionResponse> AddSandboxExclusionAsync(string sandboxPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequestedPath = sandboxPath;
            return Task.FromResult(_handler(sandboxPath));
        }
    }

    private sealed class SecurityTestHarness : IAsyncDisposable
    {
        public string RootDir { get; }
        public SpawnPaths Paths { get; }
        public SpawnDatabase Database { get; }

        private SecurityTestHarness(string rootDir, SpawnPaths paths, SpawnDatabase database)
        {
            RootDir = rootDir;
            Paths = paths;
            Database = database;
        }

        public static async Task<SecurityTestHarness> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"spawn-w08-sec-{Guid.NewGuid():N}");
            string dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);
            var paths = SpawnPaths.CreateIsolated(dataDir);
            paths.EnsureDirectoriesExist();
            var db = new SpawnDatabase(paths);
            await db.InitializeAsync();
            return new SecurityTestHarness(root, paths, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(RootDir))
                {
                    Directory.Delete(RootDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class StubGameLaunchService : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
        {
            add { }
            remove { }
        }

        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameLaunchResult.Failure("STUB", "stub"));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }

    private sealed class StubGameDataService : IGameDataManagementService
    {
        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(string gameId, bool refreshOnline = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameDataEntryDto>>([]);
        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDataCleanResult { Succeeded = false, Message = "stub" });
        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);
        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDataCleanResult { Succeeded = false, Message = "stub" });
    }
}
