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

    public static string FormatAntiCheatTierHexColor(AntiCheatTier tier) => tier switch
    {
        AntiCheatTier.Tier0 => "#10B981",
        AntiCheatTier.Tier1 => "#F59E0B",
        AntiCheatTier.Tier2 => "#DC2626",
        AntiCheatTier.Tier3 => "#DC2626",
        _ => "#10B981",
    };

    public static string BuildVirusTotalFileUrl(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return string.Empty;
        }

        string clean = sha256.Trim().ToLowerInvariant();
        return $"https://www.virustotal.com/gui/file/{Uri.EscapeDataString(clean)}";
    }

    public static string FormatTrainerProviderLabel(TrainerProviderKind provider) => provider switch
    {
        TrainerProviderKind.FlingMain => "flingtrainer.com",
        TrainerProviderKind.FlingArchive => "archive.flingtrainer.com",
        _ => "本地导入",
    };

    public static string FormatTrainerVersionConfidenceLabel(TrainerVersionConfidence confidence) => confidence switch
    {
        TrainerVersionConfidence.RangeMatched => "版本范围匹配",
        TrainerVersionConfidence.PossiblyOutdated => "可能已过期",
        _ => "建议按更新日期核对",
    };

    public static string FormatTrainerAuthenticodeLabel(TrainerAuthenticodeStatus status) => status switch
    {
        TrainerAuthenticodeStatus.ValidSigned => "已数字签名",
        TrainerAuthenticodeStatus.InvalidOrUntrustedSignature => "签名无效/不受信任",
        TrainerAuthenticodeStatus.Unsigned => "无数字签名（外部修改器常态）",
        _ => "未检测",
    };

    public static string FormatPreTrainerSnapshotStatus(PreTrainerSnapshotStatus status, string? skipReason) => status switch
    {
        PreTrainerSnapshotStatus.Completed => "已备份",
        PreTrainerSnapshotStatus.Skipped => string.IsNullOrWhiteSpace(skipReason) ? "已跳过" : $"已跳过（{skipReason}）",
        PreTrainerSnapshotStatus.Failed => string.IsNullOrWhiteSpace(skipReason) ? "备份失败" : $"备份失败（{skipReason}）",
        _ => "未知",
    };

    public static string FormatDefenderStatusBadge(DefenderExclusionStatus status) => status switch
    {
        DefenderExclusionStatus.AlreadyExcluded => "已排除 · 就绪",
        DefenderExclusionStatus.AddedAndVerified => "已添加并回读验证",
        DefenderExclusionStatus.TamperProtectionBlocked => "篡改保护拦截（需手动添加）",
        DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode => "第三方杀软接管（被动模式）",
        DefenderExclusionStatus.SmartAppControlNotice => "Smart App Control 强制模式",
        DefenderExclusionStatus.UacCancelledByUser => "已取消 UAC",
        DefenderExclusionStatus.VerificationReadbackFailed => "回读核验未通过",
        DefenderExclusionStatus.Failed => "校验未通过",
        _ => "未配置或待检测",
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

    public static string FormatSaveTriggerLabel(SaveSnapshotTriggerKind trigger) => trigger switch
    {
        SaveSnapshotTriggerKind.Manual => "手动备份",
        SaveSnapshotTriggerKind.ManualPin => "手动固定",
        SaveSnapshotTriggerKind.AutoQuiescent => "静默热备",
        SaveSnapshotTriggerKind.PostExit => "退出自动",
        SaveSnapshotTriggerKind.PreRollbackSafety => "回滚安全点",
        SaveSnapshotTriggerKind.PreBranchSwitchSafety => "切分支安全点",
        SaveSnapshotTriggerKind.PreTrainerImport => "修改器前导入",
        SaveSnapshotTriggerKind.CloudPull => "云端拉取",
        SaveSnapshotTriggerKind.CloudDivergenceBackup => "分叉保留备份",
        SaveSnapshotTriggerKind.BranchFork => "分支起点",
        _ => "存档快照",
    };

    public static string FormatSaveFileCategoryTag(SaveFileCategory category) => category switch
    {
        SaveFileCategory.Save => "[save]",
        SaveFileCategory.Config => "[config]",
        SaveFileCategory.RegistryExport => "[registry]",
        _ => "[save]",
    };

    public static string FormatCloudProviderLabel(CloudSyncProviderType provider) => provider switch
    {
        CloudSyncProviderType.LocalFolder => "本地同步目录 / 网盘",
        CloudSyncProviderType.WebDav => "WebDAV 服务器",
        CloudSyncProviderType.S3 => "S3 / R2 对象存储",
        _ => "本地同步目录",
    };

    public static string FormatCloudSyncRelationLabel(CloudSyncRelation relation) => relation switch
    {
        CloudSyncRelation.NotConfigured => "未配置云端端点",
        CloudSyncRelation.UpToDate => "已与云端同步",
        CloudSyncRelation.FastForwardPush => "本机领先 · 可推送至云端",
        CloudSyncRelation.FastForwardPull => "云端有新进度 · 可拉取",
        CloudSyncRelation.Diverged => "发现双端分叉冲突",
        CloudSyncRelation.Error => "云同步异常",
        _ => "未知状态",
    };

    public static string FormatSaveWatcherStatusLabel(SaveWatcherStatusKind status) => status switch
    {
        SaveWatcherStatusKind.Watching => "实时监听中",
        SaveWatcherStatusKind.Debouncing => "检测到写入 · 静默防撕裂等待中",
        SaveWatcherStatusKind.Capturing => "正在增量备份快照…",
        SaveWatcherStatusKind.Error => "监听异常",
        _ => "待机未监听",
    };

    public static string FormatDeduplicationPercent(double ratio)
    {
        double pct = Math.Clamp(ratio, 0.0, 1.0) * 100.0;
        return string.Create(CultureInfo.InvariantCulture, $"去重节省 {pct:0.0}%");
    }

    public static string FormatByteDelta(long deltaBytes)
    {
        if (deltaBytes == 0)
        {
            return "±0 B";
        }

        string sign = deltaBytes > 0 ? "+" : "-";
        return $"{sign}{FormatByteSize(Math.Abs(deltaBytes))}";
    }
}
