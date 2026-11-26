using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;
using Spawn.Host.Helpers;
using Spawn.Modules.Progress.Catalog;
using Spawn.Modules.Progress.Runtime;
using Spawn.Modules.Progress.Security;

namespace Spawn.Host.ViewModels;

/// <summary>
/// ViewModel powering the Game Detail <c>修改器</c> sub-tab (<see cref="GameDetailSubTab.Trainers"/>):
/// four-state anti-cheat safety gate, single-primary-button 3-state machine (Unbound / Ready / Running),
/// clean-room FLiNG discovery + Challenge fallback + multi-EXE selection + 225/226 quarantine diagnostics,
/// virtualized hotkey proxy with FreeHosted manual entries, VirusTotal hash link, and pre-trainer save snapshot rollback.
/// </summary>
public sealed partial class TrainerWorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _db;
    private readonly ITrainerCatalogService _catalogService;
    private readonly ITrainerRuntimeService _runtimeService;
    private readonly IPreTrainerSaveSnapshotService _snapshotService;
    private readonly ITrainerSafetyGate _safetyGate;
    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly IGameLibraryService _libraryService;
    private readonly IGameLaunchService _launchService;
    private readonly Action<string> _openExternalUrl;
    private readonly Action<string> _copyToClipboard;
    private readonly Func<string, IReadOnlyList<SafeProcessEntry>> _directoryProcessFinder;
    private readonly ISaveTimeMachineService? _saveTimeMachineService;
    private readonly Dictionary<string, string> _gameTitleCache = new(StringComparer.OrdinalIgnoreCase);

    private GameListItemViewModel? _currentGame;
    private TrainerCandidate? _pendingMultiExeCandidate;
    private string? _pendingMultiExeLocalPath;
    private string? _pendingMultiExeReplaceTrainerId;
    private string? _guardActiveProtectedGameId;
    private IReadOnlyList<string> _guardDetectedResidualProcesses = [];
    private int _loadGeneration;
    private bool _disposed;

    public event Action<string, bool>? StatusFeedbackRequested;
    public event Action? OpenTrainerSettingsRequested;
    public event Func<string, Task>? GameRecordUpdatedRequested;
    public event Func<string, Task>? TrainerCatalogChangedRequested;

    public string? CurrentGameId => _currentGame?.GameId;

    public ObservableCollection<TrainerCatalogRecord> BoundTrainers { get; } = [];
    public ObservableCollection<TrainerCandidateItemViewModel> MatchCandidates { get; } = [];
    public ObservableCollection<TrainerHotkeyRowViewModel> Hotkeys { get; } = [];
    public ObservableCollection<PreTrainerSnapshotItemViewModel> PreTrainerSnapshots { get; } = [];
    public ObservableCollection<TrainerExecutableChoice> PendingExecutableChoices { get; } = [];

    [ObservableProperty]
    private TrainerRuntimeState _runtimeState = TrainerRuntimeState.Unbound;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    private TrainerCatalogRecord? _activeTrainer;

    [ObservableProperty]
    private TrainerAllowanceDecision? _allowanceDecision;

    [ObservableProperty]
    private VerifiedOfflineBypass? _offlineBypassStatus;

    [ObservableProperty]
    private LiveAntiCheatProbeReport? _liveAntiCheatProbe;

    [ObservableProperty]
    private AntiCheatGuardPhase _guardPhase = AntiCheatGuardPhase.IdleSafe;

    [ObservableProperty]
    private double _guardCooldownRemainingSeconds;

    [ObservableProperty]
    private string _guardActiveProtectedGameTitle = string.Empty;

    [ObservableProperty]
    private bool _isVanguardWarningExpanded;

    [ObservableProperty]
    private bool _isOfflinePanelExpanded = true;

    [ObservableProperty]
    private bool _offlineConfirmationChecked;

    [ObservableProperty]
    private bool _isOriginalWindowVisible = true;

    [ObservableProperty]
    private string _searchOverrideText = string.Empty;

    [ObservableProperty]
    private string _effectiveSearchQueryLabel = string.Empty;

    [ObservableProperty]
    private bool _showBrowserFallbackCard;

    [ObservableProperty]
    private string? _fallbackBrowserUrl;

    [ObservableProperty]
    private string _browserFallbackMessage = string.Empty;

    [ObservableProperty]
    private bool _showQuarantineCard;

    [ObservableProperty]
    private string _quarantineDiagnosticMessage = string.Empty;

    [ObservableProperty]
    private bool _isSmartAppControlBlocked;

    [ObservableProperty]
    private bool _showMultiExecutablePicker;

    [ObservableProperty]
    private TrainerExecutableChoice? _selectedExecutableChoice;

    [ObservableProperty]
    private string _gameUpdateDriftNotice = string.Empty;

    [ObservableProperty]
    private string _latestSnapshotNoticeText = string.Empty;

    [ObservableProperty]
    private string _externalGameLaunchNotice = string.Empty;

    [ObservableProperty]
    private string _otherRunningTrainerGameTitle = string.Empty;

    [ObservableProperty]
    private string _newManualHotkeyLabel = string.Empty;

    [ObservableProperty]
    private string _newManualHotkeyChord = "Num 1";

    [ObservableProperty]
    private TrainerHotkeyEntryKind _newManualHotkeyEntryKind = TrainerHotkeyEntryKind.Toggle;

    [ObservableProperty]
    private bool _inheritManualHotkeysOnReplace = true;

    [ObservableProperty]
    private PreTrainerSnapshotItemViewModel? _pendingRollbackSnapshot;

    [ObservableProperty]
    private bool _isRemoveConfirmOpen;

    [ObservableProperty]
    private bool _isSelectedGameRunning;

    [ObservableProperty]
    private double _downloadProgressPercent;

    [ObservableProperty]
    private string _downloadStageText = string.Empty;

    public bool HasSelectedGame => _currentGame is not null;
    public bool HasBoundTrainer => ActiveTrainer is not null;
    public bool HasMultipleBoundTrainers => BoundTrainers.Count > 1;
    public IReadOnlyList<string> GuardDetectedResidualProcesses => _guardDetectedResidualProcesses;
    public string GuardPhaseDisplayText => UiFormatters.FormatAntiCheatGuardPhase(GuardPhase);

    public TrainerCatalogRecord? SelectedTrainer
    {
        get => ActiveTrainer;
        set
        {
            if (value is not null
                && !string.Equals(ActiveTrainer?.TrainerId, value.TrainerId, StringComparison.OrdinalIgnoreCase))
            {
                _ = SelectTrainerAsync(value);
            }
        }
    }

    public bool NewManualHotkeyIsTriggerOnce
    {
        get => NewManualHotkeyEntryKind == TrainerHotkeyEntryKind.Trigger;
        set
        {
            TrainerHotkeyEntryKind next = value ? TrainerHotkeyEntryKind.Trigger : TrainerHotkeyEntryKind.Toggle;
            if (NewManualHotkeyEntryKind != next)
            {
                NewManualHotkeyEntryKind = next;
                OnPropertyChanged();
            }
        }
    }

    public bool IsRollbackConfirmOpen => PendingRollbackSnapshot is not null;

    public string RollbackConfirmPromptText => PendingRollbackSnapshot is null
        ? string.Empty
        : $"确认将存档回滚至 {PendingRollbackSnapshot.CreatedAtText} 的快照（{PendingRollbackSnapshot.MetricsText}）？回滚前会自动创建安全点备份。";

    public string RemoveConfirmPromptText => ActiveTrainer is null
        ? string.Empty
        : $"确认从沙箱移除修改器「{TrainerDisplayTitle}」及其热键配置？已备份的存档快照不会被删除。";

    public bool IsRunningForCurrentGame =>
        _runtimeService.ActiveSession is { } active
        && _currentGame is not null
        && string.Equals(active.GameId, _currentGame.GameId, StringComparison.OrdinalIgnoreCase);

    public bool IsRunningForOtherGame =>
        _runtimeService.ActiveSession is { } active
        && (_currentGame is null
            || !string.Equals(active.GameId, _currentGame.GameId, StringComparison.OrdinalIgnoreCase));

    public string OtherRunningTrainerConflictText => IsRunningForOtherGame
        ? $"游戏「{(string.IsNullOrWhiteSpace(OtherRunningTrainerGameTitle) ? _runtimeService.ActiveSession?.GameId : OtherRunningTrainerGameTitle)}」的修改器正在运行中，同一时间仅支持托管一个修改器会话。"
        : string.Empty;

    public bool HasExternalGameLaunchNotice => !string.IsNullOrWhiteSpace(ExternalGameLaunchNotice);
    public string SelectedExecutableChoiceSummaryText => SelectedExecutableChoice?.DisplaySummary ?? string.Empty;
    public bool CanRedownloadQuarantinedTrainer => ShowQuarantineCard && HasValidExternalSourceUrl && !IsBusy;

    public bool IsGuardLocked => GuardPhase != AntiCheatGuardPhase.IdleSafe;

    public string GuardLockBannerText => GuardPhase switch
    {
        AntiCheatGuardPhase.ProtectedRunning => !string.IsNullOrWhiteSpace(GuardActiveProtectedGameTitle)
            ? $"反作弊守卫已锁定：检测到受保护网游「{GuardActiveProtectedGameTitle}」正在运行，已禁止所有修改器启动与热键代理。"
            : "反作弊守卫已锁定：检测到受保护网游正在运行，已禁止所有修改器启动与热键代理。",
        AntiCheatGuardPhase.ResidualGuardWatch => _guardDetectedResidualProcesses.Count > 0
            ? $"反作弊守卫监测中：检测到残余反作弊进程/内核驱动（{string.Join("、", _guardDetectedResidualProcesses)}），请等待彻底退出后完成 15 秒冷却…"
            : "反作弊守卫监测中：等待反作弊伴生守护进程与会话内核驱动彻底退出…",
        AntiCheatGuardPhase.CooldownLocked =>
            string.Create(CultureInfo.InvariantCulture, $"反作弊 15s 余温冷却中 · 剩余 {GuardCooldownRemainingSeconds:0.0}s · 修改器与热键控件已置灰锁定"),
        _ => string.Empty,
    };

    /// <summary>
    /// True for Tier 1, Tier 3, or Tier 2 without verified single-player capability: physically hides ALL download/launch/hotkey controls.
    /// </summary>
    public bool IsProtectedOnlineGameBlocked =>
        _currentGame is not null
        && (AllowanceDecision?.Outcome == TrainerAllowanceOutcome.BlockedByTier
            || (_currentGame.Record.AntiCheatTier is AntiCheatTier.Tier1 or AntiCheatTier.Tier3));

    public bool IsOfflineSinglePlayerVerificationPending =>
        ShowProtectedFenceCard && AllowanceDecision?.IsOfflineSinglePlayerVerificationPending == true;

    public bool CanRetryOfflineVerification => IsOfflineSinglePlayerVerificationPending && !IsBusy;

    /// <summary>
    /// True for hybrid single-player Tier 2 games eligible for controlled <c>VerifiedOfflineBypass</c>.
    /// </summary>
    public bool IsHybridSinglePlayerGame =>
        _currentGame is not null
        && !IsProtectedOnlineGameBlocked
        && _currentGame.Record.AntiCheatTier == AntiCheatTier.Tier2
        && (OfflineBypassStatus?.IsEligible == true
            || AllowanceDecision?.CanConfirmOfflineBypass == true
            || AllowanceDecision?.UsedVerifiedOfflineBypass == true
            || _currentGame.Record.VerifiedOfflineBypass);

    public bool IsOfflineBypassConfirmed => OfflineBypassStatus?.IsConfirmed == true;
    public bool IsLiveAntiCheatClear => LiveAntiCheatProbe?.IsClearForTrainer ?? (OfflineBypassStatus?.IsLiveAntiCheatClear ?? true);

    public bool ShowProtectedFenceCard => HasSelectedGame && IsProtectedOnlineGameBlocked;
    public bool ShowTrainerWorkspaceControls => HasSelectedGame && !IsProtectedOnlineGameBlocked;
    public bool ShowHybridOfflinePanel => ShowTrainerWorkspaceControls && IsHybridSinglePlayerGame;

    public bool IsHybridExecutableAntiCheatLauncher =>
        ShowHybridOfflinePanel
        && _currentGame is not null
        && AntiCheatFeatureCatalog.IsAntiCheatLauncherExecutable(_currentGame.Record.ExecutableRelPath);

    public string HybridAntiCheatLauncherWarningText => IsHybridExecutableAntiCheatLauncher
        ? $"当前配置启动程序「{Path.GetFileName(_currentGame!.Record.ExecutableRelPath)}」为反作弊引导器：从 SPAWN 直接启动仍会拉起反作弊，请配置或使用离线无 AC 主程序。"
        : string.Empty;

    public bool ShowVanguardIdleWarning =>
        ShowTrainerWorkspaceControls
        && (AllowanceDecision?.IsVanguardIdleWarning == true
            || AllowanceDecision?.Outcome == TrainerAllowanceOutcome.VanguardIdleWarning
            || AllowanceDecision?.Outcome == TrainerAllowanceOutcome.BlockedByVanguardStrictMode
            || LiveAntiCheatProbe?.IsVanguardKernelIdleLoaded == true);

    public bool IsBlockedByVanguardStrictMode =>
        AllowanceDecision?.Outcome == TrainerAllowanceOutcome.BlockedByVanguardStrictMode;

    public string VanguardWarningSummaryText => IsBlockedByVanguardStrictMode
        ? "检测到 Riot Vanguard (vgk.sys) 驱动驻留，且已开启「vgk.sys 严格模式」— 修改器启动已被禁止。"
        : "检测到本机驻留 Riot Vanguard 内核驱动 (vgk.sys 空闲状态，vgc.exe 未活跃) — 建议游玩单机前确认未开启 Valorant/LoL。";

    public string ProtectedFenceTitleText => _currentGame is null
        ? string.Empty
        : (IsOfflineSinglePlayerVerificationPending
            ? $"{_currentGame.AntiCheatBadgeText} · 离线核验保守拦截"
            : $"{_currentGame.AntiCheatBadgeText} · 修改器安全围栏已启用");

    public string ProtectedFenceDescriptionText =>
        AllowanceDecision?.Message
        ?? "该游戏属于受反作弊保护的联网/竞技游戏（Tier 1/3 或无单人模式的 Tier 2）。为防止账号误封，SPAWN 已物理禁用此游戏的修改器检索、下载、启动与热键代理。";

    public string LiveAntiCheatStatusText
    {
        get
        {
            if (LiveAntiCheatProbe is null)
            {
                return IsLiveAntiCheatClear
                    ? "实时 AC 进程与会话驱动：0 活跃（清零就绪）"
                    : "检测到活跃反作弊进程或会话驱动，禁止启动修改器";
            }

            if (LiveAntiCheatProbe.IsClearForTrainer)
            {
                return "实时核验通过：反作弊进程 0 · 会话内核驱动 0（清零就绪）";
            }

            var indicators = LiveAntiCheatProbe.ActiveAntiCheatProcesses
                .Concat(LiveAntiCheatProbe.ActiveSessionAntiCheatDrivers)
                .ToList();
            if (LiveAntiCheatProbe.IsVanguardClientActive)
            {
                indicators.Add("vgc.exe");
            }

            return $"检测到活跃反作弊组件（{string.Join("、", indicators)}），请以离线/无 AC 模式重启游戏后再试。";
        }
    }

    public string HybridOfflineStatusBadgeText =>
        IsOfflineBypassConfirmed && IsLiveAntiCheatClear
            ? "离线无 AC 就绪"
            : (IsOfflineBypassConfirmed ? "等待 AC 清零" : "需确认离线模式");

    // 3-State Single Primary Button Machine
    public string PrimaryActionButtonText => IsRunningForCurrentGame
        ? "运行中 · 停止修改器"
        : (HasBoundTrainer ? "启动修改器" : "匹配修改器");

    public bool IsPrimaryButtonRunningStyle => IsRunningForCurrentGame;
    public bool ShowUnboundSecondaryImportButton => ShowTrainerWorkspaceControls && !HasBoundTrainer && !IsRunningForCurrentGame;
    public bool ShowReadyMoreMenuButton => ShowTrainerWorkspaceControls && HasBoundTrainer;
    public bool CanModifyBoundTrainer => HasBoundTrainer && !IsRunningForCurrentGame && !IsBusy;
    public bool ShowRunningWindowToggleButton => ShowTrainerWorkspaceControls && IsRunningForCurrentGame;

    public string OriginalWindowToggleButtonText => IsOriginalWindowVisible ? "隐藏原窗口" : "显示原窗口";

    public bool CanLaunchOrRunTrainer =>
        ShowTrainerWorkspaceControls
        && !IsGuardLocked
        && !IsBlockedByVanguardStrictMode
        && (!IsHybridSinglePlayerGame || (IsOfflineBypassConfirmed && IsLiveAntiCheatClear));

    public bool IsPrimaryActionButtonEnabled =>
        !IsBusy
        && ShowTrainerWorkspaceControls
        && (IsRunningForCurrentGame
            || (!IsGuardLocked
                && (!HasBoundTrainer || CanLaunchOrRunTrainer)));

    public bool CanTriggerHotkeys => IsRunningForCurrentGame && !IsGuardLocked && !IsBusy;

    public bool IsFreeHostedMode => ActiveTrainer?.ParseMode == TrainerParseMode.FreeHosted;
    public bool HasMatchCandidates => MatchCandidates.Count > 0;
    public bool HasHotkeys => Hotkeys.Count > 0;
    public bool HasPreTrainerSnapshots => PreTrainerSnapshots.Count > 0;
    public bool HasGameUpdateDriftNotice => !string.IsNullOrWhiteSpace(GameUpdateDriftNotice);
    public bool HasLatestSnapshotNotice => !string.IsNullOrWhiteSpace(LatestSnapshotNoticeText);
    public bool IsDownloading => RuntimeState == TrainerRuntimeState.Downloading;

    // Provenance disclosures & active trainer metadata summary
    public string OnlineDiscoveryDisclosureTitle { get; } = TrainerSourceProvenance.OnlineDiscoveryDisclosureTitle;
    public string OnlineDiscoveryDisclosureBody { get; } = TrainerSourceProvenance.OnlineDiscoveryDisclosureBody;
    public string UnboundProvenanceHintText { get; } = TrainerSourceProvenance.UnboundProvenanceHintText;

    public TrainerSourceProvenanceInfo? ActiveTrainerProvenance =>
        ActiveTrainer is null ? null : TrainerSourceProvenance.DescribeRecord(ActiveTrainer);

    public string TrainerDisplayTitle => ActiveTrainer?.DisplayName ?? "未绑定修改器";
    public bool IsActiveTrainerExternalWebSource => ActiveTrainerProvenance?.IsExternalWebsite == true;
    public string TrainerSourceDomainText => ActiveTrainerProvenance?.BadgeText ?? string.Empty;
    public string TrainerSourceHostText => ActiveTrainerProvenance?.SourceHost ?? string.Empty;
    public bool HasValidExternalSourceUrl => ActiveTrainerProvenance?.HasValidHttpsSourceUrl == true;
    public string TrainerSourcePageUrl => ActiveTrainerProvenance?.ValidatedHttpsSourceUrl ?? string.Empty;
    public string TrainerSourcePageDisplayLine => HasValidExternalSourceUrl
        ? $"来源网页：{TrainerSourcePageUrl}"
        : string.Empty;
    public string TrainerSourceCompactOriginText => ActiveTrainerProvenance?.CompactOriginSummary ?? string.Empty;
    public string TrainerSourceProvenanceNoteText => ActiveTrainerProvenance?.DetailedDisclosureText ?? string.Empty;
    public string TrainerVersionAndDateText => ActiveTrainer is null
        ? string.Empty
        : $"{ActiveTrainer.VersionLabel ?? "本地版本"}{(string.IsNullOrWhiteSpace(ActiveTrainer.SourceUpdatedAt) ? string.Empty : $" · {ActiveTrainer.SourceUpdatedAt}")}";
    public string TrainerFileMetaText => ActiveTrainer is null
        ? string.Empty
        : $"{UiFormatters.FormatByteSize(ActiveTrainer.FileSize ?? 0)} · {ActiveTrainer.PeMachine ?? "x64"} · {(ActiveTrainer.RequiresAdmin ? "需管理员 (Level 2 UAC)" : "普通权限 (Level 1)")} · {UiFormatters.FormatTrainerAuthenticodeLabel(ActiveTrainer.AuthenticodeStatus)}";
    public string TrainerPinnedSha256Short => ActiveTrainer is null
        ? string.Empty
        : (ActiveTrainer.FileSha256.Length > 16 ? $"{ActiveTrainer.FileSha256[..16]}…" : ActiveTrainer.FileSha256);
    public string TrainerPinnedSha256Full => ActiveTrainer?.FileSha256 ?? string.Empty;

    public string HotkeyProxyDisclaimerText => IsFreeHostedMode
        ? "自由托管模式：热键代理通过前台模拟按键发送，您可在下方手动添加常用快捷键；列表开关仅显示代理触发状态。"
        : "热键代理通过前台模拟按键发送；若游戏自身绑定了相同键位将同时触发。列表开关仅显示代理触发状态，若与游戏内实际状态不一致，可在右上角 ⋯ 菜单点击「重置开关显示」。";

    public TrainerWorkbenchViewModel(
        SpawnPaths paths,
        SpawnDatabase db,
        ITrainerCatalogService catalogService,
        ITrainerRuntimeService runtimeService,
        IPreTrainerSaveSnapshotService snapshotService,
        ITrainerSafetyGate safetyGate,
        IAntiCheatGuardService antiCheatGuard,
        IGameLibraryService libraryService,
        IGameLaunchService launchService,
        Action<string>? openExternalUrl = null,
        Action<string>? copyToClipboard = null,
        Func<string, IReadOnlyList<SafeProcessEntry>>? directoryProcessFinder = null,
        ISaveTimeMachineService? saveTimeMachineService = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        _antiCheatGuard = antiCheatGuard ?? throw new ArgumentNullException(nameof(antiCheatGuard));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _openExternalUrl = openExternalUrl ?? OpenUrlWithShell;
        _copyToClipboard = copyToClipboard ?? CopyTextToSystemClipboard;
        _directoryProcessFinder = directoryProcessFinder
            ?? (static dir => SafeProcessInspector.FindProcessesUnderDirectory(dir, excludeAuxiliary: true));
        _saveTimeMachineService = saveTimeMachineService;

        UpdateGuardSnapshot(_antiCheatGuard.CurrentSnapshot);
        _runtimeService.StateChanged += OnRuntimeStateChanged;
        _antiCheatGuard.StateChanged += OnAntiCheatGuardStateChanged;
    }

    partial void OnActiveTrainerChanged(TrainerCatalogRecord? value)
    {
        OnPropertyChanged(nameof(SelectedTrainer));
        OnPropertyChanged(nameof(RemoveConfirmPromptText));
    }

    partial void OnPendingRollbackSnapshotChanged(PreTrainerSnapshotItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsRollbackConfirmOpen));
        OnPropertyChanged(nameof(RollbackConfirmPromptText));
    }

    partial void OnSelectedExecutableChoiceChanged(TrainerExecutableChoice? value) =>
        OnPropertyChanged(nameof(SelectedExecutableChoiceSummaryText));

    partial void OnNewManualHotkeyEntryKindChanged(TrainerHotkeyEntryKind value) =>
        OnPropertyChanged(nameof(NewManualHotkeyIsTriggerOnce));

    partial void OnExternalGameLaunchNoticeChanged(string value) =>
        OnPropertyChanged(nameof(HasExternalGameLaunchNotice));

    partial void OnOtherRunningTrainerGameTitleChanged(string value) =>
        OnPropertyChanged(nameof(OtherRunningTrainerConflictText));

    partial void OnShowQuarantineCardChanged(bool value) =>
        OnPropertyChanged(nameof(CanRedownloadQuarantinedTrainer));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanModifyBoundTrainer));
        OnPropertyChanged(nameof(CanRedownloadQuarantinedTrainer));
        OnPropertyChanged(nameof(CanRetryOfflineVerification));
    }

    public Task LoadForGameAsync(GameListItemViewModel? game, CancellationToken ct = default) =>
        LoadForGameAndTrainerAsync(game, preferredTrainerId: null, ct);

    public async Task LoadForGameAndTrainerAsync(
        GameListItemViewModel? game,
        string? preferredTrainerId,
        CancellationToken ct = default)
    {
        int generation = Interlocked.Increment(ref _loadGeneration);
        bool isDifferentGame = !string.Equals(_currentGame?.GameId, game?.GameId, StringComparison.OrdinalIgnoreCase);
        _currentGame = game;

        if (isDifferentGame)
        {
            // Clear stale per-game search override and transient notices whenever switching games.
            SearchOverrideText = string.Empty;
            EffectiveSearchQueryLabel = string.Empty;
            GameUpdateDriftNotice = string.Empty;
            LatestSnapshotNoticeText = string.Empty;
            ExternalGameLaunchNotice = string.Empty;
            MatchCandidates.Clear();
        }

        PendingRollbackSnapshot = null;
        IsRemoveConfirmOpen = false;
        PendingExecutableChoices.Clear();
        ShowMultiExecutablePicker = false;
        ShowBrowserFallbackCard = false;
        ShowQuarantineCard = false;
        IsSmartAppControlBlocked = false;
        _pendingMultiExeCandidate = null;
        _pendingMultiExeLocalPath = null;
        _pendingMultiExeReplaceTrainerId = null;

        if (game is null)
        {
            IsSelectedGameRunning = false;
            ActiveTrainer = null;
            BoundTrainers.Clear();
            Hotkeys.Clear();
            PreTrainerSnapshots.Clear();
            AllowanceDecision = null;
            OfflineBypassStatus = null;
            LiveAntiCheatProbe = null;
            RuntimeState = TrainerRuntimeState.Unbound;
            NotifyAllDerivedStates();
            return;
        }

        _gameTitleCache[game.GameId] = game.Title;
        UpdateGuardSnapshot(_antiCheatGuard.CurrentSnapshot);
        if (GuardPhase == AntiCheatGuardPhase.ProtectedRunning && !string.IsNullOrWhiteSpace(_guardActiveProtectedGameId))
        {
            await ResolveProtectedGameTitleAsync(_guardActiveProtectedGameId, ct).ConfigureAwait(true);
        }

        await RefreshOtherRunningTrainerConflictTitleAsync(ct).ConfigureAwait(true);

        TrainerSettingsDto settings = await _db.GetTrainerSettingsAsync(ct).ConfigureAwait(true);
        if (generation != Volatile.Read(ref _loadGeneration))
        {
            return;
        }

        // Single consolidated safety evaluation (1 live anti-cheat probe instead of 3).
        TrainerGameSafetySnapshot safetySnapshot = await _safetyGate
            .EvaluateGameSafetySnapshotAsync(game.Record, settings.StrictVanguardMode, ct)
            .ConfigureAwait(true);
        if (generation != Volatile.Read(ref _loadGeneration))
        {
            return;
        }

        bool blockedByTier = safetySnapshot.AllowanceDecision.Outcome == TrainerAllowanceOutcome.BlockedByTier
            || game.Record.AntiCheatTier is AntiCheatTier.Tier1 or AntiCheatTier.Tier3;

        IReadOnlyList<TrainerCatalogRecord> loadedTrainers = [];
        TrainerCatalogRecord? selectedTrainer = null;
        IReadOnlyList<TrainerHotkeyBindingRecord> loadedBindings = [];
        IReadOnlyList<MergedSnapshotEntry> loadedSnapshots = [];
        bool gameRunning = false;

        if (!blockedByTier)
        {
            loadedTrainers = await _catalogService.ListForGameAsync(game.GameId, ct).ConfigureAwait(true);
            selectedTrainer = ResolvePreferredTrainer(loadedTrainers, preferredTrainerId);

            if (selectedTrainer is not null)
            {
                loadedBindings = await _catalogService.GetHotkeysAsync(selectedTrainer.TrainerId, ct).ConfigureAwait(true);
            }

            loadedSnapshots = await LoadMergedSnapshotsAsync(game.GameId, ct).ConfigureAwait(true);
            gameRunning = IsGameRunningIncludingExternal(game);

            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }
        }

        // Atomically commit new state and swap observable collections after all async I/O completes (zero flicker).
        AllowanceDecision = safetySnapshot.AllowanceDecision;
        LiveAntiCheatProbe = safetySnapshot.LiveProbe;
        OfflineBypassStatus = safetySnapshot.OfflineBypass;
        OfflineConfirmationChecked = safetySnapshot.OfflineBypass.IsConfirmed;
        IsSelectedGameRunning = gameRunning;

        if (!blockedByTier)
        {
            ActiveTrainer = selectedTrainer;
            ReplaceCollection(BoundTrainers, loadedTrainers);
            ReplaceCollection(
                Hotkeys,
                loadedBindings.OrderBy(h => h.SortOrder).Select(h => CreateHotkeyRow(h)));
            ReplaceCollection(
                PreTrainerSnapshots,
                loadedSnapshots.Select(s => new PreTrainerSnapshotItemViewModel(
                    s.Record,
                    gameRunning,
                    RequestRollbackSnapshotAsync,
                    isTimeMachineSnapshot: s.IsTimeMachine)));
            GameUpdateDriftNotice = ComputeGameUpdateDriftNotice(selectedTrainer, game);
            RuntimeState = ResolveRuntimeState(selectedTrainer);
        }
        else
        {
            ActiveTrainer = null;
            BoundTrainers.Clear();
            Hotkeys.Clear();
            PreTrainerSnapshots.Clear();
            GameUpdateDriftNotice = string.Empty;
            RuntimeState = TrainerRuntimeState.Unbound;
        }

        NotifyAllDerivedStates();
    }

    public async Task SelectTrainerAsync(TrainerCatalogRecord? trainer, CancellationToken ct = default)
    {
        if (trainer is null || _currentGame is null)
        {
            return;
        }

        if (IsRunningForCurrentGame)
        {
            StatusFeedbackRequested?.Invoke("当前修改器正在运行，请先停止后再切换版本", true);
            return;
        }

        TrainerCatalogRecord? match = BoundTrainers.FirstOrDefault(
            t => string.Equals(t.TrainerId, trainer.TrainerId, StringComparison.OrdinalIgnoreCase)) ?? trainer;

        IReadOnlyList<TrainerHotkeyBindingRecord> bindings = await _catalogService.GetHotkeysAsync(match.TrainerId, ct).ConfigureAwait(true);
        ActiveTrainer = match;
        ReplaceCollection(Hotkeys, bindings.OrderBy(h => h.SortOrder).Select(h => CreateHotkeyRow(h)));
        GameUpdateDriftNotice = ComputeGameUpdateDriftNotice(match, _currentGame);
        IsRemoveConfirmOpen = false;
        RuntimeState = ResolveRuntimeState(match);
        NotifyAllDerivedStates();
    }

    public async Task SelectTrainerByIdAsync(string trainerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trainerId) || _currentGame is null)
        {
            return;
        }

        TrainerCatalogRecord? match = BoundTrainers.FirstOrDefault(
            t => string.Equals(t.TrainerId, trainerId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            IReadOnlyList<TrainerCatalogRecord> refreshed = await _catalogService.ListForGameAsync(_currentGame.GameId, ct).ConfigureAwait(true);
            ReplaceCollection(BoundTrainers, refreshed);
            match = refreshed.FirstOrDefault(t => string.Equals(t.TrainerId, trainerId, StringComparison.OrdinalIgnoreCase));
        }

        if (match is not null)
        {
            await SelectTrainerAsync(match, ct).ConfigureAwait(true);
        }
    }

    public void UpdateSelectedGameRunningState(bool isRunning)
    {
        IsSelectedGameRunning = isRunning;
        foreach (var snap in PreTrainerSnapshots)
        {
            snap.IsGameRunning = isRunning;
        }
    }

    public void SyncOriginalWindowVisibility(bool visible)
    {
        IsOriginalWindowVisible = visible;
        OnPropertyChanged(nameof(OriginalWindowToggleButtonText));
    }

    public Task RefreshSnapshotsForCurrentGameAsync(CancellationToken ct = default) =>
        _currentGame is not null
            ? RefreshSnapshotsOnlyAsync(_currentGame.GameId, ct)
            : Task.CompletedTask;

    public async Task ReloadCurrentGameIfMatchesAsync(string gameId, CancellationToken ct = default)
    {
        if (_currentGame is null || !string.Equals(_currentGame.GameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsProtectedOnlineGameBlocked)
        {
            await RefreshBoundTrainerAndSnapshotsAsync(_currentGame.GameId, ActiveTrainer?.TrainerId, ct).ConfigureAwait(true);
        }

        NotifyAllDerivedStates();
    }

    public void UpdateGuardSnapshot(AntiCheatGuardSnapshot snapshot)
    {
        GuardPhase = snapshot.Phase;
        GuardCooldownRemainingSeconds = snapshot.RemainingCooldownSeconds;
        _guardDetectedResidualProcesses = snapshot.DetectedResidualProcesses ?? [];
        _guardActiveProtectedGameId = snapshot.ActiveProtectedGameId;

        if (snapshot.Phase == AntiCheatGuardPhase.ProtectedRunning && !string.IsNullOrWhiteSpace(snapshot.ActiveProtectedGameId))
        {
            string resolved = TryResolveGameTitleSynchronously(snapshot.ActiveProtectedGameId);
            GuardActiveProtectedGameTitle = resolved;
            if (!_gameTitleCache.ContainsKey(snapshot.ActiveProtectedGameId))
            {
                _ = ResolveProtectedGameTitleAsync(snapshot.ActiveProtectedGameId, CancellationToken.None);
            }
        }
        else
        {
            GuardActiveProtectedGameTitle = string.Empty;
        }

        OnPropertyChanged(nameof(IsGuardLocked));
        OnPropertyChanged(nameof(GuardPhaseDisplayText));
        OnPropertyChanged(nameof(GuardDetectedResidualProcesses));
        OnPropertyChanged(nameof(GuardLockBannerText));
        OnPropertyChanged(nameof(CanLaunchOrRunTrainer));
        OnPropertyChanged(nameof(IsPrimaryActionButtonEnabled));
        OnPropertyChanged(nameof(CanTriggerHotkeys));
    }

    [RelayCommand]
    public async Task RecheckGuardStatusAsync()
    {
        AntiCheatGuardSnapshot snapshot = _antiCheatGuard is AntiCheatGuardService concreteGuard
            ? concreteGuard.RecheckLiveResiduals()
            : _antiCheatGuard.CurrentSnapshot;

        UpdateGuardSnapshot(snapshot);
        if (snapshot.Phase == AntiCheatGuardPhase.ProtectedRunning && !string.IsNullOrWhiteSpace(snapshot.ActiveProtectedGameId))
        {
            await ResolveProtectedGameTitleAsync(snapshot.ActiveProtectedGameId, CancellationToken.None).ConfigureAwait(true);
        }

        if (_currentGame is not null)
        {
            await RefreshLiveAntiCheatProbeAsync().ConfigureAwait(true);
            return;
        }

        StatusFeedbackRequested?.Invoke(
            IsGuardLocked ? GuardLockBannerText : "反作弊守卫重检完成：当前处于安全空闲状态。",
            IsGuardLocked);
    }

    [RelayCommand]
    public async Task RetrySafetyVerificationAsync()
    {
        if (_currentGame is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在重新核验单人模式资格与实时反作弊状态…";
        NotifyAllDerivedStates();
        try
        {
            await LoadForGameAndTrainerAsync(_currentGame, ActiveTrainer?.TrainerId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(
                IsProtectedOnlineGameBlocked
                    ? ProtectedFenceDescriptionText
                    : "单人模式与安全状态重新核验完成。",
                IsProtectedOnlineGameBlocked);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    private void ToggleVanguardWarning() => IsVanguardWarningExpanded = !IsVanguardWarningExpanded;

    [RelayCommand]
    private void ToggleOfflinePanel() => IsOfflinePanelExpanded = !IsOfflinePanelExpanded;

    [RelayCommand]
    private void OpenTrainerSettings() => OpenTrainerSettingsRequested?.Invoke();

    [RelayCommand]
    private async Task RefreshLiveAntiCheatProbeAsync()
    {
        if (_currentGame is null)
        {
            return;
        }

        TrainerSettingsDto settings = await _db.GetTrainerSettingsAsync().ConfigureAwait(true);
        TrainerGameSafetySnapshot safety = await _safetyGate
            .EvaluateGameSafetySnapshotAsync(_currentGame.Record, settings.StrictVanguardMode)
            .ConfigureAwait(true);
        LiveAntiCheatProbe = safety.LiveProbe;
        OfflineBypassStatus = safety.OfflineBypass;
        AllowanceDecision = safety.AllowanceDecision;
        OfflineConfirmationChecked = safety.OfflineBypass.IsConfirmed;
        NotifyAllDerivedStates();
        StatusFeedbackRequested?.Invoke(LiveAntiCheatStatusText, !IsLiveAntiCheatClear);
    }

    [RelayCommand]
    public async Task ConfirmOfflineBypassAsync()
    {
        if (_currentGame is null || !IsHybridSinglePlayerGame)
        {
            return;
        }

        await _libraryService.UpdateGameMetadataAsync(
            _currentGame.GameId,
            new GameMetadataUpdate
            {
                VerifiedOfflineBypass = true,
                VerifiedOfflineBypassAtUtc = DateTimeOffset.UtcNow,
            }).ConfigureAwait(true);

        var refreshed = await _libraryService.GetGameByIdAsync(_currentGame.GameId).ConfigureAwait(true);
        if (refreshed is not null)
        {
            _currentGame.UpdateRecord(refreshed);
        }

        if (GameRecordUpdatedRequested is not null)
        {
            await GameRecordUpdatedRequested(_currentGame.GameId).ConfigureAwait(true);
        }

        await RefreshLiveAntiCheatProbeAsync().ConfigureAwait(true);
        StatusFeedbackRequested?.Invoke("已记录离线无 AC 模式确认（静态安全分级不变，启动修改器前仍会实时校验零反作弊）", false);
    }

    [RelayCommand]
    public async Task RevokeOfflineBypassAsync()
    {
        if (_currentGame is null)
        {
            return;
        }

        await _libraryService.UpdateGameMetadataAsync(
            _currentGame.GameId,
            new GameMetadataUpdate
            {
                VerifiedOfflineBypass = false,
            }).ConfigureAwait(true);

        var refreshed = await _libraryService.GetGameByIdAsync(_currentGame.GameId).ConfigureAwait(true);
        if (refreshed is not null)
        {
            _currentGame.UpdateRecord(refreshed);
        }

        if (GameRecordUpdatedRequested is not null)
        {
            await GameRecordUpdatedRequested(_currentGame.GameId).ConfigureAwait(true);
        }

        await RefreshLiveAntiCheatProbeAsync().ConfigureAwait(true);
        StatusFeedbackRequested?.Invoke("已撤销该游戏的离线无 AC 放行确认", false);
    }

    [RelayCommand]
    public async Task ExecutePrimaryActionAsync()
    {
        if (IsRunningForCurrentGame)
        {
            await StopTrainerAsync().ConfigureAwait(true);
        }
        else if (HasBoundTrainer)
        {
            await LaunchTrainerAsync().ConfigureAwait(true);
        }
        else
        {
            await MatchTrainersAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task MatchTrainersAsync()
    {
        if (_currentGame is null || IsProtectedOnlineGameBlocked || IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在按英文标准名检索第三方外部网站 FLiNG Trainer (flingtrainer.com)…";
        RuntimeState = TrainerRuntimeState.Matching;
        ShowBrowserFallbackCard = false;
        ShowQuarantineCard = false;
        NotifyAllDerivedStates();

        try
        {
            string? overrideQuery = string.IsNullOrWhiteSpace(SearchOverrideText) ? null : SearchOverrideText.Trim();
            TrainerMatchResult result = await _catalogService.MatchAsync(_currentGame.GameId, overrideQuery).ConfigureAwait(true);
            EffectiveSearchQueryLabel = string.IsNullOrWhiteSpace(result.EffectiveQuery)
                ? string.Empty
                : $"检索词：{result.EffectiveQuery}（来源：{UiFormatters.FormatTrainerTitleSource(result.QuerySource)}）";

            MatchCandidates.Clear();
            foreach (TrainerCandidate cand in result.Candidates)
            {
                MatchCandidates.Add(new TrainerCandidateItemViewModel(
                    cand,
                    LoadCandidateDetailsAsync,
                    DownloadCandidateAsync,
                    OpenExternalUrlSafe,
                    CopyCandidatePageUrlSafe));
            }

            if (result.FallbackToBrowserRecommended)
            {
                RuntimeState = TrainerRuntimeState.FallbackBrowser;
                ShowBrowserFallbackCard = true;
                FallbackBrowserUrl = result.FallbackBrowserUrl ?? "https://flingtrainer.com/";
                BrowserFallbackMessage = result.Message ?? "外部源站触发人机验证（403/Challenge），已停止自动请求。请在默认浏览器打开该外部网页下载后直接拖放至本窗口导入。";
                StatusFeedbackRequested?.Invoke(BrowserFallbackMessage, true);
            }
            else
            {
                RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
                StatusFeedbackRequested?.Invoke(
                    result.Candidates.Count > 0
                        ? $"已在第三方外部网站检索到 {result.Candidates.Count} 个候选条目，请核对来源后查看版本或下载"
                        : (result.Message ?? "未匹配到候选修改器，可调整英文搜索词或直接手动导入本机修改器文件"),
                    result.Candidates.Count == 0);
            }
        }
        catch (Exception ex)
        {
            RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
            StatusFeedbackRequested?.Invoke($"匹配修改器失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    private async Task ImportLocalTrainerAsync()
    {
        if (_currentGame is null || IsProtectedOnlineGameBlocked || IsBusy)
        {
            return;
        }

        if (IsRunningForCurrentGame)
        {
            StatusFeedbackRequested?.Invoke("修改器正在运行中，请先停止后再执行导入或覆盖。", true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = HasBoundTrainer ? "选择本地修改器文件以覆盖当前版本" : "选择本地外部修改器可执行文件或压缩包",
            Filter = "修改器文件 (*.exe;*.zip;*.rar;*.7z)|*.exe;*.zip;*.rar;*.7z|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        if (HasBoundTrainer)
        {
            await ImportReplaceTrainerPathAsync(dialog.FileName, ActiveTrainer!.TrainerId, InheritManualHotkeysOnReplace).ConfigureAwait(true);
        }
        else
        {
            await ImportLocalTrainerPathAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ImportReplaceTrainerAsync()
    {
        if (_currentGame is null || IsProtectedOnlineGameBlocked || IsBusy)
        {
            return;
        }

        if (IsRunningForCurrentGame)
        {
            StatusFeedbackRequested?.Invoke("修改器正在运行中，请先停止后再执行覆盖导入。", true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择新的本地修改器可执行文件或压缩包以覆盖当前绑定",
            Filter = "修改器文件 (*.exe;*.zip;*.rar;*.7z)|*.exe;*.zip;*.rar;*.7z|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        await ImportReplaceTrainerPathAsync(dialog.FileName, ActiveTrainer?.TrainerId, InheritManualHotkeysOnReplace).ConfigureAwait(true);
    }

    public async Task ImportLocalTrainerPathAsync(string localFilePath, string? selectedExecutableRelPath = null)
    {
        if (_currentGame is null || IsProtectedOnlineGameBlocked || IsBusy || string.IsNullOrWhiteSpace(localFilePath))
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在校验魔数、安全解压并钉扎 SHA-256…";
        RuntimeState = TrainerRuntimeState.Importing;
        ShowBrowserFallbackCard = false;
        ShowQuarantineCard = false;
        ShowMultiExecutablePicker = false;
        NotifyAllDerivedStates();

        try
        {
            TrainerImportResult result = string.IsNullOrWhiteSpace(selectedExecutableRelPath)
                ? await _catalogService.ImportLocalAsync(_currentGame.GameId, localFilePath).ConfigureAwait(true)
                : await _catalogService.ImportLocalWithExecutableSelectionAsync(_currentGame.GameId, localFilePath, selectedExecutableRelPath).ConfigureAwait(true);

            await HandleImportOutcomeAsync(result, pendingCandidate: null, pendingLocalPath: localFilePath, pendingReplaceTrainerId: null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
            StatusFeedbackRequested?.Invoke($"本地导入失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    public async Task ImportReplaceTrainerPathAsync(
        string localFilePath,
        string? replaceTrainerId = null,
        bool? inheritManualHotkeys = null,
        string? selectedExecutableRelPath = null)
    {
        if (_currentGame is null || IsProtectedOnlineGameBlocked || IsBusy || string.IsNullOrWhiteSpace(localFilePath))
        {
            return;
        }

        if (IsRunningForCurrentGame)
        {
            StatusFeedbackRequested?.Invoke("修改器正在运行中，请先停止后再覆盖当前修改器。", true);
            return;
        }

        string? targetReplaceId = string.IsNullOrWhiteSpace(replaceTrainerId)
            ? ActiveTrainer?.TrainerId
            : replaceTrainerId;
        bool shouldInheritManual = inheritManualHotkeys ?? InheritManualHotkeysOnReplace;

        IsBusy = true;
        BusyMessage = "正在校验新修改器、安全替换旧版本并钉扎 SHA-256…";
        RuntimeState = TrainerRuntimeState.Importing;
        ShowBrowserFallbackCard = false;
        ShowQuarantineCard = false;
        ShowMultiExecutablePicker = false;
        NotifyAllDerivedStates();

        try
        {
            TrainerImportResult result = await _catalogService
                .ImportLocalReplacingAsync(
                    _currentGame.GameId,
                    localFilePath,
                    targetReplaceId,
                    shouldInheritManual,
                    selectedExecutableRelPath)
                .ConfigureAwait(true);

            await HandleImportOutcomeAsync(result, pendingCandidate: null, pendingLocalPath: localFilePath, pendingReplaceTrainerId: targetReplaceId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
            StatusFeedbackRequested?.Invoke($"覆盖导入修改器失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    private async Task ConfirmExecutableChoiceAsync()
    {
        if (_currentGame is null || SelectedExecutableChoice is null || IsBusy)
        {
            return;
        }

        string chosenRelPath = SelectedExecutableChoice.RelativePath;
        TrainerCandidate? cand = _pendingMultiExeCandidate;
        string? localPath = _pendingMultiExeLocalPath;
        string? replaceTrainerId = _pendingMultiExeReplaceTrainerId;
        ShowMultiExecutablePicker = false;
        PendingExecutableChoices.Clear();
        _pendingMultiExeCandidate = null;
        _pendingMultiExeLocalPath = null;
        _pendingMultiExeReplaceTrainerId = null;

        if (cand is not null)
        {
            await DownloadCandidateCoreAsync(cand, chosenRelPath).ConfigureAwait(true);
        }
        else if (!string.IsNullOrWhiteSpace(localPath))
        {
            if (!string.IsNullOrWhiteSpace(replaceTrainerId))
            {
                await ImportReplaceTrainerPathAsync(localPath, replaceTrainerId, InheritManualHotkeysOnReplace, chosenRelPath).ConfigureAwait(true);
            }
            else
            {
                await ImportLocalTrainerPathAsync(localPath, chosenRelPath).ConfigureAwait(true);
            }
        }
    }

    [RelayCommand]
    private void CancelExecutableChoice()
    {
        ShowMultiExecutablePicker = false;
        PendingExecutableChoices.Clear();
        _pendingMultiExeCandidate = null;
        _pendingMultiExeLocalPath = null;
        _pendingMultiExeReplaceTrainerId = null;
        RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
        NotifyAllDerivedStates();
    }

    [RelayCommand]
    public async Task LaunchTrainerAsync()
    {
        if (_currentGame is null || ActiveTrainer is null || IsBusy || IsProtectedOnlineGameBlocked)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在执行反作弊硬验票、启动前存档快照与沙箱托管…";
        ShowQuarantineCard = false;
        ExternalGameLaunchNotice = string.Empty;
        NotifyAllDerivedStates();

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

            TrainerLaunchOutcome outcome = await _runtimeService.LaunchAsync(ActiveTrainer.TrainerId, options).ConfigureAwait(true);

            if (outcome.AllowanceDecision is not null)
            {
                AllowanceDecision = outcome.AllowanceDecision;
            }

            if (outcome.PreSnapshot is not null)
            {
                string skipLabel = UiFormatters.FormatPreTrainerSnapshotSkipReason(outcome.PreSnapshot.SkipReason);
                LatestSnapshotNoticeText = outcome.PreSnapshot.Status switch
                {
                    PreTrainerSnapshotStatus.Completed => !string.IsNullOrWhiteSpace(outcome.PreSnapshot.Message)
                        ? outcome.PreSnapshot.Message!
                        : $"启动前已自动备份存档（{outcome.PreSnapshot.Snapshot?.FileCount ?? 0} 个文件）",
                    PreTrainerSnapshotStatus.Skipped => $"启动前快照已跳过（不阻断启动）：{outcome.PreSnapshot.Message ?? (string.IsNullOrWhiteSpace(skipLabel) ? "已跳过" : skipLabel)}",
                    _ => $"启动前快照未完成（不阻断启动）：{outcome.PreSnapshot.Message ?? "备份异常"}",
                };
                await RefreshSnapshotsOnlyAsync(_currentGame.GameId).ConfigureAwait(true);
            }

            if (outcome.IntegrityReport is { GameFilesChangedSinceBinding: true })
            {
                GameUpdateDriftNotice = outcome.IntegrityReport.GameUpdateNotice
                    ?? "游戏文件相对绑定修改器时已发生变化，如热键失效建议检查新版修改器。";
            }

            if (!outcome.Succeeded)
            {
                if (outcome.FailureKind is TrainerLaunchFailureKind.AntivirusQuarantined or TrainerLaunchFailureKind.SmartAppControlBlocked)
                {
                    RuntimeState = TrainerRuntimeState.Quarantined;
                    ShowQuarantineCard = true;
                    IsSmartAppControlBlocked = outcome.FailureKind == TrainerLaunchFailureKind.SmartAppControlBlocked;
                    QuarantineDiagnosticMessage = outcome.Message ?? "修改器文件已被杀软隔离或受 Smart App Control 策略拦截。";
                }
                else
                {
                    RuntimeState = TrainerRuntimeState.Ready;
                }

                await RefreshOtherRunningTrainerConflictTitleAsync().ConfigureAwait(true);
                StatusFeedbackRequested?.Invoke(outcome.Message ?? "启动修改器未成功", outcome.FailureKind != TrainerLaunchFailureKind.UacCancelledByUser);
                return;
            }

            IsOriginalWindowVisible = !options.HideOriginalWindow;
            ExternalGameLaunchNotice = outcome.ExternalLaunchNotice ?? string.Empty;
            OtherRunningTrainerGameTitle = string.Empty;
            RuntimeState = TrainerRuntimeState.Running;
            await NotifyTrainerCatalogChangedAsync(_currentGame.GameId).ConfigureAwait(true);
            string elevationText = UiFormatters.FormatTrainerElevationLevel(outcome.Session?.ElevationLevel);
            string externalSuffix = outcome.IsTargetGameLaunchedExternally ? " · 检测到游戏非 SPAWN 启动，退出游戏后请手动停止" : string.Empty;
            StatusFeedbackRequested?.Invoke(
                $"修改器已在隔离沙箱中运行（{elevationText} · 已启用零注入静音与防残留作业对象{externalSuffix}）",
                false);
        }
        catch (Exception ex)
        {
            RuntimeState = TrainerRuntimeState.Ready;
            StatusFeedbackRequested?.Invoke($"启动修改器失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task StopOtherAndLaunchCurrentTrainerAsync()
    {
        if (_currentGame is null || ActiveTrainer is null || IsBusy || IsProtectedOnlineGameBlocked)
        {
            return;
        }

        if (_runtimeService.ActiveSession is { } otherSession
            && !string.Equals(otherSession.GameId, _currentGame.GameId, StringComparison.OrdinalIgnoreCase))
        {
            IsBusy = true;
            BusyMessage = "正在停止另一个游戏的修改器会话…";
            NotifyAllDerivedStates();
            try
            {
                await _runtimeService.StopAsync(otherSession.SessionId).ConfigureAwait(true);
                OtherRunningTrainerGameTitle = string.Empty;
                await NotifyTrainerCatalogChangedAsync(otherSession.GameId).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusFeedbackRequested?.Invoke($"停止其他修改器失败：{ex.Message}", true);
                return;
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
                NotifyAllDerivedStates();
            }
        }

        await LaunchTrainerAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task StopTrainerAsync()
    {
        if (_runtimeService.ActiveSession is not { } session)
        {
            ResetInMemoryHotkeyProxyStates();
            ExternalGameLaunchNotice = string.Empty;
            RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
            NotifyAllDerivedStates();
            return;
        }

        IsBusy = true;
        BusyMessage = "正在优雅关闭修改器并清场隔离作业对象…";
        NotifyAllDerivedStates();

        try
        {
            await _runtimeService.StopAsync(session.SessionId).ConfigureAwait(true);
            ResetInMemoryHotkeyProxyStates();
            ExternalGameLaunchNotice = string.Empty;
            OtherRunningTrainerGameTitle = string.Empty;
            RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
            await NotifyTrainerCatalogChangedAsync(session.GameId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke("修改器会话已停止，沙箱进程树已清零", false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"停止修改器时出现异常：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task ToggleOriginalWindowVisibilityAsync()
    {
        if (_runtimeService.ActiveSession is not { } session)
        {
            return;
        }

        bool nextVisible = !IsOriginalWindowVisible;
        try
        {
            TrainerSetWindowVisibilityResponse reply = await _runtimeService
                .SetOriginalWindowVisibleWithResultAsync(session.SessionId, nextVisible)
                .ConfigureAwait(true);

            if (!reply.Succeeded || reply.AffectedWindowCount <= 0)
            {
                StatusFeedbackRequested?.Invoke(
                    reply.ErrorMessage ?? "未检测到可切换显隐的修改器顶层窗口（可能尚在初始化或无可见窗口），窗口状态未变更。",
                    true);
                return;
            }

            IsOriginalWindowVisible = nextVisible;
            OnPropertyChanged(nameof(OriginalWindowToggleButtonText));
            StatusFeedbackRequested?.Invoke(
                nextVisible
                    ? $"已显示修改器原窗口（{reply.AffectedWindowCount} 个窗口）"
                    : $"已隐藏修改器原窗口（{reply.AffectedWindowCount} 个窗口）",
                false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"切换修改器窗口显隐失败：{ex.Message}", true);
        }
    }

    [RelayCommand]
    private async Task OpenTrainerSandboxFolderAsync()
    {
        try
        {
            string targetDir = await ResolveActiveTrainerDirectoryAsync().ConfigureAwait(true);
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
            StatusFeedbackRequested?.Invoke($"打开沙箱目录失败：{ex.Message}", true);
        }
    }

    [RelayCommand]
    public async Task VerifyTrainerIntegrityAsync()
    {
        if (ActiveTrainer is null)
        {
            return;
        }

        TrainerIntegrityReport report = await _catalogService.VerifyAsync(ActiveTrainer.TrainerId).ConfigureAwait(true);
        if (report.GameFilesChangedSinceBinding && !string.IsNullOrWhiteSpace(report.GameUpdateNotice))
        {
            GameUpdateDriftNotice = report.GameUpdateNotice!;
            OnPropertyChanged(nameof(HasGameUpdateDriftNotice));
        }

        if (report.IsValid)
        {
            StatusFeedbackRequested?.Invoke($"SHA-256 完整性校验通过：{report.ActualSha256?[..16]}…", false);
        }
        else
        {
            if (report.Win32ErrorCode is 225 or 226 || !report.FileExists)
            {
                ShowQuarantineCard = true;
                QuarantineDiagnosticMessage = report.FailureMessage ?? "沙箱内的修改器主程序缺失或被安全软件隔离。";
            }

            StatusFeedbackRequested?.Invoke(report.FailureMessage ?? "SHA-256 校验未通过，禁止启动该文件", true);
        }

        NotifyAllDerivedStates();
    }

    [RelayCommand]
    public async Task ResetHotkeyProxyStatesAsync()
    {
        if (ActiveTrainer is null)
        {
            return;
        }

        await _db.ResetTrainerHotkeyProxyStatesAsync(ActiveTrainer.TrainerId).ConfigureAwait(true);
        foreach (var row in Hotkeys)
        {
            row.ProxyState = false;
        }

        StatusFeedbackRequested?.Invoke("已重置所有热键代理开关显示状态", false);
    }

    [RelayCommand]
    public void OpenVirusTotalHash()
    {
        if (ActiveTrainer is null || string.IsNullOrWhiteSpace(ActiveTrainer.FileSha256))
        {
            return;
        }

        // Strict privacy rule: ONLY open the SHA-256 lookup URL on explicit user click; NEVER upload any file.
        string url = UiFormatters.BuildVirusTotalFileUrl(ActiveTrainer.FileSha256);
        OpenExternalUrlSafe(url);
        StatusFeedbackRequested?.Invoke("已在默认浏览器打开 VirusTotal 哈希查询页面（未上传任何本地文件）", false);
    }

    [RelayCommand]
    public void OpenActiveTrainerSourcePage()
    {
        if (ActiveTrainer is null
            || !TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(ActiveTrainer.SourcePageUrl, out string safeHttpsUrl, out string host))
        {
            return;
        }

        OpenExternalUrlSafe(safeHttpsUrl);
        StatusFeedbackRequested?.Invoke($"已在默认浏览器打开修改器外部来源网页（{host}）", false);
    }

    [RelayCommand]
    public void CopyActiveTrainerSourceUrl()
    {
        if (ActiveTrainer is null
            || !TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(ActiveTrainer.SourcePageUrl, out string safeHttpsUrl, out string host))
        {
            return;
        }

        CopyTextSafe(safeHttpsUrl, $"已复制修改器外部来源网页链接（{host}）");
    }

    [RelayCommand]
    public void OpenFallbackBrowser()
    {
        if (TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(FallbackBrowserUrl, out string safeHttpsUrl, out _))
        {
            OpenExternalUrlSafe(safeHttpsUrl);
        }
    }

    [RelayCommand]
    public void OpenSmartAppControlDocs() =>
        OpenExternalUrlSafe(DefenderExclusionAssistant.SmartAppControlLearnMoreUrl);

    [RelayCommand]
    public async Task RedownloadQuarantinedTrainerAsync()
    {
        if (_currentGame is null || IsProtectedOnlineGameBlocked || IsBusy)
        {
            return;
        }

        if (ActiveTrainer is not null
            && TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(ActiveTrainer.SourcePageUrl, out string safeHttpsUrl, out _))
        {
            var candidate = new TrainerCandidate
            {
                CandidateId = ActiveTrainer.TrainerId,
                Provider = ActiveTrainer.Provider == TrainerProviderKind.FlingArchive
                    ? TrainerProviderKind.FlingArchive
                    : TrainerProviderKind.FlingMain,
                Title = ActiveTrainer.DisplayName ?? _currentGame.Title,
                PageUrl = safeHttpsUrl,
                VersionLabel = ActiveTrainer.VersionLabel,
                UpdatedDateText = ActiveTrainer.SourceUpdatedAt,
            };

            TrainerCandidateDetailsResult details = await _catalogService
                .LoadCandidateDetailsWithDiagnosticsAsync(_currentGame.GameId, candidate)
                .ConfigureAwait(true);
            if (details.ChallengeDetected)
            {
                RuntimeState = TrainerRuntimeState.FallbackBrowser;
                ShowBrowserFallbackCard = true;
                FallbackBrowserUrl = details.FallbackBrowserUrl ?? safeHttpsUrl;
                BrowserFallbackMessage = details.ErrorMessage ?? "外部源站触发人机验证（403/Challenge），请在默认浏览器重新下载后拖放导入。";
                StatusFeedbackRequested?.Invoke(BrowserFallbackMessage, true);
                NotifyAllDerivedStates();
                return;
            }

            if (details.Candidate.Attachments.Count > 0)
            {
                await DownloadCandidateCoreAsync(details.Candidate, selectedExecutableRelPath: null).ConfigureAwait(true);
                return;
            }
        }

        await MatchTrainersAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public Task ReimportQuarantinedTrainerAsync() =>
        HasBoundTrainer ? ImportReplaceTrainerAsync() : ImportLocalTrainerAsync();

    [RelayCommand]
    public void RequestRemoveTrainer()
    {
        if (ActiveTrainer is null || _currentGame is null)
        {
            return;
        }

        IsRemoveConfirmOpen = true;
        OnPropertyChanged(nameof(RemoveConfirmPromptText));
        StatusFeedbackRequested?.Invoke($"{RemoveConfirmPromptText} 请点击确认或再次点击「移除」执行。", false);
    }

    [RelayCommand]
    public void CancelRemoveTrainer()
    {
        IsRemoveConfirmOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmRemoveTrainerAsync()
    {
        if (ActiveTrainer is null || _currentGame is null)
        {
            IsRemoveConfirmOpen = false;
            return;
        }

        IsRemoveConfirmOpen = false;
        if (IsRunningForCurrentGame)
        {
            await StopTrainerAsync().ConfigureAwait(true);
        }

        string trainerId = ActiveTrainer.TrainerId;
        string gameId = _currentGame.GameId;
        await _catalogService.RemoveAsync(trainerId).ConfigureAwait(true);
        GameUpdateDriftNotice = string.Empty;
        ShowQuarantineCard = false;
        await RefreshBoundTrainerAndSnapshotsAsync(gameId, preferredTrainerId: null, CancellationToken.None).ConfigureAwait(true);
        NotifyAllDerivedStates();
        await NotifyTrainerCatalogChangedAsync(gameId).ConfigureAwait(true);
        StatusFeedbackRequested?.Invoke("已从沙箱移除该修改器及其目录记录", false);
    }

    [RelayCommand]
    public Task RemoveTrainerAsync()
    {
        if (ActiveTrainer is null || _currentGame is null)
        {
            return Task.CompletedTask;
        }

        if (!IsRemoveConfirmOpen)
        {
            RequestRemoveTrainer();
            return Task.CompletedTask;
        }

        return ConfirmRemoveTrainerAsync();
    }

    [RelayCommand]
    public async Task AddManualHotkeyAsync()
    {
        if (ActiveTrainer is null)
        {
            return;
        }

        string label = NewManualHotkeyLabel.Trim();
        string chord = NewManualHotkeyChord.Trim();
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(chord))
        {
            StatusFeedbackRequested?.Invoke("请输入功能名称与目标热键（例如 Num 1、F1 或 Ctrl+F2）", true);
            return;
        }

        if (!HotkeyChordParser.TryParse(chord, out _))
        {
            StatusFeedbackRequested?.Invoke(
                $"无法识别热键「{chord}」，未保存。请使用规范格式，例如：Num 1、F1、Ctrl+F2、Alt+Num +、Shift+Home。",
                true);
            return;
        }

        string entryId = $"manual-{SpawnId.NewUlid()}";
        var record = new TrainerHotkeyBindingRecord
        {
            TrainerId = ActiveTrainer.TrainerId,
            EntryId = entryId,
            Label = label,
            OriginalChord = chord,
            EntryKind = NewManualHotkeyEntryKind,
            ProxyState = false,
            SortOrder = Hotkeys.Count,
        };

        Hotkeys.Add(CreateHotkeyRow(record, isManualEntry: true));
        await _catalogService.SaveHotkeyBindingsAsync(ActiveTrainer.TrainerId, Hotkeys.Select(h => h.ToRecord()).ToList()).ConfigureAwait(true);

        NewManualHotkeyLabel = string.Empty;
        OnPropertyChanged(nameof(HasHotkeys));
        string kindText = record.EntryKind == TrainerHotkeyEntryKind.Trigger ? "单次触发" : "开关";
        StatusFeedbackRequested?.Invoke($"已添加手动代理热键（{kindText}）：{label} ({chord})", false);
    }

    public async Task SaveHotkeyMappedChordAsync(TrainerHotkeyRowViewModel row, string? rawMappedChord)
    {
        if (ActiveTrainer is null)
        {
            return;
        }

        string? normalized = string.IsNullOrWhiteSpace(rawMappedChord) ? null : rawMappedChord.Trim();
        if (normalized is not null && !HotkeyChordParser.TryParse(normalized, out _))
        {
            StatusFeedbackRequested?.Invoke(
                $"映射热键「{normalized}」格式无效，未保存。请使用规范格式，例如：Num 1、F2、Ctrl+F2、Alt+Num +。",
                true);
            return;
        }

        if (normalized is not null && string.Equals(normalized, row.OriginalChord, StringComparison.OrdinalIgnoreCase))
        {
            normalized = null;
        }

        row.MappedChord = normalized;
        row.IsEditingMappedChord = false;
        row.EditingMappedChordText = string.Empty;

        await _catalogService.SaveHotkeyBindingsAsync(ActiveTrainer.TrainerId, Hotkeys.Select(h => h.ToRecord()).ToList()).ConfigureAwait(true);

        StatusFeedbackRequested?.Invoke(
            normalized is null
                ? $"已恢复「{row.Label}」默认热键（{row.OriginalChord}）"
                : $"已将「{row.Label}」代理热键更新为 {normalized}",
            false);
    }

    [RelayCommand]
    public async Task CreateManualSnapshotAsync()
    {
        if (_currentGame is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (_saveTimeMachineService is not null)
            {
                try
                {
                    CreateSaveSnapshotResult tmRes = await _saveTimeMachineService
                        .CreateSnapshotAsync(new CreateSaveSnapshotRequest
                        {
                            GameId = _currentGame.GameId,
                            TriggerKind = SaveSnapshotTriggerKind.PreTrainerImport,
                            Note = $"修改器工作台手动备份（{ActiveTrainer?.DisplayName ?? _currentGame.Title}）",
                            IncludeConfigFiles = false,
                            SkipIfUnchangedFromHead = false,
                        })
                        .ConfigureAwait(true);

                    if (tmRes.Succeeded)
                    {
                        LatestSnapshotNoticeText = string.IsNullOrWhiteSpace(tmRes.Message)
                            ? $"已通过存档时光机创建手动快照（{tmRes.Snapshot?.FileCount ?? 0} 个文件）"
                            : (tmRes.Message.Contains("时光机", StringComparison.Ordinal)
                                ? tmRes.Message
                                : $"已通过存档时光机创建手动快照：{tmRes.Message}");
                        await RefreshSnapshotsOnlyAsync(_currentGame.GameId).ConfigureAwait(true);
                        StatusFeedbackRequested?.Invoke(LatestSnapshotNoticeText, false);
                        return;
                    }

                    if (tmRes.Skipped && tmRes.SkipReason != SaveSnapshotSkipReason.DisabledBySettings)
                    {
                        string reasonText = UiFormatters.FormatPreTrainerSnapshotSkipReason(tmRes.SkipReason.ToString());
                        LatestSnapshotNoticeText = string.IsNullOrWhiteSpace(tmRes.Message)
                            ? $"存档快照已跳过（{reasonText}）"
                            : tmRes.Message;
                        await RefreshSnapshotsOnlyAsync(_currentGame.GameId).ConfigureAwait(true);
                        StatusFeedbackRequested?.Invoke(LatestSnapshotNoticeText, false);
                        return;
                    }
                }
                catch
                {
                    // Fall back smoothly to legacy pre-trainer snapshot service when Time Machine is unavailable.
                }
            }

            PreTrainerSnapshotResult res = await _snapshotService
                .CreateAsync(_currentGame.GameId, ActiveTrainer?.TrainerId, isManual: true)
                .ConfigureAwait(true);
            LatestSnapshotNoticeText = res.Message ?? UiFormatters.FormatPreTrainerSnapshotStatus(res.Status, res.SkipReason);
            await RefreshSnapshotsOnlyAsync(_currentGame.GameId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(LatestSnapshotNoticeText, res.Status == PreTrainerSnapshotStatus.Failed);
        }
        finally
        {
            IsBusy = false;
            NotifyAllDerivedStates();
        }
    }

    public async Task TriggerHotkeyRowAsync(TrainerHotkeyRowViewModel row)
    {
        if (_runtimeService.ActiveSession is not { } session || !IsRunningForCurrentGame || IsGuardLocked)
        {
            StatusFeedbackRequested?.Invoke("请先启动修改器并确保反作弊守卫处于安全空闲态后再触发热键", true);
            return;
        }

        try
        {
            await _runtimeService.TriggerHotkeyAsync(session.SessionId, row.EntryId).ConfigureAwait(true);
            if (row.IsToggle)
            {
                // Persistence is owned exclusively by TrainerRuntimeService.TriggerHotkeyAsync; avoid duplicate DB write.
                row.ProxyState = !row.ProxyState;
            }

            StatusFeedbackRequested?.Invoke($"已向前台发送热键 {row.EffectiveChord}（{row.Label}）", false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"发送热键失败：{ex.Message}", true);
        }
    }

    private async Task RemoveManualHotkeyRowAsync(TrainerHotkeyRowViewModel row)
    {
        if (ActiveTrainer is null)
        {
            return;
        }

        Hotkeys.Remove(row);
        await _catalogService.SaveHotkeyBindingsAsync(ActiveTrainer.TrainerId, Hotkeys.Select(h => h.ToRecord()).ToList()).ConfigureAwait(true);
        OnPropertyChanged(nameof(HasHotkeys));
    }

    private async Task LoadCandidateDetailsAsync(TrainerCandidateItemViewModel item)
    {
        if (_currentGame is null || item.IsLoadingDetails)
        {
            return;
        }

        item.IsLoadingDetails = true;
        try
        {
            TrainerCandidateDetailsResult details = await _catalogService
                .LoadCandidateDetailsWithDiagnosticsAsync(_currentGame.GameId, item.Candidate)
                .ConfigureAwait(true);
            item.UpdateCandidate(details.Candidate);

            if (details.ChallengeDetected)
            {
                RuntimeState = TrainerRuntimeState.FallbackBrowser;
                ShowBrowserFallbackCard = true;
                FallbackBrowserUrl = details.FallbackBrowserUrl ?? item.PageUrl;
                BrowserFallbackMessage = details.ErrorMessage
                    ?? "外部源站详情页触发人机验证（403/Challenge），已停止自动请求。请在默认浏览器打开该网页下载后直接拖放至本窗口导入。";
                StatusFeedbackRequested?.Invoke(BrowserFallbackMessage, true);
                NotifyAllDerivedStates();
                return;
            }

            if (details.Candidate.Attachments.Count == 0)
            {
                StatusFeedbackRequested?.Invoke(
                    details.ErrorMessage ?? "未能从详情页提取可下载附件，可点击「在浏览器打开」后手动拖放导入",
                    true);
            }
            else
            {
                StatusFeedbackRequested?.Invoke(
                    $"已加载 {details.Candidate.Attachments.Count} 个可选版本与 {details.Candidate.PreviewHotkeys.Count} 条预览热键，确认版本后点击「下载所选版本」。",
                    false);
            }
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"加载候选详情失败：{ex.Message}", true);
        }
        finally
        {
            item.IsLoadingDetails = false;
        }
    }

    private Task DownloadCandidateAsync(TrainerCandidateItemViewModel item) =>
        DownloadCandidateCoreAsync(item.BuildSelectionCandidate(), selectedExecutableRelPath: null);

    private async Task DownloadCandidateCoreAsync(TrainerCandidate candidate, string? selectedExecutableRelPath)
    {
        if (_currentGame is null || IsProtectedOnlineGameBlocked || IsBusy)
        {
            return;
        }

        IsBusy = true;
        RuntimeState = TrainerRuntimeState.Downloading;
        DownloadProgressPercent = 0;
        string downloadHost = TrainerSourceProvenance.ResolveSourceHost(candidate.Provider, candidate.PageUrl);
        DownloadStageText = $"正在从第三方外部网站 ({downloadHost}) 下载所选修改器…";
        ShowBrowserFallbackCard = false;
        ShowQuarantineCard = false;
        NotifyAllDerivedStates();

        var progress = new Progress<TrainerDownloadProgress>(p =>
        {
            DownloadStageText = string.IsNullOrWhiteSpace(p.StageLabel)
                ? $"已下载 {UiFormatters.FormatByteSize(p.BytesReceived)}"
                : p.StageLabel;
            if (p.TotalBytes is > 0)
            {
                DownloadProgressPercent = Math.Clamp(p.BytesReceived * 100.0 / p.TotalBytes.Value, 0.0, 100.0);
            }
        });

        try
        {
            TrainerImportResult result = string.IsNullOrWhiteSpace(selectedExecutableRelPath)
                ? await _catalogService.DownloadAsync(_currentGame.GameId, candidate, progress).ConfigureAwait(true)
                : await _catalogService.DownloadWithExecutableSelectionAsync(_currentGame.GameId, candidate, selectedExecutableRelPath, progress).ConfigureAwait(true);

            await HandleImportOutcomeAsync(result, pendingCandidate: candidate, pendingLocalPath: null, pendingReplaceTrainerId: null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
            StatusFeedbackRequested?.Invoke($"下载修改器失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            DownloadStageText = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    private async Task HandleImportOutcomeAsync(
        TrainerImportResult result,
        TrainerCandidate? pendingCandidate,
        string? pendingLocalPath,
        string? pendingReplaceTrainerId)
    {
        if (result.Succeeded && result.Trainer is not null)
        {
            ActiveTrainer = result.Trainer;
            MatchCandidates.Clear();
            ShowMultiExecutablePicker = false;
            ShowBrowserFallbackCard = false;
            ShowQuarantineCard = false;
            IsRemoveConfirmOpen = false;
            RuntimeState = TrainerRuntimeState.Ready;

            IReadOnlyList<TrainerCatalogRecord> allTrainers = await _catalogService.ListForGameAsync(result.Trainer.GameId).ConfigureAwait(true);
            ReplaceCollection(
                BoundTrainers,
                allTrainers.Count > 0 ? allTrainers : [result.Trainer]);
            ReplaceCollection(
                Hotkeys,
                result.Hotkeys.OrderBy(h => h.SortOrder).Select(h => CreateHotkeyRow(h)));

            await NotifyTrainerCatalogChangedAsync(ActiveTrainer.GameId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(
                $"已校验并导入修改器「{ActiveTrainer.DisplayName}」（{TrainerSourceDomainText} · SHA-256 已钉扎 · 共 {Hotkeys.Count} 条热键）",
                false);
            return;
        }

        switch (result.FailureKind)
        {
            case TrainerImportFailureKind.MultipleExecutablesSelectionRequired:
                _pendingMultiExeCandidate = pendingCandidate;
                _pendingMultiExeLocalPath = pendingLocalPath;
                _pendingMultiExeReplaceTrainerId = pendingReplaceTrainerId;
                ReplaceCollection(PendingExecutableChoices, result.CandidateExecutables);
                SelectedExecutableChoice = PendingExecutableChoices.FirstOrDefault();
                ShowMultiExecutablePicker = true;
                RuntimeState = TrainerRuntimeState.Verifying;
                StatusFeedbackRequested?.Invoke("压缩包内包含多个可执行文件，请选择要托管的主修改器程序（辅助 EXE 绝不自动执行）", false);
                break;

            case TrainerImportFailureKind.ChallengeOrHtmlFallback:
                RuntimeState = TrainerRuntimeState.FallbackBrowser;
                ShowBrowserFallbackCard = true;
                FallbackBrowserUrl = result.FallbackBrowserUrl ?? pendingCandidate?.PageUrl ?? "https://flingtrainer.com/";
                BrowserFallbackMessage = result.Message ?? "源站触发人机验证（403/Challenge），已中止自动下载。请在默认浏览器打开原页下载并拖放到窗口导入。";
                StatusFeedbackRequested?.Invoke(BrowserFallbackMessage, true);
                break;

            case TrainerImportFailureKind.AntivirusQuarantined:
            case TrainerImportFailureKind.SmartAppControlBlocked:
                RuntimeState = TrainerRuntimeState.Quarantined;
                ShowQuarantineCard = true;
                IsSmartAppControlBlocked = result.FailureKind == TrainerImportFailureKind.SmartAppControlBlocked;
                QuarantineDiagnosticMessage = result.Message
                    ?? (IsSmartAppControlBlocked
                        ? "文件被 Windows 11 Smart App Control 拦截（Win32 4551）。"
                        : "修改器文件已被安全软件隔离或删除（Win32 225/226 或解压后文件缺失）。建议前往「设置 → 修改器与安全沙箱」配置单一沙箱目录排除。");
                StatusFeedbackRequested?.Invoke(QuarantineDiagnosticMessage, true);
                break;

            default:
                RuntimeState = HasBoundTrainer ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
                StatusFeedbackRequested?.Invoke(
                    result.Message ?? $"导入未完成：{UiFormatters.FormatTrainerImportFailureKind(result.FailureKind)}",
                    true);
                break;
        }
    }

    private async Task RefreshBoundTrainerAndSnapshotsAsync(string gameId, string? preferredTrainerId, CancellationToken ct)
    {
        IReadOnlyList<TrainerCatalogRecord> trainers = await _catalogService.ListForGameAsync(gameId, ct).ConfigureAwait(true);
        TrainerCatalogRecord? selected = ResolvePreferredTrainer(trainers, preferredTrainerId);

        IReadOnlyList<TrainerHotkeyBindingRecord> bindings = selected is not null
            ? await _catalogService.GetHotkeysAsync(selected.TrainerId, ct).ConfigureAwait(true)
            : [];

        ActiveTrainer = selected;
        ReplaceCollection(BoundTrainers, trainers);
        ReplaceCollection(Hotkeys, bindings.OrderBy(h => h.SortOrder).Select(h => CreateHotkeyRow(h)));
        GameUpdateDriftNotice = _currentGame is not null
            ? ComputeGameUpdateDriftNotice(selected, _currentGame)
            : string.Empty;

        await RefreshSnapshotsOnlyAsync(gameId, ct).ConfigureAwait(true);
        RuntimeState = ResolveRuntimeState(selected);
    }

    private async Task RefreshSnapshotsOnlyAsync(string gameId, CancellationToken ct = default)
    {
        IReadOnlyList<MergedSnapshotEntry> snapshots = await LoadMergedSnapshotsAsync(gameId, ct).ConfigureAwait(true);
        bool running = _currentGame is not null && string.Equals(_currentGame.GameId, gameId, StringComparison.OrdinalIgnoreCase)
            ? IsGameRunningIncludingExternal(_currentGame)
            : _launchService.GetActiveSession(gameId) is not null;
        IsSelectedGameRunning = running;

        ReplaceCollection(
            PreTrainerSnapshots,
            snapshots.Select(s => new PreTrainerSnapshotItemViewModel(
                s.Record,
                running,
                RequestRollbackSnapshotAsync,
                isTimeMachineSnapshot: s.IsTimeMachine)));

        OnPropertyChanged(nameof(HasPreTrainerSnapshots));
    }

    private async Task<IReadOnlyList<MergedSnapshotEntry>> LoadMergedSnapshotsAsync(string gameId, CancellationToken ct)
    {
        IReadOnlyList<PreTrainerSnapshotRecord> legacySnapshots = await _snapshotService.ListAsync(gameId, ct).ConfigureAwait(true);
        var merged = new List<MergedSnapshotEntry>(legacySnapshots.Count);
        var seenSnapshotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_saveTimeMachineService is not null)
        {
            try
            {
                SaveTreeGraphDto graph = await _saveTimeMachineService.GetSaveTreeGraphAsync(gameId, ct).ConfigureAwait(true);
                foreach (SaveTreeNodeDto node in graph.Nodes)
                {
                    if (node.Snapshot.TriggerKind != SaveSnapshotTriggerKind.PreTrainerImport)
                    {
                        continue;
                    }

                    SaveSnapshotRecord tmSnap = node.Snapshot;
                    if (!seenSnapshotIds.Add(tmSnap.SnapshotId))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(tmSnap.LegacyPreTrainerSnapshotId))
                    {
                        seenSnapshotIds.Add(tmSnap.LegacyPreTrainerSnapshotId!);
                    }

                    merged.Add(new MergedSnapshotEntry(
                        new PreTrainerSnapshotRecord
                        {
                            SnapshotId = tmSnap.SnapshotId,
                            GameId = tmSnap.GameId,
                            TriggerKind = PreTrainerSnapshotTriggerKind.PreTrainer,
                            Status = PreTrainerSnapshotStatus.Completed,
                            FileCount = tmSnap.FileCount,
                            TotalBytes = tmSnap.TotalRawBytes,
                            CreatedAtUtc = tmSnap.CreatedAtUtc,
                        },
                        IsTimeMachine: true));
                }
            }
            catch
            {
                // Ignore transient Time Machine graph errors so legacy pre-trainer snapshots remain accessible.
            }
        }

        foreach (PreTrainerSnapshotRecord legacy in legacySnapshots)
        {
            if (seenSnapshotIds.Add(legacy.SnapshotId))
            {
                merged.Add(new MergedSnapshotEntry(legacy, IsTimeMachine: false));
            }
        }

        merged.Sort(static (a, b) => b.Record.CreatedAtUtc.CompareTo(a.Record.CreatedAtUtc));
        return merged;
    }

    private sealed record MergedSnapshotEntry(PreTrainerSnapshotRecord Record, bool IsTimeMachine);

    private Task RequestRollbackSnapshotAsync(PreTrainerSnapshotItemViewModel item)
    {
        if (ReferenceEquals(PendingRollbackSnapshot, item))
        {
            return ConfirmRollbackAsync();
        }

        RequestRollbackSnapshot(item);
        return Task.CompletedTask;
    }

    [RelayCommand]
    public void RequestRollbackSnapshot(PreTrainerSnapshotItemViewModel? item)
    {
        if (item is null || _currentGame is null || IsBusy)
        {
            return;
        }

        if (IsGameRunningIncludingExternal(_currentGame))
        {
            UpdateSelectedGameRunningState(true);
            StatusFeedbackRequested?.Invoke("检测到游戏正在运行中，为防止存档损坏已禁用回滚；请先退出游戏后再恢复快照", true);
            return;
        }

        PendingRollbackSnapshot = item;
        StatusFeedbackRequested?.Invoke($"{RollbackConfirmPromptText} 请点击确认或再次点击该快照的「一键回滚」执行。", false);
    }

    [RelayCommand]
    public void CancelRollback()
    {
        PendingRollbackSnapshot = null;
    }

    [RelayCommand]
    public async Task ConfirmRollbackAsync()
    {
        PreTrainerSnapshotItemViewModel? target = PendingRollbackSnapshot;
        PendingRollbackSnapshot = null;
        if (target is null)
        {
            return;
        }

        await RestoreSnapshotAsync(target).ConfigureAwait(true);
    }

    public async Task RestoreSnapshotAsync(PreTrainerSnapshotItemViewModel item)
    {
        if (_currentGame is null || IsBusy)
        {
            return;
        }

        if (ReferenceEquals(PendingRollbackSnapshot, item))
        {
            PendingRollbackSnapshot = null;
        }

        if (IsGameRunningIncludingExternal(_currentGame))
        {
            UpdateSelectedGameRunningState(true);
            StatusFeedbackRequested?.Invoke("游戏正在运行中，为防止存档损坏已禁用回滚；请先退出游戏后再恢复快照", true);
            return;
        }

        IsBusy = true;
        BusyMessage = "正在创建回滚前安全点并原子还原存档快照…";
        NotifyAllDerivedStates();

        try
        {
            if (item.IsTimeMachineSnapshot && _saveTimeMachineService is not null)
            {
                SaveRestoreOutcome tmRestore = await _saveTimeMachineService
                    .RollbackToSnapshotAsync(new RollbackSaveRequest
                    {
                        SnapshotId = item.SnapshotId,
                        IncludeConfigFiles = false,
                        CreateSafetySnapshot = true,
                        ModMismatchPolicy = SaveModMismatchPolicy.AbortAndPrompt,
                    })
                    .ConfigureAwait(true);

                await RefreshSnapshotsOnlyAsync(_currentGame.GameId).ConfigureAwait(true);
                StatusFeedbackRequested?.Invoke(
                    !string.IsNullOrWhiteSpace(tmRestore.Message)
                        ? tmRestore.Message
                        : (tmRestore.Succeeded ? $"已从存档时光机恢复快照（还原 {tmRestore.RestoredFileCount} 个文件）" : "回滚存档时光机快照失败"),
                    !tmRestore.Succeeded);
                return;
            }

            PreTrainerRestoreResult result = await _snapshotService.RestoreAsync(item.SnapshotId).ConfigureAwait(true);
            await RefreshSnapshotsOnlyAsync(_currentGame.GameId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(
                result.Message ?? (result.Succeeded ? $"已恢复存档快照（还原 {result.RestoredFileCount} 个文件）" : "回滚存档快照失败"),
                !result.Succeeded);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"回滚快照异常：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    public async Task<string> ResolveActiveTrainerDirectoryAsync(CancellationToken cancellationToken = default)
    {
        TrainerSettingsDto settings = await _db.GetTrainerSettingsAsync(cancellationToken).ConfigureAwait(true);
        string sandboxRoot = _paths.ResolveTrainerSandboxDirectory(settings.CustomSandboxRoot, requirePureAscii: false);
        if (ActiveTrainer is null)
        {
            return sandboxRoot;
        }

        string? relDir = Path.GetDirectoryName(ActiveTrainer.SandboxRelativePath);
        if (!string.IsNullOrWhiteSpace(relDir))
        {
            string combined = Path.GetFullPath(Path.Combine(sandboxRoot, relDir));
            if (Directory.Exists(combined))
            {
                return combined;
            }
        }

        return sandboxRoot;
    }

    private void OnRuntimeStateChanged(object? sender, TrainerRuntimeStateChanged ev)
    {
        void Apply()
        {
            if (_currentGame is null || !string.Equals(ev.GameId, _currentGame.GameId, StringComparison.OrdinalIgnoreCase))
            {
                _ = RefreshOtherRunningTrainerConflictTitleAsync(CancellationToken.None);
                OnPropertyChanged(nameof(IsRunningForOtherGame));
                OnPropertyChanged(nameof(OtherRunningTrainerConflictText));
                return;
            }

            RuntimeState = ev.CurrentState == TrainerRuntimeState.Ready && ActiveTrainer is null
                ? TrainerRuntimeState.Unbound
                : ev.CurrentState;

            if (ev.CurrentState is TrainerRuntimeState.Ready or TrainerRuntimeState.Unbound or TrainerRuntimeState.EmergencyStopped)
            {
                ResetInMemoryHotkeyProxyStates();
                ExternalGameLaunchNotice = string.Empty;
            }

            if (ev.CurrentState == TrainerRuntimeState.EmergencyStopped && !string.IsNullOrWhiteSpace(ev.Message))
            {
                StatusFeedbackRequested?.Invoke(ev.Message!, true);
            }

            NotifyAllDerivedStates();
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            _ = dispatcher.InvokeAsync(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void OnAntiCheatGuardStateChanged(object? sender, AntiCheatGuardSnapshot snap)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            _ = dispatcher.InvokeAsync(() => UpdateGuardSnapshot(snap));
        }
        else
        {
            UpdateGuardSnapshot(snap);
        }
    }

    private TrainerHotkeyRowViewModel CreateHotkeyRow(TrainerHotkeyBindingRecord record, bool isManualEntry = false) =>
        new(
            record,
            TriggerHotkeyRowAsync,
            RemoveManualHotkeyRowAsync,
            isManualEntry: isManualEntry,
            onSaveMappedChord: SaveHotkeyMappedChordAsync);

    private TrainerCatalogRecord? ResolvePreferredTrainer(
        IReadOnlyList<TrainerCatalogRecord> trainers,
        string? preferredTrainerId)
    {
        if (trainers.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredTrainerId))
        {
            TrainerCatalogRecord? byPreferred = trainers.FirstOrDefault(
                t => string.Equals(t.TrainerId, preferredTrainerId, StringComparison.OrdinalIgnoreCase));
            if (byPreferred is not null)
            {
                return byPreferred;
            }
        }

        string? activeSessionTrainerId = _runtimeService.ActiveSession?.TrainerId;
        if (!string.IsNullOrWhiteSpace(activeSessionTrainerId))
        {
            TrainerCatalogRecord? bySession = trainers.FirstOrDefault(
                t => string.Equals(t.TrainerId, activeSessionTrainerId, StringComparison.OrdinalIgnoreCase));
            if (bySession is not null)
            {
                return bySession;
            }
        }

        return trainers[0];
    }

    private TrainerRuntimeState ResolveRuntimeState(TrainerCatalogRecord? selectedTrainer)
    {
        if (IsRunningForCurrentGame)
        {
            return TrainerRuntimeState.Running;
        }

        return selectedTrainer is not null ? TrainerRuntimeState.Ready : TrainerRuntimeState.Unbound;
    }

    private static string ComputeGameUpdateDriftNotice(TrainerCatalogRecord? trainer, GameListItemViewModel game)
    {
        if (trainer is not null
            && !string.IsNullOrWhiteSpace(trainer.BoundBuildId)
            && !string.IsNullOrWhiteSpace(game.Record.SteamBuildId)
            && !string.Equals(trainer.BoundBuildId, game.Record.SteamBuildId, StringComparison.OrdinalIgnoreCase))
        {
            return $"游戏 Steam BuildId 已从 {trainer.BoundBuildId} 更新至 {game.Record.SteamBuildId}，建议检查新版修改器。";
        }

        return string.Empty;
    }

    private bool IsGameRunningIncludingExternal(GameListItemViewModel game)
    {
        if (game.IsRunning || _launchService.GetActiveSession(game.GameId) is not null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(game.Record.InstallDir))
        {
            return false;
        }

        try
        {
            return _directoryProcessFinder(game.Record.InstallDir).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void ResetInMemoryHotkeyProxyStates()
    {
        foreach (var row in Hotkeys)
        {
            row.ProxyState = false;
        }
    }

    private string TryResolveGameTitleSynchronously(string gameId)
    {
        if (_currentGame is not null && string.Equals(_currentGame.GameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            return _currentGame.Title;
        }

        if (_gameTitleCache.TryGetValue(gameId, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        if (AntiCheatFeatureCatalog.KnownProtectedGameExecutables.TryGetValue(gameId, out var known))
        {
            return known.DisplayName;
        }

        return gameId;
    }

    private async Task ResolveProtectedGameTitleAsync(string gameId, CancellationToken ct)
    {
        string title = await LookupGameTitleAsync(gameId, ct).ConfigureAwait(true);
        if (string.Equals(_guardActiveProtectedGameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            GuardActiveProtectedGameTitle = title;
            OnPropertyChanged(nameof(GuardLockBannerText));
        }
    }

    private async Task RefreshOtherRunningTrainerConflictTitleAsync(CancellationToken ct = default)
    {
        if (!IsRunningForOtherGame || _runtimeService.ActiveSession is not { } otherSession)
        {
            OtherRunningTrainerGameTitle = string.Empty;
            return;
        }

        OtherRunningTrainerGameTitle = await LookupGameTitleAsync(otherSession.GameId, ct).ConfigureAwait(true);
    }

    private async Task<string> LookupGameTitleAsync(string gameId, CancellationToken ct)
    {
        if (_currentGame is not null && string.Equals(_currentGame.GameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            _gameTitleCache[gameId] = _currentGame.Title;
            return _currentGame.Title;
        }

        if (_gameTitleCache.TryGetValue(gameId, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        try
        {
            GameRecord? record = await _libraryService.GetGameByIdAsync(gameId, ct).ConfigureAwait(true)
                ?? await _db.GetGameByIdAsync(gameId, ct).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(record?.Title))
            {
                _gameTitleCache[gameId] = record.Title;
                return record.Title;
            }
        }
        catch
        {
            // Ignore transient lookup error and keep fallback identifier.
        }

        return TryResolveGameTitleSynchronously(gameId);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }

    private Task NotifyTrainerCatalogChangedAsync(string gameId) =>
        TrainerCatalogChangedRequested is not null
            ? TrainerCatalogChangedRequested(gameId)
            : Task.CompletedTask;

    private void CopyCandidatePageUrlSafe(string url) =>
        CopyTextSafe(url, $"已复制外部网站页面链接：{url}");

    private void CopyTextSafe(string text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _copyToClipboard(text);
            StatusFeedbackRequested?.Invoke(successMessage, false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"复制链接失败：{ex.Message}", true);
        }
    }

    private void OpenExternalUrlSafe(string url)
    {
        if (!TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(url, out string safeHttpsUrl, out _))
        {
            return;
        }

        try
        {
            _openExternalUrl(safeHttpsUrl);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"无法打开外部链接：{ex.Message}", true);
        }
    }

    private static void OpenUrlWithShell(string url)
    {
        if (!TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(url, out string safeHttpsUrl, out _))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = safeHttpsUrl,
            UseShellExecute = true,
        });
    }

    private static void CopyTextToSystemClipboard(string text) =>
        Clipboard.SetText(text);

    private void NotifyAllDerivedStates()
    {
        OnPropertyChanged(nameof(HasSelectedGame));
        OnPropertyChanged(nameof(HasBoundTrainer));
        OnPropertyChanged(nameof(HasMultipleBoundTrainers));
        OnPropertyChanged(nameof(SelectedTrainer));
        OnPropertyChanged(nameof(IsRunningForCurrentGame));
        OnPropertyChanged(nameof(IsRunningForOtherGame));
        OnPropertyChanged(nameof(OtherRunningTrainerConflictText));
        OnPropertyChanged(nameof(HasExternalGameLaunchNotice));
        OnPropertyChanged(nameof(SelectedExecutableChoiceSummaryText));
        OnPropertyChanged(nameof(CanRedownloadQuarantinedTrainer));
        OnPropertyChanged(nameof(IsRollbackConfirmOpen));
        OnPropertyChanged(nameof(RollbackConfirmPromptText));
        OnPropertyChanged(nameof(RemoveConfirmPromptText));
        OnPropertyChanged(nameof(IsGuardLocked));
        OnPropertyChanged(nameof(GuardPhaseDisplayText));
        OnPropertyChanged(nameof(GuardDetectedResidualProcesses));
        OnPropertyChanged(nameof(GuardLockBannerText));
        OnPropertyChanged(nameof(IsProtectedOnlineGameBlocked));
        OnPropertyChanged(nameof(IsOfflineSinglePlayerVerificationPending));
        OnPropertyChanged(nameof(CanRetryOfflineVerification));
        OnPropertyChanged(nameof(IsHybridSinglePlayerGame));
        OnPropertyChanged(nameof(IsHybridExecutableAntiCheatLauncher));
        OnPropertyChanged(nameof(HybridAntiCheatLauncherWarningText));
        OnPropertyChanged(nameof(IsOfflineBypassConfirmed));
        OnPropertyChanged(nameof(IsLiveAntiCheatClear));
        OnPropertyChanged(nameof(ShowProtectedFenceCard));
        OnPropertyChanged(nameof(ShowTrainerWorkspaceControls));
        OnPropertyChanged(nameof(ShowHybridOfflinePanel));
        OnPropertyChanged(nameof(ShowVanguardIdleWarning));
        OnPropertyChanged(nameof(IsBlockedByVanguardStrictMode));
        OnPropertyChanged(nameof(VanguardWarningSummaryText));
        OnPropertyChanged(nameof(ProtectedFenceTitleText));
        OnPropertyChanged(nameof(ProtectedFenceDescriptionText));
        OnPropertyChanged(nameof(LiveAntiCheatStatusText));
        OnPropertyChanged(nameof(HybridOfflineStatusBadgeText));
        OnPropertyChanged(nameof(PrimaryActionButtonText));
        OnPropertyChanged(nameof(IsPrimaryButtonRunningStyle));
        OnPropertyChanged(nameof(ShowUnboundSecondaryImportButton));
        OnPropertyChanged(nameof(ShowReadyMoreMenuButton));
        OnPropertyChanged(nameof(CanModifyBoundTrainer));
        OnPropertyChanged(nameof(ShowRunningWindowToggleButton));
        OnPropertyChanged(nameof(OriginalWindowToggleButtonText));
        OnPropertyChanged(nameof(CanLaunchOrRunTrainer));
        OnPropertyChanged(nameof(IsPrimaryActionButtonEnabled));
        OnPropertyChanged(nameof(CanTriggerHotkeys));
        OnPropertyChanged(nameof(IsFreeHostedMode));
        OnPropertyChanged(nameof(HasMatchCandidates));
        OnPropertyChanged(nameof(HasHotkeys));
        OnPropertyChanged(nameof(HasPreTrainerSnapshots));
        OnPropertyChanged(nameof(HasGameUpdateDriftNotice));
        OnPropertyChanged(nameof(HasLatestSnapshotNotice));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(ActiveTrainerProvenance));
        OnPropertyChanged(nameof(TrainerDisplayTitle));
        OnPropertyChanged(nameof(IsActiveTrainerExternalWebSource));
        OnPropertyChanged(nameof(TrainerSourceDomainText));
        OnPropertyChanged(nameof(TrainerSourceHostText));
        OnPropertyChanged(nameof(HasValidExternalSourceUrl));
        OnPropertyChanged(nameof(TrainerSourcePageUrl));
        OnPropertyChanged(nameof(TrainerSourcePageDisplayLine));
        OnPropertyChanged(nameof(TrainerSourceCompactOriginText));
        OnPropertyChanged(nameof(TrainerSourceProvenanceNoteText));
        OnPropertyChanged(nameof(TrainerVersionAndDateText));
        OnPropertyChanged(nameof(TrainerFileMetaText));
        OnPropertyChanged(nameof(TrainerPinnedSha256Short));
        OnPropertyChanged(nameof(TrainerPinnedSha256Full));
        OnPropertyChanged(nameof(HotkeyProxyDisclaimerText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtimeService.StateChanged -= OnRuntimeStateChanged;
        _antiCheatGuard.StateChanged -= OnAntiCheatGuardStateChanged;
    }
}
