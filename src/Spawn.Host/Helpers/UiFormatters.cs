using System.Globalization;
using Spawn.Contracts;

namespace Spawn.Host.Helpers;

public static class UiFormatters
{
    public static string FormatDurationHms(long totalSeconds)
    {
        long clamped = Math.Max(0, totalSeconds);
        long hours = clamped / 3600;
        long minutes = (clamped % 3600) / 60;
        long seconds = clamped % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:D2}:{minutes:D2}:{seconds:D2}");
    }

    public static string FormatByteSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        double kb = bytes / 1024.0;
        if (kb < 1024.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{kb:0.0} KB");
        }

        double mb = kb / 1024.0;
        if (mb < 1024.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{mb:0.00} MB");
        }

        double gb = mb / 1024.0;
        return string.Create(CultureInfo.InvariantCulture, $"{gb:0.00} GB");
    }

    public static string FormatGameDataCategoryTitle(GameDataCategory category) => category switch
    {
        GameDataCategory.SaveData => "存档数据",
        GameDataCategory.AchievementData => "离线成就文件",
        GameDataCategory.ConfigAndLogs => "配置与日志",
        GameDataCategory.RegistryData => "注册表项",
        _ => "游戏数据",
    };

    public static string FormatPlatformShortLabel(PlatformSource source) => source switch
    {
        PlatformSource.GogOffline => "GOG",
        PlatformSource.Steamrip => "Steamrip",
        PlatformSource.SteamLocal => "Steam",
        _ => "单机",
    };

    public static string FormatPlatformFullLabel(PlatformSource source) => source switch
    {
        PlatformSource.GogOffline => "GOG 离线版",
        PlatformSource.Steamrip => "Steamrip 免安装",
        PlatformSource.SteamLocal => "Steam 本地库",
        _ => "独立单机",
    };

    public static string FormatAntiCheatTierBadge(AntiCheatTier tier) => tier switch
    {
        AntiCheatTier.Tier0 => "Tier 0 纯净单机",
        AntiCheatTier.Tier1 => "Tier 1 弱保护",
        AntiCheatTier.Tier2 => "Tier 2 反作弊保护",
        AntiCheatTier.Tier3 => "Tier 3 内核级保护",
        _ => "Tier 0 纯净单机",
    };

    public static string FormatOverlayThemeLabel(OverlayThemeKind theme) => theme switch
    {
        OverlayThemeKind.Ps5Trophy => "PS5 奖杯（100% 自动白金连发）",
        OverlayThemeKind.XboxRare => "Xbox 稀有紫钻",
        _ => "Steam 经典",
    };

    public static string FormatOverlayPositionLabel(OverlayPositionKind pos) => pos switch
    {
        OverlayPositionKind.BottomRight => "右下角（推荐）",
        OverlayPositionKind.TopRight => "右上角",
        OverlayPositionKind.BottomLeft => "左下角",
        OverlayPositionKind.TopLeft => "左上角",
        OverlayPositionKind.TopCenter => "顶部居中",
        OverlayPositionKind.BottomCenter => "底部居中",
        _ => "右下角（推荐）",
    };

    public static string FormatOverlaySoundPresetLabel(OverlaySoundPreset preset) => preset switch
    {
        OverlaySoundPreset.ThemeSynthesized => "主题合成和弦音效",
        OverlaySoundPreset.CustomWav => "自定义 WAV 音频文件",
        _ => "Steam 原版同款音效（推荐）",
    };
}
