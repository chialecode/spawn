using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Host.Controls;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels.Saves;

public enum SaveInspectorTab
{
    Files = 0,
    Diff = 1,
}

public enum SaveDiffTargetMode
{
    Parent = 0,
    CurrentDisk = 1,
}

/// <summary>
/// Per-game Save Time Machine &amp; Branching Save Tree ViewModel powering the reusable <c>SaveWorkbenchControl</c>
/// (shared between Game Detail's <c>存档</c> sub-tab and the top-level Column 1 <c>存档</c> workbench).
/// </summary>
public sealed partial class SaveWorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly ISaveTimeMachineService? _saveService;
    private readonly ICloudSaveSyncService? _cloudSyncService;
    private readonly ISaveWatcherService? _watcherService;
    private readonly ILudusaviRuleResolver? _ruleResolver;
    private readonly IGameLaunchService _launchService;
    private readonly Action<string> _openFolder;
    private readonly Func<string?> _pickExportFolder;

    private GameListItemViewModel? _currentGame;
    private SaveTreeGraphDto? _rawGraph;
    private SaveGameStateRecord? _gameState;
    private ResolvedSaveLayout? _resolvedLayout;
    private int _loadGeneration;
    private int _inspectorGeneration;
    private bool _suppressAutoSaveToggles;
    private bool _suppressBranchSelectionCommand;
    private bool _disposed;

    public event Action<string, bool>? StatusFeedbackRequested;
    public event Action? OpenSavesSettingsRequested;
    public event Func<string, Task>? SaveTreeMutatedRequested;

    public string? CurrentGameId => _currentGame?.GameId;
    public bool IsServiceAvailable => _saveService is not null;

    public IReadOnlyList<SaveBranchColorOption> BranchColorPalette { get; } =
    [
        new("#1677FF", "强调蓝"),
        new("#10B981", "翡翠绿"),
        new("#8B5CF6", "紫罗兰"),
        new("#F59E0B", "琥珀橙"),
        new("#EC4899", "洋红"),
        new("#06B6D4", "湖蓝"),
        new("#EF4444", "绯红"),
        new("#6366F1", "靛蓝"),
    ];

    public ObservableCollection<SaveBranchItemViewModel> Branches { get; } = [];
    public ObservableCollection<SaveSnapshotRowViewModel> GraphRows { get; } = [];
    public ObservableCollection<SaveFileEntryViewModel> SelectedNodeFiles { get; } = [];
    public ObservableCollection<SaveDiffEntryViewModel> SelectedNodeDiffItems { get; } = [];
    public ObservableCollection<CloudEndpointItemViewModel> AvailableCloudEndpoints { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    private bool _isSelectedGameRunning;

    [ObservableProperty]
    private SaveBranchItemViewModel? _activeBranch;

    [ObservableProperty]
    private SaveSnapshotRowViewModel? _selectedNode;

    [ObservableProperty]
    private SaveTreeFilterMode _treeFilterMode = SaveTreeFilterMode.All;

    [ObservableProperty]
    private string _treeSearchText = string.Empty;

    [ObservableProperty]
    private double _graphColumnWidth = SaveTreeGraphLayoutEngine.MinGraphWidth;

    [ObservableProperty]
    private int _totalSnapshotCount;

    [ObservableProperty]
    private int _totalBranchCount;

    [ObservableProperty]
    private bool _isDetachedHead;

    [ObservableProperty]
    private long _totalRawBytes;

    [ObservableProperty]
    private long _totalDeduplicatedBytes;

    [ObservableProperty]
    private double _deduplicationRatio;

    // Quick snapshot creation options
    [ObservableProperty]
    private string _quickSnapshotNote = string.Empty;

    [ObservableProperty]
    private bool _includeConfigOnSnapshot;

    [ObservableProperty]
    private bool _pinNewSnapshot;

    // Auto-backup toggles & watcher status
    [ObservableProperty]
    private bool _autoWatchEnabled = true;

    [ObservableProperty]
    private bool _postExitSnapshotEnabled = true;

    [ObservableProperty]
    private SaveWatcherStatusKind _watcherStatus = SaveWatcherStatusKind.Stopped;

    [ObservableProperty]
    private string _resolvedLocationsSummaryText = "正在探测存档路径…";

    // Inline Fork Branch popover state
    [ObservableProperty]
    private bool _isForkPanelOpen;

    [ObservableProperty]
    private string _forkBranchNameInput = string.Empty;

    [ObservableProperty]
    private string _forkBranchDescriptionInput = string.Empty;

    [ObservableProperty]
    private string _selectedForkColorHex = SaveBranchColors.Palette[1];

    [ObservableProperty]
    private bool _switchToForkedBranch = true;

    [ObservableProperty]
    private string? _forkSourceSnapshotId;

    [ObservableProperty]
    private string _forkSourceLabel = string.Empty;

    // Inline Rename Branch popover state
    [ObservableProperty]
    private bool _isRenameBranchPanelOpen;

    [ObservableProperty]
    private string _renameBranchNameInput = string.Empty;

    [ObservableProperty]
    private string _renameBranchColorHex = SaveBranchColors.DefaultMainColorHex;

    // Inline Rollback confirmation bar
    [ObservableProperty]
    private bool _isRollbackConfirmOpen;

    [ObservableProperty]
    private bool _rollbackIncludeConfig;

    [ObservableProperty]
    private bool _rollbackRequiresModMismatchConfirm;

    [ObservableProperty]
    private string _rollbackModMismatchWarning = string.Empty;

    // Node Inspector state
    [ObservableProperty]
    private SaveInspectorTab _inspectorTab = SaveInspectorTab.Files;

    [ObservableProperty]
    private SaveDiffTargetMode _diffTargetMode = SaveDiffTargetMode.Parent;

    [ObservableProperty]
    private bool _isEditingNote;

    [ObservableProperty]
    private string _editNoteInput = string.Empty;

    [ObservableProperty]
    private bool _isLoadingInspector;

    [ObservableProperty]
    private string _diffSummaryText = string.Empty;

    // Responsive < 740 DIP compact layout
    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isInspectorOpenOnCompact;

    // Cloud Sync Drawer & Divergence Resolution Modal
    [ObservableProperty]
    private bool _isCloudDrawerOpen;

    [ObservableProperty]
    private CloudEndpointItemViewModel? _selectedCloudEndpoint;

    [ObservableProperty]
    private CloudSyncPlanPreview? _cloudSyncPlan;

    [ObservableProperty]
    private bool _isCloudSyncBusy;

    [ObservableProperty]
    private double _cloudSyncProgressPercent;

    [ObservableProperty]
    private string _cloudSyncProgressText = string.Empty;

    [ObservableProperty]
    private bool _hasUnresolvedCloudConflict;

    [ObservableProperty]
    private DateTimeOffset? _lastSyncedAtUtc;

    [ObservableProperty]
    private bool _isDivergenceModalOpen;

    public bool HasSelectedGame => _currentGame is not null;
    public bool HasSnapshots => TotalSnapshotCount > 0;
    public bool HasVisibleGraphRows => GraphRows.Count > 0;
    public bool HasSelectedNode => SelectedNode is not null;
    public bool ShowDesktopInspector => !IsCompactLayout && HasSelectedNode;
    public bool ShowCompactInspectorOverlay => IsCompactLayout && IsInspectorOpenOnCompact && HasSelectedNode;
    public bool ShowFilesInspectorTab => InspectorTab == SaveInspectorTab.Files;
    public bool ShowDiffInspectorTab => InspectorTab == SaveInspectorTab.Diff;
    public bool HasSelectedNodeFiles => SelectedNodeFiles.Count > 0;
    public bool HasSelectedNodeDiffItems => SelectedNodeDiffItems.Count > 0;
    public bool CanCreateSnapshot => HasSelectedGame && IsServiceAvailable && !IsBusy;
    public bool CanMutateDiskSaves => HasSelectedGame && IsServiceAvailable && !IsBusy && !IsSelectedGameRunning;
    public bool CanRollbackSelected => CanMutateDiskSaves && SelectedNode is not null;
    public bool CanForkFromSelected => HasSelectedGame && IsServiceAvailable && !IsBusy && SelectedNode is not null;
    public bool CanDeleteActiveBranch => HasSelectedGame && IsServiceAvailable && !IsBusy && ActiveBranch is { IsDefault: false } && Branches.Count > 1;
    public bool CanDeleteSelectedSnapshot => HasSelectedGame && IsServiceAvailable && !IsBusy && SelectedNode is { IsHead: false, IsPinned: false };

    public string ActiveBranchNameText => ActiveBranch?.Name ?? _rawGraph?.ActiveBranchName ?? "main";
    public Brush ActiveBranchColorBrush => ActiveBranch?.ColorBrush ?? BranchColorPalette[0].SwatchBrush;

    public string TreeMetricsSummaryText => TotalSnapshotCount == 0
        ? "暂无存档快照 · 点击「创建快照」建立第一个时间点"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{TotalBranchCount} 分支 · {TotalSnapshotCount} 快照 · 原始 {UiFormatters.FormatByteSize(TotalRawBytes)} · 去重实占 {UiFormatters.FormatByteSize(TotalDeduplicatedBytes)}（{UiFormatters.FormatDeduplicationPercent(DeduplicationRatio)}）");

    public string WatcherStatusBadgeText => IsSelectedGameRunning
        ? $"{UiFormatters.FormatSaveWatcherStatusLabel(WatcherStatus)} · 游戏运行中"
        : UiFormatters.FormatSaveWatcherStatusLabel(WatcherStatus);

    public bool IsWatcherActive => WatcherStatus is SaveWatcherStatusKind.Watching
        or SaveWatcherStatusKind.Debouncing
        or SaveWatcherStatusKind.Capturing;

    public string CloudStatusChipText
    {
        get
        {
            if (HasUnresolvedCloudConflict)
            {
                return "⚠ 云端分叉冲突";
            }

            if (SelectedCloudEndpoint is null)
            {
                return "☁ 云同步未配置";
            }

            return LastSyncedAtUtc.HasValue
                ? $"☁ 已同步 · {LastSyncedAtUtc.Value.ToLocalTime():MM-dd HH:mm}"
                : $"☁ {SelectedCloudEndpoint.DisplayName}";
        }
    }

    public string SelectedNodePinActionText => SelectedNode?.IsPinned == true ? "取消固定 (★)" : "固定此快照 (★)";
    public string RollbackButtonToolTip => IsSelectedGameRunning
        ? "游戏正在运行中，为防止坏档已禁止回滚与切换分支；请先关闭游戏"
        : "回滚前将自动创建「回滚安全点」快照，并原子替换存档文件";

    public bool HasCloudSyncPlan => CloudSyncPlan is not null;
    public string CloudPlanSummaryText => CloudSyncPlan is null
        ? "点击「检查同步计划」对比本机与远端 64 代血缘链（默认仅同步 [save]，不同步本机 [config]）。"
        : $"{UiFormatters.FormatCloudSyncRelationLabel(CloudSyncPlan.Relation)} · {CloudSyncPlan.Message}";

    public string CloudPlanTransferMetricsText => CloudSyncPlan is null
        ? string.Empty
        : CloudSyncPlan.Relation switch
        {
            CloudSyncRelation.FastForwardPush => string.Create(
                CultureInfo.InvariantCulture,
                $"待上传 {CloudSyncPlan.MissingRemoteChunkCount} 块（约 {UiFormatters.FormatByteSize(CloudSyncPlan.EstimatedUploadBytes)}，<=32MB SPPK 分包）· {CloudSyncPlan.SaveFileCount} 个 [save] 文件"),
            CloudSyncRelation.FastForwardPull => string.Create(
                CultureInfo.InvariantCulture,
                $"待拉取 {CloudSyncPlan.MissingLocalChunkCount} 块（约 {UiFormatters.FormatByteSize(CloudSyncPlan.EstimatedDownloadBytes)}）· {CloudSyncPlan.SaveFileCount} 个 [save] 文件"),
            CloudSyncRelation.Diverged => "本机与远端进度互不为祖先，请点击下方按钮打开双端分叉向导（绝不静默覆盖）。",
            _ => string.Empty,
        };

    public bool CanExecuteDirectCloudSync =>
        !IsCloudSyncBusy
        && !IsSelectedGameRunning
        && SelectedCloudEndpoint is not null
        && (CloudSyncPlan is null
            || CloudSyncPlan.Relation is CloudSyncRelation.FastForwardPush
                or CloudSyncRelation.FastForwardPull
                or CloudSyncRelation.UpToDate
                or CloudSyncRelation.Diverged);

    public string LocalDivergenceCardTitle => CloudSyncPlan?.LocalHeadSnapshot is { } local
        ? $"本机进度 · {ActiveBranchNameText} (#{local.ShortId})"
        : "本机当前进度";

    public string LocalDivergenceCardDetails => CloudSyncPlan?.LocalHeadSnapshot is { } local
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"时间：{local.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n游玩：{UiFormatters.FormatDurationHms(local.PlaytimeActiveSeconds)} · {local.FileCount} 文件 · {UiFormatters.FormatByteSize(local.TotalRawBytes)}\n备注：{(string.IsNullOrWhiteSpace(local.Note) ? "（无备注）" : local.Note)}")
        : "暂无本机快照详情";

    public string RemoteDivergenceCardTitle => CloudSyncPlan?.RemoteHead is { } remote
        ? $"远端进度 · {remote.BranchName ?? "main"} ({remote.MachineName ?? remote.MachineId})"
        : "远端云同步进度";

    public string RemoteDivergenceCardDetails => CloudSyncPlan?.RemoteHead is { } remote
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"时间：{remote.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n游玩：{UiFormatters.FormatDurationHms(remote.PlaytimeActiveSeconds)} · {remote.FileCount} 文件 · {UiFormatters.FormatByteSize(remote.TotalRawBytes)}\n快照：#{(remote.HeadSnapshotId.Length >= 8 ? remote.HeadSnapshotId[^8..].ToLowerInvariant() : remote.HeadSnapshotId.ToLowerInvariant())}")
        : "暂无远端快照详情";

    public string CommonAncestorSummaryText => !string.IsNullOrWhiteSpace(CloudSyncPlan?.CommonAncestorSnapshotId)
        ? $"共同分叉祖先：#{(CloudSyncPlan!.CommonAncestorSnapshotId!.Length >= 8 ? CloudSyncPlan.CommonAncestorSnapshotId[^8..].ToLowerInvariant() : CloudSyncPlan.CommonAncestorSnapshotId.ToLowerInvariant())}"
        : "共同祖先：未在最近 64 代链中命中（独立双端历史）";

    public SaveWorkbenchViewModel(
        IGameLaunchService launchService,
        ISaveTimeMachineService? saveService = null,
        ICloudSaveSyncService? cloudSyncService = null,
        ISaveWatcherService? watcherService = null,
        ILudusaviRuleResolver? ruleResolver = null,
        Action<string>? openFolder = null,
        Func<string?>? pickExportFolder = null)
    {
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _saveService = saveService;
        _cloudSyncService = cloudSyncService;
        _watcherService = watcherService;
        _ruleResolver = ruleResolver;
        _openFolder = openFolder ?? OpenDirectoryInExplorer;
        _pickExportFolder = pickExportFolder ?? BrowseForExportDirectory;

        if (_watcherService is not null)
        {
            _watcherService.WatcherStatusChanged += OnWatcherStatusChanged;
        }
    }

    public async Task LoadForGameAsync(GameListItemViewModel? game, CancellationToken ct = default)
    {
        int generation = Interlocked.Increment(ref _loadGeneration);
        _currentGame = game;
        IsSelectedGameRunning = game is not null
            && (game.IsRunning || _launchService.ActiveSessions.ContainsKey(game.GameId));

        CloseTransientPopovers();
        IsInspectorOpenOnCompact = false;
        CloudSyncPlan = null;
        IsDivergenceModalOpen = false;

        if (game is null)
        {
            _rawGraph = null;
            _gameState = null;
            _resolvedLayout = null;
            Branches.Clear();
            GraphRows.Clear();
            SelectedNode = null;
            SelectedNodeFiles.Clear();
            SelectedNodeDiffItems.Clear();
            TotalSnapshotCount = 0;
            TotalBranchCount = 0;
            TotalRawBytes = 0;
            TotalDeduplicatedBytes = 0;
            DeduplicationRatio = 0;
            ResolvedLocationsSummaryText = string.Empty;
            NotifyAllDerivedStates();
            return;
        }

        if (_saveService is null)
        {
            _rawGraph = null;
            Branches.Clear();
            GraphRows.Clear();
            SelectedNode = null;
            ResolvedLocationsSummaryText = "存档时间机器服务正在初始化…";
            NotifyAllDerivedStates();
            return;
        }

        try
        {
            SaveGameStateRecord state = await _saveService.GetGameStateAsync(game.GameId, ct).ConfigureAwait(true);
            SaveTreeGraphDto graph = await _saveService.GetSaveTreeGraphAsync(game.GameId, ct).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            _gameState = state;
            _suppressAutoSaveToggles = true;
            try
            {
                AutoWatchEnabled = state.AutoWatchEnabled;
                PostExitSnapshotEnabled = state.PostExitSnapshotEnabled;
                IncludeConfigOnSnapshot = state.IncludeConfigFiles;
                HasUnresolvedCloudConflict = state.HasUnresolvedCloudConflict || graph.HasUnresolvedCloudConflict;
                LastSyncedAtUtc = state.LastSyncedAtUtc ?? graph.LastSyncedAtUtc;
            }
            finally
            {
                _suppressAutoSaveToggles = false;
            }

            if (_watcherService is not null)
            {
                WatcherStatus = _watcherService.GetStatus(game.GameId).Status;
            }
            else
            {
                WatcherStatus = SaveWatcherStatusKind.Stopped;
            }

            ApplyTreeGraph(graph, preserveSelectionId: SelectedNode?.SnapshotId);

            await LoadCloudEndpointsAsync(state.CloudEndpointId, ct).ConfigureAwait(true);
            await RefreshResolvedLocationsSummaryAsync(game.GameId, refreshOnline: false, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"加载游戏存档树失败：{ex.Message}", true);
        }
        finally
        {
            NotifyAllDerivedStates();
        }
    }

    public async Task ReloadCurrentGameIfMatchesAsync(string gameId, CancellationToken ct = default)
    {
        if (_currentGame is null
            || _saveService is null
            || !string.Equals(_currentGame.GameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? keepId = SelectedNode?.SnapshotId;
        SaveGameStateRecord state = await _saveService.GetGameStateAsync(gameId, ct).ConfigureAwait(true);
        SaveTreeGraphDto graph = await _saveService.GetSaveTreeGraphAsync(gameId, ct).ConfigureAwait(true);
        _gameState = state;
        HasUnresolvedCloudConflict = state.HasUnresolvedCloudConflict || graph.HasUnresolvedCloudConflict;
        LastSyncedAtUtc = state.LastSyncedAtUtc ?? graph.LastSyncedAtUtc;
        ApplyTreeGraph(graph, preserveSelectionId: keepId);
        NotifyAllDerivedStates();
    }

    public void UpdateSelectedGameRunningState(bool isRunning)
    {
        IsSelectedGameRunning = isRunning;
        NotifyAllDerivedStates();
    }

    public void UpdateViewportWidth(double controlWidth)
    {
        bool compact = controlWidth > 0 && controlWidth < 740.0;
        if (IsCompactLayout != compact)
        {
            IsCompactLayout = compact;
            if (!compact)
            {
                IsInspectorOpenOnCompact = false;
            }

            OnPropertyChanged(nameof(ShowDesktopInspector));
            OnPropertyChanged(nameof(ShowCompactInspectorOverlay));
        }
    }

    partial void OnTreeFilterModeChanged(SaveTreeFilterMode value) => RebuildLayoutRows(SelectedNode?.SnapshotId);
    partial void OnTreeSearchTextChanged(string value) => RebuildLayoutRows(SelectedNode?.SnapshotId);

    partial void OnSelectedNodeChanged(SaveSnapshotRowViewModel? oldValue, SaveSnapshotRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            if (IsCompactLayout)
            {
                IsInspectorOpenOnCompact = true;
            }
        }

        IsEditingNote = false;
        IsRollbackConfirmOpen = false;
        RollbackRequiresModMismatchConfirm = false;
        NotifyAllDerivedStates();
        _ = LoadSelectedNodeInspectorAsync(newValue);
    }

    partial void OnInspectorTabChanged(SaveInspectorTab value)
    {
        OnPropertyChanged(nameof(ShowFilesInspectorTab));
        OnPropertyChanged(nameof(ShowDiffInspectorTab));
        if (value == SaveInspectorTab.Diff && SelectedNode is not null)
        {
            _ = LoadSelectedNodeDiffAsync(SelectedNode);
        }
    }

    partial void OnDiffTargetModeChanged(SaveDiffTargetMode value)
    {
        if (SelectedNode is not null)
        {
            _ = LoadSelectedNodeDiffAsync(SelectedNode);
        }
    }

    partial void OnAutoWatchEnabledChanged(bool value)
    {
        if (_suppressAutoSaveToggles)
        {
            return;
        }

        _ = PersistGameStateTogglesAsync();
    }

    partial void OnPostExitSnapshotEnabledChanged(bool value)
    {
        if (_suppressAutoSaveToggles)
        {
            return;
        }

        _ = PersistGameStateTogglesAsync();
    }

    partial void OnIncludeConfigOnSnapshotChanged(bool value)
    {
        if (_suppressAutoSaveToggles)
        {
            return;
        }

        _ = PersistGameStateTogglesAsync();
    }

    partial void OnSelectedCloudEndpointChanged(CloudEndpointItemViewModel? value)
    {
        OnPropertyChanged(nameof(CloudStatusChipText));
        OnPropertyChanged(nameof(CanExecuteDirectCloudSync));
        if (!_suppressAutoSaveToggles && _currentGame is not null && _gameState is not null)
        {
            _ = PersistGameStateTogglesAsync();
        }
    }

    partial void OnActiveBranchChanged(SaveBranchItemViewModel? oldValue, SaveBranchItemViewModel? newValue)
    {
        OnPropertyChanged(nameof(ActiveBranchNameText));
        OnPropertyChanged(nameof(ActiveBranchColorBrush));
        OnPropertyChanged(nameof(CanDeleteActiveBranch));

        if (_suppressBranchSelectionCommand
            || newValue is null
            || _currentGame is null
            || _rawGraph is null
            || string.Equals(newValue.BranchId, _rawGraph.ActiveBranchId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = SwitchToBranchAsync(newValue);
    }

    [RelayCommand]
    private void SelectNode(SaveSnapshotRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (ReferenceEquals(SelectedNode, row) && IsCompactLayout)
        {
            IsInspectorOpenOnCompact = true;
            OnPropertyChanged(nameof(ShowCompactInspectorOverlay));
            return;
        }

        SelectedNode = row;
    }

    [RelayCommand]
    private void CloseCompactInspector()
    {
        IsInspectorOpenOnCompact = false;
        OnPropertyChanged(nameof(ShowCompactInspectorOverlay));
    }

    [RelayCommand]
    private void ClearTreeSearch() => TreeSearchText = string.Empty;

    [RelayCommand]
    private void ToggleCloudDrawer() => IsCloudDrawerOpen = !IsCloudDrawerOpen;

    [RelayCommand]
    private void OpenSavesSettings() => OpenSavesSettingsRequested?.Invoke();

    [RelayCommand]
    public async Task CreateSnapshotAsync()
    {
        if (_currentGame is null || _saveService is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在通过 FastCDC + BLAKE3 创建增量存档快照…";
        NotifyAllDerivedStates();

        try
        {
            string? note = string.IsNullOrWhiteSpace(QuickSnapshotNote) ? null : QuickSnapshotNote.Trim();
            CreateSaveSnapshotResult result = await _saveService.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = _currentGame.GameId,
                TriggerKind = PinNewSnapshot ? SaveSnapshotTriggerKind.ManualPin : SaveSnapshotTriggerKind.Manual,
                BranchId = ActiveBranch?.BranchId,
                Note = note,
                IncludeConfigFiles = IncludeConfigOnSnapshot,
                Pin = PinNewSnapshot,
            }).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusFeedbackRequested?.Invoke(result.Message, !result.Skipped);
                return;
            }

            QuickSnapshotNote = string.Empty;
            PinNewSnapshot = false;
            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, result.Snapshot?.SnapshotId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(
                result.DeduplicatedBytesSaved > 0
                    ? $"{result.Message}（去重节省 {UiFormatters.FormatByteSize(result.DeduplicatedBytesSaved)}）"
                    : result.Message,
                false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"创建存档快照失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    private void OpenForkPopoverFromSelected()
    {
        if (SelectedNode is null)
        {
            OpenForkPopoverFromHead();
            return;
        }

        OpenForkPopoverCore(SelectedNode.SnapshotId, $"{SelectedNode.DisplayTitle} ({SelectedNode.ShortId})");
    }

    [RelayCommand]
    private void OpenForkPopoverFromHead()
    {
        string? headId = _rawGraph?.CurrentHeadSnapshotId ?? SelectedNode?.SnapshotId;
        string label = !string.IsNullOrWhiteSpace(headId)
            ? $"当前 HEAD (#{(headId.Length >= 8 ? headId[^8..].ToLowerInvariant() : headId.ToLowerInvariant())})"
            : "当前分支起点";
        OpenForkPopoverCore(headId, label);
    }

    private void OpenForkPopoverCore(string? fromSnapshotId, string sourceLabel)
    {
        CloseTransientPopovers();
        ForkSourceSnapshotId = fromSnapshotId;
        ForkSourceLabel = sourceLabel;
        ForkBranchNameInput = string.Create(CultureInfo.InvariantCulture, $"branch-{Branches.Count + 1}");
        ForkBranchDescriptionInput = string.Empty;
        SelectedForkColorHex = SaveBranchColors.PickForIndex(Branches.Count);
        SwitchToForkedBranch = !IsSelectedGameRunning;
        IsForkPanelOpen = true;
    }

    [RelayCommand]
    private void SelectForkColor(string? colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            SelectedForkColorHex = colorHex;
        }
    }

    [RelayCommand]
    private void CancelForkPopover() => IsForkPanelOpen = false;

    [RelayCommand]
    public async Task ConfirmForkBranchAsync()
    {
        if (_currentGame is null || _saveService is null || IsBusy)
        {
            return;
        }

        string name = ForkBranchNameInput.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusFeedbackRequested?.Invoke("请输入新分支名称（例如 boss-rush、bad-ending）", true);
            return;
        }

        bool switchNow = SwitchToForkedBranch && !IsSelectedGameRunning;
        IsBusy = true;
        BusyMessage = $"正在创建分支「{name}」…";
        NotifyAllDerivedStates();

        try
        {
            ForkBranchResult result = await _saveService.CreateBranchAsync(new ForkBranchRequest
            {
                GameId = _currentGame.GameId,
                FromSnapshotId = ForkSourceSnapshotId,
                BranchName = name,
                Description = string.IsNullOrWhiteSpace(ForkBranchDescriptionInput) ? null : ForkBranchDescriptionInput.Trim(),
                ColorHex = SelectedForkColorHex,
                SwitchToNewBranch = switchNow,
                CreateSafetySnapshotIfSwitching = true,
            }).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusFeedbackRequested?.Invoke(result.Message, true);
                return;
            }

            IsForkPanelOpen = false;
            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, result.Branch?.HeadSnapshotId ?? ForkSourceSnapshotId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(result.Message, false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"创建分支失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task SwitchToBranchAsync(SaveBranchItemViewModel? targetBranch)
    {
        if (targetBranch is null || _currentGame is null || _saveService is null || IsBusy)
        {
            return;
        }

        if (IsSelectedGameRunning)
        {
            StatusFeedbackRequested?.Invoke("游戏运行中已禁止切换分支，请先退出游戏后再切换存档线", true);
            RestoreActiveBranchSelectionFromGraph();
            return;
        }

        IsBusy = true;
        BusyMessage = $"正在切换至分支「{targetBranch.Name}」并原子检出存档…";
        NotifyAllDerivedStates();

        try
        {
            SaveRestoreOutcome outcome = await _saveService.SwitchBranchAsync(new SwitchBranchRequest
            {
                GameId = _currentGame.GameId,
                TargetBranchId = targetBranch.BranchId,
                CreateSafetySnapshot = true,
                IncludeConfigFiles = IncludeConfigOnSnapshot,
            }).ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                RestoreActiveBranchSelectionFromGraph();
                StatusFeedbackRequested?.Invoke(outcome.Message, true);
                return;
            }

            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, outcome.TargetSnapshot?.SnapshotId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(outcome.Message, false);
        }
        catch (Exception ex)
        {
            RestoreActiveBranchSelectionFromGraph();
            StatusFeedbackRequested?.Invoke($"切换分支失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    private void OpenRenameBranchPopover()
    {
        if (ActiveBranch is null)
        {
            return;
        }

        CloseTransientPopovers();
        RenameBranchNameInput = ActiveBranch.Name;
        RenameBranchColorHex = ActiveBranch.ColorHex;
        IsRenameBranchPanelOpen = true;
    }

    [RelayCommand]
    private void SelectRenameBranchColor(string? colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            RenameBranchColorHex = colorHex;
        }
    }

    [RelayCommand]
    private void CancelRenameBranch() => IsRenameBranchPanelOpen = false;

    [RelayCommand]
    public async Task ConfirmRenameBranchAsync()
    {
        if (_currentGame is null || _saveService is null || ActiveBranch is null || IsBusy)
        {
            return;
        }

        string newName = RenameBranchNameInput.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusFeedbackRequested?.Invoke("分支名称不能为空", true);
            return;
        }

        IsBusy = true;
        NotifyAllDerivedStates();
        try
        {
            SaveBranchRecord? updated = await _saveService.RenameBranchAsync(
                ActiveBranch.BranchId,
                newName,
                ActiveBranch.Description,
                RenameBranchColorHex).ConfigureAwait(true);

            if (updated is null)
            {
                StatusFeedbackRequested?.Invoke("重命名分支失败，请检查名称是否重复", true);
                return;
            }

            IsRenameBranchPanelOpen = false;
            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, SelectedNode?.SnapshotId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke($"已更新分支为「{updated.Name}」", false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"重命名分支失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task DeleteActiveBranchAsync()
    {
        if (_currentGame is null || _saveService is null || ActiveBranch is null || IsBusy)
        {
            return;
        }

        if (ActiveBranch.IsDefault)
        {
            StatusFeedbackRequested?.Invoke("默认主分支（main）不可删除", true);
            return;
        }

        IsBusy = true;
        NotifyAllDerivedStates();
        try
        {
            DeleteBranchResult result = await _saveService.DeleteBranchAsync(ActiveBranch.BranchId, deleteSnapshots: false).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                StatusFeedbackRequested?.Invoke(result.Message, true);
                return;
            }

            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, preserveSelectionId: null).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(result.Message, false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"删除分支失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    private void RequestRollbackSelected()
    {
        if (SelectedNode is null)
        {
            return;
        }

        if (IsSelectedGameRunning)
        {
            StatusFeedbackRequested?.Invoke("游戏运行中已禁止回滚存档，请先退出游戏后再操作", true);
            return;
        }

        CloseTransientPopovers();
        RollbackIncludeConfig = false;
        RollbackRequiresModMismatchConfirm = false;
        RollbackModMismatchWarning = string.Empty;
        IsRollbackConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelRollbackConfirm()
    {
        IsRollbackConfirmOpen = false;
        RollbackRequiresModMismatchConfirm = false;
        RollbackModMismatchWarning = string.Empty;
    }

    [RelayCommand]
    public Task ConfirmRollbackSelectedAsync() =>
        ExecuteRollbackCoreAsync(SaveModMismatchPolicy.AbortAndPrompt);

    [RelayCommand]
    public Task ForceRollbackIgnoringModMismatchAsync() =>
        ExecuteRollbackCoreAsync(SaveModMismatchPolicy.ProceedAnyway);

    private async Task ExecuteRollbackCoreAsync(SaveModMismatchPolicy mismatchPolicy)
    {
        if (_currentGame is null || _saveService is null || SelectedNode is null || IsBusy)
        {
            return;
        }

        if (IsSelectedGameRunning)
        {
            StatusFeedbackRequested?.Invoke("游戏正在运行中，已拒绝回滚操作", true);
            return;
        }

        string targetId = SelectedNode.SnapshotId;
        IsBusy = true;
        BusyMessage = "正在自动创建回滚安全点并原子还原存档…";
        NotifyAllDerivedStates();

        try
        {
            SaveRestoreOutcome outcome = await _saveService.RollbackToSnapshotAsync(new RollbackSaveRequest
            {
                SnapshotId = targetId,
                IncludeConfigFiles = RollbackIncludeConfig,
                ModMismatchPolicy = mismatchPolicy,
                CreateSafetySnapshot = true,
            }).ConfigureAwait(true);

            if (outcome.RequiresModMismatchConfirmation)
            {
                RollbackRequiresModMismatchConfirm = true;
                RollbackModMismatchWarning = outcome.Message;
                StatusFeedbackRequested?.Invoke(outcome.Message, true);
                return;
            }

            if (!outcome.Succeeded)
            {
                StatusFeedbackRequested?.Invoke(outcome.Message, true);
                return;
            }

            IsRollbackConfirmOpen = false;
            RollbackRequiresModMismatchConfirm = false;
            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, targetId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(outcome.Message, false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"回滚快照失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task TogglePinSelectedAsync()
    {
        if (_currentGame is null || _saveService is null || SelectedNode is null || IsBusy)
        {
            return;
        }

        bool nextPin = !SelectedNode.IsPinned;
        try
        {
            bool ok = await _saveService.SetSnapshotPinnedAsync(SelectedNode.SnapshotId, nextPin).ConfigureAwait(true);
            if (!ok)
            {
                StatusFeedbackRequested?.Invoke("更新固定状态失败", true);
                return;
            }

            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, SelectedNode.SnapshotId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(nextPin ? "已固定此快照（★ 永不自动清理）" : "已取消固定此快照", false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"更新固定状态失败：{ex.Message}", true);
        }
    }

    [RelayCommand]
    private void BeginEditNote()
    {
        if (SelectedNode is null)
        {
            return;
        }

        EditNoteInput = SelectedNode.Note ?? string.Empty;
        IsEditingNote = true;
    }

    [RelayCommand]
    private void CancelEditNote() => IsEditingNote = false;

    [RelayCommand]
    public async Task SaveEditNoteAsync()
    {
        if (_currentGame is null || _saveService is null || SelectedNode is null || IsBusy)
        {
            return;
        }

        string? normalized = string.IsNullOrWhiteSpace(EditNoteInput) ? null : EditNoteInput.Trim();
        try
        {
            bool ok = await _saveService.UpdateSnapshotNoteAsync(SelectedNode.SnapshotId, normalized).ConfigureAwait(true);
            if (!ok)
            {
                StatusFeedbackRequested?.Invoke("保存备注失败", true);
                return;
            }

            IsEditingNote = false;
            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, SelectedNode.SnapshotId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke("已更新快照备注", false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"更新快照备注失败：{ex.Message}", true);
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedSnapshotAsync()
    {
        if (_currentGame is null || _saveService is null || SelectedNode is null || IsBusy)
        {
            return;
        }

        if (SelectedNode.IsHead)
        {
            StatusFeedbackRequested?.Invoke("当前 HEAD 快照不可直接删除", true);
            return;
        }

        if (SelectedNode.IsPinned)
        {
            StatusFeedbackRequested?.Invoke("已固定（★）的快照受保护，请先取消固定后再删除", true);
            return;
        }

        IsBusy = true;
        NotifyAllDerivedStates();
        try
        {
            DeleteSnapshotResult result = await _saveService.DeleteSnapshotAsync(SelectedNode.SnapshotId).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                StatusFeedbackRequested?.Invoke(result.Message, true);
                return;
            }

            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, preserveSelectionId: null).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(result.Message, false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"删除快照失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task ExportSelectedSnapshotAsync()
    {
        if (_saveService is null || SelectedNode is null || IsBusy)
        {
            return;
        }

        string? targetDir = _pickExportFolder();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在解压 CAS 块并导出快照副本…";
        NotifyAllDerivedStates();
        try
        {
            ExportSnapshotResult result = await _saveService.ExportSnapshotToFolderAsync(SelectedNode.SnapshotId, targetDir).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(result.Message, !result.Succeeded);
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.ExportDirectory) && Directory.Exists(result.ExportDirectory))
            {
                _openFolder(result.ExportDirectory);
            }
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"导出快照失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    private void OpenSaveFolder()
    {
        string? dir = _resolvedLayout?.SaveLocations
            .Select(l => l.ResolvedFullPath)
            .Select(p => File.Exists(p) ? Path.GetDirectoryName(p) : p)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));

        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = _currentGame?.Record.InstallDir;
        }

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            StatusFeedbackRequested?.Invoke("尚未定位到已存在的存档文件夹", true);
            return;
        }

        try
        {
            _openFolder(dir);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"打开存档文件夹失败：{ex.Message}", true);
        }
    }

    [RelayCommand]
    private void JumpToParentSnapshot()
    {
        if (SelectedNode?.ParentSnapshotId is not { } parentId)
        {
            return;
        }

        SaveSnapshotRowViewModel? parentRow = GraphRows.FirstOrDefault(
            r => string.Equals(r.SnapshotId, parentId, StringComparison.OrdinalIgnoreCase));
        if (parentRow is not null)
        {
            SelectedNode = parentRow;
        }
    }

    [RelayCommand]
    public async Task RefreshSaveLocationsAsync()
    {
        if (_currentGame is null)
        {
            return;
        }

        await RefreshResolvedLocationsSummaryAsync(_currentGame.GameId, refreshOnline: true).ConfigureAwait(true);
        StatusFeedbackRequested?.Invoke(ResolvedLocationsSummaryText, false);
    }

    [RelayCommand]
    public async Task ImportLegacyPreTrainerSnapshotsAsync()
    {
        if (_currentGame is null || _saveService is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "正在导入旧版修改器启动前快照至分支存档树…";
        NotifyAllDerivedStates();
        try
        {
            LegacyPreTrainerImportReport report = await _saveService.ImportLegacyPreTrainerSnapshotsAsync(_currentGame.GameId).ConfigureAwait(true);
            string? lastImportedId = report.ImportedSnapshotIds.Count > 0
                ? report.ImportedSnapshotIds[^1]
                : null;
            await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, lastImportedId).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(
                report.ImportedCount > 0
                    ? $"已无损导入 {report.ImportedCount} 份旧版修改器前快照（跳过已导入 {report.AlreadyImportedCount} 份）"
                    : "未发现尚未导入的旧版修改器启动前快照",
                false);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"导入旧版快照失败：{ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task PlanCloudSyncAsync()
    {
        if (_currentGame is null || _cloudSyncService is null || IsCloudSyncBusy)
        {
            return;
        }

        IsCloudDrawerOpen = true;
        IsCloudSyncBusy = true;
        CloudSyncProgressText = "正在拉取远端 64 代血缘指针并比对分叉关系…";
        CloudSyncProgressPercent = 20;
        NotifyAllDerivedStates();

        try
        {
            CloudSyncPlanPreview preview = await _cloudSyncService.PlanSyncAsync(
                _currentGame.GameId,
                SelectedCloudEndpoint?.EndpointId).ConfigureAwait(true);
            CloudSyncPlan = preview;
            HasUnresolvedCloudConflict = preview.Relation == CloudSyncRelation.Diverged;

            if (preview.Relation == CloudSyncRelation.Diverged)
            {
                IsDivergenceModalOpen = true;
            }

            StatusFeedbackRequested?.Invoke(preview.Message, preview.Relation == CloudSyncRelation.Error);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"检查云同步计划失败：{ex.Message}", true);
        }
        finally
        {
            IsCloudSyncBusy = false;
            CloudSyncProgressPercent = 0;
            CloudSyncProgressText = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    [RelayCommand]
    public async Task ExecuteCloudSyncAsync()
    {
        if (CloudSyncPlan?.Relation == CloudSyncRelation.Diverged)
        {
            IsDivergenceModalOpen = true;
            return;
        }

        await ExecuteCloudSyncCoreAsync(divergenceResolution: null).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ResolveDivergenceAdoptRemoteAsync()
    {
        IsDivergenceModalOpen = false;
        await ExecuteCloudSyncCoreAsync(CloudDivergenceResolution.AdoptRemoteAndBackupLocal).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ResolveDivergenceKeepLocalAsync()
    {
        IsDivergenceModalOpen = false;
        await ExecuteCloudSyncCoreAsync(CloudDivergenceResolution.KeepLocalAndBackupRemote).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelDivergenceModal() => IsDivergenceModalOpen = false;

    private async Task ExecuteCloudSyncCoreAsync(CloudDivergenceResolution? divergenceResolution)
    {
        if (_currentGame is null || _cloudSyncService is null || IsCloudSyncBusy)
        {
            return;
        }

        if (IsSelectedGameRunning)
        {
            StatusFeedbackRequested?.Invoke("游戏运行中已禁止执行云同步写入，请先退出游戏", true);
            return;
        }

        IsCloudSyncBusy = true;
        CloudSyncProgressPercent = 5;
        CloudSyncProgressText = "正在准备 32MB SPPK 增量封包与血缘校验…";
        NotifyAllDerivedStates();

        try
        {
            var progress = new Progress<CloudSyncProgressTick>(tick =>
            {
                CloudSyncProgressText = tick.Message;
                CloudSyncProgressPercent = tick.TotalUnits > 0
                    ? Math.Clamp((double)tick.CompletedUnits / tick.TotalUnits * 100.0, 5.0, 100.0)
                    : 50.0;
            });

            CloudSyncExecutionReport report = await _cloudSyncService.ExecuteSyncAsync(
                _currentGame.GameId,
                SelectedCloudEndpoint?.EndpointId,
                divergenceResolution,
                progress).ConfigureAwait(true);

            if (report.Relation == CloudSyncRelation.Diverged && divergenceResolution is null)
            {
                CloudSyncPlan = await _cloudSyncService.PlanSyncAsync(
                    _currentGame.GameId,
                    SelectedCloudEndpoint?.EndpointId).ConfigureAwait(true);
                HasUnresolvedCloudConflict = true;
                IsDivergenceModalOpen = true;
                return;
            }

            if (report.Succeeded)
            {
                CloudSyncPlan = null;
                HasUnresolvedCloudConflict = false;
                LastSyncedAtUtc = DateTimeOffset.UtcNow;
                await ReloadCurrentGameAndNotifyParentAsync(_currentGame.GameId, report.ResultHeadSnapshotId).ConfigureAwait(true);
            }

            StatusFeedbackRequested?.Invoke(report.Message, !report.Succeeded);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"执行云同步失败：{ex.Message}", true);
        }
        finally
        {
            IsCloudSyncBusy = false;
            CloudSyncProgressPercent = 0;
            CloudSyncProgressText = string.Empty;
            NotifyAllDerivedStates();
        }
    }

    private void ApplyTreeGraph(SaveTreeGraphDto graph, string? preserveSelectionId)
    {
        _rawGraph = graph;
        TotalSnapshotCount = graph.Nodes.Count;
        TotalBranchCount = graph.Branches.Count;
        IsDetachedHead = graph.IsDetachedHead;
        TotalRawBytes = graph.TotalRawBytes;
        TotalDeduplicatedBytes = graph.TotalDeduplicatedBytes;
        DeduplicationRatio = graph.DeduplicationRatio;
        HasUnresolvedCloudConflict = graph.HasUnresolvedCloudConflict;
        LastSyncedAtUtc = graph.LastSyncedAtUtc ?? LastSyncedAtUtc;

        _suppressBranchSelectionCommand = true;
        try
        {
            Branches.Clear();
            SaveBranchItemViewModel? matchedActive = null;
            foreach (SaveBranchRecord branch in graph.Branches)
            {
                bool isActive = string.Equals(branch.BranchId, graph.ActiveBranchId, StringComparison.OrdinalIgnoreCase);
                var vm = new SaveBranchItemViewModel(branch, isActive);
                Branches.Add(vm);
                if (isActive)
                {
                    matchedActive = vm;
                }
            }

            ActiveBranch = matchedActive ?? Branches.FirstOrDefault();
        }
        finally
        {
            _suppressBranchSelectionCommand = false;
        }

        RebuildLayoutRows(preserveSelectionId ?? graph.CurrentHeadSnapshotId);
    }

    private void RebuildLayoutRows(string? preferredSnapshotId)
    {
        SaveGraphLayoutResult layout = SaveTreeGraphLayoutEngine.ComputeLayout(
            _rawGraph,
            TreeFilterMode,
            TreeSearchText,
            ActiveBranch?.BranchId);

        GraphColumnWidth = layout.RecommendedGraphWidth;

        var colorByBranchName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_rawGraph is not null)
        {
            foreach (SaveBranchRecord b in _rawGraph.Branches)
            {
                colorByBranchName[b.Name] = b.ColorHex;
            }
        }

        string? activeBranchName = ActiveBranch?.Name ?? _rawGraph?.ActiveBranchName;

        GraphRows.Clear();
        SaveSnapshotRowViewModel? toSelect = null;
        foreach (SaveGraphRowLayout row in layout.Rows)
        {
            var rowVm = new SaveSnapshotRowViewModel(row, activeBranchName, colorByBranchName);
            GraphRows.Add(rowVm);

            if (!string.IsNullOrWhiteSpace(preferredSnapshotId)
                && string.Equals(rowVm.SnapshotId, preferredSnapshotId, StringComparison.OrdinalIgnoreCase))
            {
                toSelect = rowVm;
            }
        }

        toSelect ??= GraphRows.FirstOrDefault(r => r.IsHead) ?? GraphRows.FirstOrDefault();
        SelectedNode = toSelect;

        OnPropertyChanged(nameof(HasSnapshots));
        OnPropertyChanged(nameof(HasVisibleGraphRows));
        OnPropertyChanged(nameof(TreeMetricsSummaryText));
    }

    private async Task LoadSelectedNodeInspectorAsync(SaveSnapshotRowViewModel? row)
    {
        int gen = Interlocked.Increment(ref _inspectorGeneration);
        SelectedNodeFiles.Clear();
        SelectedNodeDiffItems.Clear();
        DiffSummaryText = string.Empty;

        if (row is null || _saveService is null)
        {
            OnPropertyChanged(nameof(HasSelectedNodeFiles));
            OnPropertyChanged(nameof(HasSelectedNodeDiffItems));
            return;
        }

        IsLoadingInspector = true;
        try
        {
            IReadOnlyList<SaveSnapshotFileRecord> files = await _saveService.GetSnapshotFilesAsync(row.SnapshotId).ConfigureAwait(true);
            if (gen != Volatile.Read(ref _inspectorGeneration))
            {
                return;
            }

            foreach (SaveSnapshotFileRecord file in files)
            {
                SelectedNodeFiles.Add(new SaveFileEntryViewModel(file));
            }

            if (InspectorTab == SaveInspectorTab.Diff)
            {
                await LoadSelectedNodeDiffCoreAsync(row, gen).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            DiffSummaryText = $"读取快照清单失败：{ex.Message}";
        }
        finally
        {
            if (gen == Volatile.Read(ref _inspectorGeneration))
            {
                IsLoadingInspector = false;
                OnPropertyChanged(nameof(HasSelectedNodeFiles));
                OnPropertyChanged(nameof(HasSelectedNodeDiffItems));
            }
        }
    }

    private Task LoadSelectedNodeDiffAsync(SaveSnapshotRowViewModel row)
    {
        int gen = Interlocked.Increment(ref _inspectorGeneration);
        return LoadSelectedNodeDiffCoreAsync(row, gen);
    }

    private async Task LoadSelectedNodeDiffCoreAsync(SaveSnapshotRowViewModel row, int generation)
    {
        if (_saveService is null)
        {
            return;
        }

        SelectedNodeDiffItems.Clear();
        try
        {
            SaveSnapshotDiffDto diff;
            if (DiffTargetMode == SaveDiffTargetMode.CurrentDisk)
            {
                diff = await _saveService.DiffSnapshotWithDiskAsync(row.SnapshotId).ConfigureAwait(true);
            }
            else if (!string.IsNullOrWhiteSpace(row.ParentSnapshotId))
            {
                diff = await _saveService.DiffSnapshotsAsync(row.ParentSnapshotId, row.SnapshotId).ConfigureAwait(true);
            }
            else
            {
                // Root snapshot: all captured files are Added relative to empty state.
                IReadOnlyList<SaveSnapshotFileRecord> files = await _saveService.GetSnapshotFilesAsync(row.SnapshotId).ConfigureAwait(true);
                diff = new SaveSnapshotDiffDto
                {
                    BaseLabel = "空初始状态",
                    TargetLabel = row.ShortId,
                    AddedCount = files.Count,
                    Items = files.Select(f => new SaveFileDiffItemDto
                    {
                        LogicalPath = f.LogicalPath,
                        Category = f.Category,
                        DiffKind = SaveFileDiffKind.Added,
                        LeftSize = 0,
                        RightSize = f.FileSize,
                        RightHash = f.FileBlake3,
                    }).ToList(),
                };
            }

            if (generation != Volatile.Read(ref _inspectorGeneration))
            {
                return;
            }

            foreach (SaveFileDiffItemDto item in diff.Items.Where(i => i.DiffKind != SaveFileDiffKind.Unchanged))
            {
                SelectedNodeDiffItems.Add(new SaveDiffEntryViewModel(item));
            }

            DiffSummaryText = string.Create(
                CultureInfo.InvariantCulture,
                $"{diff.BaseLabel} → {diff.TargetLabel}：+{diff.AddedCount} 新增 · ~{diff.ModifiedCount} 变更 · -{diff.DeletedCount} 删除 · {diff.UnchangedCount} 未变");
        }
        catch (Exception ex)
        {
            DiffSummaryText = $"计算差异失败：{ex.Message}";
        }
        finally
        {
            OnPropertyChanged(nameof(HasSelectedNodeDiffItems));
        }
    }

    private async Task LoadCloudEndpointsAsync(string? boundEndpointId, CancellationToken ct)
    {
        if (_cloudSyncService is null)
        {
            return;
        }

        IReadOnlyList<CloudSyncEndpointRecord> endpoints = await _cloudSyncService.ListEndpointsAsync(ct).ConfigureAwait(true);
        _suppressAutoSaveToggles = true;
        try
        {
            AvailableCloudEndpoints.Clear();
            CloudEndpointItemViewModel? matched = null;
            CloudEndpointItemViewModel? defaultItem = null;
            foreach (CloudSyncEndpointRecord ep in endpoints)
            {
                var vm = new CloudEndpointItemViewModel(ep);
                AvailableCloudEndpoints.Add(vm);
                if (!string.IsNullOrWhiteSpace(boundEndpointId)
                    && string.Equals(ep.EndpointId, boundEndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    matched = vm;
                }

                if (ep.IsDefault)
                {
                    defaultItem = vm;
                }
            }

            SelectedCloudEndpoint = matched ?? defaultItem;
        }
        finally
        {
            _suppressAutoSaveToggles = false;
        }
    }

    private async Task RefreshResolvedLocationsSummaryAsync(string gameId, bool refreshOnline, CancellationToken ct = default)
    {
        if (_ruleResolver is null)
        {
            ResolvedLocationsSummaryText = "已就绪 · 自动复用游戏数据路径";
            return;
        }

        try
        {
            ResolvedSaveLayout layout = await _ruleResolver.ResolveGameSaveLayoutAsync(gameId, refreshOnline, ct).ConfigureAwait(true);
            _resolvedLayout = layout;
            int saveCount = layout.SaveLocations.Count;
            int cfgCount = layout.ConfigLocations.Count;
            int regCount = layout.RegistryKeys.Count;
            string ruleSuffix = string.IsNullOrWhiteSpace(layout.MatchedRuleName)
                ? string.Empty
                : $" · 规则 {layout.MatchedRuleName}";
            ResolvedLocationsSummaryText = string.Create(
                CultureInfo.InvariantCulture,
                $"已定位 {saveCount} 处 [save] · {cfgCount} 处 [config] · {regCount} 项 HKCU 注册表{ruleSuffix}");
        }
        catch
        {
            ResolvedLocationsSummaryText = "未能探测到存档路径（可点击 ⋯ 刷新路径规则）";
        }
    }

    private async Task PersistGameStateTogglesAsync()
    {
        if (_currentGame is null || _saveService is null)
        {
            return;
        }

        try
        {
            SaveGameStateRecord baseState = _gameState ?? await _saveService.GetGameStateAsync(_currentGame.GameId).ConfigureAwait(true);
            SaveGameStateRecord updated = baseState with
            {
                AutoWatchEnabled = AutoWatchEnabled,
                PostExitSnapshotEnabled = PostExitSnapshotEnabled,
                IncludeConfigFiles = IncludeConfigOnSnapshot,
                CloudEndpointId = SelectedCloudEndpoint?.EndpointId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            _gameState = await _saveService.UpdateGameStateAsync(updated).ConfigureAwait(true);

            if (_watcherService is not null)
            {
                if (AutoWatchEnabled && IsSelectedGameRunning)
                {
                    await _watcherService.StartWatchingGameAsync(_currentGame.GameId).ConfigureAwait(true);
                }
                else if (!AutoWatchEnabled)
                {
                    await _watcherService.StopWatchingGameAsync(_currentGame.GameId).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"保存自动备份设置失败：{ex.Message}", true);
        }
    }

    private async Task ReloadCurrentGameAndNotifyParentAsync(string gameId, string? preserveSelectionId)
    {
        if (_saveService is null)
        {
            return;
        }

        SaveTreeGraphDto graph = await _saveService.GetSaveTreeGraphAsync(gameId).ConfigureAwait(true);
        ApplyTreeGraph(graph, preserveSelectionId);
        if (SaveTreeMutatedRequested is not null)
        {
            await SaveTreeMutatedRequested(gameId).ConfigureAwait(true);
        }
    }

    private void RestoreActiveBranchSelectionFromGraph()
    {
        _suppressBranchSelectionCommand = true;
        try
        {
            string? activeId = _rawGraph?.ActiveBranchId;
            ActiveBranch = Branches.FirstOrDefault(b => string.Equals(b.BranchId, activeId, StringComparison.OrdinalIgnoreCase))
                ?? Branches.FirstOrDefault();
        }
        finally
        {
            _suppressBranchSelectionCommand = false;
        }
    }

    private void CloseTransientPopovers()
    {
        IsForkPanelOpen = false;
        IsRenameBranchPanelOpen = false;
        IsRollbackConfirmOpen = false;
        IsEditingNote = false;
    }

    private void OnWatcherStatusChanged(object? sender, SaveWatcherStatusDto status)
    {
        if (_currentGame is null
            || !string.Equals(status.GameId, _currentGame.GameId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        void Apply()
        {
            WatcherStatus = status.Status;
            OnPropertyChanged(nameof(WatcherStatusBadgeText));
            OnPropertyChanged(nameof(IsWatcherActive));
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void NotifyAllDerivedStates()
    {
        OnPropertyChanged(nameof(HasSelectedGame));
        OnPropertyChanged(nameof(HasSnapshots));
        OnPropertyChanged(nameof(HasVisibleGraphRows));
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(ShowDesktopInspector));
        OnPropertyChanged(nameof(ShowCompactInspectorOverlay));
        OnPropertyChanged(nameof(ShowFilesInspectorTab));
        OnPropertyChanged(nameof(ShowDiffInspectorTab));
        OnPropertyChanged(nameof(HasSelectedNodeFiles));
        OnPropertyChanged(nameof(HasSelectedNodeDiffItems));
        OnPropertyChanged(nameof(CanCreateSnapshot));
        OnPropertyChanged(nameof(CanMutateDiskSaves));
        OnPropertyChanged(nameof(CanRollbackSelected));
        OnPropertyChanged(nameof(CanForkFromSelected));
        OnPropertyChanged(nameof(CanDeleteActiveBranch));
        OnPropertyChanged(nameof(CanDeleteSelectedSnapshot));
        OnPropertyChanged(nameof(ActiveBranchNameText));
        OnPropertyChanged(nameof(ActiveBranchColorBrush));
        OnPropertyChanged(nameof(TreeMetricsSummaryText));
        OnPropertyChanged(nameof(WatcherStatusBadgeText));
        OnPropertyChanged(nameof(IsWatcherActive));
        OnPropertyChanged(nameof(CloudStatusChipText));
        OnPropertyChanged(nameof(SelectedNodePinActionText));
        OnPropertyChanged(nameof(RollbackButtonToolTip));
        OnPropertyChanged(nameof(HasCloudSyncPlan));
        OnPropertyChanged(nameof(CloudPlanSummaryText));
        OnPropertyChanged(nameof(CloudPlanTransferMetricsText));
        OnPropertyChanged(nameof(CanExecuteDirectCloudSync));
        OnPropertyChanged(nameof(LocalDivergenceCardTitle));
        OnPropertyChanged(nameof(LocalDivergenceCardDetails));
        OnPropertyChanged(nameof(RemoteDivergenceCardTitle));
        OnPropertyChanged(nameof(RemoteDivergenceCardDetails));
        OnPropertyChanged(nameof(CommonAncestorSummaryText));
    }

    private static void OpenDirectoryInExplorer(string directoryPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directoryPath}\"",
            UseShellExecute = true,
        });
    }

    private static string? BrowseForExportDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择存档快照导出目录",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcherService is not null)
        {
            _watcherService.WatcherStatusChanged -= OnWatcherStatusChanged;
        }
    }
}
