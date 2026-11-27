using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Configurable timing and execution parameters for <see cref="GameLaunchService"/>.
/// Defaults follow the M2 specification (<c>1Hz</c> sampling, <c>15s</c> shell-break trigger, <c>35s</c> observation window),
/// while allowing deterministic sub-second overrides in automated unit/integration tests.
/// </summary>
public sealed class GameLaunchOptions
{
    public TimeSpan PlaytimeSampleInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ShellBreakTriggerThreshold { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ShellBreakObservationWindow { get; set; } = TimeSpan.FromSeconds(35);
    public TimeSpan ShellBreakPollInterval { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Extended observation window for Steam-delegated launches when Steam cold-starts or was restarted
    /// (accommodating Steam client login, updates, and shader pre-caching).
    /// </summary>
    public TimeSpan SteamLaunchObservationWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Observation window for Steam-delegated launches when the Steam client is already running.
    /// </summary>
    public TimeSpan SteamRunningLaunchObservationWindow { get; set; } = TimeSpan.FromSeconds(75);

    public double DefaultAfkTimeoutSeconds { get; set; } = PlaytimeTracker.DefaultAfkTimeoutSeconds;

    /// <summary>
    /// Upper-bound time budget for LOOP-06 pre-launch sensitive worker shutdown hooks before launching a Tier 2/3 game.
    /// </summary>
    public TimeSpan SensitiveWorkerShutdownBudget { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Generous grace period given to a running game after sending <c>CloseMainWindow</c> during graceful stop
    /// so it can flush auto-saves or prompt the user cleanly without premature process-tree termination.
    /// </summary>
    public TimeSpan GracefulStopTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Time budget to wait for SQLite session ledger flush after an explicit <c>forceKill: true</c> termination.
    /// </summary>
    public TimeSpan ForceKillWaitTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Forces <c>CREATE_NO_WINDOW</c> when launching child executables (used by headless automated tests).
    /// </summary>
    public bool ForceCreateNoWindow { get; set; }

    /// <summary>
    /// Forces entering <c>ShellBreakingWatch</c> on fast initial exit even for single-exe directories without platform stubs.
    /// </summary>
    public bool AlwaysObserveShellBreakWindow { get; set; }
}

/// <summary>
/// Dual-engine game lifecycle orchestrator (<see cref="IGameLaunchService"/>):
/// <list type="number">
/// <item><description>Pre-launch four-layer anti-cheat inspection, independent <c>VerifiedOfflineBypass</c> evaluation, LOOP-06 sensitive worker purge, &amp; 15s residual cooldown gate.</description></item>
/// <item><description>Tier 0/1 direct launch bound to a non-killing Windows Job Object + IOCP (<c>JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO</c>, zero <c>KILL_ON_JOB_CLOSE</c>).</description></item>
/// <item><description>Tier 2/3 PPID-decoupled launch via Explorer Shell COM + <c>0x1000</c> directory-prefix shell-breaking catch-all.</description></item>
/// <item><description>1Hz <c>Active / Background / AFK</c> playtime tracking with atomic SQLite session &amp; cumulative ledger persistence.</description></item>
/// </list>
/// </summary>
public sealed class GameLaunchService : IGameLaunchService, IDisposable, IAsyncDisposable
{
    private readonly SpawnDatabase _database;
    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly ISpawnEventBus _eventBus;
    private readonly IUserActivityProbe _activityProbe;
    private readonly IGameProcessStarter _processStarter;
    private readonly TimeProvider _timeProvider;
    private readonly GameLaunchOptions _options;
    private readonly ILogger<GameLaunchService> _logger;
    private readonly ISensitiveWorkerShutdownHook[] _sensitiveWorkerShutdownHooks;
    private readonly ITrainerSafetyGate? _trainerSafetyGate;
    private readonly SpawnPaths? _paths;
    private readonly Func<string, IReadOnlyList<string>>? _sandboxOrphanProbe;
    private readonly ISteamShortcutService _steamShortcutService;
    private readonly ISteamClientLocator? _steamClientLocator;

    private readonly ConcurrentDictionary<string, ActiveGameSession> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RunningSessionContext> _runningContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<nint, int> _trustedOverlayWindows = new();
    private int _disposed;

    public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged;

    public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions => _activeSessions;

    public GameLaunchService(
        SpawnDatabase database,
        IAntiCheatGuardService antiCheatGuard,
        ISpawnEventBus eventBus,
        IUserActivityProbe? activityProbe = null,
        IGameProcessStarter? processStarter = null,
        TimeProvider? timeProvider = null,
        GameLaunchOptions? options = null,
        ILogger<GameLaunchService>? logger = null,
        IEnumerable<ISensitiveWorkerShutdownHook>? sensitiveWorkerShutdownHooks = null,
        ITrainerSafetyGate? trainerSafetyGate = null,
        SpawnPaths? paths = null,
        Func<string, IReadOnlyList<string>>? sandboxOrphanProbe = null,
        ISteamShortcutService? steamShortcutService = null,
        ISteamClientLocator? steamClientLocator = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(antiCheatGuard);
        ArgumentNullException.ThrowIfNull(eventBus);

        _database = database;
        _antiCheatGuard = antiCheatGuard;
        _eventBus = eventBus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _activityProbe = activityProbe ?? new Win32UserActivityProbe(_timeProvider);
        _processStarter = processStarter ?? new DefaultGameProcessStarter();
        _options = options ?? new GameLaunchOptions();
        _logger = logger ?? NullLogger<GameLaunchService>.Instance;
        _sensitiveWorkerShutdownHooks = sensitiveWorkerShutdownHooks is not null
            ? sensitiveWorkerShutdownHooks.Where(static h => h is not null).ToArray()
            : [];
        _trainerSafetyGate = trainerSafetyGate ?? antiCheatGuard as ITrainerSafetyGate;
        _paths = paths;
        _sandboxOrphanProbe = sandboxOrphanProbe;
        _steamClientLocator = steamClientLocator ?? (steamShortcutService is null ? SteamClientLocator.Default : null);
        _steamShortcutService = steamShortcutService
            ?? new SteamShortcutService(_steamClientLocator ?? SteamClientLocator.Default, timeProvider: _timeProvider);
    }

    public ActiveGameSession? GetActiveSession(string gameId) =>
        !string.IsNullOrWhiteSpace(gameId) && _activeSessions.TryGetValue(gameId, out var session)
            ? session
            : null;

    public void RegisterTrustedOverlayWindow(nint windowHandle, int processId)
    {
        if (windowHandle != nint.Zero)
        {
            _trustedOverlayWindows[windowHandle] = processId;
        }
    }

    public void UnregisterTrustedOverlayWindow(nint windowHandle)
    {
        if (windowHandle != nint.Zero)
        {
            _trustedOverlayWindows.TryRemove(windowHandle, out _);
        }
    }

    /// <summary>
    /// Awaits natural or user-requested termination of an active game session (no-op if already exited).
    /// </summary>
    public async Task WaitForGameExitAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        if (_runningContexts.TryGetValue(gameId, out var ctx))
        {
            await ctx.CompletionTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<GameLaunchResult> LaunchGameAsync(
        string gameId,
        CancellationToken cancellationToken = default) =>
        LaunchGameAsync(gameId, new GameLaunchExecutionOptions(), cancellationToken);

    public async Task<GameLaunchResult> LaunchGameAsync(
        string gameId,
        GameLaunchExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        options ??= new GameLaunchExecutionOptions();

        if (string.IsNullOrWhiteSpace(gameId))
        {
            return GameLaunchResult.Failure("INVALID_GAME_ID", "游戏标识不能为空。");
        }

        if (_runningContexts.ContainsKey(gameId))
        {
            return GameLaunchResult.Failure("ALREADY_RUNNING", "该游戏当前已在运行中。");
        }

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GameLaunchResult.Failure("GAME_NOT_FOUND", "未在游戏库中找到该游戏记录。");
        }

        if (!Directory.Exists(game.InstallDir))
        {
            return GameLaunchResult.Failure("INSTALL_DIR_MISSING", $"游戏安装目录不存在：{game.InstallDir}");
        }

        string exeFullPath = game.ExecutableFullPath;
        if (!File.Exists(exeFullPath))
        {
            return GameLaunchResult.Failure("EXECUTABLE_NOT_FOUND", $"找不到游戏可执行文件：{exeFullPath}");
        }

        GameSteamLaunchSettings savedSteamSettings = await _database
            .GetGameSteamLaunchSettingsAsync(game.GameId, cancellationToken)
            .ConfigureAwait(false);
        GameLaunchMode effectiveLaunchMode = options.LaunchModeOverride ?? savedSteamSettings.LaunchMode;
        bool launchedViaSteam = effectiveLaunchMode == GameLaunchMode.Steam;

        // 1. Four-layer static anti-cheat determination (fixes Layer-1-only Math.Max stacking)
        //    plus independent VerifiedOfflineBypass evaluation without altering static anti_cheat_tier.
        //    D-03 / D-27 red line: official Steam launches MUST force isProtectedLaunch = true for Tier 2/3,
        //    explicitly ignoring game.VerifiedOfflineBypass because Steam launches the official AC bootstrapper.
        var (staticTier, resolvedSignature, isProtectedLaunch) = await ResolveLaunchProtectionAsync(
                game,
                launchedViaSteam,
                cancellationToken)
            .ConfigureAwait(false);

        // Hard-block non-Steam Tier 2/3 games from launching through a Steam non-Steam shortcut.
        if (launchedViaSteam && game.PlatformSource != PlatformSource.SteamLocal && staticTier >= AntiCheatTier.Tier2)
        {
            return GameLaunchResult.Failure(
                "STEAM_LAUNCH_BLOCKED_ANTICHEAT",
                $"当前游戏为非 Steam 来源且静态反作弊等级为 {staticTier}，为防止 Steam 覆盖层与内核反作弊冲突，已禁止通过 Steam 启动（仅正版 Steam 库游戏支持通过 Steam 启动）。");
        }

        var guardSnap = _antiCheatGuard.CurrentSnapshot;
        if (guardSnap.Phase is AntiCheatGuardPhase.ResidualGuardWatch or AntiCheatGuardPhase.CooldownLocked)
        {
            return GameLaunchResult.Failure(
                "ANTICHEAT_COOLDOWN_ACTIVE",
                $"反作弊安全守护余温冷却中（阶段：{guardSnap.Phase}，剩余 {guardSnap.RemainingCooldownSeconds:0.0} 秒），请等待冷却结束后再启动游戏。");
        }

        if (guardSnap.Phase == AntiCheatGuardPhase.ProtectedRunning
            && !string.Equals(guardSnap.ActiveProtectedGameId, game.GameId, StringComparison.OrdinalIgnoreCase))
        {
            return GameLaunchResult.Failure(
                "PROTECTED_GAME_ACTIVE",
                "当前已有受反作弊保护的网游正在运行，为保障账号安全已阻止并发启动。");
        }

        GameRecord effectiveGame = game with { AntiCheatTier = staticTier, AntiCheatSignature = resolvedSignature };

        // 1b. Resolve Steam launch URI before purging workers or inserting a session ledger row.
        string? steamLaunchUri = null;
        bool steamWasRunning = false;
        if (launchedViaSteam)
        {
            var (steamOk, resolvedUri, resolvedSteamRunning, steamErrorCode, steamErrorMessage) =
                await ResolveSteamLaunchTargetAsync(effectiveGame, savedSteamSettings, options, cancellationToken)
                    .ConfigureAwait(false);

            if (!steamOk || string.IsNullOrWhiteSpace(resolvedUri))
            {
                return GameLaunchResult.Failure(
                    steamErrorCode ?? "STEAM_LAUNCH_BLOCKED",
                    steamErrorMessage ?? "无法通过 Steam 启动该游戏。");
            }

            steamLaunchUri = resolvedUri;
            steamWasRunning = resolvedSteamRunning;
        }

        // 2. LOOP-06: Tier 2/3 pre-launch automatic sensitive Worker purge (2s budget) + secondary clean-field re-check
        //    (including sandbox path prefix orphan check, Cheat Engine, renamed CE drivers, CEDRIVER*, WinRing0).
        //    SPAWN NEVER kills user-launched third-party tools; it refuses the launch and explains why.
        if (isProtectedLaunch)
        {
            var hookResidue = await PurgeSensitiveWorkersBeforeProtectedLaunchAsync(cancellationToken)
                .ConfigureAwait(false);

            var envReport = _antiCheatGuard.InspectHighRiskEnvironment();
            var sandboxOrphans = await DetectConfiguredSandboxOrphansAsync(cancellationToken)
                .ConfigureAwait(false);

            var blockingProcesses = new List<string>(envReport.BlockingProcesses.Count + hookResidue.Count + sandboxOrphans.Count);
            AddDistinct(blockingProcesses, envReport.BlockingProcesses);
            AddDistinct(blockingProcesses, hookResidue);
            AddDistinct(blockingProcesses, sandboxOrphans);

            var blockingDrivers = envReport.BlockingDrivers;

            if (blockingProcesses.Count > 0 || blockingDrivers.Count > 0)
            {
                var parts = new List<string>();
                if (blockingProcesses.Count > 0)
                {
                    parts.Add($"高危进程：{string.Join("、", blockingProcesses)}");
                }

                if (blockingDrivers.Count > 0)
                {
                    parts.Add($"高危内核驱动：{string.Join("、", blockingDrivers)}（需关闭相关工具并重启系统卸载驱动）");
                }

                return GameLaunchResult.Failure(
                    "HIGH_RISK_ENVIRONMENT_BLOCKED",
                    $"检测到与反作弊网游冲突的高危环境，已阻止启动 {staticTier} 受保护游戏以保护账号安全。{string.Join("；", parts)}。");
            }
        }

        bool suppressAchievementToast = launchedViaSteam
            && game.PlatformSource == PlatformSource.SteamLocal
            && (options.SuppressDuplicateAchievementToast
                || await _database.GetSteamSuppressDuplicateAchievementToastAsync(cancellationToken).ConfigureAwait(false));

        TimeSpan initialObservationWindow = launchedViaSteam
            ? (steamWasRunning ? _options.SteamRunningLaunchObservationWindow : _options.SteamLaunchObservationWindow)
            : _options.ShellBreakObservationWindow;

        double afkTimeoutSec = await ResolveAfkTimeoutSecondsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset startedUtc = _timeProvider.GetUtcNow();
        string sessionId = SpawnId.NewUlid(startedUtc);

        var sessionCtx = new RunningSessionContext(
            sessionId,
            effectiveGame,
            startedUtc,
            afkTimeoutSec,
            new PlaytimeTracker(_activityProbe))
        {
            IsProtectedLaunch = isProtectedLaunch,
            LaunchedViaSteam = launchedViaSteam,
            SuppressAchievementToast = suppressAchievementToast,
            InitialObservationWindow = initialObservationWindow,
        };

        if (!_runningContexts.TryAdd(game.GameId, sessionCtx))
        {
            sessionCtx.Dispose();
            return GameLaunchResult.Failure("ALREADY_RUNNING", "该游戏当前已在运行中。");
        }

        string workingDir = Path.GetDirectoryName(exeFullPath) ?? game.InstallDir;
        GameProcessStartRequest startRequest = launchedViaSteam
            ? new GameProcessStartRequest(
                ExecutableFullPath: steamLaunchUri!,
                WorkingDirectory: string.Empty,
                Arguments: null,
                CreateNoWindow: false)
            : new GameProcessStartRequest(
                ExecutableFullPath: exeFullPath,
                WorkingDirectory: workingDir,
                Arguments: game.LaunchArguments,
                CreateNoWindow: _options.ForceCreateNoWindow);

        bool protectedGuardNotified = false;
        try
        {
            await _database.InsertPlaySessionAsync(
                new PlaySessionRecord
                {
                    SessionId = sessionId,
                    GameId = game.GameId,
                    StartedUtc = startedUtc,
                },
                cancellationToken).ConfigureAwait(false);

            ActiveGameSession initialSession;
            bool useDecoupledLauncher = isProtectedLaunch || launchedViaSteam;

            if (useDecoupledLauncher)
            {
                // Tier 2 / Tier 3 and Steam-delegated launches ALWAYS use Explorer Shell COM decoupling
                // + 0x1000 InstallDir directory tracking (NEVER attach steam.exe to JobObjectProcessTracker).
                if (isProtectedLaunch)
                {
                    _antiCheatGuard.NotifyProtectedGameStarted(game.GameId, staticTier);
                    protectedGuardNotified = true;
                }

                await _processStarter.StartExplorerDecoupledAsync(startRequest, cancellationToken).ConfigureAwait(false);

                sessionCtx.IsExplorerDecoupled = true;
                sessionCtx.UseDirectoryTrackingOnly = true;
                sessionCtx.Playtime.TransitionExplicitState(PlaySessionActivityState.ShellBreakingWatch);

                initialSession = new ActiveGameSession
                {
                    SessionId = sessionId,
                    GameId = game.GameId,
                    GameTitle = game.Title,
                    InstallDir = game.InstallDir,
                    AntiCheatTier = staticTier,
                    State = PlaySessionActivityState.ShellBreakingWatch,
                    RootProcessId = 0,
                    TrackedProcessIds = [],
                    StartedUtc = startedUtc,
                    LaunchedViaSteam = launchedViaSteam,
                    SuppressAchievementToast = suppressAchievementToast,
                };
            }
            else
            {
                // Tier 0 / Tier 1 (or verified offline-bypassed Tier 2 non-AC executable): Direct start + Non-Killing JobObject + IOCP tree tracking.
                var jobTracker = new JobObjectProcessTracker();
                sessionCtx.JobTracker = jobTracker;

                var proc = _processStarter.StartDirectProcess(startRequest);
                sessionCtx.DirectProcess = proc;
                int rootPid = SafeGetProcessId(proc);
                sessionCtx.RootProcessId = rootPid;

                var attachOutcome = jobTracker.TryAttachProcess(proc.SafeHandle, rootPid);
                sessionCtx.JobAttachOutcome = attachOutcome;

                int[] initialPids = rootPid > 0 ? [rootPid] : [];
                sessionCtx.Playtime.TransitionExplicitState(PlaySessionActivityState.Active);

                initialSession = new ActiveGameSession
                {
                    SessionId = sessionId,
                    GameId = game.GameId,
                    GameTitle = game.Title,
                    InstallDir = game.InstallDir,
                    AntiCheatTier = staticTier,
                    State = PlaySessionActivityState.Active,
                    RootProcessId = rootPid,
                    TrackedProcessIds = initialPids,
                    StartedUtc = startedUtc,
                    LaunchedViaSteam = false,
                    SuppressAchievementToast = false,
                };
            }

            _activeSessions[game.GameId] = initialSession;

            await PublishSessionEventAsync(
                new ActiveGameSessionChangedEvent(
                    GameId: game.GameId,
                    Session: initialSession,
                    CurrentState: initialSession.State,
                    CompletedSession: null,
                    OccurredUtc: startedUtc,
                    LaunchedViaSteam: launchedViaSteam,
                    SuppressAchievementToast: suppressAchievementToast),
                publishToBus: true).ConfigureAwait(false);

            // Start asynchronous lifecycle & 1Hz playtime monitor.
            sessionCtx.MonitorTask = Task.Run(() => MonitorSessionLifecycleAsync(sessionCtx), CancellationToken.None);

            return GameLaunchResult.Success(initialSession);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "Failed launching game {GameId} ({Title}).", game.GameId, game.Title);
            }

            _runningContexts.TryRemove(game.GameId, out _);
            _activeSessions.TryRemove(game.GameId, out _);

            if (isProtectedLaunch && protectedGuardNotified)
            {
                _ = _antiCheatGuard.NotifyProtectedGameExitedAsync(game.GameId, staticTier, CancellationToken.None);
            }

            sessionCtx.Dispose();
            return GameLaunchResult.Failure("LAUNCH_EXCEPTION", $"启动游戏失败：{ex.Message}");
        }
    }

    public async Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var result = await StopGameAsync(
            gameId,
            forceKill: false,
            gracefulTimeout: null,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async Task<GameStopResult> StopGameAsync(
        string gameId,
        bool forceKill,
        TimeSpan? gracefulTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId) || !_runningContexts.TryGetValue(gameId, out var ctx))
        {
            return GameStopResult.NotRunning();
        }

        PlaySessionActivityState currentState = _activeSessions.TryGetValue(gameId, out var currentBeforeStop)
            ? currentBeforeStop.State
            : PlaySessionActivityState.Active;

        var (attemptGeneration, supersededByForceKill) = ctx.BeginStopAttempt(forceKill, currentState);
        if (supersededByForceKill)
        {
            // A force-kill is already terminating this session; await its completion without reverting Stopping state.
            TimeSpan joinWait = _options.ForceKillWaitTimeout > TimeSpan.Zero
                ? _options.ForceKillWaitTimeout
                : TimeSpan.FromSeconds(3);
            bool completed = await WaitForSessionCompletionAsync(ctx, joinWait, cancellationToken).ConfigureAwait(false);
            return completed
                ? GameStopResult.ForceKilled()
                : GameStopResult.GracefulTimeout("强制终止正在进行中，等待进程树退出。");
        }

        if (_activeSessions.TryGetValue(gameId, out var current))
        {
            var stoppingSession = current with { State = PlaySessionActivityState.Stopping };
            _activeSessions[gameId] = stoppingSession;
            await PublishSessionEventAsync(
                new ActiveGameSessionChangedEvent(
                    GameId: gameId,
                    Session: stoppingSession,
                    CurrentState: PlaySessionActivityState.Stopping,
                    OccurredUtc: _timeProvider.GetUtcNow(),
                    LaunchedViaSteam: ctx.LaunchedViaSteam,
                    SuppressAchievementToast: ctx.SuppressAchievementToast),
                publishToBus: true).ConfigureAwait(false);
        }

        var pidsToStop = CollectLiveSessionPids(ctx);

        // If the session is currently waiting in ShellBreakingWatch (e.g. waiting for Steam or a decoupled launcher
        // to spawn the game) and no game process has started yet, cancel the watch loop immediately so StopGameAsync
        // cleanly tears down the session with zero leaked tasks or handles.
        if (currentState == PlaySessionActivityState.ShellBreakingWatch
            && pidsToStop.Count == 0
            && FindDirectoryGamePids(ctx.Game.InstallDir).Count == 0)
        {
            ctx.CancelMonitor();
            TimeSpan watchFlushWait = _options.ForceKillWaitTimeout > TimeSpan.Zero
                ? _options.ForceKillWaitTimeout
                : TimeSpan.FromSeconds(3);
            _ = await WaitForSessionCompletionAsync(ctx, watchFlushWait, cancellationToken).ConfigureAwait(false);
            return forceKill ? GameStopResult.ForceKilled() : GameStopResult.StoppedGracefully();
        }

        if (!forceKill)
        {
            foreach (int pid in pidsToStop)
            {
                TryRequestGracefulClose(pid);
            }

            TimeSpan effectiveGracefulTimeout = gracefulTimeout ?? _options.GracefulStopTimeout;
            if (effectiveGracefulTimeout < TimeSpan.Zero)
            {
                effectiveGracefulTimeout = TimeSpan.Zero;
            }

            bool exitedGracefully;
            try
            {
                exitedGracefully = await WaitForSessionCompletionAsync(ctx, effectiveGracefulTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled waiting for graceful stop: restore active tracking unless a concurrent forceKill took over.
                await RestoreSessionIfGracefulAttemptStillOwnerAsync(gameId, ctx, attemptGeneration)
                    .ConfigureAwait(false);
                throw;
            }

            if (exitedGracefully)
            {
                return ctx.IsForceKillActive
                    ? GameStopResult.ForceKilled()
                    : GameStopResult.StoppedGracefully();
            }

            // If a concurrent forceKill took over while we were waiting, do not clear StopRequested or revert Stopping!
            if (ctx.IsForceKillActive)
            {
                TimeSpan joinWait = _options.ForceKillWaitTimeout > TimeSpan.Zero
                    ? _options.ForceKillWaitTimeout
                    : TimeSpan.FromSeconds(3);
                bool finished = await WaitForSessionCompletionAsync(ctx, joinWait, cancellationToken).ConfigureAwait(false);
                return finished
                    ? GameStopResult.ForceKilled()
                    : GameStopResult.GracefulTimeout("强制终止正在进行中，等待进程树退出。");
            }

            // Re-check liveness at the boundary in case processes exited right as the grace window elapsed.
            if (CollectLiveSessionPids(ctx).Count == 0)
            {
                TimeSpan flushWait = _options.ForceKillWaitTimeout > TimeSpan.Zero
                    ? _options.ForceKillWaitTimeout
                    : TimeSpan.FromSeconds(2);
                _ = await WaitForSessionCompletionAsync(ctx, flushWait, cancellationToken).ConfigureAwait(false);
                return GameStopResult.StoppedGracefully();
            }

            // Graceful close timed out while the game is still running: NEVER force-kill behind the player's back.
            // Restore tracking state cleanly ONLY if no newer stop/forceKill attempt superseded this generation.
            await RestoreSessionIfGracefulAttemptStillOwnerAsync(gameId, ctx, attemptGeneration)
                .ConfigureAwait(false);

            return GameStopResult.GracefulTimeout();
        }

        // Explicit forceKill == true: optional brief graceful window when explicitly specified, then terminate process tree.
        if (gracefulTimeout is { } explicitGrace && explicitGrace > TimeSpan.Zero)
        {
            foreach (int pid in pidsToStop)
            {
                TryRequestGracefulClose(pid);
            }

            if (await WaitForSessionCompletionAsync(ctx, explicitGrace, cancellationToken).ConfigureAwait(false))
            {
                return GameStopResult.StoppedGracefully();
            }
        }

        foreach (int pid in CollectLiveSessionPids(ctx))
        {
            TryForceTerminateProcessTree(pid);
        }

        TimeSpan killWait = _options.ForceKillWaitTimeout > TimeSpan.Zero
            ? _options.ForceKillWaitTimeout
            : TimeSpan.FromSeconds(3);
        bool completedAfterKill = await WaitForSessionCompletionAsync(ctx, killWait, cancellationToken)
            .ConfigureAwait(false);
        if (!completedAfterKill)
        {
            // Force-cancel monitor loop if external process resisted termination.
            ctx.CancelMonitor();
            _ = await WaitForSessionCompletionAsync(ctx, TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        return GameStopResult.ForceKilled();
    }

    private async Task RestoreSessionIfGracefulAttemptStillOwnerAsync(
        string gameId,
        RunningSessionContext ctx,
        long attemptGeneration)
    {
        if (!ctx.TryAbortGracefulStopAttempt(attemptGeneration, out PlaySessionActivityState stateToRestore))
        {
            return;
        }

        if (_runningContexts.ContainsKey(gameId) && _activeSessions.TryGetValue(gameId, out var stillActive))
        {
            var restoredSession = stillActive with
            {
                State = stateToRestore,
                TrackedProcessIds = CollectLiveSessionPids(ctx),
            };
            _activeSessions[gameId] = restoredSession;
            await PublishSessionEventAsync(
                new ActiveGameSessionChangedEvent(
                    GameId: gameId,
                    Session: restoredSession,
                    CurrentState: stateToRestore,
                    OccurredUtc: _timeProvider.GetUtcNow(),
                    LaunchedViaSteam: ctx.LaunchedViaSteam,
                    SuppressAchievementToast: ctx.SuppressAchievementToast),
                publishToBus: true).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForSessionCompletionAsync(
        RunningSessionContext ctx,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (ctx.CompletionTcs.Task.IsCompleted)
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return ctx.CompletionTcs.Task.IsCompleted;
        }

        try
        {
            if (ReferenceEquals(_timeProvider, TimeProvider.System))
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(timeout);
                await ctx.CompletionTcs.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
                return true;
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delayTask = DelayWithTimeProviderAsync(timeout, delayCts.Token);
            Task completed = await Task.WhenAny(ctx.CompletionTcs.Task, delayTask).ConfigureAwait(false);
            delayCts.Cancel();
            if (ReferenceEquals(completed, ctx.CompletionTcs.Task))
            {
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ctx.CompletionTcs.Task.IsCompleted;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ctx.CompletionTcs.Task.IsCompleted;
        }
    }

    // -------------------------------------------------------------------------
    // Core Dual-Engine Lifecycle & 1Hz Playtime Loop
    // -------------------------------------------------------------------------

    private async Task MonitorSessionLifecycleAsync(RunningSessionContext ctx)
    {
        var token = ctx.MonitorCts.Token;
        int? exitCode = null;
        int tickCounter = 0;

        bool canShellBreak = _options.AlwaysObserveShellBreakWindow
            || ctx.IsExplorerDecoupled
            || HasPotentialShellBreakTargets(ctx.Game.InstallDir);

        try
        {
            // If Explorer-decoupled (Tier 2/3 or Steam launch), first discover initial game process via 0x1000 directory scan.
            if (ctx.IsExplorerDecoupled)
            {
                var adopted = await WaitInShellBreakingWindowAsync(ctx, ctx.InitialObservationWindow, token).ConfigureAwait(false);
                if (adopted.Count == 0)
                {
                    return;
                }

                ctx.SetDirectoryTrackedPids(adopted);
            }

            while (!token.IsCancellationRequested)
            {
                var livePids = CollectLiveSessionPids(ctx);

                if (livePids.Count == 0)
                {
                    // Check immediately via 0x1000 directory prefix if a child/restarted game exe is already alive.
                    var dirMatches = FindDirectoryGamePids(ctx.Game.InstallDir);
                    if (dirMatches.Count > 0 && !ctx.StopRequested)
                    {
                        ctx.SwitchToDirectoryTracking(dirMatches);
                        livePids = dirMatches;
                    }
                    else
                    {
                        TimeSpan elapsed = _timeProvider.GetUtcNow() - ctx.StartedUtc;
                        bool withinTrigger = elapsed <= _options.ShellBreakTriggerThreshold
                                             && _options.ShellBreakObservationWindow > TimeSpan.Zero;

                        if (withinTrigger && canShellBreak && !ctx.StopRequested)
                        {
                            await TransitionToShellBreakingStateAsync(ctx).ConfigureAwait(false);
                            var adopted = await WaitInShellBreakingWindowAsync(ctx, _options.ShellBreakObservationWindow, token).ConfigureAwait(false);
                            if (adopted.Count > 0 && !ctx.StopRequested)
                            {
                                ctx.SwitchToDirectoryTracking(adopted);
                                livePids = [.. adopted];
                            }
                            else
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // Wait one playtime sample interval (1Hz in production), waking early if JobObject hits 0!
                bool wokeEarlyOnJobZero = await WaitNextSampleOrJobZeroAsync(ctx, _options.PlaytimeSampleInterval, token)
                    .ConfigureAwait(false);

                if (wokeEarlyOnJobZero)
                {
                    // Loop back immediately to evaluate shell-breaking vs clean exit without adding an extra 1s delay.
                    continue;
                }

                // Re-verify process liveness right after the 1s interval before crediting 1 second of playtime.
                livePids = CollectLiveSessionPids(ctx);
                if (livePids.Count == 0)
                {
                    continue;
                }

                var (trustedHwnds, trustedPids) = SnapshotTrustedOverlayTargets();
                var tick = ctx.Playtime.SampleOneSecond(
                    livePids,
                    trustedHwnds,
                    trustedPids,
                    ctx.AfkTimeoutSeconds);

                int primaryPid = ctx.RootProcessId > 0 && livePids.Contains(ctx.RootProcessId)
                    ? ctx.RootProcessId
                    : livePids[0];

                bool pidsChanged = !SequenceEqualUnordered(ctx.LastPublishedPids, livePids);
                ctx.LastPublishedPids = livePids;
                ctx.RootProcessId = primaryPid;

                bool isStopping = ctx.StopRequested;
                PlaySessionActivityState effectiveState = isStopping ? PlaySessionActivityState.Stopping : tick.State;

                var updatedSession = new ActiveGameSession
                {
                    SessionId = ctx.SessionId,
                    GameId = ctx.Game.GameId,
                    GameTitle = ctx.Game.Title,
                    InstallDir = ctx.Game.InstallDir,
                    AntiCheatTier = ctx.Game.AntiCheatTier,
                    State = effectiveState,
                    RootProcessId = primaryPid,
                    TrackedProcessIds = livePids,
                    StartedUtc = ctx.StartedUtc,
                    SessionActiveSec = tick.ActiveSec,
                    SessionBackgroundSec = tick.BackgroundSec,
                    SessionAfkSec = tick.AfkSec,
                    LaunchedViaSteam = ctx.LaunchedViaSteam,
                    SuppressAchievementToast = ctx.SuppressAchievementToast,
                };

                _activeSessions[ctx.Game.GameId] = updatedSession;

                bool publishToBus = !isStopping && (tick.StateChanged || pidsChanged || tickCounter == 0);
                await PublishSessionEventAsync(
                    new ActiveGameSessionChangedEvent(
                        GameId: ctx.Game.GameId,
                        Session: updatedSession,
                        CurrentState: effectiveState,
                        CompletedSession: null,
                        OccurredUtc: _timeProvider.GetUtcNow(),
                        LaunchedViaSteam: ctx.LaunchedViaSteam,
                        SuppressAchievementToast: ctx.SuppressAchievementToast),
                    publishToBus: publishToBus).ConfigureAwait(false);

                tickCounter++;
                if (tickCounter % 30 == 0)
                {
                    await TryCheckpointSessionAsync(ctx).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown or forced stop.
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "Unexpected error monitoring game session {SessionId} ({Title}).", ctx.SessionId, ctx.Game.Title);
            }
        }
        finally
        {
            exitCode = TryReadProcessExitCode(ctx.DirectProcess);
            DateTimeOffset endedUtc = _timeProvider.GetUtcNow();

            _activeSessions.TryRemove(ctx.Game.GameId, out _);
            _runningContexts.TryRemove(ctx.Game.GameId, out _);

            var completedRecord = new PlaySessionRecord
            {
                SessionId = ctx.SessionId,
                GameId = ctx.Game.GameId,
                StartedUtc = ctx.StartedUtc,
                EndedUtc = endedUtc,
                ActiveSec = ctx.Playtime.ActiveSec,
                BackgroundSec = ctx.Playtime.BackgroundSec,
                AfkSec = ctx.Playtime.AfkSec,
                ExitCode = exitCode,
            };

            try
            {
                await _database.CompletePlaySessionAndAccumulateAsync(completedRecord, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(ex, "Failed persisting completed play session {SessionId}.", ctx.SessionId);
                }
            }

            if (ctx.IsProtectedLaunch)
            {
                // Run daemon + session kernel AC driver exit verification + mandatory 15s post-exit cooldown asynchronously.
                _ = Task.Run(() => _antiCheatGuard.NotifyProtectedGameExitedAsync(
                    ctx.Game.GameId,
                    ctx.Game.AntiCheatTier,
                    CancellationToken.None));
            }

            await PublishSessionEventAsync(
                new ActiveGameSessionChangedEvent(
                    GameId: ctx.Game.GameId,
                    Session: null,
                    CurrentState: PlaySessionActivityState.Stopped,
                    CompletedSession: completedRecord,
                    OccurredUtc: endedUtc,
                    LaunchedViaSteam: ctx.LaunchedViaSteam,
                    SuppressAchievementToast: ctx.SuppressAchievementToast),
                publishToBus: true).ConfigureAwait(false);

            ctx.CompletionTcs.TrySetResult();
            ctx.Dispose();
        }
    }

    private async Task TransitionToShellBreakingStateAsync(RunningSessionContext ctx)
    {
        bool changed = ctx.Playtime.TransitionExplicitState(PlaySessionActivityState.ShellBreakingWatch);
        if (_activeSessions.TryGetValue(ctx.Game.GameId, out var current))
        {
            var watchSession = current with
            {
                State = PlaySessionActivityState.ShellBreakingWatch,
                TrackedProcessIds = [],
            };
            _activeSessions[ctx.Game.GameId] = watchSession;

            if (changed)
            {
                await PublishSessionEventAsync(
                    new ActiveGameSessionChangedEvent(
                        GameId: ctx.Game.GameId,
                        Session: watchSession,
                        CurrentState: PlaySessionActivityState.ShellBreakingWatch,
                        OccurredUtc: _timeProvider.GetUtcNow(),
                        LaunchedViaSteam: ctx.LaunchedViaSteam,
                        SuppressAchievementToast: ctx.SuppressAchievementToast),
                    publishToBus: true).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<int>> WaitInShellBreakingWindowAsync(
        RunningSessionContext ctx,
        TimeSpan observationWindow,
        CancellationToken cancellationToken)
    {
        if (observationWindow <= TimeSpan.Zero)
        {
            return FindDirectoryGamePids(ctx.Game.InstallDir);
        }

        DateTimeOffset deadline = _timeProvider.GetUtcNow() + observationWindow;
        TimeSpan pollStep = _options.ShellBreakPollInterval > TimeSpan.Zero
            ? _options.ShellBreakPollInterval
            : TimeSpan.FromMilliseconds(250);

        while (!cancellationToken.IsCancellationRequested && !ctx.StopRequested)
        {
            var matches = FindDirectoryGamePids(ctx.Game.InstallDir);
            if (matches.Count > 0)
            {
                return matches;
            }

            TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan sleep = remaining < pollStep ? remaining : pollStep;
            await DelayWithTimeProviderAsync(sleep, cancellationToken).ConfigureAwait(false);
        }

        return [];
    }

    private async Task<bool> WaitNextSampleOrJobZeroAsync(
        RunningSessionContext ctx,
        TimeSpan sampleInterval,
        CancellationToken cancellationToken)
    {
        TimeSpan effectiveDelay = sampleInterval > TimeSpan.Zero ? sampleInterval : TimeSpan.FromMilliseconds(50);

        if (!ctx.UseDirectoryTrackingOnly
            && ctx.JobTracker is { IsActiveProcessZero: false } jobTracker
            && ctx.JobAttachOutcome == JobAttachOutcome.Attached)
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delayTask = DelayWithTimeProviderAsync(effectiveDelay, delayCts.Token);
            Task jobZeroTask = jobTracker.WaitForActiveProcessZeroAsync(delayCts.Token);

            Task completed = await Task.WhenAny(delayTask, jobZeroTask).ConfigureAwait(false);
            delayCts.Cancel();

            if (ReferenceEquals(completed, jobZeroTask) && jobTracker.IsActiveProcessZero)
            {
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        await DelayWithTimeProviderAsync(effectiveDelay, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static List<int> CollectLiveSessionPids(RunningSessionContext ctx)
    {
        var live = new HashSet<int>();

        if (!ctx.UseDirectoryTrackingOnly && ctx.JobTracker is not null && ctx.JobAttachOutcome == JobAttachOutcome.Attached)
        {
            foreach (int pid in ctx.JobTracker.GetTrackedProcessIds())
            {
                if (pid > 4 && SafeProcessInspector.IsProcessAlive(pid))
                {
                    string? path = SafeProcessInspector.TryGetProcessExecutablePath(pid);
                    if (path is null || !SafeProcessInspector.IsAuxiliaryOrCrashProcessName(path))
                    {
                        live.Add(pid);
                    }
                }
            }
        }

        if (ctx.RootProcessId > 4 && SafeProcessInspector.IsProcessAlive(ctx.RootProcessId))
        {
            live.Add(ctx.RootProcessId);
        }

        if (ctx.UseDirectoryTrackingOnly || ctx.JobAttachOutcome == JobAttachOutcome.FallbackRequired)
        {
            foreach (int pid in ctx.GetDirectoryTrackedPids())
            {
                if (pid > 4 && SafeProcessInspector.IsProcessAlive(pid))
                {
                    live.Add(pid);
                }
            }

            if (live.Count == 0)
            {
                foreach (int pid in FindDirectoryGamePids(ctx.Game.InstallDir))
                {
                    live.Add(pid);
                }
            }
        }

        return [.. live];
    }

    private static List<int> FindDirectoryGamePids(string installDir)
    {
        var entries = SafeProcessInspector.FindProcessesUnderDirectory(installDir, excludeAuxiliary: true);
        if (entries.Count == 0)
        {
            return [];
        }

        int selfPid = Environment.ProcessId;
        var pids = new List<int>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.ProcessId != selfPid
                && entry.ProcessId > 4
                && !IsSteamClientHelperProcessName(entry.ExeFileName))
            {
                pids.Add(entry.ProcessId);
            }
        }

        return pids;
    }

    private static bool IsSteamClientHelperProcessName(string? exeFileName) =>
        !string.IsNullOrWhiteSpace(exeFileName)
        && (string.Equals(exeFileName, SteamClientLocator.SteamExecutableFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(exeFileName, "steamwebhelper.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exeFileName, "gameoverlayui.exe", StringComparison.OrdinalIgnoreCase));

    private static bool HasPotentialShellBreakTargets(string installDir)
    {
        try
        {
            int nonAuxExeCount = 0;
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };

            foreach (string file in Directory.EnumerateFiles(installDir, "*", opts))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("EOSSDK", StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".info", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !SafeProcessInspector.IsAuxiliaryOrCrashProcessName(name))
                {
                    nonAuxExeCount++;
                    if (nonAuxExeCount >= 2)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private async Task<(bool Succeeded, string? LaunchUri, bool SteamWasRunning, string? ErrorCode, string? ErrorMessage)>
        ResolveSteamLaunchTargetAsync(
            GameRecord effectiveGame,
            GameSteamLaunchSettings savedSettings,
            GameLaunchExecutionOptions options,
            CancellationToken cancellationToken)
    {
        string? steamInstallDirOverride = await _database
            .GetSteamInstallDirectoryOverrideAsync(cancellationToken)
            .ConfigureAwait(false);
        uint? targetAccountId = await _database
            .GetSteamTargetAccountIdAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(options.SteamLaunchUri))
        {
            string explicitUri = options.SteamLaunchUri.Trim();
            if (!DefaultGameProcessStarter.IsProtocolUriTarget(explicitUri)
                || !explicitUri.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, false, "INVALID_STEAM_LAUNCH_URI", $"无效的 Steam 启动协议地址：{explicitUri}");
            }

            bool wasRunning = options.SteamWasRunningBeforeLaunch
                ?? (_steamClientLocator?.Discover(steamInstallDirOverride, targetAccountId).IsSteamRunning ?? false);
            return (true, explicitUri, wasRunning, null, null);
        }

        SteamLaunchPreparationResult prep = await _steamShortcutService
            .PrepareLaunchAsync(
                effectiveGame,
                savedSettings,
                steamInstallDirOverride,
                targetAccountId,
                cancellationToken)
            .ConfigureAwait(false);

        switch (prep.Status)
        {
            case SteamLaunchPreparationStatus.ReadyOfficialSteam:
            case SteamLaunchPreparationStatus.ReadyNonSteamShortcut:
            {
                if (string.IsNullOrWhiteSpace(prep.LaunchUri))
                {
                    return (false, null, false, "STEAM_LAUNCH_URI_MISSING", "未能解析有效的 Steam 启动协议地址（steam://rungameid/...）。");
                }

                if (prep.ShortcutAppId is { } readyShortcutId && readyShortcutId != savedSettings.RegisteredShortcutAppId)
                {
                    await _database
                        .UpdateGameRegisteredShortcutAppIdAsync(effectiveGame.GameId, readyShortcutId, cancellationToken)
                        .ConfigureAwait(false);
                }

                bool wasRunning = options.SteamWasRunningBeforeLaunch
                    ?? (_steamClientLocator?.Discover(steamInstallDirOverride, targetAccountId).IsSteamRunning ?? false);
                return (true, prep.LaunchUri, wasRunning, null, null);
            }

            case SteamLaunchPreparationStatus.NeedsShortcutWriteSteamClosed:
            {
                SteamShortcutSyncResult sync = await _steamShortcutService
                    .ApplyShortcutAndRestartIfNeededAsync(
                        effectiveGame,
                        savedSettings,
                        allowGracefulSteamRestart: false,
                        steamInstallDirOverride,
                        targetAccountId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!sync.Succeeded || string.IsNullOrWhiteSpace(sync.LaunchUri))
                {
                    return (
                        false,
                        null,
                        false,
                        "STEAM_SHORTCUT_SYNC_FAILED",
                        sync.ErrorMessage ?? "写入 Steam shortcuts.vdf 非 Steam 快捷方式失败。");
                }

                if (sync.ShortcutAppId is { } syncedAppId)
                {
                    await _database
                        .UpdateGameRegisteredShortcutAppIdAsync(effectiveGame.GameId, syncedAppId, cancellationToken)
                        .ConfigureAwait(false);
                }

                bool wasRunning = options.SteamWasRunningBeforeLaunch ?? false;
                return (true, sync.LaunchUri, wasRunning, null, null);
            }

            case SteamLaunchPreparationStatus.NeedsSteamRestartToSync:
                return (
                    false,
                    null,
                    true,
                    "STEAM_RESTART_REQUIRED",
                    prep.ErrorMessage
                    ?? "Steam 客户端当前正在运行，首次登记或更新非 Steam 快捷方式需经由用户确认后优雅重启 Steam；SPAWN 不会静默强杀 Steam。");

            case SteamLaunchPreparationStatus.BlockedTierAntiCheat:
                return (
                    false,
                    null,
                    false,
                    "STEAM_LAUNCH_BLOCKED_ANTICHEAT",
                    prep.ErrorMessage ?? "当前非 Steam 反作弊受保护游戏禁止通过 Steam 快捷方式启动。");

            case SteamLaunchPreparationStatus.BlockedSteamNotFound:
                return (
                    false,
                    null,
                    false,
                    "STEAM_NOT_FOUND",
                    prep.ErrorMessage ?? "未检测到本机 Steam 客户端，请在「设置 → 常规与游戏库」中手动指定 Steam 路径。");

            case SteamLaunchPreparationStatus.BlockedNoSteamAccount:
                return (
                    false,
                    null,
                    false,
                    "STEAM_ACCOUNT_NOT_FOUND",
                    prep.ErrorMessage ?? "已检测到 Steam 客户端，但未发现可用的 Steam 账号数据目录（userdata）。");

            case SteamLaunchPreparationStatus.BlockedInvalidExecutable:
                return (
                    false,
                    null,
                    false,
                    "EXECUTABLE_NOT_FOUND",
                    prep.ErrorMessage ?? $"找不到有效的游戏可执行文件：{effectiveGame.ExecutableFullPath}");

            default:
                return (
                    false,
                    null,
                    false,
                    "STEAM_LAUNCH_BLOCKED",
                    prep.ErrorMessage ?? "当前游戏无法通过 Steam 启动。");
        }
    }

    private async Task<(AntiCheatTier StaticTier, string? Signature, bool IsProtectedLaunch)> ResolveLaunchProtectionAsync(
        GameRecord game,
        bool launchedViaSteam,
        CancellationToken cancellationToken)
    {
        AntiCheatTier staticTier;
        string? signature;
        bool? hasSinglePlayer = null;

        if (_trainerSafetyGate is not null)
        {
            var fourLayer = await _trainerSafetyGate.InspectGameFourLayerAsync(
                game,
                allowOnlineLookup: false,
                ct: cancellationToken).ConfigureAwait(false);
            staticTier = fourLayer.EffectiveTier > game.AntiCheatTier ? fourLayer.EffectiveTier : game.AntiCheatTier;
            signature = fourLayer.PrimarySignature ?? game.AntiCheatSignature;
            hasSinglePlayer = fourLayer.HasSinglePlayerCategory;
        }
        else
        {
            var liveAc = _antiCheatGuard.InspectGameDirectory(game.InstallDir);
            var awacy = AreWeAntiCheatYetIndex.Default.Inspect(game);
            staticTier = liveAc.Tier > game.AntiCheatTier ? liveAc.Tier : game.AntiCheatTier;
            if (awacy is not null && awacy.Tier > staticTier)
            {
                staticTier = awacy.Tier;
            }

            signature = liveAc.Signature ?? awacy?.PrimarySignature ?? game.AntiCheatSignature;
            hasSinglePlayer = awacy?.HasSinglePlayerHint;
        }

        // CRITICAL RED LINE (D-03 / D-27): Launching an official SteamLocal Tier 2/3 game via Steam
        // MUST explicitly ignore VerifiedOfflineBypass and enforce full protected launch isolation + 15s cooldown.
        bool verifiedOfflineBypassActive = false;
        if (!launchedViaSteam
            && staticTier == AntiCheatTier.Tier2
            && game.VerifiedOfflineBypass
            && hasSinglePlayer == true
            && !AntiCheatFeatureCatalog.IsAntiCheatLauncherExecutable(game.ExecutableRelPath))
        {
            var liveProbe = _trainerSafetyGate is not null
                ? await _trainerSafetyGate.ProbeLiveAntiCheatAsync(cancellationToken).ConfigureAwait(false)
                : AntiCheatLiveEvaluator.Evaluate(
                    DefaultAntiCheatSystemProbe.Instance,
                    _timeProvider.GetUtcNow(),
                    resolveFullPaths: false,
                    out _);
            verifiedOfflineBypassActive = liveProbe.IsClearForTrainer;
        }

        bool isProtectedLaunch = staticTier >= AntiCheatTier.Tier2 && !verifiedOfflineBypassActive;
        return (staticTier, signature, isProtectedLaunch);
    }

    private async Task<List<string>> PurgeSensitiveWorkersBeforeProtectedLaunchAsync(CancellationToken cancellationToken)
    {
        var residue = new List<string>();
        if (_sensitiveWorkerShutdownHooks.Length == 0)
        {
            return residue;
        }

        TimeSpan totalBudget = _options.SensitiveWorkerShutdownBudget > TimeSpan.Zero
            ? _options.SensitiveWorkerShutdownBudget
            : TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();

        foreach (var hook in _sensitiveWorkerShutdownHooks)
        {
            TimeSpan remaining = totalBudget - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                AddDistinct(residue, ["敏感 Worker 预清场超出 2 秒预算"]);
                break;
            }

            try
            {
                using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budgetCts.CancelAfter(remaining);

                var report = await hook.ShutdownAllForProtectedLaunchAsync(remaining, budgetCts.Token)
                    .ConfigureAwait(false);

                if (report is null)
                {
                    AddDistinct(residue, ["敏感 Worker 清场钩子返回空报告"]);
                    continue;
                }

                if (report.RemainingSandboxProcesses.Count > 0)
                {
                    AddDistinct(residue, report.RemainingSandboxProcesses);
                }

                if (!report.Succeeded)
                {
                    AddDistinct(residue, [string.IsNullOrWhiteSpace(report.Message) ? "敏感 Worker 清场未确认完成" : report.Message!]);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AddDistinct(residue, ["敏感 Worker 预清场超时（2 秒预算）"]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Sensitive worker shutdown hook failed during protected launch pre-flight.");
                }

                AddDistinct(residue, [$"敏感 Worker 预清场失败：{ex.GetType().Name}"]);
            }
        }

        return residue;
    }

    private async Task<IReadOnlyList<string>> DetectConfiguredSandboxOrphansAsync(CancellationToken cancellationToken)
    {
        var sandboxRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_paths is not null && !string.IsNullOrWhiteSpace(_paths.TrainerSandboxDirectory))
        {
            sandboxRoots.Add(_paths.TrainerSandboxDirectory);
        }

        try
        {
            var settings = await _database.GetTrainerSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(settings.CustomSandboxRoot))
            {
                sandboxRoots.Add(settings.CustomSandboxRoot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ignore settings lookup failure; default sandbox root (if present) is still swept.
        }

        if (sandboxRoots.Count == 0 && _sandboxOrphanProbe is null)
        {
            return [];
        }

        var found = new List<string>();
        if (sandboxRoots.Count == 0 && _sandboxOrphanProbe is not null)
        {
            AddDistinct(found, _sandboxOrphanProbe(string.Empty));
            return found;
        }

        foreach (string root in sandboxRoots)
        {
            IReadOnlyList<string> orphans = _sandboxOrphanProbe is not null
                ? _sandboxOrphanProbe(root)
                : HighRiskEnvironmentProbe.DetectSandboxOrphanProcesses(root);
            AddDistinct(found, orphans);
        }

        return found;
    }

    private static void AddDistinct(List<string> sink, IEnumerable<string> items)
    {
        foreach (string item in items)
        {
            if (!string.IsNullOrWhiteSpace(item) && !sink.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                sink.Add(item);
            }
        }
    }

    private async Task<double> ResolveAfkTimeoutSecondsAsync(CancellationToken cancellationToken)
    {
        string? raw = await _database.GetSettingAsync(AppSettingKeys.LauncherAfkTimeoutSec, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed >= 5.0)
        {
            return parsed;
        }

        return _options.DefaultAfkTimeoutSeconds;
    }

    private async Task TryCheckpointSessionAsync(RunningSessionContext ctx)
    {
        try
        {
            await _database.UpdatePlaySessionAsync(
                new PlaySessionRecord
                {
                    SessionId = ctx.SessionId,
                    GameId = ctx.Game.GameId,
                    StartedUtc = ctx.StartedUtc,
                    ActiveSec = ctx.Playtime.ActiveSec,
                    BackgroundSec = ctx.Playtime.BackgroundSec,
                    AfkSec = ctx.Playtime.AfkSec,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Non-fatal periodic session checkpoint failure for {SessionId}.", ctx.SessionId);
            }
        }
    }

    private async Task PublishSessionEventAsync(ActiveGameSessionChangedEvent evt, bool publishToBus)
    {
        try
        {
            SessionChanged?.Invoke(this, evt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SessionChanged subscriber threw an exception.");
        }

        if (publishToBus)
        {
            try
            {
                await _eventBus.PublishAsync(evt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventBus subscriber threw an exception handling ActiveGameSessionChangedEvent.");
            }
        }
    }

    private (HashSet<nint> Hwnds, HashSet<int> Pids) SnapshotTrustedOverlayTargets()
    {
        var hwnds = new HashSet<nint>();
        var pids = new HashSet<int>();
        foreach (var kvp in _trustedOverlayWindows)
        {
            if (kvp.Key != nint.Zero)
            {
                hwnds.Add(kvp.Key);
            }

            if (kvp.Value > 0)
            {
                pids.Add(kvp.Value);
            }
        }

        return (hwnds, pids);
    }

    private async Task DelayWithTimeProviderAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (ReferenceEquals(_timeProvider, TimeProvider.System))
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var timer = _timeProvider.CreateTimer(
            static s => ((TaskCompletionSource)s!).TrySetResult(),
            tcs,
            delay,
            Timeout.InfiniteTimeSpan);
        await using var reg = cancellationToken.Register(
            static s => ((TaskCompletionSource)s!).TrySetCanceled(),
            tcs);
        await tcs.Task.ConfigureAwait(false);
    }

    private static bool TryRequestGracefulClose(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                return true;
            }

            return proc.CloseMainWindow();
        }
        catch
        {
            // Process already exited, has no message window, or is inaccessible.
            return false;
        }
    }

    private static void TryForceTerminateProcessTree(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process already exited or inaccessible.
        }
    }

    private static int SafeGetProcessId(Process proc)
    {
        try
        {
            return proc.Id;
        }
        catch
        {
            return 0;
        }
    }

    private static int? TryReadProcessExitCode(Process? proc)
    {
        if (proc is null)
        {
            return null;
        }

        try
        {
            return proc.HasExited ? proc.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool SequenceEqualUnordered(IReadOnlyList<int> left, List<int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!right.Contains(left[i]))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var kvp in _runningContexts)
        {
            // Cancelling our internal monitor loop detaches SPAWN cleanly WITHOUT killing running games!
            kvp.Value.CancelMonitor();
            kvp.Value.Dispose();
        }

        _runningContexts.Clear();
        _activeSessions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var contexts = _runningContexts.Values.ToArray();
        foreach (var ctx in contexts)
        {
            ctx.CancelMonitor();
        }

        foreach (var ctx in contexts)
        {
            if (ctx.MonitorTask is not null)
            {
                try
                {
                    await ctx.MonitorTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore shutdown timeout.
                }
            }

            ctx.Dispose();
        }

        _runningContexts.Clear();
        _activeSessions.Clear();
    }

    // -------------------------------------------------------------------------
    // Per-Session Mutable State Container
    // -------------------------------------------------------------------------

    private sealed class RunningSessionContext(
        string sessionId,
        GameRecord game,
        DateTimeOffset startedUtc,
        double afkTimeoutSeconds,
        PlaytimeTracker playtime) : IDisposable
    {
        private readonly Lock _pidLock = new();
        private readonly Lock _stopLock = new();
        private List<int> _directoryTrackedPids = [];
        private bool _stopRequested;
        private bool _forceKillActive;
        private long _stopAttemptGeneration;
        private PlaySessionActivityState _stateBeforeStop = PlaySessionActivityState.Active;
        private int _disposed;

        public string SessionId { get; } = sessionId;
        public GameRecord Game { get; } = game;
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public double AfkTimeoutSeconds { get; } = afkTimeoutSeconds;
        public PlaytimeTracker Playtime { get; } = playtime;

        public CancellationTokenSource MonitorCts { get; } = new();
        public TaskCompletionSource CompletionTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? MonitorTask { get; set; }

        public Process? DirectProcess { get; set; }
        public JobObjectProcessTracker? JobTracker { get; set; }
        public JobAttachOutcome JobAttachOutcome { get; set; }
        public int RootProcessId { get; set; }
        public bool IsExplorerDecoupled { get; set; }
        public bool IsProtectedLaunch { get; set; }
        public bool LaunchedViaSteam { get; set; }
        public bool SuppressAchievementToast { get; set; }
        public TimeSpan InitialObservationWindow { get; set; }
        public volatile bool UseDirectoryTrackingOnly;
        public IReadOnlyList<int> LastPublishedPids { get; set; } = [];

        public bool StopRequested
        {
            get
            {
                lock (_stopLock)
                {
                    return _stopRequested;
                }
            }
        }

        public bool IsForceKillActive
        {
            get
            {
                lock (_stopLock)
                {
                    return _forceKillActive;
                }
            }
        }

        public (long Generation, bool SupersededByForceKill) BeginStopAttempt(
            bool forceKill,
            PlaySessionActivityState currentState)
        {
            lock (_stopLock)
            {
                if (!forceKill && _forceKillActive)
                {
                    return (_stopAttemptGeneration, true);
                }

                if (!_stopRequested
                    && currentState is not PlaySessionActivityState.Stopping and not PlaySessionActivityState.Stopped)
                {
                    _stateBeforeStop = currentState;
                }

                _stopRequested = true;
                if (forceKill)
                {
                    _forceKillActive = true;
                }

                long gen = ++_stopAttemptGeneration;
                return (gen, false);
            }
        }

        public bool TryAbortGracefulStopAttempt(long generation, out PlaySessionActivityState stateToRestore)
        {
            lock (_stopLock)
            {
                stateToRestore = _stateBeforeStop;
                if (_forceKillActive || _stopAttemptGeneration != generation)
                {
                    return false;
                }

                _stopRequested = false;
                return true;
            }
        }

        public void SwitchToDirectoryTracking(IReadOnlyList<int> pids)
        {
            UseDirectoryTrackingOnly = true;
            SetDirectoryTrackedPids(pids);
        }

        public void SetDirectoryTrackedPids(IReadOnlyList<int> pids)
        {
            lock (_pidLock)
            {
                _directoryTrackedPids = [.. pids];
                if (pids.Count > 0)
                {
                    RootProcessId = pids[0];
                }
            }
        }

        public IReadOnlyList<int> GetDirectoryTrackedPids()
        {
            lock (_pidLock)
            {
                return [.. _directoryTrackedPids];
            }
        }

        public void CancelMonitor()
        {
            try
            {
                MonitorCts.Cancel();
            }
            catch
            {
                // Ignore race.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CancelMonitor();
            JobTracker?.Dispose();
            DirectProcess?.Dispose();
            MonitorCts.Dispose();
        }
    }
}
