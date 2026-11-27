using System.Text.Json.Serialization;

namespace Spawn.Contracts;

/// <summary>
/// Canonical definition of a single achievement in a game's schema.
/// </summary>
public sealed record AchievementDefinitionDto
{
    public required string ApiName { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? HiddenSpoilerDescription { get; init; }
    public bool IsHidden { get; init; }
    public string? IconPath { get; init; }
    public string? IconGrayPath { get; init; }
    public double? GlobalPercent { get; init; }
    public int SortOrder { get; init; }

    [JsonIgnore]
    public bool IsRare => GlobalPercent is < 10.0;
}

/// <summary>
/// Player unlock state for a single achievement.
/// </summary>
public sealed record AchievementUnlockDto
{
    public required string GameId { get; init; }
    public required string ApiName { get; init; }
    public bool IsUnlocked { get; init; }
    public DateTimeOffset? UnlockedUtc { get; init; }
    public double? CurrentProgress { get; init; }
    public double? MaxProgress { get; init; }
    public string SourceProvider { get; init; } = string.Empty;
    public string? RawSourcePath { get; init; }
    public bool NotifiedPopup { get; init; }
}

/// <summary>
/// Combined achievement definition + player unlock status row for UI presentation.
/// </summary>
public sealed record GameAchievementItemDto
{
    public required AchievementDefinitionDto Definition { get; init; }
    public AchievementUnlockDto? Unlock { get; init; }

    [JsonIgnore]
    public bool IsUnlocked => Unlock?.IsUnlocked ?? false;

    [JsonIgnore]
    public DateTimeOffset? UnlockedUtc => Unlock?.UnlockedUtc;

    public string GetEffectiveDescription(bool revealHiddenSpoilers)
    {
        if (IsUnlocked || !Definition.IsHidden || revealHiddenSpoilers)
        {
            if (!string.IsNullOrWhiteSpace(Definition.Description))
            {
                return Definition.Description;
            }

            if (!string.IsNullOrWhiteSpace(Definition.HiddenSpoilerDescription))
            {
                return Definition.HiddenSpoilerDescription!;
            }

            return string.Empty;
        }

        return "隐藏成就 · 点击展开查看条件";
    }
}

/// <summary>
/// Full achievement schema + unlock progress summary for a single game.
/// </summary>
public sealed record GameAchievementSummaryDto
{
    public required string GameId { get; init; }
    public string SchemaKey { get; init; } = string.Empty;
    public AchievementSchemaSource SchemaSource { get; init; } = AchievementSchemaSource.None;
    public AchievementCaptureMode CaptureMode { get; init; } = AchievementCaptureMode.None;
    public string ChannelStatusText { get; init; } = string.Empty;
    public bool HasGbeForkActivated { get; init; }
    public bool CanActivateGbeFork { get; init; }
    public int UnlockedCount { get; init; }
    public int TotalCount { get; init; }
    public DateTimeOffset? LastSyncedUtc { get; init; }
    public IReadOnlyList<GameAchievementItemDto> Items { get; init; } = [];

    [JsonIgnore]
    public double CompletionPercent => TotalCount <= 0 ? 0.0 : Math.Round((double)UnlockedCount * 100.0 / TotalCount, 1);
}

/// <summary>
/// Outcome of deploying or restoring the non-destructive gbe_fork offline achievement enabler.
/// </summary>
public sealed record GbeForkActivationResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public string? BackupDllPath { get; init; }
    public string? OriginalDllSha256 { get; init; }

    public static GbeForkActivationResult Ok(string message, string? backupDllPath = null, string? originalSha256 = null) =>
        new()
        {
            Succeeded = true,
            Message = message,
            BackupDllPath = backupDllPath,
            OriginalDllSha256 = originalSha256,
        };

    public static GbeForkActivationResult Fail(string message) =>
        new() { Succeeded = false, Message = message };
}

/// <summary>
/// IPC payload dispatched from <c>Spawn.Host</c> to <c>Spawn.OverlayHost</c> over Named Pipe JSON-RPC.
/// </summary>
public sealed record OverlayToastRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string? GameId { get; init; }
    public required string GameTitle { get; init; }
    public required string AchievementApiName { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public bool IsRare { get; init; }
    public double? GlobalPercent { get; init; }
    public bool IsPlatinumCompletion { get; init; }
    public OverlayThemeKind Theme { get; init; } = OverlayThemeKind.Steam;
    public OverlayPositionKind Position { get; init; } = OverlayPositionKind.BottomRight;
    public OverlaySoundPreset SoundPreset { get; init; } = OverlaySoundPreset.SteamAuthentic;
    public string? CustomSoundPath { get; init; }
    public string? AudioOutputDeviceId { get; init; }
    public double SoundVolume { get; init; } = 1.0;
    public bool MuteSound { get; init; }
    public int DurationMs { get; init; } = 4500;
    public bool ReplaceActivePreview { get; init; }
    public int? DisplayDurationMs { get; init; }
    public bool EnhanceRareAndPlatinumSound { get; init; } = true;
}

/// <summary>
/// Optional diagnostic surface exposed by <c>Spawn.Host</c>'s overlay IPC wrapper so achievement callers
/// can present actionable launch failure reasons without breaking <see cref="IOverlayIpcClient"/>.
/// </summary>
public interface IOverlayIpcDiagnostics
{
    string? LastLaunchError { get; }
}

/// <summary>
/// Honest outcome of dispatching a test or preview achievement toast over Overlay IPC.
/// </summary>
public sealed record OverlayToastDispatchResult(
    bool Succeeded,
    string Message);

/// <summary>
/// Honest outcome of manually marking or resetting an achievement unlock state.
/// </summary>
public sealed record ManualAchievementToggleResult(
    bool Succeeded,
    bool SyncedToLocalEmulatorFile,
    string Message);

/// <summary>
/// Well-known logical identifiers for <see cref="OverlaySettingsDto.AudioOutputDeviceId"/>.
/// </summary>
public static class OverlayAudioOutputModes
{
    public const string AutoSmart = "auto-smart";
    public const string SystemDefault = "system-default";
}

/// <summary>
/// Heartbeat response returned by <c>Spawn.OverlayHost</c>.
/// </summary>
public sealed record OverlayHeartbeatReply
{
    public int ProcessId { get; init; }
    public long WindowHandle { get; init; }
    public DateTimeOffset ServerTimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public int PendingToastCount { get; init; }
}

/// <summary>
/// Desensitized status summary of the configured Steam Web API credential.
/// </summary>
public sealed record SteamApiKeyStatusDto(
    bool IsConfigured,
    string SourceLabel,
    string? MaskedPreview);

/// <summary>
/// User-configurable overlay &amp; sound preferences stored in <c>AppSettings</c>.
/// </summary>
public sealed record OverlaySettingsDto
{
    public OverlayThemeKind Theme { get; init; } = OverlayThemeKind.Steam;
    public OverlayPositionKind Position { get; init; } = OverlayPositionKind.BottomRight;
    public OverlaySoundPreset SoundPreset { get; init; } = OverlaySoundPreset.SteamAuthentic;
    public string? CustomSoundPath { get; init; }
    public string? AudioOutputDeviceId { get; init; }
    public double SoundVolume { get; init; } = 1.0;
    public bool MuteSound { get; init; }
    public bool EnhanceRareAndPlatinumSound { get; init; } = true;
}

/// <summary>
/// Well-known setting keys persisted in the <c>app_settings</c> table.
/// </summary>
public static class AppSettingKeys
{
    public const string UiTheme = "ui.theme";
    public const string OverlayTheme = "overlay.theme";
    public const string OverlayPosition = "overlay.position";
    public const string OverlaySoundPreset = "overlay.sound_preset";
    public const string OverlayCustomSoundPath = "overlay.custom_sound_path";
    public const string OverlayAudioDeviceId = "overlay.audio_device_id";
    public const string OverlaySoundVolume = "overlay.sound_volume";
    public const string OverlayMuteSound = "overlay.mute_sound";
    public const string OverlayEnhanceRareAndPlatinumSound = "overlay.enhance_rare_platinum_sound";
    public const string AchievementRevealSpoilers = "achievement.reveal_spoilers";
    public const string AchievementLanguage = "achievement.language";
    public const string LibraryScanDirectories = "library.scan_directories";
    public const string LauncherAfkTimeoutSec = "launcher.afk_timeout_sec";
    public const string SteamWebApiCredentialId = "steam-web-api";

    // M3 Trainer & Sandbox settings
    public const string TrainerCustomSandboxRoot = "trainer.custom_sandbox_root";
    public const string TrainerAudioMode = "trainer.audio_mode";
    public const string TrainerCueVolume = "trainer.cue_volume";
    public const string TrainerEnableA0PreMute = "trainer.enable_a0_premute";
    public const string TrainerHotkeyHoldMs = "trainer.hotkey_hold_ms";
    public const string TrainerPreSnapshotEnabled = "trainer.pre_snapshot_enabled";
    public const string TrainerPreSnapshotRetentionCount = "trainer.pre_snapshot_retention_count";
    public const string TrainerPreSnapshotMaxSizeMb = "trainer.pre_snapshot_max_size_mb";
    public const string TrainerStopWithGameExit = "trainer.stop_with_game_exit";
    public const string TrainerHideOriginalWindowOnLaunch = "trainer.hide_original_window_on_launch";
    public const string TrainerStrictVanguardMode = "trainer.strict_vanguard_mode";
    public const string TrainerIncludeFlingArchive = "trainer.include_fling_archive";
    public const string TrainerDefaultScanCodeMode = "trainer.default_scancode_mode";

    // M4 Save Time Machine & Cloud Sync settings
    public const string SaveMachineId = "saves.machine_id";
    public const string SaveMachineDisplayName = "saves.machine_display_name";
    public const string SaveAutoWatchByDefault = "saves.auto_watch_default";
    public const string SavePostExitSnapshotByDefault = "saves.post_exit_default";
    public const string SaveQuiescentDebounceMs = "saves.quiescent_debounce_ms";
    public const string SaveDefaultMaxRollingSnapshots = "saves.default_max_rolling_snapshots";
    public const string SaveDefaultMaxSnapshotSizeMb = "saves.default_max_snapshot_size_mb";
    public const string SaveIncludeConfigFilesByDefault = "saves.include_config_default";
    public const string SaveGcGracePeriodHours = "saves.gc_grace_period_hours";

    // Steam client discovery & Launch-via-Steam settings
    public const string SteamInstallDirectoryOverride = "launcher.steam_install_dir_override";
    public const string SteamTargetAccountId = "launcher.steam_target_account_id";
    public const string SteamSuppressDuplicateAchievementToast = "launcher.steam_suppress_duplicate_toast";
    public const string LauncherLaunchModePrefix = "launcher.launch_mode.";
    public const string LauncherSteamUseAppIdNamePrefix = "launcher.steam_use_appid_name.";
    public const string LauncherSteamShortcutAppIdPrefix = "launcher.steam_shortcut_appid.";

    public static string GetGameLaunchModeKey(string gameId) => $"{LauncherLaunchModePrefix}{gameId}";
    public static string GetGameSteamUseAppIdNameKey(string gameId) => $"{LauncherSteamUseAppIdNamePrefix}{gameId}";
    public static string GetGameSteamShortcutAppIdKey(string gameId) => $"{LauncherSteamShortcutAppIdPrefix}{gameId}";
}
