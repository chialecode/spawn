using Microsoft.Data.Sqlite;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Ipc;
using Spawn.Modules.Progress.Runtime;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Runtime;

/// <summary>
/// Host-side <see cref="TrainerRuntimeService"/>, <see cref="ExternalTrainerBackend"/>,
/// <see cref="SensitiveWorkerShutdownHook"/> (LOOP-06), and <see cref="WorkerElevatedDefenderExecutor"/> tests.
/// 100% isolated temporary SQLite DB and sandbox; zero real UAC, zero real SendInput, zero real Defender writes.
/// </summary>
public sealed class TrainerRuntimeAndShutdownHookTests
{
    [Fact]
    public async Task LaunchAsync_FullHappyPath_ChainsGateVerifySnapshotAudioBackendAndWatchdog()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();

        var states = new List<TrainerRuntimeState>();
        env.Runtime.StateChanged += (_, e) => states.Add(e.CurrentState);

        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions
        {
            HotkeyHoldMs = 80,
            CreatePreTrainerSnapshot = true,
            StopWithGameExit = true,
        });

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.NotNull(outcome.Session);
        Assert.Equal(TrainerRuntimeState.Running, env.Runtime.State);
        Assert.Equal([env.TrainerId], env.Catalog.VerifiedIds);
        Assert.Equal(1, env.Snapshots.CreateCalls);
        Assert.Equal(1, env.Audio.PrepareCalls);
        Assert.Contains(outcome.Session!.TrainerProcessId, env.Audio.TrackedPids);
        Assert.Contains(outcome.Session.SessionId, env.SafetyGate.StartedSessions);

        Assert.Contains(TrainerRuntimeState.PreFlight, states);
        Assert.Contains(TrainerRuntimeState.Snapshotting, states);
        Assert.Contains(TrainerRuntimeState.Launching, states);
        Assert.Contains(TrainerRuntimeState.Running, states);

        // Stop cleanly
        await env.Runtime.StopAsync(outcome.Session.SessionId);
        Assert.Equal(TrainerRuntimeState.Ready, env.Runtime.State);
        Assert.Null(env.Runtime.ActiveSession);
        Assert.Contains(outcome.Session.SessionId, env.SafetyGate.EndedSessions);
    }

    [Fact]
    public async Task LaunchAsync_SafetyGateBlocked_RefusesLaunchWithoutCallingVerifyOrBackend()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        env.SafetyGate.NextDecision = new TrainerAllowanceDecision
        {
            Outcome = TrainerAllowanceOutcome.BlockedByLiveAntiCheat,
            IsAllowed = false,
            Message = "检测到运行中的反作弊守护进程。",
            StaticGameTier = AntiCheatTier.Tier0,
            GuardPhase = AntiCheatGuardPhase.IdleSafe,
        };

        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions());

        Assert.False(outcome.Succeeded);
        Assert.Equal(TrainerLaunchFailureKind.SafetyGateBlocked, outcome.FailureKind);
        Assert.Empty(env.Catalog.VerifiedIds);
        Assert.Equal(0, env.Backend.ActivateCalls);
        Assert.Equal(TrainerRuntimeState.Ready, env.Runtime.State);
    }

    [Fact]
    public async Task LaunchAsync_AntivirusQuarantine225_BlocksLaunchAndTransitionsToQuarantined()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        env.Catalog.NextIntegrityReport = new TrainerIntegrityReport
        {
            TrainerId = env.TrainerId,
            IsValid = false,
            FileExists = false,
            Win32ErrorCode = 225,
            FailureMessage = "文件已被杀软隔离 (ERROR_VIRUS_INFECTED)。",
        };

        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions());

        Assert.False(outcome.Succeeded);
        Assert.Equal(TrainerLaunchFailureKind.AntivirusQuarantined, outcome.FailureKind);
        Assert.Equal(225, outcome.Win32ErrorCode);
        Assert.Equal(TrainerRuntimeState.Quarantined, env.Runtime.State);
        Assert.Equal(0, env.Backend.ActivateCalls);
    }

    [Fact]
    public async Task LaunchAsync_PreTrainerSnapshotThrows_NeverBlocksTrainerLaunch()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        env.Snapshots.ThrowOnCreate = true;

        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions
        {
            CreatePreTrainerSnapshot = true,
        });

        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.PreSnapshot);
        Assert.Equal(PreTrainerSnapshotStatus.Failed, outcome.PreSnapshot!.Status);
        Assert.Equal(TrainerRuntimeState.Running, env.Runtime.State);
    }

    [Fact]
    public async Task ExternalTrainerBackend_UacCancelled1223_ReturnsUacCancelledWithoutRetrying()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        var cancellingLauncher = new FakeWorkerLauncher(new WorkerProcessLaunchResult(
            Succeeded: false,
            ElevationLevel: TrainerElevationLevel.Level2HighIlUac,
            WasCancelledByUser: true,
            Win32ErrorCode: 1223,
            ErrorMessage: "用户已取消 UAC 提权授权。"));

        await using var realBackend = new ExternalTrainerBackend(
            env.Paths,
            workerLauncher: cancellingLauncher,
            elevationAdvisor: new TrainerElevationAdvisor(new StaticManifestInspector(true), new StaticIntegrityProbe(0x2000)));

        await using var runtime = new TrainerRuntimeService(
            env.Database,
            env.Paths,
            env.SafetyGate,
            env.Catalog,
            env.Snapshots,
            env.Audio,
            realBackend,
            env.GameLaunch,
            new TrainerElevationAdvisor(new StaticManifestInspector(true), new StaticIntegrityProbe(0x2000)));

        TrainerLaunchOutcome outcome = await runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });

        Assert.False(outcome.Succeeded);
        Assert.Equal(TrainerLaunchFailureKind.UacCancelledByUser, outcome.FailureKind);
        Assert.Equal(1223, outcome.Win32ErrorCode);
        Assert.Equal(1, cancellingLauncher.LaunchAttempts); // Must NEVER retry UAC!
        Assert.Equal(TrainerElevationLevel.Level2HighIlUac, cancellingLauncher.LastRequestedLevel);
    }

    [Fact]
    public async Task TriggerHotkeyAndAudioFallback_ForwardsToBackendAndFlipsToggleProxyState()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions
        {
            HotkeyHoldMs = 95,
            ScanCodeMode = false,
            CreatePreTrainerSnapshot = false,
        });
        Assert.True(outcome.Succeeded);
        string sessionId = outcome.Session!.SessionId;

        await env.Runtime.TriggerHotkeyAsync(sessionId, "god-mode");

        Assert.Single(env.Backend.HotkeyRequests);
        TrainerTriggerHotkeyRequest sent = env.Backend.HotkeyRequests[0];
        Assert.Equal((ushort)0x0D, sent.VirtualKey); // Alt+Num Enter
        Assert.True(sent.IsExtendedKeyOverride);
        Assert.Equal([HotkeyChordParser.VkMenuAlt], sent.Modifiers);
        Assert.Equal(95, sent.HoldMsOverride);

        // ProxyState flipped in SQLite for Toggle entry
        var hotkeys = await env.Database.GetTrainerHotkeyBindingsAsync(env.TrainerId);
        Assert.True(hotkeys.Single(h => h.EntryId == "god-mode").ProxyState);

        // Audio controller E_ACCESSDENIED fallback forwarded to backend
        await env.Audio.RaiseFallbackRequiredAsync([5555, 5556], mute: true, volume: 0.0);
        Assert.Single(env.Backend.AudioFallbackCalls);
        Assert.Equal([5555, 5556], env.Backend.AudioFallbackCalls[0].Pids);
        Assert.True(env.Backend.AudioFallbackCalls[0].Mute);
    }

    [Fact]
    public async Task WatchdogEmergencyStop_ImmediatelyDestroysActiveSessionAndUpdatesState()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        var states = new List<TrainerRuntimeState>();
        var readyAfterEmergency = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });
        Assert.True(outcome.Succeeded);

        env.Runtime.StateChanged += (_, e) =>
        {
            states.Add(e.CurrentState);
            if (e.CurrentState == TrainerRuntimeState.Ready && states.Contains(TrainerRuntimeState.EmergencyStopped))
            {
                readyAfterEmergency.TrySetResult(true);
            }
        };

        env.SafetyGate.RaiseEmergencyStop(new TrainerEmergencyStopEvent
        {
            Reason = "检测到受保护网游反作弊进程 SGuard64.exe",
            DetectedProcesses = ["SGuard64.exe"],
        });

        Assert.True(await readyAfterEmergency.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(env.Runtime.ActiveSession);
        Assert.Contains(TrainerShutdownReason.AntiCheatWatchdogEmergency, env.Backend.ShutdownReasons);
        Assert.Contains(outcome.Session!.SessionId, env.SafetyGate.EndedSessions);
    }

    [Fact]
    public async Task GameSessionStopped_RespectsStopWithGameExitSetting()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        var stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions
        {
            CreatePreTrainerSnapshot = false,
            StopWithGameExit = true,
        });
        Assert.True(outcome.Succeeded);

        env.Runtime.StateChanged += (_, e) =>
        {
            if (e.CurrentState == TrainerRuntimeState.Ready)
            {
                stoppedTcs.TrySetResult(true);
            }
        };

        env.GameLaunch.RaiseSessionChanged(new ActiveGameSessionChangedEvent(
            env.GameId,
            Session: null,
            PlaySessionActivityState.Stopped));

        Assert.True(await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(env.Runtime.ActiveSession);
        Assert.Contains(TrainerShutdownReason.GameExited, env.Backend.StopReasons);
    }

    [Fact]
    public async Task SensitiveWorkerShutdownHook_Loop06_PurgesActiveSessionsAndSandboxOrphansAndVerifiesClean()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });
        Assert.True(outcome.Succeeded);

        var orphanScanner = new FakeSandboxProcessScanner(
            [new SafeProcessEntry(8888, 1, "rogue-trainer-child.exe", Path.Combine(env.Paths.TrainerSandboxDirectory, "rogue-trainer-child.exe"))]);

        var hook = new SensitiveWorkerShutdownHook(
            env.Runtime,
            env.Backend,
            env.Paths,
            env.Database,
            orphanScanner);

        SensitiveWorkerShutdownReport report = await hook.ShutdownAllForProtectedLaunchAsync(TimeSpan.FromSeconds(2));

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(1, report.TerminatedWorkerCount);
        Assert.Equal(1, report.TerminatedOrphanCount);
        Assert.Empty(report.RemainingSandboxProcesses);
        Assert.Contains(8888, orphanScanner.KilledPids);
        Assert.Null(env.Runtime.ActiveSession);
    }

    [Fact]
    public async Task SensitiveWorkerShutdownHook_Loop06_StubbornOrphan_ReportsFailureWithRemainingNames()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        var stubbornScanner = new FakeSandboxProcessScanner(
            [new SafeProcessEntry(9991, 1, "unkillable.exe", Path.Combine(env.Paths.TrainerSandboxDirectory, "unkillable.exe"))],
            allowTerminate: false);

        var hook = new SensitiveWorkerShutdownHook(
            env.Runtime,
            env.Backend,
            env.Paths,
            env.Database,
            stubbornScanner);

        SensitiveWorkerShutdownReport report = await hook.ShutdownAllForProtectedLaunchAsync(TimeSpan.FromMilliseconds(200));

        Assert.False(report.Succeeded);
        Assert.Single(report.RemainingSandboxProcesses);
        Assert.Contains("unkillable.exe", report.RemainingSandboxProcesses[0]);
    }

    [Fact]
    public async Task WorkerElevatedDefenderExecutor_RequestsLevel2AndHandlesUacCancelWithoutRetry()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        var cancellingLauncher = new FakeWorkerLauncher(new WorkerProcessLaunchResult(
            Succeeded: false,
            ElevationLevel: TrainerElevationLevel.Level2HighIlUac,
            WasCancelledByUser: true,
            Win32ErrorCode: 1223,
            ErrorMessage: "用户已取消 UAC 提权授权。"));

        var executor = new WorkerElevatedDefenderExecutor(env.Paths, cancellingLauncher);
        TrainerAddDefenderExclusionResponse resp = await executor.AddSandboxExclusionAsync(env.Paths.TrainerSandboxDirectory);

        Assert.False(resp.Succeeded);
        Assert.False(resp.VerifiedByReadback);
        Assert.Equal(DefenderExclusionStatus.UacCancelledByUser, resp.Status);
        Assert.Equal(1, cancellingLauncher.LaunchAttempts);
        Assert.Equal(TrainerElevationLevel.Level2HighIlUac, cancellingLauncher.LastRequestedLevel);
    }

    [Theory]
    [InlineData("ANTI_CHEAT_BLOCKED")]
    [InlineData("GUARD_LOCKED")]
    public async Task ExternalTrainerBackend_WorkerAntiCheatOrGuardRejection_ForwardsPolicyAndMapsToSafetyGateBlocked(string workerErrorCode)
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        TrainerLaunchRequest? capturedRequest = null;

        var inProcessLauncher = new InProcessRejectingWorkerLauncher(req =>
        {
            capturedRequest = req;
            return new TrainerLaunchResponse
            {
                Succeeded = false,
                ErrorCode = workerErrorCode,
                ErrorMessage = $"Worker rejected launch with {workerErrorCode}.",
            };
        });

        await using var realBackend = new ExternalTrainerBackend(
            env.Paths,
            workerLauncher: inProcessLauncher,
            elevationAdvisor: new TrainerElevationAdvisor(new StaticManifestInspector(false), new StaticIntegrityProbe(0x2000)));

        await using var runtime = new TrainerRuntimeService(
            env.Database,
            env.Paths,
            env.SafetyGate,
            env.Catalog,
            env.Snapshots,
            env.Audio,
            realBackend,
            env.GameLaunch,
            new TrainerElevationAdvisor(new StaticManifestInspector(false), new StaticIntegrityProbe(0x2000)));

        TrainerLaunchOutcome outcome = await runtime.LaunchAsync(
            env.TrainerId,
            new TrainerLaunchOptions
            {
                CreatePreTrainerSnapshot = false,
                StrictVanguardMode = true,
            });

        Assert.False(outcome.Succeeded);
        Assert.Equal(TrainerLaunchFailureKind.SafetyGateBlocked, outcome.FailureKind);
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.StrictVanguardMode);
        Assert.Equal(env.Paths.AntiCheatGuardStateFilePath, capturedRequest.GuardStateFilePath);
        await inProcessLauncher.WaitForCompletionAsync();
    }

    [Fact]
    public async Task LaunchAsync_ImmediateAudioFallbackDuringTrackProcess_SeesActiveSessionWithoutRace()
    {
        await using var env = await RuntimeTestHarness.CreateAsync();
        var fallbackDispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        env.Audio.OnTrackProcess = pid =>
        {
            // Simulate an immediate E_ACCESSDENIED fallback callback fired synchronously during TrackProcess!
            _ = env.Audio.RaiseFallbackRequiredAsync([pid], mute: true, volume: 0.0)
                .ContinueWith(_ => fallbackDispatched.TrySetResult(true), TaskScheduler.Default);
        };

        TrainerLaunchOutcome outcome = await env.Runtime.LaunchAsync(
            env.TrainerId,
            new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });

        Assert.True(outcome.Succeeded);
        Assert.True(await fallbackDispatched.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(env.Backend.AudioFallbackCalls);
        Assert.Equal([outcome.Session!.TrainerProcessId], env.Backend.AudioFallbackCalls[0].Pids);
    }

    private sealed class RuntimeTestHarness : IAsyncDisposable
    {
        public string RootDir { get; }
        public SpawnPaths Paths { get; }
        public SpawnDatabase Database { get; }
        public string GameId { get; } = "game-demo";
        public string TrainerId { get; } = "trainer-demo";
        public FakeSafetyGate SafetyGate { get; } = new();
        public FakeCatalogService Catalog { get; }
        public FakeSnapshotService Snapshots { get; } = new();
        public FakeAudioController Audio { get; } = new();
        public FakeTrainerBackend Backend { get; } = new();
        public FakeGameLaunchService GameLaunch { get; } = new();
        public TrainerRuntimeService Runtime { get; }

        private RuntimeTestHarness(string rootDir, SpawnPaths paths, SpawnDatabase db)
        {
            RootDir = rootDir;
            Paths = paths;
            Database = db;
            Catalog = new FakeCatalogService(db);
            Runtime = new TrainerRuntimeService(
                db,
                paths,
                SafetyGate,
                Catalog,
                Snapshots,
                Audio,
                Backend,
                GameLaunch,
                new TrainerElevationAdvisor(new StaticManifestInspector(false), new StaticIntegrityProbe(0x2000)));
        }

        public static async Task<RuntimeTestHarness> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"spawn-w05-rt-{Guid.NewGuid():N}");
            string dataDir = Path.Combine(root, "data");
            string gameDir = Path.Combine(root, "game");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(gameDir);

            var paths = SpawnPaths.CreateIsolated(dataDir);
            paths.EnsureDirectoriesExist();
            var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            var harness = new RuntimeTestHarness(root, paths, db);

            await db.UpsertGameAsync(new GameRecord
            {
                GameId = harness.GameId,
                Title = "Demo Singleplayer",
                InstallDir = gameDir,
                ExecutableRelPath = "Demo.exe",
                AntiCheatTier = AntiCheatTier.Tier0,
            });

            string trainerRel = Path.Combine(harness.GameId, harness.TrainerId, "DemoTrainer.exe");
            string trainerFull = Path.Combine(paths.TrainerSandboxDirectory, trainerRel);
            Directory.CreateDirectory(Path.GetDirectoryName(trainerFull)!);
            await File.WriteAllBytesAsync(trainerFull, [0x4D, 0x5A, 0x90, 0x00]);

            await db.UpsertTrainerAsync(new TrainerCatalogRecord
            {
                TrainerId = harness.TrainerId,
                GameId = harness.GameId,
                DisplayName = "Demo Trainer",
                SandboxRelativePath = trainerRel,
                FileSha256 = new string('b', 64),
                RequiresAdmin = false,
                ParseMode = TrainerParseMode.Structured,
            });

            await db.ReplaceTrainerHotkeyBindingsAsync(harness.TrainerId,
            [
                new TrainerHotkeyBindingRecord
                {
                    TrainerId = harness.TrainerId,
                    EntryId = "god-mode",
                    Label = "God Mode",
                    OriginalChord = "Alt+Num Enter",
                    EntryKind = TrainerHotkeyEntryKind.Toggle,
                    ProxyState = false,
                    SortOrder = 1,
                },
            ]);

            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    if (Directory.Exists(RootDir))
                    {
                        Directory.Delete(RootDir, recursive: true);
                    }

                    return;
                }
                catch
                {
                    Thread.Sleep(60);
                }
            }
        }
    }

    private sealed class FakeSafetyGate : ITrainerSafetyGate
    {
        public bool IsWatchdogActive => StartedSessions.Count > EndedSessions.Count;
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered;
        public List<string> StartedSessions { get; } = [];
        public List<string> EndedSessions { get; } = [];

        public TrainerAllowanceDecision NextDecision { get; set; } = new()
        {
            Outcome = TrainerAllowanceOutcome.Allowed,
            IsAllowed = true,
            Message = "Tier 0 单机安全放行。",
            StaticGameTier = AntiCheatTier.Tier0,
            GuardPhase = AntiCheatGuardPhase.IdleSafe,
        };

        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport { EffectiveTier = game.AntiCheatTier });

        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveAntiCheatProbeReport());

        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default) =>
            Task.FromResult(new VerifiedOfflineBypass { GameId = game.GameId, StaticAntiCheatTier = game.AntiCheatTier });

        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default) =>
            Task.FromResult(NextDecision);

        public void NotifyTrainerSessionStarted(string sessionId, string gameId) => StartedSessions.Add(sessionId);
        public void NotifyTrainerSessionEnded(string sessionId) => EndedSessions.Add(sessionId);
        public void RaiseEmergencyStop(TrainerEmergencyStopEvent e) => EmergencyStopTriggered?.Invoke(this, e);
    }

    private sealed class FakeCatalogService(SpawnDatabase db) : ITrainerCatalogService
    {
        public List<string> VerifiedIds { get; } = [];
        public TrainerIntegrityReport? NextIntegrityReport { get; set; }

        public Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerMatchResult { GameId = gameId, EffectiveQuery = "Demo" });

        public Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false });

        public Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false });

        public Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default)
        {
            VerifiedIds.Add(trainerId);
            return Task.FromResult(NextIntegrityReport ?? new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = true,
                FileExists = true,
                HashMatches = true,
                IsInsideSandbox = true,
            });
        }

        public Task RemoveAsync(string trainerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
            db.GetTrainerHotkeyBindingsAsync(trainerId, ct);
    }

    private sealed class FakeSnapshotService : IPreTrainerSaveSnapshotService
    {
        public int CreateCalls { get; private set; }
        public bool ThrowOnCreate { get; set; }

        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default)
        {
            CreateCalls++;
            if (ThrowOnCreate)
            {
                throw new IOException("Simulated sharing violation.");
            }

            return Task.FromResult(new PreTrainerSnapshotResult
            {
                Succeeded = true,
                Status = PreTrainerSnapshotStatus.Completed,
            });
        }

        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>([]);

        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerRestoreResult { Succeeded = true });
    }

    private sealed class FakeAudioController : ITrainerAudioController
    {
        public int PrepareCalls { get; private set; }
        public List<int> TrackedPids { get; } = [];
        public Action<int>? OnTrackProcess { get; set; }
        public event Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>? FallbackRequired;

        public Task PrepareAsync(TrainerAudioPolicy policy, string sandboxRoot, CancellationToken ct = default)
        {
            PrepareCalls++;
            return Task.CompletedTask;
        }

        public void TrackProcess(int pid)
        {
            TrackedPids.Add(pid);
            OnTrackProcess?.Invoke(pid);
        }
        public Task ApplyPolicyAsync(TrainerAudioPolicy policy, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task RaiseFallbackRequiredAsync(IReadOnlyList<int> pids, bool mute, double volume) =>
            FallbackRequired is not null ? FallbackRequired(pids, mute, volume, CancellationToken.None) : Task.CompletedTask;
    }

    private sealed class FakeTrainerBackend : ITrainerBackend, ITrainerBackendSessionEvents
    {
        private readonly HashSet<string> _active = [];

        public string BackendKind => ExternalTrainerBackend.Kind;
        public int ActivateCalls { get; private set; }
        public int ActiveWorkerCount => _active.Count;
        public List<TrainerTriggerHotkeyRequest> HotkeyRequests { get; } = [];
        public List<(IReadOnlyList<int> Pids, bool Mute, double Volume)> AudioFallbackCalls { get; } = [];
        public List<TrainerShutdownReason> StopReasons { get; } = [];
        public List<TrainerShutdownReason> ShutdownReasons { get; } = [];

        public event Action<TrainerProcessCreatedNotification>? ProcessCreated;
        public event Action<TrainerSessionEndedNotification>? SessionEnded;

        public IReadOnlyList<int> GetActiveWorkerProcessIds() => _active.Count > 0 ? [5001] : [];

        public Task<TrainerSessionHandle> ActivateSessionAsync(TrainerLaunchSpec spec, CancellationToken ct = default)
        {
            ActivateCalls++;
            _active.Add(spec.SessionId);
            return Task.FromResult(new TrainerSessionHandle
            {
                SessionId = spec.SessionId,
                GameId = spec.GameId,
                TrainerId = spec.TrainerId,
                BackendKind = BackendKind,
                ElevationLevel = spec.RequiresAdmin || spec.TargetGameIsElevated
                    ? TrainerElevationLevel.Level2HighIlUac
                    : TrainerElevationLevel.Level1MediumIl,
                WorkerProcessId = 5001,
                TrainerProcessId = 6001,
            });
        }

        public Task TriggerEntryAsync(string sessionId, string entryId, bool targetEnabled, CancellationToken ct = default) => Task.CompletedTask;

        public Task TriggerHotkeyAsync(string sessionId, TrainerTriggerHotkeyRequest request, CancellationToken ct = default)
        {
            HotkeyRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task ApplyAudioFallbackAsync(string sessionId, IReadOnlyList<int> pids, bool mute, double volume, CancellationToken ct = default)
        {
            AudioFallbackCalls.Add((pids, mute, volume));
            return Task.CompletedTask;
        }

        public Task StopSessionAsync(string sessionId, TrainerShutdownReason reason, CancellationToken ct = default)
        {
            StopReasons.Add(reason);
            _active.Remove(sessionId);
            _ = ProcessCreated;
            _ = SessionEnded;
            return Task.CompletedTask;
        }

        public Task ShutdownAllImmediatelyAsync(TrainerShutdownReason reason, CancellationToken ct = default)
        {
            ShutdownReasons.Add(reason);
            _active.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeGameLaunchService : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged;

        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameLaunchResult.Failure("UNUSED", "Unused in unit test."));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }

        public void RaiseSessionChanged(ActiveGameSessionChangedEvent e) => SessionChanged?.Invoke(this, e);
    }

    private sealed class FakeWorkerLauncher(WorkerProcessLaunchResult result) : ITrainerWorkerLauncher
    {
        public int LaunchAttempts { get; private set; }
        public TrainerElevationLevel LastRequestedLevel { get; private set; }

        public string? ResolveWorkerExecutablePath() => @"C:\Fake\Workers\Trainer\Spawn.TrainerWorker.exe";

        public Task<WorkerProcessLaunchResult> LaunchWorkerAsync(
            TrainerElevationLevel elevationLevel,
            string pipeName,
            string launchNonce,
            CancellationToken cancellationToken = default)
        {
            LaunchAttempts++;
            LastRequestedLevel = elevationLevel;
            return Task.FromResult(result);
        }

        public void TryTerminateWorkerProcess(int workerProcessId) { }
    }

    private sealed class FakeSandboxProcessScanner(
        IReadOnlyList<SafeProcessEntry> initialOrphans,
        bool allowTerminate = true) : ISandboxProcessScanner
    {
        private readonly List<SafeProcessEntry> _orphans = [.. initialOrphans];
        public List<int> KilledPids { get; } = [];

        public IReadOnlyList<SafeProcessEntry> FindProcessesUnderSandbox(string sandboxRoot) => _orphans.ToArray();

        public bool TryTerminateProcess(int processId)
        {
            KilledPids.Add(processId);
            if (!allowTerminate)
            {
                return false;
            }

            _orphans.RemoveAll(p => p.ProcessId == processId);
            return true;
        }
    }

    private sealed class StaticManifestInspector(bool requiresAdmin) : IPeManifestInspector
    {
        public PeManifestElevationResult InspectExecutable(string executablePath) =>
            new(true, requiresAdmin ? "requireAdministrator" : "asInvoker", requiresAdmin);
    }

    private sealed class StaticIntegrityProbe(int rid) : IProcessIntegrityProbe
    {
        public ProcessIntegrityInspectionResult InspectProcessIntegrity(int processId) =>
            new(true, rid, rid >= Win32ProcessIntegrityProbe.HighIntegrityRid);
    }

    private sealed class InProcessRejectingWorkerLauncher(Func<TrainerLaunchRequest, TrainerLaunchResponse> onLaunch)
        : ITrainerWorkerLauncher
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _clientTask;

        public string? ResolveWorkerExecutablePath() => Environment.ProcessPath;

        public Task<WorkerProcessLaunchResult> LaunchWorkerAsync(
            TrainerElevationLevel elevationLevel,
            string pipeName,
            string launchNonce,
            CancellationToken cancellationToken = default)
        {
            string currentExe = Environment.ProcessPath!;
            int currentPid = Environment.ProcessId;

            _clientTask = Task.Run(async () =>
            {
                await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
                {
                    PipeName = pipeName,
                    ExpectedHostPid = currentPid,
                    LaunchNonce = launchNonce,
                    WorkerIntegrityLevel = 0x2000,
                    ExpectedHostExecutablePaths = [currentExe],
                });

                var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var hostGone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += () => hostGone.TrySetResult(true);
                client.RegisterHandler<TrainerLaunchRequest, TrainerLaunchResponse>(
                    TrainerIpcMethods.Launch,
                    (req, _) =>
                    {
                        TrainerLaunchResponse reply = onLaunch(req!);
                        handled.TrySetResult(true);
                        return Task.FromResult(reply);
                    });

                await client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                _ = await handled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                // Stay connected until the host has read the reply and let go, or the test is done: closing a fixed
                // 50 ms after the handler ran raced the reply on slow CI runners.
                await Task.WhenAny(hostGone.Task, _release.Task).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }, CancellationToken.None);

            return Task.FromResult(new WorkerProcessLaunchResult(
                Succeeded: true,
                ElevationLevel: elevationLevel,
                WorkerProcessId: currentPid));
        }

        public void TryTerminateWorkerProcess(int workerProcessId) { }

        public Task WaitForCompletionAsync()
        {
            _release.TrySetResult(true);
            return _clientTask ?? Task.CompletedTask;
        }
    }
}
