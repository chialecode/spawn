namespace Spawn.Contracts;

/// <summary>
/// Pluggable execution backend for single-player trainers: <c>"ExternalTrainer"</c> (M3) or <c>"NativeModPack"</c> (M6).
/// </summary>
public interface ITrainerBackend : IAsyncDisposable
{
    /// <summary>Backend discriminator: <c>"ExternalTrainer"</c> (M3) or <c>"NativeModPack"</c> (M6).</summary>
    string BackendKind { get; }

    Task<TrainerSessionHandle> ActivateSessionAsync(TrainerLaunchSpec spec, CancellationToken ct = default);
    Task TriggerEntryAsync(string sessionId, string entryId, bool targetEnabled, CancellationToken ct = default);

    Task TriggerHotkeyAsync(string sessionId, TrainerTriggerHotkeyRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException($"Backend '{BackendKind}' does not support direct hotkey injection.");

    Task SetWindowVisibilityAsync(string sessionId, bool visible, CancellationToken ct = default) =>
        Task.CompletedTask;

    async Task<TrainerSetWindowVisibilityResponse> SetWindowVisibilityWithResultAsync(
        string sessionId,
        bool visible,
        CancellationToken ct = default)
    {
        await SetWindowVisibilityAsync(sessionId, visible, ct).ConfigureAwait(false);
        return new TrainerSetWindowVisibilityResponse
        {
            Succeeded = true,
            AffectedWindowCount = 1,
        };
    }

    Task ApplyAudioFallbackAsync(string sessionId, IReadOnlyList<int> pids, bool mute, double volume, CancellationToken ct = default) =>
        Task.CompletedTask;

    Task StopSessionAsync(string sessionId, TrainerShutdownReason reason, CancellationToken ct = default) =>
        ShutdownAllImmediatelyAsync(reason, ct);

    Task ShutdownAllImmediatelyAsync(TrainerShutdownReason reason, CancellationToken ct = default);
}

/// <summary>
/// Clean-room trainer discovery, user-triggered single download, local import, SHA-256 TOCTOU verification, and hotkey catalog.
/// </summary>
public interface ITrainerCatalogService
{
    Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default);
    Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default);
    Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default);
    Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default);
    Task RemoveAsync(string trainerId, CancellationToken ct = default);

    Task<IReadOnlyList<TrainerCatalogRecord>> ListForGameAsync(string gameId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrainerCatalogRecord>>([]);

    Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrainerHotkeyBindingRecord>>([]);

    Task<TrainerCandidate> LoadCandidateDetailsAsync(string gameId, TrainerCandidate candidate, CancellationToken ct = default) =>
        Task.FromResult(candidate);

    async Task<TrainerCandidateDetailsResult> LoadCandidateDetailsWithDiagnosticsAsync(
        string gameId,
        TrainerCandidate candidate,
        CancellationToken ct = default)
    {
        TrainerCandidate loaded = await LoadCandidateDetailsAsync(gameId, candidate, ct).ConfigureAwait(false);
        return new TrainerCandidateDetailsResult
        {
            Candidate = loaded,
            Succeeded = true,
            ChallengeDetected = false,
        };
    }

    Task<TrainerImportResult> DownloadWithExecutableSelectionAsync(
        string gameId,
        TrainerCandidate candidate,
        string? selectedExecutableRelPath,
        IProgress<TrainerDownloadProgress>? progress = null,
        CancellationToken ct = default) =>
        DownloadAsync(gameId, candidate, progress, ct);

    Task<TrainerImportResult> ImportLocalWithExecutableSelectionAsync(
        string gameId,
        string userSelectedPath,
        string? selectedExecutableRelPath,
        CancellationToken ct = default) =>
        ImportLocalAsync(gameId, userSelectedPath, ct);

    Task<TrainerImportResult> ImportLocalReplacingAsync(
        string gameId,
        string userSelectedPath,
        string? replaceTrainerId,
        bool inheritManualHotkeys = true,
        string? selectedExecutableRelPath = null,
        CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(selectedExecutableRelPath)
            ? ImportLocalAsync(gameId, userSelectedPath, ct)
            : ImportLocalWithExecutableSelectionAsync(gameId, userSelectedPath, selectedExecutableRelPath, ct);

    Task SaveHotkeyBindingsAsync(string trainerId, IReadOnlyList<TrainerHotkeyBindingRecord> bindings, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Host-side orchestrator for trainer lifecycle, safety pre-flight, pre-trainer snapshot, audio controller, and worker delegation.
/// </summary>
public interface ITrainerRuntimeService
{
    TrainerRuntimeState State { get; }
    TrainerSessionHandle? ActiveSession { get; }
    event EventHandler<TrainerRuntimeStateChanged>? StateChanged;

    Task<TrainerLaunchOutcome> LaunchAsync(string trainerId, TrainerLaunchOptions options, CancellationToken ct = default);
    Task TriggerHotkeyAsync(string sessionId, string entryId, CancellationToken ct = default);
    Task SetOriginalWindowVisibleAsync(string sessionId, bool visible, CancellationToken ct = default);

    async Task<TrainerSetWindowVisibilityResponse> SetOriginalWindowVisibleWithResultAsync(
        string sessionId,
        bool visible,
        CancellationToken ct = default)
    {
        await SetOriginalWindowVisibleAsync(sessionId, visible, ct).ConfigureAwait(false);
        return new TrainerSetWindowVisibilityResponse
        {
            Succeeded = true,
            AffectedWindowCount = 1,
        };
    }

    Task StopAsync(string sessionId, CancellationToken ct = default);

    Task StopAllAsync(TrainerShutdownReason reason = TrainerShutdownReason.UserRequested, CancellationToken ct = default) =>
        ActiveSession is { } session ? StopAsync(session.SessionId, ct) : Task.CompletedTask;
}

/// <summary>
/// Zero-injection dual-layer (A0 INI/SMF + A dedicated MTA WASAPI) audio controller for external trainer processes.
/// </summary>
public interface ITrainerAudioController : IAsyncDisposable
{
    /// <summary>Raised when Host-side WASAPI returns <c>E_ACCESSDENIED</c> on a High IL trainer session and Worker fallback is needed.</summary>
    event Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>? FallbackRequired;

    Task PrepareAsync(TrainerAudioPolicy policy, string sandboxRoot, CancellationToken ct = default);
    void TrackProcess(int pid);
    Task ApplyPolicyAsync(TrainerAudioPolicy policy, CancellationToken ct = default);
    Task RestoreA0BackupsAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Non-blocking lightweight pre-trainer save snapshot and one-click restore service (REQ-PRG-08, D-25).
/// </summary>
public interface IPreTrainerSaveSnapshotService
{
    Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default);

    Task<PreTrainerSnapshotResult> CreateAsync(
        string gameId,
        string? trainerId,
        bool isManual,
        CancellationToken ct = default) =>
        CreateAsync(gameId, trainerId, ct);

    Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default);
    Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default);
}

/// <summary>
/// Zero-circular-dependency shutdown hook implemented by <c>Spawn.Modules.Progress</c> and injected into <c>GameLaunchService</c> (LOOP-06).
/// </summary>
public interface ISensitiveWorkerShutdownHook
{
    Task<SensitiveWorkerShutdownReport> ShutdownAllForProtectedLaunchAsync(TimeSpan budget, CancellationToken ct = default);
}

/// <summary>
/// Opt-in Windows Defender single-folder sandbox exclusion assistant and Smart App Control diagnostic service.
/// </summary>
public interface IDefenderExclusionAssistant
{
    Task<DefenderExclusionStatusReport> InspectStatusAsync(string sandboxPath, CancellationToken ct = default);
    Task<DefenderExclusionStatusReport> RequestExclusionAsync(string sandboxPath, CancellationToken ct = default);
}

/// <summary>
/// Unified trainer anti-cheat safety gate: four-layer Tier 0 inspection, live zero-AC hard ticket,
/// controlled <c>VerifiedOfflineBypass</c> evaluation, and 2-second active-session emergency stop watchdog.
/// </summary>
public interface ITrainerSafetyGate
{
    bool IsWatchdogActive { get; }
    event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered;

    Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default);
    Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default);
    Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default);
    Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default);

    async Task<TrainerGameSafetySnapshot> EvaluateGameSafetySnapshotAsync(
        GameRecord game,
        bool strictVanguardMode = false,
        CancellationToken ct = default)
    {
        var allowance = await EvaluateTrainerAllowanceAsync(game, strictVanguardMode, ct).ConfigureAwait(false);
        var live = await ProbeLiveAntiCheatAsync(ct).ConfigureAwait(false);
        var bypass = await EvaluateOfflineBypassAsync(game, ct).ConfigureAwait(false);
        return new TrainerGameSafetySnapshot
        {
            FourLayerReport = new FourLayerAntiCheatReport
            {
                EffectiveTier = allowance.StaticGameTier,
                HasSinglePlayerCategory = bypass.HasSinglePlayerMode ? true : null,
            },
            LiveProbe = live,
            OfflineBypass = bypass,
            AllowanceDecision = allowance,
        };
    }

    void NotifyTrainerSessionStarted(string sessionId, string gameId);
    void NotifyTrainerSessionEnded(string sessionId);
}
