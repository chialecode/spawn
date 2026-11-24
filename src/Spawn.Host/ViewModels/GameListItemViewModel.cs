using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Spawn.Contracts;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels;

public sealed partial class GameListItemViewModel : ObservableObject
{
    private readonly record struct ArtworkFileStamp(string? FullPath, bool Exists, long Length, long LastWriteUtcTicks);

    private ArtworkFileStamp _smallIconStamp;
    private ArtworkFileStamp _coverImageStamp;
    private bool _artworkInitialized;

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
    public AntiCheatTier AntiCheatTier => Record.AntiCheatTier;
    public string AntiCheatBadgeText => UiFormatters.FormatAntiCheatTierBadge(Record.AntiCheatTier);
    public string AntiCheatBadgeHexColor => UiFormatters.FormatAntiCheatTierHexColor(Record.AntiCheatTier);
    public bool HasVerifiedOfflineBypassBadge => Record.AntiCheatTier == AntiCheatTier.Tier2 && Record.VerifiedOfflineBypass;
    public string VerifiedOfflineBypassBadgeText => HasVerifiedOfflineBypassBadge ? "离线无 AC 就绪" : string.Empty;

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
        ArgumentNullException.ThrowIfNull(updated);

        bool recordChanged = !Equals(Record, updated);
        if (recordChanged)
        {
            Record = updated;
        }

        ReloadArtwork();

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
        OnPropertyChanged(nameof(PlaytimeSummaryText));
        OnPropertyChanged(nameof(LastPlayedSummaryText));
    }

    private void ReloadArtwork()
    {
        string? smallSource = Record.IconPath ?? Record.CoverImagePath;
        string? coverSource = Record.CoverImagePath ?? Record.HeroImagePath ?? Record.IconPath;

        ArtworkFileStamp nextSmallStamp = CaptureArtworkStamp(smallSource);
        ArtworkFileStamp nextCoverStamp = CaptureArtworkStamp(coverSource);

        if (!_artworkInitialized || nextSmallStamp != _smallIconStamp)
        {
            _smallIconStamp = nextSmallStamp;
            SmallIcon = ImageLoaderHelper.LoadFrozen(smallSource, maxDecodeWidth: 96);
        }

        if (!_artworkInitialized || nextCoverStamp != _coverImageStamp)
        {
            _coverImageStamp = nextCoverStamp;
            CoverImage = ImageLoaderHelper.LoadFrozen(coverSource, maxDecodeWidth: 320);
        }

        _artworkInitialized = true;
    }

    private static ArtworkFileStamp CaptureArtworkStamp(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return default;
        }

        try
        {
            string fullPath = Path.GetFullPath(rawPath);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new ArtworkFileStamp(fullPath, Exists: false, Length: 0, LastWriteUtcTicks: 0);
            }

            return new ArtworkFileStamp(fullPath, Exists: true, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            return new ArtworkFileStamp(rawPath, Exists: false, Length: 0, LastWriteUtcTicks: 0);
        }
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
        OnPropertyChanged(nameof(InitialsText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(SidebarRightTag));
        OnPropertyChanged(nameof(EffectiveChannelStatusText));
        OnPropertyChanged(nameof(IsChannelPendingPatch));
        OnPropertyChanged(nameof(PlaytimeSummaryText));
        OnPropertyChanged(nameof(LastPlayedSummaryText));
    }
}
