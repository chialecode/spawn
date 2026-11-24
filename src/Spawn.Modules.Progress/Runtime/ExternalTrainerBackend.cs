using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Ipc;

namespace Spawn.Modules.Progress.Runtime;

/// <summary>
/// Typed failure raised by <see cref="ExternalTrainerBackend"/> so <see cref="TrainerRuntimeService"/>
/// maps worker launch, UAC cancellation, handshake, quarantine, or Smart App Control errors deterministically.
/// </summary>
public sealed class TrainerWorkerLaunchException : Exception
{
    public TrainerLaunchFailureKind FailureKind { get; }
    public int? Win32ErrorCode { get; }

    public TrainerWorkerLaunchException(
        TrainerLaunchFailureKind failureKind,
        string message,
        int? win32ErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        Win32ErrorCode = win32ErrorCode;
    }
}

/// <summary>
/// Optional event contract implemented by worker-backed <see cref="ITrainerBackend"/> implementations
/// to stream IOCP child-process and natural-exit notifications to <see cref="TrainerRuntimeService"/>.
/// </summary>
public interface ITrainerBackendSessionEvents
{
    event Action<TrainerProcessCreatedNotification>? ProcessCreated;
    event Action<TrainerSessionEndedNotification>? SessionEnded;
    int ActiveWorkerCount { get; }
    IReadOnlyList<int> GetActiveWorkerProcessIds();
}

/// <summary>
/// External single-player trainer execution backend (<c>BackendKind = "ExternalTrainer"</c>) that isolates
/// every trainer session inside a dedicated <c>Spawn.TrainerWorker.exe</c> process over an inverted
/// single-instance cross-IL Named Pipe with mutual PID / path / 256-bit LaunchNonce authentication.
/// </summary>
public sealed class ExternalTrainerBackend : ITrainerBackend, ITrainerBackendSessionEvents
{
    public const string Kind = "ExternalTrainer";

    private readonly SpawnPaths _paths;
    private readonly ITrainerWorkerLauncher _workerLauncher;
    private readonly ITrainerElevationAdvisor _elevationAdvisor;
    private readonly ITrainerPipeServerFactory _pipeServerFactory;
    private readonly ILogger<ExternalTrainerBackend> _logger;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _rpcTimeout;

    private readonly ConcurrentDictionary<string, WorkerSessionContext> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public string BackendKind => Kind;
    public int ActiveWorkerCount => _sessions.Count;

    public event Action<TrainerProcessCreatedNotification>? ProcessCreated;
    public event Action<TrainerSessionEndedNotification>? SessionEnded;

    public ExternalTrainerBackend(
        SpawnPaths paths,
        ITrainerWorkerLauncher? workerLauncher = null,
        ITrainerElevationAdvisor? elevationAdvisor = null,
        ITrainerPipeServerFactory? pipeServerFactory = null,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? rpcTimeout = null,
        ILogger<ExternalTrainerBackend>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _workerLauncher = workerLauncher ?? new Win32TrainerWorkerLauncher();
        _elevationAdvisor = elevationAdvisor ?? new TrainerElevationAdvisor();
        _pipeServerFactory = pipeServerFactory ?? DefaultTrainerPipeServerFactory.Instance;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _rpcTimeout = rpcTimeout ?? TimeSpan.FromSeconds(10);
        _logger = logger ?? NullLogger<ExternalTrainerBackend>.Instance;
    }

    public IReadOnlyList<int> GetActiveWorkerProcessIds() =>
        _sessions.Values.Select(static s => s.WorkerProcessId).Distinct().ToArray();

    public async Task<TrainerSessionHandle> ActivateSessionAsync(
        TrainerLaunchSpec spec,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(spec);

        TrainerElevationLevel elevationLevel = _elevationAdvisor.DetermineElevationLevel(
            spec.RequiresAdmin,
            spec.TargetGameIsElevated);

        string pipeName = _paths.CreateTrainerSessionPipeName();
        using var nonce = SingleUseLaunchNonce.Generate();
        string? resolvedWorkerPath = _workerLauncher.ResolveWorkerExecutablePath();

        TrainerPipeServer server = _pipeServerFactory.CreateServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            LaunchNonce = nonce,
            ExpectedWorkerExecutablePaths = string.IsNullOrWhiteSpace(resolvedWorkerPath) ? null : [resolvedWorkerPath],
        });

        int spawnedWorkerPid = 0;
        bool committed = false;

        try
        {
            // Step 1: Bind single-instance Named Pipe in kernel BEFORE spawning Spawn.TrainerWorker.exe.
            server.StartListening();

            // Register IOCP notification handlers prior to handshake/launch so early NEW_PROCESS events are never missed.
            server.RegisterNotificationHandler<TrainerProcessCreatedNotification>(
                TrainerIpcMethods.ProcessCreated,
                (notification, _) =>
                {
                    if (notification is not null)
                    {
                        ProcessCreated?.Invoke(notification);
                    }

                    return Task.CompletedTask;
                });

            server.RegisterNotificationHandler<TrainerSessionEndedNotification>(
                TrainerIpcMethods.SessionEnded,
                (notification, _) =>
                {
                    if (notification is not null)
                    {
                        HandleRemoteSessionEnded(notification);
                    }

                    return Task.CompletedTask;
                });

            // Step 2: Launch Worker at Level 1 (CreateProcessW) or Level 2 (ShellExecuteExW runas).
            WorkerProcessLaunchResult launch = await _workerLauncher.LaunchWorkerAsync(
                elevationLevel,
                pipeName,
                nonce.EncodedValue,
                ct).ConfigureAwait(false);

            if (!launch.Succeeded)
            {
                TrainerLaunchFailureKind failKind = launch.WasCancelledByUser
                    ? TrainerLaunchFailureKind.UacCancelledByUser
                    : launch.WorkerBinaryMissing
                        ? TrainerLaunchFailureKind.WorkerBinaryMissing
                        : TrainerLaunchFailureKind.WorkerLaunchFailed;

                throw new TrainerWorkerLaunchException(
                    failKind,
                    launch.ErrorMessage ?? "启动 Spawn.TrainerWorker 失败。",
                    launch.Win32ErrorCode);
            }

            spawnedWorkerPid = launch.WorkerProcessId;

            // Step 3: Mutual PID + 0x1000 path + 256-bit single-use LaunchNonce handshake.
            try
            {
                _ = await server.WaitForAuthenticatedClientAsync(
                    spawnedWorkerPid,
                    _handshakeTimeout,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TrainerWorkerLaunchException(
                    TrainerLaunchFailureKind.WorkerHandshakeFailed,
                    $"Spawn.TrainerWorker 管道双向鉴权握手失败：{ex.Message}",
                    innerException: ex);
            }

            // Step 4: Send whitelisted trainer.launch request (SandboxRoot + SandboxRelativePath + pinned SHA-256).
            string workingDir = Path.GetDirectoryName(Path.Combine(spec.SandboxRoot, spec.SandboxRelativePath)) ?? spec.SandboxRoot;
            var launchRequest = new TrainerLaunchRequest
            {
                SessionId = spec.SessionId,
                SandboxRoot = spec.SandboxRoot,
                SandboxRelativePath = spec.SandboxRelativePath,
                Sha256 = spec.ExpectedSha256,
                WorkingDirectory = workingDir,
                HotkeyHoldMs = Math.Clamp(spec.Options.HotkeyHoldMs, TrainerSettingsDto.MinHotkeyHoldMs, TrainerSettingsDto.MaxHotkeyHoldMs),
                ExpectedArchitecture = spec.ExpectedArchitecture,
                HideOriginalWindow = spec.Options.HideOriginalWindow,
                ScanCodeMode = spec.Options.ScanCodeMode,
                StrictVanguardMode = spec.Options.StrictVanguardMode,
                GuardStateFilePath = _paths.AntiCheatGuardStateFilePath,
            };

            TrainerLaunchResponse? launchReply = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
                TrainerIpcMethods.Launch,
                launchRequest,
                _rpcTimeout,
                ct).ConfigureAwait(false);

            if (launchReply is null || !launchReply.Succeeded)
            {
                TrainerLaunchFailureKind kind = MapLaunchFailure(launchReply);
                throw new TrainerWorkerLaunchException(
                    kind,
                    launchReply?.ErrorMessage ?? "Worker 未能启动修改器进程。",
                    launchReply?.Win32ErrorCode);
            }

            var handle = new TrainerSessionHandle
            {
                SessionId = spec.SessionId,
                GameId = spec.GameId,
                TrainerId = spec.TrainerId,
                BackendKind = BackendKind,
                ElevationLevel = elevationLevel,
                WorkerProcessId = spawnedWorkerPid,
                TrainerProcessId = launchReply.TrainerPid,
                StartedAtUtc = DateTimeOffset.UtcNow,
            };

            var ctx = new WorkerSessionContext(spec.SessionId, spawnedWorkerPid, server, handle);
            if (!_sessions.TryAdd(spec.SessionId, ctx))
            {
                throw new TrainerWorkerLaunchException(
                    TrainerLaunchFailureKind.AlreadyRunning,
                    $"修改器会话 '{spec.SessionId}' 已存在。");
            }

            server.Disconnected += () => HandlePipeDisconnected(spec.SessionId);
            committed = true;
            return handle;
        }
        finally
        {
            if (!committed)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (spawnedWorkerPid > 4)
                {
                    _workerLauncher.TryTerminateWorkerProcess(spawnedWorkerPid);
                }
            }
        }
    }

    public Task TriggerEntryAsync(string sessionId, string entryId, bool targetEnabled, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"Backend '{BackendKind}' triggers hotkeys via TriggerHotkeyAsync rather than direct memory entry toggling.");

    public async Task TriggerHotkeyAsync(
        string sessionId,
        TrainerTriggerHotkeyRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        if (!_sessions.TryGetValue(sessionId, out WorkerSessionContext? ctx))
        {
            throw new InvalidOperationException($"未找到活跃修改器会话 '{sessionId}'。");
        }

        TrainerTriggerHotkeyResponse? reply = await ctx.Server.InvokeAsync<TrainerTriggerHotkeyRequest, TrainerTriggerHotkeyResponse>(
            TrainerIpcMethods.TriggerHotkey,
            request,
            _rpcTimeout,
            ct).ConfigureAwait(false);

        if (reply is null || !reply.Succeeded)
        {
            throw new InvalidOperationException(reply?.ErrorMessage ?? "热键代理执行失败。");
        }
    }

    public async Task SetWindowVisibilityAsync(string sessionId, bool visible, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessions.TryGetValue(sessionId, out WorkerSessionContext? ctx))
        {
            return;
        }

        _ = await ctx.Server.InvokeAsync<TrainerSetWindowVisibilityRequest, TrainerSetWindowVisibilityResponse>(
            TrainerIpcMethods.SetWindowVisibility,
            new TrainerSetWindowVisibilityRequest { SessionId = sessionId, Visible = visible },
            _rpcTimeout,
            ct).ConfigureAwait(false);
    }

    public async Task ApplyAudioFallbackAsync(
        string sessionId,
        IReadOnlyList<int> pids,
        bool mute,
        double volume,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (pids.Count == 0 || !_sessions.TryGetValue(sessionId, out WorkerSessionContext? ctx))
        {
            return;
        }

        _ = await ctx.Server.InvokeAsync<TrainerSetAudioFallbackRequest, TrainerSetAudioFallbackResponse>(
            TrainerIpcMethods.SetAudioFallback,
            new TrainerSetAudioFallbackRequest
            {
                SessionId = sessionId,
                Pids = pids,
                Mute = mute,
                Volume = Math.Clamp(volume, 0.0, 1.0),
            },
            _rpcTimeout,
            ct).ConfigureAwait(false);
    }

    public async Task StopSessionAsync(
        string sessionId,
        TrainerShutdownReason reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (!_sessions.TryRemove(sessionId, out WorkerSessionContext? ctx))
        {
            return;
        }

        await ShutdownContextAsync(ctx, reason, ct).ConfigureAwait(false);
    }

    public async Task ShutdownAllImmediatelyAsync(TrainerShutdownReason reason, CancellationToken ct = default)
    {
        var snapshot = _sessions.Keys.ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        var tasks = new List<Task>(snapshot.Length);
        foreach (string sessionId in snapshot)
        {
            if (_sessions.TryRemove(sessionId, out WorkerSessionContext? ctx))
            {
                tasks.Add(ShutdownContextAsync(ctx, reason, ct));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await ShutdownAllImmediatelyAsync(TrainerShutdownReason.HostShutdownOrUpdate, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ShutdownContextAsync(
        WorkerSessionContext ctx,
        TrainerShutdownReason reason,
        CancellationToken ct)
    {
        ctx.MarkStopping();

        bool isEmergency = reason is TrainerShutdownReason.AntiCheatWatchdogEmergency
            or TrainerShutdownReason.ProtectedGamePreFlightPurge
            or TrainerShutdownReason.IntegrityOrSecurityViolation;

        int gracefulMs = isEmergency ? 500 : 1500;
        TimeSpan rpcBudget = isEmergency ? TimeSpan.FromMilliseconds(900) : TimeSpan.FromMilliseconds(2500);

        try
        {
            if (ctx.Server.IsAuthenticated)
            {
                if (isEmergency)
                {
                    _ = await ctx.Server.InvokeAsync<TrainerEmergencyShutdownRequest, TrainerStopResponse>(
                        TrainerIpcMethods.EmergencyShutdown,
                        new TrainerEmergencyShutdownRequest
                        {
                            SessionId = ctx.SessionId,
                            GracefulTimeoutMs = gracefulMs,
                            Reason = reason,
                        },
                        rpcBudget,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    _ = await ctx.Server.InvokeAsync<TrainerStopRequest, TrainerStopResponse>(
                        TrainerIpcMethods.Stop,
                        new TrainerStopRequest
                        {
                            SessionId = ctx.SessionId,
                            GracefulTimeoutMs = gracefulMs,
                            Reason = reason,
                        },
                        rpcBudget,
                        ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Trainer worker RPC shutdown fallback triggered for session {SessionId}.", ctx.SessionId);
            }
        }
        finally
        {
            await ctx.Server.DisposeAsync().ConfigureAwait(false);
            if (ctx.WorkerProcessId > 4 && SafeProcessInspector.IsProcessAlive(ctx.WorkerProcessId))
            {
                _workerLauncher.TryTerminateWorkerProcess(ctx.WorkerProcessId);
            }
        }
    }

    private void HandleRemoteSessionEnded(TrainerSessionEndedNotification notification)
    {
        if (_sessions.TryRemove(notification.SessionId, out WorkerSessionContext? ctx))
        {
            if (ctx.TryMarkEndedNotified())
            {
                _ = Task.Run(() => ctx.Server.DisposeAsync().AsTask(), CancellationToken.None);
                SessionEnded?.Invoke(notification);
            }
        }
    }

    private void HandlePipeDisconnected(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out WorkerSessionContext? ctx))
        {
            if (!ctx.IsStopping && ctx.TryMarkEndedNotified())
            {
                SessionEnded?.Invoke(new TrainerSessionEndedNotification
                {
                    SessionId = sessionId,
                    Reason = TrainerShutdownReason.PipeDisconnected,
                    OccurredUtc = DateTimeOffset.UtcNow,
                });
            }
        }
    }

    private static TrainerLaunchFailureKind MapLaunchFailure(TrainerLaunchResponse? reply)
    {
        if (reply is null)
        {
            return TrainerLaunchFailureKind.WorkerLaunchFailed;
        }

        if (reply.Win32ErrorCode is 225 or 226
            || string.Equals(reply.ErrorCode, "ANTIVIRUS_QUARANTINED", StringComparison.OrdinalIgnoreCase))
        {
            return TrainerLaunchFailureKind.AntivirusQuarantined;
        }

        if (reply.Win32ErrorCode == 4551
            || string.Equals(reply.ErrorCode, "SMART_APP_CONTROL_BLOCKED", StringComparison.OrdinalIgnoreCase))
        {
            return TrainerLaunchFailureKind.SmartAppControlBlocked;
        }

        if (string.Equals(reply.ErrorCode, "ANTI_CHEAT_BLOCKED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reply.ErrorCode, "GUARD_LOCKED", StringComparison.OrdinalIgnoreCase))
        {
            return TrainerLaunchFailureKind.SafetyGateBlocked;
        }

        if (string.Equals(reply.ErrorCode, "SHA256_MISMATCH", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reply.ErrorCode, "PATH_OUTSIDE_SANDBOX", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reply.ErrorCode, "PATH_TRAVERSAL_REJECTED", StringComparison.OrdinalIgnoreCase))
        {
            return TrainerLaunchFailureKind.IntegrityHashMismatch;
        }

        return TrainerLaunchFailureKind.WorkerLaunchFailed;
    }

    private sealed class WorkerSessionContext(
        string sessionId,
        int workerProcessId,
        TrainerPipeServer server,
        TrainerSessionHandle handle)
    {
        private int _stopping;
        private int _endedNotified;

        public string SessionId { get; } = sessionId;
        public int WorkerProcessId { get; } = workerProcessId;
        public TrainerPipeServer Server { get; } = server;
        public TrainerSessionHandle Handle { get; } = handle;
        public bool IsStopping => Volatile.Read(ref _stopping) != 0;

        public void MarkStopping() => Interlocked.Exchange(ref _stopping, 1);
        public bool TryMarkEndedNotified() => Interlocked.Exchange(ref _endedNotified, 1) == 0;
    }
}
