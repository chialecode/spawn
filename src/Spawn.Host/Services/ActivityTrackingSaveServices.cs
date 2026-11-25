using Spawn.Contracts;
using Spawn.Core.Update;

namespace Spawn.Host.Services;

/// <summary>
/// Transparent decorator around <see cref="ISaveTimeMachineService"/> that marks active snapshot, rollback,
/// branch-checkout, import, and GC operations in <see cref="UpdateActivityTracker"/> so the auto-updater
/// never restarts SPAWN mid-write.
/// </summary>
public sealed class ActivityTrackingSaveTimeMachineService(
    ISaveTimeMachineService inner,
    UpdateActivityTracker tracker) : ISaveTimeMachineService
{
    private readonly ISaveTimeMachineService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly UpdateActivityTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    public event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged
    {
        add => _inner.SaveTreeChanged += value;
        remove => _inner.SaveTreeChanged -= value;
    }

    public Task<SaveGameStateRecord> GetGameStateAsync(string gameId, CancellationToken ct = default) =>
        _inner.GetGameStateAsync(gameId, ct);

    public Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default) =>
        _inner.UpdateGameStateAsync(state, ct);

    public Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        _inner.GetSettingsAsync(ct);

    public Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default) =>
        _inner.SaveSettingsAsync(settings, ct);

    public Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string gameId, CancellationToken ct = default) =>
        _inner.GetSaveTreeGraphAsync(gameId, ct);

    public Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default) =>
        _inner.GetLibraryOverviewAsync(ct);

    public Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default) =>
        _inner.GetSnapshotFilesAsync(snapshotId, ct);

    public Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default) =>
        _inner.GetVaultStatsAsync(ct);

    public Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(string leftSnapshotId, string rightSnapshotId, CancellationToken ct = default) =>
        _inner.DiffSnapshotsAsync(leftSnapshotId, rightSnapshotId, ct);

    public Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string snapshotId, CancellationToken ct = default) =>
        _inner.DiffSnapshotWithDiskAsync(snapshotId, ct);

    public async Task<CreateSaveSnapshotResult> CreateSnapshotAsync(CreateSaveSnapshotRequest request, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.CreateSnapshotAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.RollbackToSnapshotAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.CreateBranchAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.SwitchBranchAsync(request, ct).ConfigureAwait(false);
    }

    public Task<SaveBranchRecord?> RenameBranchAsync(
        string branchId,
        string newName,
        string? description = null,
        string? colorHex = null,
        CancellationToken ct = default) =>
        _inner.RenameBranchAsync(branchId, newName, description, colorHex, ct);

    public async Task<DeleteBranchResult> DeleteBranchAsync(string branchId, bool deleteSnapshots = false, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.DeleteBranchAsync(branchId, deleteSnapshots, ct).ConfigureAwait(false);
    }

    public Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default) =>
        _inner.SetSnapshotPinnedAsync(snapshotId, isPinned, ct);

    public Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default) =>
        _inner.UpdateSnapshotNoteAsync(snapshotId, note, ct);

    public async Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.DeleteSnapshotAsync(snapshotId, ct).ConfigureAwait(false);
    }

    public async Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(string snapshotId, string targetDirectory, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.ExportSnapshotToFolderAsync(snapshotId, targetDirectory, ct).ConfigureAwait(false);
    }

    public async Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(string gameId, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.ImportLegacyPreTrainerSnapshotsAsync(gameId, ct).ConfigureAwait(false);
    }

    public async Task<SaveGcReport> RunCasGarbageCollectionAsync(TimeSpan? gracePeriodOverride = null, CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.RunCasGarbageCollectionAsync(gracePeriodOverride, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Transparent decorator around <see cref="ICloudSaveSyncService"/> that tracks active SPPK sync execution
/// inside <see cref="UpdateActivityTracker"/>.
/// </summary>
public sealed class ActivityTrackingCloudSaveSyncService(
    ICloudSaveSyncService inner,
    UpdateActivityTracker tracker) : ICloudSaveSyncService
{
    private readonly ICloudSaveSyncService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly UpdateActivityTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    public Task<IReadOnlyList<CloudSyncEndpointRecord>> ListEndpointsAsync(CancellationToken ct = default) =>
        _inner.ListEndpointsAsync(ct);

    public Task<CloudSyncEndpointRecord> SaveEndpointAsync(
        CloudSyncEndpointRecord endpoint,
        CloudSyncEndpointSecrets? secrets = null,
        CancellationToken ct = default) =>
        _inner.SaveEndpointAsync(endpoint, secrets, ct);

    public Task<bool> DeleteEndpointAsync(string endpointId, CancellationToken ct = default) =>
        _inner.DeleteEndpointAsync(endpointId, ct);

    public Task<CloudSyncEndpointTestResult> TestEndpointConnectionAsync(string endpointId, CancellationToken ct = default) =>
        _inner.TestEndpointConnectionAsync(endpointId, ct);

    public Task<CloudSyncPlanPreview> PlanSyncAsync(string gameId, string? endpointId = null, CancellationToken ct = default) =>
        _inner.PlanSyncAsync(gameId, endpointId, ct);

    public async Task<CloudSyncExecutionReport> ExecuteSyncAsync(
        string gameId,
        string? endpointId = null,
        CloudDivergenceResolution? divergenceResolution = null,
        IProgress<CloudSyncProgressTick>? progress = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.ExecuteSyncAsync(gameId, endpointId, divergenceResolution, progress, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudSyncExecutionReport>> SyncAllConfiguredGamesAsync(
        IProgress<CloudSyncProgressTick>? progress = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.SyncAllConfiguredGamesAsync(progress, ct).ConfigureAwait(false);
    }
}
