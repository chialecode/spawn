using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Achievement;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// End-to-end offline integration tests covering the Steam launch pipeline across
/// <c>Spawn.Core</c>, <c>Spawn.Modules.Launcher</c>, and <c>Spawn.Modules.Achievement</c>.
/// All tests run headlessly inside isolated temp sandboxes and never touch real Steam or host user data.
/// </summary>
public sealed class SteamLaunchIntegrationTests
{
    [Theory]
    [InlineData("steam://rungameid/504230", true)]
    [InlineData("steam://rungameid/10000000000000000000", true)]
    [InlineData("  STEAM://RUNGAMEID/1245620  ", true)]
    [InlineData(@"C:\Games\Celeste\Celeste.exe", false)]
    [InlineData("D:/Games/Test/game.exe", false)]
    [InlineData(@"\\fileserver\share\game.exe", false)]
    [InlineData("file:///C:/Games/game.exe", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("steam://rungameid/504230\r\n", false)]
    [InlineData("steam://rungameid/504230\r\nextra", false)]
    [InlineData("steam://rungameid/504230\0", false)]
    public void ExplorerShellLauncher_IsProtocolUriTarget_ClassifiesSteamUrisAndRejectsPathsAndControlChars(
        string candidate,
        bool expectedIsUri)
    {
        Assert.Equal(expectedIsUri, DefaultGameProcessStarter.IsProtocolUriTarget(candidate));
    }

    [Fact]
    public void ExplorerShellLauncher_BuildShellExecuteParameters_BuildsCleanUriVsExecutableParameters()
    {
        var uriReq = new GameProcessStartRequest(
            ExecutableFullPath: "  steam://rungameid/504230  ",
            WorkingDirectory: @"C:\Ignored",
            Arguments: "--ignored",
            CreateNoWindow: true);

        var uriParams = DefaultGameProcessStarter.BuildShellExecuteParameters(uriReq);
        Assert.True(uriParams.IsUri);
        Assert.Equal("steam://rungameid/504230", uriParams.Target);
        Assert.Equal(string.Empty, uriParams.Arguments);
        Assert.Equal(string.Empty, uriParams.WorkingDirectory);
        Assert.Equal("open", uriParams.Verb);
        Assert.Equal(1, uriParams.ShowCommand); // SW_SHOWNORMAL

        using var sandbox = new TestSandbox();
        string gameDir = sandbox.CreateSubDirectory("games", "ShellParamTarget");
        string exePath = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exePath, [0x4D, 0x5A]);

        var exeReq = new GameProcessStartRequest(
            ExecutableFullPath: exePath,
            WorkingDirectory: gameDir,
            Arguments: "-windowed",
            CreateNoWindow: true);

        var exeParams = DefaultGameProcessStarter.BuildShellExecuteParameters(exeReq);
        Assert.False(exeParams.IsUri);
        Assert.Equal(exePath, exeParams.Target);
        Assert.Equal("-windowed", exeParams.Arguments);
        Assert.Equal(gameDir, exeParams.WorkingDirectory);
        Assert.Equal("open", exeParams.Verb);
        Assert.Equal(0, exeParams.ShowCommand); // SW_HIDE
    }

    [Fact]
    public async Task GameLaunchService_OfficialSteamLocalTier0_LaunchesViaExplorerUri_AndRespectsToastSuppressionSetting()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        FakeSteamInstall steam = CreateFakeSteamInstall(sandbox, accountId: 1001, isSteamRunning: true);
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync(steam.RootDir);

        GameRecord game = await SeedFakeGameAsync(
            sandbox,
            folderName: "CelesteSteam",
            exeName: "Celeste.exe",
            title: "Celeste",
            platformSource: PlatformSource.SteamLocal,
            antiCheatTier: AntiCheatTier.Tier0,
            steamAppId: 504230);
        await sandbox.Database.SaveGameSteamLaunchSettingsAsync(
            game.GameId,
            new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam });

        var eventBus = new InMemorySpawnEventBus();
        var observedEvents = new List<ActiveGameSessionChangedEvent>();
        using IDisposable sub = eventBus.Subscribe<ActiveGameSessionChangedEvent>((ev, _) =>
        {
            lock (observedEvents)
            {
                observedEvents.Add(ev);
            }

            return Task.CompletedTask;
        });

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);
        var starter = new RecordingGameProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            eventBus,
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            options: new GameLaunchOptions
            {
                SteamRunningLaunchObservationWindow = TimeSpan.FromSeconds(10),
                ShellBreakPollInterval = TimeSpan.FromMilliseconds(30),
                ForceKillWaitTimeout = TimeSpan.FromSeconds(2),
            },
            steamShortcutService: steam.ShortcutService,
            steamClientLocator: steam.Locator,
            directoryProcessProbe: static _ => [],
            processAliveProbe: static _ => false);

        // 1. Default setting (SteamSuppressDuplicateAchievementToast = true) -> suppresses SPAWN overlay toast.
        GameLaunchResult res1 = await launchService.LaunchGameAsync(game.GameId);
        Assert.True(res1.Succeeded, res1.ErrorMessage);
        Assert.NotNull(res1.Session);
        Assert.True(res1.Session!.LaunchedViaSteam);
        Assert.True(res1.Session.SuppressAchievementToast);
        Assert.Equal(PlaySessionActivityState.ShellBreakingWatch, res1.Session.State);
        Assert.Equal(0, starter.DirectStartCount);
        Assert.Single(starter.DecoupledRequests);
        Assert.Equal("steam://rungameid/504230", starter.DecoupledRequests[0].ExecutableFullPath);

        lock (observedEvents)
        {
            Assert.Contains(observedEvents, e =>
                e.GameId == game.GameId
                && e.CurrentState == PlaySessionActivityState.ShellBreakingWatch
                && e.LaunchedViaSteam
                && e.SuppressAchievementToast);
        }

        GameStopResult stop1 = await launchService.StopGameAsync(game.GameId, forceKill: false);
        Assert.True(stop1.Succeeded);
        Assert.Equal(GameStopOutcomeKind.StoppedGracefully, stop1.Outcome);
        Assert.Null(launchService.GetActiveSession(game.GameId));

        // 2. Disabling duplicate toast suppression propagates SuppressAchievementToast = false.
        await sandbox.Database.SetSteamSuppressDuplicateAchievementToastAsync(false);

        GameLaunchResult res2 = await launchService.LaunchGameAsync(game.GameId);
        Assert.True(res2.Succeeded, res2.ErrorMessage);
        Assert.NotNull(res2.Session);
        Assert.True(res2.Session!.LaunchedViaSteam);
        Assert.False(res2.Session.SuppressAchievementToast);
        Assert.Equal(2, starter.DecoupledRequests.Count);
        Assert.Equal("steam://rungameid/504230", starter.DecoupledRequests[1].ExecutableFullPath);

        GameStopResult stop2 = await launchService.StopGameAsync(game.GameId, forceKill: false);
        Assert.True(stop2.Succeeded);
        Assert.Null(launchService.GetActiveSession(game.GameId));
    }

    [Fact]
    public async Task GameLaunchService_OfficialSteamLocalTier2WithVerifiedOfflineBypass_StillForcesProtectedLaunchAndCooldown()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        FakeSteamInstall steam = CreateFakeSteamInstall(sandbox, accountId: 2002, isSteamRunning: true);
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync(steam.RootDir);

        GameRecord game = await SeedFakeGameAsync(
            sandbox,
            folderName: "EldenRingSteam",
            exeName: "eldenring.exe",
            title: "Elden Ring",
            platformSource: PlatformSource.SteamLocal,
            antiCheatTier: AntiCheatTier.Tier2,
            steamAppId: 1245620,
            verifiedOfflineBypass: true);

        var clock = new ManualTimeProvider();
        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean,
            pollInterval: TimeSpan.FromMilliseconds(250));
        var shutdownHook = new RecordingSensitiveWorkerShutdownHook();
        var safetyGate = new StubTrainerSafetyGate(AntiCheatTier.Tier2, hasSinglePlayer: true, isClearForTrainer: true);
        var starter = new RecordingGameProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            timeProvider: TimeProvider.System,
            options: new GameLaunchOptions
            {
                SteamRunningLaunchObservationWindow = TimeSpan.FromSeconds(10),
                ShellBreakPollInterval = TimeSpan.FromMilliseconds(30),
                ForceKillWaitTimeout = TimeSpan.FromSeconds(2),
            },
            sensitiveWorkerShutdownHooks: [shutdownHook],
            trainerSafetyGate: safetyGate,
            steamShortcutService: steam.ShortcutService,
            steamClientLocator: steam.Locator,
            directoryProcessProbe: static _ => [],
            processAliveProbe: static _ => false);

        GameLaunchResult launch = await launchService.LaunchGameAsync(
            game.GameId,
            new GameLaunchExecutionOptions { LaunchModeOverride = GameLaunchMode.Steam });

        Assert.True(launch.Succeeded, launch.ErrorMessage);
        Assert.Equal(1, shutdownHook.CallCount);
        Assert.Equal(0, starter.DirectStartCount);
        Assert.Single(starter.DecoupledRequests);
        Assert.Equal("steam://rungameid/1245620", starter.DecoupledRequests[0].ExecutableFullPath);
        Assert.Equal(AntiCheatGuardPhase.ProtectedRunning, guard.CurrentSnapshot.Phase);
        Assert.Equal(game.GameId, guard.CurrentSnapshot.ActiveProtectedGameId);

        // Stopping the protected Steam session triggers AntiCheatGuard residual check + 15s cooldown.
        GameStopResult stopped = await launchService.StopGameAsync(game.GameId, forceKill: false);
        Assert.True(stopped.Succeeded);

        DateTime waitDeadline = DateTime.UtcNow.AddSeconds(5);
        while (guard.CurrentSnapshot.Phase != AntiCheatGuardPhase.CooldownLocked && DateTime.UtcNow < waitDeadline)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(15);
        }

        Assert.Equal(AntiCheatGuardPhase.CooldownLocked, guard.CurrentSnapshot.Phase);

        // Relaunching while in 15s cooldown MUST be blocked.
        GameLaunchResult blocked = await launchService.LaunchGameAsync(
            game.GameId,
            new GameLaunchExecutionOptions { LaunchModeOverride = GameLaunchMode.Steam });
        Assert.False(blocked.Succeeded);
        Assert.Equal("ANTICHEAT_COOLDOWN_ACTIVE", blocked.ErrorCode);
    }

    [Theory]
    [InlineData(AntiCheatTier.Tier2)]
    [InlineData(AntiCheatTier.Tier3)]
    public async Task GameLaunchService_NonSteamTier2OrTier3_HardBlockedWithAntiCheatErrorCode_NeverWritesShortcuts(
        AntiCheatTier tier)
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        FakeSteamInstall steam = CreateFakeSteamInstall(sandbox, accountId: 3003, isSteamRunning: false);
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync(steam.RootDir);

        GameRecord game = await SeedFakeGameAsync(
            sandbox,
            folderName: $"NonSteam_{tier}",
            exeName: "protected_game.exe",
            title: $"Protected Non-Steam {tier}",
            platformSource: PlatformSource.Standalone,
            antiCheatTier: tier);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);
        var starter = new RecordingGameProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            steamShortcutService: steam.ShortcutService,
            steamClientLocator: steam.Locator);

        GameLaunchResult res = await launchService.LaunchGameAsync(
            game.GameId,
            new GameLaunchExecutionOptions { LaunchModeOverride = GameLaunchMode.Steam });

        Assert.False(res.Succeeded);
        Assert.Equal("STEAM_LAUNCH_BLOCKED_ANTICHEAT", res.ErrorCode);
        Assert.Equal(0, starter.DirectStartCount);
        Assert.Empty(starter.DecoupledRequests);
        Assert.False(File.Exists(steam.ShortcutsVdfPath));
        Assert.Equal(0, steam.ProcessController.ShutdownCalls);
        Assert.Equal(0, steam.ProcessController.StartCalls);
    }

    [Fact]
    public async Task GameLaunchService_NonSteamTier0_SteamClosed_WritesShortcutsPersistsAppIdAndLaunchesUri()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        FakeSteamInstall steam = CreateFakeSteamInstall(sandbox, accountId: 4004, isSteamRunning: false);
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync(steam.RootDir);

        GameRecord game = await SeedFakeGameAsync(
            sandbox,
            folderName: "OfflineIndieGame",
            exeName: "indie.exe",
            title: "Offline Indie",
            platformSource: PlatformSource.GogOffline,
            antiCheatTier: AntiCheatTier.Tier0);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);
        var starter = new RecordingGameProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            options: new GameLaunchOptions
            {
                SteamLaunchObservationWindow = TimeSpan.FromSeconds(10),
                ShellBreakPollInterval = TimeSpan.FromMilliseconds(30),
                ForceKillWaitTimeout = TimeSpan.FromSeconds(2),
            },
            steamShortcutService: steam.ShortcutService,
            steamClientLocator: steam.Locator,
            directoryProcessProbe: static _ => [],
            processAliveProbe: static _ => false);

        GameLaunchResult res = await launchService.LaunchGameAsync(
            game.GameId,
            new GameLaunchExecutionOptions { LaunchModeOverride = GameLaunchMode.Steam });

        Assert.True(res.Succeeded, res.ErrorMessage);
        Assert.NotNull(res.Session);
        Assert.True(res.Session!.LaunchedViaSteam);
        Assert.False(res.Session.SuppressAchievementToast); // Non-Steam games keep SPAWN achievement toasts.
        Assert.True(File.Exists(steam.ShortcutsVdfPath));

        GameSteamLaunchSettings persisted = await sandbox.Database.GetGameSteamLaunchSettingsAsync(game.GameId);
        Assert.NotNull(persisted.RegisteredShortcutAppId);
        Assert.NotEqual(0, persisted.RegisteredShortcutAppId.Value);

        string expectedUri = SteamShortcutsVdfSerializer.BuildRunGameIdUri(persisted.RegisteredShortcutAppId.Value);
        Assert.Single(starter.DecoupledRequests);
        Assert.Equal(expectedUri, starter.DecoupledRequests[0].ExecutableFullPath);

        // Verify the binary shortcuts.vdf round-trips cleanly and contains our entry.
        BinaryVdfMap doc = SteamShortcutsVdfSerializer.Read(await File.ReadAllBytesAsync(steam.ShortcutsVdfPath));
        BinaryVdfMap? shortcutsMap = doc.GetMap("shortcuts");
        Assert.NotNull(shortcutsMap);
        BinaryVdfField entryField = Assert.Single(shortcutsMap!.Fields);
        var entryMap = Assert.IsType<BinaryVdfMap>(entryField.Value);
        Assert.Equal(persisted.RegisteredShortcutAppId.Value, entryMap.GetInt32("appid"));
        Assert.Equal("Offline Indie", entryMap.GetString("AppName"));
        Assert.Equal(0, steam.ProcessController.ShutdownCalls);

        GameStopResult stop = await launchService.StopGameAsync(game.GameId, forceKill: false);
        Assert.True(stop.Succeeded);
        Assert.Null(launchService.GetActiveSession(game.GameId));
    }

    [Fact]
    public async Task GameLaunchService_NonSteamTier0_SteamRunning_RefusesWithRestartRequiredWithoutTouchingSteamOrVdf()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        FakeSteamInstall steam = CreateFakeSteamInstall(sandbox, accountId: 5005, isSteamRunning: true);
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync(steam.RootDir);

        GameRecord game = await SeedFakeGameAsync(
            sandbox,
            folderName: "NeedsRestartGame",
            exeName: "game.exe",
            title: "Needs Steam Restart",
            platformSource: PlatformSource.Standalone,
            antiCheatTier: AntiCheatTier.Tier0);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);
        var starter = new RecordingGameProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            steamShortcutService: steam.ShortcutService,
            steamClientLocator: steam.Locator);

        GameLaunchResult res = await launchService.LaunchGameAsync(
            game.GameId,
            new GameLaunchExecutionOptions { LaunchModeOverride = GameLaunchMode.Steam });

        Assert.False(res.Succeeded);
        Assert.Equal("STEAM_RESTART_REQUIRED", res.ErrorCode);
        Assert.Equal(0, starter.DirectStartCount);
        Assert.Empty(starter.DecoupledRequests);
        Assert.False(File.Exists(steam.ShortcutsVdfPath));
        Assert.Equal(0, steam.ProcessController.ShutdownCalls);
        Assert.Equal(0, steam.ProcessController.StartCalls);
    }

    [Fact]
    public async Task GameLaunchService_ExtendedSteamObservationWindow_AdoptsDelayedProcessPast35s_AndStopCancelsWatchCleanly()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        FakeSteamInstall steam = CreateFakeSteamInstall(sandbox, accountId: 6006, isSteamRunning: false);
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync(steam.RootDir);

        GameRecord gameA = await SeedFakeGameAsync(
            sandbox,
            folderName: "SlowSteamColdStart",
            exeName: "slowgame.exe",
            title: "Slow Steam Game",
            platformSource: PlatformSource.SteamLocal,
            antiCheatTier: AntiCheatTier.Tier0,
            steamAppId: 600600);

        GameRecord gameB = await SeedFakeGameAsync(
            sandbox,
            folderName: "CancelledSteamWatch",
            exeName: "cancelgame.exe",
            title: "Cancelled Steam Game",
            platformSource: PlatformSource.SteamLocal,
            antiCheatTier: AntiCheatTier.Tier0,
            steamAppId: 600601);

        var clock = new ManualTimeProvider();
        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);
        var starter = new RecordingGameProcessStarter();
        var activityProbe = new MutableUserActivityProbe
        {
            Foreground = new ForegroundWindowSnapshot(new nint(0x4242), ProcessId: 77777, WindowClassName: "GameWnd"),
            KeyboardMouseIdleSec = 0.0,
            GamepadIdleSec = 9999.0,
        };

        int spawnedPid = 0;
        bool pidAlive = false;

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            activityProbe: activityProbe,
            processStarter: starter,
            timeProvider: clock,
            options: new GameLaunchOptions
            {
                PlaytimeSampleInterval = TimeSpan.FromSeconds(1),
                ShellBreakObservationWindow = TimeSpan.FromSeconds(35),
                SteamLaunchObservationWindow = TimeSpan.FromSeconds(120),
                SteamRunningLaunchObservationWindow = TimeSpan.FromSeconds(75),
                ShellBreakPollInterval = TimeSpan.FromSeconds(5),
                ForceKillWaitTimeout = TimeSpan.FromSeconds(2),
            },
            steamShortcutService: steam.ShortcutService,
            steamClientLocator: steam.Locator,
            directoryProcessProbe: dir =>
                spawnedPid > 0 && pidAlive && string.Equals(dir, gameA.InstallDir, StringComparison.OrdinalIgnoreCase)
                    ? [spawnedPid]
                    : [],
            processAliveProbe: pid => pid == spawnedPid && pidAlive);

        // Part A: Launch gameA with Steam closed (120s window). Advance past normal 35s to 50s -> MUST still be watching!
        GameLaunchResult launchA = await launchService.LaunchGameAsync(
            gameA.GameId,
            new GameLaunchExecutionOptions
            {
                LaunchModeOverride = GameLaunchMode.Steam,
                SteamWasRunningBeforeLaunch = false,
            });
        Assert.True(launchA.Succeeded, launchA.ErrorMessage);
        Assert.Equal(PlaySessionActivityState.ShellBreakingWatch, launchService.GetActiveSession(gameA.GameId)?.State);

        for (int i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5)); // total 50s > 35s normal ShellBreakObservationWindow
            await Task.Delay(10);
        }

        ActiveGameSession? stillWatchingAt50s = launchService.GetActiveSession(gameA.GameId);
        Assert.NotNull(stillWatchingAt50s);
        Assert.Equal(PlaySessionActivityState.ShellBreakingWatch, stillWatchingAt50s!.State);

        // Spawn delayed game process at t = 50s -> watch adopts PID and transitions to Active.
        spawnedPid = 77777;
        pidAlive = true;

        DateTime adoptDeadline = DateTime.UtcNow.AddSeconds(5);
        while (launchService.GetActiveSession(gameA.GameId)?.State != PlaySessionActivityState.Active
               && DateTime.UtcNow < adoptDeadline)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15);
        }

        ActiveGameSession? adoptedSession = launchService.GetActiveSession(gameA.GameId);
        Assert.NotNull(adoptedSession);
        Assert.Equal(PlaySessionActivityState.Active, adoptedSession!.State);
        Assert.Equal(77777, adoptedSession.RootProcessId);
        Assert.True(adoptedSession.LaunchedViaSteam);
        Assert.True(adoptedSession.SessionActiveSec >= 1);

        // Game exits cleanly -> session finishes and persists to SQLite.
        pidAlive = false;
        Task exitWait = launchService.WaitForGameExitAsync(gameA.GameId);
        DateTime exitDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!exitWait.IsCompleted && DateTime.UtcNow < exitDeadline)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15);
        }

        await exitWait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(launchService.GetActiveSession(gameA.GameId));
        var sessionsA = await sandbox.Database.GetPlaySessionsByGameAsync(gameA.GameId);
        Assert.Single(sessionsA);
        Assert.NotNull(sessionsA[0].EndedUtc);
        Assert.True(sessionsA[0].ActiveSec >= 1);

        // Part B: StopGameAsync cancels a session still waiting in ShellBreakingWatch cleanly with zero leaked state.
        GameLaunchResult launchB = await launchService.LaunchGameAsync(
            gameB.GameId,
            new GameLaunchExecutionOptions
            {
                LaunchModeOverride = GameLaunchMode.Steam,
                SteamWasRunningBeforeLaunch = false,
            });
        Assert.True(launchB.Succeeded, launchB.ErrorMessage);
        Assert.Equal(PlaySessionActivityState.ShellBreakingWatch, launchService.GetActiveSession(gameB.GameId)?.State);

        GameStopResult cancelWatch = await launchService.StopGameAsync(gameB.GameId, forceKill: false);
        Assert.True(cancelWatch.Succeeded);
        Assert.Equal(GameStopOutcomeKind.StoppedGracefully, cancelWatch.Outcome);
        Assert.Null(launchService.GetActiveSession(gameB.GameId));
        Assert.Empty(launchService.ActiveSessions);
    }

    [Fact]
    public async Task AchievementService_SteamSessionToastSuppression_PersistsUnlocksWithZeroOverlayCalls_AndClearsOnStopped()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        await sandbox.Database.SaveOverlaySettingsAsync(new OverlaySettingsDto
        {
            Theme = OverlayThemeKind.Steam,
            SoundPreset = OverlaySoundPreset.SteamAuthentic,
            MuteSound = true,
            SoundVolume = 0.0,
        });

        string customSteamDir = sandbox.CreateSubDirectory("portable-steam");
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync(customSteamDir);

        string appDataRoaming = sandbox.CreateSubDirectory("env", "AppDataRoaming");
        string gameDir = sandbox.CreateSubDirectory("games", "SteamToastSuppressionGame");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "game.exe"), new byte[1024]);
        string gseSaveDir = Directory.CreateDirectory(Path.Combine(appDataRoaming, "GSE Saves", "700700")).FullName;
        string gseAchFile = Path.Combine(gseSaveDir, "achievements.json");
        await File.WriteAllTextAsync(gseAchFile, "{}", Encoding.UTF8);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Steam Toast Suppression Game",
            InstallDir = gameDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.SteamLocal,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 700700,
        };
        await sandbox.Database.UpsertGameAsync(game);
        await sandbox.Database.UpsertAchievementSchemaAsync(
            game.GameId,
            "Steam_700700",
            AchievementSchemaSource.LocalEmulatorSchema,
            "schinese",
            [
                new AchievementDefinitionDto { ApiName = "ACH_STEAM_1", DisplayName = "Steam 会话中成就", SortOrder = 0 },
                new AchievementDefinitionDto { ApiName = "ACH_STEAM_EXIT_FLUSH", DisplayName = "退出瞬间落盘成就", SortOrder = 1 },
                new AchievementDefinitionDto { ApiName = "ACH_AFTER_STOP", DisplayName = "会话结束后成就", SortOrder = 2 },
            ]);

        var fakeIpc = new StubOverlayIpcClient();
        var services = new ServiceCollection();
        services.AddSpawnCore(sandbox.Paths);
        services.AddSingleton(sandbox.Database);
        services.AddSingleton<IOverlayIpcClient>(fakeIpc);
        services.AddSpawnAchievement(opt =>
        {
            opt.AllowNetworkDownloads = false;
            opt.EnableGogCometAutoStart = false;
            opt.EnableCommunitySpoilerRequests = false;
            opt.DebounceWindow = TimeSpan.FromMilliseconds(40);
            opt.ActiveGamePollInterval = TimeSpan.FromMilliseconds(80);
            opt.EnvironmentOverrides = new AchievementEnvironmentOverrides
            {
                AppDataRoaming = appDataRoaming,
                LocalAppData = sandbox.CreateSubDirectory("env", "LocalAppData"),
                PublicDocuments = sandbox.CreateSubDirectory("env", "PublicDocs"),
                UserDocuments = sandbox.CreateSubDirectory("env", "UserDocs"),
                ProgramData = sandbox.CreateSubDirectory("env", "ProgramData"),
                SteamInstallPath = null, // Allow DB SteamInstallDirectoryOverride to propagate!
            };
        });

        await using ServiceProvider sp = services.BuildServiceProvider();
        var eventBus = sp.GetRequiredService<ISpawnEventBus>();
        var achService = sp.GetRequiredService<AchievementService>();

        var notifications = new List<AchievementUnlockedNotification>();
        achService.AchievementUnlocked += (_, n) =>
        {
            lock (notifications)
            {
                notifications.Add(n);
            }
        };

        await achService.StartMonitoringAsync();
        try
        {
            // 1. Steam session starts with SuppressAchievementToast = true.
            var activeSession = new ActiveGameSession
            {
                SessionId = SpawnId.NewUlid(),
                GameId = game.GameId,
                GameTitle = game.Title,
                InstallDir = game.InstallDir,
                AntiCheatTier = AntiCheatTier.Tier0,
                State = PlaySessionActivityState.ShellBreakingWatch,
                StartedUtc = DateTimeOffset.UtcNow,
                LaunchedViaSteam = true,
                SuppressAchievementToast = true,
            };

            await eventBus.PublishAsync(new ActiveGameSessionChangedEvent(
                GameId: game.GameId,
                Session: activeSession,
                CurrentState: PlaySessionActivityState.ShellBreakingWatch,
                CompletedSession: null,
                OccurredUtc: DateTimeOffset.UtcNow,
                LaunchedViaSteam: true,
                SuppressAchievementToast: true));

            // Unlock during active Steam session: persisted in spawn.db, NotifiedPopup = true, 0 overlay IPC calls.
            await File.WriteAllTextAsync(
                gseAchFile,
                """{"ACH_STEAM_1": {"earned": true, "earned_time": 1735000001}}""",
                Encoding.UTF8);
            _ = await achService.ScanLocalUnlocksAsync(game.GameId, silentSeed: false);
            await Task.Delay(120); // Allow any FSW debounce callback to settle too.

            Assert.Empty(fakeIpc.SentToasts);
            var unlock1 = Assert.Single(
                await sandbox.Database.GetAchievementUnlocksAsync(game.GameId),
                u => u.ApiName == "ACH_STEAM_1");
            Assert.True(unlock1.IsUnlocked);
            Assert.True(unlock1.NotifiedPopup);

            lock (notifications)
            {
                var n1 = Assert.Single(notifications, n => n.Definition.ApiName == "ACH_STEAM_1");
                Assert.True(n1.SuppressOverlayToast);
            }

            // 2. Final post-exit flush on Stopped STILL suppresses overlay toast, then clears suppression.
            await File.WriteAllTextAsync(
                gseAchFile,
                """{"ACH_STEAM_1": {"earned": true, "earned_time": 1735000001}, "ACH_STEAM_EXIT_FLUSH": {"earned": true, "earned_time": 1735000002}}""",
                Encoding.UTF8);

            await eventBus.PublishAsync(new ActiveGameSessionChangedEvent(
                GameId: game.GameId,
                Session: null,
                CurrentState: PlaySessionActivityState.Stopped,
                CompletedSession: null,
                OccurredUtc: DateTimeOffset.UtcNow,
                LaunchedViaSteam: true,
                SuppressAchievementToast: true));
            await Task.Delay(120);

            Assert.Empty(fakeIpc.SentToasts);
            var unlock2 = Assert.Single(
                await sandbox.Database.GetAchievementUnlocksAsync(game.GameId),
                u => u.ApiName == "ACH_STEAM_EXIT_FLUSH");
            Assert.True(unlock2.IsUnlocked);
            Assert.True(unlock2.NotifiedPopup);

            // 3. After Stopped, suppression is cleared: subsequent unlock dispatches normally to OverlayHost
            //    (whether caught first by FSW real-time monitoring or by ScanLocalUnlocksAsync)
            //    and forwards the configured SteamInstallDirectoryOverride!
            await File.WriteAllTextAsync(
                gseAchFile,
                """{"ACH_STEAM_1": {"earned": true, "earned_time": 1735000001}, "ACH_STEAM_EXIT_FLUSH": {"earned": true, "earned_time": 1735000002}, "ACH_AFTER_STOP": {"earned": true, "earned_time": 1735000003}}""",
                Encoding.UTF8);
            _ = await achService.ScanLocalUnlocksAsync(game.GameId, silentSeed: false);

            DateTime toastDeadline = DateTime.UtcNow.AddSeconds(3);
            while (fakeIpc.SnapshotSentToasts().Count == 0 && DateTime.UtcNow < toastDeadline)
            {
                await Task.Delay(25);
            }

            var sentToast = Assert.Single(fakeIpc.SnapshotSentToasts());
            Assert.Equal("ACH_AFTER_STOP", sentToast.AchievementApiName);
            Assert.Equal(customSteamDir, sentToast.SteamInstallDirectoryOverride);

            var unlock3 = Assert.Single(
                await sandbox.Database.GetAchievementUnlocksAsync(game.GameId),
                u => u.ApiName == "ACH_AFTER_STOP");
            Assert.True(unlock3.IsUnlocked);
            Assert.True(unlock3.NotifiedPopup);
        }
        finally
        {
            await achService.StopMonitoringAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Offline Test Doubles & Fixtures
    // -------------------------------------------------------------------------

    private static async Task<GameRecord> SeedFakeGameAsync(
        TestSandbox sandbox,
        string folderName,
        string exeName,
        string title,
        PlatformSource platformSource,
        AntiCheatTier antiCheatTier,
        int? steamAppId = null,
        bool verifiedOfflineBypass = false)
    {
        string gameDir = sandbox.CreateSubDirectory("games", folderName);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, exeName), [0x4D, 0x5A]);

        var record = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = title,
            InstallDir = gameDir,
            ExecutableRelPath = exeName,
            PlatformSource = platformSource,
            AntiCheatTier = antiCheatTier,
            SteamAppId = steamAppId,
            VerifiedOfflineBypass = verifiedOfflineBypass,
        };
        await sandbox.Database.UpsertGameAsync(record);
        return record;
    }

    private static FakeSteamInstall CreateFakeSteamInstall(
        TestSandbox sandbox,
        uint accountId,
        bool isSteamRunning)
    {
        string root = sandbox.CreateSubDirectory("steam-client");
        string steamExe = Path.Combine(root, "steam.exe");
        File.WriteAllBytes(steamExe, [0x4D, 0x5A]);

        string configDir = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        ulong steamId64 = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(accountId);
        File.WriteAllText(
            Path.Combine(configDir, "loginusers.vdf"),
            $$"""
            "users"
            {
                "{{steamId64}}"
                {
                    "AccountName"       "spawn_test_user"
                    "PersonaName"       "SPAWN Tester"
                    "MostRecent"        "1"
                    "Timestamp"         "1735000000"
                }
            }
            """,
            Encoding.UTF8);

        string userConfigDir = Directory.CreateDirectory(
            Path.Combine(root, "userdata", accountId.ToString(), "config")).FullName;
        string shortcutsVdfPath = Path.Combine(userConfigDir, "shortcuts.vdf");

        var probe = new FakeSteamEnvironmentProbe(root, isSteamRunning, activeAccountId: accountId);
        var locator = new SteamClientLocator(probe);
        var controller = new RecordingSteamProcessController();
        var shortcutService = new SteamShortcutService(
            locator,
            controller,
            options: new SteamShortcutServiceOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(200),
                StartupLoginTimeout = TimeSpan.FromMilliseconds(200),
                PollInterval = TimeSpan.FromMilliseconds(20),
            });

        return new FakeSteamInstall(root, shortcutsVdfPath, probe, locator, controller, shortcutService);
    }

    private sealed record FakeSteamInstall(
        string RootDir,
        string ShortcutsVdfPath,
        FakeSteamEnvironmentProbe Probe,
        SteamClientLocator Locator,
        RecordingSteamProcessController ProcessController,
        SteamShortcutService ShortcutService);

    private sealed class FakeSteamEnvironmentProbe(string steamRoot, bool isRunning, uint activeAccountId) : ISteamEnvironmentProbe
    {
        public bool IsRunning { get; set; } = isRunning;

        public string? ReadRegistryString(RegistryHive hive, RegistryView view, string subKeyPath, string valueName) =>
            null;

        public int? ReadRegistryDword(RegistryHive hive, RegistryView view, string subKeyPath, string valueName)
        {
            if (IsRunning && string.Equals(valueName, "ActiveUser", StringComparison.OrdinalIgnoreCase))
            {
                return unchecked((int)activeAccountId);
            }

            if (IsRunning && string.Equals(valueName, "pid", StringComparison.OrdinalIgnoreCase))
            {
                return 8888;
            }

            return null;
        }

        public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        public IEnumerable<string> EnumerateDirectories(string path) =>
            Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];

        public string? TryReadAllText(string path) =>
            File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

        public DateTimeOffset? TryGetLastWriteTimeUtc(string path) =>
            File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

        public string GetFolderPath(Environment.SpecialFolder folder) => Path.Combine(steamRoot, "__unused_special__");

        public bool IsSteamProcessRunning(int? candidatePid, string? expectedSteamExePath, out int? resolvedPid)
        {
            resolvedPid = IsRunning ? 8888 : null;
            return IsRunning;
        }
    }

    private sealed class RecordingSteamProcessController : ISteamProcessController
    {
        public int ShutdownCalls { get; private set; }
        public int StartCalls { get; private set; }

        public Task<bool> RequestGracefulShutdownAsync(string steamExePath, CancellationToken cancellationToken = default)
        {
            ShutdownCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> StartSteamAsync(string steamExePath, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingGameProcessStarter : IGameProcessStarter
    {
        public int DirectStartCount { get; private set; }
        public List<GameProcessStartRequest> DecoupledRequests { get; } = [];

        public System.Diagnostics.Process StartDirectProcess(GameProcessStartRequest request)
        {
            DirectStartCount++;
            throw new InvalidOperationException("Direct launch was not expected in Steam-delegated launch test.");
        }

        public Task StartExplorerDecoupledAsync(GameProcessStartRequest request, CancellationToken cancellationToken = default)
        {
            DecoupledRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSensitiveWorkerShutdownHook : ISensitiveWorkerShutdownHook
    {
        public int CallCount { get; private set; }

        public Task<SensitiveWorkerShutdownReport> ShutdownAllForProtectedLaunchAsync(
            TimeSpan budget,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(SensitiveWorkerShutdownReport.Clean);
        }
    }

    private sealed class StubTrainerSafetyGate(
        AntiCheatTier effectiveTier,
        bool hasSinglePlayer,
        bool isClearForTrainer) : ITrainerSafetyGate
    {
        public bool IsWatchdogActive => false;
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered { add { } remove { } }

        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(
            GameRecord game,
            bool allowOnlineLookup = true,
            CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport
            {
                EffectiveTier = effectiveTier,
                PrimarySignature = "EasyAntiCheat",
                HasSinglePlayerCategory = hasSinglePlayer,
            });

        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveAntiCheatProbeReport
            {
                ActiveAntiCheatProcesses = isClearForTrainer ? [] : ["EasyAntiCheat.exe"],
                ProbedAtUtc = DateTimeOffset.UtcNow,
            });

        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default) =>
            Task.FromResult(new VerifiedOfflineBypass
            {
                GameId = game.GameId,
                IsEligible = hasSinglePlayer && effectiveTier == AntiCheatTier.Tier2,
                IsConfirmed = game.VerifiedOfflineBypass && isClearForTrainer,
                HasSinglePlayerMode = hasSinglePlayer,
                IsLiveAntiCheatClear = isClearForTrainer,
                StaticAntiCheatTier = effectiveTier,
            });

        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(
            GameRecord game,
            bool strictVanguardMode = false,
            CancellationToken ct = default) =>
            Task.FromResult(new TrainerAllowanceDecision
            {
                Outcome = default,
                IsAllowed = false,
                Message = "stub",
            });

        public void NotifyTrainerSessionStarted(string sessionId, string gameId) { }
        public void NotifyTrainerSessionEnded(string sessionId) { }
    }

    private sealed class StaticUserActivityProbe : IUserActivityProbe
    {
        public ForegroundWindowSnapshot GetForegroundWindow() =>
            new(new nint(0x1001), ProcessId: 0, WindowClassName: "StaticTestWnd");

        public double GetKeyboardMouseIdleSeconds() => 0.0;
        public double GetGamepadIdleSeconds() => 9999.0;
    }

    private sealed class MutableUserActivityProbe : IUserActivityProbe
    {
        public ForegroundWindowSnapshot Foreground { get; set; }
        public double KeyboardMouseIdleSec { get; set; }
        public double GamepadIdleSec { get; set; }

        public ForegroundWindowSnapshot GetForegroundWindow() => Foreground;
        public double GetKeyboardMouseIdleSeconds() => KeyboardMouseIdleSec;
        public double GetGamepadIdleSeconds() => GamepadIdleSec;
    }

    private sealed class StubOverlayIpcClient : IOverlayIpcClient
    {
        private readonly Lock _lock = new();
        public List<OverlayToastRequest> SentToasts { get; } = [];
        public string PipeName => "stub-steam-integration-pipe";

        public IReadOnlyList<OverlayToastRequest> SnapshotSentToasts()
        {
            lock (_lock)
            {
                return [.. SentToasts];
            }
        }

        public Task<bool> SendToastAsync(OverlayToastRequest request, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                SentToasts.Add(request);
            }

            return Task.FromResult(true);
        }

        public Task<int> SendToastBatchAsync(IReadOnlyList<OverlayToastRequest> requests, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                SentToasts.AddRange(requests);
            }

            return Task.FromResult(requests.Count);
        }

        public Task<OverlayHeartbeatReply?> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<OverlayHeartbeatReply?>(null);

        public Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
