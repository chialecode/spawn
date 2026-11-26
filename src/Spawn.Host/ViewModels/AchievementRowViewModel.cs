using System.Globalization;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spawn.Contracts;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels;

public sealed partial class AchievementRowViewModel : ObservableObject
{
    private readonly Func<AchievementRowViewModel, bool, Task> _onSetUnlockedAsync;
    private readonly Func<AchievementRowViewModel, Task> _onPreviewToastAsync;
    private readonly Dispatcher? _uiDispatcher;
    private readonly SynchronizationContext? _syncContext;

    private BitmapImage? _iconImage;
    private double _iconOpacity;
    private int _iconLoadStarted;
    private Task _iconLoadTask = Task.CompletedTask;

    public string GameId { get; }
    public string GameTitle { get; }
    public GameAchievementItemDto Item { get; }
    public AchievementCaptureMode ChannelCaptureMode { get; }

    [ObservableProperty]
    private bool _globalRevealSpoilers;

    [ObservableProperty]
    private bool _isRowSpoilerRevealed;

    public string ApiName => Item.Definition.ApiName;
    public string DisplayName => Item.Definition.DisplayName;
    public bool IsUnlocked => Item.IsUnlocked;
    public bool IsLocked => !Item.IsUnlocked;
    public bool IsHidden => Item.Definition.IsHidden;
    public bool IsRare => Item.Definition.IsRare;
    public double? GlobalPercent => Item.Definition.GlobalPercent;

    /// <summary>
    /// True only when a non-rare achievement has a global unlock rate (rare rows already display the rate inside <see cref="RareBadgeText"/>,
    /// avoiding duplicate percentage labels on the same row).
    /// </summary>
    public bool HasGlobalPercent => Item.Definition.GlobalPercent.HasValue && !IsRare;

    public string GlobalPercentText => HasGlobalPercent
        ? string.Create(CultureInfo.InvariantCulture, $"全球 {Item.Definition.GlobalPercent!.Value:0.0}%")
        : string.Empty;

    public string RareBadgeText => Item.Definition.GlobalPercent.HasValue
        ? string.Create(CultureInfo.InvariantCulture, $"稀有 {Item.Definition.GlobalPercent.Value:0.0}%")
        : "稀有成就";

    public string UnlockedStatusText => Item.IsUnlocked
        ? (Item.UnlockedUtc.HasValue
            ? Item.UnlockedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "已解锁")
        : "未解锁";

    public bool HasNumericProgress =>
        Item.Unlock?.CurrentProgress is double cur
        && Item.Unlock?.MaxProgress is double max
        && !double.IsNaN(cur)
        && !double.IsInfinity(cur)
        && !double.IsNaN(max)
        && !double.IsInfinity(max)
        && max > 0.0
        && cur >= 0.0;

    public double ProgressFraction => HasNumericProgress
        ? Math.Clamp(Item.Unlock!.CurrentProgress!.Value / Item.Unlock.MaxProgress!.Value, 0.0, 1.0)
        : 0.0;

    public string ProgressLabel => HasNumericProgress
        ? $"{FormatProgressNumber(Item.Unlock!.CurrentProgress!.Value)} / {FormatProgressNumber(Item.Unlock.MaxProgress!.Value)}"
        : string.Empty;

    public AchievementChannelDotState ChannelDotState => GameListItemViewModel.ResolveChannelDotState(ChannelCaptureMode);
    public bool IsChannelReady => ChannelDotState == AchievementChannelDotState.Ready;
    public bool IsChannelPendingPatch => ChannelDotState == AchievementChannelDotState.PendingGbeFork;
    public bool IsChannelNone => ChannelDotState == AchievementChannelDotState.None;
    public string ChannelDotHexColor => GameListItemViewModel.ResolveChannelDotHexColor(ChannelDotState);

    public bool CanClickToRevealSpoiler =>
        !IsUnlocked && IsHidden && !GlobalRevealSpoilers && !IsRowSpoilerRevealed;

    public bool CanCollapseRowSpoiler =>
        !IsUnlocked && IsHidden && !GlobalRevealSpoilers && IsRowSpoilerRevealed;

    public bool CanToggleRowSpoiler =>
        !IsUnlocked && IsHidden && !GlobalRevealSpoilers;

    public string RowSpoilerToggleLabel => IsRowSpoilerRevealed ? "收起剧透" : "查看隐藏条件";

    public string EffectiveDescription
    {
        get
        {
            string desc = Item.GetEffectiveDescription(GlobalRevealSpoilers || IsRowSpoilerRevealed);
            return string.IsNullOrWhiteSpace(desc)
                ? (IsHidden ? "隐藏成就（官方未公开具体文字描述）" : "达成游戏内指定条件即可解锁")
                : desc;
        }
    }

    public bool IsIconLoadStarted => Volatile.Read(ref _iconLoadStarted) != 0;

    /// <summary>
    /// Lazily triggers background icon decoding on first access so typing/filtering never blocks the UI thread.
    /// </summary>
    public BitmapImage? IconImage
    {
        get
        {
            EnsureIconLoadStarted();
            return _iconImage;
        }
    }

    public double IconOpacity
    {
        get => _iconOpacity;
        private set => SetProperty(ref _iconOpacity, value);
    }

    public AchievementRowViewModel(
        string gameId,
        string gameTitle,
        GameAchievementItemDto item,
        bool globalRevealSpoilers,
        Func<AchievementRowViewModel, bool, Task> onSetUnlockedAsync,
        Func<AchievementRowViewModel, Task> onPreviewToastAsync,
        AchievementCaptureMode channelCaptureMode = AchievementCaptureMode.None)
    {
        GameId = gameId;
        GameTitle = gameTitle;
        Item = item ?? throw new ArgumentNullException(nameof(item));
        ChannelCaptureMode = channelCaptureMode;
        _globalRevealSpoilers = globalRevealSpoilers;
        _onSetUnlockedAsync = onSetUnlockedAsync;
        _onPreviewToastAsync = onPreviewToastAsync;
        _uiDispatcher = GameListItemViewModel.ResolveLiveUiDispatcher();
        _syncContext = _uiDispatcher is null ? SynchronizationContext.Current : null;

        // Deterministic initial opacity without touching disk during construction.
        _iconOpacity = item.IsUnlocked
            ? 1.0
            : (!string.IsNullOrWhiteSpace(item.Definition.IconGrayPath) ? 0.92 : 0.45);
    }

    /// <summary>
    /// Ensures the row's achievement icon is decoded asynchronously and returns the completion task.
    /// </summary>
    public Task EnsureIconLoadedAsync(CancellationToken cancellationToken = default)
    {
        EnsureIconLoadStarted();
        return cancellationToken.CanBeCanceled
            ? _iconLoadTask.WaitAsync(cancellationToken)
            : _iconLoadTask;
    }

    private void EnsureIconLoadStarted()
    {
        if (Interlocked.Exchange(ref _iconLoadStarted, 1) != 0)
        {
            return;
        }

        string? primaryPath = Item.IsUnlocked
            ? (Item.Definition.IconPath ?? Item.Definition.IconGrayPath)
            : Item.Definition.IconGrayPath;
        string? fallbackPath = Item.IsUnlocked ? null : Item.Definition.IconPath;

        if (string.IsNullOrWhiteSpace(primaryPath) && string.IsNullOrWhiteSpace(fallbackPath))
        {
            _iconLoadTask = Task.CompletedTask;
            return;
        }

        // Fast zero-I/O memory cache hit avoids any flicker when scrolling back into view.
        if (!string.IsNullOrWhiteSpace(primaryPath)
            && ImageLoaderHelper.TryGetCached(primaryPath, 112, out BitmapImage? cachedPrimary))
        {
            _iconImage = cachedPrimary;
            _iconOpacity = Item.IsUnlocked ? 1.0 : 0.92;
            _iconLoadTask = Task.CompletedTask;
            return;
        }

        if (string.IsNullOrWhiteSpace(primaryPath)
            && !string.IsNullOrWhiteSpace(fallbackPath)
            && ImageLoaderHelper.TryGetCached(fallbackPath, 112, out BitmapImage? cachedFallback))
        {
            _iconImage = cachedFallback;
            _iconOpacity = 0.45;
            _iconLoadTask = Task.CompletedTask;
            return;
        }

        _iconLoadTask = LoadIconAsync(primaryPath, fallbackPath);
    }

    private async Task LoadIconAsync(string? primaryPath, string? fallbackPath)
    {
        BitmapImage? loaded = null;
        double targetOpacity = Item.IsUnlocked ? 1.0 : 0.92;

        if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            loaded = await ImageLoaderHelper.LoadFrozenAsync(primaryPath, maxDecodeWidth: 112).ConfigureAwait(false);
        }

        if (loaded is null && !string.IsNullOrWhiteSpace(fallbackPath))
        {
            loaded = await ImageLoaderHelper.LoadFrozenAsync(fallbackPath, maxDecodeWidth: 112).ConfigureAwait(false);
            if (!Item.IsUnlocked)
            {
                targetOpacity = 0.45;
            }
        }

        if (loaded is null)
        {
            return;
        }

        await PostToOwnerThreadAsync(() =>
        {
            _iconImage = loaded;
            IconOpacity = targetOpacity;
            OnPropertyChanged(nameof(IconImage));
        }).ConfigureAwait(false);
    }

    private Task PostToOwnerThreadAsync(Action action)
    {
        if (_uiDispatcher is not null && !_uiDispatcher.HasShutdownStarted && !_uiDispatcher.HasShutdownFinished)
        {
            if (_uiDispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return _uiDispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
        }

        if (_syncContext is not null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _syncContext.Post(_ =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        action();
        return Task.CompletedTask;
    }

    private static string FormatProgressNumber(double value)
    {
        double rounded = Math.Round(value);
        return Math.Abs(value - rounded) < 1e-6
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    partial void OnGlobalRevealSpoilersChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClickToRevealSpoiler));
        OnPropertyChanged(nameof(CanCollapseRowSpoiler));
        OnPropertyChanged(nameof(CanToggleRowSpoiler));
        OnPropertyChanged(nameof(RowSpoilerToggleLabel));
        OnPropertyChanged(nameof(EffectiveDescription));
    }

    partial void OnIsRowSpoilerRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClickToRevealSpoiler));
        OnPropertyChanged(nameof(CanCollapseRowSpoiler));
        OnPropertyChanged(nameof(CanToggleRowSpoiler));
        OnPropertyChanged(nameof(RowSpoilerToggleLabel));
        OnPropertyChanged(nameof(EffectiveDescription));
    }

    [RelayCommand]
    private void RevealRowSpoiler()
    {
        IsRowSpoilerRevealed = true;
    }

    [RelayCommand]
    private void HideRowSpoiler()
    {
        IsRowSpoilerRevealed = false;
    }

    [RelayCommand]
    private void ToggleRowSpoiler()
    {
        if (!CanToggleRowSpoiler)
        {
            return;
        }

        IsRowSpoilerRevealed = !IsRowSpoilerRevealed;
    }

    [RelayCommand]
    private Task MarkUnlockedAsync() => _onSetUnlockedAsync(this, true);

    [RelayCommand]
    private Task ResetUnlockedAsync() => _onSetUnlockedAsync(this, false);

    [RelayCommand]
    private Task PreviewToastAsync() => _onPreviewToastAsync(this);
}
