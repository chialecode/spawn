using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Full production coordinator for <c>Spawn.Modules.Achievement</c> (<see cref="IAchievementService"/>):
/// - Orchestrates all <see cref="IAchievementSourceProvider"/> plugins (16+ Steam emulators, GOG Comet 9977 / Galaxy DB / Nemirtingas, DRM-Free native save rules, Steam Binary VDF)
/// - Drives the Chinese Schema + Hidden Spoiler + SkiaSharp Rec.709 Grayscale waterfall
/// - Runs the 4-layer zero-miss real-time monitor (Silent Initial Seed + 64KB narrow FSW + 120ms per-path Channel debounce + 500ms metadata micro-poller)
/// - Dispatches real-time unlock &amp; 100% Platinum celebration toasts over <see cref="IOverlayIpcClient"/>.
/// </summary>
public sealed class AchievementService : IAchievementService, IAsyncDisposable, IDisposable
{
    public const int BulkUnlockSummaryThreshold = 8;
    public const string BulkSummaryToastApiName = "__SPAWN_BATCH_SUMMARY__";

    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _database;
    private readonly ISpawnEventBus _eventBus;
    private readonly IOverlayIpcClient _overlayIpcClient;
    private readonly IReadOnlyList<IAchievementSourceProvider> _providers;
    private readonly NativeSaveAchievementProvider _nativeSaveProvider;
    private readonly GogCometLocalDaemon _cometDaemon;
    private readonly AchievementSchemaService _schemaService;
    private readonly GbeForkActivatorService _gbeActivator;
    private readonly AchievementModuleOptions _options;
    private readonly ILogger<AchievementService> _logger;

    private readonly List<IDisposable> _eventSubscriptions = [];
    private readonly ConcurrentDictionary<string, GameWatchRegistration> _watchedGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveGameSession> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gameScanLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _monitorLifecycleLock = new(1, 1);

    private Channel<(string GameId, string? TriggerPath)>? _debounceChannel;
    private CancellationTokenSource? _monitorCts;
    private Task? _debounceWorkerTask;
    private Task? _microPollWorkerTask;
    private volatile bool _isMonitoring;
    private int _disposed;

    public event EventHandler<AchievementUnlockedNotification>? AchievementUnlocked;

    public bool IsMonitoring => _isMonitoring;

    /// <summary>
    /// Diagnostic message produced by the most recent manual achievement mark/reset call.
    /// </summary>
    public string? LastManualToggleMessage { get; private set; }

    public AchievementService(
        SpawnPaths paths,
        SpawnDatabase database,
        ISpawnEventBus eventBus,
        IOverlayIpcClient overlayIpcClient,
        IEnumerable<IAchievementSourceProvider> providers,
        NativeSaveAchievementProvider nativeSaveProvider,
        GogCometLocalDaemon cometDaemon,
        AchievementSchemaService schemaService,
        GbeForkActivatorService gbeActivator,
        AchievementModuleOptions? options = null,
        ILogger<AchievementService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(overlayIpcClient);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(nativeSaveProvider);
        ArgumentNullException.ThrowIfNull(cometDaemon);
        ArgumentNullException.ThrowIfNull(schemaService);
        ArgumentNullException.ThrowIfNull(gbeActivator);

        _paths = paths;
        _database = database;
        _eventBus = eventBus;
        _overlayIpcClient = overlayIpcClient;
        _providers = [.. providers.OrderBy(p => p.Priority)];
        _nativeSaveProvider = nativeSaveProvider;
        _cometDaemon = cometDaemon;
        _schemaService = schemaService;
        _gbeActivator = gbeActivator;
        _options = options ?? new AchievementModuleOptions();
        _logger = logger ?? NullLogger<AchievementService>.Instance;

        _cometDaemon.AchievementCaptured += OnCometAchievementCapturedAsync;
        _eventSubscriptions.Add(_eventBus.Subscribe<GameImportedOrRescannedEvent>(OnGameImportedOrRescannedAsync));
        _eventSubscriptions.Add(_eventBus.Subscribe<ActiveGameSessionChangedEvent>(OnActiveGameSessionChangedAsync));
        _eventSubscriptions.Add(_eventBus.Subscribe<GameRemovedEvent>(OnGameRemovedAsync));
    }

    public Task<GameAchievementSummaryDto> GetGameAchievementsAsync(
        string gameId,
        CancellationToken cancellationToken = default) =>
        _database.GetGameAchievementSummaryAsync(gameId, cancellationToken);

    public Task<IReadOnlyList<AchievementUnlockDto>> GetRecentUnlocksAsync(
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        _database.GetRecentUnlocksAsync(limit, cancellationToken);

    public async Task<bool> GetRevealHiddenSpoilersAsync(CancellationToken cancellationToken = default)
    {
        string? raw = await _database.GetSettingAsync(AppSettingKeys.AchievementRevealSpoilers, cancellationToken).ConfigureAwait(false);
        return bool.TryParse(raw, out bool val) && val;
    }

    public Task SetRevealHiddenSpoilersAsync(bool revealSpoilers, CancellationToken cancellationToken = default) =>
        _database.SetSettingAsync(AppSettingKeys.AchievementRevealSpoilers, revealSpoilers ? "true" : "false", cancellationToken);

    public async Task<GameAchievementSummaryDto> SyncGameSchemaAsync(
        string gameId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return new GameAchievementSummaryDto { GameId = gameId };
        }

        var existingDefs = await _database.GetAchievementDefinitionsAsync(gameId, cancellationToken).ConfigureAwait(false);
        string preferredLang = await _database.GetSettingAsync(AppSettingKeys.AchievementLanguage, cancellationToken).ConfigureAwait(false)
                               ?? "schinese";

        if (forceRefresh || existingDefs.Count == 0)
        {
            var resolved = await _schemaService.FetchAndBuildSchemaAsync(game, preferredLang, forceRefresh, cancellationToken).ConfigureAwait(false);

            // Persist resolved SteamAppId / GogGameId and detected capture channel onto GameRecord
            var updatedGame = await RefreshGameCaptureChannelRecordAsync(
                game,
                resolved.ResolvedSteamAppId,
                resolved.ResolvedGogGameId,
                resolved.Definitions,
                cancellationToken).ConfigureAwait(false);

            await _database.UpsertAchievementSchemaAsync(
                gameId: updatedGame.GameId,
                schemaKey: resolved.SchemaKey,
                source: resolved.Source,
                language: preferredLang,
                definitions: resolved.Definitions,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (_isMonitoring)
            {
                await AttachOrRefreshGameWatchersAsync(updatedGame, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await RefreshGameCaptureChannelRecordAsync(game, game.SteamAppId, game.GogGameId, existingDefs, cancellationToken).ConfigureAwait(false);
        }

        var summary = await _database.GetGameAchievementSummaryAsync(gameId, cancellationToken).ConfigureAwait(false);
        await PublishSchemaUpdatedEventAsync(summary, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    public async Task<int> ScanLocalUnlocksAsync(
        string gameId,
        bool silentSeed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var gate = _gameScanLocks.GetOrAdd(gameId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (newlyUnlockedCount, _) = await ScanLocalUnlocksInternalAsync(
                gameId,
                silentSeed,
                isRealtimeMonitor: false,
                cancellationToken).ConfigureAwait(false);
            return newlyUnlockedCount;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> SetAchievementUnlockedAsync(
        string gameId,
        string apiName,
        bool unlocked,
        CancellationToken cancellationToken = default)
    {
        ManualAchievementToggleResult result = await SetAchievementUnlockedDetailedAsync(
            gameId,
            apiName,
            unlocked,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>
    /// Manually marks or resets an achievement and honestly reports whether a local Goldberg/GSE JSON save file
    /// was also updated versus only updating SPAWN's display record.
    /// </summary>
    public async Task<ManualAchievementToggleResult> SetAchievementUnlockedDetailedAsync(
        string gameId,
        string apiName,
        bool unlocked,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            const string missingMsg = "未找到指定游戏记录，未能更新成就状态。";
            LastManualToggleMessage = missingMsg;
            return new ManualAchievementToggleResult(false, false, missingMsg);
        }

        var unlockDto = new AchievementUnlockDto
        {
            GameId = gameId,
            ApiName = apiName.Trim(),
            IsUnlocked = unlocked,
            UnlockedUtc = unlocked ? DateTimeOffset.UtcNow : null,
            SourceProvider = "Manual",
            NotifiedPopup = true,
        };

        await _database.UpsertAchievementUnlocksBatchAsync(gameId, [unlockDto], cancellationToken).ConfigureAwait(false);

        // Best-effort sync to local Goldberg / gbe_fork JSON if applicable
        bool syncedToLocalGoldbergFile = false;
        var context = await BuildSourceContextAsync(game, cancellationToken).ConfigureAwait(false);
        var targets = DiscoverAllTargets(context);
        foreach (var t in targets.Where(t => t.Flavor is EmulatorFlavor.GbeFork or EmulatorFlavor.Goldberg && !string.IsNullOrWhiteSpace(t.FilePath)))
        {
            if (File.Exists(t.FilePath!))
            {
                bool synced = await SteamEmulatorAchievementProvider.TrySyncLocalGoldbergSaveAsync(
                    t.FilePath!, apiName, unlocked, cancellationToken).ConfigureAwait(false);
                if (synced)
                {
                    syncedToLocalGoldbergFile = true;
                }
            }
        }

        var summary = await _database.GetGameAchievementSummaryAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (unlocked)
        {
            var def = summary.Items.FirstOrDefault(i => string.Equals(i.Definition.ApiName, apiName, StringComparison.OrdinalIgnoreCase))?.Definition
                      ?? new AchievementDefinitionDto { ApiName = apiName, DisplayName = apiName };

            var notification = new AchievementUnlockedNotification
            {
                GameId = gameId,
                GameTitle = game.Title,
                Definition = def,
                Unlock = unlockDto,
                UnlockedCount = summary.UnlockedCount,
                TotalCount = summary.TotalCount,
                IsAllAchievementsCompleted = summary.TotalCount > 0 && summary.UnlockedCount >= summary.TotalCount,
                SuppressOverlayToast = true,
            };

            AchievementUnlocked?.Invoke(this, notification);
            await _eventBus.PublishAsync(notification, cancellationToken).ConfigureAwait(false);
        }

        await PublishSchemaUpdatedEventAsync(summary, cancellationToken).ConfigureAwait(false);

        string actionText = unlocked ? "已标记为已解锁" : "已重置为未解锁";
        string message = syncedToLocalGoldbergFile
            ? $"{actionText}，并已同步写入本地 Goldberg/GSE 成就文件。"
            : $"{actionText}（非 Goldberg/GSE 渠道，仅更新 SPAWN 展示记录，未改动游戏本体或模拟器文件）。";

        LastManualToggleMessage = message;
        return new ManualAchievementToggleResult(true, syncedToLocalGoldbergFile, message);
    }

    public async Task<GbeForkActivationResult> ActivateGbeForkAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GbeForkActivationResult.Fail("未找到指定游戏记录。");
        }

        var defs = await _database.GetAchievementDefinitionsAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (defs.Count == 0 && game.AntiCheatTier == AntiCheatTier.Tier0 && !GbeForkActivatorService.IsOfficialSteamAppsPath(game.InstallDir))
        {
            var synced = await SyncGameSchemaAsync(gameId, forceRefresh: false, cancellationToken).ConfigureAwait(false);
            defs = [.. synced.Items.Select(i => i.Definition)];
            game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false) ?? game;
        }

        var result = await _gbeActivator.ActivateAsync(game, game.SteamAppId, defs, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            bool hasParallelSaveRule = _nativeSaveProvider.FindMatchingRules(game, game.SteamAppId, game.GogGameId).Count > 0;
            var updatedGame = game with
            {
                AchievementCaptureMode = AchievementCaptureMode.GbeForkActivated,
                AchievementChannelLabel = hasParallelSaveRule
                    ? "Goldberg GSE + 存档双通道 · 就绪"
                    : "Goldberg GSE (已无损激活) · 就绪",
            };
            await _database.UpsertGameAsync(updatedGame, cancellationToken).ConfigureAwait(false);

            if (_isMonitoring)
            {
                await AttachOrRefreshGameWatchersAsync(updatedGame, cancellationToken).ConfigureAwait(false);
            }

            var summary = await _database.GetGameAchievementSummaryAsync(gameId, cancellationToken).ConfigureAwait(false);
            await PublishSchemaUpdatedEventAsync(summary, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<GbeForkActivationResult> RestoreOriginalDllAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GbeForkActivationResult.Fail("未找到指定游戏记录。");
        }

        var result = await _gbeActivator.RestoreOriginalDllAsync(game, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            var defs = await _database.GetAchievementDefinitionsAsync(gameId, cancellationToken).ConfigureAwait(false);
            var refreshed = await RefreshGameCaptureChannelRecordAsync(
                game with { AchievementCaptureMode = AchievementCaptureMode.None, AchievementChannelLabel = null },
                game.SteamAppId,
                game.GogGameId,
                defs,
                cancellationToken).ConfigureAwait(false);

            if (_isMonitoring)
            {
                await AttachOrRefreshGameWatchersAsync(refreshed, cancellationToken).ConfigureAwait(false);
            }

            var summary = await _database.GetGameAchievementSummaryAsync(gameId, cancellationToken).ConfigureAwait(false);
            await PublishSchemaUpdatedEventAsync(summary, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task SendTestToastAsync(
        string? gameId = null,
        OverlayThemeKind? themeOverride = null,
        CancellationToken cancellationToken = default)
    {
        OverlayToastDispatchResult result = await TrySendTestToastAsync(gameId, themeOverride, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    /// <summary>
    /// Dispatches a live preview toast with immediate preview replacement and honestly reports whether
    /// <c>Spawn.OverlayHost</c> acknowledged the IPC request.
    /// </summary>
    public async Task<OverlayToastDispatchResult> TrySendTestToastAsync(
        string? gameId = null,
        OverlayThemeKind? themeOverride = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _database.GetOverlaySettingsAsync(cancellationToken).ConfigureAwait(false);
        string gameTitle = "SPAWN 成就中心";
        string apiName = "SPAWN_TEST_ACHIEVEMENT";
        string displayName = "全渠道实时跳杯已就绪";
        string description = "144Hz 零注入透明覆盖层与多流派离线成就监听运行正常。";
        string? iconPath = null;
        double? globalPercent = 4.2;

        if (!string.IsNullOrWhiteSpace(gameId))
        {
            var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
            if (game is not null)
            {
                gameTitle = game.Title;
                var defs = await _database.GetAchievementDefinitionsAsync(gameId, cancellationToken).ConfigureAwait(false);
                var sample = defs.FirstOrDefault(d => !d.IsHidden) ?? (defs.Count > 0 ? defs[0] : null);
                if (sample is not null)
                {
                    apiName = sample.ApiName;
                    displayName = sample.DisplayName;
                    description = !string.IsNullOrWhiteSpace(sample.Description)
                        ? sample.Description
                        : (sample.HiddenSpoilerDescription ?? description);
                    iconPath = sample.IconPath;
                    globalPercent = sample.GlobalPercent ?? 8.5;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            string demoIconDir = _paths.GetAchievementIconDirectory("spawn_demo");
            string demoIcon = Path.Combine(demoIconDir, "demo_badge.png");
            if (!File.Exists(demoIcon))
            {
                await File.WriteAllBytesAsync(
                    demoIcon,
                    AchievementIconProcessor.GenerateProceduralFallbackBadgePng(grayscale: false, isRare: true),
                    cancellationToken).ConfigureAwait(false);
            }

            iconPath = demoIcon;
        }

        var toast = new OverlayToastRequest
        {
            GameId = gameId,
            GameTitle = gameTitle,
            AchievementApiName = apiName,
            DisplayName = displayName,
            Description = description,
            IconPath = iconPath,
            IsRare = globalPercent is < 10.0,
            GlobalPercent = globalPercent,
            IsPlatinumCompletion = (themeOverride ?? settings.Theme) == OverlayThemeKind.Ps5Trophy && string.IsNullOrWhiteSpace(gameId),
            Theme = themeOverride ?? settings.Theme,
            Position = settings.Position,
            SoundPreset = settings.SoundPreset,
            CustomSoundPath = settings.CustomSoundPath,
            AudioOutputDeviceId = settings.AudioOutputDeviceId,
            SoundVolume = settings.SoundVolume,
            MuteSound = settings.MuteSound,
            DurationMs = 4500,
            ReplaceActivePreview = true,
            EnhanceRareAndPlatinumSound = settings.EnhanceRareAndPlatinumSound,
        };

        bool delivered = await _overlayIpcClient.SendToastAsync(toast, cancellationToken).ConfigureAwait(false);
        if (delivered)
        {
            return new OverlayToastDispatchResult(true, "已推送测试跳杯弹窗（零焦点抢占）。");
        }

        string? launchDiag = (_overlayIpcClient as IOverlayIpcDiagnostics)?.LastLaunchError;
        string errorMessage = !string.IsNullOrWhiteSpace(launchDiag)
            ? $"未能连接 OverlayHost 弹窗服务：{launchDiag}"
            : "未能连接 OverlayHost 弹窗服务（进程未启动或命名管道无响应）。";

        _logger.LogWarning("Overlay test toast dispatch failed: {Reason}", errorMessage);
        return new OverlayToastDispatchResult(false, errorMessage);
    }

    public async Task ResetGameAchievementStateAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var gate = _gameScanLocks.GetOrAdd(gameId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.ResetGameAchievementsOnlyAsync(gameId, cancellationToken).ConfigureAwait(false);

            var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
            if (game is not null)
            {
                // Clear local Comet state cache if present so GOG Comet unlocks can re-trigger cleanly
                var (gid, cid) = GogAchievementProvider.TryParseGogGameInfo(game.InstallDir);
                foreach (string? key in new[] { game.GogGameId, gid, game.GogClientId, cid })
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        string cometState = _cometDaemon.GetPersistedStateFilePath(key);
                        if (File.Exists(cometState))
                        {
                            try { File.Delete(cometState); } catch { }
                        }
                    }
                }

                if (_isMonitoring)
                {
                    await AttachOrRefreshGameWatchersAsync(game, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        var summary = await _database.GetGameAchievementSummaryAsync(gameId, cancellationToken).ConfigureAwait(false);
        await PublishSchemaUpdatedEventAsync(summary, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
    {
        await _monitorLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isMonitoring)
            {
                return;
            }

            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);

            _debounceChannel = Channel.CreateUnbounded<(string GameId, string? TriggerPath)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            _monitorCts = new CancellationTokenSource();
            _isMonitoring = true;

            var games = await _database.GetAllGamesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var game in games)
            {
                // Perform incremental silent seed across all parallel channels (including native/cloud save JSON rules)
                // so achievements unlocked offline prior to SPAWN launch/update immediately appear in the library!
                await ScanLocalUnlocksAsync(game.GameId, silentSeed: true, cancellationToken).ConfigureAwait(false);
                await AttachOrRefreshGameWatchersAsync(game, cancellationToken).ConfigureAwait(false);
            }

            _debounceWorkerTask = Task.Run(() => RunDebounceConsumerLoopAsync(_debounceChannel.Reader, _monitorCts.Token), CancellationToken.None);
            _microPollWorkerTask = Task.Run(() => RunMetadataMicroPollerLoopAsync(_monitorCts.Token), CancellationToken.None);
        }
        finally
        {
            _monitorLifecycleLock.Release();
        }
    }

    public async Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        Task? debounceTask = null;
        Task? pollTask = null;

        await _monitorLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isMonitoring)
            {
                return;
            }

            _isMonitoring = false;
            _debounceChannel?.Writer.TryComplete();
            if (_monitorCts is not null)
            {
                await _monitorCts.CancelAsync().ConfigureAwait(false);
            }

            foreach (var (_, reg) in _watchedGames.ToArray())
            {
                reg.Dispose();
            }
            _watchedGames.Clear();

            debounceTask = _debounceWorkerTask;
            pollTask = _microPollWorkerTask;
            _debounceWorkerTask = null;
            _microPollWorkerTask = null;
        }
        finally
        {
            _monitorLifecycleLock.Release();
        }

        if (debounceTask is not null)
        {
            try { await debounceTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); } catch { }
        }

        if (pollTask is not null)
        {
            try { await pollTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); } catch { }
        }

        await _cometDaemon.StopAsync(cancellationToken).ConfigureAwait(false);

        _monitorCts?.Dispose();
        _monitorCts = null;
        _debounceChannel = null;
    }

    // -------------------------------------------------------------------------
    // Internal Event Handlers & Monitoring Engine
    // -------------------------------------------------------------------------

    private async Task OnGameImportedOrRescannedAsync(GameImportedOrRescannedEvent evt, CancellationToken ct)
    {
        try
        {
            await SyncGameSchemaAsync(evt.Game.GameId, forceRefresh: false, ct).ConfigureAwait(false);
            await ScanLocalUnlocksAsync(evt.Game.GameId, silentSeed: true, ct).ConfigureAwait(false);

            if (_isMonitoring)
            {
                var latest = await _database.GetGameByIdAsync(evt.Game.GameId, ct).ConfigureAwait(false) ?? evt.Game;
                await AttachOrRefreshGameWatchersAsync(latest, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-sync schema/unlocks on import for GameId {GameId}.", evt.Game.GameId);
        }
    }

    private async Task OnActiveGameSessionChangedAsync(ActiveGameSessionChangedEvent evt, CancellationToken ct)
    {
        try
        {
            if (evt.CurrentState == PlaySessionActivityState.Stopped || evt.Session is null)
            {
                _activeSessions.TryRemove(evt.GameId, out _);

                // Final post-exit sweep so unlocks written during shutdown are never missed
                await ScanLocalUnlocksRealtimeAsync(evt.GameId, ct).ConfigureAwait(false);

                if (_activeSessions.IsEmpty && _cometDaemon.IsListening)
                {
                    await _cometDaemon.StopAsync(ct).ConfigureAwait(false);
                }

                return;
            }

            bool isFirstTransition = !_activeSessions.ContainsKey(evt.GameId);
            _activeSessions[evt.GameId] = evt.Session;

            if (isFirstTransition)
            {
                var game = await _database.GetGameByIdAsync(evt.GameId, ct).ConfigureAwait(false);
                if (game is not null)
                {
                    if (_options.EnableGogCometAutoStart && GogAchievementProvider.HasGalaxySdkDll(game.InstallDir))
                    {
                        var (gid, cid) = GogAchievementProvider.TryParseGogGameInfo(game.InstallDir);
                        _cometDaemon.RegisterGameSession(game.GogGameId ?? gid, game.GogClientId ?? cid);
                        await _cometDaemon.StartAsync(_options.GogCometPort, ct).ConfigureAwait(false);
                    }

                    if (_isMonitoring)
                    {
                        await AttachOrRefreshGameWatchersAsync(game, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling ActiveGameSessionChangedEvent for GameId {GameId}.", evt.GameId);
        }
    }

    private Task OnGameRemovedAsync(GameRemovedEvent evt, CancellationToken ct)
    {
        _activeSessions.TryRemove(evt.GameId, out _);
        if (_watchedGames.TryRemove(evt.GameId, out var reg))
        {
            reg.Dispose();
        }

        return Task.CompletedTask;
    }

    private async Task OnCometAchievementCapturedAsync(GogCometAchievementCapturedEventArgs args, CancellationToken ct)
    {
        var allGames = await _database.GetAllGamesAsync(ct).ConfigureAwait(false);
        var matchedGame = allGames.FirstOrDefault(g =>
            (!string.IsNullOrWhiteSpace(args.GameId) && string.Equals(g.GogGameId, args.GameId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(args.ClientId) && string.Equals(g.GogClientId, args.ClientId, StringComparison.OrdinalIgnoreCase)));

        matchedGame ??= _activeSessions.Keys
            .Select(id => allGames.FirstOrDefault(g => g.GameId == id))
            .FirstOrDefault(g => g is not null);

        if (matchedGame is null)
        {
            return;
        }

        var unlockDto = new AchievementUnlockDto
        {
            GameId = matchedGame.GameId,
            ApiName = args.Entry.ApiName,
            IsUnlocked = args.Entry.IsUnlocked,
            UnlockedUtc = args.Entry.UnlockedUtc ?? DateTimeOffset.UtcNow,
            SourceProvider = nameof(EmulatorFlavor.GogCometLocal),
            RawSourcePath = args.PersistedStateFilePath,
            NotifiedPopup = false,
        };

        var newly = await _database.UpsertAchievementUnlocksBatchAsync(matchedGame.GameId, [unlockDto], ct).ConfigureAwait(false);
        if (newly.Count > 0)
        {
            await DispatchNewlyUnlockedNotificationsAsync(matchedGame, newly, silentSeed: false, ct).ConfigureAwait(false);
        }
    }

    private async Task AttachOrRefreshGameWatchersAsync(GameRecord game, CancellationToken ct)
    {
        var context = await BuildSourceContextAsync(game, ct).ConfigureAwait(false);
        var targets = DiscoverAllTargets(context);

        if (_watchedGames.TryRemove(game.GameId, out var oldReg))
        {
            oldReg.Dispose();
        }

        var registration = new GameWatchRegistration(game.GameId, targets);
        var attachedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.FilePath))
            {
                continue;
            }

            registration.FileStamps[target.FilePath] = ResilientFileReader.GetMetadataStamp(target.FilePath);
            if (target.Flavor == EmulatorFlavor.GogGalaxyDb)
            {
                string walPath = target.FilePath + "-wal";
                registration.FileStamps[walPath] = ResilientFileReader.GetMetadataStamp(walPath);
            }

            var (watchDir, includeSubdirs) = ResolveSafeWatchDirectory(target.FilePath, game.InstallDir);
            if (watchDir is null || !attachedDirs.Add($"{watchDir}|{includeSubdirs}"))
            {
                continue;
            }

            try
            {
                var fsw = new FileSystemWatcher(watchDir)
                {
                    IncludeSubdirectories = includeSubdirs,
                    InternalBufferSize = Math.Clamp(_options.FileSystemWatcherBufferBytes, 8192, 65536),
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size
                                   | NotifyFilters.CreationTime,
                };

                string capturedGameId = game.GameId;
                void HandleFsEvent(string? changedFullPath)
                {
                    if (!_isMonitoring || string.IsNullOrWhiteSpace(changedFullPath))
                    {
                        return;
                    }

                    if (MatchesAnyWatchedTarget(changedFullPath, registration.Targets))
                    {
                        _debounceChannel?.Writer.TryWrite((capturedGameId, changedFullPath));
                    }
                }

                fsw.Created += (_, e) => HandleFsEvent(e.FullPath);
                fsw.Changed += (_, e) => HandleFsEvent(e.FullPath);
                fsw.Renamed += (_, e) =>
                {
                    HandleFsEvent(e.FullPath);
                    HandleFsEvent(e.OldFullPath);
                };
                fsw.Deleted += (_, _) => registration.MarkNeedsRebind();
                fsw.Error += (_, _) => registration.MarkNeedsRebind();

                fsw.EnableRaisingEvents = true;
                registration.Watchers.Add(fsw);
                registration.WatchedDirectories.Add((watchDir, includeSubdirs));
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Could not attach FileSystemWatcher to {Dir}; micro-poller will cover changes.", watchDir);
                }
            }
        }

        _watchedGames[game.GameId] = registration;
    }

    private async Task RunDebounceConsumerLoopAsync(
        ChannelReader<(string GameId, string? TriggerPath)> reader,
        CancellationToken ct)
    {
        var pendingDueUtc = new Dictionary<string, (DateTimeOffset DueUtc, string? LastTriggerPath)>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (pendingDueUtc.Count == 0)
                {
                    var item = await reader.ReadAsync(ct).ConfigureAwait(false);
                    pendingDueUtc[item.GameId] = (DateTimeOffset.UtcNow + _options.DebounceWindow, item.TriggerPath);
                }

                while (reader.TryRead(out var extra))
                {
                    pendingDueUtc[extra.GameId] = (DateTimeOffset.UtcNow + _options.DebounceWindow, extra.TriggerPath);
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                var nextDue = pendingDueUtc.Values.Min(v => v.DueUtc);
                if (nextDue > now)
                {
                    TimeSpan wait = nextDue - now;
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var delayTask = Task.Delay(wait, delayCts.Token);
                    var readWaitTask = reader.WaitToReadAsync(ct).AsTask();
                    await Task.WhenAny(delayTask, readWaitTask).ConfigureAwait(false);
                    await delayCts.CancelAsync().ConfigureAwait(false);
                    continue;
                }

                var readyGames = pendingDueUtc
                    .Where(kv => kv.Value.DueUtc <= DateTimeOffset.UtcNow)
                    .ToList();

                foreach (var (gameId, (_, triggerPath)) in readyGames)
                {
                    pendingDueUtc.Remove(gameId);
                    await ProcessDebouncedScanWithSelfHealAsync(gameId, triggerPath, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error in achievement debounce consumer loop.");
            }
        }
    }

    private async Task ProcessDebouncedScanWithSelfHealAsync(string gameId, string? triggerPath, CancellationToken ct)
    {
        var gate = _gameScanLocks.GetOrAdd(gameId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        bool retryForHalfWrite = false;

        try
        {
            var (_, totalParsedEntries) = await ScanLocalUnlocksInternalAsync(
                gameId,
                silentSeed: false,
                isRealtimeMonitor: true,
                ct).ConfigureAwait(false);

            // Half-write self-healing: if the triggering file exists with bytes > 0 yet parsed 0 entries (mid-flush torn JSON/INI),
            // schedule one follow-up read after 160ms.
            if (totalParsedEntries == 0 && !string.IsNullOrWhiteSpace(triggerPath))
            {
                var stamp = ResilientFileReader.GetMetadataStamp(triggerPath);
                if (stamp.Exists && stamp.LengthBytes > 0)
                {
                    retryForHalfWrite = true;
                }
            }
        }
        finally
        {
            gate.Release();
        }

        if (retryForHalfWrite && !ct.IsCancellationRequested)
        {
            await Task.Delay(160, ct).ConfigureAwait(false);
            await ScanLocalUnlocksRealtimeAsync(gameId, ct).ConfigureAwait(false);
        }
    }

    private async Task ScanLocalUnlocksRealtimeAsync(string gameId, CancellationToken ct)
    {
        var gate = _gameScanLocks.GetOrAdd(gameId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ = await ScanLocalUnlocksInternalAsync(gameId, silentSeed: false, isRealtimeMonitor: true, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunMetadataMicroPollerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.ActiveGamePollInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            foreach (var (gameId, reg) in _watchedGames.ToArray())
            {
                // Self-heal FileSystemWatchers when a watched leaf folder was deleted (e.g. GameDataManagementService cleanup)
                // or when a previously missing target folder has now been created on disk.
                bool watchedDirDeleted = reg.NeedsRebind || reg.WatchedDirectories.Any(d => !Directory.Exists(d.DirectoryPath));
                bool missingLeafNowCreated = !watchedDirDeleted && reg.WatchedDirectories.Any(d => d.IsAncestorFallback)
                                             && reg.Targets.Any(t => !string.IsNullOrWhiteSpace(t.FilePath)
                                                                     && Directory.Exists(Path.GetDirectoryName(t.FilePath)));

                if (watchedDirDeleted || missingLeafNowCreated)
                {
                    var latestGame = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
                    if (latestGame is not null)
                    {
                        var prevStamps = reg.FileStamps.ToArray();
                        await AttachOrRefreshGameWatchersAsync(latestGame, ct).ConfigureAwait(false);
                        if (_watchedGames.TryGetValue(gameId, out var refreshedReg))
                        {
                            // Preserve prior stamps across watcher rebind so a newly created file immediately diffs as Exists: false -> true!
                            foreach (var (p, oldStamp) in prevStamps)
                            {
                                refreshedReg.FileStamps[p] = oldStamp;
                            }
                        }
                    }
                }

                if (!_watchedGames.TryGetValue(gameId, out var activeReg))
                {
                    continue;
                }

                // Poll active games every 500ms, plus lightweight stamp check on watched target files
                bool changed = false;
                string? changedPath = null;

                foreach (var (path, prevStamp) in activeReg.FileStamps.ToArray())
                {
                    var currentStamp = ResilientFileReader.GetMetadataStamp(path);
                    if (currentStamp != prevStamp)
                    {
                        activeReg.FileStamps[path] = currentStamp;
                        if (currentStamp.Exists)
                        {
                            changed = true;
                            changedPath = path;
                        }
                    }
                }

                // For active sessions using GreenLuma registry, trigger periodic registry check
                if (!changed && _activeSessions.ContainsKey(gameId) && activeReg.Targets.Any(t => t.Flavor == EmulatorFlavor.GreenLuma))
                {
                    changed = true;
                }

                if (changed)
                {
                    _debounceChannel?.Writer.TryWrite((gameId, changedPath));
                }
            }
        }
    }

    private async Task<(int NewlyUnlockedCount, int TotalParsedEntries)> ScanLocalUnlocksInternalAsync(
        string gameId,
        bool silentSeed,
        bool isRealtimeMonitor,
        CancellationToken ct)
    {
        var game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return (0, 0);
        }

        var context = await BuildSourceContextAsync(game, ct).ConfigureAwait(false);
        var targets = DiscoverAllTargets(context);
        if (targets.Count == 0)
        {
            return (0, 0);
        }

        // Prevent toast bombardment when a manual rescan runs outside active real-time monitoring.
        bool suppressOverlayPopups = silentSeed || (!isRealtimeMonitor && !_isMonitoring);

        var providerMap = _providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        var batchToUpsert = new Dictionary<string, AchievementUnlockDto>(StringComparer.OrdinalIgnoreCase);
        int totalParsed = 0;

        foreach (var target in targets)
        {
            if (!providerMap.TryGetValue(target.ProviderId, out var provider))
            {
                continue;
            }

            if (_watchedGames.TryGetValue(gameId, out var reg) && !string.IsNullOrWhiteSpace(target.FilePath))
            {
                reg.FileStamps[target.FilePath] = ResilientFileReader.GetMetadataStamp(target.FilePath);
            }

            var parsed = await provider.ReadUnlocksAsync(target, context, ct).ConfigureAwait(false);
            totalParsed += parsed.Count;

            foreach (var entry in parsed)
            {
                if (string.IsNullOrWhiteSpace(entry.ApiName))
                {
                    continue;
                }

                // Never fabricate "today" (UtcNow) timestamps during silent initial seeding when the source format
                // does not record per-achievement unlock times (e.g., LIMBO bitmask or flat INI/JSON arrays).
                DateTimeOffset? resolvedUtc = entry.IsUnlocked
                    ? (entry.UnlockedUtc ?? (silentSeed ? null : DateTimeOffset.UtcNow))
                    : null;

                var candidate = new AchievementUnlockDto
                {
                    GameId = gameId,
                    ApiName = entry.ApiName,
                    IsUnlocked = entry.IsUnlocked,
                    UnlockedUtc = resolvedUtc,
                    CurrentProgress = entry.CurrentProgress,
                    MaxProgress = entry.MaxProgress,
                    SourceProvider = target.Flavor.ToString(),
                    RawSourcePath = target.FilePath ?? target.RegistryKeyPath,
                    NotifiedPopup = suppressOverlayPopups,
                };

                // If multiple targets exist for the same game, prefer an unlocked state over locked
                if (!batchToUpsert.TryGetValue(entry.ApiName, out var existing)
                    || (!existing.IsUnlocked && candidate.IsUnlocked))
                {
                    batchToUpsert[entry.ApiName] = candidate;
                }
            }
        }

        if (batchToUpsert.Count == 0)
        {
            return (0, totalParsed);
        }

        var newlyUnlocked = await _database.UpsertAchievementUnlocksBatchAsync(
            gameId, [.. batchToUpsert.Values], ct).ConfigureAwait(false);

        if (newlyUnlocked.Count > 0)
        {
            await DispatchNewlyUnlockedNotificationsAsync(game, newlyUnlocked, suppressOverlayPopups, ct).ConfigureAwait(false);
        }
        else if (suppressOverlayPopups)
        {
            var summary = await _database.GetGameAchievementSummaryAsync(gameId, ct).ConfigureAwait(false);
            await PublishSchemaUpdatedEventAsync(summary, ct).ConfigureAwait(false);
        }

        return (newlyUnlocked.Count, totalParsed);
    }

    private async Task DispatchNewlyUnlockedNotificationsAsync(
        GameRecord game,
        IReadOnlyList<AchievementUnlockDto> newlyUnlocked,
        bool silentSeed,
        CancellationToken ct)
    {
        var summary = await _database.GetGameAchievementSummaryAsync(game.GameId, ct).ConfigureAwait(false);
        var defByApi = summary.Items.ToDictionary(i => i.Definition.ApiName, i => i.Definition, StringComparer.OrdinalIgnoreCase);

        bool completedAll = summary.TotalCount > 0 && summary.UnlockedCount >= summary.TotalCount;
        var overlaySettings = silentSeed
            ? null
            : await _database.GetOverlaySettingsAsync(ct).ConfigureAwait(false);

        var toastBatch = new List<OverlayToastRequest>(newlyUnlocked.Count);

        for (int i = 0; i < newlyUnlocked.Count; i++)
        {
            var unlock = newlyUnlocked[i];
            if (!defByApi.TryGetValue(unlock.ApiName, out var def))
            {
                def = new AchievementDefinitionDto
                {
                    ApiName = unlock.ApiName,
                    DisplayName = unlock.ApiName,
                    Description = string.Empty,
                };
            }

            bool isFinalCompletingItem = completedAll && i == newlyUnlocked.Count - 1;
            string effectiveDesc = !string.IsNullOrWhiteSpace(def.Description)
                ? def.Description
                : (def.HiddenSpoilerDescription ?? string.Empty);

            var notification = new AchievementUnlockedNotification
            {
                GameId = game.GameId,
                GameTitle = game.Title,
                Definition = def,
                Unlock = unlock,
                UnlockedCount = summary.UnlockedCount,
                TotalCount = summary.TotalCount,
                IsAllAchievementsCompleted = isFinalCompletingItem,
                SuppressOverlayToast = silentSeed,
            };

            AchievementUnlocked?.Invoke(this, notification);
            await _eventBus.PublishAsync(notification, ct).ConfigureAwait(false);

            if (!silentSeed && overlaySettings is not null)
            {
                toastBatch.Add(new OverlayToastRequest
                {
                    GameId = game.GameId,
                    GameTitle = game.Title,
                    AchievementApiName = def.ApiName,
                    DisplayName = def.DisplayName,
                    Description = effectiveDesc,
                    IconPath = def.IconPath,
                    IsRare = def.IsRare,
                    GlobalPercent = def.GlobalPercent,
                    IsPlatinumCompletion = isFinalCompletingItem,
                    Theme = overlaySettings.Theme,
                    Position = overlaySettings.Position,
                    SoundPreset = overlaySettings.SoundPreset,
                    CustomSoundPath = overlaySettings.CustomSoundPath,
                    AudioOutputDeviceId = overlaySettings.AudioOutputDeviceId,
                    SoundVolume = overlaySettings.SoundVolume,
                    MuteSound = overlaySettings.MuteSound,
                    EnhanceRareAndPlatinumSound = overlaySettings.EnhanceRareAndPlatinumSound,
                });
            }
        }

        if (!silentSeed && toastBatch.Count > 0)
        {
            if (toastBatch.Count >= BulkUnlockSummaryThreshold)
            {
                // Collapse large bulk unlock bursts (>= 8) into a single summary toast so rescans never spam popups.
                OverlayToastRequest representative = toastBatch[^1];
                var sampleNames = toastBatch
                    .Select(static t => t.DisplayName)
                    .Where(static n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToList();
                string summaryDesc = sampleNames.Count switch
                {
                    0 => $"《{game.Title}》新增 {toastBatch.Count} 项成就，可在成就中心查看详情",
                    1 => $"含「{sampleNames[0]}」等，可在成就中心查看全部详情",
                    _ => $"含「{sampleNames[0]}」「{sampleNames[1]}」等，可在成就中心查看全部详情",
                };
                bool anyRare = toastBatch.Any(static t => t.IsRare || t.GlobalPercent is < 10.0);
                double? rarestPercent = toastBatch.Where(static t => t.GlobalPercent.HasValue)
                    .Select(static t => t.GlobalPercent!.Value)
                    .DefaultIfEmpty()
                    .Min();
                if (!toastBatch.Any(static t => t.GlobalPercent.HasValue))
                {
                    rarestPercent = null;
                }

                var summaryToast = representative with
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    AchievementApiName = BulkSummaryToastApiName,
                    DisplayName = $"已连续解锁 {toastBatch.Count} 项成就",
                    Description = summaryDesc,
                    IsRare = anyRare,
                    GlobalPercent = rarestPercent,
                    IsPlatinumCompletion = completedAll,
                    DurationMs = 3800,
                    DisplayDurationMs = 3800,
                };

                bool delivered = await _overlayIpcClient.SendToastAsync(summaryToast, ct).ConfigureAwait(false);
                if (delivered)
                {
                    foreach (var u in newlyUnlocked)
                    {
                        await _database.MarkAchievementPopupNotifiedAsync(game.GameId, u.ApiName, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to deliver bulk achievement summary toast ({Count} items) for GameId {GameId}; keeping NotifiedPopup=0.", toastBatch.Count, game.GameId);
                }
            }
            else if (toastBatch.Count == 1)
            {
                bool delivered = await _overlayIpcClient.SendToastAsync(toastBatch[0], ct).ConfigureAwait(false);
                if (delivered)
                {
                    await _database.MarkAchievementPopupNotifiedAsync(game.GameId, newlyUnlocked[0].ApiName, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning("Failed to deliver achievement toast {ApiName} for GameId {GameId}; keeping NotifiedPopup=0.", newlyUnlocked[0].ApiName, game.GameId);
                }
            }
            else
            {
                int deliveredCount = await _overlayIpcClient.SendToastBatchAsync(toastBatch, ct).ConfigureAwait(false);
                if (deliveredCount > 0)
                {
                    int toMark = Math.Min(deliveredCount, newlyUnlocked.Count);
                    for (int idx = 0; idx < toMark; idx++)
                    {
                        await _database.MarkAchievementPopupNotifiedAsync(game.GameId, newlyUnlocked[idx].ApiName, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to deliver achievement toast batch ({Count} items) for GameId {GameId}; keeping NotifiedPopup=0.", toastBatch.Count, game.GameId);
                }
            }
        }

        await PublishSchemaUpdatedEventAsync(summary, ct).ConfigureAwait(false);
    }

    private async Task<GameRecord> RefreshGameCaptureChannelRecordAsync(
        GameRecord game,
        int? resolvedSteamAppId,
        string? resolvedGogGameId,
        IReadOnlyList<AchievementDefinitionDto> definitions,
        CancellationToken ct)
    {
        var (detectedGogId, detectedGogClient) = GogAchievementProvider.TryParseGogGameInfo(game.InstallDir);
        int? steamAppId = game.SteamAppId ?? resolvedSteamAppId;
        string? gogGameId = game.GogGameId ?? resolvedGogGameId ?? detectedGogId;
        string? gogClientId = game.GogClientId ?? detectedGogClient;

        var probeGame = game with
        {
            SteamAppId = steamAppId,
            GogGameId = gogGameId,
            GogClientId = gogClientId,
        };

        var ctx = new AchievementSourceContext
        {
            Game = probeGame,
            EffectiveSteamAppId = steamAppId,
            EffectiveGogGameId = gogGameId,
            EffectiveGogClientId = gogClientId,
            SchemaDefinitions = definitions,
            Crc32ToApiName = AchievementSourceContext.BuildCrc32Lookup(definitions.Select(d => d.ApiName)),
            EnvironmentOverrides = _options.EnvironmentOverrides,
        };

        var targets = DiscoverAllTargets(ctx);
        AchievementCaptureMode mode;
        string label;

        var primaryTarget = targets.Count > 0 ? targets[0] : null;
        bool hasSteamEmuTarget = targets.Any(t => string.Equals(t.ProviderId, SteamEmulatorAchievementProvider.Id, StringComparison.OrdinalIgnoreCase));
        bool hasParallelSaveRule = targets.Any(t => string.Equals(t.ProviderId, NativeSaveAchievementProvider.Id, StringComparison.OrdinalIgnoreCase));
        bool hasEnablerManifest = !string.IsNullOrWhiteSpace(probeGame.InstallDir)
                                  && File.Exists(Path.Combine(probeGame.InstallDir, GbeForkActivatorService.ManifestFileName));

        if (game.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated || hasEnablerManifest)
        {
            mode = AchievementCaptureMode.GbeForkActivated;
            label = hasParallelSaveRule
                ? "Goldberg GSE + 存档双通道 · 就绪"
                : "Goldberg GSE (已无损激活) · 就绪";
        }
        else if (primaryTarget is not null)
        {
            mode = primaryTarget.CaptureMode;
            label = hasSteamEmuTarget && hasParallelSaveRule
                ? primaryTarget.ChannelLabel.Replace(" · 就绪", " + 存档双通道 · 就绪", StringComparison.Ordinal)
                : primaryTarget.ChannelLabel;
        }
        else if (GbeForkActivatorService.IsCandidateForActivation(probeGame))
        {
            mode = AchievementCaptureMode.PendingGbeForkActivation;
            label = "待激活离线成就补丁";
        }
        else
        {
            mode = AchievementCaptureMode.None;
            label = "未检测到成就通道";
        }

        var updated = probeGame with
        {
            AchievementCaptureMode = mode,
            AchievementChannelLabel = label,
        };

        if (updated != game)
        {
            await _database.UpsertGameAsync(updated, ct).ConfigureAwait(false);
        }

        return updated;
    }

    private async Task<AchievementSourceContext> BuildSourceContextAsync(GameRecord game, CancellationToken ct)
    {
        var defs = await _database.GetAchievementDefinitionsAsync(game.GameId, ct).ConfigureAwait(false);
        var (detectedGogId, detectedGogClient) = GogAchievementProvider.TryParseGogGameInfo(game.InstallDir);
        string? gogGameId = game.GogGameId ?? detectedGogId;
        string? gogClientId = game.GogClientId ?? detectedGogClient;

        var rule = _nativeSaveProvider.FindMatchingRule(game, game.SteamAppId, gogGameId);
        int? steamAppId = game.SteamAppId
                          ?? rule?.MappedSteamAppId
                          ?? SteamEmulatorFormatParsers.TryDetectSteamAppIdFromDirectory(game.InstallDir);

        return new AchievementSourceContext
        {
            Game = game,
            EffectiveSteamAppId = steamAppId,
            EffectiveGogGameId = gogGameId,
            EffectiveGogClientId = gogClientId,
            SchemaDefinitions = defs,
            Crc32ToApiName = AchievementSourceContext.BuildCrc32Lookup(defs.Select(d => d.ApiName)),
            EnvironmentOverrides = _options.EnvironmentOverrides,
        };
    }

    private List<AchievementWatchTarget> DiscoverAllTargets(AchievementSourceContext context)
    {
        var all = new List<AchievementWatchTarget>();
        foreach (var provider in _providers)
        {
            var discovered = provider.DiscoverTargets(context);
            if (discovered.Count > 0)
            {
                all.AddRange(discovered);
            }
        }

        return all;
    }

    private Task PublishSchemaUpdatedEventAsync(GameAchievementSummaryDto summary, CancellationToken ct) =>
        _eventBus.PublishAsync(
            new AchievementSchemaUpdatedEvent(
                GameId: summary.GameId,
                UnlockedCount: summary.UnlockedCount,
                TotalCount: summary.TotalCount,
                CaptureMode: summary.CaptureMode,
                ChannelStatusText: summary.ChannelStatusText,
                OccurredUtc: DateTimeOffset.UtcNow),
            ct).AsTask();

    private static (string? WatchDir, bool IncludeSubdirectories) ResolveSafeWatchDirectory(string targetFilePath, string? gameInstallDir)
    {
        try
        {
            string? immediateDir = Path.GetDirectoryName(targetFilePath);
            if (string.IsNullOrWhiteSpace(immediateDir))
            {
                return (null, false);
            }

            if (Directory.Exists(immediateDir))
            {
                return (immediateDir, false);
            }

            // If immediate child directory doesn't exist yet (e.g. `<PublicDocs>/Steam/CODEX/<AppId>/achievements.ini` after data cleanup
            // or `<GameDir>/save_game/savegame.txt`), walk up up to 5 ancestor levels without ever attaching to a drive root.
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cur = Directory.GetParent(immediateDir);
            int hops = 1;
            while (cur is not null && hops <= 5)
            {
                if (cur.Exists && cur.Parent is not null)
                {
                    bool isRawUserProfile = !string.IsNullOrWhiteSpace(userProfile)
                                            && string.Equals(
                                                Path.TrimEndingDirectorySeparator(cur.FullName),
                                                Path.TrimEndingDirectorySeparator(userProfile),
                                                StringComparison.OrdinalIgnoreCase);
                    if (!isRawUserProfile)
                    {
                        return (cur.FullName, true);
                    }
                }

                cur = cur.Parent;
                hops++;
            }

            if (!string.IsNullOrWhiteSpace(gameInstallDir) && Directory.Exists(gameInstallDir))
            {
                string normInstall = Path.GetFullPath(gameInstallDir);
                if (Path.GetFullPath(targetFilePath).StartsWith(normInstall, StringComparison.OrdinalIgnoreCase))
                {
                    return (normInstall, true);
                }
            }
        }
        catch
        {
            // Ignore invalid paths.
        }

        return (null, false);
    }

    private static bool MatchesAnyWatchedTarget(string changedPath, IReadOnlyList<AchievementWatchTarget> targets)
    {
        string fullChanged;
        try
        {
            fullChanged = Path.GetFullPath(changedPath);
        }
        catch
        {
            return false;
        }

        string fileName = Path.GetFileName(fullChanged);

        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t.FilePath))
            {
                continue;
            }

            if (string.Equals(fullChanged, t.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Match newly created ancestor directories along the path to a previously missing target file
            // (e.g. when CODEX/1703340 or RUNE/1794680 is created and populated simultaneously on first unlock after cleanup!)
            string prefixWithSep = Path.TrimEndingDirectorySeparator(fullChanged) + Path.DirectorySeparatorChar;
            if (t.FilePath.StartsWith(prefixWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Match SQLite WAL/journal or atomic temp files in same directory (e.g. gameplay.db-wal, achievements.json.tmp)
            string? targetDir = Path.GetDirectoryName(t.FilePath);
            string targetName = Path.GetFileName(t.FilePath);
            if (!string.IsNullOrWhiteSpace(targetDir)
                && fullChanged.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase)
                && fileName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cometDaemon.AchievementCaptured -= OnCometAchievementCapturedAsync;
        foreach (var sub in _eventSubscriptions)
        {
            sub.Dispose();
        }
        _eventSubscriptions.Clear();

        foreach (var (_, reg) in _watchedGames.ToArray())
        {
            reg.Dispose();
        }
        _watchedGames.Clear();

        try { _monitorCts?.Cancel(); } catch { }
        _monitorCts?.Dispose();
        _cometDaemon.Dispose();
        _schemaService.Dispose();
        _gbeActivator.Dispose();
        _monitorLifecycleLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cometDaemon.AchievementCaptured -= OnCometAchievementCapturedAsync;
        foreach (var sub in _eventSubscriptions)
        {
            sub.Dispose();
        }
        _eventSubscriptions.Clear();

        await StopMonitoringAsync().ConfigureAwait(false);
        await _cometDaemon.DisposeAsync().ConfigureAwait(false);
        _schemaService.Dispose();
        _gbeActivator.Dispose();
        _monitorLifecycleLock.Dispose();
    }

    private sealed class GameWatchRegistration(string gameId, IReadOnlyList<AchievementWatchTarget> targets) : IDisposable
    {
        private volatile bool _needsRebind;

        public string GameId { get; } = gameId;
        public IReadOnlyList<AchievementWatchTarget> Targets { get; } = targets;
        public List<FileSystemWatcher> Watchers { get; } = [];
        public List<(string DirectoryPath, bool IsAncestorFallback)> WatchedDirectories { get; } = [];
        public ConcurrentDictionary<string, FileMetadataStamp> FileStamps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool NeedsRebind => _needsRebind;

        public void MarkNeedsRebind() => _needsRebind = true;

        public void Dispose()
        {
            foreach (var w in Watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch
                {
                    // Ignore watcher dispose error.
                }
            }

            Watchers.Clear();
        }
    }
}
