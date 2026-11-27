using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;
using Spawn.Core.Update;
using Spawn.Modules.Progress.Catalog;
using Spawn.Modules.Progress.Runtime;
using Spawn.Modules.Progress.Snapshots;
using Spawn.Modules.Progress.UnitTests.Catalog;
using Spawn.Modules.Progress.UnitTests.Snapshots;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Runtime;

/// <summary>
/// Targeted regression suite for Issue #63:
/// manual pre-trainer snapshots, unified Save Time Machine priority + fallback, guard diagnostics &amp; offline Tier 2 messaging,
/// hotkey proxy state auto-reset on stop/natural exit, external-launched game 0x1000 fallback, replace-import with manual hotkey inheritance,
/// silent antivirus deletion quarantine attribution, candidate detail challenge propagation, and window visibility affected-count passthrough.
/// </summary>
public sealed class TrainerSafetyAndUxOptimizationTests
{
    [Fact]
    public async Task PreTrainerSnapshot_ManualCreate_BypassesAutoDisabledSettingAndActivityDecoratorForwardsFlag()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "ManualSnapGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "ManualSnapGame");
        await File.WriteAllTextAsync(Path.Combine(saveDir, "slot1.sav"), "MANUAL_SAVE_PAYLOAD", Encoding.UTF8);

        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "ManualSnapGame",
            InstallDir = installDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        });

        // Disable automatic pre-trainer snapshot in settings.
        await sandbox.Database.SaveTrainerSettingsAsync(new TrainerSettingsDto
        {
            CreatePreTrainerSnapshot = false,
        });

        var gameData = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "save-1",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = true,
                IsSafeToClean = true,
            },
        ]);

        var inner = new PreTrainerSaveSnapshotService(
            sandbox.Database,
            sandbox.Paths,
            gameData,
            new StubSnapshotGameLaunchService());
        var tracker = new UpdateActivityTracker();
        IPreTrainerSaveSnapshotService decorated = new ActivityTrackingPreTrainerSnapshotService(inner, tracker);

        // Automatic call (isManual: false) MUST still skip when CreatePreTrainerSnapshot == false.
        PreTrainerSnapshotResult autoRes = await decorated.CreateAsync(gameId, trainerId: null, isManual: false);
        Assert.False(autoRes.Succeeded);
        Assert.Equal(PreTrainerSnapshotStatus.Skipped, autoRes.Status);
        Assert.Equal(PreTrainerSnapshotSkipReason.DisabledBySettings, autoRes.SkipReason);

        // Manual call (isManual: true) MUST succeed through decorator without being blocked by auto-backup setting!
        PreTrainerSnapshotResult manualRes = await decorated.CreateAsync(gameId, trainerId: null, isManual: true);
        Assert.True(manualRes.Succeeded, manualRes.Message);
        Assert.Equal(PreTrainerSnapshotStatus.Completed, manualRes.Status);
        Assert.NotNull(manualRes.Snapshot);
        Assert.Equal(1, manualRes.Snapshot!.FileCount);
        Assert.Equal(0, tracker.ActiveSaveSnapshotOrRollbackCount);
    }

    [Fact]
    public async Task PreTrainerSnapshot_RestoreAsync_BlocksWhenExternallyLaunchedGameProcessDetectedUnderInstallDir()
    {
        await using var sandbox = new SnapshotTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "ExternalRunGame");
        string saveDir = sandbox.CreateSubDirectory("saves", "ExternalRunGame");
        string savePath = Path.Combine(saveDir, "slot0.sav");
        await File.WriteAllTextAsync(savePath, "ORIGINAL_BEFORE_EXTERNAL", Encoding.UTF8);

        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "ExternalRunGame",
            InstallDir = installDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        });

        var gameData = new StubSnapshotGameDataService(_ =>
        [
            new GameDataEntryDto
            {
                EntryId = "save-ext",
                GameId = gameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = saveDir,
                Exists = true,
                IsSafeToClean = true,
            },
        ]);

        bool simulateExternalRunning = false;
        var service = new PreTrainerSaveSnapshotService(
            sandbox.Database,
            sandbox.Paths,
            gameData,
            new StubSnapshotGameLaunchService(), // SPAWN active session is always null!
            timeProvider: null,
            logger: null,
            afterSingleFileCopyHook: null,
            maxBytesOverrideForTest: null,
            directoryProcessFinder: dir => simulateExternalRunning
                ? [new SafeProcessEntry(4242, 4, "game.exe", Path.Combine(dir, "game.exe"))]
                : []);

        PreTrainerSnapshotResult snap = await service.CreateAsync(gameId, trainerId: null, isManual: true);
        Assert.True(snap.Succeeded);

        await File.WriteAllTextAsync(savePath, "MODIFIED_WHILE_EXTERNAL_RUNNING", Encoding.UTF8);

        // External process detected via pure 0x1000 fallback -> RestoreAsync MUST refuse!
        simulateExternalRunning = true;
        PreTrainerRestoreResult blocked = await service.RestoreAsync(snap.Snapshot!.SnapshotId);
        Assert.False(blocked.Succeeded);
        Assert.Contains("外部启动", blocked.Message, StringComparison.Ordinal);
        Assert.Equal("MODIFIED_WHILE_EXTERNAL_RUNNING", await File.ReadAllTextAsync(savePath));

        // Once external process exits -> RestoreAsync succeeds!
        simulateExternalRunning = false;
        PreTrainerRestoreResult restored = await service.RestoreAsync(snap.Snapshot.SnapshotId);
        Assert.True(restored.Succeeded, restored.Message);
        Assert.Equal("ORIGINAL_BEFORE_EXTERNAL", await File.ReadAllTextAsync(savePath));
    }

    [Fact]
    public async Task TrainerRuntimeService_PrefersSaveTimeMachineWithNonBlockingDegradationAndLegacyFallback()
    {
        await using var env = await MiniRuntimeEnv.CreateAsync();

        // 1. Time Machine succeeds -> uses PreTrainerImport trigger, never calls legacy snapshot service.
        var tmOk = new StubSaveTimeMachineService((_, _) => Task.FromResult(new CreateSaveSnapshotResult
        {
            Succeeded = true,
            Message = "时光机启动前快照已创建。",
            Snapshot = new SaveSnapshotRecord
            {
                SnapshotId = "tm-snap-001",
                GameId = env.GameId,
                BranchId = "main",
                TriggerKind = SaveSnapshotTriggerKind.PreTrainerImport,
                FileCount = 4,
                TotalRawBytes = 2048,
                CreatedMachineId = "test-machine",
            },
        }));

        await using (var rt1 = env.BuildRuntime(saveTimeMachineService: tmOk))
        {
            TrainerLaunchOutcome res = await rt1.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = true });
            Assert.True(res.Succeeded, res.Message);
            Assert.NotNull(res.PreSnapshot);
            Assert.Equal(PreTrainerSnapshotStatus.Completed, res.PreSnapshot!.Status);
            Assert.Equal("tm-snap-001", res.PreSnapshot.Snapshot!.SnapshotId);
            Assert.Single(tmOk.Requests);
            Assert.Equal(SaveSnapshotTriggerKind.PreTrainerImport, tmOk.Requests[0].TriggerKind);
            Assert.Equal(0, env.LegacySnapshots.CreateCalls);
            await rt1.StopAsync(res.Session!.SessionId);
        }

        // 2. Time Machine times out -> skips non-blockingly with SkipReason.Timeout, trainer still launches!
        var tmSlow = new StubSaveTimeMachineService(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new CreateSaveSnapshotResult { Succeeded = false, Message = "unreachable" };
        });

        await using (var rt2 = env.BuildRuntime(
            saveTimeMachineService: tmSlow,
            preTrainerSnapshotTimeout: TimeSpan.FromMilliseconds(40)))
        {
            TrainerLaunchOutcome res = await rt2.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = true });
            Assert.True(res.Succeeded, res.Message);
            Assert.NotNull(res.PreSnapshot);
            Assert.Equal(PreTrainerSnapshotStatus.Skipped, res.PreSnapshot!.Status);
            Assert.Equal(PreTrainerSnapshotSkipReason.Timeout, res.PreSnapshot.SkipReason);
            Assert.Equal(0, env.LegacySnapshots.CreateCalls);
            await rt2.StopAsync(res.Session!.SessionId);
        }

        // 3. Time Machine skips due to size threshold -> preserves Skipped/SizeThresholdExceeded without blocking launch.
        var tmOversize = new StubSaveTimeMachineService((_, _) => Task.FromResult(new CreateSaveSnapshotResult
        {
            Succeeded = false,
            Skipped = true,
            SkipReason = SaveSnapshotSkipReason.SizeThresholdExceeded,
            Message = "存档体积超过上限已跳过。",
        }));

        await using (var rt3 = env.BuildRuntime(saveTimeMachineService: tmOversize))
        {
            TrainerLaunchOutcome res = await rt3.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = true });
            Assert.True(res.Succeeded, res.Message);
            Assert.Equal(PreTrainerSnapshotStatus.Skipped, res.PreSnapshot!.Status);
            Assert.Equal(PreTrainerSnapshotSkipReason.SizeThresholdExceeded, res.PreSnapshot.SkipReason);
            await rt3.StopAsync(res.Session!.SessionId);
        }

        // 4. Time Machine throws -> gracefully falls back to legacy snapshot engine.
        var tmThrowing = new StubSaveTimeMachineService((_, _) => throw new InvalidOperationException("Time Machine unavailable"));
        await using (var rt4 = env.BuildRuntime(saveTimeMachineService: tmThrowing))
        {
            TrainerLaunchOutcome res = await rt4.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = true });
            Assert.True(res.Succeeded, res.Message);
            Assert.Equal(PreTrainerSnapshotStatus.Completed, res.PreSnapshot!.Status);
            Assert.Equal(1, env.LegacySnapshots.CreateCalls);
            await rt4.StopAsync(res.Session!.SessionId);
        }
    }

    [Fact]
    public async Task TrainerRuntimeService_ResetsHotkeyProxyStatesInSqliteOnBothStopAndNaturalExit()
    {
        await using var env = await MiniRuntimeEnv.CreateAsync();
        await using var rt = env.BuildRuntime();

        // Scenario A: Explicit StopAsync resets SQLite proxy_state back to false.
        TrainerLaunchOutcome launch1 = await rt.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });
        Assert.True(launch1.Succeeded);
        await rt.TriggerHotkeyAsync(launch1.Session!.SessionId, "hp-inf");
        Assert.True((await env.Database.GetTrainerHotkeyBindingsAsync(env.TrainerId)).Single().ProxyState);

        await rt.StopAsync(launch1.Session.SessionId);
        Assert.False((await env.Database.GetTrainerHotkeyBindingsAsync(env.TrainerId)).Single().ProxyState);

        // Scenario B: Trainer exits naturally (SessionEnded notification) resets SQLite proxy_state BEFORE Ready fires.
        TrainerLaunchOutcome launch2 = await rt.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });
        Assert.True(launch2.Succeeded);
        await rt.TriggerHotkeyAsync(launch2.Session!.SessionId, "hp-inf");
        Assert.True((await env.Database.GetTrainerHotkeyBindingsAsync(env.TrainerId)).Single().ProxyState);

        bool proxyStateWhenReadyObserved = true;
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.StateChanged += (_, ev) =>
        {
            if (ev.CurrentState == TrainerRuntimeState.Ready)
            {
                proxyStateWhenReadyObserved = env.Database.GetTrainerHotkeyBindingsAsync(env.TrainerId)
                    .GetAwaiter().GetResult().Single().ProxyState;
                readyTcs.TrySetResult(true);
            }
        };

        env.Backend.RaiseSessionEnded(new TrainerSessionEndedNotification
        {
            SessionId = launch2.Session.SessionId,
            Reason = TrainerShutdownReason.TrainerExitedNaturally,
            ExitCode = 0,
        });

        Assert.True(await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(proxyStateWhenReadyObserved);
        Assert.False((await env.Database.GetTrainerHotkeyBindingsAsync(env.TrainerId)).Single().ProxyState);
    }

    [Fact]
    public async Task TrainerRuntimeService_SilentAntivirusDeletion_ClassifiesAsAntivirusQuarantinedAndSetsQuarantinedState()
    {
        await using var env = await MiniRuntimeEnv.CreateAsync();
        env.Catalog.NextIntegrityReport = new TrainerIntegrityReport
        {
            TrainerId = env.TrainerId,
            IsValid = false,
            FileExists = false,
            HashMatches = false,
            IsInsideSandbox = true,
            Win32ErrorCode = null,
            FailureMessage = "修改器文件已缺失（可能被杀毒软件静默隔离或删除）。",
        };

        await using var rt = env.BuildRuntime();
        TrainerLaunchOutcome outcome = await rt.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });

        Assert.False(outcome.Succeeded);
        Assert.Equal(TrainerLaunchFailureKind.AntivirusQuarantined, outcome.FailureKind);
        Assert.Equal(TrainerRuntimeState.Quarantined, rt.State);
    }

    [Fact]
    public async Task TrainerRuntimeService_ExternalLaunchedElevatedGame_UpgradesElevationAndReturnsExternalNotice()
    {
        await using var env = await MiniRuntimeEnv.CreateAsync();

        var elevatedPidProbe = new PidBasedIntegrityProbe(pid => pid == 7777 ? 0x3000 : 0x2000);
        var advisor = new TrainerElevationAdvisor(new StaticManifest(false), elevatedPidProbe);

        await using var rt = env.BuildRuntime(
            elevationAdvisor: advisor,
            directoryProcessFinder: dir => [new SafeProcessEntry(7777, 4, "Demo.exe", Path.Combine(dir, "Demo.exe"))]);

        TrainerLaunchOutcome outcome = await rt.LaunchAsync(env.TrainerId, new TrainerLaunchOptions { CreatePreTrainerSnapshot = false });

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(outcome.IsTargetGameLaunchedExternally);
        Assert.Contains("退出游戏后请手动停止修改器", outcome.ExternalLaunchNotice, StringComparison.Ordinal);
        Assert.NotNull(env.Backend.LastLaunchSpec);
        Assert.True(env.Backend.LastLaunchSpec!.TargetGameIsElevated);
        Assert.Equal(TrainerElevationLevel.Level2HighIlUac, outcome.Session!.ElevationLevel);

        // Window visibility result passthrough (AffectedWindowCount = 0 vs 2).
        env.Backend.NextWindowVisibilityResponse = new TrainerSetWindowVisibilityResponse
        {
            Succeeded = true,
            AffectedWindowCount = 0,
        };
        TrainerSetWindowVisibilityResponse visZero = await rt.SetOriginalWindowVisibleWithResultAsync(outcome.Session.SessionId, false);
        Assert.True(visZero.Succeeded);
        Assert.Equal(0, visZero.AffectedWindowCount);

        await rt.StopAsync(outcome.Session.SessionId);
    }

    [Fact]
    public async Task TrainerSafetyGate_SingleConsolidatedProbeAndDistinctOfflineVsConfirmedMultiplayerTier2Messages()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "Tier2ProbeGame");
        File.WriteAllText(Path.Combine(installDir, "EasyAntiCheat_EOS.exe"), "stub");

        var countingProbe = new CountingAntiCheatSystemProbe();
        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            sessionDriverProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));

        // 1. Offline / Steam metadata unreachable -> IsOfflineSinglePlayerVerificationPending = true, fail-closed BlockedByTier.
        using var offlineGate = new TrainerSafetyGate(
            guard,
            database: sandbox.Database,
            systemProbe: countingProbe,
            awacyIndex: AreWeAntiCheatYetIndex.Default,
            steamHttpMessageHandler: new RecordingMockHttpHandler(_ => throw new HttpRequestException("Offline")));

        var tier2Game = new GameRecord
        {
            GameId = "g-tier2-offline",
            Title = "Unknown Hybrid EAC Title",
            SteamAppId = 999888777,
            InstallDir = installDir,
            ExecutableRelPath = "unknown_hybrid.exe",
            AntiCheatTier = AntiCheatTier.Tier2,
        };

        TrainerGameSafetySnapshot offlineSnap = await offlineGate.EvaluateGameSafetySnapshotAsync(tier2Game);
        Assert.Equal(1, countingProbe.ProcessProbeCalls); // Single live AC probe instead of 3!
        Assert.False(offlineSnap.AllowanceDecision.IsAllowed);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByTier, offlineSnap.AllowanceDecision.Outcome);
        Assert.True(offlineSnap.AllowanceDecision.IsOfflineSinglePlayerVerificationPending);
        Assert.Contains("离线", offlineSnap.AllowanceDecision.Message, StringComparison.Ordinal);
        Assert.Contains("重试", offlineSnap.AllowanceDecision.Message, StringComparison.Ordinal);

        // 2. Confirmed pure-multiplayer Tier 2 game (Steam categories = Multiplayer only, no Single-player id=2).
        const string pureMpJson = """
            {"730":{"success":true,"data":{"name":"Counter-Strike 2","categories":[{"id":1,"description":"Multi-player"},{"id":8,"description":"Valve Anti-Cheat enabled"}]}}}
            """;
        using var onlineGate = new TrainerSafetyGate(
            guard,
            database: sandbox.Database,
            systemProbe: countingProbe,
            awacyIndex: AreWeAntiCheatYetIndex.Default,
            steamHttpMessageHandler: new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(pureMpJson, Encoding.UTF8, "application/json"),
            }));

        var pureMpGame = new GameRecord
        {
            GameId = "g-tier2-mp",
            Title = "Counter-Strike 2",
            SteamAppId = 730,
            InstallDir = installDir,
            ExecutableRelPath = "cs2.exe",
            AntiCheatTier = AntiCheatTier.Tier2,
        };

        TrainerGameSafetySnapshot mpSnap = await onlineGate.EvaluateGameSafetySnapshotAsync(pureMpGame);
        Assert.False(mpSnap.AllowanceDecision.IsAllowed);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByTier, mpSnap.AllowanceDecision.Outcome);
        Assert.False(mpSnap.AllowanceDecision.IsOfflineSinglePlayerVerificationPending);
        Assert.Contains("纯多人", mpSnap.AllowanceDecision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AntiCheatGuardService_RecheckLiveResiduals_ReportsExactResidualsAndNeverShortensCooldown()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        string[] activeResiduals = ["EasyAntiCheat_EOS.exe"];
        string[] activeDrivers = ["EasyAntiCheat_EOS.sys"];

        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: () => activeResiduals,
            pollInterval: TimeSpan.FromSeconds(30),
            sessionDriverProbe: () => activeDrivers);

        AntiCheatGuardSnapshot snap1 = guard.RecheckLiveResiduals();
        Assert.Equal(AntiCheatGuardPhase.ResidualGuardWatch, snap1.Phase);
        Assert.Contains("EasyAntiCheat_EOS.exe", snap1.DetectedResidualProcesses);
        Assert.Contains("EasyAntiCheat_EOS.sys", snap1.DetectedResidualProcesses);

        // Entering ProtectedRunning must NEVER be cleared by RecheckLiveResiduals.
        guard.NotifyProtectedGameStarted("g-valorant", AntiCheatTier.Tier3);
        activeResiduals = [];
        activeDrivers = [];
        AntiCheatGuardSnapshot protectedSnap = guard.RecheckLiveResiduals();
        Assert.Equal(AntiCheatGuardPhase.ProtectedRunning, protectedSnap.Phase);
        Assert.Equal("g-valorant", protectedSnap.ActiveProtectedGameId);
    }

    [Fact]
    public async Task TrainerCatalogService_ImportLocalReplacing_InheritsManualHotkeysAndPreservesOldOnFailure()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "ReplaceTestGame");
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = "g-replace",
            Title = "Replace Test Game",
            InstallDir = gameDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        });

        using var http = new HttpClient(new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var catalog = new TrainerCatalogService(
            sandbox.Database,
            sandbox.Paths,
            new CanonicalTitleResolver(sandbox.Database, http),
            new FlingCleanRoomDiscoveryClient(sandbox.Paths, http),
            new TrainerSafeDownloader(http),
            new TrainerArchiveExtractor());

        string v1Path = Path.Combine(sandbox.RootDirectory, "MyTrainer_v1.exe");
        string v2Path = Path.Combine(sandbox.RootDirectory, "MyTrainer_v2.exe");
        string badPath = Path.Combine(sandbox.RootDirectory, "NotAnExe.txt");
        await File.WriteAllBytesAsync(v1Path, SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, salt: 1));
        await File.WriteAllBytesAsync(v2Path, SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, salt: 2));
        await File.WriteAllTextAsync(badPath, "plain text invalid magic");

        TrainerImportResult import1 = await catalog.ImportLocalAsync("g-replace", v1Path);
        Assert.True(import1.Succeeded, import1.Message);
        string oldTrainerId = import1.Trainer!.TrainerId;

        await catalog.SaveHotkeyBindingsAsync(oldTrainerId,
        [
            new TrainerHotkeyBindingRecord
            {
                TrainerId = oldTrainerId,
                EntryId = "manual-custom-1",
                Label = "自定义无限金币",
                OriginalChord = "Ctrl+F5",
                EntryKind = TrainerHotkeyEntryKind.Trigger,
                SortOrder = 0,
            },
        ]);

        // Failing replacement MUST preserve old trainer intact!
        TrainerImportResult failedReplace = await catalog.ImportLocalReplacingAsync("g-replace", badPath, oldTrainerId, inheritManualHotkeys: true);
        Assert.False(failedReplace.Succeeded);
        Assert.Single(await catalog.ListForGameAsync("g-replace"));
        Assert.Equal(oldTrainerId, (await catalog.ListForGameAsync("g-replace"))[0].TrainerId);

        // Valid replacement replaces old trainer AND inherits manual-* hotkeys!
        TrainerImportResult replaced = await catalog.ImportLocalReplacingAsync("g-replace", v2Path, oldTrainerId, inheritManualHotkeys: true);
        Assert.True(replaced.Succeeded, replaced.Message);
        Assert.NotEqual(oldTrainerId, replaced.Trainer!.TrainerId);

        IReadOnlyList<TrainerCatalogRecord> remaining = await catalog.ListForGameAsync("g-replace");
        Assert.Single(remaining);
        Assert.Equal(replaced.Trainer.TrainerId, remaining[0].TrainerId);

        IReadOnlyList<TrainerHotkeyBindingRecord> inherited = await catalog.GetHotkeysAsync(replaced.Trainer.TrainerId);
        Assert.Single(inherited);
        Assert.Equal("自定义无限金币", inherited[0].Label);
        Assert.Equal("Ctrl+F5", inherited[0].OriginalChord);
        Assert.Equal(TrainerHotkeyEntryKind.Trigger, inherited[0].EntryKind);
    }

    [Fact]
    public async Task TrainerCatalogService_LoadCandidateDetailsWithDiagnostics_PropagatesChallengeDetected()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "ChallengeGame");
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = "g-cf",
            Title = "Challenge Game",
            InstallDir = gameDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        });

        var handler = new RecordingMockHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/robots.txt", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("User-agent: *\nAllow: /", Encoding.UTF8, "text/plain"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><title>Just a moment...</title><div>cf-challenge</div></html>", Encoding.UTF8, "text/html"),
            };
        });

        using var http = new HttpClient(handler);
        var catalog = new TrainerCatalogService(
            sandbox.Database,
            sandbox.Paths,
            new CanonicalTitleResolver(sandbox.Database, http),
            new FlingCleanRoomDiscoveryClient(sandbox.Paths, http),
            new TrainerSafeDownloader(http),
            new TrainerArchiveExtractor());

        var cand = new TrainerCandidate
        {
            CandidateId = "cand-1",
            Provider = TrainerProviderKind.FlingMain,
            Title = "Challenge Game Trainer",
            PageUrl = "https://flingtrainer.com/trainer/challenge-game-trainer/",
        };

        TrainerCandidateDetailsResult details = await catalog.LoadCandidateDetailsWithDiagnosticsAsync("g-cf", cand);
        Assert.False(details.Succeeded);
        Assert.True(details.ChallengeDetected);
        Assert.Equal(cand.PageUrl, details.FallbackBrowserUrl);
    }

    private sealed class MiniRuntimeEnv : IAsyncDisposable
    {
        public string RootDir { get; }
        public SpawnPaths Paths { get; }
        public SpawnDatabase Database { get; }
        public string GameId { get; } = "g-opt-demo";
        public string TrainerId { get; } = "tr-opt-demo";
        public MiniSafetyGate SafetyGate { get; } = new();
        public MiniCatalog Catalog { get; }
        public MiniLegacySnapshotService LegacySnapshots { get; } = new();
        public MiniAudio Audio { get; } = new();
        public MiniBackend Backend { get; } = new();
        public MiniGameLaunch GameLaunch { get; } = new();

        private MiniRuntimeEnv(string rootDir, SpawnPaths paths, SpawnDatabase db)
        {
            RootDir = rootDir;
            Paths = paths;
            Database = db;
            Catalog = new MiniCatalog(db);
        }

        public TrainerRuntimeService BuildRuntime(
            ISaveTimeMachineService? saveTimeMachineService = null,
            ITrainerElevationAdvisor? elevationAdvisor = null,
            Func<string, IReadOnlyList<SafeProcessEntry>>? directoryProcessFinder = null,
            TimeSpan? preTrainerSnapshotTimeout = null) =>
            new(
                Database,
                Paths,
                SafetyGate,
                Catalog,
                LegacySnapshots,
                Audio,
                Backend,
                GameLaunch,
                elevationAdvisor ?? new TrainerElevationAdvisor(new StaticManifest(false), new PidBasedIntegrityProbe(_ => 0x2000)),
                logger: null,
                saveTimeMachineService: saveTimeMachineService,
                directoryProcessFinder: directoryProcessFinder ?? (_ => []),
                preTrainerSnapshotTimeout: preTrainerSnapshotTimeout);

        public static async Task<MiniRuntimeEnv> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"spawn-opt-rt-{Guid.NewGuid():N}");
            string dataDir = Path.Combine(root, "data");
            string gameDir = Path.Combine(root, "game");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(gameDir);

            var paths = SpawnPaths.CreateIsolated(dataDir);
            paths.EnsureDirectoriesExist();
            var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            var env = new MiniRuntimeEnv(root, paths, db);
            await db.UpsertGameAsync(new GameRecord
            {
                GameId = env.GameId,
                Title = "Demo Singleplayer",
                InstallDir = gameDir,
                ExecutableRelPath = "Demo.exe",
                AntiCheatTier = AntiCheatTier.Tier0,
            });

            string rel = Path.Combine(env.GameId, env.TrainerId, "Trainer.exe");
            string full = Path.Combine(paths.TrainerSandboxDirectory, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, [0x4D, 0x5A, 0x90, 0x00]);

            await db.UpsertTrainerAsync(new TrainerCatalogRecord
            {
                TrainerId = env.TrainerId,
                GameId = env.GameId,
                DisplayName = "Demo Trainer",
                SandboxRelativePath = rel,
                FileSha256 = new string('c', 64),
                ParseMode = TrainerParseMode.Structured,
            });

            await db.ReplaceTrainerHotkeyBindingsAsync(env.TrainerId,
            [
                new TrainerHotkeyBindingRecord
                {
                    TrainerId = env.TrainerId,
                    EntryId = "hp-inf",
                    Label = "Infinite Health",
                    OriginalChord = "Num 1",
                    EntryKind = TrainerHotkeyEntryKind.Toggle,
                    ProxyState = false,
                    SortOrder = 0,
                },
            ]);

            return env;
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (int i = 0; i < 10; i++)
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
                    Thread.Sleep(50);
                }
            }
        }
    }

    private sealed class StubSaveTimeMachineService(
        Func<CreateSaveSnapshotRequest, CancellationToken, Task<CreateSaveSnapshotResult>> onCreate)
        : ISaveTimeMachineService
    {
        public List<CreateSaveSnapshotRequest> Requests { get; } = [];
        public event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged { add { } remove { } }

        public Task<CreateSaveSnapshotResult> CreateSnapshotAsync(CreateSaveSnapshotRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return onCreate(request, ct);
        }

        public Task<SaveGameStateRecord> GetGameStateAsync(string gameId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string gameId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(string leftSnapshotId, string rightSnapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string snapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveBranchRecord?> RenameBranchAsync(string branchId, string newName, string? description = null, string? colorHex = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DeleteBranchResult> DeleteBranchAsync(string branchId, bool deleteSnapshots = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(string snapshotId, string targetDirectory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(string gameId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveGcReport> RunCasGarbageCollectionAsync(TimeSpan? gracePeriodOverride = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class CountingAntiCheatSystemProbe : IAntiCheatSystemProbe
    {
        public int ProcessProbeCalls { get; private set; }
        public IReadOnlyList<SafeProcessEntry> EnumerateProcesses(bool resolveFullPaths)
        {
            ProcessProbeCalls++;
            return [];
        }

        public IReadOnlyList<string> EnumerateLoadedDriverBaseNames() => [];
        public IReadOnlyList<ActiveKernelDriverInfo> EnumerateActiveDriverServices() => [];
    }

    private sealed class StaticManifest(bool requiresAdmin) : IPeManifestInspector
    {
        public PeManifestElevationResult InspectExecutable(string executablePath) =>
            new(true, requiresAdmin ? "requireAdministrator" : "asInvoker", requiresAdmin);
    }

    private sealed class PidBasedIntegrityProbe(Func<int, int> ridForPid) : IProcessIntegrityProbe
    {
        public ProcessIntegrityInspectionResult InspectProcessIntegrity(int processId)
        {
            int rid = ridForPid(processId);
            return new ProcessIntegrityInspectionResult(true, rid, rid >= Win32ProcessIntegrityProbe.HighIntegrityRid);
        }
    }

    private sealed class MiniSafetyGate : ITrainerSafetyGate
    {
        public bool IsWatchdogActive => false;
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered { add { } remove { } }
        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport { EffectiveTier = game.AntiCheatTier });
        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveAntiCheatProbeReport());
        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default) =>
            Task.FromResult(new VerifiedOfflineBypass { GameId = game.GameId, StaticAntiCheatTier = game.AntiCheatTier });
        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default) =>
            Task.FromResult(new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.Allowed,
                IsAllowed = true,
                Message = "允许",
                StaticGameTier = game.AntiCheatTier,
                GuardPhase = AntiCheatGuardPhase.IdleSafe,
            });
        public void NotifyTrainerSessionStarted(string sessionId, string gameId) { }
        public void NotifyTrainerSessionEnded(string sessionId) { }
    }

    private sealed class MiniCatalog(SpawnDatabase db) : ITrainerCatalogService
    {
        public TrainerIntegrityReport? NextIntegrityReport { get; set; }
        public Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerMatchResult { GameId = gameId, EffectiveQuery = "Demo" });
        public Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false });
        public Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false });
        public Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult(NextIntegrityReport ?? new TrainerIntegrityReport
            {
                TrainerId = trainerId,
                IsValid = true,
                FileExists = true,
                HashMatches = true,
                IsInsideSandbox = true,
            });
        public Task RemoveAsync(string trainerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
            db.GetTrainerHotkeyBindingsAsync(trainerId, ct);
    }

    private sealed class MiniLegacySnapshotService : IPreTrainerSaveSnapshotService
    {
        public int CreateCalls { get; private set; }
        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(new PreTrainerSnapshotResult
            {
                Succeeded = true,
                Status = PreTrainerSnapshotStatus.Completed,
            });
        }

        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, bool isManual, CancellationToken ct = default) =>
            CreateAsync(gameId, trainerId, ct);

        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>([]);
        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerRestoreResult { Succeeded = true });
    }

    private sealed class MiniAudio : ITrainerAudioController
    {
        public event Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>? FallbackRequired { add { } remove { } }
        public Task PrepareAsync(TrainerAudioPolicy policy, string sandboxRoot, CancellationToken ct = default) => Task.CompletedTask;
        public void TrackProcess(int pid) { }
        public Task ApplyPolicyAsync(TrainerAudioPolicy policy, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MiniBackend : ITrainerBackend, ITrainerBackendSessionEvents
    {
        public string BackendKind => ExternalTrainerBackend.Kind;
        public int ActiveWorkerCount { get; private set; }
        public TrainerLaunchSpec? LastLaunchSpec { get; private set; }
        public TrainerSetWindowVisibilityResponse NextWindowVisibilityResponse { get; set; } = new()
        {
            Succeeded = true,
            AffectedWindowCount = 1,
        };

        public event Action<TrainerProcessCreatedNotification>? ProcessCreated { add { } remove { } }
        public event Action<TrainerSessionEndedNotification>? SessionEnded;

        public IReadOnlyList<int> GetActiveWorkerProcessIds() => ActiveWorkerCount > 0 ? [5001] : [];

        public Task<TrainerSessionHandle> ActivateSessionAsync(TrainerLaunchSpec spec, CancellationToken ct = default)
        {
            LastLaunchSpec = spec;
            ActiveWorkerCount = 1;
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
        public Task TriggerHotkeyAsync(string sessionId, TrainerTriggerHotkeyRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TrainerSetWindowVisibilityResponse> SetWindowVisibilityWithResultAsync(string sessionId, bool visible, CancellationToken ct = default) =>
            Task.FromResult(NextWindowVisibilityResponse);
        public Task StopSessionAsync(string sessionId, TrainerShutdownReason reason, CancellationToken ct = default)
        {
            ActiveWorkerCount = 0;
            return Task.CompletedTask;
        }
        public Task ShutdownAllImmediatelyAsync(TrainerShutdownReason reason, CancellationToken ct = default)
        {
            ActiveWorkerCount = 0;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseSessionEnded(TrainerSessionEndedNotification notification)
        {
            ActiveWorkerCount = 0;
            SessionEnded?.Invoke(notification);
        }
    }

    private sealed class MiniGameLaunch : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged { add { } remove { } }
        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameLaunchResult.Failure("UNUSED", "Unused"));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }
}
