using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spawn.Contracts;
using Spawn.Host.Controls;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels.Saves;

/// <summary>
/// Selectable branch color swatch option used in Fork Branch and Rename Branch popovers.
/// </summary>
public sealed partial class SaveBranchColorOption : ObservableObject
{
    public string ColorHex { get; }
    public string Label { get; }
    public Brush SwatchBrush { get; }

    [ObservableProperty]
    private bool _isSelectedForFork;

    [ObservableProperty]
    private bool _isSelectedForRename;

    public SaveBranchColorOption(string colorHex, string label)
    {
        ColorHex = colorHex;
        Label = label;
        SwatchBrush = CreateBrush(colorHex);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }
        catch
        {
            // Fallback below.
        }

        var fallback = new SolidColorBrush(Color.FromRgb(0x16, 0x77, 0xFF));
        fallback.Freeze();
        return fallback;
    }
}

/// <summary>
/// Named branch badge displayed on branch tip nodes inside the virtualized save tree list.
/// </summary>
public sealed record SaveBranchTipPillViewModel(string Name, string ColorHex, bool IsActiveBranch)
{
    public string DisplayName => string.Equals(Name, "main", StringComparison.OrdinalIgnoreCase)
        ? "主线 (main)"
        : Name;

    public Brush AccentBrush { get; } = CreateFrozenColorBrush(ColorHex, alpha: 255);
    public Brush SoftBackgroundBrush { get; } = CreateFrozenColorBrush(ColorHex, alpha: 34);

    private static SolidColorBrush CreateFrozenColorBrush(string hex, byte alpha)
    {
        Color color = Color.FromRgb(0x16, 0x77, 0xFF);
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color parsed)
            {
                color = parsed;
            }
        }
        catch
        {
            // Keep default blue.
        }

        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Named branch item displayed in the Branch Selector dropdown and branch management menu.
/// </summary>
public sealed partial class SaveBranchItemViewModel : ObservableObject
{
    public SaveBranchRecord Record { get; }
    public string BranchId => Record.BranchId;
    public string Name => Record.Name;
    public string? Description => Record.Description;
    public string ColorHex => Record.ColorHex;
    public bool IsDefault => Record.IsDefault;
    public int SnapshotCount => Record.SnapshotCount;
    public Brush ColorBrush { get; }

    [ObservableProperty]
    private bool _isActive;

    public string DisplayName => FormatBranchDisplayName(Name, IsDefault);

    public bool CanBackgroundDelete => !IsDefault && !IsActive;

    public string DisplayLabel => string.Create(
        CultureInfo.InvariantCulture,
        $"{DisplayName} ({SnapshotCount})");

    public string ComboSummaryText => IsActive
        ? $"{DisplayName} · 当前路线"
        : DisplayName;

    public SaveBranchItemViewModel(SaveBranchRecord record, bool isActive)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        _isActive = isActive;
        ColorBrush = ParseFrozenBrush(record.ColorHex);
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ComboSummaryText));
        OnPropertyChanged(nameof(CanBackgroundDelete));
    }

    public static string FormatBranchDisplayName(string? name, bool isDefault = false)
    {
        if (isDefault || string.Equals(name, "main", StringComparison.OrdinalIgnoreCase))
        {
            return "主线 (main)";
        }

        return string.IsNullOrWhiteSpace(name) ? "主线 (main)" : name.Trim();
    }

    private static SolidColorBrush ParseFrozenBrush(string? hex)
    {
        Color color = Color.FromRgb(0x16, 0x77, 0xFF);
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color parsed)
                {
                    color = parsed;
                }
            }
            catch
            {
                // Keep default accent.
            }
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Single virtualized row inside the Branching Save Tree list, combining precomputed Bezier graph layout
/// with branch tip pills, trigger glyph, tabular mono metadata, and selection state.
/// </summary>
public sealed partial class SaveSnapshotRowViewModel : ObservableObject
{
    public SaveGraphRowLayout GraphLayout { get; }
    public SaveTreeNodeDto Node => GraphLayout.Node;
    public SaveSnapshotRecord Snapshot => Node.Snapshot;

    public string SnapshotId => Snapshot.SnapshotId;
    public string ShortId => $"#{Snapshot.ShortId}";
    public string BranchId => Snapshot.BranchId;
    public string BranchName => Node.BranchName;
    public string BranchColorHex => GraphLayout.BranchColorHex;
    public string? ParentSnapshotId => Node.ParentSnapshotId;
    public bool HasParent => !string.IsNullOrWhiteSpace(ParentSnapshotId);

    public bool IsHead => GraphLayout.IsHead;
    public bool IsDetachedHead => GraphLayout.IsDetachedHead;
    public bool IsBranchTip => GraphLayout.IsBranchTip;
    public bool IsCloudSynced => Node.IsCloudSynced;
    public bool IsSafetyNode => GraphLayout.IsSafetyNode;
    public SaveSnapshotTriggerKind TriggerKind => Snapshot.TriggerKind;

    public IReadOnlyList<SaveBranchTipPillViewModel> BranchTipPills { get; }
    public bool HasBranchTipPills => BranchTipPills.Count > 0;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private string? _note;

    public string BranchDisplayName => SaveBranchItemViewModel.FormatBranchDisplayName(BranchName);

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Note)
        ? Note!.Trim()
        : $"{UiFormatters.FormatSaveTriggerLabel(TriggerKind)} · {BranchDisplayName}";

    public string TriggerLabel => UiFormatters.FormatSaveTriggerLabel(TriggerKind);

    public string TriggerIconPathData => TriggerKind switch
    {
        SaveSnapshotTriggerKind.Manual => "M5,4 H19 V20 L12,16 L5,20 Z",
        SaveSnapshotTriggerKind.ManualPin => "M12,3 L14.6,8.3 L20.5,9.1 L16.2,13.3 L17.2,19.1 L12,16.4 L6.8,19.1 L7.8,13.3 L3.5,9.1 L9.4,8.3 Z",
        SaveSnapshotTriggerKind.AutoQuiescent => "M13,2 L4,14 H11 L10,22 L20,10 H13 Z",
        SaveSnapshotTriggerKind.PostExit => "M14,4 H19 V20 H14 M4,12 H15 M11,8 L15,12 L11,16",
        SaveSnapshotTriggerKind.PreRollbackSafety or SaveSnapshotTriggerKind.PreBranchSwitchSafety => "M12,2 L19,5 V11 C19,16 15.8,20.4 12,22 C8.2,20.4 5,16 5,11 V5 Z",
        SaveSnapshotTriggerKind.PreTrainerImport => "M6,4 V9 M6,13 V20 M12,4 V14 M12,18 V20 M18,4 V7 M18,11 V20",
        SaveSnapshotTriggerKind.CloudPull or SaveSnapshotTriggerKind.CloudDivergenceBackup => "M6.5,18 H17.5 A4,4 0 0,0 18,10.1 A5.5,5.5 0 0,0 7.4,8.6 A3.8,3.8 0 0,0 6.5,18 Z",
        SaveSnapshotTriggerKind.BranchFork => "M6,4 V20 M18,6 V10 C18,13 15,14 12,14 H6 M4,6 A2,2 0 1,0 8,6 A2,2 0 1,0 4,6 M16,6 A2,2 0 1,0 20,6 A2,2 0 1,0 16,6",
        _ => "M5,4 H19 V20 L12,16 L5,20 Z",
    };

    public string HeadBadgeText => IsDetachedHead ? "历史回溯中" : "当前存档";

    public string TimestampText => Snapshot.CreatedAtUtc
        .ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string TimestampFullText => Snapshot.CreatedAtUtc
        .ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string SizeText => UiFormatters.FormatByteSize(Snapshot.TotalRawBytes);
    public string StoredSizeText => UiFormatters.FormatByteSize(Snapshot.TotalStoredBytes);

    public string FileCountText => Snapshot.RegistryKeyCount > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Snapshot.FileCount} 文件 + {Snapshot.RegistryKeyCount} 注册表")
        : string.Create(CultureInfo.InvariantCulture, $"{Snapshot.FileCount} 文件");

    public string PlaytimeText => UiFormatters.FormatDurationHms(Snapshot.PlaytimeActiveSeconds);

    public string MetaLineText => string.Create(
        CultureInfo.InvariantCulture,
        $"{TimestampText} · {SizeText} · {FileCountText} · 游玩 {PlaytimeText} · {ShortId}");

    public string CloudStatusBadgeText => IsCloudSynced ? "☁ 已同步" : string.Empty;

    public SaveSnapshotRowViewModel(
        SaveGraphRowLayout graphLayout,
        string? activeBranchName,
        IReadOnlyDictionary<string, string> branchColorByName)
    {
        GraphLayout = graphLayout ?? throw new ArgumentNullException(nameof(graphLayout));
        _isPinned = graphLayout.IsPinned;
        _note = graphLayout.Node.Snapshot.Note;

        var pills = new List<SaveBranchTipPillViewModel>();
        foreach (string tipName in graphLayout.Node.BranchTipNames)
        {
            string color = branchColorByName.TryGetValue(tipName, out string? mapped)
                ? mapped
                : graphLayout.BranchColorHex;
            bool isActive = !string.IsNullOrWhiteSpace(activeBranchName)
                && string.Equals(tipName, activeBranchName, StringComparison.OrdinalIgnoreCase);
            pills.Add(new SaveBranchTipPillViewModel(tipName, color, isActive));
        }

        BranchTipPills = pills;
    }

    partial void OnNoteChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));
}

/// <summary>
/// Single file or HKCU registry export displayed in the Node Inspector's <c>文件清单</c> tab.
/// </summary>
public sealed class SaveFileEntryViewModel
{
    public SaveSnapshotFileRecord Record { get; }
    public string LogicalPath => Record.LogicalPath;
    public string OriginalPath => Record.OriginalPath;
    public SaveFileCategory Category => Record.Category;
    public string CategoryTag => UiFormatters.FormatSaveFileCategoryTag(Record.Category);
    public bool IsSaveCategory => Record.Category == SaveFileCategory.Save;
    public bool IsConfigCategory => Record.Category == SaveFileCategory.Config;
    public bool IsRegistryCategory => Record.Category == SaveFileCategory.RegistryExport;
    public string FileSizeText => UiFormatters.FormatByteSize(Record.FileSize);
    public string ShortHash => Record.FileBlake3.Length > 10 ? Record.FileBlake3[..10] : Record.FileBlake3;
    public string LastWriteText => Record.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public SaveFileEntryViewModel(SaveSnapshotFileRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }
}

/// <summary>
/// Single file change line displayed in the Node Inspector's <c>变更对比</c> tab.
/// </summary>
public sealed class SaveDiffEntryViewModel
{
    public SaveFileDiffItemDto Item { get; }
    public string LogicalPath => Item.LogicalPath;
    public SaveFileCategory Category => Item.Category;
    public string CategoryTag => UiFormatters.FormatSaveFileCategoryTag(Item.Category);
    public SaveFileDiffKind DiffKind => Item.DiffKind;
    public bool IsAdded => Item.DiffKind == SaveFileDiffKind.Added;
    public bool IsModified => Item.DiffKind == SaveFileDiffKind.Modified;
    public bool IsDeleted => Item.DiffKind == SaveFileDiffKind.Deleted;

    public string DiffGlyph => Item.DiffKind switch
    {
        SaveFileDiffKind.Added => "+",
        SaveFileDiffKind.Modified => "~",
        SaveFileDiffKind.Deleted => "-",
        _ => "=",
    };

    public string DiffLabel => Item.DiffKind switch
    {
        SaveFileDiffKind.Added => "新增",
        SaveFileDiffKind.Modified => "变更",
        SaveFileDiffKind.Deleted => "删除",
        _ => "未变",
    };

    public string SizeDeltaText => Item.DiffKind switch
    {
        SaveFileDiffKind.Added => $"+{UiFormatters.FormatByteSize(Item.RightSize)}",
        SaveFileDiffKind.Deleted => $"-{UiFormatters.FormatByteSize(Item.LeftSize)}",
        SaveFileDiffKind.Modified => UiFormatters.FormatByteDelta(Item.SizeDelta),
        _ => UiFormatters.FormatByteSize(Item.RightSize),
    };

    public SaveDiffEntryViewModel(SaveFileDiffItemDto item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }
}

/// <summary>
/// Prioritization bucket for games in the Column 2 <c>存档</c> contextual sidebar.
/// </summary>
public enum SaveSidebarGameCategory
{
    CloudConflict = 0,
    ActiveWatchingOrRunning = 1,
    HasSnapshots = 2,
    NoSnapshotsYet = 3,
}

/// <summary>
/// Per-game sidebar row in the top-level <c>存档</c> navigation section.
/// </summary>
public sealed partial class SaveSidebarGameItemViewModel : ObservableObject
{
    public GameListItemViewModel Game { get; }
    public string GameId => Game.GameId;
    public string Title => Game.Title;
    public BitmapImage? SmallIcon => Game.SmallIcon;
    public string InitialsText => Game.InitialsText;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _activeBranchName = "main";

    [ObservableProperty]
    private int _branchCount;

    [ObservableProperty]
    private int _snapshotCount;

    [ObservableProperty]
    private bool _isWatching;

    [ObservableProperty]
    private bool _hasCloudConflict;

    [ObservableProperty]
    private DateTimeOffset? _lastSnapshotAtUtc;

    public SaveSidebarGameCategory Category
    {
        get
        {
            if (HasCloudConflict)
            {
                return SaveSidebarGameCategory.CloudConflict;
            }

            if (IsWatching || Game.IsRunning)
            {
                return SaveSidebarGameCategory.ActiveWatchingOrRunning;
            }

            return SnapshotCount > 0
                ? SaveSidebarGameCategory.HasSnapshots
                : SaveSidebarGameCategory.NoSnapshotsYet;
        }
    }

    public int SortPriority => (int)Category;
    public bool IsStatusConflict => HasCloudConflict;
    public bool IsStatusWatching => !HasCloudConflict && (IsWatching || Game.IsRunning);
    public bool IsStatusHasSaves => !HasCloudConflict && !IsWatching && !Game.IsRunning && SnapshotCount > 0;

    public string StatusPillText
    {
        get
        {
            if (HasCloudConflict)
            {
                return "分叉冲突";
            }

            if (SnapshotCount > 0)
            {
                return BranchCount > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"{BranchCount} 分支 · {SnapshotCount}")
                    : string.Create(CultureInfo.InvariantCulture, $"{SnapshotCount} 快照");
            }

            return IsWatching ? "监听中" : "未备份";
        }
    }

    public string SubtitleText
    {
        get
        {
            if (SnapshotCount == 0)
            {
                return Game.IsRunning ? "游戏运行中 · 尚未建档" : "点击进入时间机器备份";
            }

            string timePart = LastSnapshotAtUtc.HasValue
                ? LastSnapshotAtUtc.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "刚刚";
            return $"{SaveBranchItemViewModel.FormatBranchDisplayName(ActiveBranchName)} · {timePart}";
        }
    }

    public SaveSidebarGameItemViewModel(
        GameListItemViewModel game,
        SaveLibraryGameOverviewDto? overview,
        bool isSelected)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        _isSelected = isSelected;
        ApplyOverview(overview);
    }

    public void ApplyOverview(SaveLibraryGameOverviewDto? overview)
    {
        ActiveBranchName = string.IsNullOrWhiteSpace(overview?.ActiveBranchName) ? "main" : overview!.ActiveBranchName;
        BranchCount = overview?.BranchCount ?? 0;
        SnapshotCount = overview?.SnapshotCount ?? 0;
        IsWatching = overview?.IsWatching ?? false;
        HasCloudConflict = overview?.HasCloudConflict ?? false;
        LastSnapshotAtUtc = overview?.LastSnapshotAtUtc;
        NotifyDerived();
    }

    public void NotifyDerived()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SmallIcon));
        OnPropertyChanged(nameof(InitialsText));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(SortPriority));
        OnPropertyChanged(nameof(IsStatusConflict));
        OnPropertyChanged(nameof(IsStatusWatching));
        OnPropertyChanged(nameof(IsStatusHasSaves));
        OnPropertyChanged(nameof(StatusPillText));
        OnPropertyChanged(nameof(SubtitleText));
    }
}

/// <summary>
/// Single virtualized per-game card row in the Column 3 <c>全部存档总览</c> list.
/// </summary>
public sealed partial class ManagedSaveGameOverviewItemViewModel : ObservableObject
{
    private readonly Action<ManagedSaveGameOverviewItemViewModel> _onOpenTree;
    private readonly Func<ManagedSaveGameOverviewItemViewModel, Task> _onQuickBackup;
    private readonly Func<ManagedSaveGameOverviewItemViewModel, Task> _onQuickSync;

    public SaveLibraryGameOverviewDto Overview { get; }
    public GameListItemViewModel? Game { get; }

    public string GameId => Overview.GameId;
    public string GameTitle => Game?.Title ?? Overview.GameTitle;
    public BitmapImage? GameIcon => Game?.SmallIcon;
    public string GameInitials => Game?.InitialsText ?? "S";
    public string ActiveBranchName => string.IsNullOrWhiteSpace(Overview.ActiveBranchName) ? "main" : Overview.ActiveBranchName;
    public string ActiveBranchDisplayName => SaveBranchItemViewModel.FormatBranchDisplayName(ActiveBranchName);
    public int BranchCount => Overview.BranchCount;
    public int SnapshotCount => Overview.SnapshotCount;
    public int PinnedCount => Overview.PinnedCount;
    public bool IsWatching => Overview.IsWatching;
    public bool IsGameRunning => Game?.IsRunning ?? Overview.IsGameRunning;
    public bool HasCloudEndpoint => Overview.HasCloudEndpoint;
    public bool HasCloudConflict => Overview.HasCloudConflict;

    public string BranchAndSnapshotMetricsText => string.Create(
        CultureInfo.InvariantCulture,
        $"{BranchCount} 分支 · {SnapshotCount} 快照{(PinnedCount > 0 ? $" · ★ {PinnedCount} 固定" : string.Empty)}");

    public string StorageMetricsText => string.Create(
        CultureInfo.InvariantCulture,
        $"原始 {UiFormatters.FormatByteSize(Overview.TotalRawBytes)} · 实占 {UiFormatters.FormatByteSize(Overview.TotalStoredBytes)}");

    public string LastSnapshotText => Overview.LastSnapshotAtUtc.HasValue
        ? $"最近备份 {Overview.LastSnapshotAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "尚未创建存档快照";

    public string CloudStatusText => HasCloudConflict
        ? "分叉冲突待处理"
        : (HasCloudEndpoint
            ? (Overview.LastSyncedAtUtc.HasValue
                ? $"云端已同步 · {Overview.LastSyncedAtUtc.Value.ToLocalTime():MM-dd HH:mm}"
                : "已绑定云端点 · 待同步")
            : "仅本地存储");

    [ObservableProperty]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    public IRelayCommand OpenTreeCommand { get; }
    public IAsyncRelayCommand QuickBackupCommand { get; }
    public IAsyncRelayCommand QuickSyncCommand { get; }

    public ManagedSaveGameOverviewItemViewModel(
        SaveLibraryGameOverviewDto overview,
        GameListItemViewModel? game,
        Action<ManagedSaveGameOverviewItemViewModel> onOpenTree,
        Func<ManagedSaveGameOverviewItemViewModel, Task> onQuickBackup,
        Func<ManagedSaveGameOverviewItemViewModel, Task> onQuickSync)
    {
        Overview = overview ?? throw new ArgumentNullException(nameof(overview));
        Game = game;
        _onOpenTree = onOpenTree ?? throw new ArgumentNullException(nameof(onOpenTree));
        _onQuickBackup = onQuickBackup ?? throw new ArgumentNullException(nameof(onQuickBackup));
        _onQuickSync = onQuickSync ?? throw new ArgumentNullException(nameof(onQuickSync));

        OpenTreeCommand = new RelayCommand(() => _onOpenTree(this));
        QuickBackupCommand = new AsyncRelayCommand(() => _onQuickBackup(this));
        QuickSyncCommand = new AsyncRelayCommand(() => _onQuickSync(this));
    }
}

/// <summary>
/// Configured cloud sync endpoint item displayed in Settings and in the workbench Cloud Sync drawer.
/// </summary>
public sealed partial class CloudEndpointItemViewModel : ObservableObject
{
    public CloudSyncEndpointRecord Record { get; }
    public string EndpointId => Record.EndpointId;
    public string DisplayName => Record.DisplayName;
    public CloudSyncProviderType ProviderType => Record.ProviderType;
    public string ProviderLabel => UiFormatters.FormatCloudProviderLabel(Record.ProviderType);
    public string EndpointUrl => Record.EndpointUrl;
    public string BucketOrRoot => Record.BucketOrRoot;
    public string? Region => Record.Region;
    public bool IsDefault => Record.IsDefault;
    public bool HasSecretConfigured => Record.HasSecretConfigured;

    public string SummaryLine => string.IsNullOrWhiteSpace(BucketOrRoot)
        ? $"{ProviderLabel} · {EndpointUrl}"
        : $"{ProviderLabel} · {EndpointUrl} / {BucketOrRoot}";

    public string SecretStatusText => ProviderType == CloudSyncProviderType.LocalFolder
        ? "本地目录免凭据"
        : (HasSecretConfigured ? "已加密存储凭据 (DPAPI)" : "未配置访问凭据");

    [ObservableProperty]
    private string _lastTestResultText = string.Empty;

    [ObservableProperty]
    private bool _lastTestSucceeded;

    public bool HasTestResult => !string.IsNullOrWhiteSpace(LastTestResultText);

    public CloudEndpointItemViewModel(CloudSyncEndpointRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }

    partial void OnLastTestResultTextChanged(string value) => OnPropertyChanged(nameof(HasTestResult));
}

/// <summary>
/// Single probed or user-configured save/config/registry target displayed inside the workbench Paths Config drawer.
/// </summary>
public sealed class ResolvedSaveLocationItemViewModel
{
    public ResolvedSaveLocation Location { get; }
    public string ResolvedFullPath => Location.ResolvedFullPath;
    public string LogicalTemplatePath => Location.LogicalTemplatePath;
    public SaveFileCategory Category => Location.Category;
    public string CategoryTag => UiFormatters.FormatSaveFileCategoryTag(Location.Category);
    public bool IsSaveCategory => Location.Category == SaveFileCategory.Save;
    public bool IsConfigCategory => Location.Category == SaveFileCategory.Config;
    public bool IsRegistryCategory => Location.Category == SaveFileCategory.RegistryExport;
    public string SourceLabel => Location.SourceLabel;
    public bool Exists => Location.Exists;
    public bool IsCustom => Location.IsCustom;
    public string? CustomLocationId => Location.CustomLocationId;
    public string ExistsBadgeText => Exists ? "现存有效" : "未生成";

    public string MetricsText => IsRegistryCategory
        ? (Exists ? "注册表项已存在" : "注册表项尚未生成")
        : (Exists
            ? string.Create(CultureInfo.InvariantCulture, $"{Location.FileCount} 文件 · {UiFormatters.FormatByteSize(Location.TotalBytes)}")
            : "尚未在磁盘生成");

    public ResolvedSaveLocationItemViewModel(ResolvedSaveLocation location)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
    }
}
