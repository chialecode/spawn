using System.Text.Json.Serialization;

namespace Spawn.Contracts;

/// <summary>
/// Curated branch lane palette matching SPAWN's Light/Dark theme tokens (<c>SaveBranchLane0Brush</c>..7).
/// </summary>
public static class SaveBranchColors
{
    public const string DefaultMainColorHex = "#1677FF";

    public static IReadOnlyList<string> Palette { get; } =
    [
        "#1677FF",
        "#10B981",
        "#8B5CF6",
        "#F59E0B",
        "#EC4899",
        "#06B6D4",
        "#EF4444",
        "#6366F1",
    ];

    public static string PickForIndex(int index) =>
        Palette[Math.Abs(index) % Palette.Count];
}

/// <summary>
/// Named save branch in a game's Git-like branching save tree (<c>save_branches</c>).
/// </summary>
public sealed record SaveBranchRecord
{
    public required string BranchId { get; init; }
    public required string GameId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string ColorHex { get; init; } = SaveBranchColors.DefaultMainColorHex;
    public string? HeadSnapshotId { get; init; }
    public string? BaseSnapshotId { get; init; }
    public bool IsDefault { get; init; }
    public bool IsArchived { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Projected count of snapshots directly belonging to this branch.</summary>
    public int SnapshotCount { get; init; }
}

/// <summary>
/// Per-game active branch pointer, HEAD / detached-HEAD state, auto-backup toggles, and cloud sync binding (<c>save_game_state</c>).
/// </summary>
public sealed record SaveGameStateRecord
{
    public required string GameId { get; init; }
    public string? ActiveBranchId { get; init; }
    public string? CurrentHeadSnapshotId { get; init; }
    public bool AutoWatchEnabled { get; init; } = true;
    public bool PostExitSnapshotEnabled { get; init; } = true;
    public bool IncludeConfigFiles { get; init; }
    public int MaxRollingSnapshots { get; init; } = SaveSettingsDto.DefaultRollingRetentionCount;
    public string? CloudEndpointId { get; init; }
    public DateTimeOffset? LastSyncedAtUtc { get; init; }
    public bool HasUnresolvedCloudConflict { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Single immutable save snapshot node in a game's branching DAG (<c>save_snapshots</c>).
/// </summary>
public sealed record SaveSnapshotRecord
{
    public required string SnapshotId { get; init; }
    public required string GameId { get; init; }
    public required string BranchId { get; init; }
    public string? ParentSnapshotId { get; init; }
    public string? MergeParentSnapshotId { get; init; }
    public string? ProfileId { get; init; }
    public string ModProfileHash { get; init; } = string.Empty;
    public SaveSnapshotTriggerKind TriggerKind { get; init; } = SaveSnapshotTriggerKind.Manual;
    public string? Note { get; init; }
    public int FileCount { get; init; }
    public int RegistryKeyCount { get; init; }
    public long TotalRawBytes { get; init; }
    public long TotalStoredBytes { get; init; }
    public required string CreatedMachineId { get; init; }
    public long PlaytimeActiveSeconds { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsPinned { get; init; }
    public string? LegacyPreTrainerSnapshotId { get; init; }

    [JsonIgnore]
    public string ShortId =>
        SnapshotId.Length >= 8
            ? SnapshotId[^8..].ToLowerInvariant()
            : SnapshotId.ToLowerInvariant();
}

/// <summary>
/// Single logical file or HKCU registry export captured inside a snapshot (<c>snapshot_files</c>).
/// </summary>
public sealed record SaveSnapshotFileRecord
{
    public required string SnapshotId { get; init; }

    /// <summary>Placeholder-normalized logical path (e.g., <c>&lt;winAppData&gt;/Game/slot0.sav</c> or <c>registry://HKCU/...</c>).</summary>
    public required string LogicalPath { get; init; }

    /// <summary>Original machine-local absolute path (or canonical <c>HKCU\...</c> key) at capture time.</summary>
    public required string OriginalPath { get; init; }

    public SaveFileCategory Category { get; init; } = SaveFileCategory.Save;
    public long FileSize { get; init; }
    public DateTimeOffset LastWriteTimeUtc { get; init; }
    public required string FileBlake3 { get; init; }
    public IReadOnlyList<string> ChunkHashes { get; init; } = [];
}

/// <summary>
/// Content-addressed deduplicated chunk record persisted in SQLite (<c>cas_chunks</c>).
/// </summary>
public sealed record CasChunkRecord
{
    public required string ChunkBlake3 { get; init; }
    public long RawSizeBytes { get; init; }
    public long CompressedSizeBytes { get; init; }
    public int RefCount { get; init; } = 1;
    public string? PackId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUnreferencedAtUtc { get; init; }
}

/// <summary>
/// Aggregate storage &amp; deduplication metrics for the local CAS chunk vault.
/// </summary>
public sealed record CasVaultStatsDto
{
    public int TotalChunkCount { get; init; }
    public int ReferencedChunkCount { get; init; }
    public int UnreferencedChunkCount { get; init; }
    public long TotalRawBytes { get; init; }
    public long TotalCompressedBytes { get; init; }
    public long TotalSnapshotRawBytes { get; init; }

    [JsonIgnore]
    public long DeduplicationSavedBytes => Math.Max(0, TotalSnapshotRawBytes - TotalCompressedBytes);

    [JsonIgnore]
    public double DeduplicationRatio =>
        TotalSnapshotRawBytes <= 0
            ? 0.0
            : Math.Clamp((double)DeduplicationSavedBytes / TotalSnapshotRawBytes, 0.0, 1.0);
}

/// <summary>
/// Precompiled Ludusavi manifest rule persisted in SQLite (<c>ludusavi_rules</c>).
/// </summary>
public sealed record LudusaviRuleRecord
{
    public required string RuleName { get; init; }
    public int? SteamAppId { get; init; }
    public string? GogId { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<string> SavePaths { get; init; } = [];
    public IReadOnlyList<string> ConfigPaths { get; init; } = [];
    public IReadOnlyList<string> RegistryRules { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Single expanded save/config/registry target discovered for a game.
/// </summary>
public sealed record ResolvedSaveLocation
{
    public required string LogicalTemplatePath { get; init; }
    public required string ResolvedFullPath { get; init; }
    public SaveFileCategory Category { get; init; } = SaveFileCategory.Save;
    public GameDataSourceRule SourceRule { get; init; } = GameDataSourceRule.Ludusavi;
    public string SourceLabel { get; init; } = "Ludusavi";
    public bool Exists { get; init; }
    public bool IsSafe { get; init; } = true;
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Complete resolved save layout for a game with strict <c>[save]</c>, <c>[config]</c>, and <c>HKCU</c> separation.
/// </summary>
public sealed record ResolvedSaveLayout
{
    public required string GameId { get; init; }
    public string? MatchedRuleName { get; init; }
    public IReadOnlyList<ResolvedSaveLocation> Locations { get; init; } = [];
    public IReadOnlyList<ResolvedSaveLocation> SaveLocations { get; init; } = [];
    public IReadOnlyList<ResolvedSaveLocation> ConfigLocations { get; init; } = [];
    public IReadOnlyList<ResolvedSaveLocation> RegistryKeys { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Positioned DAG node in a game's branching save tree for UI rendering and traversal.
/// </summary>
public sealed record SaveTreeNodeDto
{
    public required SaveSnapshotRecord Snapshot { get; init; }
    public required string BranchName { get; init; }
    public string BranchColorHex { get; init; } = SaveBranchColors.DefaultMainColorHex;
    public int LaneIndex { get; init; }
    public int RowIndex { get; init; }
    public string? ParentSnapshotId { get; init; }
    public string? MergeParentSnapshotId { get; init; }
    public IReadOnlyList<string> ChildSnapshotIds { get; init; } = [];
    public bool IsCurrentHead { get; init; }
    public bool IsBranchTip { get; init; }
    public IReadOnlyList<string> BranchTipNames { get; init; } = [];
    public bool IsDetachedHead { get; init; }
    public bool IsCloudSynced { get; init; }
}

/// <summary>
/// Directed parent-child or merge edge in the save tree DAG.
/// </summary>
public sealed record SaveTreeEdgeDto
{
    public required string FromSnapshotId { get; init; }
    public required string ToSnapshotId { get; init; }
    public int FromLane { get; init; }
    public int ToLane { get; init; }
    public int FromRow { get; init; }
    public int ToRow { get; init; }
    public string ColorHex { get; init; } = SaveBranchColors.DefaultMainColorHex;
    public bool IsForkEdge { get; init; }
    public bool IsMergeEdge { get; init; }
}

/// <summary>
/// Complete branching save tree graph query result for a single game.
/// </summary>
public sealed record SaveTreeGraphDto
{
    public required string GameId { get; init; }
    public string? ActiveBranchId { get; init; }
    public string ActiveBranchName { get; init; } = "main";
    public string? CurrentHeadSnapshotId { get; init; }
    public bool IsDetachedHead { get; init; }
    public IReadOnlyList<SaveBranchRecord> Branches { get; init; } = [];
    public IReadOnlyList<SaveTreeNodeDto> Nodes { get; init; } = [];
    public IReadOnlyList<SaveTreeEdgeDto> Edges { get; init; } = [];
    public long TotalRawBytes { get; init; }
    public long TotalDeduplicatedBytes { get; init; }
    public bool HasUnresolvedCloudConflict { get; init; }
    public DateTimeOffset? LastSyncedAtUtc { get; init; }

    [JsonIgnore]
    public double DeduplicationRatio =>
        TotalRawBytes <= 0
            ? 0.0
            : Math.Clamp((double)Math.Max(0, TotalRawBytes - TotalDeduplicatedBytes) / TotalRawBytes, 0.0, 1.0);
}

/// <summary>
/// Per-file diff line comparing two snapshots or a snapshot against live disk files.
/// </summary>
public sealed record SaveFileDiffItemDto
{
    public required string LogicalPath { get; init; }
    public SaveFileCategory Category { get; init; } = SaveFileCategory.Save;
    public SaveFileDiffKind DiffKind { get; init; }
    public long LeftSize { get; init; }
    public long RightSize { get; init; }
    public string? LeftHash { get; init; }
    public string? RightHash { get; init; }

    [JsonIgnore]
    public long SizeDelta => RightSize - LeftSize;
}

/// <summary>
/// Aggregate diff report between two snapshots or a snapshot and current disk state.
/// </summary>
public sealed record SaveSnapshotDiffDto
{
    public required string BaseLabel { get; init; }
    public required string TargetLabel { get; init; }
    public IReadOnlyList<SaveFileDiffItemDto> Items { get; init; } = [];
    public int AddedCount { get; init; }
    public int ModifiedCount { get; init; }
    public int DeletedCount { get; init; }
    public int UnchangedCount { get; init; }
}

/// <summary>
/// Per-game summary row for the Column 1 Saves Hub sidebar and overview list.
/// </summary>
public sealed record SaveLibraryGameOverviewDto
{
    public required string GameId { get; init; }
    public required string GameTitle { get; init; }
    public string? IconPath { get; init; }
    public string? ActiveBranchId { get; init; }
    public string ActiveBranchName { get; init; } = "main";
    public int BranchCount { get; init; }
    public int SnapshotCount { get; init; }
    public int PinnedCount { get; init; }
    public long TotalRawBytes { get; init; }
    public long TotalStoredBytes { get; init; }
    public DateTimeOffset? LastSnapshotAtUtc { get; init; }
    public bool IsWatching { get; init; }
    public bool IsGameRunning { get; init; }
    public bool HasCloudEndpoint { get; init; }
    public string? CloudEndpointId { get; init; }
    public bool HasCloudConflict { get; init; }
    public DateTimeOffset? LastSyncedAtUtc { get; init; }
    public bool HasLegacyPreTrainerSnapshots { get; init; }
}

/// <summary>
/// Request to capture a new Save Time Machine snapshot.
/// </summary>
public sealed record CreateSaveSnapshotRequest
{
    public required string GameId { get; init; }
    public SaveSnapshotTriggerKind TriggerKind { get; init; } = SaveSnapshotTriggerKind.Manual;
    public string? BranchId { get; init; }
    public string? Note { get; init; }
    public bool? IncludeConfigFiles { get; init; }
    public bool Pin { get; init; }
    public bool SkipIfUnchangedFromHead { get; init; }
    public string? ExpectedParentSnapshotId { get; init; }
    public string? ModProfileHash { get; init; }
    public string? ProfileId { get; init; }
}

/// <summary>
/// Outcome of capturing a Save Time Machine snapshot.
/// </summary>
public sealed record CreateSaveSnapshotResult
{
    public required bool Succeeded { get; init; }
    public bool Skipped { get; init; }
    public SaveSnapshotSkipReason SkipReason { get; init; } = SaveSnapshotSkipReason.None;
    public required string Message { get; init; }
    public SaveSnapshotRecord? Snapshot { get; init; }
    public long DeduplicatedBytesSaved { get; init; }
    public int NewChunkCount { get; init; }
    public int ReusedChunkCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Request to fork a new named branch from any snapshot (or current HEAD when <see cref="FromSnapshotId"/> is null).
/// </summary>
public sealed record ForkBranchRequest
{
    public required string GameId { get; init; }
    public string? FromSnapshotId { get; init; }
    public required string BranchName { get; init; }
    public string? Description { get; init; }
    public string? ColorHex { get; init; }
    public bool SwitchToNewBranch { get; init; } = true;
    public bool CreateSafetySnapshotIfSwitching { get; init; } = true;
    public bool IncludeConfigFilesOnCheckout { get; init; }
}

/// <summary>
/// Outcome of creating/forking a named branch.
/// </summary>
public sealed record ForkBranchResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public SaveBranchRecord? Branch { get; init; }
    public SaveRestoreOutcome? CheckoutOutcome { get; init; }
}

/// <summary>
/// Request to switch active branch and atomically check out its HEAD snapshot to disk.
/// </summary>
public sealed record SwitchBranchRequest
{
    public required string GameId { get; init; }
    public required string TargetBranchId { get; init; }
    public bool CreateSafetySnapshot { get; init; } = true;
    public bool IncludeConfigFiles { get; init; }
    public SaveModMismatchPolicy ModMismatchPolicy { get; init; } = SaveModMismatchPolicy.AbortAndPrompt;
}

/// <summary>
/// Request to roll back disk saves and HKCU registry to a specific snapshot node.
/// </summary>
public sealed record RollbackSaveRequest
{
    public required string SnapshotId { get; init; }
    public bool IncludeConfigFiles { get; init; }
    public SaveModMismatchPolicy ModMismatchPolicy { get; init; } = SaveModMismatchPolicy.AbortAndPrompt;
    public bool CreateSafetySnapshot { get; init; } = true;
    public string? ForkNewBranchName { get; init; }
    public string? ForkNewBranchColorHex { get; init; }
}

/// <summary>
/// Outcome of an atomic save rollback or branch checkout operation.
/// </summary>
public sealed record SaveRestoreOutcome
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public bool RequiresModMismatchConfirmation { get; init; }
    public string? ExpectedModProfileHash { get; init; }
    public string? CurrentModProfileHash { get; init; }
    public SaveSnapshotRecord? SafetySnapshot { get; init; }
    public SaveSnapshotRecord? TargetSnapshot { get; init; }
    public string? ActiveBranchId { get; init; }
    public int RestoredFileCount { get; init; }
    public int RestoredRegistryKeyCount { get; init; }
    public int DeletedExtraneousFileCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Outcome of deleting a named branch.
/// </summary>
public sealed record DeleteBranchResult(
    bool Succeeded,
    string Message,
    int DeletedSnapshotCount = 0);

/// <summary>
/// Outcome of deleting an individual snapshot node.
/// </summary>
public sealed record DeleteSnapshotResult(
    bool Succeeded,
    string Message,
    int UnreferencedChunkCount = 0);

/// <summary>
/// Outcome of exporting a snapshot's files &amp; registry JSON to a user-selected directory.
/// </summary>
public sealed record ExportSnapshotResult(
    bool Succeeded,
    string Message,
    string? ExportDirectory = null,
    int ExportedFileCount = 0,
    int ExportedRegistryKeyCount = 0);

/// <summary>
/// Outcome of idempotently importing legacy M3 <c>pre_trainer_snapshots</c> into the branching save tree.
/// </summary>
public sealed record LegacyPreTrainerImportReport(
    int ImportedCount,
    int AlreadyImportedCount,
    int SkippedCount,
    IReadOnlyList<string> ImportedSnapshotIds,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Live status snapshot of a game's quiescent anti-tear watcher.
/// </summary>
public sealed record SaveWatcherStatusDto
{
    public required string GameId { get; init; }
    public SaveWatcherStatusKind Status { get; init; } = SaveWatcherStatusKind.Stopped;
    public IReadOnlyList<string> WatchedDirectories { get; init; } = [];
    public DateTimeOffset? LastEventUtc { get; init; }
    public string? LastSnapshotId { get; init; }
    public string? LastMessage { get; init; }
}

/// <summary>
/// Summary report from running CAS chunk garbage collection.
/// </summary>
public sealed record SaveGcReport(
    int DeletedChunkCount,
    long ReclaimedBytes,
    int RemainingChunkCount,
    long RemainingStoredBytes);

/// <summary>
/// Configured decentralized cloud sync endpoint persisted in <c>cloud_sync_endpoints</c>.
/// Secrets are never stored in SQLite; they live encrypted in <c>DpapiSecretStore</c> under <see cref="SecretCredentialId"/>.
/// </summary>
public sealed record CloudSyncEndpointRecord
{
    public required string EndpointId { get; init; }
    public required string DisplayName { get; init; }
    public CloudSyncProviderType ProviderType { get; init; } = CloudSyncProviderType.LocalFolder;
    public required string EndpointUrl { get; init; }
    public string BucketOrRoot { get; init; } = string.Empty;
    public string? Region { get; init; }
    public bool PathStyleAddressing { get; init; } = true;
    public string? SecretCredentialId { get; init; }
    public bool IsDefault { get; init; }
    public bool HasSecretConfigured { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Plaintext credentials passed exclusively during endpoint save/update and immediately protected via Windows CurrentUser DPAPI.
/// </summary>
public sealed record CloudSyncEndpointSecrets(
    string? UsernameOrAccessKey,
    string? PasswordOrSecretKey);

/// <summary>
/// Outcome of verifying connectivity and write/read access to a cloud sync endpoint.
/// </summary>
public sealed record CloudSyncEndpointTestResult(
    bool Succeeded,
    string Message,
    long LatencyMs = 0);

/// <summary>
/// Summary of a single remote machine's <c>heads/&lt;GameId&gt;/&lt;MachineId&gt;.json</c> pointer.
/// </summary>
public sealed record CloudRemoteHeadSummary
{
    public required string MachineId { get; init; }
    public string? MachineName { get; init; }
    public required string HeadSnapshotId { get; init; }
    public string? BranchName { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public long PlaytimeActiveSeconds { get; init; }
    public int FileCount { get; init; }
    public long TotalRawBytes { get; init; }
    public IReadOnlyList<string> AncestorIds { get; init; } = [];
}

/// <summary>
/// Dry-run cloud sync preview returned by <see cref="ICloudSaveSyncService.PlanSyncAsync"/>.
/// </summary>
public sealed record CloudSyncPlanPreview
{
    public required string GameId { get; init; }
    public string? EndpointId { get; init; }
    public CloudSyncRelation Relation { get; init; } = CloudSyncRelation.NotConfigured;
    public required string Message { get; init; }
    public SaveSnapshotRecord? LocalHeadSnapshot { get; init; }
    public CloudRemoteHeadSummary? RemoteHead { get; init; }
    public IReadOnlyList<CloudRemoteHeadSummary> AllRemoteHeads { get; init; } = [];
    public string? CommonAncestorSnapshotId { get; init; }
    public int MissingRemoteChunkCount { get; init; }
    public long EstimatedUploadBytes { get; init; }
    public int MissingLocalChunkCount { get; init; }
    public long EstimatedDownloadBytes { get; init; }
    public int SaveFileCount { get; init; }
}

/// <summary>
/// Streaming progress tick reported during cloud sync upload/download.
/// </summary>
public sealed record CloudSyncProgressTick(
    string Stage,
    int CompletedUnits,
    int TotalUnits,
    long BytesTransferred,
    string Message);

/// <summary>
/// Outcome of executing a decentralized 32MB SPPK cloud sync.
/// </summary>
public sealed record CloudSyncExecutionReport
{
    public required bool Succeeded { get; init; }
    public CloudSyncRelation Relation { get; init; }
    public required string Message { get; init; }
    public int UploadedPackCount { get; init; }
    public long UploadedBytes { get; init; }
    public int DownloadedChunkCount { get; init; }
    public long DownloadedBytes { get; init; }
    public string? ResultHeadSnapshotId { get; init; }
    public string? DivergenceBackupBranchId { get; init; }
    public string? DivergenceBackupBranchName { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Global user preferences for Save Time Machine, anti-tear watcher, CAS GC, and machine identity (<c>app_settings</c>).
/// </summary>
public sealed record SaveSettingsDto
{
    public const int DefaultQuiescentDebounceMs = 2500;
    public const int MinQuiescentDebounceMs = 500;
    public const int MaxQuiescentDebounceMs = 15000;
    public const int DefaultRollingRetentionCount = 30;
    public const int DefaultSnapshotMaxSizeMb = 512;
    public const int DefaultGcGracePeriodHours = 24;

    public bool AutoWatchByDefault { get; init; } = true;
    public bool PostExitSnapshotByDefault { get; init; } = true;
    public int QuiescentDebounceMs { get; init; } = DefaultQuiescentDebounceMs;
    public int DefaultMaxRollingSnapshots { get; init; } = DefaultRollingRetentionCount;
    public int DefaultMaxSnapshotSizeMb { get; init; } = DefaultSnapshotMaxSizeMb;
    public bool IncludeConfigFilesByDefault { get; init; }
    public int GcGracePeriodHours { get; init; } = DefaultGcGracePeriodHours;
    public string MachineId { get; init; } = string.Empty;
    public string MachineDisplayName { get; init; } = string.Empty;
}
