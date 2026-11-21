using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spawn.Contracts;
using Spawn.Core.Update;

namespace Spawn.Host.ViewModels;

public sealed record UpdateHistoryItemViewModel(
    string TimestampText,
    string VersionTransitionText,
    string TriggerLabel,
    string ResultLabel,
    string Detail,
    bool IsSuccess,
    bool IsFailure,
    bool IsDeferred);

/// <summary>
/// ViewModel backing the Settings <c>更新</c> category (<see cref="SettingsCategory.Updates"/>)
/// and the non-intrusive main-window update-ready banner.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly IUpdateService? _updateService;
    private readonly Action<string> _openExternalUrl;
    private bool _suppressSettingPersistence;
    private string? _dismissedBannerVersion;
    private int _disposed;

    public event Action<string>? StatusFeedbackRequested;

    [ObservableProperty]
    private bool _isUpdateSupported;

    [ObservableProperty]
    private string _currentVersionText = SpawnBuildInfo.Version;

    [ObservableProperty]
    private string _channelText = SpawnBuildInfo.Channel;

    [ObservableProperty]
    private string _commitShortShaText = FormatCommitShortSha(SpawnBuildInfo.CommitSha);

    [ObservableProperty]
    private UpdateServiceState _serviceState = UpdateServiceState.DisabledDevChannel;

    [ObservableProperty]
    private bool _autoCheckEnabled = true;

    [ObservableProperty]
    private bool _autoInstallWhenIdle = true;

    [ObservableProperty]
    private DateTimeOffset? _lastCheckUtc;

    [ObservableProperty]
    private DateTimeOffset? _nextCheckUtc;

    [ObservableProperty]
    private string _statusSummaryText = UpdateService.DevChannelStatusMessage;

    [ObservableProperty]
    private PreparedUpdateDto? _readyUpdate;

    [ObservableProperty]
    private double _downloadProgressPercent;

    [ObservableProperty]
    private bool _isReadyBannerDismissed;

    public ObservableCollection<UpdateHistoryItemViewModel> HistoryItems { get; } = [];

    public bool IsDevOrSourceChannel => !IsUpdateSupported;

    public string DevChannelNoticeText => IsUpdateSupported ? string.Empty : UpdateService.DevChannelStatusMessage;

    public string ChannelBadgeText => string.IsNullOrWhiteSpace(ChannelText)
        ? "SOURCE"
        : ChannelText.ToUpperInvariant();

    public string ChannelDescriptionText => IsUpdateSupported
        ? "正式版通道（GitHub Releases 每周签名构建 · ECDSA P-256 验签）"
        : $"{DevChannelNoticeText}（当前通道：{ChannelText}，永不联网检查更新）";

    public bool IsBusy => ServiceState is UpdateServiceState.Checking
        or UpdateServiceState.Downloading
        or UpdateServiceState.Installing;

    public bool IsDownloading => ServiceState == UpdateServiceState.Downloading;

    public bool HasReadyUpdate => IsUpdateSupported && ReadyUpdate is not null;

    public bool ShowReadyBanner => HasReadyUpdate && !IsReadyBannerDismissed;

    public string ReadyVersionText => ReadyUpdate is null ? string.Empty : $"v{ReadyUpdate.Version}";

    public string ReadyBannerText => ReadyUpdate is null
        ? string.Empty
        : $"新版本已就绪，重启更新（v{ReadyUpdate.Version}）";

    public string PrimaryActionButtonText => ServiceState switch
    {
        UpdateServiceState.Checking => "正在检查更新…",
        UpdateServiceState.Downloading => string.Create(CultureInfo.InvariantCulture, $"正在下载 ({Math.Round(DownloadProgressPercent):0}%)…"),
        UpdateServiceState.Installing => "正在启动更新…",
        _ when HasReadyUpdate => "立即重启更新",
        _ => "检查更新",
    };

    public bool CanExecutePrimaryAction => IsUpdateSupported && !IsBusy;

    public bool HasReleaseNotesUrl => HasReadyUpdate && !string.IsNullOrWhiteSpace(ReadyUpdate?.NotesUrl);

    public string InstallPolicyDescriptionText => !IsUpdateSupported
        ? "开发版与源码运行不会执行应用内更新；本地提交后由 package.py --install-dev 自动重装。"
        : AutoInstallWhenIdle
            ? "自动下载验签；仅当无受追踪游戏、无修改器会话、无存档快照/清理进行中且仅驻留托盘时自动安装，主窗口打开时仅提示。"
            : "仅提醒模式：自动下载验签后通过提示条与托盘提醒，绝不自动重启。";

    public string LastCheckTimeText => !IsUpdateSupported
        ? "开发版不检查"
        : LastCheckUtc.HasValue
            ? LastCheckUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "尚未检查";

    public string NextCheckTimeText => !IsUpdateSupported
        ? "开发版不检查"
        : !AutoCheckEnabled
            ? "自动检查已关闭"
            : NextCheckUtc.HasValue
                ? NextCheckUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "启动后 60 秒 / 每 24 小时";

    public bool HasHistory => HistoryItems.Count > 0;

    public UpdateViewModel(
        IUpdateService? updateService = null,
        Action<string>? openExternalUrl = null)
    {
        _updateService = updateService;
        _openExternalUrl = openExternalUrl ?? OpenExternalUriDefault;

        if (_updateService is not null)
        {
            ApplySnapshot(_updateService.CurrentSnapshot);
            _updateService.StateChanged += OnUpdateServiceStateChanged;
        }
        else
        {
            ApplySnapshot(new UpdateStateSnapshot
            {
                IsUpdateSupported = SpawnBuildInfo.IsReleaseChannel,
                CurrentVersion = SpawnBuildInfo.Version,
                Channel = SpawnBuildInfo.Channel,
                CommitSha = SpawnBuildInfo.CommitSha,
                State = SpawnBuildInfo.IsReleaseChannel
                    ? UpdateServiceState.Idle
                    : UpdateServiceState.DisabledDevChannel,
                Settings = new UpdateSettingsDto(),
                StatusMessage = SpawnBuildInfo.IsReleaseChannel
                    ? "等待检查更新"
                    : UpdateService.DevChannelStatusMessage,
                History = [],
            });
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_updateService is not null)
        {
            ApplySnapshot(_updateService.CurrentSnapshot);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task PrimaryActionAsync()
    {
        if (!CanExecutePrimaryAction)
        {
            return;
        }

        if (HasReadyUpdate)
        {
            await RestartAndUpdateAsync().ConfigureAwait(true);
        }
        else
        {
            await CheckForUpdatesAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        if (_updateService is null)
        {
            StatusFeedbackRequested?.Invoke(DevChannelNoticeText);
            return;
        }

        if (!IsUpdateSupported)
        {
            StatusFeedbackRequested?.Invoke(DevChannelNoticeText);
            return;
        }

        UpdateCheckResult result;
        try
        {
            result = await _updateService.CheckForUpdatesAsync(UpdateTriggerKind.Manual)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusFeedbackRequested?.Invoke("已取消本次更新检查。");
            return;
        }
        catch (Exception ex)
        {
            // Never let a service failure escape to the WPF Dispatcher (would crash the host).
            StatusFeedbackRequested?.Invoke($"检查更新失败：{ex.Message}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            StatusFeedbackRequested?.Invoke(result.Message);
        }
    }

    [RelayCommand]
    public async Task RestartAndUpdateAsync()
    {
        if (_updateService is null || !HasReadyUpdate)
        {
            return;
        }

        UpdateInstallResult result;
        try
        {
            result = await _updateService.InstallReadyUpdateAsync(UpdateTriggerKind.Manual)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusFeedbackRequested?.Invoke("已取消本次重启更新。");
            return;
        }
        catch (Exception ex)
        {
            // Never let a service failure escape to the WPF Dispatcher (would crash the host).
            StatusFeedbackRequested?.Invoke($"重启更新失败：{ex.Message}");
            return;
        }

        if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.Message))
        {
            StatusFeedbackRequested?.Invoke(result.Message);
        }
    }

    [RelayCommand]
    public void DismissReadyBanner()
    {
        _dismissedBannerVersion = ReadyUpdate?.Version;
        IsReadyBannerDismissed = true;
    }

    [RelayCommand]
    public void OpenReleaseNotes()
    {
        string? url = ReadyUpdate?.NotesUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            _openExternalUrl(url);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"无法打开发布说明页面：{ex.Message}");
        }
    }

    partial void OnIsUpdateSupportedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDevOrSourceChannel));
        OnPropertyChanged(nameof(DevChannelNoticeText));
        OnPropertyChanged(nameof(ChannelDescriptionText));
        OnPropertyChanged(nameof(HasReadyUpdate));
        OnPropertyChanged(nameof(ShowReadyBanner));
        OnPropertyChanged(nameof(CanExecutePrimaryAction));
        OnPropertyChanged(nameof(HasReleaseNotesUrl));
        OnPropertyChanged(nameof(InstallPolicyDescriptionText));
        OnPropertyChanged(nameof(LastCheckTimeText));
        OnPropertyChanged(nameof(NextCheckTimeText));
    }

    partial void OnChannelTextChanged(string value)
    {
        OnPropertyChanged(nameof(ChannelBadgeText));
        OnPropertyChanged(nameof(ChannelDescriptionText));
    }

    partial void OnServiceStateChanged(UpdateServiceState value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(PrimaryActionButtonText));
        OnPropertyChanged(nameof(CanExecutePrimaryAction));
    }

    partial void OnDownloadProgressPercentChanged(double value) =>
        OnPropertyChanged(nameof(PrimaryActionButtonText));

    partial void OnReadyUpdateChanged(PreparedUpdateDto? value)
    {
        if (value is not null && !string.Equals(_dismissedBannerVersion, value.Version, StringComparison.OrdinalIgnoreCase))
        {
            IsReadyBannerDismissed = false;
        }
        else if (value is null)
        {
            _dismissedBannerVersion = null;
            IsReadyBannerDismissed = false;
        }

        OnPropertyChanged(nameof(HasReadyUpdate));
        OnPropertyChanged(nameof(ShowReadyBanner));
        OnPropertyChanged(nameof(ReadyVersionText));
        OnPropertyChanged(nameof(ReadyBannerText));
        OnPropertyChanged(nameof(PrimaryActionButtonText));
        OnPropertyChanged(nameof(HasReleaseNotesUrl));
    }

    partial void OnIsReadyBannerDismissedChanged(bool value) =>
        OnPropertyChanged(nameof(ShowReadyBanner));

    partial void OnLastCheckUtcChanged(DateTimeOffset? value) =>
        OnPropertyChanged(nameof(LastCheckTimeText));

    partial void OnNextCheckUtcChanged(DateTimeOffset? value) =>
        OnPropertyChanged(nameof(NextCheckTimeText));

    partial void OnAutoCheckEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(NextCheckTimeText));
        PersistSettingsIfReady();
    }

    partial void OnAutoInstallWhenIdleChanged(bool value)
    {
        OnPropertyChanged(nameof(InstallPolicyDescriptionText));
        PersistSettingsIfReady();
    }

    private void PersistSettingsIfReady()
    {
        if (_suppressSettingPersistence || _updateService is null || !IsUpdateSupported)
        {
            return;
        }

        var updated = new UpdateSettingsDto
        {
            AutoCheckEnabled = AutoCheckEnabled,
            AutoInstallWhenIdle = AutoInstallWhenIdle,
        };
        _ = _updateService.SaveSettingsAsync(updated);
    }

    private void OnUpdateServiceStateChanged(object? sender, UpdateStateSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    ApplySnapshot(snapshot);
                }
            });
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(UpdateStateSnapshot snapshot)
    {
        _suppressSettingPersistence = true;
        try
        {
            IsUpdateSupported = snapshot.IsUpdateSupported;
            CurrentVersionText = string.IsNullOrWhiteSpace(snapshot.CurrentVersion)
                ? SpawnBuildInfo.Version
                : snapshot.CurrentVersion;
            ChannelText = string.IsNullOrWhiteSpace(snapshot.Channel)
                ? SpawnBuildInfo.Channel
                : snapshot.Channel;
            CommitShortShaText = FormatCommitShortSha(snapshot.CommitSha);
            ServiceState = snapshot.State;
            AutoCheckEnabled = snapshot.Settings.AutoCheckEnabled;
            AutoInstallWhenIdle = snapshot.Settings.AutoInstallWhenIdle;
            LastCheckUtc = snapshot.LastCheckUtc;
            NextCheckUtc = snapshot.NextCheckUtc;
            StatusSummaryText = string.IsNullOrWhiteSpace(snapshot.StatusMessage)
                ? (snapshot.IsUpdateSupported ? "等待检查更新" : DevChannelNoticeText)
                : snapshot.StatusMessage;
            DownloadProgressPercent = snapshot.DownloadProgressPercent ?? 0.0;
            ReadyUpdate = snapshot.ReadyUpdate;

            HistoryItems.Clear();
            foreach (UpdateHistoryEntryDto entry in snapshot.History)
            {
                HistoryItems.Add(FormatHistoryItem(entry));
            }

            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(PrimaryActionButtonText));
            OnPropertyChanged(nameof(CanExecutePrimaryAction));
            OnPropertyChanged(nameof(InstallPolicyDescriptionText));
            OnPropertyChanged(nameof(LastCheckTimeText));
            OnPropertyChanged(nameof(NextCheckTimeText));
        }
        finally
        {
            _suppressSettingPersistence = false;
        }
    }

    private static UpdateHistoryItemViewModel FormatHistoryItem(UpdateHistoryEntryDto entry)
    {
        string timestamp = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string transition = !string.IsNullOrWhiteSpace(entry.ToVersion)
            && !string.Equals(entry.FromVersion, entry.ToVersion, StringComparison.OrdinalIgnoreCase)
            ? $"v{entry.FromVersion} → v{entry.ToVersion}"
            : $"v{entry.FromVersion}";
        string triggerLabel = entry.Trigger == UpdateTriggerKind.Manual ? "手动" : "自动";
        string resultLabel = entry.Result switch
        {
            UpdateHistoryResult.UpToDate => "已是最新",
            UpdateHistoryResult.Downloaded => "已下载就绪",
            UpdateHistoryResult.InstallLaunched => "已启动更新",
            UpdateHistoryResult.Completed => "更新完成",
            UpdateHistoryResult.DeferredOffline => "离线顺延",
            UpdateHistoryResult.Failed => "失败",
            _ => "记录",
        };

        return new UpdateHistoryItemViewModel(
            TimestampText: timestamp,
            VersionTransitionText: transition,
            TriggerLabel: triggerLabel,
            ResultLabel: resultLabel,
            Detail: entry.Detail,
            IsSuccess: entry.Result is UpdateHistoryResult.Completed or UpdateHistoryResult.Downloaded or UpdateHistoryResult.InstallLaunched or UpdateHistoryResult.UpToDate,
            IsFailure: entry.Result == UpdateHistoryResult.Failed,
            IsDeferred: entry.Result == UpdateHistoryResult.DeferredOffline);
    }

    public static string FormatCommitShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return "local";
        }

        string trimmed = sha.Trim();
        return trimmed.Length > 10 ? trimmed[..7] : trimmed;
    }

    private static void OpenExternalUriDefault(string uri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true,
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_updateService is not null)
        {
            _updateService.StateChanged -= OnUpdateServiceStateChanged;
        }
    }
}
