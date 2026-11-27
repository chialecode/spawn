using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Spawn.Contracts;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels;

/// <summary>
/// Tri-state indicator for a game's achievement capture channel dot
/// (<c>Ready</c> green, <c>PendingGbeFork</c> amber, <c>None</c> muted gray).
/// </summary>
public enum AchievementChannelDotState
{
    None = 0,
    PendingGbeFork = 1,
    Ready = 2,
}

public sealed partial class GameListItemViewModel : ObservableObject
{
    private static readonly HashSet<string> FallbackIconFileStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "icon",
        "icon_online",
        "sidebaricon",
        "sidebaricon2x",
    };

    private readonly Dispatcher? _uiDispatcher;

    private string? _lastSmallIconPath;
    private string? _lastCoverSourcePath;
    private bool _isCoverIconFallback;
    private int _artworkLoadVersion;
    private CancellationTokenSource? _artworkLoadCts;

    public Task PendingArtworkLoadTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private GameRecord _record;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ActiveGameSession? _activeSession;

    [ObservableProperty]
    private GameSteamLaunchSettings _steamLaunchSettings = new();

    [ObservableProperty]
    private int _unlockedAchievements;

    [ObservableProperty]
    private int _totalAchievements;

    [ObservableProperty]
    private string _channelStatusOverride = string.Empty;

    [ObservableProperty]
    private BitmapImage? _smallIcon;

    [ObservableProperty]
    private BitmapImage? _coverImage;

    public string GameId => Record.GameId;
    public string Title => Record.Title;
    public PlatformSource PlatformSource => Record.PlatformSource;
    public string PlatformShortLabel => UiFormatters.FormatPlatformShortLabel(Record.PlatformSource);
    public string PlatformFullLabel => UiFormatters.FormatPlatformFullLabel(Record.PlatformSource);
    public AntiCheatTier AntiCheatTier => Record.AntiCheatTier;
    public string AntiCheatBadgeText => UiFormatters.FormatAntiCheatTierBadge(Record.AntiCheatTier);
    public string AntiCheatBadgeHexColor => UiFormatters.FormatAntiCheatTierHexColor(Record.AntiCheatTier);
    public bool HasVerifiedOfflineBypassBadge => Record.AntiCheatTier == AntiCheatTier.Tier2 && Record.VerifiedOfflineBypass;
    public string VerifiedOfflineBypassBadgeText => HasVerifiedOfflineBypassBadge ? "离线无 AC 就绪" : string.Empty;

    public bool IsOfficialSteamLaunchTarget =>
        Record.PlatformSource == PlatformSource.SteamLocal && Record.SteamAppId is > 0;

    public bool IsSteamLaunchBlockedByAntiCheat =>
        !IsOfficialSteamLaunchTarget && Record.AntiCheatTier >= AntiCheatTier.Tier2;

    public bool IsSteamLaunchConfigured =>
        SteamLaunchSettings.LaunchMode == GameLaunchMode.Steam && !IsSteamLaunchBlockedByAntiCheat;

    public string LaunchModeBadgeText => IsSteamLaunchConfigured ? "通过 Steam 启动" : string.Empty;

    public string LaunchModeBadgeToolTip => IsSteamLaunchConfigured
        ? (IsOfficialSteamLaunchTarget
            ? $"默认通过 Steam 官方协议（steam://rungameid/{Record.SteamAppId}）启动，启用 Steam Input 手柄映射"
            : "默认通过 Steam 非 Steam 快捷方式启动，启用 Steam Input 手柄映射（可在「游戏属性与启动设置」修改）")
        : string.Empty;

    /// <summary>
    /// True when <see cref="CoverImage"/> comes from a dedicated poster/cover/hero asset (safe for UniformToFill poster card rendering).
    /// </summary>
    public bool HasPosterCover => CoverImage is not null && !_isCoverIconFallback;

    /// <summary>
    /// True when <see cref="CoverImage"/> falls back to an extracted icon rather than a poster cover (should be centered without stretching).
    /// </summary>
    public bool HasFallbackIcon => CoverImage is not null && _isCoverIconFallback;

    public bool IsCoverIconFallback => _isCoverIconFallback;

    public string InitialsText
    {
        get
        {
            string t = Record.Title.Trim();
            if (string.IsNullOrEmpty(t))
            {
                return "S";
            }

            return t.Length >= 2 && char.IsAsciiLetter(t[0]) && char.IsAsciiLetter(t[1])
                ? t[..2].ToUpperInvariant()
                : t[..1].ToUpperInvariant();
        }
    }

    public bool IsRunning => ActiveSession is not null && ActiveSession.State != PlaySessionActivityState.Stopped;

    public PlaySessionActivityState LiveActivityState => ActiveSession?.State ?? PlaySessionActivityState.Stopped;

    public string LiveActivityStateText => LiveActivityState switch
    {
        PlaySessionActivityState.Launching => "正在启动",
        PlaySessionActivityState.ShellBreakingWatch => ActiveSession?.LaunchedViaSteam == true
            ? "等待 Steam 启动"
            : "等待游戏主进程",
        PlaySessionActivityState.Active => "有效游玩中",
        PlaySessionActivityState.Background => "后台切出",
        PlaySessionActivityState.Afk => "挂机静默",
        PlaySessionActivityState.Stopping => "正在退出",
        _ => string.Empty,
    };

    public long CurrentSessionTotalSec => ActiveSession is null
        ? 0
        : Math.Max(0, ActiveSession.SessionActiveSec)
          + Math.Max(0, ActiveSession.SessionBackgroundSec)
          + Math.Max(0, ActiveSession.SessionAfkSec);

    public string CurrentSessionDurationText => IsRunning
        ? $"本次 {UiFormatters.FormatDurationHms(CurrentSessionTotalSec)}"
        : string.Empty;

    public string LiveSessionBadgeText
    {
        get
        {
            if (!IsRunning)
            {
                return string.Empty;
            }

            return LiveActivityState switch
            {
                PlaySessionActivityState.Active
                    or PlaySessionActivityState.Background
                    or PlaySessionActivityState.Afk => $"{LiveActivityStateText} · {CurrentSessionDurationText}",
                _ => LiveActivityStateText,
            };
        }
    }

    public bool HasEverPlayed => Record.TotalPlaytimeSec > 0 || Record.LastPlayedUtc.HasValue || IsRunning;

    public string SidebarRightTag => IsRunning ? "运行中" : PlatformShortLabel;

    public double CompletionPercent => TotalAchievements <= 0
        ? 0.0
        : Math.Round((double)UnlockedAchievements * 100.0 / TotalAchievements, 1);

    public string AchievementRatioText => TotalAchievements > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{UnlockedAchievements}/{TotalAchievements}")
        : "0/0";

    public string EffectiveChannelStatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ChannelStatusOverride))
            {
                return ChannelStatusOverride;
            }

            if (!string.IsNullOrWhiteSpace(Record.AchievementChannelLabel))
            {
                return Record.AchievementChannelLabel!;
            }

            return Record.AchievementCaptureMode switch
            {
                AchievementCaptureMode.GogNativeSave => "GOG 原生存档监听 · 就绪",
                AchievementCaptureMode.GogCometLocal => "GOG Comet 9977 · 就绪",
                AchievementCaptureMode.SteamEmulator => "Goldberg GSE / 模拟器 · 就绪",
                AchievementCaptureMode.SteamLocalCache => "Steam 本地统计缓存 · 就绪",
                AchievementCaptureMode.GbeForkActivated => "Goldberg GSE (已无损激活) · 就绪",
                AchievementCaptureMode.PendingGbeForkActivation => "待激活离线成就补丁",
                _ => "纯单机运行模式",
            };
        }
    }

    public AchievementChannelDotState ChannelDotState => ResolveChannelDotState(Record.AchievementCaptureMode);
    public bool IsChannelReady => ChannelDotState == AchievementChannelDotState.Ready;
    public bool IsChannelPendingPatch => ChannelDotState == AchievementChannelDotState.PendingGbeFork;
    public bool IsChannelNone => ChannelDotState == AchievementChannelDotState.None;
    public string ChannelDotHexColor => ResolveChannelDotHexColor(ChannelDotState);

    public static AchievementChannelDotState ResolveChannelDotState(AchievementCaptureMode mode) => mode switch
    {
        AchievementCaptureMode.PendingGbeForkActivation => AchievementChannelDotState.PendingGbeFork,
        AchievementCaptureMode.SteamEmulator
            or AchievementCaptureMode.SteamLocalCache
            or AchievementCaptureMode.GogCometLocal
            or AchievementCaptureMode.GogNativeSave
            or AchievementCaptureMode.GbeForkActivated => AchievementChannelDotState.Ready,
        _ => AchievementChannelDotState.None,
    };

    public static string ResolveChannelDotHexColor(AchievementChannelDotState state) => state switch
    {
        AchievementChannelDotState.Ready => "#10B981",
        AchievementChannelDotState.PendingGbeFork => "#F59E0B",
        _ => "#9CA3AF",
    };

    public string PlaytimeSummaryText
    {
        get
        {
            if (!HasEverPlayed)
            {
                return "尚未游玩";
            }

            long active = Record.TotalActiveSec + (ActiveSession?.SessionActiveSec ?? 0);
            long bg = Record.TotalBackgroundSec + (ActiveSession?.SessionBackgroundSec ?? 0);
            long afk = Record.TotalAfkSec + (ActiveSession?.SessionAfkSec ?? 0);
            return $"有效游玩 {UiFormatters.FormatDurationHms(active)}  ·  后台 {UiFormatters.FormatDurationHms(bg)}  ·  挂机 {UiFormatters.FormatDurationHms(afk)}";
        }
    }

    public string LastPlayedSummaryText
    {
        get
        {
            if (IsRunning)
            {
                return LiveSessionBadgeText;
            }

            return Record.LastPlayedUtc.HasValue
                ? $"上次游玩 {Record.LastPlayedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "尚未游玩";
        }
    }

    public GameListItemViewModel(GameRecord record)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _uiDispatcher = ResolveLiveUiDispatcher();
        ReloadArtwork(forceRefresh: true);
    }

    internal static Dispatcher? ResolveLiveUiDispatcher()
    {
        if (SynchronizationContext.Current is DispatcherSynchronizationContext)
        {
            Dispatcher? currentDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
            if (currentDispatcher is not null
                && !currentDispatcher.HasShutdownStarted
                && !currentDispatcher.HasShutdownFinished)
            {
                return currentDispatcher;
            }
        }

        Dispatcher? appDispatcher = System.Windows.Application.Current?.Dispatcher;
        if (appDispatcher is not null
            && !appDispatcher.HasShutdownStarted
            && !appDispatcher.HasShutdownFinished
            && appDispatcher.CheckAccess())
        {
            return appDispatcher;
        }

        return null;
    }

    partial void OnCoverImageChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(HasPosterCover));
        OnPropertyChanged(nameof(HasFallbackIcon));
    }

    public void UpdateRecord(GameRecord updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        bool recordChanged = !Equals(Record, updated);
        if (recordChanged)
        {
            Record = updated;
        }

        // Always revalidate artwork asynchronously in background on UpdateRecord so in-place overwrites
        // (e.g. upgraded HD icon.png or replaced custom cover) or deletions refresh cleanly without UI-thread I/O.
        ReloadArtwork(forceRefresh: true);

        if (recordChanged)
        {
            NotifyDerivedProperties();
        }
    }

    public void UpdateSession(ActiveGameSession? session)
    {
        if (Equals(ActiveSession, session))
        {
            return;
        }

        ActiveSession = session;
        NotifyDerivedProperties();
    }

    public void UpdateSteamLaunchSettings(GameSteamLaunchSettings? settings)
    {
        GameSteamLaunchSettings normalized = settings ?? new GameSteamLaunchSettings();
        if (Equals(SteamLaunchSettings, normalized))
        {
            return;
        }

        SteamLaunchSettings = normalized;
        OnPropertyChanged(nameof(IsSteamLaunchConfigured));
        OnPropertyChanged(nameof(LaunchModeBadgeText));
        OnPropertyChanged(nameof(LaunchModeBadgeToolTip));
    }

    public void UpdateAchievementCounts(int unlocked, int total, string? channelStatus = null)
    {
        bool unlockedChanged = UnlockedAchievements != unlocked;
        bool totalChanged = TotalAchievements != total;
        bool statusChanged = !string.IsNullOrWhiteSpace(channelStatus)
                             && !string.Equals(ChannelStatusOverride, channelStatus, StringComparison.Ordinal);

        if (!unlockedChanged && !totalChanged && !statusChanged)
        {
            return;
        }

        UnlockedAchievements = unlocked;
        TotalAchievements = total;
        if (statusChanged)
        {
            ChannelStatusOverride = channelStatus!;
        }

        if (unlockedChanged || totalChanged)
        {
            OnPropertyChanged(nameof(CompletionPercent));
            OnPropertyChanged(nameof(AchievementRatioText));
        }

        if (statusChanged)
        {
            OnPropertyChanged(nameof(EffectiveChannelStatusText));
        }
    }

    public void NotifyPlaytimeTick()
    {
        OnPropertyChanged(nameof(LiveActivityState));
        OnPropertyChanged(nameof(LiveActivityStateText));
        OnPropertyChanged(nameof(CurrentSessionTotalSec));
        OnPropertyChanged(nameof(CurrentSessionDurationText));
        OnPropertyChanged(nameof(LiveSessionBadgeText));
        OnPropertyChanged(nameof(HasEverPlayed));
        OnPropertyChanged(nameof(PlaytimeSummaryText));
        OnPropertyChanged(nameof(LastPlayedSummaryText));
    }

    private void ReloadArtwork(bool forceRefresh)
    {
        string? smallSource = Record.IconPath ?? Record.CoverImagePath;
        string? coverSource = Record.CoverImagePath ?? Record.HeroImagePath ?? Record.IconPath;

        bool nextIsFallback = IsIconFallbackArtwork(coverSource, Record.IconPath);
        if (_isCoverIconFallback != nextIsFallback)
        {
            _isCoverIconFallback = nextIsFallback;
            OnPropertyChanged(nameof(IsCoverIconFallback));
            OnPropertyChanged(nameof(HasPosterCover));
            OnPropertyChanged(nameof(HasFallbackIcon));
        }

        bool smallPathChanged = !string.Equals(smallSource, _lastSmallIconPath, StringComparison.OrdinalIgnoreCase);
        bool coverPathChanged = !string.Equals(coverSource, _lastCoverSourcePath, StringComparison.OrdinalIgnoreCase);

        if (!forceRefresh && !smallPathChanged && !coverPathChanged)
        {
            return;
        }

        _lastSmallIconPath = smallSource;
        _lastCoverSourcePath = coverSource;

        bool needAsyncSmall = false;
        bool needAsyncCover = false;

        if (string.IsNullOrWhiteSpace(smallSource))
        {
            SmallIcon = null;
        }
        else
        {
            if (ImageLoaderHelper.TryGetCached(smallSource, 96, out var cachedSmall))
            {
                // Immediate flicker-free placeholder from memory cache; still revalidate LastWriteTimeUtc in background on forceRefresh.
                SmallIcon = cachedSmall;
                needAsyncSmall = forceRefresh;
            }
            else
            {
                needAsyncSmall = true;
            }
        }

        if (string.IsNullOrWhiteSpace(coverSource))
        {
            CoverImage = null;
        }
        else
        {
            if (ImageLoaderHelper.TryGetCached(coverSource, 320, out var cachedCover))
            {
                CoverImage = cachedCover;
                needAsyncCover = forceRefresh;
            }
            else
            {
                needAsyncCover = true;
            }
        }

        if (!needAsyncSmall && !needAsyncCover)
        {
            PendingArtworkLoadTask = Task.CompletedTask;
            return;
        }

        int version = Interlocked.Increment(ref _artworkLoadVersion);
        CancellationTokenSource? previousCts = Interlocked.Exchange(ref _artworkLoadCts, new CancellationTokenSource());
        try
        {
            previousCts?.Cancel();
            previousCts?.Dispose();
        }
        catch
        {
            // Ignore cancellation race.
        }

        CancellationToken ct = _artworkLoadCts!.Token;
        PendingArtworkLoadTask = LoadArtworkInBackgroundAsync(
            version,
            needAsyncSmall ? smallSource : null,
            needAsyncCover ? coverSource : null,
            ct);
    }

    private async Task LoadArtworkInBackgroundAsync(
        int version,
        string? smallSource,
        string? coverSource,
        CancellationToken cancellationToken)
    {
        BitmapImage? decodedSmall = null;
        BitmapImage? decodedCover = null;

        if (!string.IsNullOrWhiteSpace(smallSource))
        {
            decodedSmall = await ImageLoaderHelper.LoadFrozenAsync(smallSource, maxDecodeWidth: 96, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(coverSource))
        {
            decodedCover = await ImageLoaderHelper.LoadFrozenAsync(coverSource, maxDecodeWidth: 320, cancellationToken)
                .ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _artworkLoadVersion) != version)
        {
            return;
        }

        try
        {
            await PostToOwnerThreadAsync(() =>
            {
                if (Volatile.Read(ref _artworkLoadVersion) != version)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(smallSource))
                {
                    SmallIcon = decodedSmall;
                }

                if (!string.IsNullOrWhiteSpace(coverSource))
                {
                    CoverImage = decodedCover;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore Dispatcher shutdown cancellation during app exit.
        }
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

        // No live WPF Dispatcher (headless/test execution): decoded BitmapImage instances are already frozen,
        // so updating properties directly avoids starving on non-WPF SynchronizationContexts (e.g. xUnit MaxConcurrencySyncContext).
        action();
        return Task.CompletedTask;
    }

    internal static bool IsIconFallbackArtwork(string? coverSource, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(coverSource))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(coverSource), Path.GetFullPath(iconPath), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                if (string.Equals(coverSource, iconPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        string ext = Path.GetExtension(coverSource);
        if (string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(coverSource);
        return FallbackIconFileStems.Contains(stem);
    }

    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PlatformSource));
        OnPropertyChanged(nameof(PlatformShortLabel));
        OnPropertyChanged(nameof(PlatformFullLabel));
        OnPropertyChanged(nameof(AntiCheatTier));
        OnPropertyChanged(nameof(AntiCheatBadgeText));
        OnPropertyChanged(nameof(AntiCheatBadgeHexColor));
        OnPropertyChanged(nameof(HasVerifiedOfflineBypassBadge));
        OnPropertyChanged(nameof(VerifiedOfflineBypassBadgeText));
        OnPropertyChanged(nameof(IsOfficialSteamLaunchTarget));
        OnPropertyChanged(nameof(IsSteamLaunchBlockedByAntiCheat));
        OnPropertyChanged(nameof(IsSteamLaunchConfigured));
        OnPropertyChanged(nameof(LaunchModeBadgeText));
        OnPropertyChanged(nameof(LaunchModeBadgeToolTip));
        OnPropertyChanged(nameof(HasPosterCover));
        OnPropertyChanged(nameof(HasFallbackIcon));
        OnPropertyChanged(nameof(IsCoverIconFallback));
        OnPropertyChanged(nameof(InitialsText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(LiveActivityState));
        OnPropertyChanged(nameof(LiveActivityStateText));
        OnPropertyChanged(nameof(CurrentSessionTotalSec));
        OnPropertyChanged(nameof(CurrentSessionDurationText));
        OnPropertyChanged(nameof(LiveSessionBadgeText));
        OnPropertyChanged(nameof(HasEverPlayed));
        OnPropertyChanged(nameof(SidebarRightTag));
        OnPropertyChanged(nameof(EffectiveChannelStatusText));
        OnPropertyChanged(nameof(ChannelDotState));
        OnPropertyChanged(nameof(IsChannelReady));
        OnPropertyChanged(nameof(IsChannelPendingPatch));
        OnPropertyChanged(nameof(IsChannelNone));
        OnPropertyChanged(nameof(ChannelDotHexColor));
        OnPropertyChanged(nameof(PlaytimeSummaryText));
        OnPropertyChanged(nameof(LastPlayedSummaryText));
    }
}
