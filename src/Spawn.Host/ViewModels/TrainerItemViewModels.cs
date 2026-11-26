using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spawn.Contracts;
using Spawn.Host.Helpers;
using Spawn.Modules.Progress.Catalog;

namespace Spawn.Host.ViewModels;

/// <summary>
/// Single virtualized hotkey row in the Trainer workbench (<see cref="TrainerHotkeyBindingRecord"/>).
/// </summary>
public sealed partial class TrainerHotkeyRowViewModel : ObservableObject
{
    private readonly Func<TrainerHotkeyRowViewModel, Task> _onTrigger;
    private readonly Func<TrainerHotkeyRowViewModel, Task>? _onRemove;
    private readonly Func<TrainerHotkeyRowViewModel, string?, Task>? _onSaveMappedChord;

    public string TrainerId { get; }
    public string EntryId { get; }
    public string Label { get; }
    public string? Tooltip { get; }
    public string OriginalChord { get; }
    public TrainerHotkeyEntryKind EntryKind { get; }
    public int SortOrder { get; }
    public bool IsManualEntry { get; }

    [ObservableProperty]
    private string? _mappedChord;

    [ObservableProperty]
    private bool _isEditingMappedChord;

    [ObservableProperty]
    private string _editingMappedChordText = string.Empty;

    [ObservableProperty]
    private bool _proxyState;

    public string EffectiveChord => string.IsNullOrWhiteSpace(MappedChord) ? OriginalChord : MappedChord!;
    public bool HasCustomMapping => !string.IsNullOrWhiteSpace(MappedChord);
    public bool CanEditMappedChord => _onSaveMappedChord is not null;
    public bool HasTooltip => !string.IsNullOrWhiteSpace(Tooltip);
    public bool IsToggle => EntryKind == TrainerHotkeyEntryKind.Toggle;
    public string ActionButtonLabel => IsToggle
        ? (ProxyState ? "代理已触发 · 再次发送" : "触发开关")
        : "发送按键";
    public string ProxyStateBadgeText => IsToggle
        ? (ProxyState ? "已发送（代理态）" : "未触发")
        : "单次触发";

    public IAsyncRelayCommand TriggerCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IRelayCommand BeginEditMappedChordCommand { get; }
    public IRelayCommand CancelEditMappedChordCommand { get; }
    public IAsyncRelayCommand SaveMappedChordCommand { get; }
    public IAsyncRelayCommand ClearMappedChordCommand { get; }

    public TrainerHotkeyRowViewModel(
        TrainerHotkeyBindingRecord record,
        Func<TrainerHotkeyRowViewModel, Task> onTrigger,
        Func<TrainerHotkeyRowViewModel, Task>? onRemove = null,
        bool isManualEntry = false,
        Func<TrainerHotkeyRowViewModel, string?, Task>? onSaveMappedChord = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        _onTrigger = onTrigger ?? throw new ArgumentNullException(nameof(onTrigger));
        _onRemove = onRemove;
        _onSaveMappedChord = onSaveMappedChord;

        TrainerId = record.TrainerId;
        EntryId = record.EntryId;
        Label = record.Label;
        Tooltip = record.Tooltip;
        OriginalChord = record.OriginalChord;
        _mappedChord = record.MappedChord;
        EntryKind = record.EntryKind;
        SortOrder = record.SortOrder;
        _proxyState = record.ProxyState;
        IsManualEntry = isManualEntry || record.EntryId.StartsWith("manual-", StringComparison.OrdinalIgnoreCase);

        TriggerCommand = new AsyncRelayCommand(() => _onTrigger(this));
        RemoveCommand = new AsyncRelayCommand(() => _onRemove is not null ? _onRemove(this) : Task.CompletedTask);
        BeginEditMappedChordCommand = new RelayCommand(BeginEditMappedChord);
        CancelEditMappedChordCommand = new RelayCommand(CancelEditMappedChord);
        SaveMappedChordCommand = new AsyncRelayCommand(
            () => _onSaveMappedChord is not null ? _onSaveMappedChord(this, EditingMappedChordText) : Task.CompletedTask);
        ClearMappedChordCommand = new AsyncRelayCommand(
            () => _onSaveMappedChord is not null ? _onSaveMappedChord(this, null) : Task.CompletedTask);
    }

    private void BeginEditMappedChord()
    {
        EditingMappedChordText = EffectiveChord;
        IsEditingMappedChord = true;
    }

    private void CancelEditMappedChord()
    {
        IsEditingMappedChord = false;
        EditingMappedChordText = string.Empty;
    }

    partial void OnMappedChordChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveChord));
        OnPropertyChanged(nameof(HasCustomMapping));
    }

    partial void OnProxyStateChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionButtonLabel));
        OnPropertyChanged(nameof(ProxyStateBadgeText));
    }

    public TrainerHotkeyBindingRecord ToRecord() =>
        new()
        {
            TrainerId = TrainerId,
            EntryId = EntryId,
            Label = Label,
            Tooltip = Tooltip,
            OriginalChord = OriginalChord,
            MappedChord = MappedChord,
            EntryKind = EntryKind,
            ProxyState = ProxyState,
            SortOrder = SortOrder,
        };
}

/// <summary>
/// Clean-room external website discovery candidate card with explicit source provenance and selectable version attachments.
/// </summary>
public sealed partial class TrainerCandidateItemViewModel : ObservableObject
{
    private readonly Func<TrainerCandidateItemViewModel, Task> _onLoadDetails;
    private readonly Func<TrainerCandidateItemViewModel, Task> _onDownload;
    private readonly Action<string> _onOpenBrowser;
    private readonly Action<string> _onCopyText;

    [ObservableProperty]
    private TrainerCandidate _candidate;

    [ObservableProperty]
    private TrainerAttachmentCandidate? _selectedAttachment;

    [ObservableProperty]
    private bool _isLoadingDetails;

    public string Title => Candidate.Title;
    public string PageUrl => Candidate.PageUrl;
    public TrainerCandidateProvenanceInfo Provenance => TrainerSourceProvenance.DescribeCandidate(Candidate);
    public string ProviderLabel => Provenance.BadgeText;
    public string SourceHostText => Provenance.SourceHost;
    public string ProvenanceHintText => Provenance.ProvenanceHintText;
    public bool HasValidHttpsPageUrl => Provenance.HasValidHttpsPageUrl;
    public string ValidatedHttpsPageUrl => Provenance.ValidatedHttpsPageUrl ?? string.Empty;
    public string SourceUrlDisplayText => Provenance.HasValidHttpsPageUrl
        ? $"来源网页：{Provenance.ValidatedHttpsPageUrl}"
        : $"来源站点：{Provenance.SourceHost}";
    public string VersionSummaryText => string.IsNullOrWhiteSpace(Candidate.VersionLabel)
        ? (Candidate.UpdatedDateText ?? "点击加载附件版本列表")
        : $"{Candidate.VersionLabel}{(string.IsNullOrWhiteSpace(Candidate.UpdatedDateText) ? string.Empty : $" · {Candidate.UpdatedDateText}")}";
    public string ConfidenceBadgeText => UiFormatters.FormatTrainerVersionConfidenceLabel(
        SelectedAttachment?.VersionConfidence ?? Candidate.VersionConfidence);
    public string ConfidenceReasonText => SelectedAttachment?.ConfidenceReason ?? Candidate.ConfidenceReason ?? string.Empty;
    public bool HasConfidenceReason => !string.IsNullOrWhiteSpace(ConfidenceReasonText);
    public bool HasAttachments => Candidate.Attachments.Count > 0;
    public IReadOnlyList<TrainerAttachmentCandidate> Attachments => Candidate.Attachments;
    public IReadOnlyList<TrainerHotkeyBindingRecord> PreviewHotkeys => Candidate.PreviewHotkeys;
    public int PreviewHotkeyCount => Candidate.PreviewHotkeys.Count;
    public bool HasPreviewHotkeys => Candidate.PreviewHotkeys.Count > 0;
    public string PreviewHotkeysSummaryText => HasPreviewHotkeys
        ? $"预览热键（{PreviewHotkeyCount} 项）：{string.Join(" · ", Candidate.PreviewHotkeys.Take(4).Select(h => $"{h.OriginalChord} {h.Label}"))}"
        : string.Empty;
    public string DownloadButtonLabel => HasAttachments ? "下载所选版本" : "查看版本与热键";

    public IAsyncRelayCommand LoadDetailsCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand OpenInBrowserCommand { get; }
    public IRelayCommand CopyPageUrlCommand { get; }

    public TrainerCandidateItemViewModel(
        TrainerCandidate candidate,
        Func<TrainerCandidateItemViewModel, Task> onLoadDetails,
        Func<TrainerCandidateItemViewModel, Task> onDownload,
        Action<string> onOpenBrowser,
        Action<string>? onCopyText = null)
    {
        _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        _selectedAttachment = candidate.SelectedAttachment ?? (candidate.Attachments.Count > 0 ? candidate.Attachments[0] : null);
        _onLoadDetails = onLoadDetails ?? throw new ArgumentNullException(nameof(onLoadDetails));
        _onDownload = onDownload ?? throw new ArgumentNullException(nameof(onDownload));
        _onOpenBrowser = onOpenBrowser ?? throw new ArgumentNullException(nameof(onOpenBrowser));
        _onCopyText = onCopyText ?? CopyToSystemClipboardSafe;

        LoadDetailsCommand = new AsyncRelayCommand(() => _onLoadDetails(this));
        DownloadCommand = new AsyncRelayCommand(() => HasAttachments ? _onDownload(this) : _onLoadDetails(this));
        OpenInBrowserCommand = new RelayCommand(OpenValidatedPageInBrowser);
        CopyPageUrlCommand = new RelayCommand(CopyValidatedPageUrl);
    }

    partial void OnSelectedAttachmentChanged(TrainerAttachmentCandidate? value)
    {
        OnPropertyChanged(nameof(ConfidenceBadgeText));
        OnPropertyChanged(nameof(ConfidenceReasonText));
        OnPropertyChanged(nameof(HasConfidenceReason));
    }

    public void UpdateCandidate(TrainerCandidate updated)
    {
        Candidate = updated;
        SelectedAttachment = updated.SelectedAttachment ?? (updated.Attachments.Count > 0 ? updated.Attachments[0] : null);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageUrl));
        OnPropertyChanged(nameof(Provenance));
        OnPropertyChanged(nameof(ProviderLabel));
        OnPropertyChanged(nameof(SourceHostText));
        OnPropertyChanged(nameof(ProvenanceHintText));
        OnPropertyChanged(nameof(HasValidHttpsPageUrl));
        OnPropertyChanged(nameof(ValidatedHttpsPageUrl));
        OnPropertyChanged(nameof(SourceUrlDisplayText));
        OnPropertyChanged(nameof(VersionSummaryText));
        OnPropertyChanged(nameof(ConfidenceBadgeText));
        OnPropertyChanged(nameof(ConfidenceReasonText));
        OnPropertyChanged(nameof(HasConfidenceReason));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(Attachments));
        OnPropertyChanged(nameof(PreviewHotkeys));
        OnPropertyChanged(nameof(PreviewHotkeyCount));
        OnPropertyChanged(nameof(HasPreviewHotkeys));
        OnPropertyChanged(nameof(PreviewHotkeysSummaryText));
        OnPropertyChanged(nameof(DownloadButtonLabel));
    }

    public TrainerCandidate BuildSelectionCandidate() =>
        SelectedAttachment is not null
            ? Candidate with { SelectedAttachment = SelectedAttachment }
            : Candidate;

    private void OpenValidatedPageInBrowser()
    {
        if (TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(Candidate.PageUrl, out string safeHttpsUrl, out _))
        {
            _onOpenBrowser(safeHttpsUrl);
        }
    }

    private void CopyValidatedPageUrl()
    {
        if (TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(Candidate.PageUrl, out string safeHttpsUrl, out _))
        {
            _onCopyText(safeHttpsUrl);
        }
    }

    private static void CopyToSystemClipboardSafe(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Ignore clipboard contention.
        }
    }
}

/// <summary>
/// Single pre-trainer or pre-restore safety save snapshot row with one-click rollback.
/// </summary>
public sealed partial class PreTrainerSnapshotItemViewModel : ObservableObject
{
    private readonly Func<PreTrainerSnapshotItemViewModel, Task> _onRestore;

    public PreTrainerSnapshotRecord Record { get; }
    public bool IsTimeMachineSnapshot { get; }

    [ObservableProperty]
    private bool _isGameRunning;

    public string SnapshotId => Record.SnapshotId;
    public string TriggerBadgeText => IsTimeMachineSnapshot
        ? "修改器启动前 · 时光机"
        : (Record.TriggerKind == PreTrainerSnapshotTriggerKind.PreRestoreSafety
            ? "回滚前安全点"
            : "修改器启动前");
    public string StatusText => UiFormatters.FormatPreTrainerSnapshotStatus(Record.Status, Record.SkipReason);
    public bool IsCompleted => Record.Status == PreTrainerSnapshotStatus.Completed;
    public bool IsSkipped => Record.Status == PreTrainerSnapshotStatus.Skipped;
    public string CreatedAtText => Record.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string MetricsText => IsCompleted
        ? $"{Record.FileCount} 个文件 · {UiFormatters.FormatByteSize(Record.TotalBytes)}"
        : "未写入文件";
    public string RestoredNoteText => Record.RestoredAtUtc.HasValue
        ? $"已于 {Record.RestoredAtUtc.Value.ToLocalTime():MM-dd HH:mm} 回滚"
        : string.Empty;
    public bool HasRestoredNote => Record.RestoredAtUtc.HasValue;
    public bool CanRestore => IsCompleted && !IsGameRunning;

    public IAsyncRelayCommand RestoreCommand { get; }

    public PreTrainerSnapshotItemViewModel(
        PreTrainerSnapshotRecord record,
        bool isGameRunning,
        Func<PreTrainerSnapshotItemViewModel, Task> onRestore,
        bool isTimeMachineSnapshot = false)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        IsTimeMachineSnapshot = isTimeMachineSnapshot;
        _isGameRunning = isGameRunning;
        _onRestore = onRestore ?? throw new ArgumentNullException(nameof(onRestore));
        RestoreCommand = new AsyncRelayCommand(() => _onRestore(this));
    }

    partial void OnIsGameRunningChanged(bool value) => OnPropertyChanged(nameof(CanRestore));
}

/// <summary>
/// Prioritization bucket for games listed in the Column 2 <c>修改器</c> contextual sidebar.
/// </summary>
public enum TrainerSidebarGameCategory
{
    RunningTrainer = 0,
    HasBoundTrainer = 1,
    ReadySinglePlayer = 2,
    SafetyLocked = 3,
}

/// <summary>
/// Per-game sidebar row in the top-level <c>修改器</c> navigation section.
/// Prioritizes actively running trainers -> bound/installed trainers -> eligible single-player games -> safety-locked games.
/// </summary>
public sealed partial class TrainerSidebarGameItemViewModel : ObservableObject
{
    public GameListItemViewModel Game { get; }

    public string GameId => Game.GameId;
    public string Title => Game.Title;
    public BitmapImage? SmallIcon => Game.SmallIcon;
    public string InitialsText => Game.InitialsText;
    public AntiCheatTier AntiCheatTier => Game.AntiCheatTier;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _hasRunningTrainer;

    [ObservableProperty]
    private int _boundTrainerCount;

    [ObservableProperty]
    private bool _isSafetyLocked;

    [ObservableProperty]
    private bool _isHybridSinglePlayer;

    [ObservableProperty]
    private bool _isVerifiedOfflineBypass;

    [ObservableProperty]
    private string _boundTrainerNamesSummary = string.Empty;

    public TrainerSidebarGameCategory Category
    {
        get
        {
            if (HasRunningTrainer)
            {
                return TrainerSidebarGameCategory.RunningTrainer;
            }

            if (BoundTrainerCount > 0)
            {
                return TrainerSidebarGameCategory.HasBoundTrainer;
            }

            return IsSafetyLocked
                ? TrainerSidebarGameCategory.SafetyLocked
                : TrainerSidebarGameCategory.ReadySinglePlayer;
        }
    }

    public int SortPriority => (int)Category;
    public bool HasBoundTrainers => BoundTrainerCount > 0;
    public bool IsStatusRunning => HasRunningTrainer;
    public bool IsStatusBound => !HasRunningTrainer && BoundTrainerCount > 0;
    public bool IsStatusLocked => !HasRunningTrainer && BoundTrainerCount == 0 && IsSafetyLocked;
    public bool IsStatusOfflineReady => !HasRunningTrainer && BoundTrainerCount == 0 && !IsSafetyLocked && IsVerifiedOfflineBypass;
    public bool IsStatusHybridPending => !HasRunningTrainer && BoundTrainerCount == 0 && !IsSafetyLocked && IsHybridSinglePlayer && !IsVerifiedOfflineBypass;
    public bool IsStatusReadySinglePlayer => !HasRunningTrainer && BoundTrainerCount == 0 && !IsSafetyLocked && !IsHybridSinglePlayer && !IsVerifiedOfflineBypass;

    public string StatusPillText
    {
        get
        {
            if (HasRunningTrainer)
            {
                return "运行中";
            }

            if (BoundTrainerCount > 1)
            {
                return string.Create(CultureInfo.InvariantCulture, $"已绑定 · {BoundTrainerCount}");
            }

            if (BoundTrainerCount == 1)
            {
                return "已绑定";
            }

            if (IsSafetyLocked)
            {
                return "安全锁定";
            }

            if (IsVerifiedOfflineBypass)
            {
                return "离线就绪";
            }

            if (IsHybridSinglePlayer)
            {
                return "混合单机";
            }

            return "可匹配";
        }
    }

    public string SubtitleText
    {
        get
        {
            if (HasRunningTrainer)
            {
                return !string.IsNullOrWhiteSpace(BoundTrainerNamesSummary)
                    ? $"运行中 · {BoundTrainerNamesSummary}"
                    : "修改器运行中";
            }

            if (BoundTrainerCount > 0)
            {
                return !string.IsNullOrWhiteSpace(BoundTrainerNamesSummary)
                    ? BoundTrainerNamesSummary
                    : string.Create(CultureInfo.InvariantCulture, $"已安装 {BoundTrainerCount} 个修改器");
            }

            if (IsSafetyLocked)
            {
                return $"{Game.AntiCheatBadgeText} · 已禁修改器";
            }

            if (IsVerifiedOfflineBypass)
            {
                return "Tier 2 · 离线无 AC 就绪";
            }

            if (IsHybridSinglePlayer)
            {
                return "Tier 2 · 需确认离线模式";
            }

            return "Tier 0 纯净单机";
        }
    }

    public double RowOpacity => IsSafetyLocked && BoundTrainerCount == 0 ? 0.72 : 1.0;

    public TrainerSidebarGameItemViewModel(
        GameListItemViewModel game,
        bool hasRunningTrainer,
        int boundTrainerCount,
        bool isSafetyLocked,
        bool isHybridSinglePlayer,
        bool isVerifiedOfflineBypass,
        string boundTrainerNamesSummary,
        bool isSelected)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        _hasRunningTrainer = hasRunningTrainer;
        _boundTrainerCount = boundTrainerCount;
        _isSafetyLocked = isSafetyLocked;
        _isHybridSinglePlayer = isHybridSinglePlayer;
        _isVerifiedOfflineBypass = isVerifiedOfflineBypass;
        _boundTrainerNamesSummary = boundTrainerNamesSummary ?? string.Empty;
        _isSelected = isSelected;
    }

    public void UpdateState(
        bool hasRunningTrainer,
        int boundTrainerCount,
        bool isSafetyLocked,
        bool isHybridSinglePlayer,
        bool isVerifiedOfflineBypass,
        string boundTrainerNamesSummary)
    {
        HasRunningTrainer = hasRunningTrainer;
        BoundTrainerCount = boundTrainerCount;
        IsSafetyLocked = isSafetyLocked;
        IsHybridSinglePlayer = isHybridSinglePlayer;
        IsVerifiedOfflineBypass = isVerifiedOfflineBypass;
        BoundTrainerNamesSummary = boundTrainerNamesSummary ?? string.Empty;
        NotifyDerivedProperties();
    }

    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SmallIcon));
        OnPropertyChanged(nameof(InitialsText));
        OnPropertyChanged(nameof(AntiCheatTier));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(SortPriority));
        OnPropertyChanged(nameof(HasBoundTrainers));
        OnPropertyChanged(nameof(IsStatusRunning));
        OnPropertyChanged(nameof(IsStatusBound));
        OnPropertyChanged(nameof(IsStatusLocked));
        OnPropertyChanged(nameof(IsStatusOfflineReady));
        OnPropertyChanged(nameof(IsStatusHybridPending));
        OnPropertyChanged(nameof(IsStatusReadySinglePlayer));
        OnPropertyChanged(nameof(StatusPillText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(RowOpacity));
    }
}

/// <summary>
/// Single virtualized row in the <c>全部修改器总览</c> list representing one installed/bound trainer across the entire library.
/// </summary>
public sealed partial class ManagedTrainerItemViewModel : ObservableObject
{
    private readonly Func<ManagedTrainerItemViewModel, Task> _onPrimaryAction;
    private readonly Action<ManagedTrainerItemViewModel> _onOpenWorkbench;
    private readonly Func<ManagedTrainerItemViewModel, Task> _onOpenSandboxFolder;
    private readonly Func<ManagedTrainerItemViewModel, Task> _onVerifyIntegrity;
    private readonly Action<ManagedTrainerItemViewModel> _onOpenVirusTotal;
    private readonly Func<ManagedTrainerItemViewModel, Task> _onRemove;
    private readonly Action<string> _onOpenExternalUrl;
    private readonly Action<string> _onCopyText;

    public TrainerCatalogRecord Record { get; }
    public GameListItemViewModel? Game { get; }
    public TrainerSourceProvenanceInfo Provenance { get; }

    public string TrainerId => Record.TrainerId;
    public string GameId => Record.GameId;
    public string GameTitle => Game?.Title ?? "未知游戏";
    public BitmapImage? GameIcon => Game?.SmallIcon;
    public string GameInitials => Game?.InitialsText ?? "T";
    public string TrainerDisplayName => !string.IsNullOrWhiteSpace(Record.DisplayName)
        ? Record.DisplayName!
        : Path.GetFileName(Record.SandboxRelativePath);
    public string VersionLabelText => !string.IsNullOrWhiteSpace(Record.VersionLabel)
        ? Record.VersionLabel!
        : (Record.Provider == TrainerProviderKind.LocalImport ? "本地版本" : "标准版本");
    public bool IsExternalWebsiteSource => Provenance.IsExternalWebsite;
    public string ProviderLabel => Provenance.BadgeText;
    public string SourceHostText => Provenance.SourceHost;
    public bool HasValidSourcePageUrl => Provenance.HasValidHttpsSourceUrl;
    public string ValidatedSourcePageUrl => Provenance.ValidatedHttpsSourceUrl ?? string.Empty;
    public string SourceOriginSummaryText => Provenance.CompactOriginSummary;
    public string SourceDetailedDisclosureText => Provenance.DetailedDisclosureText;
    public string FullSha256 => Record.FileSha256;
    public string ShortSha256 => Record.FileSha256.Length > 12
        ? $"{Record.FileSha256[..12]}…"
        : Record.FileSha256;
    public string FileMetaShortText => string.Create(
        CultureInfo.InvariantCulture,
        $"{UiFormatters.FormatByteSize(Record.FileSize ?? 0)} · {Record.PeMachine ?? "x64"}{(Record.RequiresAdmin ? " · UAC" : string.Empty)}");
    public string LastLaunchedText => Record.LastLaunchedAtUtc.HasValue
        ? $"上次启动 {Record.LastLaunchedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"导入于 {Record.ImportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 尚未启动";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isGuardLocked;

    [ObservableProperty]
    private bool _isSafetyBlocked;

    [ObservableProperty]
    private bool _isBusy;

    public string PrimaryActionText => IsRunning ? "停止" : "启动";
    public bool CanExecutePrimaryAction => !IsBusy && (IsRunning || (!IsGuardLocked && !IsSafetyBlocked));

    public string SafetyHintBadgeText
    {
        get
        {
            if (IsSafetyBlocked)
            {
                return "安全锁定";
            }

            if (Game?.HasVerifiedOfflineBypassBadge == true)
            {
                return "离线无 AC 就绪";
            }

            if (Game?.AntiCheatTier == AntiCheatTier.Tier2)
            {
                return "需确认离线";
            }

            return string.Empty;
        }
    }

    public bool HasSafetyHintBadge => !string.IsNullOrWhiteSpace(SafetyHintBadgeText);

    public IAsyncRelayCommand PrimaryLaunchOrStopCommand { get; }
    public IRelayCommand OpenWorkbenchCommand { get; }
    public IAsyncRelayCommand OpenSandboxFolderCommand { get; }
    public IAsyncRelayCommand VerifyIntegrityCommand { get; }
    public IRelayCommand OpenVirusTotalCommand { get; }
    public IRelayCommand OpenSourcePageCommand { get; }
    public IRelayCommand CopySourcePageUrlCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    public ManagedTrainerItemViewModel(
        TrainerCatalogRecord record,
        GameListItemViewModel? game,
        bool isRunning,
        bool isGuardLocked,
        bool isSafetyBlocked,
        Func<ManagedTrainerItemViewModel, Task> onPrimaryAction,
        Action<ManagedTrainerItemViewModel> onOpenWorkbench,
        Func<ManagedTrainerItemViewModel, Task> onOpenSandboxFolder,
        Func<ManagedTrainerItemViewModel, Task> onVerifyIntegrity,
        Action<ManagedTrainerItemViewModel> onOpenVirusTotal,
        Func<ManagedTrainerItemViewModel, Task> onRemove,
        Action<string>? onOpenExternalUrl = null,
        Action<string>? onCopyText = null)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Game = game;
        Provenance = TrainerSourceProvenance.DescribeRecord(record);
        _isRunning = isRunning;
        _isGuardLocked = isGuardLocked;
        _isSafetyBlocked = isSafetyBlocked;
        _onPrimaryAction = onPrimaryAction ?? throw new ArgumentNullException(nameof(onPrimaryAction));
        _onOpenWorkbench = onOpenWorkbench ?? throw new ArgumentNullException(nameof(onOpenWorkbench));
        _onOpenSandboxFolder = onOpenSandboxFolder ?? throw new ArgumentNullException(nameof(onOpenSandboxFolder));
        _onVerifyIntegrity = onVerifyIntegrity ?? throw new ArgumentNullException(nameof(onVerifyIntegrity));
        _onOpenVirusTotal = onOpenVirusTotal ?? throw new ArgumentNullException(nameof(onOpenVirusTotal));
        _onRemove = onRemove ?? throw new ArgumentNullException(nameof(onRemove));
        _onOpenExternalUrl = onOpenExternalUrl ?? OpenHttpsUrlWithShellSafe;
        _onCopyText = onCopyText ?? CopyTextToClipboardSafe;

        PrimaryLaunchOrStopCommand = new AsyncRelayCommand(() => _onPrimaryAction(this));
        OpenWorkbenchCommand = new RelayCommand(() => _onOpenWorkbench(this));
        OpenSandboxFolderCommand = new AsyncRelayCommand(() => _onOpenSandboxFolder(this));
        VerifyIntegrityCommand = new AsyncRelayCommand(() => _onVerifyIntegrity(this));
        OpenVirusTotalCommand = new RelayCommand(() => _onOpenVirusTotal(this));
        OpenSourcePageCommand = new RelayCommand(OpenValidatedSourcePage);
        CopySourcePageUrlCommand = new RelayCommand(CopyValidatedSourcePageUrl);
        RemoveCommand = new AsyncRelayCommand(() => _onRemove(this));
    }

    private void OpenValidatedSourcePage()
    {
        if (Provenance.HasValidHttpsSourceUrl && !string.IsNullOrWhiteSpace(Provenance.ValidatedHttpsSourceUrl))
        {
            _onOpenExternalUrl(Provenance.ValidatedHttpsSourceUrl);
        }
    }

    private void CopyValidatedSourcePageUrl()
    {
        if (Provenance.HasValidHttpsSourceUrl && !string.IsNullOrWhiteSpace(Provenance.ValidatedHttpsSourceUrl))
        {
            _onCopyText(Provenance.ValidatedHttpsSourceUrl);
        }
    }

    private static void OpenHttpsUrlWithShellSafe(string url)
    {
        if (!TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(url, out string normalizedHttpsUrl, out _))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = normalizedHttpsUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore shell launch error when no default browser is registered.
        }
    }

    private static void CopyTextToClipboardSafe(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Ignore clipboard contention.
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(CanExecutePrimaryAction));
    }

    partial void OnIsGuardLockedChanged(bool value) => OnPropertyChanged(nameof(CanExecutePrimaryAction));
    partial void OnIsSafetyBlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExecutePrimaryAction));
        OnPropertyChanged(nameof(SafetyHintBadgeText));
        OnPropertyChanged(nameof(HasSafetyHintBadge));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanExecutePrimaryAction));
}
