using System.Diagnostics;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class LaunchJobObjectAndPlaytimeTests
{
    [Fact]
    public void JobObject_KernelExtendedLimits_NeverSetsKillOnJobCloseFlag()
    {
        using var tracker = new JobObjectProcessTracker();

        Assert.True(tracker.VerifyKillOnJobCloseDisabled());

        uint flags = tracker.QueryConfiguredLimitFlags();
        Assert.Equal(0u, flags & JobObjectProcessTracker.JobObjectLimitKillOnJobClose);
        Assert.NotEqual(0u, flags & JobObjectProcessTracker.JobObjectLimitBreakawayOk);
    }

    [Fact]
    public async Task JobObject_HeadlessSilentChildProcess_DeliversActiveProcessZeroViaIocp()
    {
        using var tracker = new JobObjectProcessTracker();
        Assert.True(tracker.VerifyKillOnJobCloseDisabled());

        string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var psi = new ProcessStartInfo
        {
            FileName = comSpec,
            Arguments = "/c \"for /L %i in (1,1,4000) do @rem\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var child = Process.Start(psi);
        Assert.NotNull(child);

        var outcome = tracker.TryAttachProcess(child!.SafeHandle, child.Id);
        Assert.True(outcome is JobAttachOutcome.Attached or JobAttachOutcome.ProcessAlreadyExited);

        await tracker.WaitForActiveProcessZeroAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.True(tracker.IsActiveProcessZero);
    }

    [Fact]
    public void PlaytimeTracker_TriStateAccounting_TracksActiveOverlayWhitelistMagpieAfkAndBackground()
    {
        var probe = new FakeUserActivityProbe
        {
            Foreground = new ForegroundWindowSnapshot(new nint(0x1001), ProcessId: 4242, WindowClassName: "GameWndClass"),
            KeyboardMouseIdleSec = 2.0,
            GamepadIdleSec = 9999.0,
        };

        var tracker = new PlaytimeTracker(probe);
        int[] gamePids = [4242];
        var overlayHwnds = new HashSet<nint> { new(0x9009) };
        var overlayPids = new HashSet<int> { 7777 };

        // 1. Game window focused + keyboard active -> Active
        var t1 = tracker.SampleOneSecond(gamePids, overlayHwnds, overlayPids, afkTimeoutSeconds: 60.0);
        Assert.Equal(PlaySessionActivityState.Active, t1.State);
        Assert.Equal(1, t1.ActiveSec);

        // 2. Spawn.OverlayHost window focused -> still credited as Active!
        probe.Foreground = new ForegroundWindowSnapshot(new nint(0x9009), ProcessId: 7777, WindowClassName: "OverlayWnd");
        var t2 = tracker.SampleOneSecond(gamePids, overlayHwnds, overlayPids, afkTimeoutSeconds: 60.0);
        Assert.Equal(PlaySessionActivityState.Active, t2.State);
        Assert.Equal(2, t2.ActiveSec);

        // 3. Magpie fullscreen upscaler window focused -> still credited as Active!
        probe.Foreground = new ForegroundWindowSnapshot(
            new nint(0x8888),
            ProcessId: 5150,
            WindowClassName: PlaytimeTracker.MagpieHostWindowClass);
        var t3 = tracker.SampleOneSecond(gamePids, overlayHwnds, overlayPids, afkTimeoutSeconds: 60.0);
        Assert.Equal(PlaySessionActivityState.Active, t3.State);
        Assert.Equal(3, t3.ActiveSec);

        // 4. Both keyboard/mouse AND gamepad idle >= 60s -> AFK
        probe.Foreground = new ForegroundWindowSnapshot(new nint(0x1001), ProcessId: 4242, WindowClassName: "GameWndClass");
        probe.KeyboardMouseIdleSec = 120.0;
        probe.GamepadIdleSec = 180.0;
        var t4 = tracker.SampleOneSecond(gamePids, overlayHwnds, overlayPids, afkTimeoutSeconds: 60.0);
        Assert.Equal(PlaySessionActivityState.Afk, t4.State);
        Assert.Equal(1, t4.AfkSec);

        // 5. Keyboard still idle, but gamepad stick breaks deadzone -> immediately back to Active!
        probe.GamepadIdleSec = 0.2;
        var t5 = tracker.SampleOneSecond(gamePids, overlayHwnds, overlayPids, afkTimeoutSeconds: 60.0);
        Assert.Equal(PlaySessionActivityState.Active, t5.State);
        Assert.Equal(4, t5.ActiveSec);

        // 6. Alt-Tabbed to unrelated browser window -> Background
        probe.Foreground = new ForegroundWindowSnapshot(new nint(0x2222), ProcessId: 9999, WindowClassName: "Chrome_WidgetWin_1");
        var t6 = tracker.SampleOneSecond(gamePids, overlayHwnds, overlayPids, afkTimeoutSeconds: 60.0);
        Assert.Equal(PlaySessionActivityState.Background, t6.State);
        Assert.Equal(1, t6.BackgroundSec);
        Assert.Equal(4, t6.ActiveSec);
        Assert.Equal(1, t6.AfkSec);
    }

    [Fact]
    public async Task LaunchService_SilentSessionAndAntiCheatCooldownInterlock_PersistsSessionAndBlocksDuringCooldown()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string fakeGameDir = sandbox.CreateSubDirectory("games", "SilentLaunchTarget");
        string dummyExe = Path.Combine(fakeGameDir, "game.exe");
        await File.WriteAllBytesAsync(dummyExe, [0x4D, 0x5A]);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Silent Launch Test Game",
            InstallDir = fakeGameDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var eventBus = new InMemorySpawnEventBus();
        var clock = new ManualTimeProvider();
        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: static () => [],
            pollInterval: TimeSpan.FromMilliseconds(250));

        var probe = new FakeUserActivityProbe
        {
            Foreground = new ForegroundWindowSnapshot(new nint(0x5555), ProcessId: 0, WindowClassName: "ConsoleWindowClass"),
            KeyboardMouseIdleSec = 0.0,
            GamepadIdleSec = 9999.0,
        };

        var silentStarter = new SilentCmdStarter();
        var options = new GameLaunchOptions
        {
            ForceCreateNoWindow = true,
            PlaytimeSampleInterval = TimeSpan.FromMilliseconds(30),
            ShellBreakTriggerThreshold = TimeSpan.Zero,
            ShellBreakObservationWindow = TimeSpan.Zero,
        };

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            eventBus,
            activityProbe: probe,
            processStarter: silentStarter,
            timeProvider: TimeProvider.System,
            options: options);

        // 1. Normal Tier 0 silent launch -> attaches JobObject -> exits naturally -> persists PlaySessionRecord
        var launchRes = await launchService.LaunchGameAsync(game.GameId);
        Assert.True(launchRes.Succeeded, launchRes.ErrorMessage);
        Assert.NotNull(launchRes.Session);

        await launchService.WaitForGameExitAsync(game.GameId, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Null(launchService.GetActiveSession(game.GameId));

        var sessions = await sandbox.Database.GetPlaySessionsByGameAsync(game.GameId);
        Assert.Single(sessions);
        Assert.NotNull(sessions[0].EndedUtc);

        // 2. Put AntiCheatGuard into 15s CooldownLocked -> LaunchGameAsync must block with ANTICHEAT_COOLDOWN_ACTIVE
        guard.NotifyProtectedGameStarted("protected-game", AntiCheatTier.Tier3);
        Task exitCooldownTask = guard.NotifyProtectedGameExitedAsync("protected-game", AntiCheatTier.Tier3);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await Task.Delay(20);
        Assert.Equal(AntiCheatGuardPhase.CooldownLocked, guard.CurrentSnapshot.Phase);

        var blockedRes = await launchService.LaunchGameAsync(game.GameId);
        Assert.False(blockedRes.Succeeded);
        Assert.Equal("ANTICHEAT_COOLDOWN_ACTIVE", blockedRes.ErrorCode);

        // Finish cooldown cleanly (yield between virtual clock ticks so async continuations never race under parallel load)
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!exitCooldownTask.IsCompleted && DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5);
        }

        await exitCooldownTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FakeUserActivityProbe : IUserActivityProbe
    {
        public ForegroundWindowSnapshot Foreground { get; set; }
        public double KeyboardMouseIdleSec { get; set; }
        public double GamepadIdleSec { get; set; }

        public ForegroundWindowSnapshot GetForegroundWindow() => Foreground;
        public double GetKeyboardMouseIdleSeconds() => KeyboardMouseIdleSec;
        public double GetGamepadIdleSeconds() => GamepadIdleSec;
    }

    private sealed class SilentCmdStarter : IGameProcessStarter
    {
        public Process StartDirectProcess(GameProcessStartRequest request)
        {
            string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var psi = new ProcessStartInfo
            {
                FileName = comSpec,
                Arguments = "/c \"for /L %i in (1,1,2000) do @rem\"",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            return Process.Start(psi)!;
        }

        public Task StartExplorerDecoupledAsync(GameProcessStartRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
