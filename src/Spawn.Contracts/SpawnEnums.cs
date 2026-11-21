namespace Spawn.Contracts;

/// <summary>
/// Source ecosystem or package flavor for an imported game.
/// </summary>
public enum PlatformSource
{
    Standalone = 0,
    GogOffline = 1,
    Steamrip = 2,
    SteamLocal = 3,
}

/// <summary>
/// Four-tier anti-cheat risk classification enforced across all SPAWN modules.
/// </summary>
public enum AntiCheatTier
{
    /// <summary>Pure offline single-player title. Full companion capabilities permitted.</summary>
    Tier0 = 0,

    /// <summary>Online-authenticated single-player or gacha client. Trainers locked.</summary>
    Tier1 = 1,

    /// <summary>User-mode or light kernel anti-cheat (EAC, BattlEye, VAC). Explorer-decoupled launch + 15s cooldown.</summary>
    Tier2 = 2,

    /// <summary>Ring-0 kernel anti-cheat (ACE/SGuard64, Vanguard, Ricochet). Strict lockdown + daemon exit watch + 15s cooldown.</summary>
    Tier3 = 3,
}

/// <summary>
/// Current state of the global Anti-Cheat Guard state machine.
/// </summary>
public enum AntiCheatGuardPhase
{
    IdleSafe = 0,
    ProtectedRunning = 1,
    ResidualGuardWatch = 2,
    CooldownLocked = 3,
}

/// <summary>
/// Active or detected achievement capture channel for a game.
/// </summary>
public enum AchievementCaptureMode
{
    None = 0,
    SteamEmulator = 1,
    SteamLocalCache = 2,
    GogCometLocal = 3,
    GogNativeSave = 4,
    PendingGbeForkActivation = 5,
    GbeForkActivated = 6,
}

/// <summary>
/// Origin of localized achievement metadata (schema, descriptions, icons).
/// </summary>
public enum AchievementSchemaSource
{
    None = 0,
    LocalEmulatorSchema = 1,
    SteamWebApi = 2,
    SteamLocalBin = 3,
    GogPublicApi = 4,
    CommunityMirror = 5,
    BuiltInRule = 6,
}

/// <summary>
/// Tri-state playtime and session lifecycle activity state.
/// </summary>
public enum PlaySessionActivityState
{
    Stopped = 0,
    Launching = 1,
    Active = 2,
    Background = 3,
    Afk = 4,
    ShellBreakingWatch = 5,
    Stopping = 6,
}

/// <summary>
/// Visual theme for in-game achievement unlock overlay toasts.
/// </summary>
public enum OverlayThemeKind
{
    Steam = 0,
    Ps5Trophy = 1,
    XboxRare = 2,
}

/// <summary>
/// Screen corner / edge anchor for in-game overlay notifications.
/// </summary>
public enum OverlayPositionKind
{
    BottomRight = 0,
    TopRight = 1,
    BottomLeft = 2,
    TopLeft = 3,
    TopCenter = 4,
    BottomCenter = 5,
}

/// <summary>
/// Desktop host color theme preference.
/// </summary>
public enum SpawnUiThemeKind
{
    Light = 0,
    Dark = 1,
}

/// <summary>
/// Audio preset for in-game achievement unlock overlay notifications.
/// </summary>
public enum OverlaySoundPreset
{
    /// <summary>Default: uses the local official Steam client desktop achievement toast <c>desktop_toast_default.wav</c> (gamepad UI <c>deck_ui_achievement_toast.wav</c> as secondary), falling back to synthesized chime if unavailable.</summary>
    SteamAuthentic = 0,

    /// <summary>Built-in multi-voice synthesized chord chime tailored per theme (Steam / PS5 / Xbox).</summary>
    ThemeSynthesized = 1,

    /// <summary>User-supplied custom <c>.wav</c> audio file.</summary>
    CustomWav = 2,
}
