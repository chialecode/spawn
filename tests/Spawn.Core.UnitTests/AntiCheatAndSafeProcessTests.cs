using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class AntiCheatAndSafeProcessTests
{
    [Fact]
    public void AntiCheatGuard_InspectGameDirectory_ClassifiesTier0ToTier3SignaturesAccurately()
    {
        using var sandbox = new TestSandbox();
        using var guard = new AntiCheatGuardService();

        // Tier 0: Pure DRM-Free / offline game directory
        string tier0Dir = sandbox.CreateSubDirectory("ac", "Tier0Offline");
        File.WriteAllBytes(Path.Combine(tier0Dir, "limbo.exe"), [0x4D, 0x5A]);
        var r0 = guard.InspectGameDirectory(tier0Dir);
        Assert.Equal(AntiCheatTier.Tier0, r0.Tier);
        Assert.Empty(r0.MatchedIndicators);

        // Tier 1: HoYoKProtect / KuroSDK online single-player client
        string tier1Dir = sandbox.CreateSubDirectory("ac", "Tier1Gacha");
        File.WriteAllBytes(Path.Combine(tier1Dir, "HoYoKProtect.sys"), [0x00]);
        var r1 = guard.InspectGameDirectory(tier1Dir);
        Assert.Equal(AntiCheatTier.Tier1, r1.Tier);
        Assert.Contains("HoYoKProtect", r1.MatchedIndicators);

        // Tier 2: EasyAntiCheat / BattlEye multiplayer title
        string tier2Dir = sandbox.CreateSubDirectory("ac", "Tier2Eac", "EasyAntiCheat");
        File.WriteAllBytes(Path.Combine(tier2Dir, "EasyAntiCheat_EOS.exe"), [0x4D, 0x5A]);
        var r2 = guard.InspectGameDirectory(Path.GetDirectoryName(tier2Dir)!);
        Assert.Equal(AntiCheatTier.Tier2, r2.Tier);
        Assert.Contains(r2.MatchedIndicators, s => s.StartsWith("EasyAntiCheat", StringComparison.Ordinal));

        // Tier 3: ACE SGuard64 / Vanguard Ring-0 kernel anti-cheat (must override lower-tier indicators)
        string tier3Root = sandbox.CreateSubDirectory("ac", "Tier3Ace");
        string subAce = Directory.CreateDirectory(Path.Combine(tier3Root, "AntiCheatExpert")).FullName;
        File.WriteAllBytes(Path.Combine(subAce, "SGuard64.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(tier3Root, "HoYoKProtect.sys"), [0x00]);
        var r3 = guard.InspectGameDirectory(tier3Root);
        Assert.Equal(AntiCheatTier.Tier3, r3.Tier);
        Assert.StartsWith("ACE:", r3.Signature, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AntiCheatGuard_ProtectedSessionLifecycle_WaitsForResidualDaemonExitPlusFull15SecondCooldown()
    {
        var clock = new ManualTimeProvider();
        IReadOnlyList<string> simulatedResiduals = [];

        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: () => Volatile.Read(ref simulatedResiduals),
            pollInterval: TimeSpan.FromMilliseconds(250));

        // IdleSafe allows Tier 0 companion operations, blocks Tier 1+
        guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "GbeForkActivation");
        Assert.Throws<AntiCheatLockdownException>(() =>
            guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier2, "GbeForkActivation"));

        // Protected Tier 3 game starts -> global lockdown active
        guard.NotifyProtectedGameStarted("game-ace-01", AntiCheatTier.Tier3);
        Volatile.Write(ref simulatedResiduals, ["SGuard64.exe"]);
        Assert.Equal(AntiCheatGuardPhase.ProtectedRunning, guard.CurrentSnapshot.Phase);
        Assert.True(guard.CurrentSnapshot.IsHighRiskLocked);
        Assert.Throws<AntiCheatLockdownException>(() =>
            guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "TrainerLaunch"));

        // Game process exits, but SGuard64.exe remains alive for 3 seconds
        Task cooldownTask = guard.NotifyProtectedGameExitedAsync("game-ace-01", AntiCheatTier.Tier3);

        for (int i = 0; i < 12; i++)
        {
            await AdvanceWhenWaitingAsync(clock, cooldownTask);
        }

        Assert.Equal(AntiCheatGuardPhase.ResidualGuardWatch, guard.CurrentSnapshot.Phase);
        Assert.Contains("SGuard64.exe", guard.CurrentSnapshot.DetectedResidualProcesses);
        Assert.False(cooldownTask.IsCompleted);

        // SGuard64.exe finally terminates -> enters mandatory 15.0s residual cooldown
        Volatile.Write(ref simulatedResiduals, []);
        await AdvanceWhenWaitingAsync(clock, cooldownTask);
        await SpinWaitUntilAsync(() => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked, timeoutMs: 10_000);

        Assert.Equal(AntiCheatGuardPhase.CooldownLocked, guard.CurrentSnapshot.Phase);
        Assert.True(guard.CurrentSnapshot.RemainingCooldownSeconds > 14.0);

        // Advance 14.5s -> still locked!
        for (int i = 0; i < 58; i++)
        {
            await AdvanceWhenWaitingAsync(clock, cooldownTask);
        }

        Assert.Equal(AntiCheatGuardPhase.CooldownLocked, guard.CurrentSnapshot.Phase);
        Assert.Throws<AntiCheatLockdownException>(() =>
            guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "BlueprintStart"));

        // Advance past the 15.0s mark -> transitions cleanly back to IdleSafe
        for (int i = 0; i < 4; i++)
        {
            if (!await AdvanceWhenWaitingAsync(clock, cooldownTask))
            {
                break;
            }
        }

        await cooldownTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AntiCheatGuardPhase.IdleSafe, guard.CurrentSnapshot.Phase);
        Assert.False(guard.CurrentSnapshot.IsHighRiskLocked);
        Assert.Equal(0.0, guard.CurrentSnapshot.RemainingCooldownSeconds);

        // Tier 0 operations are now permitted again
        guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "GbeForkActivation");
    }

    [Fact]
    public void SafeProcessInspector_CurrentProcessAndAuxiliaryFilter_UsesLimitedQueryRightsOnly()
    {
        int currentPid = Environment.ProcessId;
        Assert.True(SafeProcessInspector.IsProcessAlive(currentPid));

        string? exePath = SafeProcessInspector.TryGetProcessExecutablePath(currentPid);
        Assert.False(string.IsNullOrWhiteSpace(exePath));
        Assert.True(File.Exists(exePath));

        nint syncHandle = SafeProcessInspector.TryOpenSynchronizeHandle(currentPid);
        Assert.NotEqual(nint.Zero, syncHandle);
        SafeProcessInspector.CloseSafeHandle(syncHandle);

        Assert.True(SafeProcessInspector.IsAuxiliaryOrCrashProcessName("UnityCrashHandler64.exe"));
        Assert.True(SafeProcessInspector.IsAuxiliaryOrCrashProcessName("SGuard64.exe"));
        Assert.True(SafeProcessInspector.IsAuxiliaryOrCrashProcessName("EasyAntiCheat_EOS.exe"));
        Assert.True(SafeProcessInspector.IsAuxiliaryOrCrashProcessName("unins000.exe"));
        Assert.False(SafeProcessInspector.IsAuxiliaryOrCrashProcessName("limbo.exe"));
    }

    [Fact]
    public async Task AntiCheatGuard_StartupWithResidualDaemon_LocksImmediatelyAndRequiresFullCooldownAfterExit()
    {
        var clock = new ManualTimeProvider();
        IReadOnlyList<string> simulatedResiduals = ["SGuard64.exe"];

        // SPAWN starts while SGuard64.exe from a game launched outside SPAWN is still alive.
        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: () => Volatile.Read(ref simulatedResiduals),
            pollInterval: TimeSpan.FromMilliseconds(250));

        Assert.Equal(AntiCheatGuardPhase.ResidualGuardWatch, guard.CurrentSnapshot.Phase);
        Assert.Contains("SGuard64.exe", guard.CurrentSnapshot.DetectedResidualProcesses);
        Assert.Throws<AntiCheatLockdownException>(() =>
            guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "GbeForkActivation"));

        // Daemon exits -> full 15s cooldown, still locked at 14.5s, unlocked after 15s.
        Volatile.Write(ref simulatedResiduals, []);
        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked);
        Assert.True(guard.CurrentSnapshot.RemainingCooldownSeconds > 14.0);

        await AdvanceAsync(clock, TimeSpan.FromSeconds(14.5));
        Assert.Throws<AntiCheatLockdownException>(() =>
            guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "TrainerLaunch"));

        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.IdleSafe);
        Assert.Equal(AntiCheatGuardPhase.IdleSafe, guard.CurrentSnapshot.Phase);
        guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "GbeForkActivation");
    }

    [Fact]
    public async Task AntiCheatGuard_EnsureHighRiskOperation_LiveProbeDetectsNewResidualDaemonAndRestartsCooldown()
    {
        var clock = new ManualTimeProvider();
        IReadOnlyList<string> simulatedResiduals = [];

        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: () => Volatile.Read(ref simulatedResiduals),
            pollInterval: TimeSpan.FromMilliseconds(250));

        guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "GbeForkActivation");

        // An EAC/BattlEye daemon appears while SPAWN is idle -> the very next high-risk request is refused.
        Volatile.Write(ref simulatedResiduals, ["EasyAntiCheat_EOS.exe"]);
        var ex = Assert.Throws<AntiCheatLockdownException>(() =>
            guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "TrainerLaunch"));
        Assert.Equal(AntiCheatGuardPhase.ResidualGuardWatch, ex.Phase);
        Assert.Contains("EasyAntiCheat_EOS.exe", guard.CurrentSnapshot.DetectedResidualProcesses);

        // Daemon exits -> cooldown; it reappears mid-cooldown -> back to residual watch (cooldown restarts later).
        Volatile.Write(ref simulatedResiduals, []);
        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked);
        await AdvanceAsync(clock, TimeSpan.FromSeconds(5));
        Volatile.Write(ref simulatedResiduals, ["BEService.exe"]);
        Assert.Throws<AntiCheatLockdownException>(() =>
            guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "BlueprintStart"));
        Assert.Equal(AntiCheatGuardPhase.ResidualGuardWatch, guard.CurrentSnapshot.Phase);

        Volatile.Write(ref simulatedResiduals, []);
        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked);
        Assert.True(guard.CurrentSnapshot.RemainingCooldownSeconds > 14.0);
        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.IdleSafe);
        guard.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "GbeForkActivation");
    }

    [Fact]
    public async Task AntiCheatGuard_RestartDuringCooldownOrProtectedSession_StaysLockedAcrossInstances()
    {
        using var sandbox = new TestSandbox();
        string statePath = sandbox.Paths.AntiCheatGuardStateFilePath;
        var clock = new ManualTimeProvider();
        IReadOnlyList<string> noResiduals = [];

        // Instance #1: protected Tier 3 session exits, daemon already gone -> CooldownLocked, then SPAWN is restarted at 5s.
        using (var first = new AntiCheatGuardService(clock, TimeSpan.FromSeconds(15), () => noResiduals, TimeSpan.FromMilliseconds(250), persistedStateFilePath: statePath))
        {
            first.NotifyProtectedGameStarted("game-ace-01", AntiCheatTier.Tier3);
            Assert.True(File.Exists(statePath));
            _ = first.NotifyProtectedGameExitedAsync("game-ace-01", AntiCheatTier.Tier3);
            await AdvanceUntilAsync(clock, () => first.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked);
            await AdvanceAsync(clock, TimeSpan.FromSeconds(5));
        }

        // Instance #2 (restart): remaining ~10s of the original cooldown is still enforced.
        using (var second = new AntiCheatGuardService(clock, TimeSpan.FromSeconds(15), () => noResiduals, TimeSpan.FromMilliseconds(250), persistedStateFilePath: statePath))
        {
            Assert.Equal(AntiCheatGuardPhase.CooldownLocked, second.CurrentSnapshot.Phase);
            Assert.InRange(second.CurrentSnapshot.RemainingCooldownSeconds, 8.5, 10.5);
            Assert.Throws<AntiCheatLockdownException>(() =>
                second.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "TrainerLaunch"));

            await AdvanceUntilAsync(clock, () => second.CurrentSnapshot.Phase == AntiCheatGuardPhase.IdleSafe);
            second.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "GbeForkActivation");
            Assert.False(File.Exists(statePath));
        }

        // Instance #3: SPAWN dies while a protected session is running (no exit notification ever arrives).
        using (var third = new AntiCheatGuardService(clock, TimeSpan.FromSeconds(15), () => noResiduals, TimeSpan.FromMilliseconds(250), persistedStateFilePath: statePath))
        {
            third.NotifyProtectedGameStarted("game-eac-02", AntiCheatTier.Tier2);
        }

        // Instance #4 (restart): must not trust the dead session -> residual watch + a FULL 15s cooldown before unlocking.
        using (var fourth = new AntiCheatGuardService(clock, TimeSpan.FromSeconds(15), () => noResiduals, TimeSpan.FromMilliseconds(250), persistedStateFilePath: statePath))
        {
            Assert.True(fourth.CurrentSnapshot.IsHighRiskLocked);
            await AdvanceUntilAsync(clock, () => fourth.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked);
            Assert.True(fourth.CurrentSnapshot.RemainingCooldownSeconds > 14.0);
            Assert.Throws<AntiCheatLockdownException>(() =>
                fourth.EnsureHighRiskOperationPermitted(AntiCheatTier.Tier0, "TrainerLaunch"));
            await AdvanceUntilAsync(clock, () => fourth.CurrentSnapshot.Phase == AntiCheatGuardPhase.IdleSafe);
        }

        // Corrupted state file fails closed (locked) instead of silently unlocking.
        await File.WriteAllTextAsync(statePath, "{not-json");
        using var fifth = new AntiCheatGuardService(clock, TimeSpan.FromSeconds(15), () => noResiduals, TimeSpan.FromMilliseconds(250), persistedStateFilePath: statePath);
        Assert.True(fifth.CurrentSnapshot.IsHighRiskLocked);
    }

    [Fact]
    public void AntiCheatGuard_HighRiskEnvironmentNames_DetectCheatEngineDbk64AndSensitiveWorkers()
    {
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskProcessName("cheatengine-x86_64.exe"));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskProcessName("cheatengine-x86_64-SSE4-AVX2.exe"));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskProcessName("CheatEngine74.exe"));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskProcessName("Cheat Engine.exe"));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskProcessName("Spawn.TrainerWorker.exe"));
        Assert.False(HighRiskEnvironmentProbe.IsHighRiskProcessName("limbo.exe"));
        Assert.False(HighRiskEnvironmentProbe.IsHighRiskProcessName("explorer.exe"));

        Assert.True(HighRiskEnvironmentProbe.IsHighRiskDriverName("dbk64.sys"));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskDriverName(@"\??\C:\Program Files\Cheat Engine 7.5\dbk64.sys"));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskDriverName("\"C:\\Tools\\DBK32.SYS\""));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskDriverName(@"\SystemRoot\System32\drivers\WinRing0x64.sys"));
        Assert.True(HighRiskEnvironmentProbe.IsHighRiskDriverName("inpoutx64.sys"));
        Assert.False(HighRiskEnvironmentProbe.IsHighRiskDriverName(@"\SystemRoot\System32\drivers\ntfs.sys"));

        // The real read-only sweep (Toolhelp + EnumDeviceDrivers + SCM) must run without throwing on this machine.
        var live = AntiCheatGuardService.DefaultInspectHighRiskEnvironment();
        Assert.NotNull(live.BlockingProcesses);
        Assert.NotNull(live.BlockingDrivers);

        // A failing probe fails closed.
        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => throw new InvalidOperationException("probe broken"));
        Assert.False(guard.InspectHighRiskEnvironment().IsClean);
    }

    [Fact]
    public async Task AntiCheatGuard_Tier2LaunchWithCheatEngineOrDbk64_IsBlockedBeforeAnyProcessStarts()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "EacShooter");
        Directory.CreateDirectory(Path.Combine(gameDir, "EasyAntiCheat"));
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "EasyAntiCheat", "EasyAntiCheat_EOS.exe"), [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "shooter.exe"), [0x4D, 0x5A]);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "EAC Shooter",
            InstallDir = gameDir,
            ExecutableRelPath = "shooter.exe",
            AntiCheatTier = AntiCheatTier.Tier2,
        };
        await sandbox.Database.UpsertGameAsync(game);

        HighRiskEnvironmentReport report = new(["cheatengine-x86_64.exe"], ["dbk64.sys"]);
        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: () => Volatile.Read(ref report));

        var starter = new RecordingStarter();
        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            processStarter: starter,
            options: new GameLaunchOptions { ForceCreateNoWindow = true });

        var blocked = await launchService.LaunchGameAsync(game.GameId);
        Assert.False(blocked.Succeeded);
        Assert.Equal("HIGH_RISK_ENVIRONMENT_BLOCKED", blocked.ErrorCode);
        Assert.Contains("cheatengine-x86_64.exe", blocked.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("dbk64.sys", blocked.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, starter.StartCount);
        Assert.Equal(AntiCheatGuardPhase.IdleSafe, guard.CurrentSnapshot.Phase);
        Assert.Empty(await sandbox.Database.GetPlaySessionsByGameAsync(game.GameId));

        // Driver-only residue (CE closed but dbk64.sys still loaded) is still blocking.
        Volatile.Write(ref report, new HighRiskEnvironmentReport([], ["dbk64.sys"]));
        var stillBlocked = await launchService.LaunchGameAsync(game.GameId);
        Assert.Equal("HIGH_RISK_ENVIRONMENT_BLOCKED", stillBlocked.ErrorCode);
        Assert.Equal(0, starter.StartCount);
    }

    private sealed class RecordingStarter : IGameProcessStarter
    {
        private int _startCount;

        public int StartCount => Volatile.Read(ref _startCount);

        public System.Diagnostics.Process StartDirectProcess(GameProcessStartRequest request)
        {
            Interlocked.Increment(ref _startCount);
            throw new InvalidOperationException("Must not start any process in this test.");
        }

        public Task StartExplorerDecoupledAsync(GameProcessStartRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            throw new InvalidOperationException("Must not start any process in this test.");
        }
    }

    /// <summary>
    /// Moves the clock one 250 ms poll only once the guard's loop waits on its timer: a step taken while the loop is between two polls would
    /// move the time without waking it, and it would then wait for a timer nothing fires (#31). Returns false, without moving the clock, when
    /// <paramref name="loop"/> finished instead.
    /// </summary>
    private static async Task<bool> AdvanceWhenWaitingAsync(ManualTimeProvider clock, Task loop, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (clock.PendingTimerCount == 0)
        {
            if (loop.IsCompleted)
            {
                return false;
            }

            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "The guard did not wait on its poll timer in time.");
            await Task.Delay(2);
        }

        clock.Advance(TimeSpan.FromMilliseconds(250));
        return true;
    }

    private static async Task AdvanceAsync(ManualTimeProvider clock, TimeSpan total)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            clock.Advance(step);
            await Task.Delay(2);
        }
    }

    private static async Task AdvanceUntilAsync(ManualTimeProvider clock, Func<bool> predicate, int maxSteps = 400)
    {
        for (int i = 0; i < maxSteps && !predicate(); i++)
        {
            await Task.Delay(2);
            if (predicate())
            {
                return;
            }

            clock.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.True(predicate(), "Guard did not reach the expected phase within the virtual time budget.");
    }

    private static async Task SpinWaitUntilAsync(Func<bool> predicate, int timeoutMs = 1500)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(5);
        }
    }
}
