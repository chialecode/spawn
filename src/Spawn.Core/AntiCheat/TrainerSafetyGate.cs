using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;

namespace Spawn.Core.AntiCheat;

/// <summary>
/// Implements <see cref="ITrainerSafetyGate"/>:
/// <list type="number">
/// <item><description>Four-layer static anti-cheat classification (depth-6 directory signatures, embedded MIT AreWeAntiCheatYet projection, TTL-cached Steam <c>appdetails</c> categories, and conservative offline fallback);</description></item>
/// <item><description>Controlled <c>VerifiedOfflineBypass</c> evaluation for single-player-capable <c>Tier 2</c> hybrid titles without altering static <c>anti_cheat_tier</c>;</description></item>
/// <item><description>Unified trainer allowance decision combining guard <c>IdleSafe</c>, static tier, live zero-AC hard ticket, and <c>vgk.sys</c> policy;</description></item>
/// <item><description>Zero-idle-cost 2-second active-session emergency stop watchdog using read-only <c>0x1000</c> process enumeration and kernel driver enumeration.</description></item>
/// </list>
/// </summary>
public sealed class TrainerSafetyGate : ITrainerSafetyGate, IDisposable
{
    public static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LibraryCacheRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly SpawnDatabase? _database;
    private readonly TimeProvider _timeProvider;
    private readonly IAntiCheatSystemProbe _systemProbe;
    private readonly AreWeAntiCheatYetIndex _awacyIndex;
    private readonly SteamAppCategoryClassifier _steamCategoryClassifier;
    private readonly bool _ownsSteamClassifier;
    private readonly Func<IReadOnlyList<GameRecord>>? _libraryGamesProvider;
    private readonly TimeSpan _watchdogInterval;
    private readonly ILogger<TrainerSafetyGate> _logger;

    private readonly Lock _sessionLock = new();
    private readonly Dictionary<string, string> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _watchdogTimer;
    private int _tickInProgress;
    private int _disposed;

    private volatile IReadOnlyList<GameRecord> _cachedLibraryGames = [];
    private DateTimeOffset _lastLibraryCacheRefreshUtc = DateTimeOffset.MinValue;
    private int _libraryRefreshInProgress;

    public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered;

    public bool IsWatchdogActive
    {
        get
        {
            lock (_sessionLock)
            {
                return _activeSessions.Count > 0 && _watchdogTimer is not null && Volatile.Read(ref _disposed) == 0;
            }
        }
    }

    public TrainerSafetyGate(
        IAntiCheatGuardService antiCheatGuard,
        SpawnDatabase? database = null,
        TimeProvider? timeProvider = null,
        IAntiCheatSystemProbe? systemProbe = null,
        SteamAppCategoryClassifier? steamCategoryClassifier = null,
        AreWeAntiCheatYetIndex? awacyIndex = null,
        Func<IReadOnlyList<GameRecord>>? libraryGamesProvider = null,
        TimeSpan? watchdogInterval = null,
        HttpMessageHandler? steamHttpMessageHandler = null,
        ILogger<TrainerSafetyGate>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(antiCheatGuard);

        _antiCheatGuard = antiCheatGuard;
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _systemProbe = systemProbe ?? DefaultAntiCheatSystemProbe.Instance;
        _awacyIndex = awacyIndex ?? AreWeAntiCheatYetIndex.Default;

        if (steamCategoryClassifier is not null)
        {
            _steamCategoryClassifier = steamCategoryClassifier;
            _ownsSteamClassifier = false;
        }
        else
        {
            _steamCategoryClassifier = new SteamAppCategoryClassifier(
                database: database,
                httpMessageHandler: steamHttpMessageHandler,
                timeProvider: _timeProvider);
            _ownsSteamClassifier = true;
        }

        _libraryGamesProvider = libraryGamesProvider;
        _watchdogInterval = watchdogInterval is { Ticks: > 0 } interval ? interval : DefaultWatchdogInterval;
        _logger = logger ?? NullLogger<TrainerSafetyGate>.Instance;
    }

    public async Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(
        GameRecord game,
        bool allowOnlineLookup = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var indicators = new List<string>();
        var effectiveTier = AntiCheatTier.Tier0;
        string? primarySignature = null;

        // Layer 1: Depth-6 local directory & file signature scan.
        var dirResult = _antiCheatGuard.InspectGameDirectory(game.InstallDir);
        foreach (string ind in dirResult.MatchedIndicators)
        {
            AddDistinctIgnoreCase(indicators, ind);
        }

        if (dirResult.Tier > effectiveTier)
        {
            effectiveTier = dirResult.Tier;
            primarySignature = dirResult.Signature;
        }

        // Layer 2: Embedded offline AreWeAntiCheatYet projection (MIT).
        var awacyMatch = _awacyIndex.Inspect(game);
        IReadOnlyList<string> awacyAntiCheats = awacyMatch?.AntiCheats ?? [];
        if (awacyMatch is not null)
        {
            foreach (string ac in awacyMatch.AntiCheats)
            {
                AddDistinctIgnoreCase(indicators, $"AWACY:{ac}");
            }

            if (awacyMatch.Tier > effectiveTier)
            {
                effectiveTier = awacyMatch.Tier;
                primarySignature = awacyMatch.PrimarySignature;
            }
            else if (primarySignature is null && awacyMatch.Tier > AntiCheatTier.Tier0)
            {
                primarySignature = awacyMatch.PrimarySignature;
            }
        }

        // Layer 3: Steam appdetails categories (TTL-cached in SpawnDatabase; VAC=8, MMO=20, no Single-player=2 >= Tier 2).
        SteamCategoryClassificationResult steamResult = SteamCategoryClassificationResult.NotApplicable;
        if (game.SteamAppId is > 0)
        {
            steamResult = await _steamCategoryClassifier.ClassifyAsync(
                game.SteamAppId.Value,
                allowOnlineLookup,
                ct).ConfigureAwait(false);

            foreach (string ind in steamResult.Indicators)
            {
                AddDistinctIgnoreCase(indicators, ind);
            }

            if (steamResult.FloorTier > effectiveTier)
            {
                effectiveTier = steamResult.FloorTier;
                primarySignature = steamResult.Indicators.Count > 0
                    ? steamResult.Indicators[0]
                    : "SteamCategory:Multiplayer";
            }
        }

        // Layer 4: Conservative offline / error fallback.
        bool offlineFallbackApplied = game.SteamAppId is > 0 && steamResult.OfflineOrErrorFallback;
        if (offlineFallbackApplied && game.AntiCheatTier > effectiveTier)
        {
            // Never silently downgrade an existing higher static tier when Steam categories could not be verified.
            effectiveTier = game.AntiCheatTier;
            primarySignature ??= game.AntiCheatSignature;
        }

        bool? hasSinglePlayer = steamResult.HasCategories
            ? steamResult.HasSinglePlayer
            : awacyMatch?.HasSinglePlayerHint;

        bool isMultiplayerOnly = steamResult.HasCategories
            ? steamResult.IsMultiplayerOnly
            : (awacyMatch?.HasSinglePlayerHint == false);

        return new FourLayerAntiCheatReport
        {
            EffectiveTier = effectiveTier,
            PrimarySignature = primarySignature,
            MatchedIndicators = indicators,
            AreWeAntiCheatYetMatches = awacyAntiCheats,
            HasSinglePlayerCategory = hasSinglePlayer,
            HasVacCategory = steamResult.HasVac,
            HasMmoCategory = steamResult.HasMmo,
            IsMultiplayerOnly = isMultiplayerOnly,
            UsedOnlineSteamCategories = steamResult.UsedFreshOrValidCache,
            OfflineConservativeFallbackApplied = offlineFallbackApplied,
        };
    }

    public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var report = AntiCheatLiveEvaluator.Evaluate(
            _systemProbe,
            _timeProvider.GetUtcNow(),
            resolveFullPaths: false,
            out _);

        return Task.FromResult(report);
    }

    public async Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(
        GameRecord game,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var fourLayer = await InspectGameFourLayerAsync(game, allowOnlineLookup: true, ct).ConfigureAwait(false);
        var live = await ProbeLiveAntiCheatAsync(ct).ConfigureAwait(false);
        return await EvaluateOfflineBypassCoreAsync(game, fourLayer, live, ct).ConfigureAwait(false);
    }

    public async Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(
        GameRecord game,
        bool strictVanguardMode = false,
        CancellationToken ct = default)
    {
        TrainerGameSafetySnapshot snapshot = await EvaluateGameSafetySnapshotAsync(game, strictVanguardMode, ct)
            .ConfigureAwait(false);
        return snapshot.AllowanceDecision;
    }

    /// <summary>
    /// Runs static four-layer inspection, a single live read-only anti-cheat probe, controlled offline bypass
    /// evaluation, and final allowance judgment in one consolidated pass without duplicate live OS probes.
    /// </summary>
    public async Task<TrainerGameSafetySnapshot> EvaluateGameSafetySnapshotAsync(
        GameRecord game,
        bool strictVanguardMode = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var fourLayer = await InspectGameFourLayerAsync(game, allowOnlineLookup: true, ct).ConfigureAwait(false);
        var staticTier = MaxTier(fourLayer.EffectiveTier, game.AntiCheatTier);
        var live = await ProbeLiveAntiCheatAsync(ct).ConfigureAwait(false);
        var bypass = await EvaluateOfflineBypassCoreAsync(game, fourLayer, live, ct).ConfigureAwait(false);
        var guardSnapshot = _antiCheatGuard.CurrentSnapshot;

        TrainerAllowanceDecision decision = BuildAllowanceDecision(
            game,
            fourLayer,
            staticTier,
            live,
            bypass,
            guardSnapshot,
            strictVanguardMode);

        return new TrainerGameSafetySnapshot
        {
            FourLayerReport = fourLayer,
            LiveProbe = live,
            OfflineBypass = bypass,
            AllowanceDecision = decision,
        };
    }

    private static TrainerAllowanceDecision BuildAllowanceDecision(
        GameRecord game,
        FourLayerAntiCheatReport fourLayer,
        AntiCheatTier staticTier,
        LiveAntiCheatProbeReport live,
        VerifiedOfflineBypass bypass,
        AntiCheatGuardSnapshot guardSnapshot,
        bool strictVanguardMode)
    {
        // 1. Static Tier 1 & Tier 3 are permanently locked out of trainers (D-27).
        if (staticTier is AntiCheatTier.Tier1 or AntiCheatTier.Tier3)
        {
            return new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.BlockedByTier,
                IsAllowed = false,
                Message = $"目标游戏静态反作弊等级为 {staticTier}（{fourLayer.PrimarySignature ?? game.AntiCheatSignature ?? "受保护游戏"}），永久禁止启用修改器。",
                StaticGameTier = staticTier,
                GuardPhase = guardSnapshot.Phase,
                RemainingCooldownSeconds = guardSnapshot.RemainingCooldownSeconds,
                CanConfirmOfflineBypass = false,
                IsVanguardIdleWarning = live.IsVanguardKernelIdleLoaded,
                UsedVerifiedOfflineBypass = false,
                DetectedAntiCheatProcesses = live.ActiveAntiCheatProcesses,
                DetectedAntiCheatDrivers = live.ActiveSessionAntiCheatDrivers,
            };
        }

        // 2. Tier 2 without confirmed single-player eligibility: fail closed, while honestly distinguishing
        //    confirmed multiplayer-only titles (permanent ban) from offline/unverifiable checks (retry when online).
        if (staticTier == AntiCheatTier.Tier2 && !bypass.IsEligible)
        {
            bool isConfirmedMultiplayerOnly = fourLayer.IsMultiplayerOnly || fourLayer.HasSinglePlayerCategory == false;
            bool isOfflineVerificationPending = !isConfirmedMultiplayerOnly
                && fourLayer.HasSinglePlayerCategory is null
                && (fourLayer.OfflineConservativeFallbackApplied || game.SteamAppId is > 0);

            string tier2BlockMessage;
            if (bypass.WasAutoInvalidated && !string.IsNullOrWhiteSpace(bypass.InvalidationReason))
            {
                tier2BlockMessage = bypass.InvalidationReason!;
            }
            else if (isConfirmedMultiplayerOnly)
            {
                tier2BlockMessage = "目标游戏确认为无单人模式的纯多人 Tier 2 受保护游戏，永久禁止启用修改器。";
            }
            else if (isOfflineVerificationPending)
            {
                tier2BlockMessage = "当前离线无法联网核验该 Tier 2 游戏是否具备单人模式资格（离线保守拦截，请联网后点击重试）。";
            }
            else
            {
                tier2BlockMessage = "未能确认该 Tier 2 游戏是否具备单人模式资格（离线保守拦截，请联网核验或补充 Steam AppID 后重试）。";
            }

            return new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.BlockedByTier,
                IsAllowed = false,
                Message = tier2BlockMessage,
                StaticGameTier = staticTier,
                GuardPhase = guardSnapshot.Phase,
                RemainingCooldownSeconds = guardSnapshot.RemainingCooldownSeconds,
                CanConfirmOfflineBypass = false,
                IsVanguardIdleWarning = live.IsVanguardKernelIdleLoaded,
                UsedVerifiedOfflineBypass = false,
                IsOfflineSinglePlayerVerificationPending = !isConfirmedMultiplayerOnly && !bypass.WasAutoInvalidated,
                DetectedAntiCheatProcesses = live.ActiveAntiCheatProcesses,
                DetectedAntiCheatDrivers = live.ActiveSessionAntiCheatDrivers,
            };
        }

        bool canConfirmOfflineBypass = staticTier == AntiCheatTier.Tier2 && bypass.IsEligible && !bypass.IsConfirmed;
        bool usingOfflineBypass = staticTier == AntiCheatTier.Tier2 && bypass.IsEligible && bypass.IsConfirmed;

        // 3. Anti-Cheat Guard global phase check: must be IdleSafe.
        if (guardSnapshot.Phase != AntiCheatGuardPhase.IdleSafe)
        {
            string guardReason = guardSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked
                ? $"反作弊安全守卫处于 15 秒余温冷却锁定态（剩余 {guardSnapshot.RemainingCooldownSeconds:0.0} 秒），禁止启用修改器。"
                : $"反作弊安全守卫处于全局锁定态（{FormatGuardPhaseLabel(guardSnapshot.Phase)}），禁止启用修改器。";

            return new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.BlockedByGuardPhase,
                IsAllowed = false,
                Message = guardReason,
                StaticGameTier = staticTier,
                GuardPhase = guardSnapshot.Phase,
                RemainingCooldownSeconds = guardSnapshot.RemainingCooldownSeconds,
                CanConfirmOfflineBypass = canConfirmOfflineBypass,
                IsVanguardIdleWarning = live.IsVanguardKernelIdleLoaded,
                UsedVerifiedOfflineBypass = usingOfflineBypass,
                DetectedAntiCheatProcesses = live.ActiveAntiCheatProcesses,
                DetectedAntiCheatDrivers = live.ActiveSessionAntiCheatDrivers,
            };
        }

        // 4. Eligible Tier 2 hybrid single-player game that has not yet been explicitly confirmed by the player.
        if (canConfirmOfflineBypass)
        {
            return new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.RequiresOfflineConfirmation,
                IsAllowed = false,
                Message = "该游戏为具备单人模式的 Tier 2 混合游戏，请先确认已关闭反作弊并启用受控离线放行（VerifiedOfflineBypass）。",
                StaticGameTier = staticTier,
                GuardPhase = guardSnapshot.Phase,
                RemainingCooldownSeconds = 0,
                CanConfirmOfflineBypass = true,
                IsVanguardIdleWarning = live.IsVanguardKernelIdleLoaded,
                UsedVerifiedOfflineBypass = false,
                DetectedAntiCheatProcesses = live.ActiveAntiCheatProcesses,
                DetectedAntiCheatDrivers = live.ActiveSessionAntiCheatDrivers,
            };
        }

        // 5. Real-time zero-AC hard ticket (active AC daemons, active session kernel AC drivers, or active vgc.exe).
        if (!live.IsClearForTrainer)
        {
            var detectedParts = new List<string>();
            if (live.ActiveAntiCheatProcesses.Count > 0)
            {
                detectedParts.Add($"进程：{string.Join("、", live.ActiveAntiCheatProcesses)}");
            }

            if (live.ActiveSessionAntiCheatDrivers.Count > 0)
            {
                detectedParts.Add($"内核驱动：{string.Join("、", live.ActiveSessionAntiCheatDrivers)}");
            }

            string prefix = live.IsVanguardClientActive
                ? "检测到 Riot Vanguard 客户端（vgc.exe）处于活跃运行态，已硬拦截修改器操作。"
                : "检测到实时运行的反作弊进程或会话内核驱动，已硬拦截修改器操作。";

            return new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.BlockedByLiveAntiCheat,
                IsAllowed = false,
                Message = detectedParts.Count > 0 ? $"{prefix}（{string.Join("；", detectedParts)}）" : prefix,
                StaticGameTier = staticTier,
                GuardPhase = guardSnapshot.Phase,
                RemainingCooldownSeconds = 0,
                CanConfirmOfflineBypass = false,
                IsVanguardIdleWarning = false,
                UsedVerifiedOfflineBypass = usingOfflineBypass,
                DetectedAntiCheatProcesses = live.ActiveAntiCheatProcesses,
                DetectedAntiCheatDrivers = live.ActiveSessionAntiCheatDrivers,
            };
        }

        // 6. Vanguard vgk.sys boot-time idle resident policy (vgc.exe is NOT running).
        if (live.IsVanguardKernelIdleLoaded)
        {
            if (strictVanguardMode)
            {
                return new TrainerAllowanceDecision
                {
                    Outcome = TrainerAllowanceOutcome.BlockedByVanguardStrictMode,
                    IsAllowed = false,
                    Message = "检测到 Riot Vanguard 内核驱动（vgk.sys）空闲驻留，且已开启 Vanguard 严格模式，已禁止启动修改器。",
                    StaticGameTier = staticTier,
                    GuardPhase = guardSnapshot.Phase,
                    RemainingCooldownSeconds = 0,
                    CanConfirmOfflineBypass = false,
                    IsVanguardIdleWarning = true,
                    UsedVerifiedOfflineBypass = usingOfflineBypass,
                    DetectedAntiCheatProcesses = [],
                    DetectedAntiCheatDrivers = [AntiCheatFeatureCatalog.VanguardKernelDriverFileName],
                };
            }

            return new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.VanguardIdleWarning,
                IsAllowed = true,
                Message = "检测到 Riot Vanguard 内核驱动（vgk.sys）处于空闲驻留状态（vgc.exe 未运行）。可在单机模式下继续，但建议游玩 Vanguard 网游前重启系统。",
                StaticGameTier = staticTier,
                GuardPhase = guardSnapshot.Phase,
                RemainingCooldownSeconds = 0,
                CanConfirmOfflineBypass = false,
                IsVanguardIdleWarning = true,
                UsedVerifiedOfflineBypass = usingOfflineBypass,
                DetectedAntiCheatProcesses = [],
                DetectedAntiCheatDrivers = [AntiCheatFeatureCatalog.VanguardKernelDriverFileName],
            };
        }

        // 7. All four layers & real-time hard ticket cleared.
        return new TrainerAllowanceDecision
        {
            Outcome = TrainerAllowanceOutcome.Allowed,
            IsAllowed = true,
            Message = usingOfflineBypass
                ? "已通过混合单机受控离线放行（VerifiedOfflineBypass）与实时零反作弊硬验票。"
                : "已通过四层 Tier 0 安全核验与实时零反作弊硬验票。",
            StaticGameTier = staticTier,
            GuardPhase = guardSnapshot.Phase,
            RemainingCooldownSeconds = 0,
            CanConfirmOfflineBypass = false,
            IsVanguardIdleWarning = false,
            UsedVerifiedOfflineBypass = usingOfflineBypass,
            DetectedAntiCheatProcesses = [],
            DetectedAntiCheatDrivers = [],
        };
    }

    private static string FormatGuardPhaseLabel(AntiCheatGuardPhase phase) => phase switch
    {
        AntiCheatGuardPhase.IdleSafe => "安全空闲",
        AntiCheatGuardPhase.ProtectedRunning => "受保护游戏运行中",
        AntiCheatGuardPhase.ResidualGuardWatch => "等待反作弊残影退出",
        AntiCheatGuardPhase.CooldownLocked => "15 秒余温冷却锁定",
        _ => "安全锁定",
    };

    public void NotifyTrainerSessionStarted(string sessionId, string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        bool shouldStartTimer = false;
        lock (_sessionLock)
        {
            _activeSessions[sessionId.Trim()] = gameId?.Trim() ?? string.Empty;
            if (_watchdogTimer is null)
            {
                _watchdogTimer = _timeProvider.CreateTimer(
                    static state => ((TrainerSafetyGate)state!).ExecuteWatchdogTick(),
                    this,
                    _watchdogInterval,
                    _watchdogInterval);
                shouldStartTimer = true;
            }
        }

        if (shouldStartTimer && _libraryGamesProvider is null && _database is not null)
        {
            TriggerLibraryGamesRefreshIfStale();
        }
    }

    public void NotifyTrainerSessionEnded(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        ITimer? timerToDispose = null;
        lock (_sessionLock)
        {
            _activeSessions.Remove(sessionId.Trim());
            if (_activeSessions.Count == 0 && _watchdogTimer is not null)
            {
                timerToDispose = _watchdogTimer;
                _watchdogTimer = null;
            }
        }

        timerToDispose?.Dispose();
    }

    private async Task<VerifiedOfflineBypass> EvaluateOfflineBypassCoreAsync(
        GameRecord game,
        FourLayerAntiCheatReport fourLayer,
        LiveAntiCheatProbeReport live,
        CancellationToken ct)
    {
        var staticTier = MaxTier(fourLayer.EffectiveTier, game.AntiCheatTier);
        bool hasSinglePlayer = fourLayer.HasSinglePlayerCategory == true;
        bool isEligible = staticTier == AntiCheatTier.Tier2 && hasSinglePlayer;

        bool isConfirmed = game.VerifiedOfflineBypass;
        DateTimeOffset? confirmedAtUtc = game.VerifiedOfflineBypassAtUtc;
        bool wasAutoInvalidated = false;
        string? invalidationReason = null;

        if (isConfirmed && !isEligible)
        {
            bool definitivelyInvalidTier = staticTier != AntiCheatTier.Tier2;
            bool definitivelyLostSinglePlayer = fourLayer.HasSinglePlayerCategory == false || fourLayer.IsMultiplayerOnly;

            if (definitivelyInvalidTier || definitivelyLostSinglePlayer)
            {
                wasAutoInvalidated = true;
                isConfirmed = false;
                confirmedAtUtc = null;
                invalidationReason = definitivelyInvalidTier
                    ? $"游戏静态反作弊等级已变更为 {staticTier}，受控离线放行（VerifiedOfflineBypass）已自动失效。"
                    : "检测到该游戏已不再具备单人模式分类，受控离线放行（VerifiedOfflineBypass）已自动失效。";

                if (_database is not null)
                {
                    try
                    {
                        await _database.SetGameVerifiedOfflineBypassAsync(
                            game.GameId,
                            enabled: false,
                            confirmedAtUtc: null,
                            cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(ex, "Could not persist VerifiedOfflineBypass auto-invalidation for game {GameId}.", game.GameId);
                        }
                    }
                }
            }
        }

        var activeIndicators = new List<string>(live.ActiveAntiCheatProcesses.Count + live.ActiveSessionAntiCheatDrivers.Count);
        foreach (string p in live.ActiveAntiCheatProcesses)
        {
            AddDistinctIgnoreCase(activeIndicators, p);
        }

        foreach (string d in live.ActiveSessionAntiCheatDrivers)
        {
            AddDistinctIgnoreCase(activeIndicators, d);
        }

        return new VerifiedOfflineBypass
        {
            GameId = game.GameId,
            IsConfirmed = isConfirmed,
            ConfirmedAtUtc = confirmedAtUtc,
            IsEligible = isEligible,
            WasAutoInvalidated = wasAutoInvalidated,
            InvalidationReason = invalidationReason,
            StaticAntiCheatTier = staticTier,
            HasSinglePlayerMode = hasSinglePlayer,
            IsLiveAntiCheatClear = live.IsClearForTrainer,
            ActiveAntiCheatIndicators = activeIndicators,
        };
    }

    private void ExecuteWatchdogTick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        HashSet<string> activeGameIds;
        lock (_sessionLock)
        {
            if (_activeSessions.Count == 0)
            {
                return;
            }

            activeGameIds = new HashSet<string>(_activeSessions.Values, StringComparer.OrdinalIgnoreCase);
        }

        if (Interlocked.CompareExchange(ref _tickInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (_libraryGamesProvider is null && _database is not null)
            {
                TriggerLibraryGamesRefreshIfStale();
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            var live = AntiCheatLiveEvaluator.Evaluate(
                _systemProbe,
                now,
                resolveFullPaths: true,
                out var runningProcesses);

            var detectedProcesses = new List<string>(live.ActiveAntiCheatProcesses);
            var detectedDrivers = new List<string>(live.ActiveSessionAntiCheatDrivers);
            string? triggeringGameId = null;
            var reasons = new List<string>();

            if (!live.IsClearForTrainer)
            {
                reasons.Add(live.IsVanguardClientActive
                    ? "检测到 Riot Vanguard 客户端（vgc.exe）启动"
                    : "检测到反作弊进程或会话内核驱动启动");
            }

            var guardSnap = _antiCheatGuard.CurrentSnapshot;
            if (guardSnap.Phase != AntiCheatGuardPhase.IdleSafe)
            {
                reasons.Add($"反作弊守卫进入锁定态（{FormatGuardPhaseLabel(guardSnap.Phase)}）");
                triggeringGameId ??= guardSnap.ActiveProtectedGameId;
            }

            // Detect running Tier 2/3 protected games (both from local library and well-known external executables).
            var libraryGames = GetKnownLibraryGamesSnapshot();
            DetectRunningProtectedGames(
                runningProcesses,
                libraryGames,
                activeGameIds,
                detectedProcesses,
                reasons,
                ref triggeringGameId);

            if (reasons.Count == 0)
            {
                return;
            }

            var lockdownIndicators = new List<string>(detectedProcesses.Count + detectedDrivers.Count);
            lockdownIndicators.AddRange(detectedProcesses);
            lockdownIndicators.AddRange(detectedDrivers);

            if (_antiCheatGuard is AntiCheatGuardService concreteGuard)
            {
                concreteGuard.NotifyExternalAntiCheatDetected(lockdownIndicators, triggeringGameId);
            }

            var evt = new TrainerEmergencyStopEvent
            {
                Reason = string.Join("；", reasons),
                DetectedProcesses = detectedProcesses.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                DetectedDrivers = detectedDrivers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                TriggeringGameId = triggeringGameId,
                OccurredUtc = now,
            };

            try
            {
                EmergencyStopTriggered?.Invoke(this, evt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EmergencyStopTriggered subscriber threw an exception.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Anti-cheat watchdog tick encountered an unexpected error.");
        }
        finally
        {
            Volatile.Write(ref _tickInProgress, 0);
        }
    }

    private static void DetectRunningProtectedGames(
        IReadOnlyList<SafeProcessEntry> runningProcesses,
        IReadOnlyList<GameRecord> libraryGames,
        HashSet<string> activeTrainerGameIds,
        List<string> detectedProcesses,
        List<string> reasons,
        ref string? triggeringGameId)
    {
        int selfPid = Environment.ProcessId;

        foreach (var proc in runningProcesses)
        {
            if (proc.ProcessId <= 4 || proc.ProcessId == selfPid)
            {
                continue;
            }

            // 1. Check against SPAWN's known library games (by install directory prefix).
            bool matchedLibraryGame = false;
            if (!string.IsNullOrWhiteSpace(proc.FullExecutablePath) && libraryGames.Count > 0)
            {
                foreach (var game in libraryGames)
                {
                    if (game.AntiCheatTier < AntiCheatTier.Tier2 || string.IsNullOrWhiteSpace(game.InstallDir))
                    {
                        continue;
                    }

                    string prefix = NormalizeDirectoryPrefix(game.InstallDir);
                    if (!proc.FullExecutablePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matchedLibraryGame = true;

                    // Allow the active trainer session's own verified-offline-bypassed Tier 2 game ONLY when
                    // the running process is NOT an anti-cheat launcher (e.g., eldenring.exe vs start_protected_game.exe).
                    bool isExemptOfflineBypassSession = game.AntiCheatTier == AntiCheatTier.Tier2
                        && game.VerifiedOfflineBypass
                        && activeTrainerGameIds.Contains(game.GameId)
                        && !AntiCheatFeatureCatalog.IsAntiCheatLauncherExecutable(proc.ExeFileName);

                    if (!isExemptOfflineBypassSession)
                    {
                        AddDistinctIgnoreCase(detectedProcesses, proc.ExeFileName);
                        AddDistinctIgnoreCase(reasons, $"检测到 {game.AntiCheatTier} 受保护游戏进程运行：{game.Title} ({proc.ExeFileName})");
                        triggeringGameId ??= game.GameId;
                    }

                    break;
                }
            }

            if (matchedLibraryGame)
            {
                continue;
            }

            // 2. Check against well-known external competitive/protected game executables (not necessarily in SPAWN library).
            string exeName = Path.GetFileName(proc.ExeFileName);
            if (AntiCheatFeatureCatalog.KnownProtectedGameExecutables.TryGetValue(exeName, out var known))
            {
                AddDistinctIgnoreCase(detectedProcesses, exeName);
                AddDistinctIgnoreCase(reasons, $"检测到外部 {known.Tier} 受保护游戏进程运行：{known.DisplayName} ({exeName})");
            }
        }
    }

    private IReadOnlyList<GameRecord> GetKnownLibraryGamesSnapshot()
    {
        if (_libraryGamesProvider is not null)
        {
            try
            {
                return _libraryGamesProvider();
            }
            catch
            {
                return [];
            }
        }

        return _cachedLibraryGames;
    }

    private void TriggerLibraryGamesRefreshIfStale()
    {
        if (_database is null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (now - _lastLibraryCacheRefreshUtc < LibraryCacheRefreshInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _libraryRefreshInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var games = await _database.GetAllGamesAsync(CancellationToken.None).ConfigureAwait(false);
                _cachedLibraryGames = games;
                _lastLibraryCacheRefreshUtc = _timeProvider.GetUtcNow();
            }
            catch
            {
                // Keep existing cached snapshot on transient DB error.
            }
            finally
            {
                Volatile.Write(ref _libraryRefreshInProgress, 0);
            }
        });
    }

    private static string NormalizeDirectoryPrefix(string dir)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Trim()));
        return normalized + Path.DirectorySeparatorChar;
    }

    private static AntiCheatTier MaxTier(AntiCheatTier left, AntiCheatTier right) =>
        left >= right ? left : right;

    private static void AddDistinctIgnoreCase(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ITimer? timerToDispose;
        lock (_sessionLock)
        {
            _activeSessions.Clear();
            timerToDispose = _watchdogTimer;
            _watchdogTimer = null;
        }

        timerToDispose?.Dispose();

        if (_ownsSteamClassifier)
        {
            _steamCategoryClassifier.Dispose();
        }
    }
}
