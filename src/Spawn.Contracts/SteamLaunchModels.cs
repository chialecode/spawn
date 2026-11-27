using System.Globalization;
using System.Text.Json.Serialization;

namespace Spawn.Contracts;

/// <summary>
/// Per-game launch mode determining whether SPAWN spawns the game executable directly
/// or delegates startup through the local Steam client (enabling Steam Input controller mapping).
/// </summary>
public enum GameLaunchMode
{
    Direct = 0,
    Steam = 1,
}

/// <summary>
/// Distinguishes launching an official Steam library title from launching a non-Steam game
/// registered in Steam's <c>shortcuts.vdf</c>.
/// </summary>
public enum SteamLaunchTargetKind
{
    OfficialSteamApp = 0,
    NonSteamShortcut = 1,
}

/// <summary>
/// Indicates how the local Steam installation directory was resolved.
/// </summary>
public enum SteamPathDiscoverySource
{
    NotFound = 0,
    UserOverride = 1,
    RegistryCurrentUser = 2,
    RegistryLocalMachine = 3,
    DefaultDirectory = 4,
}

/// <summary>
/// Metadata for a discovered local Steam account under <c>config\loginusers.vdf</c> and <c>userdata\&lt;accountId&gt;</c>.
/// </summary>
public sealed record SteamAccountInfo
{
    public required uint AccountId { get; init; }
    public ulong SteamId64 { get; init; }
    public string? AccountName { get; init; }
    public string? PersonaName { get; init; }
    public bool IsMostRecent { get; init; }
    public bool IsActiveProcessUser { get; init; }
    public long? LastTimestamp { get; init; }
    public string? ShortcutsVdfPath { get; init; }

    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            string idText = AccountId.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(PersonaName))
            {
                return !string.IsNullOrWhiteSpace(AccountName) && !string.Equals(PersonaName, AccountName, StringComparison.OrdinalIgnoreCase)
                    ? $"{PersonaName} ({AccountName} · {idText})"
                    : $"{PersonaName} ({idText})";
            }

            if (!string.IsNullOrWhiteSpace(AccountName))
            {
                return $"{AccountName} ({idText})";
            }

            return $"Steam 账号 {idText}";
        }
    }
}

/// <summary>
/// Consolidated result of probing the local Steam client installation, running state, and accounts.
/// </summary>
public sealed record SteamClientDiscoveryResult
{
    public bool IsDetected { get; init; }
    public string? InstallDirectory { get; init; }
    public string? SteamExecutablePath { get; init; }
    public SteamPathDiscoverySource Source { get; init; } = SteamPathDiscoverySource.NotFound;
    public bool IsUserOverride { get; init; }
    public bool IsSteamRunning { get; init; }
    public int? SteamProcessId { get; init; }
    public uint? ActiveAccountId { get; init; }
    public IReadOnlyList<SteamAccountInfo> Accounts { get; init; } = [];
    public string? WarningMessage { get; init; }
}

/// <summary>
/// Per-game Steam launch preferences persisted in <c>app_settings</c>.
/// </summary>
public sealed record GameSteamLaunchSettings
{
    public GameLaunchMode LaunchMode { get; init; } = GameLaunchMode.Direct;
    public bool UseSteamAppIdAsShortcutName { get; init; }
    public int? RegisteredShortcutAppId { get; init; }
}

/// <summary>
/// Pre-flight evaluation outcome before launching a game through Steam.
/// </summary>
public enum SteamLaunchPreparationStatus
{
    ReadyOfficialSteam = 0,
    ReadyNonSteamShortcut = 1,
    NeedsShortcutWriteSteamClosed = 2,
    NeedsSteamRestartToSync = 3,
    BlockedTierAntiCheat = 4,
    BlockedSteamNotFound = 5,
    BlockedNoSteamAccount = 6,
    BlockedInvalidExecutable = 7,
}

/// <summary>
/// Category of non-blocking compatibility warning surfaced before launching a non-Steam game through Steam.
/// </summary>
public enum SteamLaunchWarningKind
{
    OfflineEmulatorGbeFork = 0,
    OfflineEmulatorOnlineFix = 1,
    OfflineEmulatorGeneric = 2,
    ElevatedExecutableUipi = 3,
}

/// <summary>
/// User-facing compatibility notice detected during Steam launch preparation.
/// </summary>
public sealed record SteamLaunchWarning(
    SteamLaunchWarningKind Kind,
    string Message);

/// <summary>
/// Read-only pre-flight result produced by <see cref="ISteamShortcutService.PrepareLaunchAsync"/>.
/// </summary>
public sealed record SteamLaunchPreparationResult
{
    public required SteamLaunchPreparationStatus Status { get; init; }
    public SteamLaunchTargetKind? TargetKind { get; init; }
    public string? LaunchUri { get; init; }
    public uint? TargetAccountId { get; init; }
    public string? ShortcutsVdfPath { get; init; }
    public int? ShortcutAppId { get; init; }
    public string? ShortcutAppName { get; init; }
    public IReadOnlyList<SteamLaunchWarning> Warnings { get; init; } = [];
    public string? ErrorMessage { get; init; }

    [JsonIgnore]
    public bool IsBlocked => Status is SteamLaunchPreparationStatus.BlockedTierAntiCheat
        or SteamLaunchPreparationStatus.BlockedSteamNotFound
        or SteamLaunchPreparationStatus.BlockedNoSteamAccount
        or SteamLaunchPreparationStatus.BlockedInvalidExecutable;
}

/// <summary>
/// Result of synchronizing a non-Steam shortcut into <c>shortcuts.vdf</c> (with optional graceful Steam restart).
/// </summary>
public sealed record SteamShortcutSyncResult
{
    public required bool Succeeded { get; init; }
    public string? LaunchUri { get; init; }
    public int? ShortcutAppId { get; init; }
    public bool RestartedSteam { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Optional per-invocation execution overrides passed to <see cref="IGameLaunchService.LaunchGameAsync(string, GameLaunchExecutionOptions, CancellationToken)"/>.
/// </summary>
public sealed record GameLaunchExecutionOptions
{
    public GameLaunchMode? LaunchModeOverride { get; init; }
    public string? SteamLaunchUri { get; init; }
    public bool SuppressDuplicateAchievementToast { get; init; }
    public bool? SteamWasRunningBeforeLaunch { get; init; }
}
