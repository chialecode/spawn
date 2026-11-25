using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Helpers;
using Spawn.Host.ViewModels.Saves;
using Spawn.Modules.Launcher;

namespace Spawn.Host.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _db;
    private readonly IGameLibraryService _libraryService;
    private readonly IGameLaunchService _launchService;
    private readonly IAchievementService _achievementService;
    private readonly IGameDataManagementService _gameDataService;
    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly IOverlayIpcClient _overlayIpcClient;
    private readonly ISpawnEventBus _eventBus;
    private readonly ITrainerCatalogService _trainerCatalogService;
    private readonly ITrainerRuntimeService _trainerRuntimeService;
    private readonly ITrainerSafetyGate _trainerSafetyGate;
    private readonly ISaveTimeMachineService? _saveTimeMachineService;
    private readonly ICloudSaveSyncService? _cloudSaveSyncService;
    private readonly ISaveWatcherService? _saveWatcherService;
    private readonly Action<string> _openExternalUrl;
    private readonly Dispatcher? _uiDispatcher;
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _reloadSync = new();

    private PrimaryNavSection _lastNonSettingsSection = PrimaryNavSection.Library;
    private IReadOnlyList<GameAchievementItemDto> _currentGameRawAchievements = [];
    private string? _loadedAchievementRowsGameId;
    private string? _loadedAchievementRowsGameTitle;
    private IReadOnlyList<TrainerCatalogRecord> _allTrainerCatalogRecords = [];
    private readonly List<TrainerSidebarGameItemViewModel> _allTrainerSidebarGames = [];
    private readonly Dictionary<string, (bool IsSafetyLocked, bool IsHybridSinglePlayer)> _trainerSidebarSafetyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SaveSidebarGameItemViewModel> _allSaveSidebarGames = [];
    private int _saveHubRefreshGeneration;
    private bool _isReloadLoopActive;
    private PendingLibraryReloadBatch? _pendingReloadBatch;
    private bool _isApplyingInternalSelection;
    private bool _suppressSelectionAchievementAutoLoad;
    private int _userSelectionVersion;
    private int _activeLibraryScanCount;
    private int _suppressEventAutoSelectCount;
    private int _isDisposed;
    private int _trainerHubRefreshGeneration;
    private CancellationTokenSource? _bannerAutoHideCts;

    public SettingsViewModel Settings { get; }
    public TrainerWorkbenchViewModel Trainer { get; }
    public SaveWorkbenchViewModel Saves { get; }

    public ObservableCollection<GameListItemViewModel> AllGames { get; } = [];
    public ObservableCollection<GameSourceGroupViewModel> LibraryGroups { get; } = [];
    public ObservableCollection<AchievementRowViewModel> FilteredAchievements { get; } = [];
    public ObservableCollection<RecentUnlockItemViewModel> RecentUnlocks { get; } = [];

    // Dedicated Saves sidebar & global managed save overview collections
    public ObservableCollection<SaveSidebarGameItemViewModel> SaveSidebarGames { get; } = [];
    public ObservableCollection<ManagedSaveGameOverviewItemViewModel> ManagedSaveGames { get; } = [];

    // Dedicated Trainers sidebar & global managed trainers overview collections
    public ObservableCollection<TrainerSidebarGameItemViewModel> TrainerSidebarGames { get; } = [];
    public ObservableCollection<ManagedTrainerItemViewModel> ManagedTrainers { get; } = [];

    // Per-game Game Data Management collections
    public ObservableCollection<GameDataCategoryGroupViewModel> GameDataGroups { get; } = [];
    public ObservableCollection<GameDataItemViewModel> AllGameDataItems { get; } = [];

    private readonly GameSourceGroupViewModel _allGroup = new("all", "全部游戏", initiallyExpanded: true);
    private readonly GameSourceGroupViewModel _gogGroup = new("gog", "GOG", initiallyExpanded: true);
    private readonly GameSourceGroupViewModel _steamripGroup = new("steamrip", "Steamrip", initiallyExpanded: true);
    private readonly GameSourceGroupViewModel _otherGroup = new("other", "Steam 本地 / 独立", initiallyExpanded: false);

    [ObservableProperty]
    private PrimaryNavSection _activeSection = PrimaryNavSection.Library;

    [ObservableProperty]
    private bool _isNarrowWindow;

    [ObservableProperty]
    private bool _isNarrowSidebarOpen;

    [ObservableProperty]
    private GameListItemViewModel? _selectedGame;

    [ObservableProperty]
    private GameDetailSubTab _selectedGameSubTab = GameDetailSubTab.Achievements;

    [ObservableProperty]
    private bool _isAchievementsOverviewSelected = true;

    [ObservableProperty]
    private GameListItemViewModel? _selectedAchievementsGame;

    // Dedicated Saves section navigation, search & aggregate overview state
    [ObservableProperty]
    private bool _isSaveOverviewSelected = true;

    [ObservableProperty]
    private GameListItemViewModel? _selectedSaveGame;

    [ObservableProperty]
    private string _saveSidebarSearchText = string.Empty;

    [ObservableProperty]
    private int _totalSaveSnapshotsCount;

    [ObservableProperty]
    private int _totalSaveBranchesCount;

    [ObservableProperty]
    private string _saveVaultStoredSizeText = "0 B";

    [ObservableProperty]
    private string _saveVaultDedupRatioText = string.Empty;

    [ObservableProperty]
    private bool _hasAnySaveWatcherActive;

    [ObservableProperty]
    private bool _hasAnyCloudConflict;

    [ObservableProperty]
    private bool _isSyncingAllCloudSaves;

    // Dedicated Trainers section navigation & search state
    [ObservableProperty]
    private bool _isTrainerOverviewSelected = true;

    [ObservableProperty]
    private GameListItemViewModel? _selectedTrainerGame;

    [ObservableProperty]
    private string _trainerSidebarSearchText = string.Empty;

    [ObservableProperty]
    private int _totalManagedTrainersCount;

    [ObservableProperty]
    private int _boundTrainerGamesCount;

    [ObservableProperty]
    private bool _isBusyWorking;

    [ObservableProperty]
    private string _statusBannerMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusBannerError;

    [ObservableProperty]
    private bool _hasUnimportedLocalGamesHint;

    [ObservableProperty]
    private string _unimportedLocalGamesHintText = string.Empty;

    // Selected game's achievement workbench states
    [ObservableProperty]
    private GameAchievementSummaryDto? _currentAchievementSummary;

    [ObservableProperty]
    private AchievementFilterTab _selectedAchievementTab = AchievementFilterTab.All;

    [ObservableProperty]
    private bool _revealHiddenSpoilers;

    [ObservableProperty]
    private string _achievementSearchText = string.Empty;

    // Selected game's Game Data Management states
    [ObservableProperty]
    private bool _resetSpawnStateOnClean = true;

    [ObservableProperty]
    private GameDataUndoSnapshotDto? _latestUndoSnapshot;

    [ObservableProperty]
    private bool _isInspectingGameData;

    // Global Achievements Center aggregates
    [ObservableProperty]
    private int _globalTotalUnlockedCount;

    [ObservableProperty]
    private int _globalTotalAchievementCount;

    [ObservableProperty]
    private int _globalPlatinumGamesCount;

    [ObservableProperty]
    private int _globalRareUnlockedCount;

    // Anti-Cheat Guard Rail Badge
    [ObservableProperty]
    private string _guardRailToolTip = "安全守卫：Tier 0 纯净就绪";

    [ObservableProperty]
    private bool _isGuardCooldownOrProtected;

    public bool IsSidebarVisible => !IsNarrowWindow || IsNarrowSidebarOpen;

    public bool HasAnyRunningGame => AllGames.Any(g => g.IsRunning);

    public string LibraryRailToolTip => HasAnyRunningGame
        ? "游戏库 · 有游戏正在运行"
        : "游戏库（Spawn）";

    public bool HasAnyRunningTrainer =>
        _trainerRuntimeService.ActiveSession is not null
        || _trainerRuntimeService.State == TrainerRuntimeState.Running;

    public string TrainersRailToolTip => HasAnyRunningTrainer
        ? "修改器管理 · 有修改器正在运行"
        : "修改器管理";

    public string SavesRailToolTip => HasAnyCloudConflict
        ? "存档时间机器 · 存在未处理的云端分叉冲突"
        : (HasAnySaveWatcherActive ? "存档时间机器 · 静默监听备份中" : "存档时间机器与多分支存档");

    public string SaveOverviewCountBadgeText => HasAnyCloudConflict
        ? "冲突"
        : string.Create(CultureInfo.InvariantCulture, $"{TotalSaveSnapshotsCount}");

    public string SaveVaultPathText => _paths.VaultChunksDirectory;
    public bool HasManagedSaveGames => ManagedSaveGames.Count > 0;
    public bool HasFilteredSaveSidebarGames => SaveSidebarGames.Count > 0;
    public bool CanSyncAllCloudSaves => !IsSyncingAllCloudSaves && _cloudSaveSyncService is not null && !HasAnyRunningGame;

    public string TopBarTitle => ActiveSection switch
    {
        PrimaryNavSection.Settings => Settings.CurrentCategoryTitle,
        PrimaryNavSection.Achievements => IsAchievementsOverviewSelected || SelectedAchievementsGame is null
            ? "成就中心"
            : $"成就 · {SelectedAchievementsGame.Title}",
        PrimaryNavSection.Saves => IsSaveOverviewSelected || SelectedSaveGame is null
            ? "存档时间机器"
            : $"存档 · {SelectedSaveGame.Title}",
        PrimaryNavSection.Trainers => IsTrainerOverviewSelected || SelectedTrainerGame is null
            ? "修改器管理"
            : $"修改器 · {SelectedTrainerGame.Title}",
        _ => SelectedGame?.Title ?? "游戏库",
    };

    public GameListItemViewModel? ActiveWorkbenchGame => ActiveSection switch
    {
        PrimaryNavSection.Achievements => SelectedAchievementsGame,
        PrimaryNavSection.Saves => SelectedSaveGame,
        PrimaryNavSection.Trainers => SelectedTrainerGame,
        _ => SelectedGame,
    };

    public bool ShowLibraryEmptyState =>
        ActiveSection == PrimaryNavSection.Library && SelectedGame is null;

    public bool ShowLibraryGameWorkbench =>
        ActiveSection == PrimaryNavSection.Library && SelectedGame is not null;

    public bool ShowGameAchievementsSubPanel =>
        SelectedGameSubTab == GameDetailSubTab.Achievements;

    public bool ShowGameSavesSubPanel =>
        SelectedGameSubTab == GameDetailSubTab.Saves;

    public bool ShowGameDataSubPanel =>
        SelectedGameSubTab == GameDetailSubTab.GameData;

    public bool ShowGameTrainersSubPanel =>
        SelectedGameSubTab == GameDetailSubTab.Trainers;

    public bool ShowAchievementsOverviewPanel =>
        ActiveSection == PrimaryNavSection.Achievements && (IsAchievementsOverviewSelected || SelectedAchievementsGame is null);

    public bool ShowAchievementsSingleGamePanel =>
        ActiveSection == PrimaryNavSection.Achievements && !IsAchievementsOverviewSelected && SelectedAchievementsGame is not null;

    public bool ShowSavesOverviewPanel =>
        ActiveSection == PrimaryNavSection.Saves && (IsSaveOverviewSelected || SelectedSaveGame is null);

    public bool ShowSavesSingleGamePanel =>
        ActiveSection == PrimaryNavSection.Saves && !IsSaveOverviewSelected && SelectedSaveGame is not null;

    public bool ShowTrainersOverviewPanel =>
        ActiveSection == PrimaryNavSection.Trainers && (IsTrainerOverviewSelected || SelectedTrainerGame is null);

    public bool ShowTrainersSingleGamePanel =>
        ActiveSection == PrimaryNavSection.Trainers && !IsTrainerOverviewSelected && SelectedTrainerGame is not null;

    public bool ShowSettingsPanel =>
        ActiveSection == PrimaryNavSection.Settings;

    public bool HasManagedTrainers => ManagedTrainers.Count > 0;
    public bool HasFilteredTrainerSidebarGames => TrainerSidebarGames.Count > 0;
    public bool HasEligibleTrainerGame => _allTrainerSidebarGames.Any(g => !g.IsSafetyLocked);

    public string ManagedTrainersCountBadgeText =>
        string.Create(CultureInfo.InvariantCulture, $"共 {TotalManagedTrainersCount} 项");

    public string TrainerOverviewCountBadgeText => HasAnyRunningTrainer
        ? string.Create(CultureInfo.InvariantCulture, $"运行中 · {TotalManagedTrainersCount}")
        : string.Create(CultureInfo.InvariantCulture, $"{TotalManagedTrainersCount}");

    public string TrainerOverviewRuntimeStatusText => HasAnyRunningTrainer
        ? "运行中"
        : (IsGuardCooldownOrProtected ? "守卫锁定" : "待机就绪");

    public string ActiveRunningTrainerGameTitle
    {
        get
        {
            string? gameId = _trainerRuntimeService.ActiveSession?.GameId;
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return string.Empty;
            }

            return AllGames.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase))?.Title
                   ?? "单机游戏";
        }
    }

    public string ActiveRunningTrainerDisplayName
    {
        get
        {
            string? trainerId = _trainerRuntimeService.ActiveSession?.TrainerId;
            if (string.IsNullOrWhiteSpace(trainerId))
            {
                return string.Empty;
            }

            var rec = _allTrainerCatalogRecords.FirstOrDefault(r => string.Equals(r.TrainerId, trainerId, StringComparison.OrdinalIgnoreCase));
            if (rec is not null)
            {
                return !string.IsNullOrWhiteSpace(rec.DisplayName)
                    ? rec.DisplayName!
                    : Path.GetFileName(rec.SandboxRelativePath);
            }

            return Trainer.ActiveTrainer?.DisplayName ?? "已运行修改器";
        }
    }

    public string ActiveRunningTrainerElevationText =>
        _trainerRuntimeService.ActiveSession?.ElevationLevel == TrainerElevationLevel.Level2HighIlUac
            ? "Level 2 UAC 管理员"
            : "Level 1 普通权限";

    public string PrimaryLaunchButtonText =>
        SelectedGame is { IsRunning: true } ? "运行中 · 停止" : "启动游戏";

    public bool CanActivateGbeForkOnSelected =>
        !CanRestoreDllOnSelected
        && SelectedGame is { Record.AntiCheatTier: AntiCheatTier.Tier0 } selected
        && (selected.Record.PlatformSource == PlatformSource.Steamrip
            || selected.Record.AchievementCaptureMode == AchievementCaptureMode.PendingGbeForkActivation
            || CurrentAchievementSummary is { CanActivateGbeFork: true });

    public bool CanRestoreDllOnSelected =>
        CurrentAchievementSummary is { HasGbeForkActivated: true }
        || SelectedGame?.Record.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated;

    public int CurrentGameUnlockedCount => CurrentAchievementSummary?.UnlockedCount ?? 0;
    public int CurrentGameTotalCount => CurrentAchievementSummary?.TotalCount ?? 0;
    public double CurrentGameCompletionPercent => CurrentAchievementSummary?.CompletionPercent ?? 0.0;

    public string AchievementsSubTabHeaderText =>
        string.Create(CultureInfo.InvariantCulture, $"成就 ({CurrentGameUnlockedCount}/{CurrentGameTotalCount})");

    public string CurrentGameProgressSummaryText =>
        CurrentGameTotalCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"已解锁 {CurrentGameUnlockedCount} / {CurrentGameTotalCount}（{CurrentGameCompletionPercent:0.0}%）")
            : "暂未发现该游戏的成就元数据（可在右上角 ⋯ 点击「刷新中文成就元数据」）";

    public bool HasUndoSnapshot => LatestUndoSnapshot is not null;

    public string UndoSnapshotToolTipText => LatestUndoSnapshot is null
        ? "暂无可恢复的清理记录"
        : $"备份时间：{LatestUndoSnapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 包含 {LatestUndoSnapshot.BackedUpEntryCount} 项（{UiFormatters.FormatByteSize(LatestUndoSnapshot.TotalBytes)}）";

    public int SelectedCleanableEntryCount => AllGameDataItems.Count(i => i.IsSelected && i.IsSafeToClean);

    public string CleanSelectedButtonText => SelectedCleanableEntryCount > 0
        ? string.Create(CultureInfo.InvariantCulture, $"清理选中数据 ({SelectedCleanableEntryCount})")
        : "清理选中数据";

    public MainWindowViewModel(
        SpawnPaths paths,
        SpawnDatabase db,
        IGameLibraryService libraryService,
        IGameLaunchService launchService,
        IAchievementService achievementService,
        IGameDataManagementService gameDataService,
        IAntiCheatGuardService antiCheatGuard,
        IOverlayIpcClient overlayIpcClient,
        ISpawnEventBus eventBus,
        ITrainerCatalogService trainerCatalogService,
        ITrainerRuntimeService trainerRuntimeService,
        ITrainerSafetyGate trainerSafetyGate,
        SettingsViewModel settingsViewModel,
        TrainerWorkbenchViewModel trainerWorkbench,
        Action<string>? openExternalUrl = null,
        Dispatcher? uiDispatcher = null,
        SaveWorkbenchViewModel? saveWorkbench = null,
        ISaveTimeMachineService? saveTimeMachineService = null,
        ICloudSaveSyncService? cloudSaveSyncService = null,
        ISaveWatcherService? saveWatcherService = null)
    {
        _paths = paths;
        _db = db;
        _libraryService = libraryService;
        _launchService = launchService;
        _achievementService = achievementService;
        _gameDataService = gameDataService;
        _antiCheatGuard = antiCheatGuard;
        _overlayIpcClient = overlayIpcClient;
        _eventBus = eventBus;
        _trainerCatalogService = trainerCatalogService;
        _trainerRuntimeService = trainerRuntimeService;
        _trainerSafetyGate = trainerSafetyGate;
        _saveTimeMachineService = saveTimeMachineService;
        _cloudSaveSyncService = cloudSaveSyncService;
        _saveWatcherService = saveWatcherService;
        _openExternalUrl = openExternalUrl ?? OpenUrlWithShell;
        _uiDispatcher = uiDispatcher;
        Settings = settingsViewModel;
        Trainer = trainerWorkbench;
        Saves = saveWorkbench ?? new SaveWorkbenchViewModel(
            launchService,
            saveTimeMachineService,
            cloudSaveSyncService,
            saveWatcherService);

        LibraryGroups.Add(_allGroup);
        LibraryGroups.Add(_gogGroup);
        LibraryGroups.Add(_steamripGroup);
        LibraryGroups.Add(_otherGroup);

        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.CurrentCategoryTitle))
            {
                OnPropertyChanged(nameof(TopBarTitle));
            }
        };
        Settings.StatusFeedbackRequested += msg => ShowStatusBanner(msg, isError: false);
        Settings.ScanDirectoriesRequested += ScanDirectoriesBatchAsync;
        Settings.ManualGameImportRequested += ImportManualGameFromSettingsAsync;
        Settings.GlobalSpoilerPreferenceChanged += reveal => RevealHiddenSpoilers = reveal;

        Trainer.StatusFeedbackRequested += (msg, isError) => ShowStatusBanner(msg, isError);
        Trainer.OpenTrainerSettingsRequested += OpenTrainerSandboxSettings;
        Trainer.GameRecordUpdatedRequested += async gameId =>
        {
            var refreshed = await _libraryService.GetGameByIdAsync(gameId).ConfigureAwait(true);
            var item = AllGames.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null && item is not null)
            {
                item.UpdateRecord(refreshed);
            }

            _trainerSidebarSafetyCache.Remove(gameId);
            await RefreshTrainerHubStateAsync().ConfigureAwait(true);
        };
        Trainer.TrainerCatalogChangedRequested += async _ =>
        {
            await RefreshTrainerHubStateAsync().ConfigureAwait(true);
        };

        Saves.StatusFeedbackRequested += (msg, isError) => ShowStatusBanner(msg, isError);
        Saves.OpenSavesSettingsRequested += OpenSavesSettings;
        Saves.SaveTreeMutatedRequested += async _ =>
        {
            await RefreshSaveHubStateAsync().ConfigureAwait(true);
        };

        if (_saveWatcherService is not null)
        {
            _saveWatcherService.WatcherStatusChanged += OnSaveWatcherStatusChanged;
        }

        _launchService.SessionChanged += OnLaunchSessionChanged;
        _achievementService.AchievementUnlocked += OnAchievementUnlocked;
        _trainerRuntimeService.StateChanged += OnTrainerRuntimeStateChanged;
        _antiCheatGuard.StateChanged += (_, snap) =>
            DispatchToUi(() => UpdateGuardBadge(snap));

        _subscriptions.Add(_eventBus.Subscribe<GameImportedOrRescannedEvent>((ev, _) =>
            DispatchToUiOrInlineAsync(() => HandleGameImportedOrRescannedAsync(ev))));

        _subscriptions.Add(_eventBus.Subscribe<GameRemovedEvent>((_, _) =>
            DispatchToUiOrInlineAsync(() => EnqueueLibraryReloadAsync(LibraryReloadRequest.PreserveCurrent()))));

        _subscriptions.Add(_eventBus.Subscribe<AchievementSchemaUpdatedEvent>((ev, _) =>
            DispatchToUiOrInlineAsync(() => HandleSchemaUpdatedAsync(ev))));

        _subscriptions.Add(_eventBus.Subscribe<SaveTreeChangedEvent>((ev, _) =>
            DispatchToUiOrInlineAsync(() => HandleSaveTreeChangedAsync(ev))));

        _uiRefreshTimer = _uiDispatcher is not null
            ? new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnUiRefreshTick, _uiDispatcher)
            : new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
        if (_uiDispatcher is null)
        {
            _uiRefreshTimer.Tick += OnUiRefreshTick;
            _uiRefreshTimer.Start();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Settings.InitializeAsync(cancellationToken).ConfigureAwait(true);
        RevealHiddenSpoilers = Settings.GlobalRevealSpoilers;
        UpdateGuardBadge(_antiCheatGuard.CurrentSnapshot);

        await EnqueueLibraryReloadAsync(
            LibraryReloadRequest.PreserveCurrent(forceRefreshAllActiveAchievements: true),
            cancellationToken).ConfigureAwait(true);
        RefreshUnimportedLocalGamesHint();
    }

    public void UpdateViewportWidth(double windowWidth)
    {
        bool narrow = windowWidth < 1000.0;
        if (IsNarrowWindow != narrow)
        {
            IsNarrowWindow = narrow;
            if (!narrow)
            {
                IsNarrowSidebarOpen = false;
            }

            OnPropertyChanged(nameof(IsSidebarVisible));
        }
    }

    partial void OnActiveSectionChanged(PrimaryNavSection value)
    {
        if (value != PrimaryNavSection.Settings)
        {
            _lastNonSettingsSection = value;
        }

        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyMainPanelViewFlags();

        if (value == PrimaryNavSection.Achievements)
        {
            _ = RefreshAchievementsCenterOverviewAsync();
        }
        else if (value == PrimaryNavSection.Saves)
        {
            _ = RefreshSaveHubStateAsync();
            if (!IsSaveOverviewSelected && SelectedSaveGame is not null)
            {
                _ = Saves.LoadForGameAsync(SelectedSaveGame);
            }
        }
        else if (value == PrimaryNavSection.Trainers)
        {
            _ = RefreshTrainerHubStateAsync();
            if (!IsTrainerOverviewSelected && SelectedTrainerGame is not null)
            {
                _ = LoadTrainerWorkbenchForTrainerSectionGameAsync(SelectedTrainerGame);
            }
        }
        else if (value == PrimaryNavSection.Library && SelectedGame is not null)
        {
            _ = LoadAchievementsForGameAsync(SelectedGame.GameId);
            if (SelectedGameSubTab == GameDetailSubTab.Saves)
            {
                _ = Saves.LoadForGameAsync(SelectedGame);
            }
            else if (SelectedGameSubTab == GameDetailSubTab.GameData)
            {
                _ = LoadGameDataForSelectedAsync(refreshOnline: false);
            }
            else if (SelectedGameSubTab == GameDetailSubTab.Trainers)
            {
                _ = Trainer.LoadForGameAsync(SelectedGame);
            }
        }
    }

    partial void OnIsNarrowSidebarOpenChanged(bool value) => OnPropertyChanged(nameof(IsSidebarVisible));

    partial void OnSelectedGameChanged(GameListItemViewModel? oldValue, GameListItemViewModel? newValue)
    {
        if (!_isApplyingInternalSelection)
        {
            Interlocked.Increment(ref _userSelectionVersion);
        }

        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            if (!_suppressSelectionAchievementAutoLoad)
            {
                _ = LoadAchievementsForGameAsync(newValue.GameId);
            }

            if (SelectedGameSubTab == GameDetailSubTab.Saves)
            {
                _ = Saves.LoadForGameAsync(newValue);
            }
            else if (SelectedGameSubTab == GameDetailSubTab.GameData)
            {
                _ = LoadGameDataForSelectedAsync(refreshOnline: false);
            }
            else if (ActiveSection == PrimaryNavSection.Library && SelectedGameSubTab == GameDetailSubTab.Trainers)
            {
                _ = Trainer.LoadForGameAsync(newValue);
            }
        }
        else
        {
            _loadedAchievementRowsGameId = null;
            _loadedAchievementRowsGameTitle = null;
            CurrentAchievementSummary = null;
            _currentGameRawAchievements = [];
            FilteredAchievements.Clear();
            GameDataGroups.Clear();
            AllGameDataItems.Clear();
            LatestUndoSnapshot = null;
            if (ActiveSection == PrimaryNavSection.Library)
            {
                _ = Trainer.LoadForGameAsync(null);
                _ = Saves.LoadForGameAsync(null);
            }
        }

        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        OnPropertyChanged(nameof(PrimaryLaunchButtonText));
        OnPropertyChanged(nameof(CanActivateGbeForkOnSelected));
        OnPropertyChanged(nameof(CanRestoreDllOnSelected));
        NotifyMainPanelViewFlags();
    }

    partial void OnSelectedGameSubTabChanged(GameDetailSubTab value)
    {
        OnPropertyChanged(nameof(ShowGameAchievementsSubPanel));
        OnPropertyChanged(nameof(ShowGameSavesSubPanel));
        OnPropertyChanged(nameof(ShowGameDataSubPanel));
        OnPropertyChanged(nameof(ShowGameTrainersSubPanel));

        if (value == GameDetailSubTab.Saves)
        {
            _ = Saves.LoadForGameAsync(SelectedGame);
        }
        else if (value == GameDetailSubTab.GameData && SelectedGame is not null)
        {
            _ = LoadGameDataForSelectedAsync(refreshOnline: false);
        }
        else if (value == GameDetailSubTab.Trainers)
        {
            _ = Trainer.LoadForGameAsync(SelectedGame);
        }
    }

    partial void OnLatestUndoSnapshotChanged(GameDataUndoSnapshotDto? value)
    {
        OnPropertyChanged(nameof(HasUndoSnapshot));
        OnPropertyChanged(nameof(UndoSnapshotToolTipText));
    }

    partial void OnIsAchievementsOverviewSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyMainPanelViewFlags();
    }

    partial void OnSelectedAchievementsGameChanged(GameListItemViewModel? value)
    {
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyMainPanelViewFlags();
        if (value is not null && !IsAchievementsOverviewSelected)
        {
            _ = LoadAchievementsForGameAsync(value.GameId);
        }
    }

    partial void OnIsSaveOverviewSelectedChanged(bool value)
    {
        SyncSaveSidebarSelectionFlags();
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyMainPanelViewFlags();
    }

    partial void OnSelectedSaveGameChanged(GameListItemViewModel? value)
    {
        SyncSaveSidebarSelectionFlags();
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyMainPanelViewFlags();
    }

    partial void OnSaveSidebarSearchTextChanged(string value) => RebuildFilteredSaveSidebarGames();

    partial void OnTotalSaveSnapshotsCountChanged(int value) => OnPropertyChanged(nameof(SaveOverviewCountBadgeText));

    partial void OnHasAnyCloudConflictChanged(bool value)
    {
        OnPropertyChanged(nameof(SavesRailToolTip));
        OnPropertyChanged(nameof(SaveOverviewCountBadgeText));
    }

    partial void OnHasAnySaveWatcherActiveChanged(bool value) => OnPropertyChanged(nameof(SavesRailToolTip));

    partial void OnIsSyncingAllCloudSavesChanged(bool value) => OnPropertyChanged(nameof(CanSyncAllCloudSaves));

    partial void OnIsTrainerOverviewSelectedChanged(bool value)
    {
        SyncTrainerSidebarSelectionFlags();
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyMainPanelViewFlags();
    }

    partial void OnSelectedTrainerGameChanged(GameListItemViewModel? value)
    {
        SyncTrainerSidebarSelectionFlags();
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyMainPanelViewFlags();
    }

    partial void OnTrainerSidebarSearchTextChanged(string value) => RebuildFilteredTrainerSidebarGames();

    partial void OnTotalManagedTrainersCountChanged(int value)
    {
        OnPropertyChanged(nameof(ManagedTrainersCountBadgeText));
        OnPropertyChanged(nameof(TrainerOverviewCountBadgeText));
    }

    partial void OnCurrentAchievementSummaryChanged(GameAchievementSummaryDto? value)
    {
        OnPropertyChanged(nameof(CurrentGameUnlockedCount));
        OnPropertyChanged(nameof(CurrentGameTotalCount));
        OnPropertyChanged(nameof(CurrentGameCompletionPercent));
        OnPropertyChanged(nameof(AchievementsSubTabHeaderText));
        OnPropertyChanged(nameof(CurrentGameProgressSummaryText));
        OnPropertyChanged(nameof(CanActivateGbeForkOnSelected));
        OnPropertyChanged(nameof(CanRestoreDllOnSelected));
    }

    partial void OnSelectedAchievementTabChanged(AchievementFilterTab value) => RebuildFilteredAchievementRows();

    partial void OnRevealHiddenSpoilersChanged(bool value)
    {
        Settings.GlobalRevealSpoilers = value;
        foreach (var row in FilteredAchievements)
        {
            row.GlobalRevealSpoilers = value;
        }
    }

    partial void OnAchievementSearchTextChanged(string value) => RebuildFilteredAchievementRows();

    [RelayCommand]
    private void NavigateSection(PrimaryNavSection section) => ActiveSection = section;

    [RelayCommand]
    private void ReturnFromSettings() => ActiveSection = _lastNonSettingsSection;

    [RelayCommand]
    private void ToggleNarrowSidebar() => IsNarrowSidebarOpen = !IsNarrowSidebarOpen;

    [RelayCommand]
    private void SelectLibraryGame(GameListItemViewModel? item)
    {
        Interlocked.Increment(ref _userSelectionVersion);
        SelectedGame = item;
        if (ActiveSection != PrimaryNavSection.Library)
        {
            ActiveSection = PrimaryNavSection.Library;
        }
    }

    [RelayCommand]
    private void OpenGameSavesSubTab()
    {
        SelectedGameSubTab = GameDetailSubTab.Saves;
        if (SelectedGame is not null)
        {
            _ = Saves.LoadForGameAsync(SelectedGame);
        }
    }

    [RelayCommand]
    private void OpenGameDataSubTab()
    {
        SelectedGameSubTab = GameDetailSubTab.GameData;
        if (SelectedGame is not null)
        {
            _ = LoadGameDataForSelectedAsync(refreshOnline: false);
        }
    }

    [RelayCommand]
    private void OpenGameTrainersSubTab()
    {
        SelectedGameSubTab = GameDetailSubTab.Trainers;
        if (SelectedGame is not null)
        {
            _ = Trainer.LoadForGameAsync(SelectedGame);
        }
    }

    [RelayCommand]
    private void OpenLibrarySettings()
    {
        Settings.SelectedCategory = SettingsCategory.GeneralAndLibrary;
        ActiveSection = PrimaryNavSection.Settings;
    }

    [RelayCommand]
    private void OpenTrainerSandboxSettings()
    {
        Settings.SelectedCategory = SettingsCategory.TrainerAndSandbox;
        ActiveSection = PrimaryNavSection.Settings;
    }

    [RelayCommand]
    private void OpenSavesSettings()
    {
        Settings.SelectedCategory = SettingsCategory.SavesAndCloudSync;
        ActiveSection = PrimaryNavSection.Settings;
    }

    [RelayCommand]
    private void SelectSavesOverview()
    {
        bool sectionChanged = ActiveSection != PrimaryNavSection.Saves;
        IsSaveOverviewSelected = true;
        SelectedSaveGame = null;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        if (sectionChanged)
        {
            ActiveSection = PrimaryNavSection.Saves;
        }
        else
        {
            _ = RefreshSaveHubStateAsync();
        }
    }

    [RelayCommand]
    private void SelectSaveSidebarGame(SaveSidebarGameItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectSaveGameItem(item.Game);
    }

    [RelayCommand]
    private void SelectSaveGameItem(GameListItemViewModel? game)
    {
        if (game is null)
        {
            return;
        }

        bool sectionChanged = ActiveSection != PrimaryNavSection.Saves;
        SelectedSaveGame = game;
        IsSaveOverviewSelected = false;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        if (sectionChanged)
        {
            ActiveSection = PrimaryNavSection.Saves;
        }
        else
        {
            _ = Saves.LoadForGameAsync(game);
        }
    }

    [RelayCommand]
    private void ClearSaveSidebarSearch() => SaveSidebarSearchText = string.Empty;

    [RelayCommand]
    private async Task RefreshSaveHubAsync()
    {
        await RefreshSaveHubStateAsync().ConfigureAwait(true);
        if (!IsSaveOverviewSelected && SelectedSaveGame is not null)
        {
            await Saves.LoadForGameAsync(SelectedSaveGame).ConfigureAwait(true);
        }

        ShowStatusBanner($"已刷新存档库（共 {TotalSaveSnapshotsCount} 份快照 · {TotalSaveBranchesCount} 条分支）", isError: false);
    }

    [RelayCommand]
    private void OpenSaveVaultFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.VaultChunksDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.VaultChunksDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"打开存档库目录失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void OpenFirstConflictedSaveGame()
    {
        SaveSidebarGameItemViewModel? first = _allSaveSidebarGames.FirstOrDefault(g => g.HasCloudConflict);
        if (first is not null)
        {
            SelectSaveGameItem(first.Game);
            Saves.IsCloudDrawerOpen = true;
        }
    }

    [RelayCommand]
    public async Task SyncAllCloudSavesAsync()
    {
        if (_cloudSaveSyncService is null || IsSyncingAllCloudSaves)
        {
            return;
        }

        if (HasAnyRunningGame)
        {
            ShowStatusBanner("当前有游戏正在运行，请先退出游戏后再执行全库云同步", isError: true);
            return;
        }

        IsSyncingAllCloudSaves = true;
        try
        {
            IReadOnlyList<CloudSyncExecutionReport> reports = await _cloudSaveSyncService.SyncAllConfiguredGamesAsync().ConfigureAwait(true);
            await RefreshSaveHubStateAsync().ConfigureAwait(true);
            int ok = reports.Count(r => r.Succeeded);
            int conflicts = reports.Count(r => r.Relation == CloudSyncRelation.Diverged);
            ShowStatusBanner(
                conflicts > 0
                    ? $"全库云同步完成：{ok} 款已同步，{conflicts} 款发现分叉冲突待处理"
                    : (reports.Count > 0 ? $"全库云同步完成：已同步 {ok}/{reports.Count} 款游戏存档" : "尚未为任何游戏绑定云同步端点"),
                isError: conflicts > 0);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"全库云同步失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsSyncingAllCloudSaves = false;
        }
    }

    [RelayCommand]
    private void SelectTrainersOverview()
    {
        bool sectionChanged = ActiveSection != PrimaryNavSection.Trainers;
        IsTrainerOverviewSelected = true;
        SelectedTrainerGame = null;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        if (sectionChanged)
        {
            ActiveSection = PrimaryNavSection.Trainers;
        }
        else
        {
            _ = RefreshTrainerHubStateAsync();
        }
    }

    [RelayCommand]
    private void SelectTrainerSidebarGame(TrainerSidebarGameItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectTrainerGameItem(item.Game);
    }

    [RelayCommand]
    private void SelectTrainerGameItem(GameListItemViewModel? game)
    {
        if (game is null)
        {
            return;
        }

        bool sectionChanged = ActiveSection != PrimaryNavSection.Trainers;
        SelectedTrainerGame = game;
        IsTrainerOverviewSelected = false;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        if (sectionChanged)
        {
            ActiveSection = PrimaryNavSection.Trainers;
        }
        else
        {
            _ = LoadTrainerWorkbenchForTrainerSectionGameAsync(game);
        }
    }

    [RelayCommand]
    private void ClearTrainerSidebarSearch() => TrainerSidebarSearchText = string.Empty;

    [RelayCommand]
    private void SelectFirstEligibleTrainerGame()
    {
        var first = _allTrainerSidebarGames.FirstOrDefault(g => !g.IsSafetyLocked);
        if (first is not null)
        {
            SelectTrainerGameItem(first.Game);
            return;
        }

        if (AllGames.Count == 0)
        {
            OpenLibrarySettings();
            ShowStatusBanner("游戏库尚未入库单机游戏，请先在设置中配置扫描目录或导入游戏", isError: false);
        }
        else
        {
            ShowStatusBanner("当前游戏库中暂无可启用修改器的单机游戏", isError: true);
        }
    }

    [RelayCommand]
    private async Task RefreshTrainerHubAsync()
    {
        _trainerSidebarSafetyCache.Clear();
        await RefreshTrainerHubStateAsync().ConfigureAwait(true);
        if (!IsTrainerOverviewSelected && SelectedTrainerGame is not null)
        {
            await LoadTrainerWorkbenchForTrainerSectionGameAsync(SelectedTrainerGame).ConfigureAwait(true);
        }

        ShowStatusBanner($"已刷新修改器目录（共 {TotalManagedTrainersCount} 项）", isError: false);
    }

    [RelayCommand]
    private async Task OpenTrainerSandboxRootFolderAsync()
    {
        try
        {
            TrainerSettingsDto settings = await _db.GetTrainerSettingsAsync().ConfigureAwait(true);
            string sandboxRoot = _paths.ResolveTrainerSandboxDirectory(settings.CustomSandboxRoot, requirePureAscii: false);
            Directory.CreateDirectory(sandboxRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{sandboxRoot}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"打开修改器沙箱目录失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task StopActiveRunningTrainerAsync()
    {
        await Trainer.StopTrainerAsync().ConfigureAwait(true);
        await RefreshTrainerHubStateAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ToggleActiveRunningTrainerWindowAsync()
    {
        await Trainer.ToggleOriginalWindowVisibilityAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenActiveRunningTrainerWorkbench()
    {
        string? runningGameId = _trainerRuntimeService.ActiveSession?.GameId;
        if (string.IsNullOrWhiteSpace(runningGameId))
        {
            return;
        }

        var match = AllGames.FirstOrDefault(g => string.Equals(g.GameId, runningGameId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            SelectTrainerGameItem(match);
        }
    }

    [RelayCommand]
    private void SelectAchievementsOverview()
    {
        IsAchievementsOverviewSelected = true;
        SelectedAchievementsGame = null;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        _ = RefreshAchievementsCenterOverviewAsync();
    }

    [RelayCommand]
    private void SelectAchievementsGameItem(GameListItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedAchievementsGame = item;
        IsAchievementsOverviewSelected = false;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        _ = LoadAchievementsForGameAsync(item.GameId);
    }

    [RelayCommand]
    private void JumpToGameInLibrary(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        var match = AllGames.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            Interlocked.Increment(ref _userSelectionVersion);
            SelectedGame = match;
            SelectedGameSubTab = GameDetailSubTab.Achievements;
            ActiveSection = PrimaryNavSection.Library;
        }
    }

    [RelayCommand]
    private void SetAchievementFilterTab(AchievementFilterTab tab) => SelectedAchievementTab = tab;

    [RelayCommand]
    private void DismissStatusBanner() => StatusBannerMessage = string.Empty;

    public async Task ImportManualGameFromSettingsAsync(string rawInput)
    {
        if (IsBusyWorking || string.IsNullOrWhiteSpace(rawInput))
        {
            return;
        }

        string resolvedPath = ResolveUserEnteredPath(rawInput.Trim().Trim('"'));
        if (!Directory.Exists(resolvedPath) && !File.Exists(resolvedPath))
        {
            ShowStatusBanner($"未找到路径：{rawInput}", isError: true);
            return;
        }

        await ImportOrScanSinglePathAsync(resolvedPath).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ScanDefaultLibraryAsync()
    {
        var dirs = Settings.GetAllEffectiveScanDirectories();
        if (dirs.Count == 0)
        {
            OpenLibrarySettings();
            ShowStatusBanner("尚未配置游戏库扫描目录，请在「设置 → 常规与游戏库」添加目录", isError: false);
            return;
        }

        await ScanDirectoriesBatchAsync(dirs).ConfigureAwait(true);
    }

    public async Task ImportDroppedPathsAsync(IReadOnlyList<string> droppedPaths)
    {
        if (droppedPaths.Count == 0 || IsBusyWorking)
        {
            return;
        }

        // If user is on the Trainers sub-tab OR dedicated Trainers per-game workbench for a permitted game and drops a trainer archive/exe, import as trainer!
        bool isLibraryTrainerSubTab = ActiveSection == PrimaryNavSection.Library
                                      && SelectedGame is not null
                                      && SelectedGameSubTab == GameDetailSubTab.Trainers;
        bool isDedicatedTrainerGameView = ActiveSection == PrimaryNavSection.Trainers
                                          && !IsTrainerOverviewSelected
                                          && SelectedTrainerGame is not null;

        if ((isLibraryTrainerSubTab || isDedicatedTrainerGameView)
            && !Trainer.IsProtectedOnlineGameBlocked
            && droppedPaths.Count == 1
            && File.Exists(droppedPaths[0])
            && IsTrainerFileExtension(Path.GetExtension(droppedPaths[0])))
        {
            await Trainer.ImportLocalTrainerPathAsync(droppedPaths[0]).ConfigureAwait(true);
            return;
        }

        if (ActiveSection == PrimaryNavSection.Trainers
            && (IsTrainerOverviewSelected || SelectedTrainerGame is null)
            && droppedPaths.Count == 1
            && File.Exists(droppedPaths[0])
            && IsTrainerFileExtension(Path.GetExtension(droppedPaths[0])))
        {
            ShowStatusBanner("请先在左侧选择一款单机游戏，再将修改器文件拖入窗口导入至该游戏沙箱", isError: false);
            return;
        }

        var validPaths = droppedPaths.Where(static p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (validPaths.Count == 0)
        {
            return;
        }

        int selectionVersionAtStart = Volatile.Read(ref _userSelectionVersion);
        bool isSingleInputPath = validPaths.Count == 1;
        string? explicitSingleImportedId = null;
        string? firstBatchImportedId = null;
        int importedCount = 0;

        IsBusyWorking = true;
        try
        {
            foreach (string rawPath in validPaths)
            {
                if (!File.Exists(rawPath) && !Directory.Exists(rawPath))
                {
                    continue;
                }

                GameRecord? single;
                if (isSingleInputPath)
                {
                    Interlocked.Increment(ref _suppressEventAutoSelectCount);
                    try
                    {
                        single = await _libraryService.ImportGameAsync(rawPath).ConfigureAwait(true);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _suppressEventAutoSelectCount);
                    }
                }
                else
                {
                    Interlocked.Increment(ref _activeLibraryScanCount);
                    try
                    {
                        single = await _libraryService.ImportGameAsync(rawPath).ConfigureAwait(true);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeLibraryScanCount);
                    }
                }

                if (single is not null)
                {
                    if (isSingleInputPath)
                    {
                        explicitSingleImportedId = single.GameId;
                    }
                    else
                    {
                        firstBatchImportedId ??= single.GameId;
                    }

                    importedCount++;
                }
                else if (Directory.Exists(rawPath))
                {
                    Interlocked.Increment(ref _activeLibraryScanCount);
                    IReadOnlyList<GameRecord> batch;
                    try
                    {
                        batch = await _libraryService.ScanLibraryDirectoryAsync(rawPath).ConfigureAwait(true);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeLibraryScanCount);
                    }

                    if (batch.Count > 0)
                    {
                        firstBatchImportedId ??= batch[0].GameId;
                        importedCount += batch.Count;
                    }
                }
            }

            LibraryReloadRequest reloadRequest = explicitSingleImportedId is not null
                ? LibraryReloadRequest.ExplicitIfUnchanged(
                    explicitSingleImportedId,
                    selectionVersionAtStart,
                    dirtyAchievementGameId: explicitSingleImportedId)
                : LibraryReloadRequest.PreserveOrFallback(firstBatchImportedId, selectionVersionAtStart);

            await EnqueueLibraryReloadAsync(reloadRequest).ConfigureAwait(true);
            RefreshUnimportedLocalGamesHint();

            if (importedCount > 0)
            {
                ActiveSection = PrimaryNavSection.Library;
                ShowStatusBanner($"已成功导入/更新 {importedCount} 款游戏并同步成就元数据", isError: false);
            }
            else
            {
                ShowStatusBanner("未在拖入目录中识别到可导入的游戏主程序", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"导入失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    [RelayCommand]
    private async Task PrimaryLaunchOrStopAsync()
    {
        if (SelectedGame is null)
        {
            return;
        }

        string targetGameId = SelectedGame.GameId;
        if (SelectedGame.IsRunning)
        {
            bool stopped = await _launchService.StopGameAsync(targetGameId).ConfigureAwait(true);
            ShowStatusBanner(
                stopped ? $"已请求停止《{SelectedGame.Title}》" : $"未能停止《{SelectedGame.Title}》，可能已自行退出",
                isError: !stopped);
            return;
        }

        GameLaunchResult result = await _launchService.LaunchGameAsync(targetGameId).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ShowStatusBanner(result.ErrorMessage ?? "启动游戏失败", isError: true);
            return;
        }

        if (result.Session is not null && SelectedGame.GameId == targetGameId)
        {
            SelectedGame.UpdateSession(result.Session);
            RebuildGroupsPreservingSelection();
            OnPropertyChanged(nameof(HasAnyRunningGame));
            OnPropertyChanged(nameof(LibraryRailToolTip));
            OnPropertyChanged(nameof(PrimaryLaunchButtonText));
        }

        ShowStatusBanner($"已启动《{SelectedGame.Title}》 · 进程追踪与实时跳杯监控已激活", isError: false);
    }

    [RelayCommand]
    private void OpenSelectedGameFolder()
    {
        var game = ActiveWorkbenchGame;
        if (game is null || !Directory.Exists(game.Record.InstallDir))
        {
            ShowStatusBanner("游戏安装目录不存在", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{game.Record.InstallDir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"打开文件夹失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task RefreshChineseSchemaAsync()
    {
        var game = ActiveWorkbenchGame;
        if (game is null || IsBusyWorking)
        {
            return;
        }

        IsBusyWorking = true;
        try
        {
            var summary = await _achievementService.SyncGameSchemaAsync(game.GameId, forceRefresh: true).ConfigureAwait(true);
            await LoadAchievementsForGameAsync(game.GameId).ConfigureAwait(true);
            ShowStatusBanner($"已刷新《{game.Title}》中文成就元数据（共 {summary.TotalCount} 项）", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"刷新成就元数据失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    [RelayCommand]
    private async Task RescanLocalAchievementsAsync()
    {
        var game = ActiveWorkbenchGame;
        if (game is null || IsBusyWorking)
        {
            return;
        }

        IsBusyWorking = true;
        try
        {
            _ = await _libraryService.RescanGameAsync(game.GameId).ConfigureAwait(true);
            int newlyFound = await _achievementService.ScanLocalUnlocksAsync(game.GameId, silentSeed: false).ConfigureAwait(true);
            await EnqueueLibraryReloadAsync(
                LibraryReloadRequest.PreserveCurrent(dirtyAchievementGameId: game.GameId)).ConfigureAwait(true);
            ShowStatusBanner($"已完成《{game.Title}》本地成就记录对账（新增识别 {newlyFound} 项）", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"重新扫描本地成就记录失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    [RelayCommand]
    private async Task TestInGameOverlayToastAsync()
    {
        var game = ActiveWorkbenchGame;
        try
        {
            await _achievementService.SendTestToastAsync(game?.GameId, Settings.SelectedOverlayTheme).ConfigureAwait(true);
            ShowStatusBanner("已推送测试跳杯弹窗（零焦点抢占）", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"发送测试弹窗失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task ActivateGbeForkPatchAsync()
    {
        var game = ActiveWorkbenchGame;
        if (game is null || IsBusyWorking)
        {
            return;
        }

        IsBusyWorking = true;
        try
        {
            GbeForkActivationResult res = await _achievementService.ActivateGbeForkAsync(game.GameId).ConfigureAwait(true);
            await EnqueueLibraryReloadAsync(
                LibraryReloadRequest.PreserveCurrent(dirtyAchievementGameId: game.GameId)).ConfigureAwait(true);
            ShowStatusBanner(res.Message, isError: !res.Succeeded);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"激活离线成就补丁失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    [RelayCommand]
    private async Task RestoreOriginalDllAsync()
    {
        var game = ActiveWorkbenchGame;
        if (game is null || IsBusyWorking)
        {
            return;
        }

        IsBusyWorking = true;
        try
        {
            GbeForkActivationResult res = await _achievementService.RestoreOriginalDllAsync(game.GameId).ConfigureAwait(true);
            await EnqueueLibraryReloadAsync(
                LibraryReloadRequest.PreserveCurrent(dirtyAchievementGameId: game.GameId)).ConfigureAwait(true);
            ShowStatusBanner(res.Message, isError: !res.Succeeded);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"还原原始 DLL 失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedGameFromLibraryAsync()
    {
        var game = SelectedGame;
        if (game is null)
        {
            return;
        }

        if (game.IsRunning)
        {
            ShowStatusBanner("游戏正在运行中，请先停止游戏后再移除记录", isError: true);
            return;
        }

        string title = game.Title;
        bool removed = await _libraryService.RemoveGameAsync(game.GameId).ConfigureAwait(true);
        if (removed)
        {
            Interlocked.Increment(ref _userSelectionVersion);
            SelectedGame = null;
            await EnqueueLibraryReloadAsync(LibraryReloadRequest.PreserveCurrent()).ConfigureAwait(true);
            RefreshUnimportedLocalGamesHint();
            ShowStatusBanner($"已从游戏库移除《{title}》（游戏本体与存档文件完好保留）", isError: false);
        }
    }

    // -------------------------------------------------------------------------
    // Game Data Management & Reversible Cleanup Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task RefreshGameDataOnlineAsync()
    {
        if (SelectedGame is null || IsInspectingGameData)
        {
            return;
        }

        await LoadGameDataForSelectedAsync(refreshOnline: true).ConfigureAwait(true);
        ShowStatusBanner($"已同步《{SelectedGame.Title}》PCGamingWiki 与本地数据路径清单", isError: false);
    }

    [RelayCommand]
    private void SelectAllSafeGameData()
    {
        foreach (var item in AllGameDataItems)
        {
            if (item.IsSafeToClean)
            {
                item.IsSelected = true;
            }
        }

        NotifyGameDataSelectionChanged();
    }

    [RelayCommand]
    private void ClearGameDataSelection()
    {
        foreach (var item in AllGameDataItems)
        {
            item.IsSelected = false;
        }

        NotifyGameDataSelectionChanged();
    }

    [RelayCommand]
    private async Task CleanSelectedGameDataAsync()
    {
        var game = SelectedGame;
        if (game is null || IsBusyWorking)
        {
            return;
        }

        if (game.IsRunning)
        {
            ShowStatusBanner($"《{game.Title}》正在运行中，请先退出游戏后再清理数据", isError: true);
            return;
        }

        var selectedIds = AllGameDataItems
            .Where(i => i.IsSelected && i.IsSafeToClean)
            .Select(i => i.EntryId)
            .ToList();

        if (selectedIds.Count == 0 && !ResetSpawnStateOnClean)
        {
            ShowStatusBanner("请至少勾选一项要清理的数据，或开启「同时重置 SPAWN 成就进度与游玩时长」", isError: true);
            return;
        }

        IsBusyWorking = true;
        try
        {
            var req = new GameDataCleanRequest
            {
                GameId = game.GameId,
                EntryIds = selectedIds,
                ResetSpawnAchievementsAndPlaytime = ResetSpawnStateOnClean,
                CreateUndoBackup = true,
            };

            GameDataCleanResult result = await _gameDataService.CleanGameDataAsync(req).ConfigureAwait(true);
            if (result.Succeeded && ResetSpawnStateOnClean)
            {
                await _achievementService.ResetGameAchievementStateAsync(game.GameId).ConfigureAwait(true);
            }

            await EnqueueLibraryReloadAsync(
                LibraryReloadRequest.PreserveCurrent(dirtyAchievementGameId: game.GameId)).ConfigureAwait(true);
            await LoadGameDataForSelectedAsync(refreshOnline: false).ConfigureAwait(true);
            ShowStatusBanner(result.Message, isError: !result.Succeeded);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"清理游戏数据失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    [RelayCommand]
    private async Task RestoreLastCleanupAsync()
    {
        var game = SelectedGame;
        if (game is null || IsBusyWorking)
        {
            return;
        }

        if (game.IsRunning)
        {
            ShowStatusBanner($"《{game.Title}》正在运行中，请先停止游戏后再恢复备份", isError: true);
            return;
        }

        IsBusyWorking = true;
        try
        {
            GameDataCleanResult result = await _gameDataService.RestoreLastCleanupAsync(game.GameId).ConfigureAwait(true);
            await EnqueueLibraryReloadAsync(
                LibraryReloadRequest.PreserveCurrent(dirtyAchievementGameId: game.GameId)).ConfigureAwait(true);
            await LoadGameDataForSelectedAsync(refreshOnline: false).ConfigureAwait(true);
            ShowStatusBanner(result.Message, isError: !result.Succeeded);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"恢复上次清理失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    private async Task LoadGameDataForSelectedAsync(bool refreshOnline, CancellationToken cancellationToken = default)
    {
        var game = SelectedGame;
        if (game is null)
        {
            return;
        }

        string targetId = game.GameId;
        IsInspectingGameData = true;
        try
        {
            var entries = await _gameDataService.InspectGameDataAsync(targetId, refreshOnline, cancellationToken).ConfigureAwait(true);
            var latestUndo = await _gameDataService.GetLatestUndoSnapshotAsync(targetId, cancellationToken).ConfigureAwait(true);

            if (SelectedGame?.GameId != targetId)
            {
                return;
            }

            AllGameDataItems.Clear();
            GameDataGroups.Clear();

            var itemVms = entries
                .Select(e => new GameDataItemViewModel(
                    e,
                    HandleOpenGameDataLocation,
                    HandleCopyGameDataPath,
                    NotifyGameDataSelectionChanged))
                .ToList();

            foreach (var vm in itemVms)
            {
                AllGameDataItems.Add(vm);
            }

            GameDataCategory[] categoryOrder =
            [
                GameDataCategory.SaveData,
                GameDataCategory.AchievementData,
                GameDataCategory.ConfigAndLogs,
                GameDataCategory.RegistryData,
            ];

            foreach (var category in categoryOrder)
            {
                var catItems = itemVms.Where(x => x.Category == category).ToList();
                if (catItems.Count > 0)
                {
                    GameDataGroups.Add(new GameDataCategoryGroupViewModel(category, catItems));
                }
            }

            LatestUndoSnapshot = latestUndo;
            NotifyGameDataSelectionChanged();
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"读取游戏数据路径失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsInspectingGameData = false;
        }
    }

    private void HandleOpenGameDataLocation(GameDataItemViewModel item)
    {
        if (item.IsRegistryEntry)
        {
            HandleCopyGameDataPath(item);
            return;
        }

        try
        {
            string path = item.PathOrRegistryKey;
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }

            // Walk up to nearest existing parent directory
            var dir = Directory.GetParent(path);
            while (dir is { Exists: false })
            {
                dir = dir.Parent;
            }

            if (dir is { Exists: true })
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir.FullName}\"",
                    UseShellExecute = true,
                });
                ShowStatusBanner("该路径尚未由游戏生成，已打开最近的上级目录", isError: false);
            }
            else
            {
                ShowStatusBanner("目标目录尚未生成", isError: false);
            }
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"打开目录失败：{ex.Message}", isError: true);
        }
    }

    private void HandleCopyGameDataPath(GameDataItemViewModel item)
    {
        try
        {
            Clipboard.SetText(item.PathOrRegistryKey);
            ShowStatusBanner($"已复制：{item.PathOrRegistryKey}", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"复制失败：{ex.Message}", isError: true);
        }
    }

    private void NotifyGameDataSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCleanableEntryCount));
        OnPropertyChanged(nameof(CleanSelectedButtonText));
    }

    // -------------------------------------------------------------------------
    // Core Library & Achievement Sync
    // -------------------------------------------------------------------------

    private async Task ImportOrScanSinglePathAsync(string resolvedPath)
    {
        int selectionVersionAtStart = Volatile.Read(ref _userSelectionVersion);
        IsBusyWorking = true;
        try
        {
            GameRecord? directGame;
            Interlocked.Increment(ref _suppressEventAutoSelectCount);
            try
            {
                directGame = await _libraryService.ImportGameAsync(resolvedPath).ConfigureAwait(true);
            }
            finally
            {
                Interlocked.Decrement(ref _suppressEventAutoSelectCount);
            }

            if (directGame is not null)
            {
                await EnqueueLibraryReloadAsync(
                    LibraryReloadRequest.ExplicitIfUnchanged(
                        directGame.GameId,
                        selectionVersionAtStart,
                        dirtyAchievementGameId: directGame.GameId)).ConfigureAwait(true);
                RefreshUnimportedLocalGamesHint();
                if (Volatile.Read(ref _userSelectionVersion) == selectionVersionAtStart)
                {
                    ActiveSection = PrimaryNavSection.Library;
                }

                ShowStatusBanner($"已添加游戏《{directGame.Title}》并自动同步成就库", isError: false);
                return;
            }

            if (Directory.Exists(resolvedPath))
            {
                Interlocked.Increment(ref _activeLibraryScanCount);
                IReadOnlyList<GameRecord> scanned;
                try
                {
                    scanned = await _libraryService.ScanLibraryDirectoryAsync(resolvedPath).ConfigureAwait(true);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeLibraryScanCount);
                }

                string? firstId = scanned.Count > 0 ? scanned[0].GameId : null;
                await EnqueueLibraryReloadAsync(
                    LibraryReloadRequest.PreserveOrFallback(firstId, selectionVersionAtStart)).ConfigureAwait(true);
                RefreshUnimportedLocalGamesHint();
                ActiveSection = PrimaryNavSection.Library;
                ShowStatusBanner(
                    scanned.Count > 0
                        ? $"扫描完成，共发现并同步 {scanned.Count} 款单机游戏"
                        : "扫描完成，该目录下未发现包含可执行游戏主程序的子目录",
                    isError: scanned.Count == 0);
            }
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"导入或扫描失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    private async Task ScanDirectoriesBatchAsync(IReadOnlyList<string> rootDirs)
    {
        if (IsBusyWorking)
        {
            return;
        }

        int selectionVersionAtStart = Volatile.Read(ref _userSelectionVersion);
        Interlocked.Increment(ref _activeLibraryScanCount);
        IsBusyWorking = true;
        try
        {
            int totalFound = 0;
            string? firstGameId = null;

            foreach (string root in rootDirs)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var found = await _libraryService.ScanLibraryDirectoryAsync(root).ConfigureAwait(true);
                totalFound += found.Count;
                firstGameId ??= found.Count > 0 ? found[0].GameId : null;
            }

            await EnqueueLibraryReloadAsync(
                LibraryReloadRequest.PreserveOrFallback(firstGameId, selectionVersionAtStart)).ConfigureAwait(true);
            RefreshUnimportedLocalGamesHint();

            ShowStatusBanner(
                totalFound > 0
                    ? $"扫描完成，已入库/更新 {totalFound} 款游戏"
                    : "已完成目录扫描，未发现新的游戏目录",
                isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"目录扫描失败：{ex.Message}", isError: true);
        }
        finally
        {
            Interlocked.Decrement(ref _activeLibraryScanCount);
            IsBusyWorking = false;
        }
    }

    private Task HandleGameImportedOrRescannedAsync(GameImportedOrRescannedEvent ev)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return Task.CompletedTask;
        }

        bool allowAutoSelectIfNone = SelectedGame is null
                                     && Volatile.Read(ref _suppressEventAutoSelectCount) == 0
                                     && (ev.IsNewImport || Volatile.Read(ref _activeLibraryScanCount) > 0);
        string? fallbackGameId = allowAutoSelectIfNone ? ev.Game.GameId : null;
        int selectionVersion = Volatile.Read(ref _userSelectionVersion);

        return EnqueueLibraryReloadAsync(
            LibraryReloadRequest.PreserveOrFallback(
                fallbackGameId,
                selectionVersion,
                dirtyAchievementGameId: ev.Game.GameId));
    }

    private Task EnqueueLibraryReloadAsync(
        LibraryReloadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return Task.CompletedTask;
        }

        Task completionTask;
        bool shouldStartLoop = false;
        lock (_reloadSync)
        {
            _pendingReloadBatch ??= new PendingLibraryReloadBatch();
            _pendingReloadBatch.Merge(request);
            completionTask = _pendingReloadBatch.Completion.Task;

            if (!_isReloadLoopActive)
            {
                _isReloadLoopActive = true;
                shouldStartLoop = true;
            }
        }

        if (shouldStartLoop)
        {
            _ = RunLibraryReloadLoopAsync();
        }

        return cancellationToken.CanBeCanceled
            ? completionTask.WaitAsync(cancellationToken)
            : completionTask;
    }

    private async Task RunLibraryReloadLoopAsync()
    {
        while (true)
        {
            PendingLibraryReloadBatch batch;
            lock (_reloadSync)
            {
                if (_pendingReloadBatch is null || Volatile.Read(ref _isDisposed) != 0)
                {
                    if (_pendingReloadBatch is not null)
                    {
                        _pendingReloadBatch.Completion.TrySetResult();
                        _pendingReloadBatch = null;
                    }

                    _isReloadLoopActive = false;
                    return;
                }

                batch = _pendingReloadBatch;
                _pendingReloadBatch = null;
            }

            try
            {
                await ExecuteSingleReloadPassAsync(batch).ConfigureAwait(true);
                batch.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                batch.Completion.TrySetException(ex);
            }
        }
    }

    private async Task ExecuteSingleReloadPassAsync(PendingLibraryReloadBatch batch)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        var records = await _libraryService.GetAllGamesAsync().ConfigureAwait(true);
        var summariesByGameId = new Dictionary<string, GameAchievementSummaryDto>(records.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            summariesByGameId[record.GameId] = await _achievementService
                .GetGameAchievementsAsync(record.GameId)
                .ConfigureAwait(true);
        }

        var recentUnlocks = await _achievementService.GetRecentUnlocksAsync(limit: 25).ConfigureAwait(true);

        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        // Synchronous UI commit phase: read live selection state AFTER all async awaits so user clicks
        // performed during background scan or async DB queries are never overwritten by a stale snapshot.
        int liveSelectionVersion = Volatile.Read(ref _userSelectionVersion);
        string? liveSelectedId = SelectedGame?.GameId;
        string? liveAchSelectedId = SelectedAchievementsGame?.GameId;
        string? liveTrainerSelectedId = SelectedTrainerGame?.GameId;

        string? targetSelectedId;
        if (!string.IsNullOrWhiteSpace(batch.ExplicitSelectGameId)
            && batch.ExplicitSelectionVersion == liveSelectionVersion)
        {
            targetSelectedId = batch.ExplicitSelectGameId;
        }
        else if (!string.IsNullOrWhiteSpace(liveSelectedId))
        {
            targetSelectedId = liveSelectedId;
        }
        else if (!string.IsNullOrWhiteSpace(batch.FallbackSelectGameId)
                 && batch.FallbackSelectionVersion == liveSelectionVersion)
        {
            targetSelectedId = batch.FallbackSelectGameId;
        }
        else
        {
            targetSelectedId = null;
        }

        var existingById = AllGames.ToDictionary(x => x.GameId, StringComparer.OrdinalIgnoreCase);
        var updatedItems = new List<GameListItemViewModel>(records.Count);

        foreach (var record in records)
        {
            summariesByGameId.TryGetValue(record.GameId, out var summary);

            if (existingById.TryGetValue(record.GameId, out var existingVm))
            {
                existingVm.UpdateRecord(record);
                existingVm.UpdateSession(_launchService.GetActiveSession(record.GameId));
                if (summary is not null)
                {
                    existingVm.UpdateAchievementCounts(summary.UnlockedCount, summary.TotalCount, summary.ChannelStatusText);
                }

                updatedItems.Add(existingVm);
            }
            else
            {
                var vm = new GameListItemViewModel(record);
                vm.UpdateSession(_launchService.GetActiveSession(record.GameId));
                if (summary is not null)
                {
                    vm.UpdateAchievementCounts(summary.UnlockedCount, summary.TotalCount, summary.ChannelStatusText);
                }

                updatedItems.Add(vm);
            }
        }

        var ordered = SortGamesRunningFirst(updatedItems);
        SyncCollectionInPlace(AllGames, ordered);
        RebuildGroupsPreservingSelection();

        GameListItemViewModel? resolvedSelectedVm = string.IsNullOrWhiteSpace(targetSelectedId)
            ? null
            : AllGames.FirstOrDefault(g => string.Equals(g.GameId, targetSelectedId, StringComparison.OrdinalIgnoreCase));

        if (!ReferenceEquals(SelectedGame, resolvedSelectedVm))
        {
            bool hasPreloadedSummary = resolvedSelectedVm is not null
                                       && summariesByGameId.ContainsKey(resolvedSelectedVm.GameId);
            _isApplyingInternalSelection = true;
            _suppressSelectionAchievementAutoLoad = hasPreloadedSummary;
            try
            {
                SelectedGame = resolvedSelectedVm;
            }
            finally
            {
                _suppressSelectionAchievementAutoLoad = false;
                _isApplyingInternalSelection = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(liveAchSelectedId))
        {
            var resolvedAchVm = AllGames.FirstOrDefault(g => string.Equals(g.GameId, liveAchSelectedId, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(SelectedAchievementsGame, resolvedAchVm))
            {
                SelectedAchievementsGame = resolvedAchVm;
                if (SelectedAchievementsGame is null)
                {
                    IsAchievementsOverviewSelected = true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(liveTrainerSelectedId))
        {
            var resolvedTrainerVm = AllGames.FirstOrDefault(g => string.Equals(g.GameId, liveTrainerSelectedId, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(SelectedTrainerGame, resolvedTrainerVm))
            {
                SelectedTrainerGame = resolvedTrainerVm;
                if (SelectedTrainerGame is null)
                {
                    IsTrainerOverviewSelected = true;
                }
            }
        }

        _trainerSidebarSafetyCache.Clear();

        if (ActiveWorkbenchGame is { } activeGame
            && summariesByGameId.TryGetValue(activeGame.GameId, out var activeSummary))
        {
            bool forceRebuild = batch.ForceRefreshAllActiveAchievements;
            ApplyAchievementSummaryToWorkbench(activeGame, activeSummary, forceRebuild);
        }

        ApplyAchievementsCenterAggregates(AllGames, summariesByGameId, recentUnlocks);
        RefreshUnimportedLocalGamesHint();
        OnPropertyChanged(nameof(HasAnyRunningGame));
        OnPropertyChanged(nameof(LibraryRailToolTip));

        await RefreshTrainerHubStateAsync().ConfigureAwait(true);
    }

    private async Task LoadAchievementsForGameAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var summary = await _achievementService.GetGameAchievementsAsync(gameId, cancellationToken).ConfigureAwait(true);
        var gameVm = AllGames.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        gameVm?.UpdateAchievementCounts(summary.UnlockedCount, summary.TotalCount, summary.ChannelStatusText);

        if (ActiveWorkbenchGame is { } activeGame
            && string.Equals(activeGame.GameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyAchievementSummaryToWorkbench(activeGame, summary, forceRebuild: false);
        }
    }

    private void ApplyAchievementSummaryToWorkbench(
        GameListItemViewModel activeGame,
        GameAchievementSummaryDto summary,
        bool forceRebuild)
    {
        bool sameGameAndTitle = string.Equals(_loadedAchievementRowsGameId, summary.GameId, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(_loadedAchievementRowsGameTitle, activeGame.Title, StringComparison.Ordinal);
        bool itemsEqual = sameGameAndTitle
                          && _currentGameRawAchievements.Count == summary.Items.Count
                          && _currentGameRawAchievements.SequenceEqual(summary.Items);
        bool headerEqual = sameGameAndTitle
                           && CurrentAchievementSummary is not null
                           && AreAchievementSummaryHeadersEqual(CurrentAchievementSummary, summary);

        if (!headerEqual || !itemsEqual)
        {
            CurrentAchievementSummary = summary;
        }

        if (forceRebuild || !sameGameAndTitle || !itemsEqual)
        {
            _loadedAchievementRowsGameId = summary.GameId;
            _loadedAchievementRowsGameTitle = activeGame.Title;
            _currentGameRawAchievements = summary.Items;
            RebuildFilteredAchievementRows();
        }
    }

    private static bool AreAchievementSummaryHeadersEqual(GameAchievementSummaryDto a, GameAchievementSummaryDto b) =>
        string.Equals(a.GameId, b.GameId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.SchemaKey, b.SchemaKey, StringComparison.Ordinal)
        && a.SchemaSource == b.SchemaSource
        && a.CaptureMode == b.CaptureMode
        && string.Equals(a.ChannelStatusText, b.ChannelStatusText, StringComparison.Ordinal)
        && a.HasGbeForkActivated == b.HasGbeForkActivated
        && a.CanActivateGbeFork == b.CanActivateGbeFork
        && a.UnlockedCount == b.UnlockedCount
        && a.TotalCount == b.TotalCount;

    private void RebuildFilteredAchievementRows()
    {
        FilteredAchievements.Clear();
        var gameVm = ActiveWorkbenchGame;
        if (gameVm is null || _currentGameRawAchievements.Count == 0)
        {
            return;
        }

        string keyword = AchievementSearchText.Trim();

        foreach (var dto in _currentGameRawAchievements)
        {
            bool tabMatch = SelectedAchievementTab switch
            {
                AchievementFilterTab.Unlocked => dto.IsUnlocked,
                AchievementFilterTab.Locked => !dto.IsUnlocked,
                AchievementFilterTab.Hidden => dto.Definition.IsHidden,
                _ => true,
            };

            if (!tabMatch)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                bool nameHit = dto.Definition.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                               || dto.Definition.ApiName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                string visibleDesc = dto.GetEffectiveDescription(RevealHiddenSpoilers);
                bool descHit = visibleDesc.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                if (!nameHit && !descHit)
                {
                    continue;
                }
            }

            FilteredAchievements.Add(new AchievementRowViewModel(
                gameVm.GameId,
                gameVm.Title,
                dto,
                RevealHiddenSpoilers,
                HandleManualSetAchievementUnlockedAsync,
                HandlePreviewSingleAchievementToastAsync));
        }
    }

    private async Task HandleManualSetAchievementUnlockedAsync(AchievementRowViewModel row, bool unlocked)
    {
        try
        {
            bool changed = await _achievementService.SetAchievementUnlockedAsync(row.GameId, row.ApiName, unlocked).ConfigureAwait(true);
            await LoadAchievementsForGameAsync(row.GameId).ConfigureAwait(true);
            await RefreshAchievementsCenterOverviewAsync().ConfigureAwait(true);
            ShowStatusBanner(
                changed
                    ? (unlocked ? $"已将「{row.DisplayName}」标记为已解锁" : $"已将「{row.DisplayName}」重置为未解锁")
                    : "成就状态未发生变更",
                isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"修改成就状态失败：{ex.Message}", isError: true);
        }
    }

    private async Task HandlePreviewSingleAchievementToastAsync(AchievementRowViewModel row)
    {
        OverlaySettingsDto overlayCfg = await _db.GetOverlaySettingsAsync().ConfigureAwait(true);
        var req = new OverlayToastRequest
        {
            GameId = row.GameId,
            GameTitle = row.GameTitle,
            AchievementApiName = row.ApiName,
            DisplayName = row.DisplayName,
            Description = row.Item.GetEffectiveDescription(revealHiddenSpoilers: true),
            IconPath = row.Item.Definition.IconPath,
            IsRare = row.IsRare,
            GlobalPercent = row.Item.Definition.GlobalPercent,
            IsPlatinumCompletion = false,
            Theme = overlayCfg.Theme,
            Position = overlayCfg.Position,
            SoundPreset = overlayCfg.SoundPreset,
            CustomSoundPath = overlayCfg.CustomSoundPath,
            AudioOutputDeviceId = overlayCfg.AudioOutputDeviceId,
            SoundVolume = overlayCfg.SoundVolume,
            MuteSound = overlayCfg.MuteSound,
            DurationMs = 4200,
        };

        bool sent = await _overlayIpcClient.SendToastAsync(req).ConfigureAwait(true);
        ShowStatusBanner(sent ? $"已在弹窗中预览「{row.DisplayName}」" : "未能连接 OverlayHost 预览弹窗", isError: !sent);
    }

    private async Task RefreshAchievementsCenterOverviewAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = AllGames.ToList();
        var summariesByGameId = new Dictionary<string, GameAchievementSummaryDto>(snapshot.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var gameVm in snapshot)
        {
            var summary = await _achievementService.GetGameAchievementsAsync(gameVm.GameId, cancellationToken).ConfigureAwait(true);
            gameVm.UpdateAchievementCounts(summary.UnlockedCount, summary.TotalCount, summary.ChannelStatusText);
            summariesByGameId[gameVm.GameId] = summary;
        }

        var recent = await _achievementService.GetRecentUnlocksAsync(limit: 25, cancellationToken).ConfigureAwait(true);
        ApplyAchievementsCenterAggregates(snapshot, summariesByGameId, recent);
    }

    private void ApplyAchievementsCenterAggregates(
        IReadOnlyList<GameListItemViewModel> games,
        Dictionary<string, GameAchievementSummaryDto> summariesByGameId,
        IReadOnlyList<AchievementUnlockDto> recentUnlocks)
    {
        int totalUnlocked = 0;
        int totalCount = 0;
        int platinumGames = 0;
        int rareUnlocked = 0;

        var defLookup = new Dictionary<(string GameId, string ApiName), (string GameTitle, AchievementDefinitionDto Def)>();

        foreach (var gameVm in games)
        {
            if (!summariesByGameId.TryGetValue(gameVm.GameId, out var summary))
            {
                continue;
            }

            totalUnlocked += summary.UnlockedCount;
            totalCount += summary.TotalCount;
            if (summary.TotalCount > 0 && summary.UnlockedCount >= summary.TotalCount)
            {
                platinumGames++;
            }

            foreach (var item in summary.Items)
            {
                defLookup[(gameVm.GameId, item.Definition.ApiName.ToUpperInvariant())] = (gameVm.Title, item.Definition);
                if (item.IsUnlocked && item.Definition.IsRare)
                {
                    rareUnlocked++;
                }
            }
        }

        GlobalTotalUnlockedCount = totalUnlocked;
        GlobalTotalAchievementCount = totalCount;
        GlobalPlatinumGamesCount = platinumGames;
        GlobalRareUnlockedCount = rareUnlocked;

        RecentUnlocks.Clear();
        foreach (var unlock in recentUnlocks)
        {
            defLookup.TryGetValue((unlock.GameId, unlock.ApiName.ToUpperInvariant()), out var meta);
            string gameTitle = !string.IsNullOrWhiteSpace(meta.GameTitle)
                ? meta.GameTitle
                : (AllGames.FirstOrDefault(g => g.GameId == unlock.GameId)?.Title ?? "本地游戏");
            RecentUnlocks.Add(new RecentUnlockItemViewModel(unlock.GameId, gameTitle, meta.Def, unlock));
        }
    }

    private async Task HandleSchemaUpdatedAsync(AchievementSchemaUpdatedEvent ev)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        var target = AllGames.FirstOrDefault(g => string.Equals(g.GameId, ev.GameId, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            var refreshedRecord = await _libraryService.GetGameByIdAsync(ev.GameId).ConfigureAwait(true);
            if (refreshedRecord is not null)
            {
                target.UpdateRecord(refreshedRecord);
            }

            target.UpdateAchievementCounts(ev.UnlockedCount, ev.TotalCount, ev.ChannelStatusText);
            if (ReferenceEquals(SelectedGame, target))
            {
                OnPropertyChanged(nameof(CanActivateGbeForkOnSelected));
                OnPropertyChanged(nameof(CanRestoreDllOnSelected));
            }
        }

        if (ActiveWorkbenchGame is not null && string.Equals(ActiveWorkbenchGame.GameId, ev.GameId, StringComparison.OrdinalIgnoreCase))
        {
            await LoadAchievementsForGameAsync(ev.GameId).ConfigureAwait(true);
        }
    }

    private void OnLaunchSessionChanged(object? sender, ActiveGameSessionChangedEvent ev)
    {
        DispatchToUi(() => _ = ApplyLaunchSessionChangedAsync(ev));
    }

    private async Task ApplyLaunchSessionChangedAsync(ActiveGameSessionChangedEvent ev)
    {
        var gameVm = AllGames.FirstOrDefault(g => string.Equals(g.GameId, ev.GameId, StringComparison.OrdinalIgnoreCase));
        if (gameVm is not null)
        {
            if (ev.CurrentState == PlaySessionActivityState.Stopped)
            {
                gameVm.UpdateSession(null);
                var refreshedRecord = await _libraryService.GetGameByIdAsync(ev.GameId).ConfigureAwait(true);
                if (refreshedRecord is not null)
                {
                    gameVm.UpdateRecord(refreshedRecord);
                }

                if (SelectedGame?.GameId == ev.GameId && SelectedGameSubTab == GameDetailSubTab.GameData)
                {
                    await LoadGameDataForSelectedAsync(refreshOnline: false).ConfigureAwait(true);
                }

                if (string.Equals(Trainer.CurrentGameId, ev.GameId, StringComparison.OrdinalIgnoreCase))
                {
                    Trainer.UpdateSelectedGameRunningState(false);
                }

                if (string.Equals(Saves.CurrentGameId, ev.GameId, StringComparison.OrdinalIgnoreCase))
                {
                    Saves.UpdateSelectedGameRunningState(false);
                }
            }
            else
            {
                gameVm.UpdateSession(ev.Session);
                if (string.Equals(Trainer.CurrentGameId, ev.GameId, StringComparison.OrdinalIgnoreCase))
                {
                    Trainer.UpdateSelectedGameRunningState(true);
                }

                if (string.Equals(Saves.CurrentGameId, ev.GameId, StringComparison.OrdinalIgnoreCase))
                {
                    Saves.UpdateSelectedGameRunningState(true);
                }
            }
        }

        SyncCollectionInPlace(AllGames, SortGamesRunningFirst(AllGames));
        RebuildGroupsPreservingSelection();
        OnPropertyChanged(nameof(HasAnyRunningGame));
        OnPropertyChanged(nameof(CanSyncAllCloudSaves));
        OnPropertyChanged(nameof(LibraryRailToolTip));
        OnPropertyChanged(nameof(PrimaryLaunchButtonText));
    }

    private void OnAchievementUnlocked(object? sender, AchievementUnlockedNotification notification)
    {
        DispatchToUi(() => _ = ApplyAchievementUnlockedAsync(notification));
    }

    private async Task ApplyAchievementUnlockedAsync(AchievementUnlockedNotification notification)
    {
        await LoadAchievementsForGameAsync(notification.GameId).ConfigureAwait(true);
        await RefreshAchievementsCenterOverviewAsync().ConfigureAwait(true);
    }

    private void OnUiRefreshTick(object? sender, EventArgs e)
    {
        foreach (var gameVm in AllGames)
        {
            var live = _launchService.GetActiveSession(gameVm.GameId);
            if (live is not null || gameVm.IsRunning)
            {
                gameVm.UpdateSession(live);
                gameVm.NotifyPlaytimeTick();
            }
        }

        if (_antiCheatGuard.CurrentSnapshot.Phase != AntiCheatGuardPhase.IdleSafe)
        {
            UpdateGuardBadge(_antiCheatGuard.CurrentSnapshot);
        }
    }

    private void RebuildGroupsPreservingSelection()
    {
        var sorted = SortGamesRunningFirst(AllGames);

        SyncCollectionInPlace(_allGroup.Items, sorted);
        SyncCollectionInPlace(_gogGroup.Items, sorted.Where(g => g.PlatformSource == PlatformSource.GogOffline).ToList());
        SyncCollectionInPlace(_steamripGroup.Items, sorted.Where(g => g.PlatformSource == PlatformSource.Steamrip).ToList());
        SyncCollectionInPlace(_otherGroup.Items, sorted.Where(g => g.PlatformSource is PlatformSource.SteamLocal or PlatformSource.Standalone).ToList());
    }

    private static List<GameListItemViewModel> SortGamesRunningFirst(IEnumerable<GameListItemViewModel> source) =>
        source
            .OrderByDescending(g => g.IsRunning)
            .ThenBy(g => g.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static void SyncCollectionInPlace<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        if (desired.Count == 0)
        {
            if (target.Count > 0)
            {
                target.Clear();
            }

            return;
        }

        var desiredSet = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            T expected = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], expected))
            {
                continue;
            }

            int existingIndex = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], expected))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, expected);
            }
        }
    }

    private void RefreshUnimportedLocalGamesHint()
    {
        try
        {
            var pendingNames = GameDirectoryScanner.FindUnimportedLocalGameFolderNames(
                _paths.LocalGamesDirectory,
                AllGames.Select(static g => g.Record.InstallDir));

            if (pendingNames.Count > 0)
            {
                string sample = string.Join("、", pendingNames.Take(3));
                HasUnimportedLocalGamesHint = true;
                UnimportedLocalGamesHintText = $"检测到本地游戏目录存在 {pendingNames.Count} 款待入库游戏（{sample}）";
            }
            else
            {
                HasUnimportedLocalGamesHint = false;
                UnimportedLocalGamesHintText = string.Empty;
            }
        }
        catch
        {
            HasUnimportedLocalGamesHint = false;
            UnimportedLocalGamesHintText = string.Empty;
        }
    }

    private string ResolveUserEnteredPath(string rawInput)
    {
        if (Path.IsPathRooted(rawInput))
        {
            return Path.GetFullPath(rawInput);
        }

        if (!string.IsNullOrWhiteSpace(_paths.LocalWorkspaceRoot))
        {
            string candidate = Path.Combine(_paths.LocalWorkspaceRoot, rawInput);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(rawInput);
    }

    private void UpdateGuardBadge(AntiCheatGuardSnapshot snap)
    {
        IsGuardCooldownOrProtected = snap.Phase != AntiCheatGuardPhase.IdleSafe;
        GuardRailToolTip = snap.Phase switch
        {
            AntiCheatGuardPhase.ProtectedRunning => $"安全守卫：受保护网游运行中 · 高敏模块已锁定 ({_paths.ChannelName})",
            AntiCheatGuardPhase.ResidualGuardWatch => "安全守卫：监测反作弊守护残影退出中…",
            AntiCheatGuardPhase.CooldownLocked => $"安全守卫：15s 余温冷却锁定中（剩余 {snap.RemainingCooldownSeconds:0.0}s）",
            _ => $"安全守卫：Tier 0 纯净就绪 · 通道 {_paths.ChannelName}",
        };
        Trainer.UpdateGuardSnapshot(snap);
        foreach (var row in ManagedTrainers)
        {
            row.IsGuardLocked = IsGuardCooldownOrProtected;
        }

        OnPropertyChanged(nameof(TrainerOverviewRuntimeStatusText));
    }

    private void ShowStatusBanner(string message, bool isError)
    {
        _bannerAutoHideCts?.Cancel();
        _bannerAutoHideCts?.Dispose();
        _bannerAutoHideCts = null;

        StatusBannerMessage = message;
        IsStatusBannerError = isError;

        if (!string.IsNullOrWhiteSpace(message))
        {
            var cts = new CancellationTokenSource();
            _bannerAutoHideCts = cts;
            _ = AutoDismissBannerAsync(cts.Token);
        }
    }

    private async Task AutoDismissBannerAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                StatusBannerMessage = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer banner.
        }
    }

    private void NotifyMainPanelViewFlags()
    {
        OnPropertyChanged(nameof(ShowLibraryEmptyState));
        OnPropertyChanged(nameof(ShowLibraryGameWorkbench));
        OnPropertyChanged(nameof(ShowGameAchievementsSubPanel));
        OnPropertyChanged(nameof(ShowGameSavesSubPanel));
        OnPropertyChanged(nameof(ShowGameDataSubPanel));
        OnPropertyChanged(nameof(ShowGameTrainersSubPanel));
        OnPropertyChanged(nameof(ShowAchievementsOverviewPanel));
        OnPropertyChanged(nameof(ShowAchievementsSingleGamePanel));
        OnPropertyChanged(nameof(ShowSavesOverviewPanel));
        OnPropertyChanged(nameof(ShowSavesSingleGamePanel));
        OnPropertyChanged(nameof(ShowTrainersOverviewPanel));
        OnPropertyChanged(nameof(ShowTrainersSingleGamePanel));
        OnPropertyChanged(nameof(ShowSettingsPanel));
    }

    // -------------------------------------------------------------------------
    // Dedicated Saves Sidebar, Global Overview & Bidirectional Sync
    // -------------------------------------------------------------------------

    public async Task RefreshSaveHubStateAsync(CancellationToken cancellationToken = default)
    {
        int generation = Interlocked.Increment(ref _saveHubRefreshGeneration);
        var gamesSnapshot = AllGames.ToList();

        IReadOnlyList<SaveLibraryGameOverviewDto> overviews = _saveTimeMachineService is not null
            ? await _saveTimeMachineService.GetLibraryOverviewAsync(cancellationToken).ConfigureAwait(true)
            : [];

        CasVaultStatsDto vaultStats = _saveTimeMachineService is not null
            ? await _saveTimeMachineService.GetVaultStatsAsync(cancellationToken).ConfigureAwait(true)
            : await _db.GetCasVaultStatsAsync(cancellationToken).ConfigureAwait(true);

        if (generation != Volatile.Read(ref _saveHubRefreshGeneration))
        {
            return;
        }

        var overviewByGameId = overviews.ToDictionary(o => o.GameId, StringComparer.OrdinalIgnoreCase);
        var existingByGameId = _allSaveSidebarGames.ToDictionary(s => s.GameId, StringComparer.OrdinalIgnoreCase);
        var sidebarItems = new List<SaveSidebarGameItemViewModel>(gamesSnapshot.Count);
        var managedItems = new List<ManagedSaveGameOverviewItemViewModel>(gamesSnapshot.Count);

        foreach (GameListItemViewModel gameVm in gamesSnapshot)
        {
            overviewByGameId.TryGetValue(gameVm.GameId, out SaveLibraryGameOverviewDto? ov);
            SaveLibraryGameOverviewDto effectiveOverview = ov ?? new SaveLibraryGameOverviewDto
            {
                GameId = gameVm.GameId,
                GameTitle = gameVm.Title,
                ActiveBranchName = "main",
                IsGameRunning = gameVm.IsRunning,
            };

            bool isSelected = !IsSaveOverviewSelected
                && SelectedSaveGame is not null
                && string.Equals(SelectedSaveGame.GameId, gameVm.GameId, StringComparison.OrdinalIgnoreCase);

            if (existingByGameId.TryGetValue(gameVm.GameId, out SaveSidebarGameItemViewModel? existing)
                && ReferenceEquals(existing.Game, gameVm))
            {
                existing.ApplyOverview(effectiveOverview);
                existing.IsSelected = isSelected;
                sidebarItems.Add(existing);
            }
            else
            {
                sidebarItems.Add(new SaveSidebarGameItemViewModel(gameVm, effectiveOverview, isSelected));
            }

            managedItems.Add(new ManagedSaveGameOverviewItemViewModel(
                effectiveOverview,
                gameVm,
                HandleManagedSaveOpenTree,
                HandleManagedSaveQuickBackupAsync,
                HandleManagedSaveQuickSyncAsync));
        }

        _allSaveSidebarGames.Clear();
        _allSaveSidebarGames.AddRange(SortSaveSidebarGames(sidebarItems));
        RebuildFilteredSaveSidebarGames();

        var orderedManaged = managedItems
            .OrderByDescending(m => m.HasCloudConflict)
            .ThenByDescending(m => m.SnapshotCount > 0)
            .ThenByDescending(m => m.Overview.LastSnapshotAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(m => m.GameTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ManagedSaveGames.Clear();
        foreach (ManagedSaveGameOverviewItemViewModel item in orderedManaged)
        {
            ManagedSaveGames.Add(item);
        }

        TotalSaveSnapshotsCount = overviews.Sum(o => o.SnapshotCount);
        TotalSaveBranchesCount = overviews.Sum(o => o.BranchCount);
        SaveVaultStoredSizeText = UiFormatters.FormatByteSize(vaultStats.TotalCompressedBytes);
        SaveVaultDedupRatioText = vaultStats.TotalSnapshotRawBytes > 0
            ? UiFormatters.FormatDeduplicationPercent(vaultStats.DeduplicationRatio)
            : string.Empty;
        HasAnyCloudConflict = overviews.Any(o => o.HasCloudConflict);
        HasAnySaveWatcherActive = _saveWatcherService is not null
            && _saveWatcherService.GetAllStatuses().Any(s => s.Status is SaveWatcherStatusKind.Watching or SaveWatcherStatusKind.Debouncing or SaveWatcherStatusKind.Capturing);

        OnPropertyChanged(nameof(HasManagedSaveGames));
        OnPropertyChanged(nameof(SaveOverviewCountBadgeText));
        OnPropertyChanged(nameof(SavesRailToolTip));
        OnPropertyChanged(nameof(CanSyncAllCloudSaves));
    }

    private void RebuildFilteredSaveSidebarGames()
    {
        string keyword = SaveSidebarSearchText.Trim();
        var desired = new List<SaveSidebarGameItemViewModel>(_allSaveSidebarGames.Count);
        foreach (SaveSidebarGameItemViewModel item in _allSaveSidebarGames)
        {
            if (string.IsNullOrEmpty(keyword)
                || item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.ActiveBranchName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.StatusPillText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                desired.Add(item);
            }
        }

        SyncCollectionInPlace(SaveSidebarGames, desired);
        OnPropertyChanged(nameof(HasFilteredSaveSidebarGames));
    }

    private void SyncSaveSidebarSelectionFlags()
    {
        string? selectedId = IsSaveOverviewSelected ? null : SelectedSaveGame?.GameId;
        foreach (SaveSidebarGameItemViewModel item in _allSaveSidebarGames)
        {
            item.IsSelected = !string.IsNullOrWhiteSpace(selectedId)
                && string.Equals(item.GameId, selectedId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<SaveSidebarGameItemViewModel> SortSaveSidebarGames(IEnumerable<SaveSidebarGameItemViewModel> source) =>
        source
            .OrderBy(g => g.SortPriority)
            .ThenByDescending(g => g.LastSnapshotAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(g => g.SnapshotCount)
            .ThenBy(g => g.Game.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private void HandleManagedSaveOpenTree(ManagedSaveGameOverviewItemViewModel item)
    {
        GameListItemViewModel? game = item.Game ?? AllGames.FirstOrDefault(
            g => string.Equals(g.GameId, item.GameId, StringComparison.OrdinalIgnoreCase));
        if (game is not null)
        {
            SelectSaveGameItem(game);
        }
    }

    private async Task HandleManagedSaveQuickBackupAsync(ManagedSaveGameOverviewItemViewModel item)
    {
        if (_saveTimeMachineService is null || item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            CreateSaveSnapshotResult res = await _saveTimeMachineService.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = item.GameId,
                TriggerKind = SaveSnapshotTriggerKind.Manual,
            }).ConfigureAwait(true);

            await RefreshSaveHubStateAsync().ConfigureAwait(true);
            await Saves.ReloadCurrentGameIfMatchesAsync(item.GameId).ConfigureAwait(true);
            ShowStatusBanner(res.Message, isError: !res.Succeeded && !res.Skipped);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"快速备份失败：{ex.Message}", isError: true);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task HandleManagedSaveQuickSyncAsync(ManagedSaveGameOverviewItemViewModel item)
    {
        if (_cloudSaveSyncService is null || item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            CloudSyncExecutionReport res = await _cloudSaveSyncService.ExecuteSyncAsync(item.GameId).ConfigureAwait(true);
            await RefreshSaveHubStateAsync().ConfigureAwait(true);
            await Saves.ReloadCurrentGameIfMatchesAsync(item.GameId).ConfigureAwait(true);
            ShowStatusBanner(res.Message, isError: !res.Succeeded);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"同步存档失败：{ex.Message}", isError: true);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task HandleSaveTreeChangedAsync(SaveTreeChangedEvent ev)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        await Saves.ReloadCurrentGameIfMatchesAsync(ev.GameId).ConfigureAwait(true);
        if (ActiveSection == PrimaryNavSection.Saves)
        {
            await RefreshSaveHubStateAsync().ConfigureAwait(true);
        }
    }

    private void OnSaveWatcherStatusChanged(object? sender, SaveWatcherStatusDto status)
    {
        DispatchToUi(() =>
        {
            HasAnySaveWatcherActive = _saveWatcherService is not null
                && _saveWatcherService.GetAllStatuses().Any(s => s.Status is SaveWatcherStatusKind.Watching or SaveWatcherStatusKind.Debouncing or SaveWatcherStatusKind.Capturing);
        });
    }

    // -------------------------------------------------------------------------
    // Dedicated Trainers Sidebar, Global Managed Overview & Bidirectional Sync
    // -------------------------------------------------------------------------

    public async Task RefreshTrainerHubStateAsync(CancellationToken cancellationToken = default)
    {
        int generation = Interlocked.Increment(ref _trainerHubRefreshGeneration);
        var gamesSnapshot = AllGames.ToList();
        var allRecords = new List<TrainerCatalogRecord>();
        var trainersByGame = new Dictionary<string, List<TrainerCatalogRecord>>(StringComparer.OrdinalIgnoreCase);
        var seenTrainerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gameVm in gamesSnapshot)
        {
            IReadOnlyList<TrainerCatalogRecord> perGame = await _trainerCatalogService
                .ListForGameAsync(gameVm.GameId, cancellationToken)
                .ConfigureAwait(true);

            if (generation != Volatile.Read(ref _trainerHubRefreshGeneration))
            {
                return;
            }

            if (perGame.Count > 0)
            {
                var list = new List<TrainerCatalogRecord>(perGame.Count);
                foreach (var rec in perGame)
                {
                    if (seenTrainerIds.Add(rec.TrainerId))
                    {
                        allRecords.Add(rec);
                        list.Add(rec);
                    }
                }

                if (list.Count > 0)
                {
                    trainersByGame[gameVm.GameId] = list;
                }
            }
        }

        var safetyByGameId = new Dictionary<string, (bool IsSafetyLocked, bool IsHybridSinglePlayer)>(StringComparer.OrdinalIgnoreCase);
        foreach (var gameVm in gamesSnapshot)
        {
            var safety = await ResolveSidebarGameSafetyAsync(gameVm, cancellationToken).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _trainerHubRefreshGeneration))
            {
                return;
            }

            safetyByGameId[gameVm.GameId] = safety;
        }

        _allTrainerCatalogRecords = allRecords;

        TrainerSessionHandle? activeSession = _trainerRuntimeService.ActiveSession;
        string? runningGameId = activeSession?.GameId;
        string? runningTrainerId = activeSession?.TrainerId;

        var existingSidebarByGameId = _allTrainerSidebarGames.ToDictionary(s => s.GameId, StringComparer.OrdinalIgnoreCase);
        var sidebarItems = new List<TrainerSidebarGameItemViewModel>(gamesSnapshot.Count);
        foreach (var gameVm in gamesSnapshot)
        {
            var safety = safetyByGameId[gameVm.GameId];

            trainersByGame.TryGetValue(gameVm.GameId, out var boundList);
            int boundCount = boundList?.Count ?? 0;
            bool hasRunning = !string.IsNullOrWhiteSpace(runningGameId)
                              && string.Equals(runningGameId, gameVm.GameId, StringComparison.OrdinalIgnoreCase);

            string summary = boundList is { Count: > 0 }
                ? string.Join(" / ", boundList.Select(t => !string.IsNullOrWhiteSpace(t.DisplayName) ? t.DisplayName! : Path.GetFileName(t.SandboxRelativePath)))
                : string.Empty;

            bool isSelected = !IsTrainerOverviewSelected
                              && SelectedTrainerGame is not null
                              && string.Equals(SelectedTrainerGame.GameId, gameVm.GameId, StringComparison.OrdinalIgnoreCase);

            if (existingSidebarByGameId.TryGetValue(gameVm.GameId, out var existingSidebar)
                && ReferenceEquals(existingSidebar.Game, gameVm))
            {
                existingSidebar.UpdateState(
                    hasRunning,
                    boundCount,
                    safety.IsSafetyLocked,
                    safety.IsHybridSinglePlayer,
                    gameVm.HasVerifiedOfflineBypassBadge,
                    summary);
                existingSidebar.IsSelected = isSelected;
                sidebarItems.Add(existingSidebar);
            }
            else
            {
                sidebarItems.Add(new TrainerSidebarGameItemViewModel(
                    gameVm,
                    hasRunning,
                    boundCount,
                    safety.IsSafetyLocked,
                    safety.IsHybridSinglePlayer,
                    gameVm.HasVerifiedOfflineBypassBadge,
                    summary,
                    isSelected));
            }
        }

        _allTrainerSidebarGames.Clear();
        _allTrainerSidebarGames.AddRange(SortTrainerSidebarGames(sidebarItems));
        RebuildFilteredTrainerSidebarGames();

        var gamesById = gamesSnapshot.ToDictionary(g => g.GameId, StringComparer.OrdinalIgnoreCase);
        var managedItems = new List<ManagedTrainerItemViewModel>(allRecords.Count);
        foreach (var rec in allRecords)
        {
            gamesById.TryGetValue(rec.GameId, out var gameVm);
            bool isRunning = !string.IsNullOrWhiteSpace(runningTrainerId)
                             && string.Equals(runningTrainerId, rec.TrainerId, StringComparison.OrdinalIgnoreCase);
            bool isSafetyBlocked = safetyByGameId.TryGetValue(rec.GameId, out var safety)
                ? safety.IsSafetyLocked
                : (gameVm?.AntiCheatTier is AntiCheatTier.Tier1 or AntiCheatTier.Tier3);

            managedItems.Add(new ManagedTrainerItemViewModel(
                rec,
                gameVm,
                isRunning,
                IsGuardCooldownOrProtected,
                isSafetyBlocked,
                HandleManagedTrainerPrimaryActionAsync,
                HandleManagedTrainerOpenWorkbench,
                HandleManagedTrainerOpenSandboxFolderAsync,
                HandleManagedTrainerVerifyIntegrityAsync,
                HandleManagedTrainerOpenVirusTotal,
                HandleManagedTrainerRemoveAsync));
        }

        var orderedManaged = managedItems
            .OrderByDescending(m => m.IsRunning)
            .ThenByDescending(m => m.Record.LastLaunchedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(m => m.Record.ImportedAtUtc)
            .ThenBy(m => m.GameTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ManagedTrainers.Clear();
        foreach (var item in orderedManaged)
        {
            ManagedTrainers.Add(item);
        }

        TotalManagedTrainersCount = allRecords.Count;
        BoundTrainerGamesCount = trainersByGame.Count;
        OnPropertyChanged(nameof(HasManagedTrainers));
        OnPropertyChanged(nameof(HasEligibleTrainerGame));
        NotifyTrainerRuntimeFlags();
    }

    private async Task LoadTrainerWorkbenchForTrainerSectionGameAsync(GameListItemViewModel game)
    {
        await Trainer.LoadForGameAsync(game).ConfigureAwait(true);

        // Sync authoritative safety classification back into Column 2 sidebar cache immediately.
        _trainerSidebarSafetyCache[game.GameId] = (Trainer.IsProtectedOnlineGameBlocked, Trainer.IsHybridSinglePlayerGame);
        var sidebarItem = _allTrainerSidebarGames.FirstOrDefault(s => string.Equals(s.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
        if (sidebarItem is not null)
        {
            sidebarItem.UpdateState(
                sidebarItem.HasRunningTrainer,
                sidebarItem.BoundTrainerCount,
                Trainer.IsProtectedOnlineGameBlocked,
                Trainer.IsHybridSinglePlayerGame,
                game.HasVerifiedOfflineBypassBadge,
                sidebarItem.BoundTrainerNamesSummary);

            var reordered = SortTrainerSidebarGames(_allTrainerSidebarGames);
            _allTrainerSidebarGames.Clear();
            _allTrainerSidebarGames.AddRange(reordered);
            RebuildFilteredTrainerSidebarGames();
        }
    }

    private async Task<(bool IsSafetyLocked, bool IsHybridSinglePlayer)> ResolveSidebarGameSafetyAsync(
        GameListItemViewModel game,
        CancellationToken cancellationToken)
    {
        if (game.AntiCheatTier == AntiCheatTier.Tier0)
        {
            return (false, false);
        }

        if (game.AntiCheatTier is AntiCheatTier.Tier1 or AntiCheatTier.Tier3)
        {
            return (true, false);
        }

        if (game.Record.VerifiedOfflineBypass)
        {
            return (false, true);
        }

        if (_trainerSidebarSafetyCache.TryGetValue(game.GameId, out var cached))
        {
            return cached;
        }

        FourLayerAntiCheatReport fourLayer = await _trainerSafetyGate
            .InspectGameFourLayerAsync(game.Record, allowOnlineLookup: false, cancellationToken)
            .ConfigureAwait(true);

        AntiCheatTier effectiveTier = fourLayer.EffectiveTier > game.AntiCheatTier
            ? fourLayer.EffectiveTier
            : game.AntiCheatTier;

        bool isHybrid = effectiveTier == AntiCheatTier.Tier2
                        && fourLayer.HasSinglePlayerCategory == true
                        && !fourLayer.IsMultiplayerOnly;
        bool isLocked = !isHybrid;

        _trainerSidebarSafetyCache[game.GameId] = (isLocked, isHybrid);
        return (isLocked, isHybrid);
    }

    private void RebuildFilteredTrainerSidebarGames()
    {
        string keyword = TrainerSidebarSearchText.Trim();
        var desired = new List<TrainerSidebarGameItemViewModel>(_allTrainerSidebarGames.Count);

        foreach (var item in _allTrainerSidebarGames)
        {
            if (string.IsNullOrEmpty(keyword)
                || item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.Game.PlatformShortLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.BoundTrainerNamesSummary.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.StatusPillText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                desired.Add(item);
            }
        }

        SyncCollectionInPlace(TrainerSidebarGames, desired);
        OnPropertyChanged(nameof(HasFilteredTrainerSidebarGames));
    }

    private void SyncTrainerSidebarSelectionFlags()
    {
        string? selectedId = IsTrainerOverviewSelected ? null : SelectedTrainerGame?.GameId;
        foreach (var item in _allTrainerSidebarGames)
        {
            item.IsSelected = !string.IsNullOrWhiteSpace(selectedId)
                              && string.Equals(item.GameId, selectedId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<TrainerSidebarGameItemViewModel> SortTrainerSidebarGames(IEnumerable<TrainerSidebarGameItemViewModel> source) =>
        source
            .OrderBy(g => g.SortPriority)
            .ThenByDescending(g => g.Game.IsRunning)
            .ThenByDescending(g => g.BoundTrainerCount)
            .ThenBy(g => g.Game.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private void OnTrainerRuntimeStateChanged(object? sender, TrainerRuntimeStateChanged ev)
    {
        void Apply()
        {
            TrainerSessionHandle? active = _trainerRuntimeService.ActiveSession;
            string? runningGameId = active?.GameId;
            string? runningTrainerId = active?.TrainerId;

            foreach (var row in ManagedTrainers)
            {
                row.IsRunning = !string.IsNullOrWhiteSpace(runningTrainerId)
                                && string.Equals(row.TrainerId, runningTrainerId, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var sidebarItem in _allTrainerSidebarGames)
            {
                bool running = !string.IsNullOrWhiteSpace(runningGameId)
                               && string.Equals(sidebarItem.GameId, runningGameId, StringComparison.OrdinalIgnoreCase);
                if (sidebarItem.HasRunningTrainer != running)
                {
                    sidebarItem.UpdateState(
                        running,
                        sidebarItem.BoundTrainerCount,
                        sidebarItem.IsSafetyLocked,
                        sidebarItem.IsHybridSinglePlayer,
                        sidebarItem.IsVerifiedOfflineBypass,
                        sidebarItem.BoundTrainerNamesSummary);
                }
            }

            var reordered = SortTrainerSidebarGames(_allTrainerSidebarGames);
            _allTrainerSidebarGames.Clear();
            _allTrainerSidebarGames.AddRange(reordered);
            RebuildFilteredTrainerSidebarGames();

            NotifyTrainerRuntimeFlags();

            if (ev.CurrentState == TrainerRuntimeState.EmergencyStopped && !string.IsNullOrWhiteSpace(ev.Message))
            {
                ShowStatusBanner(ev.Message!, isError: true);
            }

            _ = RefreshTrainerHubStateAsync();
        }

        DispatchToUi(Apply);
    }

    private void NotifyTrainerRuntimeFlags()
    {
        OnPropertyChanged(nameof(HasAnyRunningTrainer));
        OnPropertyChanged(nameof(TrainersRailToolTip));
        OnPropertyChanged(nameof(TrainerOverviewCountBadgeText));
        OnPropertyChanged(nameof(TrainerOverviewRuntimeStatusText));
        OnPropertyChanged(nameof(ActiveRunningTrainerGameTitle));
        OnPropertyChanged(nameof(ActiveRunningTrainerDisplayName));
        OnPropertyChanged(nameof(ActiveRunningTrainerElevationText));
    }

    private async Task HandleManagedTrainerPrimaryActionAsync(ManagedTrainerItemViewModel item)
    {
        if (item.IsRunning)
        {
            await StopActiveRunningTrainerAsync().ConfigureAwait(true);
            return;
        }

        if (IsGuardCooldownOrProtected)
        {
            ShowStatusBanner("反作弊安全守卫当前处于锁定或 15s 余温冷却中，已禁止启动修改器", isError: true);
            return;
        }

        if (item.IsSafetyBlocked)
        {
            ShowStatusBanner("该游戏处于反作弊安全锁定状态，禁止启动修改器", isError: true);
            return;
        }

        if (HasAnyRunningTrainer)
        {
            ShowStatusBanner("当前已有正在运行的修改器会话，请先停止后再启动其他修改器", isError: true);
            return;
        }

        item.IsBusy = true;
        try
        {
            TrainerSettingsDto settings = await _db.GetTrainerSettingsAsync().ConfigureAwait(true);
            var options = new TrainerLaunchOptions
            {
                HotkeyHoldMs = settings.HotkeyHoldMs,
                HideOriginalWindow = settings.HideOriginalWindowOnLaunch,
                StopWithGameExit = settings.StopWithGameExit,
                CreatePreTrainerSnapshot = settings.CreatePreTrainerSnapshot,
                ScanCodeMode = settings.DefaultScanCodeMode,
                AudioPolicyOverride = settings.ToAudioPolicy(),
                StrictVanguardMode = settings.StrictVanguardMode,
            };

            TrainerLaunchOutcome outcome = await _trainerRuntimeService.LaunchAsync(item.TrainerId, options).ConfigureAwait(true);
            if (!outcome.Succeeded)
            {
                if (outcome.AllowanceDecision?.Outcome == TrainerAllowanceOutcome.RequiresOfflineConfirmation && item.Game is not null)
                {
                    SelectTrainerGameItem(item.Game);
                }

                ShowStatusBanner(
                    outcome.Message ?? "启动修改器未成功",
                    isError: outcome.FailureKind != TrainerLaunchFailureKind.UacCancelledByUser);
                return;
            }

            Trainer.SyncOriginalWindowVisibility(!options.HideOriginalWindow);
            if (string.Equals(Trainer.CurrentGameId, item.GameId, StringComparison.OrdinalIgnoreCase))
            {
                await Trainer.RefreshSnapshotsForCurrentGameAsync().ConfigureAwait(true);
            }

            ShowStatusBanner(
                $"已启动修改器「{item.TrainerDisplayName}」（{outcome.Session?.ElevationLevel} · 已启用零注入静音与防残留作业对象）",
                isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"启动修改器失败：{ex.Message}", isError: true);
        }
        finally
        {
            item.IsBusy = false;
            await RefreshTrainerHubStateAsync().ConfigureAwait(true);
        }
    }

    private void HandleManagedTrainerOpenWorkbench(ManagedTrainerItemViewModel item)
    {
        var game = item.Game ?? AllGames.FirstOrDefault(g => string.Equals(g.GameId, item.GameId, StringComparison.OrdinalIgnoreCase));
        if (game is not null)
        {
            SelectTrainerGameItem(game);
        }
    }

    private async Task HandleManagedTrainerOpenSandboxFolderAsync(ManagedTrainerItemViewModel item)
    {
        try
        {
            TrainerSettingsDto settings = await _db.GetTrainerSettingsAsync().ConfigureAwait(true);
            string sandboxRoot = _paths.ResolveTrainerSandboxDirectory(settings.CustomSandboxRoot, requirePureAscii: false);
            string targetDir = sandboxRoot;
            string? relDir = Path.GetDirectoryName(item.Record.SandboxRelativePath);
            if (!string.IsNullOrWhiteSpace(relDir))
            {
                string combined = Path.GetFullPath(Path.Combine(sandboxRoot, relDir));
                if (Directory.Exists(combined))
                {
                    targetDir = combined;
                }
            }

            Directory.CreateDirectory(targetDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{targetDir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"打开沙箱目录失败：{ex.Message}", isError: true);
        }
    }

    private async Task HandleManagedTrainerVerifyIntegrityAsync(ManagedTrainerItemViewModel item)
    {
        try
        {
            TrainerIntegrityReport report = await _trainerCatalogService.VerifyAsync(item.TrainerId).ConfigureAwait(true);
            if (report.IsValid)
            {
                string shortHash = report.ActualSha256 is { Length: > 16 } h ? $"{h[..16]}…" : (report.ActualSha256 ?? item.ShortSha256);
                string msg = report.GameFilesChangedSinceBinding && !string.IsNullOrWhiteSpace(report.GameUpdateNotice)
                    ? $"SHA-256 校验通过（{shortHash}）· {report.GameUpdateNotice}"
                    : $"SHA-256 完整性校验通过：{shortHash}";
                ShowStatusBanner(msg, isError: false);
            }
            else
            {
                ShowStatusBanner(report.FailureMessage ?? "SHA-256 校验未通过，修改器文件可能缺失、变更或被杀软隔离", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"校验 SHA-256 失败：{ex.Message}", isError: true);
        }
    }

    private void HandleManagedTrainerOpenVirusTotal(ManagedTrainerItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.FullSha256))
        {
            return;
        }

        // Strict privacy rule: ONLY open SHA-256 lookup URL on explicit user click; NEVER upload any file.
        string url = UiFormatters.BuildVirusTotalFileUrl(item.FullSha256);
        try
        {
            _openExternalUrl(url);
            ShowStatusBanner("已在默认浏览器打开 VirusTotal 哈希查询页面（未上传任何本地文件）", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"无法打开外部链接：{ex.Message}", isError: true);
        }
    }

    private async Task HandleManagedTrainerRemoveAsync(ManagedTrainerItemViewModel item)
    {
        try
        {
            if (item.IsRunning)
            {
                await StopActiveRunningTrainerAsync().ConfigureAwait(true);
            }

            await _trainerCatalogService.RemoveAsync(item.TrainerId).ConfigureAwait(true);
            await Trainer.ReloadCurrentGameIfMatchesAsync(item.GameId).ConfigureAwait(true);
            await RefreshTrainerHubStateAsync().ConfigureAwait(true);
            ShowStatusBanner($"已从沙箱移除修改器「{item.TrainerDisplayName}」", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"移除修改器失败：{ex.Message}", isError: true);
        }
    }

    private static void OpenUrlWithShell(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    private static bool IsTrainerFileExtension(string? ext) =>
        !string.IsNullOrWhiteSpace(ext)
        && (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase));

    private Dispatcher? ResolveUiDispatcher() => _uiDispatcher ?? Application.Current?.Dispatcher;

    private void DispatchToUi(Action action)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        if (ResolveUiDispatcher() is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _ = dispatcher.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _isDisposed) == 0)
                    {
                        action();
                    }
                });
            }
        }
        else
        {
            action();
        }
    }

    private Task DispatchToUiOrInlineAsync(Func<Task> asyncAction)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return Task.CompletedTask;
        }

        if (ResolveUiDispatcher() is { } dispatcher)
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _isDisposed) == 0)
                {
                    _ = asyncAction();
                }
            });
            return Task.CompletedTask;
        }

        return asyncAction();
    }

    private readonly record struct LibraryReloadRequest(
        string? ExplicitSelectGameId,
        int? ExplicitSelectionVersion,
        string? FallbackSelectGameId,
        int? FallbackSelectionVersion,
        bool ForceRefreshAllActiveAchievements,
        string? DirtyAchievementGameId)
    {
        public static LibraryReloadRequest PreserveCurrent(
            bool forceRefreshAllActiveAchievements = false,
            string? dirtyAchievementGameId = null) =>
            new(
                ExplicitSelectGameId: null,
                ExplicitSelectionVersion: null,
                FallbackSelectGameId: null,
                FallbackSelectionVersion: null,
                ForceRefreshAllActiveAchievements: forceRefreshAllActiveAchievements,
                DirtyAchievementGameId: dirtyAchievementGameId);

        public static LibraryReloadRequest PreserveOrFallback(
            string? fallbackSelectGameId,
            int fallbackSelectionVersion,
            string? dirtyAchievementGameId = null) =>
            new(
                ExplicitSelectGameId: null,
                ExplicitSelectionVersion: null,
                FallbackSelectGameId: fallbackSelectGameId,
                FallbackSelectionVersion: fallbackSelectionVersion,
                ForceRefreshAllActiveAchievements: false,
                DirtyAchievementGameId: dirtyAchievementGameId);

        public static LibraryReloadRequest ExplicitIfUnchanged(
            string explicitSelectGameId,
            int explicitSelectionVersion,
            string? dirtyAchievementGameId = null) =>
            new(
                ExplicitSelectGameId: explicitSelectGameId,
                ExplicitSelectionVersion: explicitSelectionVersion,
                FallbackSelectGameId: null,
                FallbackSelectionVersion: null,
                ForceRefreshAllActiveAchievements: false,
                DirtyAchievementGameId: dirtyAchievementGameId);
    }

    private sealed class PendingLibraryReloadBatch
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ExplicitSelectGameId { get; private set; }
        public int? ExplicitSelectionVersion { get; private set; }
        public string? FallbackSelectGameId { get; private set; }
        public int? FallbackSelectionVersion { get; private set; }
        public bool ForceRefreshAllActiveAchievements { get; private set; }
        public HashSet<string>? DirtyAchievementGameIds { get; private set; }

        public void Merge(LibraryReloadRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ExplicitSelectGameId))
            {
                ExplicitSelectGameId = request.ExplicitSelectGameId;
                ExplicitSelectionVersion = request.ExplicitSelectionVersion;
            }

            if (string.IsNullOrWhiteSpace(FallbackSelectGameId)
                && !string.IsNullOrWhiteSpace(request.FallbackSelectGameId))
            {
                FallbackSelectGameId = request.FallbackSelectGameId;
                FallbackSelectionVersion = request.FallbackSelectionVersion;
            }

            if (request.ForceRefreshAllActiveAchievements)
            {
                ForceRefreshAllActiveAchievements = true;
            }

            if (!string.IsNullOrWhiteSpace(request.DirtyAchievementGameId))
            {
                DirtyAchievementGameIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DirtyAchievementGameIds.Add(request.DirtyAchievementGameId);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _isDisposed, 1);
        _uiRefreshTimer.Stop();
        _launchService.SessionChanged -= OnLaunchSessionChanged;
        _achievementService.AchievementUnlocked -= OnAchievementUnlocked;
        _trainerRuntimeService.StateChanged -= OnTrainerRuntimeStateChanged;
        if (_saveWatcherService is not null)
        {
            _saveWatcherService.WatcherStatusChanged -= OnSaveWatcherStatusChanged;
        }

        Trainer.Dispose();
        Saves.Dispose();
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }

        _subscriptions.Clear();
        lock (_reloadSync)
        {
            if (_pendingReloadBatch is not null)
            {
                _pendingReloadBatch.Completion.TrySetResult();
                _pendingReloadBatch = null;
            }
        }

        _bannerAutoHideCts?.Cancel();
        _bannerAutoHideCts?.Dispose();
    }
}
