namespace Spawn.Contracts;

/// <summary>
/// Origin / trigger that created a Save Time Machine snapshot node in the branching DAG.
/// </summary>
public enum SaveSnapshotTriggerKind
{
    Manual = 0,
    ManualPin = 1,
    AutoQuiescent = 2,
    PostExit = 3,
    PreRollbackSafety = 4,
    PreBranchSwitchSafety = 5,
    PreTrainerImport = 6,
    CloudPull = 7,
    CloudDivergenceBackup = 8,
    BranchFork = 9,
}

/// <summary>
/// Logical classification of a file or registry export inside a Save Time Machine snapshot.
/// </summary>
public enum SaveFileCategory
{
    Save = 0,
    Config = 1,
    RegistryExport = 2,
}

/// <summary>
/// Change classification between two snapshots or between a snapshot and current disk state.
/// </summary>
public enum SaveFileDiffKind
{
    Unchanged = 0,
    Added = 1,
    Modified = 2,
    Deleted = 3,
}

/// <summary>
/// Policy governing how rollback reacts when the snapshot's <c>ModProfileHash</c> differs from the current active profile.
/// </summary>
public enum SaveModMismatchPolicy
{
    AbortAndPrompt = 0,
    ProceedAnyway = 1,
}

/// <summary>
/// Supported decentralized cloud sync storage backends for 32MB SPPK repositories.
/// </summary>
public enum CloudSyncProviderType
{
    LocalFolder = 0,
    WebDav = 1,
    S3 = 2,
}

/// <summary>
/// Three-way lineage comparison outcome between local HEAD and remote machine heads.
/// </summary>
public enum CloudSyncRelation
{
    NotConfigured = 0,
    UpToDate = 1,
    FastForwardPush = 2,
    FastForwardPull = 3,
    Diverged = 4,
    Error = 5,
}

/// <summary>
/// User-selected non-destructive resolution strategy when local and remote save histories diverge.
/// </summary>
public enum CloudDivergenceResolution
{
    /// <summary>Preserves current local HEAD on a pinned backup branch and adopts remote HEAD onto the active branch.</summary>
    AdoptRemoteAndBackupLocal = 0,

    /// <summary>Preserves remote HEAD on a pinned backup branch and pushes local HEAD as the new synced tip.</summary>
    KeepLocalAndBackupRemote = 1,
}

/// <summary>
/// Runtime state of the per-game quiescent anti-tear save file watcher.
/// </summary>
public enum SaveWatcherStatusKind
{
    Stopped = 0,
    Watching = 1,
    Debouncing = 2,
    Capturing = 3,
    Error = 4,
}

/// <summary>
/// Reason why a Save Time Machine snapshot capture was skipped non-destructively.
/// </summary>
public enum SaveSnapshotSkipReason
{
    None = 0,
    NoSaveLocationsFound = 1,
    UnchangedFromHead = 2,
    SizeThresholdExceeded = 3,
    FileLockedSharingViolation = 4,
    ConcurrentModificationDetected = 5,
    DisabledBySettings = 6,
}
