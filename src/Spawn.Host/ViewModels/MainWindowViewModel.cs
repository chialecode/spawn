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

public sealed record ShellConfirmPrompt(
    string Title,
    string Message,
    string ConfirmLabel = "确认",
    string CancelLabel = "取消",
    bool IsDanger = false);

public sealed record ShellSteamBlockedPrompt(
    string Title,
    string Message,
    bool CanOpenSteamSettings = false);

public sealed record GamePropertiesEditRequest(
    string GameId,
    string Title,
    string InstallDir,
    string ExecutableRelPath,
    IReadOnlyList<string> CandidateExecutables,
    string LaunchArguments,
    int? SteamAppId,
    string? GogGameId,
    string? CoverImagePath,
    string? IconPath,
    PlatformSource PlatformSource = PlatformSource.Standalone,
    AntiCheatTier AntiCheatTier = AntiCheatTier.Tier0,
    AchievementCaptureMode AchievementCaptureMode = AchievementCaptureMode.None,
    GameSteamLaunchSettings? SteamLaunchSettings = null,
    Func<GameRecord, GameSteamLaunchSettings, CancellationToken, Task<SteamLaunchPreparationResult>>? PreviewSteamLaunchAsync = null);

public sealed record GamePropertiesEditResult(
    string Title,
    string ExecutableRelPath,
    string LaunchArguments,
    int? SteamAppId,
    string? GogGameId,
    string? CoverImagePath,
    string? IconPath,
    GameLaunchMode? LaunchMode = null,
    bool? UseSteamAppIdAsShortcutName = null);

public enum LibrarySortMode
{
    Name = 0,
    LastPlayed = 1,
    Playtime = 2,
}

public enum LibrarySourceFilter
{
    All = 0,
    GogOffline = 1,
    Steamrip = 2,
    Other = 3,
}

public enum AchievementSidebarSortMode
{
    CompletionDesc = 0,
    UnlockedDesc = 1,
    Name = 2,
}

public enum AchievementSortMode
{
    Default = 0,
    UnlockedFirst = 1,
    UnlockTimeDesc = 2,
    RarityAsc = 3,
    NameAsc = 4,
}

public enum GameLaunchButtonPhase
{
    Idle = 0,
    Launching = 1,
    WaitingForWindow = 2,
    Running = 3,
    Stopping = 4,
    GuardCooldownLocked = 5,
}

public sealed partial class AchievementSidebarItemViewModel : ObservableObject
{
    public GameListItemViewModel Game { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string GameId => Game.GameId;
    public string Title => Game.Title;
    public string InitialsText => Game.InitialsText;
    public System.Windows.Media.Imaging.BitmapImage? SmallIcon => Game.SmallIcon;
    public string AchievementRatioText => Game.AchievementRatioText;
    public double CompletionPercent => Game.CompletionPercent;
    public int UnlockedAchievements => Game.UnlockedAchievements;
    public int TotalAchievements => Game.TotalAchievements;
    public bool IsRunning => Game.IsRunning;

    public AchievementSidebarItemViewModel(GameListItemViewModel game, bool isSelected)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        _isSelected = isSelected;
        Game.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(GameListItemViewModel.Title):
                    OnPropertyChanged(nameof(Title));
                    OnPropertyChanged(nameof(InitialsText));
                    break;
                case nameof(GameListItemViewModel.SmallIcon):
                    OnPropertyChanged(nameof(SmallIcon));
                    break;
                case nameof(GameListItemViewModel.AchievementRatioText):
                    OnPropertyChanged(nameof(AchievementRatioText));
                    break;
                case nameof(GameListItemViewModel.CompletionPercent):
                    OnPropertyChanged(nameof(CompletionPercent));
                    break;
                case nameof(GameListItemViewModel.UnlockedAchievements):
                    OnPropertyChanged(nameof(UnlockedAchievements));
                    break;
                case nameof(GameListItemViewModel.TotalAchievements):
                    OnPropertyChanged(nameof(TotalAchievements));
                    break;
                case nameof(GameListItemViewModel.IsRunning):
                    OnPropertyChanged(nameof(IsRunning));
                    break;
            }
        };
    }
}

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
    private readonly ISteamClientLocator _steamClientLocator;
    private readonly ISteamShortcutService _steamShortcutService;
    private readonly Action<string> _openExternalUrl;
    private readonly Action<string> _copyToClipboard;
    private readonly Dispatcher? _uiDispatcher;
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _reloadSync = new();
    private readonly int _achievementSearchDebounceMs;
    private readonly HashSet<string> _launchingGameIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _steamSyncingGameIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shownSteamWarningGameIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stoppingGameIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AchievementRowViewModel> _achievementRowCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AchievementSidebarItemViewModel> _allAchievementSidebarGames = [];

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
    private CancellationTokenSource? _achievementSearchDebounceCts;

    /// <summary>
    /// Host window confirmation dialog delegate for destructive/high-risk operations.
    /// When unset in headless tests, defaults to confirming so legacy tests remain non-blocking.
    /// </summary>
    public Func<ShellConfirmPrompt, bool>? ConfirmPromptHandler { get; set; }

    /// <summary>
    /// Host window native Game Properties editor dialog delegate.
    /// </summary>
    public Func<GamePropertiesEditRequest, GamePropertiesEditResult?>? EditGamePropertiesHandler { get; set; }

    /// <summary>
    /// Host window themed notice dialog shown when a Steam launch is blocked (returns <see langword="true"/>
    /// if the user chooses to jump directly to Steam settings).
    /// </summary>
    public Func<ShellSteamBlockedPrompt, bool>? SteamBlockedPromptHandler { get; set; }

    public ISteamClientLocator SteamClientLocator => _steamClientLocator;
    public ISteamShortcutService SteamShortcutService => _steamShortcutService;

    public SettingsViewModel Settings { get; }
    public TrainerWorkbenchViewModel Trainer { get; }
    public SaveWorkbenchViewModel Saves { get; }

    public ObservableCollection<GameListItemViewModel> AllGames { get; } = [];
    public ObservableCollection<GameSourceGroupViewModel> LibraryGroups { get; } = [];
    public ObservableCollection<GameSourceGroupViewModel> VisibleLibraryGroups { get; } = [];
    public ObservableCollection<AchievementSidebarItemViewModel> AchievementSidebarGames { get; } = [];
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

    private readonly GameSourceGroupViewModel _visibleLastPlayedGroup = new("sort-last-played", "最近游玩", initiallyExpanded: true);
    private readonly GameSourceGroupViewModel _visiblePlaytimeGroup = new("sort-playtime", "游玩时长", initiallyExpanded: true);
    private readonly GameSourceGroupViewModel _visibleGogGroup = new("gog", "GOG", initiallyExpanded: true);
    private readonly GameSourceGroupViewModel _visibleSteamripGroup = new("steamrip", "Steamrip", initiallyExpanded: true);
    private readonly GameSourceGroupViewModel _visibleOtherGroup = new("other", "Steam 本地 / 独立", initiallyExpanded: true);

    public IReadOnlyList<NamedOption<LibrarySortMode>> LibrarySortOptions { get; } =
    [
        new(LibrarySortMode.Name, "按名称"),
        new(LibrarySortMode.LastPlayed, "最近游玩"),
        new(LibrarySortMode.Playtime, "游玩时长"),
    ];

    public IReadOnlyList<NamedOption<AchievementSidebarSortMode>> AchievementSidebarSortOptions { get; } =
    [
        new(AchievementSidebarSortMode.CompletionDesc, "完成度优先"),
        new(AchievementSidebarSortMode.UnlockedDesc, "已解锁数"),
        new(AchievementSidebarSortMode.Name, "按名称"),
    ];

    public IReadOnlyList<NamedOption<AchievementSortMode>> AchievementSortOptions { get; } =
    [
        new(AchievementSortMode.Default, "默认顺序"),
        new(AchievementSortMode.UnlockedFirst, "已解锁优先"),
        new(AchievementSortMode.UnlockTimeDesc, "最近解锁"),
        new(AchievementSortMode.RarityAsc, "稀有度优先"),
        new(AchievementSortMode.NameAsc, "名称 A-Z"),
    ];

    [ObservableProperty]
    private PrimaryNavSection _activeSection = PrimaryNavSection.Library;

    [ObservableProperty]
    private bool _isNarrowWindow;

    [ObservableProperty]
    private bool _isNarrowSidebarOpen;

    [ObservableProperty]
    private string _librarySidebarSearchText = string.Empty;

    [ObservableProperty]
    private LibrarySortMode _selectedLibrarySortMode = LibrarySortMode.Name;

    [ObservableProperty]
    private LibrarySourceFilter _selectedLibrarySourceFilter = LibrarySourceFilter.All;

    [ObservableProperty]
    private GameListItemViewModel? _selectedGame;

    [ObservableProperty]
    private GameDetailSubTab _selectedGameSubTab = GameDetailSubTab.Achievements;

    [ObservableProperty]
    private bool _isAchievementsOverviewSelected = true;

    [ObservableProperty]
    private GameListItemViewModel? _selectedAchievementsGame;

    [ObservableProperty]
    private string _achievementSidebarSearchText = string.Empty;

    [ObservableProperty]
    private AchievementSidebarSortMode _selectedAchievementSidebarSortMode = AchievementSidebarSortMode.CompletionDesc;

    [ObservableProperty]
    private bool _hideZeroAchievementGames;

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
    private AchievementSortMode _selectedAchievementSortMode = AchievementSortMode.Default;

    [ObservableProperty]
    private bool _revealHiddenSpoilers;

    [ObservableProperty]
    private string _achievementSearchText = string.Empty;

    // Selected game's Game Data Management states (safe default: do NOT wipe SPAWN achievements/playtime unless opted in)
    [ObservableProperty]
    private bool _resetSpawnStateOnClean;

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

    // Anti-Cheat Guard Rail Badge & Popover
    [ObservableProperty]
    private AntiCheatGuardPhase _guardPhase = AntiCheatGuardPhase.IdleSafe;

    [ObservableProperty]
    private double _guardCooldownRemainingSeconds;

    [ObservableProperty]
    private string _guardRailToolTip = "安全守卫：纯净就绪";

    [ObservableProperty]
    private string _guardStatusHeadline = "安全守卫就绪";

    [ObservableProperty]
    private string _guardStatusDetail = "当前未检测到受反作弊保护的网游或残留保护进程，主程序与高敏组件物理隔离。";

    [ObservableProperty]
    private bool _isGuardCooldownOrProtected;

    public string GuardChannelText => $"运行通道：{_paths.ChannelName}（{_paths.Identity}）";
    public bool IsGuardIdle => GuardPhase == AntiCheatGuardPhase.IdleSafe;
    public bool IsGuardWarning => GuardPhase is AntiCheatGuardPhase.ResidualGuardWatch or AntiCheatGuardPhase.CooldownLocked;
    public bool IsGuardProtectedRunning => GuardPhase == AntiCheatGuardPhase.ProtectedRunning;

    public bool IsSidebarVisible => !IsNarrowWindow || IsNarrowSidebarOpen;
    public bool IsDockedSidebarVisible => !IsNarrowWindow;
    public bool IsDrawerSidebarOpen => IsNarrowWindow && IsNarrowSidebarOpen;

    public bool IsAnyBusyWorking =>
        IsBusyWorking
        || IsInspectingGameData
        || IsSyncingAllCloudSaves
        || Trainer.IsBusy
        || Settings.IsDefenderWorking
        || Settings.Update.IsBusy;

    public bool HasVisibleLibraryGames => VisibleLibraryGroups.Any(g => g.Count > 0);
    public bool HasLibrarySearchNoMatches => AllGames.Count > 0 && !HasVisibleLibraryGames;
    public bool HasFilteredAchievementSidebarGames => AchievementSidebarGames.Count > 0;
    public bool HasGameDataGroups => GameDataGroups.Count > 0;
    public bool ShowGameDataEmptyState => !IsInspectingGameData && GameDataGroups.Count == 0;

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

    public bool IsSyncingSteamShortcutForSelected =>
        SelectedGame is not null && _steamSyncingGameIds.Contains(SelectedGame.GameId);

    public bool IsWaitingForSteamLaunch =>
        SelectedGame is { ActiveSession.State: PlaySessionActivityState.ShellBreakingWatch, ActiveSession.LaunchedViaSteam: true };

    public GameLaunchButtonPhase PrimaryLaunchButtonPhase
    {
        get
        {
            var game = SelectedGame;
            if (game is null)
            {
                return GameLaunchButtonPhase.Idle;
            }

            if (_stoppingGameIds.Contains(game.GameId)
                || game.ActiveSession?.State == PlaySessionActivityState.Stopping)
            {
                return GameLaunchButtonPhase.Stopping;
            }

            if (_launchingGameIds.Contains(game.GameId)
                || _steamSyncingGameIds.Contains(game.GameId)
                || game.ActiveSession?.State == PlaySessionActivityState.Launching)
            {
                return GameLaunchButtonPhase.Launching;
            }

            if (game.ActiveSession?.State == PlaySessionActivityState.ShellBreakingWatch)
            {
                return GameLaunchButtonPhase.WaitingForWindow;
            }

            if (game.IsRunning)
            {
                return GameLaunchButtonPhase.Running;
            }

            if (GuardPhase == AntiCheatGuardPhase.CooldownLocked)
            {
                return GameLaunchButtonPhase.GuardCooldownLocked;
            }

            return GameLaunchButtonPhase.Idle;
        }
    }

    public string PrimaryLaunchButtonTag => PrimaryLaunchButtonPhase switch
    {
        GameLaunchButtonPhase.Running => "Running",
        GameLaunchButtonPhase.WaitingForWindow => IsWaitingForSteamLaunch ? "WaitingSteam" : "Launching",
        GameLaunchButtonPhase.Launching => "Launching",
        GameLaunchButtonPhase.Stopping => "Stopping",
        GameLaunchButtonPhase.GuardCooldownLocked => "CooldownLocked",
        _ => "Idle",
    };

    public bool IsPrimaryLaunchButtonEnabled => SelectedGame is not null
        && (PrimaryLaunchButtonPhase is GameLaunchButtonPhase.Idle or GameLaunchButtonPhase.Running
            || IsWaitingForSteamLaunch);

    public string PrimaryLaunchButtonText => PrimaryLaunchButtonPhase switch
    {
        GameLaunchButtonPhase.Launching => IsSyncingSteamShortcutForSelected ? "同步 Steam 中…" : "启动中…",
        GameLaunchButtonPhase.WaitingForWindow => IsWaitingForSteamLaunch ? "等待 Steam 启动…" : "等待窗口…",
        GameLaunchButtonPhase.Running => "运行中",
        GameLaunchButtonPhase.Stopping => "正在停止…",
        GameLaunchButtonPhase.GuardCooldownLocked => string.Create(
            CultureInfo.InvariantCulture,
            $"冷却锁定 ({Math.Max(1, (int)Math.Ceiling(GuardCooldownRemainingSeconds))}s)"),
        _ => "启动游戏",
    };

    public string PrimaryLaunchButtonToolTip => PrimaryLaunchButtonPhase switch
    {
        GameLaunchButtonPhase.Launching => IsSyncingSteamShortcutForSelected
            ? "正在优雅重启 Steam 并同步非 Steam 快捷方式，请稍候…"
            : "正在拉起游戏主程序，请稍候…",
        GameLaunchButtonPhase.WaitingForWindow => IsWaitingForSteamLaunch
            ? "已向 Steam 发送启动请求，正在等待游戏进程拉起；点击可取消等待"
            : "正在等待启动器交接游戏主窗口…",
        GameLaunchButtonPhase.Running => SelectedGame?.ActiveSession?.LaunchedViaSteam == true
            ? "游戏正在运行（通过 Steam 启动）；点击将二次确认后停止游戏"
            : "游戏正在运行；点击将二次确认后停止游戏",
        GameLaunchButtonPhase.Stopping => "正在安全结束游戏进程…",
        GameLaunchButtonPhase.GuardCooldownLocked => string.Create(
            CultureInfo.InvariantCulture,
            $"反作弊余温冷却中（剩余 {GuardCooldownRemainingSeconds:0.0} 秒），冷却结束后即可启动游戏"),
        _ => SelectedGame?.IsSteamLaunchConfigured == true
            ? "默认通过 Steam 启动当前游戏（启用 Steam Input 手柄映射），并自动追踪游玩时长与成就进度"
            : "启动当前游戏并自动追踪游玩时长与成就进度",
    };

    public bool ShowPrimaryLaunchPlayGlyph => PrimaryLaunchButtonPhase == GameLaunchButtonPhase.Idle;

    public string AlternateLaunchMenuItemText =>
        SelectedGame?.IsSteamLaunchConfigured == true ? "直接启动" : "通过 Steam 启动";

    public bool CanExecuteAlternateLaunchForSelected
    {
        get
        {
            var game = SelectedGame;
            if (game is null
                || game.IsRunning
                || _launchingGameIds.Contains(game.GameId)
                || _steamSyncingGameIds.Contains(game.GameId)
                || _stoppingGameIds.Contains(game.GameId)
                || GuardPhase == AntiCheatGuardPhase.CooldownLocked)
            {
                return false;
            }

            return game.IsSteamLaunchConfigured || !game.IsSteamLaunchBlockedByAntiCheat;
        }
    }

    public string AlternateLaunchMenuItemToolTip
    {
        get
        {
            var game = SelectedGame;
            if (game is null)
            {
                return string.Empty;
            }

            if (game.IsRunning)
            {
                return "游戏正在运行中";
            }

            if (GuardPhase == AntiCheatGuardPhase.CooldownLocked)
            {
                return "反作弊余温冷却锁定中，请等待冷却结束";
            }

            if (game.IsSteamLaunchConfigured)
            {
                return "本次临时跳过 Steam，直接由 SPAWN 启动该游戏主程序";
            }

            if (game.IsSteamLaunchBlockedByAntiCheat)
            {
                return $"非 Steam 来源的 {game.Record.AntiCheatTier} 反作弊游戏禁止通过非 Steam 快捷方式启动，以防止 Steam 覆盖层与内核反作弊冲突";
            }

            return "本次临时通过 Steam 启动该游戏（启用 Steam Input 手柄映射），不修改默认启动方式";
        }
    }

    public bool CanActivateGbeForkOnSelected =>
        !CanRestoreDllOnSelected
        && SelectedGame is { Record.AntiCheatTier: AntiCheatTier.Tier0 } selected
        && (selected.Record.PlatformSource == PlatformSource.Steamrip
            || selected.Record.AchievementCaptureMode == AchievementCaptureMode.PendingGbeForkActivation
            || CurrentAchievementSummary is { CanActivateGbeFork: true });

    public bool CanRestoreDllOnSelected =>
        CurrentAchievementSummary is { HasGbeForkActivated: true }
        || SelectedGame?.Record.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated;

    public bool CanActivateGbeForkOnActiveWorkbenchGame
    {
        get
        {
            var game = ActiveWorkbenchGame;
            if (game is null || CanRestoreDllOnActiveWorkbenchGame)
            {
                return false;
            }

            bool summaryMatch = CurrentAchievementSummary is not null
                                && string.Equals(CurrentAchievementSummary.GameId, game.GameId, StringComparison.OrdinalIgnoreCase);
            return game.Record.AntiCheatTier == AntiCheatTier.Tier0
                   && (game.Record.PlatformSource == PlatformSource.Steamrip
                       || game.Record.AchievementCaptureMode == AchievementCaptureMode.PendingGbeForkActivation
                       || (summaryMatch && CurrentAchievementSummary!.CanActivateGbeFork));
        }
    }

    public bool CanRestoreDllOnActiveWorkbenchGame
    {
        get
        {
            var game = ActiveWorkbenchGame;
            if (game is null)
            {
                return false;
            }

            bool summaryMatch = CurrentAchievementSummary is not null
                                && string.Equals(CurrentAchievementSummary.GameId, game.GameId, StringComparison.OrdinalIgnoreCase);
            return (summaryMatch && CurrentAchievementSummary!.HasGbeForkActivated)
                   || game.Record.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated;
        }
    }

    public int CurrentGameUnlockedCount => CurrentAchievementSummary?.UnlockedCount ?? 0;
    public int CurrentGameTotalCount => CurrentAchievementSummary?.TotalCount ?? 0;
    public double CurrentGameCompletionPercent => CurrentAchievementSummary?.CompletionPercent ?? 0.0;

    public int AchievementTabAllCount => _currentGameRawAchievements.Count;
    public int AchievementTabUnlockedCount => _currentGameRawAchievements.Count(a => a.IsUnlocked);
    public int AchievementTabLockedCount => _currentGameRawAchievements.Count(a => !a.IsUnlocked);
    public int AchievementTabHiddenCount => _currentGameRawAchievements.Count(a => a.Definition.IsHidden);

    public string AchievementTabAllLabel => string.Create(CultureInfo.InvariantCulture, $"全部 ({AchievementTabAllCount})");
    public string AchievementTabUnlockedLabel => string.Create(CultureInfo.InvariantCulture, $"已解锁 ({AchievementTabUnlockedCount})");
    public string AchievementTabLockedLabel => string.Create(CultureInfo.InvariantCulture, $"未解锁 ({AchievementTabLockedCount})");
    public string AchievementTabHiddenLabel => string.Create(CultureInfo.InvariantCulture, $"隐藏成就 ({AchievementTabHiddenCount})");

    public string AchievementsSubTabHeaderText =>
        string.Create(CultureInfo.InvariantCulture, $"成就 ({CurrentGameUnlockedCount}/{CurrentGameTotalCount})");

    public string CurrentGameProgressSummaryText =>
        CurrentGameTotalCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"已解锁 {CurrentGameUnlockedCount} / {CurrentGameTotalCount}（{CurrentGameCompletionPercent:0.0}%）")
            : "暂未发现该游戏的成就元数据（可在成就栏右上角 ⋯ 点击「刷新成就元数据」）";

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
        ISaveWatcherService? saveWatcherService = null,
        Func<ShellConfirmPrompt, bool>? confirmPromptHandler = null,
        Func<GamePropertiesEditRequest, GamePropertiesEditResult?>? editGamePropertiesHandler = null,
        Action<string>? copyToClipboard = null,
        int achievementSearchDebounceMs = 150,
        ISteamClientLocator? steamClientLocator = null,
        ISteamShortcutService? steamShortcutService = null,
        Func<ShellSteamBlockedPrompt, bool>? steamBlockedPromptHandler = null)
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
        _steamClientLocator = steamClientLocator ?? Spawn.Core.SteamClientLocator.Default;
        _steamShortcutService = steamShortcutService ?? new SteamShortcutService(_steamClientLocator);
        _openExternalUrl = openExternalUrl ?? OpenUrlWithShell;
        _copyToClipboard = copyToClipboard ?? Clipboard.SetText;
        _uiDispatcher = uiDispatcher;
        _achievementSearchDebounceMs = Math.Clamp(achievementSearchDebounceMs, 0, 1000);
        ConfirmPromptHandler = confirmPromptHandler;
        EditGamePropertiesHandler = editGamePropertiesHandler;
        SteamBlockedPromptHandler = steamBlockedPromptHandler;
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
            else if (args.PropertyName == nameof(SettingsViewModel.SelectedCategory))
            {
                if (IsNarrowWindow)
                {
                    IsNarrowSidebarOpen = false;
                }
            }
            else if (args.PropertyName == nameof(SettingsViewModel.IsDefenderWorking))
            {
                OnPropertyChanged(nameof(IsAnyBusyWorking));
            }
        };
        Settings.Update.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UpdateViewModel.IsBusy))
            {
                OnPropertyChanged(nameof(IsAnyBusyWorking));
            }
        };
        Settings.StatusFeedbackWithSeverityRequested += ShowStatusBanner;
        Settings.ScanDirectoriesRequested += ScanDirectoriesBatchAsync;
        Settings.ManualGameImportRequested += ImportManualGameFromSettingsAsync;
        Settings.ManualGameImportWithResultRequested += ImportManualGameFromSettingsWithResultAsync;
        Settings.OpenLastImportedGameRequested += JumpToGameInLibrary;
        Settings.GlobalSpoilerPreferenceChanged += reveal => RevealHiddenSpoilers = reveal;
        Settings.TrainerSafetySettingsChanged += () => _ = HandleTrainerSafetySettingsChangedAsync();
        Settings.SteamSettingsChanged += NotifyPrimaryLaunchButtonProperties;

        Trainer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TrainerWorkbenchViewModel.IsBusy))
            {
                OnPropertyChanged(nameof(IsAnyBusyWorking));
            }
        };
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
            OnPropertyChanged(nameof(IsDockedSidebarVisible));
            OnPropertyChanged(nameof(IsDrawerSidebarOpen));
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
        NotifyGbeForkWorkbenchFlags();
        NotifyMainPanelViewFlags();

        if (value == PrimaryNavSection.Achievements)
        {
            _ = RefreshAchievementsCenterOverviewAsync();
            if (!IsAchievementsOverviewSelected && SelectedAchievementsGame is not null)
            {
                _ = LoadAchievementsForGameAsync(SelectedAchievementsGame.GameId);
            }
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

    partial void OnIsNarrowSidebarOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(IsDrawerSidebarOpen));
    }

    partial void OnIsBusyWorkingChanged(bool value) => OnPropertyChanged(nameof(IsAnyBusyWorking));

    partial void OnIsInspectingGameDataChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAnyBusyWorking));
        OnPropertyChanged(nameof(ShowGameDataEmptyState));
    }

    partial void OnLibrarySidebarSearchTextChanged(string value) => RebuildVisibleLibraryGroups();

    partial void OnSelectedLibrarySortModeChanged(LibrarySortMode value) => RebuildVisibleLibraryGroups();

    partial void OnSelectedLibrarySourceFilterChanged(LibrarySourceFilter value) => RebuildVisibleLibraryGroups();

    partial void OnAchievementSidebarSearchTextChanged(string value) => RebuildFilteredAchievementSidebarGames();

    partial void OnSelectedAchievementSidebarSortModeChanged(AchievementSidebarSortMode value) => RebuildFilteredAchievementSidebarGames();

    partial void OnHideZeroAchievementGamesChanged(bool value) => RebuildFilteredAchievementSidebarGames();

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

        bool gameIdChanged = !string.Equals(oldValue?.GameId, newValue?.GameId, StringComparison.OrdinalIgnoreCase);
        if (gameIdChanged && !string.IsNullOrEmpty(AchievementSearchText))
        {
            ClearAchievementSearchImmediately();
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
            _achievementRowCache.Clear();
            CurrentAchievementSummary = null;
            _currentGameRawAchievements = [];
            FilteredAchievements.Clear();
            NotifyAchievementTabCountLabels();
            GameDataGroups.Clear();
            AllGameDataItems.Clear();
            OnPropertyChanged(nameof(HasGameDataGroups));
            OnPropertyChanged(nameof(ShowGameDataEmptyState));
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
        NotifyPrimaryLaunchButtonProperties();
        NotifyGbeForkWorkbenchFlags();
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
        SyncAchievementSidebarSelectionFlags();
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyGbeForkWorkbenchFlags();
        NotifyMainPanelViewFlags();
    }

    partial void OnSelectedAchievementsGameChanged(GameListItemViewModel? oldValue, GameListItemViewModel? newValue)
    {
        if (!string.Equals(oldValue?.GameId, newValue?.GameId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(AchievementSearchText))
        {
            ClearAchievementSearchImmediately();
        }

        SyncAchievementSidebarSelectionFlags();
        OnPropertyChanged(nameof(TopBarTitle));
        OnPropertyChanged(nameof(ActiveWorkbenchGame));
        NotifyGbeForkWorkbenchFlags();
        NotifyMainPanelViewFlags();
        if (newValue is not null && !IsAchievementsOverviewSelected)
        {
            _ = LoadAchievementsForGameAsync(newValue.GameId);
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

    partial void OnIsSyncingAllCloudSavesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSyncAllCloudSaves));
        OnPropertyChanged(nameof(IsAnyBusyWorking));
    }

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
        NotifyGbeForkWorkbenchFlags();
    }

    partial void OnSelectedAchievementTabChanged(AchievementFilterTab value) => RebuildFilteredAchievementRows();

    partial void OnSelectedAchievementSortModeChanged(AchievementSortMode value) => RebuildFilteredAchievementRows();

    partial void OnRevealHiddenSpoilersChanged(bool value)
    {
        Settings.GlobalRevealSpoilers = value;
        foreach (var row in _achievementRowCache.Values)
        {
            row.GlobalRevealSpoilers = value;
        }

        foreach (var row in FilteredAchievements)
        {
            row.GlobalRevealSpoilers = value;
        }
    }

    partial void OnAchievementSearchTextChanged(string value)
    {
        _achievementSearchDebounceCts?.Cancel();
        _achievementSearchDebounceCts?.Dispose();
        _achievementSearchDebounceCts = null;

        if (_achievementSearchDebounceMs <= 0 || string.IsNullOrWhiteSpace(value))
        {
            RebuildFilteredAchievementRows();
            return;
        }

        var cts = new CancellationTokenSource();
        _achievementSearchDebounceCts = cts;
        _ = DebounceRebuildFilteredAchievementsAsync(cts.Token);
    }

    private async Task DebounceRebuildFilteredAchievementsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_achievementSearchDebounceMs, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && Volatile.Read(ref _isDisposed) == 0)
            {
                RebuildFilteredAchievementRows();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer keystroke.
        }
    }

    private void ClearAchievementSearchImmediately()
    {
        _achievementSearchDebounceCts?.Cancel();
        _achievementSearchDebounceCts?.Dispose();
        _achievementSearchDebounceCts = null;
        AchievementSearchText = string.Empty;
    }

    private void NotifyPrimaryLaunchButtonProperties()
    {
        OnPropertyChanged(nameof(IsSyncingSteamShortcutForSelected));
        OnPropertyChanged(nameof(IsWaitingForSteamLaunch));
        OnPropertyChanged(nameof(PrimaryLaunchButtonPhase));
        OnPropertyChanged(nameof(PrimaryLaunchButtonTag));
        OnPropertyChanged(nameof(PrimaryLaunchButtonText));
        OnPropertyChanged(nameof(IsPrimaryLaunchButtonEnabled));
        OnPropertyChanged(nameof(PrimaryLaunchButtonToolTip));
        OnPropertyChanged(nameof(ShowPrimaryLaunchPlayGlyph));
        OnPropertyChanged(nameof(AlternateLaunchMenuItemText));
        OnPropertyChanged(nameof(CanExecuteAlternateLaunchForSelected));
        OnPropertyChanged(nameof(AlternateLaunchMenuItemToolTip));
    }

    private void NotifyGbeForkWorkbenchFlags()
    {
        OnPropertyChanged(nameof(CanActivateGbeForkOnSelected));
        OnPropertyChanged(nameof(CanRestoreDllOnSelected));
        OnPropertyChanged(nameof(CanActivateGbeForkOnActiveWorkbenchGame));
        OnPropertyChanged(nameof(CanRestoreDllOnActiveWorkbenchGame));
    }

    private void NotifyAchievementTabCountLabels()
    {
        OnPropertyChanged(nameof(AchievementTabAllCount));
        OnPropertyChanged(nameof(AchievementTabUnlockedCount));
        OnPropertyChanged(nameof(AchievementTabLockedCount));
        OnPropertyChanged(nameof(AchievementTabHiddenCount));
        OnPropertyChanged(nameof(AchievementTabAllLabel));
        OnPropertyChanged(nameof(AchievementTabUnlockedLabel));
        OnPropertyChanged(nameof(AchievementTabLockedLabel));
        OnPropertyChanged(nameof(AchievementTabHiddenLabel));
    }

    private bool ConfirmAction(ShellConfirmPrompt prompt) =>
        ConfirmPromptHandler is null || ConfirmPromptHandler(prompt);

    [RelayCommand]
    private void NavigateSection(PrimaryNavSection section) => ActiveSection = section;

    [RelayCommand]
    private void ReturnFromSettings() => ActiveSection = _lastNonSettingsSection;

    [RelayCommand]
    private void ToggleNarrowSidebar() => IsNarrowSidebarOpen = !IsNarrowSidebarOpen;

    [RelayCommand]
    private void CloseNarrowSidebar() => IsNarrowSidebarOpen = false;

    [RelayCommand]
    private void ClearLibrarySidebarSearch() => LibrarySidebarSearchText = string.Empty;

    [RelayCommand]
    private void SelectLibraryGame(GameListItemViewModel? item)
    {
        Interlocked.Increment(ref _userSelectionVersion);
        SelectedGame = item;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

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
    private void OpenOverlaySettings()
    {
        Settings.SelectedCategory = SettingsCategory.OverlayAndSound;
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
    private void SelectAchievementSidebarGame(AchievementSidebarItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectAchievementsGameItem(item.Game);
    }

    [RelayCommand]
    private void SelectAchievementsGameItem(GameListItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        bool sectionChanged = ActiveSection != PrimaryNavSection.Achievements;
        SelectedAchievementsGame = item;
        IsAchievementsOverviewSelected = false;
        if (IsNarrowWindow)
        {
            IsNarrowSidebarOpen = false;
        }

        if (sectionChanged)
        {
            ActiveSection = PrimaryNavSection.Achievements;
        }
        else
        {
            _ = LoadAchievementsForGameAsync(item.GameId);
        }
    }

    [RelayCommand]
    private void ClearAchievementSidebarSearch() => AchievementSidebarSearchText = string.Empty;

    [RelayCommand]
    private async Task RefreshAchievementsHubAsync()
    {
        await RefreshAchievementsCenterOverviewAsync().ConfigureAwait(true);
        if (!IsAchievementsOverviewSelected && SelectedAchievementsGame is not null)
        {
            await LoadAchievementsForGameAsync(SelectedAchievementsGame.GameId).ConfigureAwait(true);
        }

        ShowStatusBanner($"已刷新成就进度（累计已解锁 {GlobalTotalUnlockedCount} / {GlobalTotalAchievementCount}）", isError: false);
    }

    [RelayCommand]
    private void OpenRecentUnlockGame(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        var match = AllGames.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            SelectAchievementsGameItem(match);
        }
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
    private void JumpToGameInLibraryTrainers(string? gameId)
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
            SelectedGameSubTab = GameDetailSubTab.Trainers;
            ActiveSection = PrimaryNavSection.Library;
            _ = Trainer.LoadForGameAsync(match);
        }
    }

    [RelayCommand]
    private void SetAchievementFilterTab(AchievementFilterTab tab) => SelectedAchievementTab = tab;

    [RelayCommand]
    private void ClearAchievementSearch() => ClearAchievementSearchImmediately();

    [RelayCommand]
    private void DismissStatusBanner()
    {
        _bannerAutoHideCts?.Cancel();
        _bannerAutoHideCts?.Dispose();
        _bannerAutoHideCts = null;
        StatusBannerMessage = string.Empty;
        IsStatusBannerError = false;
    }

    [RelayCommand]
    private void CopyStatusBanner()
    {
        if (string.IsNullOrWhiteSpace(StatusBannerMessage))
        {
            return;
        }

        try
        {
            _copyToClipboard(StatusBannerMessage);
        }
        catch
        {
            // Ignore clipboard contention.
        }
    }

    public async Task ImportManualGameFromSettingsAsync(string rawInput)
    {
        _ = await ImportManualGameFromSettingsWithResultAsync(rawInput).ConfigureAwait(true);
    }

    public async Task<ManualImportOutcome> ImportManualGameFromSettingsWithResultAsync(string rawInput)
    {
        if (IsBusyWorking || string.IsNullOrWhiteSpace(rawInput))
        {
            return new ManualImportOutcome(false);
        }

        string resolvedPath = ResolveUserEnteredPath(rawInput.Trim().Trim('"'));
        if (!Directory.Exists(resolvedPath) && !File.Exists(resolvedPath))
        {
            ShowStatusBanner($"未找到路径：{rawInput}", isError: true);
            return new ManualImportOutcome(false);
        }

        return await ImportOrScanSinglePathAsync(resolvedPath, stayInCurrentSection: true).ConfigureAwait(true);
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
    private Task PrimaryLaunchOrStopAsync() => LaunchOrStopSpecificGameAsync(SelectedGame);

    [RelayCommand]
    private Task LaunchSelectedGameAlternateModeAsync()
    {
        var game = SelectedGame;
        if (game is null)
        {
            return Task.CompletedTask;
        }

        GameLaunchMode targetMode = game.IsSteamLaunchConfigured
            ? GameLaunchMode.Direct
            : GameLaunchMode.Steam;
        return LaunchGameWithModeAsync(game, targetMode);
    }

    [RelayCommand]
    private async Task LaunchOrStopSpecificGameAsync(GameListItemViewModel? game)
    {
        if (game is null)
        {
            return;
        }

        string targetGameId = game.GameId;
        if (_launchingGameIds.Contains(targetGameId)
            || _steamSyncingGameIds.Contains(targetGameId)
            || _stoppingGameIds.Contains(targetGameId))
        {
            return;
        }

        if (game.IsRunning)
        {
            bool isWaitingForSteam = game.ActiveSession?.State == PlaySessionActivityState.ShellBreakingWatch
                                     && game.ActiveSession.LaunchedViaSteam;

            if (!isWaitingForSteam
                && game.ActiveSession?.State is PlaySessionActivityState.Launching or PlaySessionActivityState.ShellBreakingWatch or PlaySessionActivityState.Stopping)
            {
                return;
            }

            if (!isWaitingForSteam)
            {
                bool confirmed = ConfirmAction(new ShellConfirmPrompt(
                    Title: $"停止《{game.Title}》？",
                    Message: $"确定要结束正在运行的《{game.Title}》吗？\n\n未保存的游戏进度可能会丢失，建议先在游戏内保存后再停止。",
                    ConfirmLabel: "停止游戏",
                    CancelLabel: "继续游玩",
                    IsDanger: true));
                if (!confirmed)
                {
                    return;
                }
            }

            _stoppingGameIds.Add(targetGameId);
            NotifyPrimaryLaunchButtonProperties();
            try
            {
                bool stopped = await _launchService.StopGameAsync(targetGameId).ConfigureAwait(true);
                ShowStatusBanner(
                    isWaitingForSteam
                        ? (stopped ? $"已取消等待 Steam 启动《{game.Title}》" : $"《{game.Title}》等待状态已结束")
                        : (stopped ? $"已请求停止《{game.Title}》" : $"未能停止《{game.Title}》，可能已自行退出"),
                    isError: !isWaitingForSteam && !stopped);
            }
            finally
            {
                _stoppingGameIds.Remove(targetGameId);
                NotifyPrimaryLaunchButtonProperties();
            }

            return;
        }

        await LaunchGameWithModeAsync(game, explicitModeOverride: null).ConfigureAwait(true);
    }

    public async Task LaunchGameWithModeAsync(
        GameListItemViewModel game,
        GameLaunchMode? explicitModeOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        string targetGameId = game.GameId;
        if (game.IsRunning
            || _launchingGameIds.Contains(targetGameId)
            || _steamSyncingGameIds.Contains(targetGameId)
            || _stoppingGameIds.Contains(targetGameId))
        {
            return;
        }

        if (GuardPhase == AntiCheatGuardPhase.CooldownLocked)
        {
            ShowStatusBanner(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"反作弊安全守卫余温冷却中（剩余 {GuardCooldownRemainingSeconds:0.0} 秒），请稍候再启动游戏"),
                isError: true);
            return;
        }

        GameSteamLaunchSettings savedSettings = await _db
            .GetGameSteamLaunchSettingsAsync(targetGameId, cancellationToken)
            .ConfigureAwait(true);
        game.UpdateSteamLaunchSettings(savedSettings);

        GameLaunchMode effectiveMode = explicitModeOverride ?? savedSettings.LaunchMode;
        if (effectiveMode == GameLaunchMode.Direct)
        {
            await ExecuteDirectLaunchAsync(game, explicitModeOverride == GameLaunchMode.Direct, cancellationToken)
                .ConfigureAwait(true);
            return;
        }

        await ExecuteSteamLaunchAsync(game, savedSettings, cancellationToken).ConfigureAwait(true);
    }

    private async Task ExecuteDirectLaunchAsync(
        GameListItemViewModel game,
        bool isExplicitOverride,
        CancellationToken cancellationToken)
    {
        string targetGameId = game.GameId;
        _launchingGameIds.Add(targetGameId);
        NotifyPrimaryLaunchButtonProperties();
        try
        {
            GameLaunchResult result = isExplicitOverride
                ? await _launchService.LaunchGameAsync(
                    targetGameId,
                    new GameLaunchExecutionOptions { LaunchModeOverride = GameLaunchMode.Direct },
                    cancellationToken).ConfigureAwait(true)
                : await _launchService.LaunchGameAsync(targetGameId, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ShowStatusBanner(result.ErrorMessage ?? "启动游戏失败", isError: true);
                return;
            }

            if (result.Session is not null)
            {
                game.UpdateSession(result.Session);
                RebuildGroupsPreservingSelection();
                OnPropertyChanged(nameof(HasAnyRunningGame));
                OnPropertyChanged(nameof(CanSyncAllCloudSaves));
                OnPropertyChanged(nameof(LibraryRailToolTip));
            }

            ShowStatusBanner($"已启动《{game.Title}》 · 进程追踪与实时跳杯监控已激活", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"启动《{game.Title}》失败：{ex.Message}", isError: true);
        }
        finally
        {
            _launchingGameIds.Remove(targetGameId);
            NotifyPrimaryLaunchButtonProperties();
        }
    }

    private async Task ExecuteSteamLaunchAsync(
        GameListItemViewModel game,
        GameSteamLaunchSettings savedSettings,
        CancellationToken cancellationToken)
    {
        string targetGameId = game.GameId;
        string? steamOverride = await _db.GetSteamInstallDirectoryOverrideAsync(cancellationToken).ConfigureAwait(true);
        uint? preferredAccountId = await _db.GetSteamTargetAccountIdAsync(cancellationToken).ConfigureAwait(true);
        bool suppressToastSetting = await _db.GetSteamSuppressDuplicateAchievementToastAsync(cancellationToken).ConfigureAwait(true);
        bool suppressToast = game.Record.PlatformSource == PlatformSource.SteamLocal && suppressToastSetting;

        SteamLaunchPreparationResult prep;
        try
        {
            prep = await _steamShortcutService.PrepareLaunchAsync(
                game.Record,
                savedSettings,
                steamOverride,
                preferredAccountId,
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"准备通过 Steam 启动《{game.Title}》失败：{ex.Message}", isError: true);
            return;
        }

        if (prep.IsBlocked)
        {
            string baseError = prep.ErrorMessage ?? "当前游戏无法通过 Steam 启动。";
            bool isSteamMissing = prep.Status == SteamLaunchPreparationStatus.BlockedSteamNotFound;
            string detailMessage = isSteamMissing && !baseError.Contains("设置 → 常规与游戏库", StringComparison.Ordinal)
                ? $"{baseError}\n\n提示：请前往「设置 → 常规与游戏库 → Steam 客户端与手柄映射启动」重新检测或手动指定 Steam 安装目录 / steam.exe。"
                : baseError;

            ShowStatusBanner(baseError, isError: true);

            if (SteamBlockedPromptHandler is not null)
            {
                bool openSettings = SteamBlockedPromptHandler(new ShellSteamBlockedPrompt(
                    Title: $"无法通过 Steam 启动《{game.Title}》",
                    Message: detailMessage,
                    CanOpenSteamSettings: isSteamMissing));
                if (openSettings && isSteamMissing)
                {
                    OpenLibrarySettings();
                }
            }

            return;
        }

        string? launchUri = prep.LaunchUri;
        bool steamWasRunningBeforeLaunch = false;

        if (prep.Status == SteamLaunchPreparationStatus.NeedsSteamRestartToSync)
        {
            string warningBlock = prep.Warnings.Count > 0
                ? "\n\n兼容性提示：\n" + string.Join("\n", prep.Warnings.Select(w => $"• {w.Message}"))
                : string.Empty;

            string confirmBody =
                $"通过 Steam 启动《{game.Title}》需要将快捷方式写入 Steam 的 shortcuts.vdf。\n\n"
                + "为什么需要优雅重启一次 Steam？\n"
                + "• Steam 仅在启动时读取 shortcuts.vdf，退出时会覆写该文件；若在 Steam 运行期间直接修改，更改不会生效且会被覆盖。\n\n"
                + "点击「优雅重启 Steam 并启动」后，SPAWN 将：\n"
                + "1. 向 Steam 发送温和退出指令（steam.exe -shutdown，绝不强制结束进程）并等待其安全退出；\n"
                + "2. 自动备份原 shortcuts.vdf（.spawn.bak）并原子写入本游戏快捷方式；\n"
                + "3. 重新启动 Steam 等待账号就绪，再自动通过 Steam 启动本游戏。\n\n"
                + "提醒：请确认当前 Steam 中没有正在游玩的联机游戏、未保存的云存档同步或正在进行的重要下载。"
                + warningBlock;

            bool confirmed = ConfirmAction(new ShellConfirmPrompt(
                Title: $"优雅重启 Steam 并启动《{game.Title}》？",
                Message: confirmBody,
                ConfirmLabel: "优雅重启 Steam 并启动",
                CancelLabel: "取消",
                IsDanger: false));

            if (!confirmed)
            {
                ShowStatusBanner("已取消 Steam 重启与同步（未改动 Steam 或 shortcuts.vdf）", isError: false);
                return;
            }

            _steamSyncingGameIds.Add(targetGameId);
            _launchingGameIds.Add(targetGameId);
            NotifyPrimaryLaunchButtonProperties();
            ShowStatusBanner("正在优雅重启 Steam 并同步快捷方式…", isError: false, autoDismiss: false);

            try
            {
                SteamShortcutSyncResult sync = await _steamShortcutService.ApplyShortcutAndRestartIfNeededAsync(
                    game.Record,
                    savedSettings,
                    allowGracefulSteamRestart: true,
                    steamOverride,
                    preferredAccountId,
                    cancellationToken).ConfigureAwait(true);

                if (sync.ShortcutAppId is { } syncedAppId && syncedAppId != 0)
                {
                    await _db.UpdateGameRegisteredShortcutAppIdAsync(targetGameId, syncedAppId, cancellationToken).ConfigureAwait(true);
                    savedSettings = savedSettings with { RegisteredShortcutAppId = syncedAppId };
                    game.UpdateSteamLaunchSettings(savedSettings);
                }

                Settings.RefreshSteamClientDiscovery(emitFeedback: false);

                if (!sync.Succeeded)
                {
                    ShowStatusBanner(sync.ErrorMessage ?? "同步 Steam 快捷方式失败", isError: true);
                    return;
                }

                launchUri = sync.LaunchUri ?? launchUri;
                steamWasRunningBeforeLaunch = false;
            }
            catch (Exception ex)
            {
                ShowStatusBanner($"优雅重启 Steam 并同步快捷方式失败：{ex.Message}", isError: true);
                return;
            }
            finally
            {
                _steamSyncingGameIds.Remove(targetGameId);
                NotifyPrimaryLaunchButtonProperties();
            }
        }
        else if (prep.Status == SteamLaunchPreparationStatus.NeedsShortcutWriteSteamClosed)
        {
            _steamSyncingGameIds.Add(targetGameId);
            _launchingGameIds.Add(targetGameId);
            NotifyPrimaryLaunchButtonProperties();
            ShowStatusBanner("正在写入 Steam 快捷方式并准备启动…", isError: false, autoDismiss: false);

            try
            {
                SteamShortcutSyncResult sync = await _steamShortcutService.ApplyShortcutAndRestartIfNeededAsync(
                    game.Record,
                    savedSettings,
                    allowGracefulSteamRestart: false,
                    steamOverride,
                    preferredAccountId,
                    cancellationToken).ConfigureAwait(true);

                if (sync.ShortcutAppId is { } syncedAppId && syncedAppId != 0)
                {
                    await _db.UpdateGameRegisteredShortcutAppIdAsync(targetGameId, syncedAppId, cancellationToken).ConfigureAwait(true);
                    savedSettings = savedSettings with { RegisteredShortcutAppId = syncedAppId };
                    game.UpdateSteamLaunchSettings(savedSettings);
                }

                if (!sync.Succeeded)
                {
                    ShowStatusBanner(sync.ErrorMessage ?? "写入 Steam 快捷方式失败", isError: true);
                    return;
                }

                launchUri = sync.LaunchUri ?? launchUri;
                steamWasRunningBeforeLaunch = false;
            }
            catch (Exception ex)
            {
                ShowStatusBanner($"写入 Steam 快捷方式失败：{ex.Message}", isError: true);
                return;
            }
            finally
            {
                _steamSyncingGameIds.Remove(targetGameId);
                NotifyPrimaryLaunchButtonProperties();
            }
        }
        else
        {
            // ReadyOfficialSteam or ReadyNonSteamShortcut
            SteamClientDiscoveryResult discovery = _steamClientLocator.Discover(steamOverride, preferredAccountId);
            steamWasRunningBeforeLaunch = discovery.IsSteamRunning;

            if (prep.Status == SteamLaunchPreparationStatus.ReadyNonSteamShortcut
                && prep.ShortcutAppId is { } existingAppId
                && existingAppId != 0
                && savedSettings.RegisteredShortcutAppId != existingAppId)
            {
                await _db.UpdateGameRegisteredShortcutAppIdAsync(targetGameId, existingAppId, cancellationToken).ConfigureAwait(true);
                savedSettings = savedSettings with { RegisteredShortcutAppId = existingAppId };
                game.UpdateSteamLaunchSettings(savedSettings);
            }

            _launchingGameIds.Add(targetGameId);
            NotifyPrimaryLaunchButtonProperties();
        }

        try
        {
            var execOptions = new GameLaunchExecutionOptions
            {
                LaunchModeOverride = GameLaunchMode.Steam,
                SteamLaunchUri = launchUri,
                SuppressDuplicateAchievementToast = suppressToast,
                SteamWasRunningBeforeLaunch = steamWasRunningBeforeLaunch,
            };

            GameLaunchResult result = await _launchService
                .LaunchGameAsync(targetGameId, execOptions, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ShowStatusBanner(result.ErrorMessage ?? "通过 Steam 启动游戏失败", isError: true);
                return;
            }

            if (result.Session is not null)
            {
                game.UpdateSession(result.Session);
                RebuildGroupsPreservingSelection();
                OnPropertyChanged(nameof(HasAnyRunningGame));
                OnPropertyChanged(nameof(CanSyncAllCloudSaves));
                OnPropertyChanged(nameof(LibraryRailToolTip));
            }

            string statusMsg = $"已通过 Steam 请求启动《{game.Title}》，正在等待游戏进程拉起…";
            if (prep.Warnings.Count > 0 && _shownSteamWarningGameIds.Add(targetGameId))
            {
                statusMsg = $"{statusMsg}（提示：{prep.Warnings[0].Message}）";
            }

            ShowStatusBanner(statusMsg, isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"通过 Steam 启动《{game.Title}》失败：{ex.Message}", isError: true);
        }
        finally
        {
            _launchingGameIds.Remove(targetGameId);
            _steamSyncingGameIds.Remove(targetGameId);
            NotifyPrimaryLaunchButtonProperties();
        }
    }

    [RelayCommand]
    private void OpenSelectedGameFolder() => OpenSpecificGameFolder(ActiveWorkbenchGame);

    [RelayCommand]
    private void OpenSpecificGameFolder(GameListItemViewModel? game)
    {
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
    private async Task EditGamePropertiesAsync(GameListItemViewModel? targetGame = null)
    {
        var game = targetGame ?? ActiveWorkbenchGame ?? SelectedGame;
        if (game is null || IsBusyWorking)
        {
            return;
        }

        if (EditGamePropertiesHandler is null)
        {
            ShowStatusBanner("当前环境不支持打开属性编辑面板", isError: true);
            return;
        }

        IReadOnlyList<string> candidates = EnumerateCandidateExecutables(
            game.Record.InstallDir,
            game.Record.ExecutableRelPath);

        GameSteamLaunchSettings currentSteamSettings = await _db
            .GetGameSteamLaunchSettingsAsync(game.GameId)
            .ConfigureAwait(true);

        async Task<SteamLaunchPreparationResult> PreviewSteamLaunchAsync(
            GameRecord draftGame,
            GameSteamLaunchSettings draftSettings,
            CancellationToken ct)
        {
            string? steamOverride = await _db.GetSteamInstallDirectoryOverrideAsync(ct).ConfigureAwait(false);
            uint? preferredAccountId = await _db.GetSteamTargetAccountIdAsync(ct).ConfigureAwait(false);
            return await _steamShortcutService
                .PrepareLaunchAsync(draftGame, draftSettings, steamOverride, preferredAccountId, ct)
                .ConfigureAwait(false);
        }

        var request = new GamePropertiesEditRequest(
            GameId: game.GameId,
            Title: game.Record.Title,
            InstallDir: game.Record.InstallDir,
            ExecutableRelPath: game.Record.ExecutableRelPath,
            CandidateExecutables: candidates,
            LaunchArguments: game.Record.LaunchArguments ?? string.Empty,
            SteamAppId: game.Record.SteamAppId,
            GogGameId: game.Record.GogGameId,
            CoverImagePath: game.Record.CoverImagePath,
            IconPath: game.Record.IconPath,
            PlatformSource: game.Record.PlatformSource,
            AntiCheatTier: game.Record.AntiCheatTier,
            AchievementCaptureMode: game.Record.AchievementCaptureMode,
            SteamLaunchSettings: currentSteamSettings,
            PreviewSteamLaunchAsync: PreviewSteamLaunchAsync);

        GamePropertiesEditResult? edited = EditGamePropertiesHandler(request);
        if (edited is null)
        {
            return;
        }

        string trimmedTitle = edited.Title.Trim();
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            ShowStatusBanner("游戏名称不能为空", isError: true);
            return;
        }

        string trimmedExe = edited.ExecutableRelPath.Trim();
        if (string.IsNullOrWhiteSpace(trimmedExe))
        {
            ShowStatusBanner("游戏主程序路径不能为空", isError: true);
            return;
        }

        GameLaunchMode requestedLaunchMode = edited.LaunchMode ?? currentSteamSettings.LaunchMode;
        bool isOfficialSteamAfterEdit = game.Record.PlatformSource == PlatformSource.SteamLocal && edited.SteamAppId is > 0;
        if (requestedLaunchMode == GameLaunchMode.Steam
            && !isOfficialSteamAfterEdit
            && game.Record.AntiCheatTier >= AntiCheatTier.Tier2)
        {
            ShowStatusBanner(
                $"非 Steam 来源的 {game.Record.AntiCheatTier} 游戏禁止启用通过 Steam 启动（防反作弊与覆盖层冲突）",
                isError: true);
            return;
        }

        bool idChanged = edited.SteamAppId != game.Record.SteamAppId
                         || !string.Equals(
                             edited.GogGameId?.Trim() ?? string.Empty,
                             game.Record.GogGameId?.Trim() ?? string.Empty,
                             StringComparison.OrdinalIgnoreCase);

        IsBusyWorking = true;
        try
        {
            var update = new GameMetadataUpdate
            {
                Title = trimmedTitle,
                SortTitle = trimmedTitle,
                ExecutableRelPath = trimmedExe,
                LaunchArguments = edited.LaunchArguments.Trim(),
                SteamAppId = edited.SteamAppId,
                GogGameId = edited.GogGameId?.Trim() ?? string.Empty,
                CoverImagePath = edited.CoverImagePath?.Trim() ?? string.Empty,
                IconPath = edited.IconPath?.Trim() ?? string.Empty,
            };

            GameRecord? updated = await _libraryService.UpdateGameMetadataAsync(game.GameId, update).ConfigureAwait(true);
            if (updated is null)
            {
                ShowStatusBanner("保存游戏属性失败：未找到该游戏记录", isError: true);
                return;
            }

            if (edited.LaunchMode.HasValue || edited.UseSteamAppIdAsShortcutName.HasValue)
            {
                bool useAppIdName = (edited.UseSteamAppIdAsShortcutName ?? currentSteamSettings.UseSteamAppIdAsShortcutName)
                                    && edited.SteamAppId is > 0
                                    && !isOfficialSteamAfterEdit;
                GameSteamLaunchSettings updatedSteamSettings = currentSteamSettings with
                {
                    LaunchMode = requestedLaunchMode,
                    UseSteamAppIdAsShortcutName = useAppIdName,
                };
                await _db.SaveGameSteamLaunchSettingsAsync(game.GameId, updatedSteamSettings).ConfigureAwait(true);
                game.UpdateSteamLaunchSettings(updatedSteamSettings);
                _shownSteamWarningGameIds.Remove(game.GameId);
            }

            if (idChanged)
            {
                try
                {
                    _ = await _achievementService.SyncGameSchemaAsync(game.GameId, forceRefresh: true).ConfigureAwait(true);
                }
                catch
                {
                    // Offline or rate-limited schema sync must not block metadata save.
                }
            }

            await EnqueueLibraryReloadAsync(
                LibraryReloadRequest.PreserveCurrent(dirtyAchievementGameId: game.GameId)).ConfigureAwait(true);
            NotifyPrimaryLaunchButtonProperties();
            ShowStatusBanner($"已更新《{updated.Title}》的游戏属性与启动设置", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"保存游戏属性失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusyWorking = false;
        }
    }

    public static IReadOnlyList<string> EnumerateCandidateExecutables(string installDir, string currentRelPath)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(currentRelPath))
        {
            string normalizedCurrent = currentRelPath.Trim().Replace('/', '\\');
            if (seen.Add(normalizedCurrent))
            {
                results.Add(normalizedCurrent);
            }
        }

        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return results;
        }

        try
        {
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((installDir, 0));

            while (queue.Count > 0 && results.Count < 80)
            {
                (string dir, int depth) = queue.Dequeue();
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    if (IsIgnoredAuxiliaryExecutable(fileName))
                    {
                        continue;
                    }

                    string rel = Path.GetRelativePath(installDir, file).Replace('/', '\\');
                    if (seen.Add(rel))
                    {
                        results.Add(rel);
                        if (results.Count >= 80)
                        {
                            break;
                        }
                    }
                }

                if (depth >= 3)
                {
                    continue;
                }

                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(dir))
                    {
                        string dirName = Path.GetFileName(subDir);
                        if (dirName.StartsWith('.')
                            || dirName.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase)
                            || dirName.Equals("Redist", StringComparison.OrdinalIgnoreCase)
                            || dirName.Equals("DirectX", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        queue.Enqueue((subDir, depth + 1));
                    }
                }
                catch
                {
                    // Ignore restricted subdirectories.
                }
            }
        }
        catch
        {
            // Fall back to current executable only.
        }

        return results;
    }

    private static bool IsIgnoredAuxiliaryExecutable(string fileName) =>
        fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("crashreporter", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("crashhandler", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("unitycrashhandler", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("vc_redist", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("dxsetup", StringComparison.OrdinalIgnoreCase);

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

        if (game.IsRunning)
        {
            ShowStatusBanner($"《{game.Title}》正在运行中，请先退出游戏后再激活离线成就补丁", isError: true);
            return;
        }

        bool confirmed = ConfirmAction(new ShellConfirmPrompt(
            Title: $"为《{game.Title}》激活离线成就补丁？",
            Message: $"激活后将自动备份游戏目录中的原始 steam_api DLL 文件（.spawn_vanilla_bak），并启用 Goldberg GSE 离线成就记录。\n\n注意：启用后游戏默认存档位置可能会切换至 GSE Saves 目录，建议提前确认或备份当前存档；后续随时可在菜单中无损还原原始 DLL。",
            ConfirmLabel: "确认激活",
            CancelLabel: "取消",
            IsDanger: false));
        if (!confirmed)
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

        if (game.IsRunning)
        {
            ShowStatusBanner($"《{game.Title}》正在运行中，请先退出游戏后再还原原始 DLL", isError: true);
            return;
        }

        bool confirmed = ConfirmAction(new ShellConfirmPrompt(
            Title: $"还原《{game.Title}》的原始 DLL？",
            Message: $"将按 SHA-256 校验并还原原始 steam_api DLL，同时退出 Goldberg GSE 离线成就补丁模式。\n\n注意：若您在 GSE Saves 目录下产生过新进度，请确认是否需要先备份存档。",
            ConfirmLabel: "确认还原",
            CancelLabel: "取消",
            IsDanger: false));
        if (!confirmed)
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
    private Task RemoveSelectedGameFromLibraryAsync() => RemoveSpecificGameFromLibraryAsync(SelectedGame);

    [RelayCommand]
    private async Task RemoveSpecificGameFromLibraryAsync(GameListItemViewModel? game)
    {
        if (game is null || IsBusyWorking)
        {
            return;
        }

        if (game.IsRunning)
        {
            ShowStatusBanner("游戏正在运行中，请先停止游戏后再移除记录", isError: true);
            return;
        }

        int snapshotCount = 0;
        int branchCount = 0;
        if (_saveTimeMachineService is not null)
        {
            try
            {
                SaveTreeGraphDto graph = await _saveTimeMachineService.GetSaveTreeGraphAsync(game.GameId).ConfigureAwait(true);
                snapshotCount = graph.Nodes.Count;
                branchCount = graph.Branches.Count;
            }
            catch
            {
                // Non-fatal: still show confirmation with 0 counts.
            }
        }

        int boundTrainerCount = 0;
        try
        {
            var trainers = await _trainerCatalogService.ListForGameAsync(game.GameId).ConfigureAwait(true);
            boundTrainerCount = trainers.Count;
        }
        catch
        {
            // Non-fatal.
        }

        bool hasGbeFork = game.Record.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated
                          || (CurrentAchievementSummary is { HasGbeForkActivated: true }
                              && string.Equals(CurrentAchievementSummary.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));

        string gbeWarning = hasGbeFork
            ? "\n\n⚠ 该游戏当前已激活 gbe_fork 离线成就补丁，建议先点击「还原原始 DLL」再从游戏库移除。"
            : string.Empty;

        string promptBody = string.Create(
            CultureInfo.InvariantCulture,
            $"将从 SPAWN 游戏库移除《{game.Title}》，并级联清除以下 SPAWN 管理记录：\n"
            + $"• 游玩时长：{game.PlaytimeSummaryText}\n"
            + $"• 存档时间机器：{snapshotCount} 份快照 · {branchCount} 条分支\n"
            + $"• 已绑定修改器：{boundTrainerCount} 项\n\n"
            + $"磁盘上的游戏本体与原始存档文件将完好保留，不会被删除。{gbeWarning}");

        bool confirmed = ConfirmAction(new ShellConfirmPrompt(
            Title: $"从游戏库移除《{game.Title}》？",
            Message: promptBody,
            ConfirmLabel: "确认移除",
            CancelLabel: "取消",
            IsDanger: true));
        if (!confirmed)
        {
            return;
        }

        string title = game.Title;
        string removedGameId = game.GameId;
        bool removed = await _libraryService.RemoveGameAsync(removedGameId).ConfigureAwait(true);
        if (removed)
        {
            Interlocked.Increment(ref _userSelectionVersion);
            if (SelectedGame is not null && string.Equals(SelectedGame.GameId, removedGameId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedGame = null;
            }

            if (SelectedSaveGame is not null && string.Equals(SelectedSaveGame.GameId, removedGameId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedSaveGame = null;
                IsSaveOverviewSelected = true;
                _ = Saves.LoadForGameAsync(null);
            }

            if (SelectedAchievementsGame is not null && string.Equals(SelectedAchievementsGame.GameId, removedGameId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedAchievementsGame = null;
                IsAchievementsOverviewSelected = true;
            }

            if (SelectedTrainerGame is not null && string.Equals(SelectedTrainerGame.GameId, removedGameId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedTrainerGame = null;
                IsTrainerOverviewSelected = true;
            }

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

        var selectedItems = AllGameDataItems
            .Where(i => i.IsSelected && i.IsSafeToClean)
            .ToList();
        var selectedIds = selectedItems
            .Select(i => i.EntryId)
            .ToList();

        if (selectedIds.Count == 0 && !ResetSpawnStateOnClean)
        {
            ShowStatusBanner("请至少勾选一项要清理的数据，或开启「同时重置 SPAWN 成就进度与游玩时长」", isError: true);
            return;
        }

        int existingFileCount = selectedItems
            .Where(i => !i.IsRegistryEntry && i.Exists)
            .Sum(i => Math.Max(i.Entry.FileCount, 1));
        int existingFileLocationCount = selectedItems.Count(i => !i.IsRegistryEntry && i.Exists);
        long totalBytes = selectedItems
            .Where(i => !i.IsRegistryEntry && i.Exists)
            .Sum(i => i.Entry.TotalBytes);
        int existingRegistryCount = selectedItems.Count(i => i.IsRegistryEntry && i.Exists);

        string resetLine = ResetSpawnStateOnClean
            ? "• 同时重置 SPAWN 记录：是（将清空该游戏在 SPAWN 中的成就解锁记录与游玩时长）"
            : "• 同时重置 SPAWN 记录：否（保留成就进度与游玩时长）";

        string confirmMessage = string.Create(
            CultureInfo.InvariantCulture,
            $"即将清理《{game.Title}》选中的 {selectedItems.Count} 项数据路径：\n"
            + $"• 实际已存在文件：{existingFileCount} 个（分布在 {existingFileLocationCount} 处目录/文件，共 {UiFormatters.FormatByteSize(totalBytes)}）\n"
            + $"• 实际已存在注册表项：{existingRegistryCount} 项\n"
            + $"{resetLine}\n\n"
            + $"清理前会自动创建可恢复备份，如误清可随时点击「恢复上次清理」。");

        bool confirmed = ConfirmAction(new ShellConfirmPrompt(
            Title: $"确认清理《{game.Title}》的游戏数据？",
            Message: confirmMessage,
            ConfirmLabel: "确认清理",
            CancelLabel: "取消",
            IsDanger: true));
        if (!confirmed)
        {
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
            if (result.Succeeded && ResetSpawnStateOnClean && !result.SpawnStateReset)
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
            OnPropertyChanged(nameof(HasGameDataGroups));
            OnPropertyChanged(nameof(ShowGameDataEmptyState));
            NotifyGameDataSelectionChanged();
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"读取游戏数据路径失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsInspectingGameData = false;
            OnPropertyChanged(nameof(HasGameDataGroups));
            OnPropertyChanged(nameof(ShowGameDataEmptyState));
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

    private async Task<ManualImportOutcome> ImportOrScanSinglePathAsync(
        string resolvedPath,
        bool stayInCurrentSection = false)
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
                if (!stayInCurrentSection && Volatile.Read(ref _userSelectionVersion) == selectionVersionAtStart)
                {
                    ActiveSection = PrimaryNavSection.Library;
                }

                ShowStatusBanner($"已添加游戏《{directGame.Title}》并自动同步成就库", isError: false);
                return new ManualImportOutcome(
                    Succeeded: true,
                    GameId: directGame.GameId,
                    GameTitle: directGame.Title,
                    ImportedCount: 1);
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
                string? firstTitle = scanned.Count > 0 ? scanned[0].Title : null;
                await EnqueueLibraryReloadAsync(
                    LibraryReloadRequest.PreserveOrFallback(firstId, selectionVersionAtStart)).ConfigureAwait(true);
                RefreshUnimportedLocalGamesHint();
                if (!stayInCurrentSection && scanned.Count > 0)
                {
                    ActiveSection = PrimaryNavSection.Library;
                }

                ShowStatusBanner(
                    scanned.Count > 0
                        ? $"扫描完成，共发现并同步 {scanned.Count} 款单机游戏"
                        : "扫描完成，该目录下未发现包含可执行游戏主程序的子目录",
                    isError: scanned.Count == 0);
                return new ManualImportOutcome(
                    Succeeded: scanned.Count > 0,
                    GameId: firstId,
                    GameTitle: firstTitle,
                    ImportedCount: scanned.Count);
            }

            ShowStatusBanner("未识别到可导入的游戏主程序或目录", isError: true);
            return new ManualImportOutcome(false);
        }
        catch (Exception ex)
        {
            ShowStatusBanner($"导入或扫描失败：{ex.Message}", isError: true);
            return new ManualImportOutcome(false);
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
        var steamSettingsByGameId = new Dictionary<string, GameSteamLaunchSettings>(records.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            summariesByGameId[record.GameId] = await _achievementService
                .GetGameAchievementsAsync(record.GameId)
                .ConfigureAwait(true);
            steamSettingsByGameId[record.GameId] = await _db
                .GetGameSteamLaunchSettingsAsync(record.GameId)
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
        string? liveSaveSelectedId = SelectedSaveGame?.GameId;
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
            steamSettingsByGameId.TryGetValue(record.GameId, out var steamSettings);

            if (existingById.TryGetValue(record.GameId, out var existingVm))
            {
                existingVm.UpdateRecord(record);
                existingVm.UpdateSteamLaunchSettings(steamSettings);
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
                vm.UpdateSteamLaunchSettings(steamSettings);
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

        if (!string.IsNullOrWhiteSpace(liveSaveSelectedId))
        {
            var resolvedSaveVm = AllGames.FirstOrDefault(g => string.Equals(g.GameId, liveSaveSelectedId, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(SelectedSaveGame, resolvedSaveVm))
            {
                SelectedSaveGame = resolvedSaveVm;
                if (SelectedSaveGame is null)
                {
                    IsSaveOverviewSelected = true;
                    _ = Saves.LoadForGameAsync(null);
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
        OnPropertyChanged(nameof(CanSyncAllCloudSaves));
        OnPropertyChanged(nameof(LibraryRailToolTip));
        NotifyPrimaryLaunchButtonProperties();

        await RefreshSaveHubStateAsync().ConfigureAwait(true);
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
        bool sameGameId = string.Equals(_loadedAchievementRowsGameId, summary.GameId, StringComparison.OrdinalIgnoreCase);
        bool sameGameAndTitle = sameGameId
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
            if (!sameGameId)
            {
                _achievementRowCache.Clear();
            }

            _loadedAchievementRowsGameId = summary.GameId;
            _loadedAchievementRowsGameTitle = activeGame.Title;
            _currentGameRawAchievements = summary.Items;
            NotifyAchievementTabCountLabels();
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
        var gameVm = ActiveWorkbenchGame;
        if (gameVm is null || _currentGameRawAchievements.Count == 0)
        {
            if (FilteredAchievements.Count > 0)
            {
                FilteredAchievements.Clear();
            }

            return;
        }

        string keyword = AchievementSearchText.Trim();
        var matchingDtos = new List<GameAchievementItemDto>(_currentGameRawAchievements.Count);

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
                bool rowRevealed = _achievementRowCache.TryGetValue(dto.Definition.ApiName, out var existingRow)
                                   && existingRow.IsRowSpoilerRevealed;
                bool nameHit = dto.Definition.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                               || dto.Definition.ApiName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                string visibleDesc = dto.GetEffectiveDescription(RevealHiddenSpoilers || rowRevealed);
                bool descHit = visibleDesc.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                if (!nameHit && !descHit)
                {
                    continue;
                }
            }

            matchingDtos.Add(dto);
        }

        IEnumerable<GameAchievementItemDto> orderedDtos = SelectedAchievementSortMode switch
        {
            AchievementSortMode.UnlockedFirst => matchingDtos
                .OrderByDescending(d => d.IsUnlocked)
                .ThenByDescending(d => d.UnlockedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(d => d.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            AchievementSortMode.UnlockTimeDesc => matchingDtos
                .OrderByDescending(d => d.UnlockedUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(d => d.IsUnlocked)
                .ThenBy(d => d.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            AchievementSortMode.RarityAsc => matchingDtos
                .OrderBy(d => d.Definition.GlobalPercent ?? double.MaxValue)
                .ThenBy(d => d.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            AchievementSortMode.NameAsc => matchingDtos
                .OrderBy(d => d.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => matchingDtos,
        };

        var desiredRows = new List<AchievementRowViewModel>(matchingDtos.Count);
        foreach (var dto in orderedDtos)
        {
            if (_achievementRowCache.TryGetValue(dto.Definition.ApiName, out var cached)
                && string.Equals(cached.GameId, gameVm.GameId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.GameTitle, gameVm.Title, StringComparison.Ordinal)
                && Equals(cached.Item, dto))
            {
                cached.GlobalRevealSpoilers = RevealHiddenSpoilers;
                desiredRows.Add(cached);
            }
            else
            {
                bool preservedSpoilerState = cached is not null
                                             && string.Equals(cached.GameId, gameVm.GameId, StringComparison.OrdinalIgnoreCase)
                                             && cached.IsRowSpoilerRevealed;
                var row = new AchievementRowViewModel(
                    gameVm.GameId,
                    gameVm.Title,
                    dto,
                    RevealHiddenSpoilers,
                    HandleManualSetAchievementUnlockedAsync,
                    HandlePreviewSingleAchievementToastAsync);
                if (preservedSpoilerState)
                {
                    row.IsRowSpoilerRevealed = true;
                }

                _achievementRowCache[dto.Definition.ApiName] = row;
                desiredRows.Add(row);
            }
        }

        SyncCollectionInPlace(FilteredAchievements, desiredRows);
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

        SyncAchievementSidebarPoolFromAllGames();
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
            RebuildFilteredAchievementSidebarGames();
            NotifyGbeForkWorkbenchFlags();
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
                _launchingGameIds.Remove(ev.GameId);
                _steamSyncingGameIds.Remove(ev.GameId);
                _stoppingGameIds.Remove(ev.GameId);
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
                if (ev.CurrentState is PlaySessionActivityState.Active or PlaySessionActivityState.Background or PlaySessionActivityState.Afk)
                {
                    _launchingGameIds.Remove(ev.GameId);
                }

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
        NotifyPrimaryLaunchButtonProperties();
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
        bool anySessionChanged = false;
        foreach (var gameVm in AllGames)
        {
            var live = _launchService.GetActiveSession(gameVm.GameId);
            if (live is not null || gameVm.IsRunning)
            {
                if (!Equals(gameVm.ActiveSession, live))
                {
                    anySessionChanged = true;
                }

                gameVm.UpdateSession(live);
                gameVm.NotifyPlaytimeTick();
            }
        }

        if (anySessionChanged)
        {
            NotifyPrimaryLaunchButtonProperties();
        }

        var snap = _antiCheatGuard.CurrentSnapshot;
        if (snap.Phase != AntiCheatGuardPhase.IdleSafe || GuardPhase != AntiCheatGuardPhase.IdleSafe)
        {
            UpdateGuardBadge(snap);
        }
    }

    private void RebuildGroupsPreservingSelection()
    {
        var sorted = SortGamesRunningFirst(AllGames);

        SyncCollectionInPlace(_allGroup.Items, sorted);
        SyncCollectionInPlace(_gogGroup.Items, sorted.Where(g => g.PlatformSource == PlatformSource.GogOffline).ToList());
        SyncCollectionInPlace(_steamripGroup.Items, sorted.Where(g => g.PlatformSource == PlatformSource.Steamrip).ToList());
        SyncCollectionInPlace(_otherGroup.Items, sorted.Where(g => g.PlatformSource is PlatformSource.SteamLocal or PlatformSource.Standalone).ToList());

        RebuildVisibleLibraryGroups();
        SyncAchievementSidebarPoolFromAllGames();
    }

    private void RebuildVisibleLibraryGroups()
    {
        string keyword = LibrarySidebarSearchText.Trim();
        IEnumerable<GameListItemViewModel> filtered = AllGames;

        if (!string.IsNullOrEmpty(keyword))
        {
            filtered = filtered.Where(g =>
                g.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || g.PlatformFullLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || g.PlatformShortLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        filtered = SelectedLibrarySourceFilter switch
        {
            LibrarySourceFilter.GogOffline => filtered.Where(g => g.PlatformSource == PlatformSource.GogOffline),
            LibrarySourceFilter.Steamrip => filtered.Where(g => g.PlatformSource == PlatformSource.Steamrip),
            LibrarySourceFilter.Other => filtered.Where(g => g.PlatformSource is PlatformSource.SteamLocal or PlatformSource.Standalone),
            _ => filtered,
        };

        List<GameListItemViewModel> ordered = SelectedLibrarySortMode switch
        {
            LibrarySortMode.LastPlayed => filtered
                .OrderByDescending(g => g.IsRunning)
                .ThenByDescending(g => g.Record.LastPlayedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(g => g.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            LibrarySortMode.Playtime => filtered
                .OrderByDescending(g => g.IsRunning)
                .ThenByDescending(g => g.Record.TotalActiveSec + (g.ActiveSession?.SessionActiveSec ?? 0))
                .ThenBy(g => g.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => SortGamesRunningFirst(filtered),
        };

        var desiredGroups = new List<GameSourceGroupViewModel>(3);

        if (SelectedLibrarySourceFilter == LibrarySourceFilter.All && SelectedLibrarySortMode != LibrarySortMode.Name)
        {
            var sortGroup = SelectedLibrarySortMode == LibrarySortMode.LastPlayed
                ? _visibleLastPlayedGroup
                : _visiblePlaytimeGroup;
            SyncCollectionInPlace(sortGroup.Items, ordered);
            if (sortGroup.Items.Count > 0)
            {
                desiredGroups.Add(sortGroup);
            }
        }
        else
        {
            var gogItems = ordered.Where(g => g.PlatformSource == PlatformSource.GogOffline).ToList();
            var steamripItems = ordered.Where(g => g.PlatformSource == PlatformSource.Steamrip).ToList();
            var otherItems = ordered.Where(g => g.PlatformSource is PlatformSource.SteamLocal or PlatformSource.Standalone).ToList();

            SyncCollectionInPlace(_visibleGogGroup.Items, gogItems);
            SyncCollectionInPlace(_visibleSteamripGroup.Items, steamripItems);
            SyncCollectionInPlace(_visibleOtherGroup.Items, otherItems);

            if (_visibleGogGroup.Items.Count > 0)
            {
                desiredGroups.Add(_visibleGogGroup);
            }

            if (_visibleSteamripGroup.Items.Count > 0)
            {
                desiredGroups.Add(_visibleSteamripGroup);
            }

            if (_visibleOtherGroup.Items.Count > 0)
            {
                desiredGroups.Add(_visibleOtherGroup);
            }
        }

        SyncCollectionInPlace(VisibleLibraryGroups, desiredGroups);
        OnPropertyChanged(nameof(HasVisibleLibraryGames));
        OnPropertyChanged(nameof(HasLibrarySearchNoMatches));
    }

    private void SyncAchievementSidebarPoolFromAllGames()
    {
        var existingById = _allAchievementSidebarGames.ToDictionary(x => x.GameId, StringComparer.OrdinalIgnoreCase);
        string? selectedId = IsAchievementsOverviewSelected ? null : SelectedAchievementsGame?.GameId;
        var nextPool = new List<AchievementSidebarItemViewModel>(AllGames.Count);

        foreach (var gameVm in AllGames)
        {
            bool isSelected = !string.IsNullOrWhiteSpace(selectedId)
                              && string.Equals(selectedId, gameVm.GameId, StringComparison.OrdinalIgnoreCase);
            if (existingById.TryGetValue(gameVm.GameId, out var existing) && ReferenceEquals(existing.Game, gameVm))
            {
                existing.IsSelected = isSelected;
                nextPool.Add(existing);
            }
            else
            {
                nextPool.Add(new AchievementSidebarItemViewModel(gameVm, isSelected));
            }
        }

        _allAchievementSidebarGames.Clear();
        _allAchievementSidebarGames.AddRange(nextPool);
        RebuildFilteredAchievementSidebarGames();
    }

    private void RebuildFilteredAchievementSidebarGames()
    {
        string keyword = AchievementSidebarSearchText.Trim();
        IEnumerable<AchievementSidebarItemViewModel> filtered = _allAchievementSidebarGames;

        if (HideZeroAchievementGames)
        {
            filtered = filtered.Where(g => g.TotalAchievements > 0);
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            filtered = filtered.Where(g =>
                g.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || g.Game.PlatformShortLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        List<AchievementSidebarItemViewModel> ordered = SelectedAchievementSidebarSortMode switch
        {
            AchievementSidebarSortMode.UnlockedDesc => filtered
                .OrderByDescending(g => g.UnlockedAchievements)
                .ThenByDescending(g => g.CompletionPercent)
                .ThenBy(g => g.Game.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            AchievementSidebarSortMode.Name => filtered
                .OrderBy(g => g.Game.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => filtered
                .OrderByDescending(g => g.TotalAchievements > 0)
                .ThenByDescending(g => g.CompletionPercent)
                .ThenByDescending(g => g.UnlockedAchievements)
                .ThenBy(g => g.Game.Record.EffectiveSortTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        };

        SyncCollectionInPlace(AchievementSidebarGames, ordered);
        OnPropertyChanged(nameof(HasFilteredAchievementSidebarGames));
    }

    private void SyncAchievementSidebarSelectionFlags()
    {
        string? selectedId = IsAchievementsOverviewSelected ? null : SelectedAchievementsGame?.GameId;
        foreach (var item in _allAchievementSidebarGames)
        {
            item.IsSelected = !string.IsNullOrWhiteSpace(selectedId)
                              && string.Equals(item.GameId, selectedId, StringComparison.OrdinalIgnoreCase);
        }
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
        GuardPhase = snap.Phase;
        GuardCooldownRemainingSeconds = Math.Max(0, snap.RemainingCooldownSeconds);
        IsGuardCooldownOrProtected = snap.Phase != AntiCheatGuardPhase.IdleSafe;

        string residualSummary = snap.DetectedResidualProcesses.Count > 0
            ? $"（检测到：{string.Join("、", snap.DetectedResidualProcesses.Take(3))}）"
            : string.Empty;

        GuardRailToolTip = snap.Phase switch
        {
            AntiCheatGuardPhase.ProtectedRunning => $"安全守卫：受保护网游运行中 · 高敏模块已锁定 ({_paths.ChannelName})",
            AntiCheatGuardPhase.ResidualGuardWatch => $"安全守卫：等待反作弊守护进程完全退出{residualSummary}",
            AntiCheatGuardPhase.CooldownLocked => string.Create(CultureInfo.InvariantCulture, $"安全守卫：15 秒余温冷却锁定中（剩余 {snap.RemainingCooldownSeconds:0.0} 秒）"),
            _ => $"安全守卫：纯净就绪 · 通道 {_paths.ChannelName}",
        };

        GuardStatusHeadline = snap.Phase switch
        {
            AntiCheatGuardPhase.ProtectedRunning => "受保护网游运行中 · 已锁定",
            AntiCheatGuardPhase.ResidualGuardWatch => "检测到反作弊守护残留",
            AntiCheatGuardPhase.CooldownLocked => string.Create(CultureInfo.InvariantCulture, $"余温冷却锁定（剩余 {snap.RemainingCooldownSeconds:0.0} 秒）"),
            _ => "安全守卫纯净就绪",
        };

        GuardStatusDetail = snap.Phase switch
        {
            AntiCheatGuardPhase.ProtectedRunning => "已自动清场并锁定修改器与高敏组件，确保受保护网游零干扰运行。",
            AntiCheatGuardPhase.ResidualGuardWatch => $"正在等待反作弊守护进程完全退出{residualSummary}，随后自动进入 15 秒安全冷却期。",
            AntiCheatGuardPhase.CooldownLocked => string.Create(CultureInfo.InvariantCulture, $"为确保反作弊环境彻底干净，将在 {snap.RemainingCooldownSeconds:0.0} 秒后解除启动与修改器锁定。"),
            _ => "主程序不含敏感内存或钩子接口，当前未检测到受反作弊保护的网游或守护进程。",
        };

        Settings.RefreshAntiCheatSummary(snap);
        Trainer.UpdateGuardSnapshot(snap);
        foreach (var row in ManagedTrainers)
        {
            row.IsGuardLocked = IsGuardCooldownOrProtected;
        }

        OnPropertyChanged(nameof(IsGuardIdle));
        OnPropertyChanged(nameof(IsGuardWarning));
        OnPropertyChanged(nameof(IsGuardProtectedRunning));
        OnPropertyChanged(nameof(TrainerOverviewRuntimeStatusText));
        NotifyPrimaryLaunchButtonProperties();
    }

    private void ShowStatusBanner(string message, bool isError) =>
        ShowStatusBanner(message, isError, autoDismiss: true);

    private void ShowStatusBanner(string message, bool isError, bool autoDismiss)
    {
        _bannerAutoHideCts?.Cancel();
        _bannerAutoHideCts?.Dispose();
        _bannerAutoHideCts = null;

        StatusBannerMessage = message;
        IsStatusBannerError = isError;

        // Only auto-dismiss non-error info banners when autoDismiss is enabled;
        // error banners and active sync progress banners remain visible until dismissed or superseded.
        if (autoDismiss && !string.IsNullOrWhiteSpace(message) && !isError)
        {
            var cts = new CancellationTokenSource();
            _bannerAutoHideCts = cts;
            _ = AutoDismissBannerAsync(cts.Token);
        }
    }

    private async Task HandleTrainerSafetySettingsChangedAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        _trainerSidebarSafetyCache.Clear();
        GameListItemViewModel? activeTrainerTarget = ActiveSection == PrimaryNavSection.Trainers
            ? SelectedTrainerGame
            : (ActiveSection == PrimaryNavSection.Library && SelectedGameSubTab == GameDetailSubTab.Trainers ? SelectedGame : null);
        if (activeTrainerTarget is not null)
        {
            await Trainer.LoadForGameAsync(activeTrainerTarget).ConfigureAwait(true);
        }

        await RefreshTrainerHubStateAsync().ConfigureAwait(true);
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
        Settings.Dispose();
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
        _achievementSearchDebounceCts?.Cancel();
        _achievementSearchDebounceCts?.Dispose();
    }
}
