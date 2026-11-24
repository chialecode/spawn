using System.Text.Json.Serialization;

namespace Spawn.Contracts;

/// <summary>
/// Well-known <c>app_settings</c> keys used by the signed in-app auto-updater (<c>ADR-0007 §3.3</c>).
/// Stored in the existing <c>app_settings</c> table without requiring a SQLite schema version upgrade.
/// </summary>
public static class UpdateSettingKeys
{
    public const string AutoCheckEnabled = "update.auto_check_enabled";
    public const string AutoInstallWhenIdle = "update.auto_install_when_idle";
    public const string LastCheckUtc = "update.last_check_utc";
    public const string NextCheckUtc = "update.next_check_utc";
    public const string LastCheckOutcome = "update.last_check_outcome";
    public const string LastCheckMessage = "update.last_check_message";
    public const string HistoryJson = "update.history_json";
    public const string LastNotifiedFailureVersion = "update.last_notified_failure_version";
    public const string PendingUpdateJson = "update.pending_update_json";
}

/// <summary>
/// Distinguishes automatic scheduled update actions from explicit user-triggered actions.
/// </summary>
public enum UpdateTriggerKind
{
    Auto = 0,
    Manual = 1,
}

/// <summary>
/// High-level lifecycle state of the in-app release updater.
/// </summary>
public enum UpdateServiceState
{
    DisabledDevChannel = 0,
    Idle = 1,
    Checking = 2,
    Downloading = 3,
    ReadyToInstall = 4,
    Installing = 5,
    UpToDate = 6,
    DeferredOffline = 7,
    Error = 8,
}

/// <summary>
/// Outcome of an update check / download cycle.
/// </summary>
public enum UpdateCheckOutcome
{
    DisabledDevChannel = 0,
    UpToDate = 1,
    UpdateReady = 2,
    DeferredOffline = 3,
    VerificationFailed = 4,
    DownloadFailed = 5,
    InstallStarted = 6,
    InstallFailed = 7,
}

/// <summary>
/// Persisted result classification for each row in the bounded 20-entry update history log.
/// </summary>
public enum UpdateHistoryResult
{
    UpToDate = 0,
    Downloaded = 1,
    InstallLaunched = 2,
    Completed = 3,
    DeferredOffline = 4,
    Failed = 5,
}

/// <summary>
/// User-configurable release update preferences persisted in <c>app_settings</c>.
/// Defaults per <c>Q-002</c>: automatic checks ON, automatic silent install when idle &amp; tray-only ON.
/// </summary>
public sealed record UpdateSettingsDto
{
    public bool AutoCheckEnabled { get; init; } = true;
    public bool AutoInstallWhenIdle { get; init; } = true;
}

/// <summary>
/// Signed installer payload descriptor inside <c>update-windows-x64.json</c>.
/// </summary>
public sealed record UpdateManifestInstallerDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

/// <summary>
/// Frozen release update manifest schema (<c>update-windows-x64.json</c>, <c>schemaVersion == 1</c>).
/// </summary>
public sealed record UpdateManifestDto
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("installer")]
    public required UpdateManifestInstallerDto Installer { get; init; }

    [JsonPropertyName("publishedUtc")]
    public DateTimeOffset PublishedUtc { get; init; }

    [JsonPropertyName("notesUrl")]
    public string? NotesUrl { get; init; }
}

/// <summary>
/// Locally staged and cryptographically verified update installer ready for execution.
/// </summary>
public sealed record PreparedUpdateDto
{
    public required string Version { get; init; }
    public required string InstallerName { get; init; }
    public required string InstallerPath { get; init; }
    public long InstallerSize { get; init; }
    public required string ExpectedSha256 { get; init; }
    public DateTimeOffset PublishedUtc { get; init; }
    public string? NotesUrl { get; init; }
    public DateTimeOffset PreparedAtUtc { get; init; }
}

/// <summary>
/// Single persisted update activity entry (capped at the latest 20 entries in <c>app_settings</c>).
/// </summary>
public sealed record UpdateHistoryEntryDto
{
    public DateTimeOffset TimestampUtc { get; init; }
    public required string FromVersion { get; init; }
    public string? ToVersion { get; init; }
    public UpdateTriggerKind Trigger { get; init; }
    public UpdateHistoryResult Result { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Snapshot of active host work used to gate automatic silent installation when idle and tray-only.
/// </summary>
public sealed record UpdateIdleSnapshot
{
    public bool HasRunningTrackedGame { get; init; }
    public bool HasActiveTrainerSession { get; init; }
    public bool HasActiveSaveSnapshotOrRollback { get; init; }
    public bool HasActiveGameDataCleanup { get; init; }
    public bool IsMainWindowOpen { get; init; }

    [JsonIgnore]
    public bool IsIdle =>
        !HasRunningTrackedGame
        && !HasActiveTrainerSession
        && !HasActiveSaveSnapshotOrRollback
        && !HasActiveGameDataCleanup;

    [JsonIgnore]
    public bool CanAutoInstallSilently => IsIdle && !IsMainWindowOpen;
}

/// <summary>
/// Immutable UI-consumable snapshot of the release updater state.
/// </summary>
public sealed record UpdateStateSnapshot
{
    public bool IsUpdateSupported { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public UpdateServiceState State { get; init; }
    public UpdateSettingsDto Settings { get; init; } = new();
    public DateTimeOffset? LastCheckUtc { get; init; }
    public DateTimeOffset? NextCheckUtc { get; init; }
    public UpdateCheckOutcome? LastCheckOutcome { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public PreparedUpdateDto? ReadyUpdate { get; init; }
    public double? DownloadProgressPercent { get; init; }
    public IReadOnlyList<UpdateHistoryEntryDto> History { get; init; } = [];
}

/// <summary>
/// Result returned by <see cref="IUpdateService.CheckForUpdatesAsync"/>.
/// </summary>
public sealed record UpdateCheckResult
{
    public UpdateCheckOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public PreparedUpdateDto? ReadyUpdate { get; init; }
    public bool AutoInstallTriggered { get; init; }
    public bool ShouldNotifyUser { get; init; }
}

/// <summary>
/// Result returned by <see cref="IUpdateService.InstallReadyUpdateAsync"/> or <see cref="IUpdateService.TryAutoInstallIfEligibleAsync"/>.
/// </summary>
public sealed record UpdateInstallResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool ShouldNotifyUser { get; init; }
    public string? TargetVersion { get; init; }
}

/// <summary>
/// Non-intrusive notification event emitted when an update becomes ready or an install fails (deduplicated per version).
/// </summary>
public sealed record UpdateUserNotificationEvent(
    string Title,
    string Message,
    string? TargetVersion,
    bool IsError,
    bool IsUpdateReady);

/// <summary>
/// Evaluates whether SPAWN is idle and whether the main window is currently open.
/// </summary>
public interface IUpdateIdleEvaluator
{
    UpdateIdleSnapshot Evaluate();
}

/// <summary>
/// Launches the verified per-user NSIS installer silently via <c>ShellExecute</c> (zero UAC).
/// </summary>
public interface IUpdateInstallerLauncher
{
    bool LaunchSilentInstaller(string installerPath, string arguments, out string? errorMessage);
}

/// <summary>
/// Coordinates graceful shutdown preparation (stopping trainers/overlay/services, writing <c>reason="update"</c>
/// restart state, releasing single-instance mutex) while the verified installer stream remains read-locked,
/// followed by process exit once the installer launches.
/// </summary>
public interface IUpdateQuitCoordinator
{
    Task RequestQuitForUpdateAsync(string reason, CancellationToken cancellationToken = default);
    void CompleteDeferredShutdown();
    Task AbortDeferredShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Signed in-app auto-updater for the <c>release</c> channel (<c>ADR-0007 §3.3</c>).
/// Strictly disabled (zero HTTP traffic, zero scheduling) on <c>dev</c> and <c>source</c> builds.
/// </summary>
public interface IUpdateService : IAsyncDisposable, IDisposable
{
    UpdateStateSnapshot CurrentSnapshot { get; }

    event EventHandler<UpdateStateSnapshot>? StateChanged;
    event EventHandler<UpdateUserNotificationEvent>? UserNotificationRequested;

    Task InitializeAsync(
        bool launchedAfterUpdate = false,
        bool consumedValidUpdateMarker = false,
        string? previousVersionFromMarker = null,
        CancellationToken cancellationToken = default);

    void StartBackgroundScheduler();
    Task StopBackgroundSchedulerAsync();

    Task<UpdateSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(UpdateSettingsDto settings, CancellationToken cancellationToken = default);

    Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateTriggerKind trigger,
        CancellationToken cancellationToken = default);

    Task<UpdateInstallResult> TryAutoInstallIfEligibleAsync(CancellationToken cancellationToken = default);

    Task<UpdateInstallResult> InstallReadyUpdateAsync(
        UpdateTriggerKind trigger = UpdateTriggerKind.Manual,
        CancellationToken cancellationToken = default);
}
