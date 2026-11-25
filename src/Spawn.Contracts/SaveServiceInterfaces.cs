namespace Spawn.Contracts;

/// <summary>
/// Resolves per-game save directories, <c>[config]</c> paths, and <c>HKCU</c> registry rules by combining
/// precompiled <c>ludusavi_rules</c> (16 Windows placeholders + globbing) with <see cref="IGameDataManagementService"/>.
/// </summary>
public interface ILudusaviRuleResolver
{
    Task<ResolvedSaveLayout> ResolveGameSaveLayoutAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken ct = default);

    /// <summary>
    /// Converts a machine-local absolute path (or <c>HKCU\...</c> key) into a portable placeholder-normalized logical path
    /// (e.g., <c>&lt;winAppData&gt;/Game/slot0.sav</c> or <c>registry://HKCU/Software/...</c>).
    /// </summary>
    string CanonicalizeToLogicalPath(string fullPathOrRegistryKey, GameRecord game);

    /// <summary>
    /// Expands a portable placeholder-normalized logical path back into a machine-local absolute path (or <c>HKCU\...</c> key).
    /// </summary>
    string ExpandLogicalPath(string logicalPath, GameRecord game);

    /// <summary>
    /// Ensures the built-in embedded Ludusavi rule seed has been loaded into SQLite <c>ludusavi_rules</c>.
    /// </summary>
    Task<int> EnsureBuiltInRulesSeededAsync(CancellationToken ct = default);

    /// <summary>
    /// Imports or refreshes precompiled Ludusavi rules from a JSON array payload.
    /// </summary>
    Task<int> ImportRulesJsonAsync(string rulesJson, CancellationToken ct = default);

    Task<int> GetRuleCountAsync(CancellationToken ct = default);
}

/// <summary>
/// Core Save Time Machine &amp; Git-like Branching Save Tree service (<c>REQ-PRG-01</c>, <c>REQ-PRG-02</c>).
/// </summary>
public interface ISaveTimeMachineService
{
    event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged;

    Task<SaveGameStateRecord> GetGameStateAsync(string gameId, CancellationToken ct = default);
    Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default);

    Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default);

    Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string gameId, CancellationToken ct = default);
    Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default);
    Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default);

    Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(string leftSnapshotId, string rightSnapshotId, CancellationToken ct = default);
    Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string snapshotId, CancellationToken ct = default);

    Task<CreateSaveSnapshotResult> CreateSnapshotAsync(CreateSaveSnapshotRequest request, CancellationToken ct = default);
    Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default);

    Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default);
    Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default);
    Task<SaveBranchRecord?> RenameBranchAsync(
        string branchId,
        string newName,
        string? description = null,
        string? colorHex = null,
        CancellationToken ct = default);
    Task<DeleteBranchResult> DeleteBranchAsync(string branchId, bool deleteSnapshots = false, CancellationToken ct = default);

    Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default);
    Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default);
    Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default);

    Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(string snapshotId, string targetDirectory, CancellationToken ct = default);
    Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(string gameId, CancellationToken ct = default);
    Task<SaveGcReport> RunCasGarbageCollectionAsync(TimeSpan? gracePeriodOverride = null, CancellationToken ct = default);
}

/// <summary>
/// Anti-tear quiescent file watcher &amp; post-game-exit automatic snapshot coordinator.
/// </summary>
public interface ISaveWatcherService : IAsyncDisposable
{
    event EventHandler<SaveWatcherStatusDto>? WatcherStatusChanged;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task StartWatchingGameAsync(string gameId, CancellationToken ct = default);
    Task StopWatchingGameAsync(string gameId, CancellationToken ct = default);
    SaveWatcherStatusDto GetStatus(string gameId);
    IReadOnlyList<SaveWatcherStatusDto> GetAllStatuses();
}

/// <summary>
/// Decentralized 32MB SPPK Packfile cloud sync service supporting LocalFolder, WebDAV, and S3/R2
/// with 64-generation lineage checking and non-destructive divergence branch preservation (<c>REQ-PRG-03</c>).
/// </summary>
public interface ICloudSaveSyncService
{
    Task<IReadOnlyList<CloudSyncEndpointRecord>> ListEndpointsAsync(CancellationToken ct = default);
    Task<CloudSyncEndpointRecord> SaveEndpointAsync(
        CloudSyncEndpointRecord endpoint,
        CloudSyncEndpointSecrets? secrets = null,
        CancellationToken ct = default);
    Task<bool> DeleteEndpointAsync(string endpointId, CancellationToken ct = default);
    Task<CloudSyncEndpointTestResult> TestEndpointConnectionAsync(string endpointId, CancellationToken ct = default);

    Task<CloudSyncPlanPreview> PlanSyncAsync(string gameId, string? endpointId = null, CancellationToken ct = default);
    Task<CloudSyncExecutionReport> ExecuteSyncAsync(
        string gameId,
        string? endpointId = null,
        CloudDivergenceResolution? divergenceResolution = null,
        IProgress<CloudSyncProgressTick>? progress = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<CloudSyncExecutionReport>> SyncAllConfiguredGamesAsync(
        IProgress<CloudSyncProgressTick>? progress = null,
        CancellationToken ct = default);
}
