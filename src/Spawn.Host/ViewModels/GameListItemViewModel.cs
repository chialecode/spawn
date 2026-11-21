using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Spawn.Contracts;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels;

public sealed partial class GameListItemViewModel : ObservableObject
{
    [ObservableProperty]
    private GameRecord _record;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ActiveGameSession? _activeSession;

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
    public string AntiCheatBadgeText => UiFormatters.FormatAntiCheatTierBadge(Record.AntiCheatTier);

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
                _ => "就绪 · 自动探测成就通道",
            };
        }
    }

    public bool IsChannelPendingPatch => Record.AchievementCaptureMode == AchievementCaptureMode.PendingGbeForkActivation;

    public string PlaytimeSummaryText
    {
        get
        {
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
                return "正在运行";
            }

            return Record.LastPlayedUtc.HasValue
                ? $"上次游玩 {Record.LastPlayedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "尚未启动";
        }
    }

    public GameListItemViewModel(GameRecord record)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        ReloadArtwork();
    }

    public void UpdateRecord(GameRecord updated)
    {
        Record = updated;
        ReloadArtwork();
        NotifyDerivedProperties();
    }

    public void UpdateSession(ActiveGameSession? session)
    {
        ActiveSession = session;
        NotifyDerivedProperties();
    }

    public void UpdateAchievementCounts(int unlocked, int total, string? channelStatus = null)
    {
        UnlockedAchievements = unlocked;
        TotalAchievements = total;
        if (!string.IsNullOrWhiteSpace(channelStatus))
        {
            ChannelStatusOverride = channelStatus;
        }

        OnPropertyChanged(nameof(CompletionPercent));
        OnPropertyChanged(nameof(AchievementRatioText));
        OnPropertyChanged(nameof(EffectiveChannelStatusText));
    }

    public void NotifyPlaytimeTick()
    {
        OnPropertyChanged(nameof(PlaytimeSummaryText));
        OnPropertyChanged(nameof(LastPlayedSummaryText));
    }

    private void ReloadArtwork()
    {
        SmallIcon = ImageLoaderHelper.LoadFrozen(Record.IconPath ?? Record.CoverImagePath, maxDecodeWidth: 96);
        CoverImage = ImageLoaderHelper.LoadFrozen(Record.CoverImagePath ?? Record.HeroImagePath ?? Record.IconPath, maxDecodeWidth: 320);
    }

    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PlatformSource));
        OnPropertyChanged(nameof(PlatformShortLabel));
        OnPropertyChanged(nameof(PlatformFullLabel));
        OnPropertyChanged(nameof(AntiCheatBadgeText));
        OnPropertyChanged(nameof(InitialsText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(SidebarRightTag));
        OnPropertyChanged(nameof(EffectiveChannelStatusText));
        OnPropertyChanged(nameof(IsChannelPendingPatch));
        OnPropertyChanged(nameof(PlaytimeSummaryText));
        OnPropertyChanged(nameof(LastPlayedSummaryText));
    }
}
