using Spawn.Contracts;

namespace Spawn.Core.Update;

/// <summary>
/// Thread-safe counter tracking active save snapshot/rollback and game-data cleanup operations so the
/// in-app auto-updater never auto-restarts SPAWN mid-operation.
/// </summary>
public sealed class UpdateActivityTracker
{
    private int _activeSaveSnapshotOrRollbackCount;
    private int _activeGameDataCleanupCount;

    public int ActiveSaveSnapshotOrRollbackCount => Volatile.Read(ref _activeSaveSnapshotOrRollbackCount);
    public int ActiveGameDataCleanupCount => Volatile.Read(ref _activeGameDataCleanupCount);

    public bool IsSaveSnapshotOrRollbackInProgress => ActiveSaveSnapshotOrRollbackCount > 0;
    public bool IsGameDataCleanupInProgress => ActiveGameDataCleanupCount > 0;

    public event Action? ActivityChanged;

    public IDisposable BeginSaveSnapshotOrRollback()
    {
        Interlocked.Increment(ref _activeSaveSnapshotOrRollbackCount);
        ActivityChanged?.Invoke();
        return new Scope(() =>
        {
            Interlocked.Decrement(ref _activeSaveSnapshotOrRollbackCount);
            ActivityChanged?.Invoke();
        });
    }

    public IDisposable BeginGameDataCleanup()
    {
        Interlocked.Increment(ref _activeGameDataCleanupCount);
        ActivityChanged?.Invoke();
        return new Scope(() =>
        {
            Interlocked.Decrement(ref _activeGameDataCleanupCount);
            ActivityChanged?.Invoke();
        });
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}

/// <summary>
/// Transparent decorator around <see cref="IGameDataManagementService"/> that marks active cleanup/restore
/// operations in <see cref="UpdateActivityTracker"/> without modifying <c>Spawn.Modules.Launcher</c>.
/// </summary>
public sealed class ActivityTrackingGameDataManagementService(
    IGameDataManagementService inner,
    UpdateActivityTracker tracker) : IGameDataManagementService
{
    private readonly IGameDataManagementService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly UpdateActivityTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default) =>
        _inner.InspectGameDataAsync(gameId, refreshOnlineRules, cancellationToken);

    public async Task<GameDataCleanResult> CleanGameDataAsync(
        GameDataCleanRequest request,
        CancellationToken cancellationToken = default)
    {
        using IDisposable scope = _tracker.BeginGameDataCleanup();
        return await _inner.CleanGameDataAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(
        string gameId,
        CancellationToken cancellationToken = default) =>
        _inner.GetLatestUndoSnapshotAsync(gameId, cancellationToken);

    public async Task<GameDataCleanResult> RestoreLastCleanupAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        using IDisposable scope = _tracker.BeginGameDataCleanup();
        return await _inner.RestoreLastCleanupAsync(gameId, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<GameDataEntryDto>> EnumerateSaveLocationsAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default) =>
        _inner.EnumerateSaveLocationsAsync(gameId, refreshOnlineRules, cancellationToken);

    public Task<GameDataSafetyCheckResult> ValidateSaveRestoreTargetAsync(
        string gameId,
        string pathOrRegistryKey,
        CancellationToken cancellationToken = default) =>
        _inner.ValidateSaveRestoreTargetAsync(gameId, pathOrRegistryKey, cancellationToken);
}

/// <summary>
/// Transparent decorator around <see cref="IPreTrainerSaveSnapshotService"/> that marks active snapshot/rollback
/// operations in <see cref="UpdateActivityTracker"/> without modifying <c>Spawn.Modules.Progress</c>.
/// </summary>
public sealed class ActivityTrackingPreTrainerSnapshotService(
    IPreTrainerSaveSnapshotService inner,
    UpdateActivityTracker tracker) : IPreTrainerSaveSnapshotService
{
    private readonly IPreTrainerSaveSnapshotService _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly UpdateActivityTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    public async Task<PreTrainerSnapshotResult> CreateAsync(
        string gameId,
        string? trainerId,
        CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.CreateAsync(gameId, trainerId, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(
        string gameId,
        CancellationToken ct = default) =>
        _inner.ListAsync(gameId, ct);

    public async Task<PreTrainerRestoreResult> RestoreAsync(
        string snapshotId,
        CancellationToken ct = default)
    {
        using IDisposable scope = _tracker.BeginSaveSnapshotOrRollback();
        return await _inner.RestoreAsync(snapshotId, ct).ConfigureAwait(false);
    }
}
