using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Runtime;

/// <summary>
/// Host-side single-player trainer lifecycle orchestrator (<see cref="ITrainerRuntimeService"/>):
/// chains anti-cheat hard safety ticket (<see cref="ITrainerSafetyGate"/>), TOCTOU SHA-256 verification
/// (<see cref="ITrainerCatalogService"/>), non-blocking pre-trainer save snapshot (<see cref="IPreTrainerSaveSnapshotService"/>),
/// zero-injection A0+A audio control (<see cref="ITrainerAudioController"/>), isolated <see cref="ITrainerBackend"/> delegation,
/// 2-second watchdog emergency stop subscription, and <c>StopWithGameExit</c> coupling with <see cref="IGameLaunchService"/>.
/// </summary>
public sealed class TrainerRuntimeService : ITrainerRuntimeService, IDisposable, IAsyncDisposable
{
    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly ITrainerSafetyGate _safetyGate;
    private readonly ITrainerCatalogService _catalogService;
    private readonly IPreTrainerSaveSnapshotService _snapshotService;
    private readonly ITrainerAudioController _audioController;
    private readonly ITrainerBackend _backend;
    private readonly IGameLaunchService _gameLaunchService;
    private readonly ITrainerElevationAdvisor _elevationAdvisor;
    private readonly ILogger<TrainerRuntimeService> _logger;
    private readonly ISaveTimeMachineService? _saveTimeMachineService;
    private readonly Func<string, IReadOnlyList<SafeProcessEntry>> _directoryProcessFinder;
    private readonly TimeSpan _preTrainerSnapshotTimeout;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private TrainerRuntimeState _state = TrainerRuntimeState.Ready;
    private TrainerSessionHandle? _activeSession;
    private TrainerLaunchOptions? _activeOptions;
    private CancellationTokenSource? _inFlightLaunchCts;
    private int _disposed;

    public TrainerRuntimeState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public TrainerSessionHandle? ActiveSession
    {
        get
        {
            lock (_stateLock)
            {
                return _activeSession;
            }
        }
    }

    public event EventHandler<TrainerRuntimeStateChanged>? StateChanged;

    public TrainerRuntimeService(
        SpawnDatabase database,
        SpawnPaths paths,
        ITrainerSafetyGate safetyGate,
        ITrainerCatalogService catalogService,
        IPreTrainerSaveSnapshotService snapshotService,
        ITrainerAudioController audioController,
        ITrainerBackend backend,
        IGameLaunchService gameLaunchService,
        ITrainerElevationAdvisor? elevationAdvisor = null,
        ILogger<TrainerRuntimeService>? logger = null,
        ISaveTimeMachineService? saveTimeMachineService = null,
        Func<string, IReadOnlyList<SafeProcessEntry>>? directoryProcessFinder = null,
        TimeSpan? preTrainerSnapshotTimeout = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _audioController = audioController ?? throw new ArgumentNullException(nameof(audioController));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _gameLaunchService = gameLaunchService ?? throw new ArgumentNullException(nameof(gameLaunchService));
        _elevationAdvisor = elevationAdvisor ?? new TrainerElevationAdvisor();
        _logger = logger ?? NullLogger<TrainerRuntimeService>.Instance;
        _saveTimeMachineService = saveTimeMachineService;
        _directoryProcessFinder = directoryProcessFinder
            ?? (static dir => SafeProcessInspector.FindProcessesUnderDirectory(dir, excludeAuxiliary: true));
        _preTrainerSnapshotTimeout = preTrainerSnapshotTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : TimeSpan.FromSeconds(15);

        _safetyGate.EmergencyStopTriggered += OnEmergencyStopTriggered;
        _gameLaunchService.SessionChanged += OnGameSessionChanged;
        _audioController.FallbackRequired += OnAudioFallbackRequiredAsync;

        if (_backend is ITrainerBackendSessionEvents backendEvents)
        {
            backendEvents.ProcessCreated += OnBackendProcessCreated;
            backendEvents.SessionEnded += OnBackendSessionEnded;
        }
    }

    public async Task<TrainerLaunchOutcome> LaunchAsync(
        string trainerId,
        TrainerLaunchOptions options,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);
        ArgumentNullException.ThrowIfNull(options);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        CancellationTokenSource? launchCts = null;
        try
        {
            if (ActiveSession is { } existingSession)
            {
                GameRecord? runningGame = await _database.GetGameByIdAsync(existingSession.GameId, ct).ConfigureAwait(false);
                string runningTitle = !string.IsNullOrWhiteSpace(runningGame?.Title)
                    ? runningGame.Title
                    : existingSession.GameId;
                return new TrainerLaunchOutcome
                {
                    Succeeded = false,
                    FailureKind = TrainerLaunchFailureKind.AlreadyRunning,
                    Message = $"当前游戏「{runningTitle}」的修改器正在运行中，请先停止后再启动其他修改器。",
                };
            }

            TrainerCatalogRecord? trainer = await _database.GetTrainerByIdAsync(trainerId, ct).ConfigureAwait(false);
            if (trainer is null)
            {
                return new TrainerLaunchOutcome
                {
                    Succeeded = false,
                    FailureKind = TrainerLaunchFailureKind.TrainerNotFound,
                    Message = $"未在目录中找到修改器记录：{trainerId}",
                };
            }

            GameRecord? game = await _database.GetGameByIdAsync(trainer.GameId, ct).ConfigureAwait(false);
            if (game is null)
            {
                return new TrainerLaunchOutcome
                {
                    Succeeded = false,
                    FailureKind = TrainerLaunchFailureKind.TrainerNotFound,
                    Message = $"未找到修改器关联的游戏记录：{trainer.GameId}",
                };
            }

            launchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_stateLock)
            {
                _inFlightLaunchCts = launchCts;
            }

            CancellationToken launchToken = launchCts.Token;
            TransitionState(game.GameId, trainer.TrainerId, null, TrainerRuntimeState.PreFlight, "正在执行反作弊硬验票与完整性校验…");

            // 1. Hard anti-cheat safety gate check (four-layer Tier 0 / VerifiedOfflineBypass / zero live AC / cooldown).
            TrainerAllowanceDecision allowance = await _safetyGate
                .EvaluateTrainerAllowanceAsync(game, options.StrictVanguardMode, launchToken)
                .ConfigureAwait(false);

            if (!allowance.IsAllowed)
            {
                TransitionState(game.GameId, trainer.TrainerId, null, TrainerRuntimeState.Ready, allowance.Message);
                return new TrainerLaunchOutcome
                {
                    Succeeded = false,
                    FailureKind = TrainerLaunchFailureKind.SafetyGateBlocked,
                    AllowanceDecision = allowance,
                    Message = allowance.Message,
                };
            }

            // 2. Pre-launch SHA-256 TOCTOU integrity verification (silently deleted binaries classify as AntivirusQuarantined).
            TrainerIntegrityReport integrity = await _catalogService.VerifyAsync(trainer.TrainerId, launchToken).ConfigureAwait(false);
            if (!integrity.IsValid)
            {
                bool isSandboxBoundaryViolation = !integrity.IsInsideSandbox
                    && !string.IsNullOrWhiteSpace(integrity.FailureMessage)
                    && (integrity.FailureMessage.Contains("越界", StringComparison.Ordinal)
                        || integrity.FailureMessage.Contains("重解析点", StringComparison.Ordinal)
                        || integrity.FailureMessage.Contains("路径无效", StringComparison.Ordinal));

                TrainerLaunchFailureKind integrityFailure =
                    integrity.Win32ErrorCode is 225 or 226
                    || (!integrity.FileExists && !isSandboxBoundaryViolation)
                        ? TrainerLaunchFailureKind.AntivirusQuarantined
                        : integrity.Win32ErrorCode == 4551
                            ? TrainerLaunchFailureKind.SmartAppControlBlocked
                            : TrainerLaunchFailureKind.IntegrityHashMismatch;

                TrainerRuntimeState nextState = integrityFailure is TrainerLaunchFailureKind.AntivirusQuarantined
                    or TrainerLaunchFailureKind.SmartAppControlBlocked
                    ? TrainerRuntimeState.Quarantined
                    : TrainerRuntimeState.Ready;

                TransitionState(game.GameId, trainer.TrainerId, null, nextState, integrity.FailureMessage);
                return new TrainerLaunchOutcome
                {
                    Succeeded = false,
                    FailureKind = integrityFailure,
                    AllowanceDecision = allowance,
                    IntegrityReport = integrity,
                    Win32ErrorCode = integrity.Win32ErrorCode,
                    Message = integrity.FailureMessage ?? "修改器文件 SHA-256 校验失败，已拒绝启动。",
                };
            }

            TrainerSettingsDto settings = await _database.GetTrainerSettingsAsync(launchToken).ConfigureAwait(false);
            string sandboxRoot = _paths.ResolveTrainerSandboxDirectory(settings.CustomSandboxRoot, requirePureAscii: false);

            // 3. Non-blocking pre-trainer save snapshot: prefer unified Save Time Machine, fallback to legacy engine.
            PreTrainerSnapshotResult? preSnapshot = null;
            if (options.CreatePreTrainerSnapshot)
            {
                TransitionState(game.GameId, trainer.TrainerId, null, TrainerRuntimeState.Snapshotting, "正在创建启动前存档快照…");
                preSnapshot = await CreatePreTrainerSnapshotNonBlockingAsync(game, trainer, settings, launchToken).ConfigureAwait(false);
            }

            // 4. Prepare zero-injection A0+A audio mute prior to spawning the external trainer.
            TransitionState(game.GameId, trainer.TrainerId, null, TrainerRuntimeState.Launching, "正在拉起隔离 Worker 并启动修改器…");
            TrainerAudioPolicy audioPolicy = options.AudioPolicyOverride ?? settings.ToAudioPolicy();
            try
            {
                await _audioController.PrepareAsync(audioPolicy, sandboxRoot, launchToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trainer audio controller preparation failed; continuing launch.");
            }

            // 5. Inspect target game process IL (with pure 0x1000 fallback for externally launched games) & PE manifest.
            (bool targetGameElevated, bool targetGameLaunchedExternally) = InspectTargetGameProcess(game);
            string resolvedExePath = !string.IsNullOrWhiteSpace(integrity.ResolvedExecutablePath)
                ? integrity.ResolvedExecutablePath!
                : Path.GetFullPath(Path.Combine(sandboxRoot, trainer.SandboxRelativePath));
            bool peRequiresAdmin = trainer.RequiresAdmin || _elevationAdvisor.InspectExecutableManifest(resolvedExePath).RequiresAdministrator;

            string sessionId = SpawnId.NewUlid();
            var spec = new TrainerLaunchSpec
            {
                SessionId = sessionId,
                GameId = game.GameId,
                TrainerId = trainer.TrainerId,
                SandboxRoot = sandboxRoot,
                SandboxRelativePath = trainer.SandboxRelativePath,
                ExpectedSha256 = trainer.FileSha256,
                ExpectedArchitecture = trainer.PeMachine,
                RequiresAdmin = peRequiresAdmin,
                TargetGameIsElevated = targetGameElevated,
                Options = options,
            };

            // 6. Activate isolated backend session.
            TrainerSessionHandle session;
            try
            {
                session = await _backend.ActivateSessionAsync(spec, launchToken).ConfigureAwait(false);
            }
            catch (TrainerWorkerLaunchException ex)
            {
                TrainerRuntimeState nextState = ex.FailureKind is TrainerLaunchFailureKind.AntivirusQuarantined
                    or TrainerLaunchFailureKind.SmartAppControlBlocked
                    ? TrainerRuntimeState.Quarantined
                    : TrainerRuntimeState.Ready;
                TransitionState(game.GameId, trainer.TrainerId, null, nextState, ex.Message);
                return new TrainerLaunchOutcome
                {
                    Succeeded = false,
                    FailureKind = ex.FailureKind,
                    Win32ErrorCode = ex.Win32ErrorCode,
                    AllowanceDecision = allowance,
                    PreSnapshot = preSnapshot,
                    IntegrityReport = integrity,
                    Message = ex.Message,
                };
            }

            lock (_stateLock)
            {
                _activeSession = session;
                _activeOptions = options;
            }

            if (session.TrainerProcessId > 4)
            {
                _audioController.TrackProcess(session.TrainerProcessId);
            }

            await _database.UpdateTrainerLastLaunchedAsync(trainer.TrainerId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            _safetyGate.NotifyTrainerSessionStarted(session.SessionId, game.GameId);
            TransitionState(game.GameId, trainer.TrainerId, session.SessionId, TrainerRuntimeState.Running, "修改器已在安全沙箱中运行。");

            string? externalLaunchNotice = targetGameLaunchedExternally
                ? "检测到游戏非通过 SPAWN 启动，退出游戏后请手动停止修改器。"
                : null;

            return new TrainerLaunchOutcome
            {
                Succeeded = true,
                Session = session,
                AllowanceDecision = allowance,
                PreSnapshot = preSnapshot,
                IntegrityReport = integrity,
                IsTargetGameLaunchedExternally = targetGameLaunchedExternally,
                ExternalLaunchNotice = externalLaunchNotice,
                Message = "修改器已启动。",
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TransitionState(string.Empty, trainerId, null, TrainerRuntimeState.Ready, "修改器启动因反作弊急停而中止。");
            return new TrainerLaunchOutcome
            {
                Succeeded = false,
                FailureKind = TrainerLaunchFailureKind.SafetyGateBlocked,
                Message = "修改器启动因反作弊急停而中止。",
            };
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_inFlightLaunchCts, launchCts))
                {
                    _inFlightLaunchCts = null;
                }
            }

            launchCts?.Dispose();
            _lifecycleLock.Release();
        }
    }

    public async Task TriggerHotkeyAsync(string sessionId, string entryId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        TrainerSessionHandle? session;
        TrainerLaunchOptions? options;
        lock (_stateLock)
        {
            session = _activeSession;
            options = _activeOptions;
        }

        if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"修改器会话 '{sessionId}' 未运行。");
        }

        IReadOnlyList<TrainerHotkeyBindingRecord> bindings = await _catalogService.GetHotkeysAsync(session.TrainerId, ct).ConfigureAwait(false);
        if (bindings.Count == 0)
        {
            bindings = await _database.GetTrainerHotkeyBindingsAsync(session.TrainerId, ct).ConfigureAwait(false);
        }

        TrainerHotkeyBindingRecord? binding = bindings.FirstOrDefault(
            b => string.Equals(b.EntryId, entryId, StringComparison.OrdinalIgnoreCase));

        if (binding is null)
        {
            throw new InvalidOperationException($"未找到修改器热键条目 '{entryId}'。");
        }

        if (!HotkeyChordParser.TryParse(binding.EffectiveChord, out ParsedHotkeyChord? chord) || chord is null)
        {
            throw new InvalidOperationException($"无法解析热键组合 '{binding.EffectiveChord}'。");
        }

        var request = new TrainerTriggerHotkeyRequest
        {
            SessionId = sessionId,
            EntryId = entryId,
            VirtualKey = chord.VirtualKey,
            Modifiers = chord.Modifiers,
            IsExtendedKeyOverride = chord.IsExtendedKeyOverride,
            HoldMsOverride = options?.HotkeyHoldMs ?? TrainerSettingsDto.DefaultHotkeyHoldMs,
            ScanCodeMode = options?.ScanCodeMode ?? false,
        };

        await _backend.TriggerHotkeyAsync(sessionId, request, ct).ConfigureAwait(false);

        if (binding.EntryKind == TrainerHotkeyEntryKind.Toggle)
        {
            await _database.UpdateTrainerHotkeyProxyStateAsync(
                session.TrainerId,
                binding.EntryId,
                !binding.ProxyState,
                ct).ConfigureAwait(false);
        }
    }

    public async Task<TrainerSetWindowVisibilityResponse> SetOriginalWindowVisibleWithResultAsync(
        string sessionId,
        bool visible,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        TrainerSessionHandle? session = ActiveSession;
        if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
        {
            return new TrainerSetWindowVisibilityResponse
            {
                Succeeded = false,
                AffectedWindowCount = 0,
                ErrorMessage = $"修改器会话 '{sessionId}' 未运行。",
            };
        }

        return await _backend.SetWindowVisibilityWithResultAsync(sessionId, visible, ct).ConfigureAwait(false);
    }

    public async Task SetOriginalWindowVisibleAsync(string sessionId, bool visible, CancellationToken ct = default)
    {
        _ = await SetOriginalWindowVisibleWithResultAsync(sessionId, visible, ct).ConfigureAwait(false);
    }

    public Task StopAsync(string sessionId, CancellationToken ct = default) =>
        StopInternalAsync(sessionId, TrainerShutdownReason.UserRequested, detail: null, ct);

    public Task StopAllAsync(
        TrainerShutdownReason reason = TrainerShutdownReason.UserRequested,
        CancellationToken ct = default)
    {
        CancelInFlightLaunchIfAny();
        TrainerSessionHandle? session = ActiveSession;
        if (session is not null)
        {
            return StopInternalAsync(session.SessionId, reason, detail: null, ct);
        }

        return _backend.ShutdownAllImmediatelyAsync(reason, ct);
    }

    private async Task StopInternalAsync(
        string sessionId,
        TrainerShutdownReason reason,
        string? detail,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        bool isEmergency = reason is TrainerShutdownReason.AntiCheatWatchdogEmergency
            or TrainerShutdownReason.ProtectedGamePreFlightPurge
            or TrainerShutdownReason.IntegrityOrSecurityViolation;

        if (isEmergency)
        {
            CancelInFlightLaunchIfAny();
        }

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TrainerSessionHandle? session;
            lock (_stateLock)
            {
                session = _activeSession;
                if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            TrainerRuntimeState stoppingState = isEmergency
                ? TrainerRuntimeState.EmergencyStopped
                : TrainerRuntimeState.Stopping;
            TransitionState(session.GameId, session.TrainerId, session.SessionId, stoppingState, detail ?? "正在停止修改器会话…");

            try
            {
                if (isEmergency)
                {
                    await _backend.ShutdownAllImmediatelyAsync(reason, ct).ConfigureAwait(false);
                }
                else
                {
                    await _backend.StopSessionAsync(session.SessionId, reason, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    if (_activeSession is not null && string.Equals(_activeSession.SessionId, session.SessionId, StringComparison.Ordinal))
                    {
                        _activeSession = null;
                        _activeOptions = null;
                    }
                }

                await ResetHotkeyProxyStatesSafeAsync(session.TrainerId).ConfigureAwait(false);
                _safetyGate.NotifyTrainerSessionEnded(session.SessionId);
                TransitionState(session.GameId, session.TrainerId, null, TrainerRuntimeState.Ready, detail ?? "修改器已停止。");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<PreTrainerSnapshotResult> CreatePreTrainerSnapshotNonBlockingAsync(
        GameRecord game,
        TrainerCatalogRecord trainer,
        TrainerSettingsDto settings,
        CancellationToken launchToken)
    {
        if (!settings.CreatePreTrainerSnapshot)
        {
            return new PreTrainerSnapshotResult
            {
                Succeeded = false,
                Status = PreTrainerSnapshotStatus.Skipped,
                SkipReason = PreTrainerSnapshotSkipReason.DisabledBySettings,
                Message = "已在设置中关闭修改器启动前自动存档快照。",
            };
        }

        if (_saveTimeMachineService is not null)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(launchToken);
                timeoutCts.CancelAfter(_preTrainerSnapshotTimeout);

                CreateSaveSnapshotResult tmResult = await _saveTimeMachineService.CreateSnapshotAsync(
                    new CreateSaveSnapshotRequest
                    {
                        GameId = game.GameId,
                        TriggerKind = SaveSnapshotTriggerKind.PreTrainerImport,
                        Note = $"修改器启动前自动快照（{trainer.DisplayName ?? trainer.TrainerId}）",
                        IncludeConfigFiles = false,
                        SkipIfUnchangedFromHead = false,
                    },
                    timeoutCts.Token).ConfigureAwait(false);

                if (tmResult.Succeeded && tmResult.Snapshot is not null)
                {
                    string tmMessage = string.IsNullOrWhiteSpace(tmResult.Message)
                        ? $"已通过存档时光机创建启动前快照（{tmResult.Snapshot.FileCount} 个文件）。"
                        : (tmResult.Message.Contains("时光机", StringComparison.Ordinal)
                            ? tmResult.Message
                            : $"已通过存档时光机创建启动前快照：{tmResult.Message}");

                    return new PreTrainerSnapshotResult
                    {
                        Succeeded = true,
                        Status = PreTrainerSnapshotStatus.Completed,
                        Message = tmMessage,
                        Snapshot = new PreTrainerSnapshotRecord
                        {
                            SnapshotId = tmResult.Snapshot.SnapshotId,
                            GameId = game.GameId,
                            TrainerId = trainer.TrainerId,
                            TriggerKind = PreTrainerSnapshotTriggerKind.PreTrainer,
                            Status = PreTrainerSnapshotStatus.Completed,
                            FileCount = tmResult.Snapshot.FileCount,
                            TotalBytes = tmResult.Snapshot.TotalRawBytes,
                            CreatedAtUtc = tmResult.Snapshot.CreatedAtUtc,
                        },
                    };
                }

                if (tmResult.Skipped)
                {
                    PreTrainerSnapshotSkipReason mappedReason = MapTimeMachineSkipReason(tmResult.SkipReason);
                    return new PreTrainerSnapshotResult
                    {
                        Succeeded = false,
                        Status = PreTrainerSnapshotStatus.Skipped,
                        SkipReason = mappedReason,
                        Message = string.IsNullOrWhiteSpace(tmResult.Message)
                            ? "启动前统一存档快照已跳过（不阻断修改器启动）。"
                            : tmResult.Message,
                    };
                }

                _logger.LogWarning(
                    "Save Time Machine pre-trainer snapshot returned non-skipped failure for game {GameId}: {Message}; falling back to legacy snapshot engine.",
                    game.GameId,
                    tmResult.Message);
            }
            catch (OperationCanceledException) when (launchToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Save Time Machine pre-trainer snapshot timed out after {TimeoutSeconds}s for game {GameId}; skipping non-blockingly.",
                    _preTrainerSnapshotTimeout.TotalSeconds,
                    game.GameId);
                return new PreTrainerSnapshotResult
                {
                    Succeeded = false,
                    Status = PreTrainerSnapshotStatus.Skipped,
                    SkipReason = PreTrainerSnapshotSkipReason.Timeout,
                    Message = "启动前统一存档快照超时已跳过（不阻断修改器启动）。",
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Save Time Machine pre-trainer snapshot threw for game {GameId}; falling back to legacy snapshot engine.",
                    game.GameId);
            }
        }

        try
        {
            return await _snapshotService
                .CreateAsync(game.GameId, trainer.TrainerId, isManual: false, launchToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-trainer snapshot failed for game {GameId}; continuing launch non-blockingly.", game.GameId);
            return new PreTrainerSnapshotResult
            {
                Succeeded = false,
                Status = PreTrainerSnapshotStatus.Failed,
                Message = $"启动前存档快照异常已跳过：{ex.Message}",
            };
        }
    }

    private static PreTrainerSnapshotSkipReason MapTimeMachineSkipReason(SaveSnapshotSkipReason reason) => reason switch
    {
        SaveSnapshotSkipReason.DisabledBySettings => PreTrainerSnapshotSkipReason.DisabledBySettings,
        SaveSnapshotSkipReason.NoSaveLocationsFound => PreTrainerSnapshotSkipReason.NoSaveLocationsFound,
        SaveSnapshotSkipReason.SizeThresholdExceeded => PreTrainerSnapshotSkipReason.SizeThresholdExceeded,
        SaveSnapshotSkipReason.FileLockedSharingViolation => PreTrainerSnapshotSkipReason.FileLockedSharingViolation,
        SaveSnapshotSkipReason.ConcurrentModificationDetected => PreTrainerSnapshotSkipReason.ConcurrentModificationDetected,
        _ => PreTrainerSnapshotSkipReason.None,
    };

    private (bool IsElevated, bool LaunchedExternally) InspectTargetGameProcess(GameRecord game)
    {
        ActiveGameSession? activeGame = _gameLaunchService.GetActiveSession(game.GameId);
        if (activeGame is not null)
        {
            if (activeGame.RootProcessId > 4 && _elevationAdvisor.IsProcessElevated(activeGame.RootProcessId))
            {
                return (true, false);
            }

            foreach (int pid in activeGame.TrackedProcessIds)
            {
                if (pid > 4 && pid != activeGame.RootProcessId && _elevationAdvisor.IsProcessElevated(pid))
                {
                    return (true, false);
                }
            }

            return (false, false);
        }

        if (string.IsNullOrWhiteSpace(game.InstallDir))
        {
            return (false, false);
        }

        IReadOnlyList<SafeProcessEntry> externalProcesses;
        try
        {
            externalProcesses = _directoryProcessFinder(game.InstallDir);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "External game process fallback inspection failed for {InstallDir}.", game.InstallDir);
            }

            return (false, false);
        }

        if (externalProcesses.Count == 0)
        {
            return (false, false);
        }

        foreach (SafeProcessEntry proc in externalProcesses)
        {
            if (proc.ProcessId > 4 && _elevationAdvisor.IsProcessElevated(proc.ProcessId))
            {
                return (true, true);
            }
        }

        return (false, true);
    }

    private async Task ResetHotkeyProxyStatesSafeAsync(string trainerId)
    {
        if (string.IsNullOrWhiteSpace(trainerId))
        {
            return;
        }

        try
        {
            await _database.ResetTrainerHotkeyProxyStatesAsync(trainerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to reset hotkey proxy states for trainer {TrainerId}.", trainerId);
            }
        }
    }

    private void ResetHotkeyProxyStatesBlockingSafe(string trainerId)
    {
        if (string.IsNullOrWhiteSpace(trainerId))
        {
            return;
        }

        try
        {
            Task.Run(() => ResetHotkeyProxyStatesSafeAsync(trainerId), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to reset hotkey proxy states on natural exit for trainer {TrainerId}.", trainerId);
            }
        }
    }

    private void OnEmergencyStopTriggered(object? sender, TrainerEmergencyStopEvent e)
    {
        CancelInFlightLaunchIfAny();
        TrainerSessionHandle? session = ActiveSession;
        string? sessionId = session?.SessionId;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        await StopInternalAsync(
                            sessionId,
                            TrainerShutdownReason.AntiCheatWatchdogEmergency,
                            e.Reason,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await _backend.ShutdownAllImmediatelyAsync(
                            TrainerShutdownReason.AntiCheatWatchdogEmergency,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Emergency stop handler failed for trainer session.");
                }
            },
            CancellationToken.None);
    }

    private void OnGameSessionChanged(object? sender, ActiveGameSessionChangedEvent e)
    {
        if (e.CurrentState != PlaySessionActivityState.Stopped)
        {
            return;
        }

        TrainerSessionHandle? session;
        TrainerLaunchOptions? options;
        lock (_stateLock)
        {
            session = _activeSession;
            options = _activeOptions;
        }

        if (session is null
            || options is not { StopWithGameExit: true }
            || !string.Equals(session.GameId, e.GameId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string sessionId = session.SessionId;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await StopInternalAsync(
                        sessionId,
                        TrainerShutdownReason.GameExited,
                        "游戏已退出，正在自动停止关联修改器。",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "StopWithGameExit failed for trainer session {SessionId}.", sessionId);
                }
            },
            CancellationToken.None);
    }

    private async Task OnAudioFallbackRequiredAsync(
        IReadOnlyList<int> pids,
        bool mute,
        double volume,
        CancellationToken ct)
    {
        TrainerSessionHandle? session = ActiveSession;
        if (session is null || pids.Count == 0)
        {
            return;
        }

        try
        {
            await _backend.ApplyAudioFallbackAsync(session.SessionId, pids, mute, volume, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Worker audio fallback RPC failed for session {SessionId}.", session.SessionId);
            }
        }
    }

    private void OnBackendProcessCreated(TrainerProcessCreatedNotification notification)
    {
        if (notification.ProcessId > 4)
        {
            _audioController.TrackProcess(notification.ProcessId);
        }
    }

    private void OnBackendSessionEnded(TrainerSessionEndedNotification notification)
    {
        TrainerSessionHandle? cleared = null;
        lock (_stateLock)
        {
            if (_activeSession is not null
                && string.Equals(_activeSession.SessionId, notification.SessionId, StringComparison.Ordinal))
            {
                cleared = _activeSession;
                _activeSession = null;
                _activeOptions = null;
            }
        }

        if (cleared is not null)
        {
            ResetHotkeyProxyStatesBlockingSafe(cleared.TrainerId);
            _safetyGate.NotifyTrainerSessionEnded(cleared.SessionId);
            TransitionState(
                cleared.GameId,
                cleared.TrainerId,
                null,
                TrainerRuntimeState.Ready,
                notification.Detail ?? "修改器进程已退出。");
        }
    }

    private void CancelInFlightLaunchIfAny()
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            cts = _inFlightLaunchCts;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore cancel races.
            }
        }
    }

    private void TransitionState(
        string gameId,
        string? trainerId,
        string? sessionId,
        TrainerRuntimeState nextState,
        string? message)
    {
        TrainerRuntimeState previous;
        lock (_stateLock)
        {
            previous = _state;
            _state = nextState;
        }

        if (previous == nextState && string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StateChanged?.Invoke(
            this,
            new TrainerRuntimeStateChanged
            {
                GameId = gameId,
                TrainerId = trainerId,
                SessionId = sessionId,
                PreviousState = previous,
                CurrentState = nextState,
                Message = message,
                OccurredUtc = DateTimeOffset.UtcNow,
            });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        UnsubscribeEvents();
        CancelInFlightLaunchIfAny();
        _lifecycleLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        UnsubscribeEvents();
        CancelInFlightLaunchIfAny();

        try
        {
            await _backend.ShutdownAllImmediatelyAsync(TrainerShutdownReason.HostShutdownOrUpdate, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown errors during disposal.
        }

        _lifecycleLock.Dispose();
    }

    private void UnsubscribeEvents()
    {
        _safetyGate.EmergencyStopTriggered -= OnEmergencyStopTriggered;
        _gameLaunchService.SessionChanged -= OnGameSessionChanged;
        _audioController.FallbackRequired -= OnAudioFallbackRequiredAsync;

        if (_backend is ITrainerBackendSessionEvents backendEvents)
        {
            backendEvents.ProcessCreated -= OnBackendProcessCreated;
            backendEvents.SessionEnded -= OnBackendSessionEnded;
        }
    }
}
