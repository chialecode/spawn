namespace Spawn.Contracts;

/// <summary>
/// Source provider that supplied an imported external trainer package.
/// </summary>
public enum TrainerProviderKind
{
    FlingMain = 0,
    FlingArchive = 1,
    LocalImport = 2,
}

/// <summary>
/// Origin of the canonical English game title cached in <c>trainer_title_map</c>.
/// </summary>
public enum TrainerTitleSource
{
    Unknown = 0,
    AcfName = 1,
    SteamAppDetails = 2,
    GogInfo = 3,
    Cleaner = 4,
    User = 5,
}

/// <summary>
/// Hotkey schema extraction mode for a managed trainer entry.
/// </summary>
public enum TrainerParseMode
{
    /// <summary>Structured hotkeys extracted from source DOM or user-defined bindings.</summary>
    Structured = 0,

    /// <summary>Unparsed or non-FLiNG trainer hosted in sandbox with mute, lifecycle coupling, and optional manual hotkeys.</summary>
    FreeHosted = 1,
}

/// <summary>
/// Interaction style of a single trainer hotkey row.
/// </summary>
public enum TrainerHotkeyEntryKind
{
    Toggle = 0,
    Trigger = 1,
}

/// <summary>
/// Non-blocking confidence tier comparing a trainer's declared version string against local game fingerprints.
/// </summary>
public enum TrainerVersionConfidence
{
    /// <summary>Version range or build aligns with local game metadata.</summary>
    RangeMatched = 0,

    /// <summary>Open-ended or date-based trainer version; verify against update date.</summary>
    CheckByDate = 1,

    /// <summary>Local game appears newer than the trainer release; may be outdated.</summary>
    PossiblyOutdated = 2,
}

/// <summary>
/// Lifecycle state machine of the single-player trainer manager for a game.
/// </summary>
public enum TrainerRuntimeState
{
    Unbound = 0,
    Matching = 1,
    Downloading = 2,
    FallbackBrowser = 3,
    Importing = 4,
    Verifying = 5,
    Ready = 6,
    Quarantined = 7,
    PreFlight = 8,
    Snapshotting = 9,
    Launching = 10,
    Running = 11,
    Stopping = 12,
    EmergencyStopped = 13,
}

/// <summary>
/// Zero-injection audio control policy applied to external trainer processes (D-04, D-28).
/// </summary>
public enum TrainerAudioMode
{
    /// <summary>Default (D-28): completely mute all trainer audio sessions via A0 pre-write + WASAPI SetMute(true).</summary>
    FullMute = 0,

    /// <summary>Experimental: keep sound effects at <see cref="TrainerAudioPolicy.CueVolume"/> while relying on A0 to silence BGM.</summary>
    KeepCuesExperimental = 1,

    /// <summary>Do not alter trainer audio volume or mute state.</summary>
    Passthrough = 2,
}

/// <summary>
/// Tiered on-demand elevation level used to launch <c>Spawn.TrainerWorker</c> (D-11 / ADR-0006).
/// </summary>
public enum TrainerElevationLevel
{
    /// <summary>Zero UAC: Medium IL direct spawn via <c>CreateProcessW</c>.</summary>
    Level1MediumIl = 1,

    /// <summary>One UAC prompt per session: High IL spawn via <c>ShellExecuteExW(runas)</c>.</summary>
    Level2HighIlUac = 2,
}

/// <summary>
/// Cause of stopping or destroying a trainer session / worker.
/// </summary>
public enum TrainerShutdownReason
{
    UserRequested = 0,
    GameExited = 1,
    TrainerExitedNaturally = 2,
    ProtectedGamePreFlightPurge = 3,
    AntiCheatWatchdogEmergency = 4,
    HostShutdownOrUpdate = 5,
    PipeDisconnected = 6,
    IntegrityOrSecurityViolation = 7,
}

/// <summary>
/// Categorized failure reason for trainer download or local import pipelines.
/// </summary>
public enum TrainerImportFailureKind
{
    None = 0,
    NetworkError = 1,
    ChallengeOrHtmlFallback = 2,
    InvalidMagicOrFormat = 3,
    ZipSlipRejected = 4,
    SizeLimitExceeded = 5,
    AntivirusQuarantined = 6,
    SmartAppControlBlocked = 7,
    MultipleExecutablesSelectionRequired = 8,
    IntegrityHashMismatch = 9,
    UnsupportedArchiveFormat = 10,
    Cancelled = 11,
}

/// <summary>
/// Categorized failure reason for launching a managed trainer session.
/// </summary>
public enum TrainerLaunchFailureKind
{
    None = 0,
    TrainerNotFound = 1,
    SafetyGateBlocked = 2,
    IntegrityHashMismatch = 3,
    AntivirusQuarantined = 4,
    SmartAppControlBlocked = 5,
    UacCancelledByUser = 6,
    WorkerBinaryMissing = 7,
    WorkerHandshakeFailed = 8,
    WorkerLaunchFailed = 9,
    AlreadyRunning = 10,
}

/// <summary>
/// Read-only Authenticode signature status of an imported trainer binary (informational badge only).
/// </summary>
public enum TrainerAuthenticodeStatus
{
    Unknown = 0,
    Unsigned = 1,
    ValidSigned = 2,
    InvalidOrUntrustedSignature = 3,
}

/// <summary>
/// Unified outcome from <see cref="ITrainerSafetyGate"/> deciding whether trainer operations are permitted.
/// </summary>
public enum TrainerAllowanceOutcome
{
    Allowed = 0,
    BlockedByTier = 1,
    BlockedByGuardPhase = 2,
    BlockedByLiveAntiCheat = 3,
    RequiresOfflineConfirmation = 4,
    VanguardIdleWarning = 5,
    BlockedByVanguardStrictMode = 6,
}

/// <summary>
/// Trigger classification for lightweight pre-trainer save snapshots.
/// </summary>
public enum PreTrainerSnapshotTriggerKind
{
    PreTrainer = 0,
    PreRestoreSafety = 1,
}

/// <summary>
/// Execution status recorded in <c>pre_trainer_snapshots</c>.
/// </summary>
public enum PreTrainerSnapshotStatus
{
    Completed = 0,
    Skipped = 1,
    Failed = 2,
}

/// <summary>
/// Non-blocking reason a pre-trainer save snapshot was skipped.
/// </summary>
public enum PreTrainerSnapshotSkipReason
{
    None = 0,
    DisabledBySettings = 1,
    NoSaveLocationsFound = 2,
    SizeThresholdExceeded = 3,
    FileLockedSharingViolation = 4,
    ConcurrentModificationDetected = 5,
    SafetyFenceBlocked = 6,
    Timeout = 7,
}

/// <summary>
/// Result status of inspecting or requesting a Windows Defender single-folder sandbox exclusion.
/// </summary>
public enum DefenderExclusionStatus
{
    Unknown = 0,
    AlreadyExcluded = 1,
    AddedAndVerified = 2,
    TamperProtectionBlocked = 3,
    ThirdPartyAntivirusPassiveMode = 4,
    SmartAppControlNotice = 5,
    UacCancelledByUser = 6,
    VerificationReadbackFailed = 7,
    Failed = 8,
}

/// <summary>
/// Modifier key bitmask for structured trainer hotkey chords.
/// </summary>
[Flags]
public enum TrainerHotkeyModifier
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}
