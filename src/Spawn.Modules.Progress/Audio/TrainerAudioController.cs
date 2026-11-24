using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Audio;

/// <summary>
/// Zero-injection dual-layer trainer audio controller (M3-W06):
/// <list type="bullet">
/// <item><description><b>A0</b>: pre-writes FLiNG INI <c>OnLoadMusic</c> and the 27-byte 5.0-second silent <c>TrainerBGM.mid</c> with <c>.spawn_bak</c> backup/restore.</description></item>
/// <item><description><b>A</b>: initializes Windows Core Audio on a dedicated background <see cref="ApartmentState.MTA"/> thread,
/// strictly executes <c>RegisterSessionNotification → GetSessionEnumerator → GetCount()</c> on every active render endpoint,
/// handles endpoint hot-plug via <c>IMMNotificationClient</c>, matches tracked main/child PIDs and <c>0x1000</c> sandbox-prefix paths,
/// and raises <see cref="FallbackRequired"/> when <c>E_ACCESSDENIED (0x80070005)</c> occurs on elevated trainer sessions.</description></item>
/// </list>
/// </summary>
public sealed class TrainerAudioController : ITrainerAudioController
{
    private readonly ITrainerAudioA0Preparer _a0Preparer;
    private readonly ICoreAudioDeviceEnumeratorFactory _enumeratorFactory;
    private readonly Func<int, string?> _processPathResolver;
    private readonly ILogger<TrainerAudioController> _logger;

    private readonly object _queueLock = new();
    private readonly ConcurrentDictionary<int, byte> _trackedPids = new();
    private readonly ConcurrentDictionary<Task, byte> _inFlightFallbackTasks = new();

    // State owned exclusively by the dedicated MTA worker thread.
    private readonly Dictionary<string, AttachedEndpoint> _attachedEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private ICoreAudioDeviceEnumerator? _deviceEnumerator;
    private EndpointNotificationSink? _endpointNotificationSink;
    private TrainerAudioPolicy _currentPolicy = new();
    private string? _normalizedSandboxPrefix;

    private BlockingCollection<Action>? _workQueue;
    private Thread? _mtaThread;
    private TaskCompletionSource? _mtaExitedTcs;

    private volatile bool _callbacksActive;
    private bool _disposed;
    private bool _a0Prepared;

    [ActivatorUtilitiesConstructor]
    public TrainerAudioController(ILogger<TrainerAudioController>? logger = null)
        : this(
            new TrainerAudioA0Preparer(),
            new WindowsCoreAudioDeviceEnumeratorFactory(),
            SafeProcessInspector.TryGetProcessExecutablePath,
            logger)
    {
    }

    public TrainerAudioController(
        ITrainerAudioA0Preparer a0Preparer,
        ICoreAudioDeviceEnumeratorFactory enumeratorFactory,
        Func<int, string?>? processPathResolver = null,
        ILogger<TrainerAudioController>? logger = null)
    {
        _a0Preparer = a0Preparer ?? throw new ArgumentNullException(nameof(a0Preparer));
        _enumeratorFactory = enumeratorFactory ?? throw new ArgumentNullException(nameof(enumeratorFactory));
        _processPathResolver = processPathResolver ?? SafeProcessInspector.TryGetProcessExecutablePath;
        _logger = logger ?? NullLogger<TrainerAudioController>.Instance;
    }

    /// <inheritdoc />
    public event Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>? FallbackRequired;

    /// <summary>Managed thread ID of the active dedicated MTA thread, if started (diagnostic / test inspection).</summary>
    internal int? MtaThreadManagedId
    {
        get
        {
            lock (_queueLock)
            {
                return _mtaThread?.ManagedThreadId;
            }
        }
    }

    /// <inheritdoc />
    public async Task PrepareAsync(TrainerAudioPolicy policy, string sandboxRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        TrainerAudioPolicy normalizedPolicy = NormalizePolicy(policy);
        string? normalizedPrefix = NormalizeSandboxPrefix(sandboxRoot);

        if (normalizedPolicy.EnableA0 && normalizedPolicy.Mode != TrainerAudioMode.Passthrough)
        {
            _ = await _a0Preparer.PrepareAsync(ct).ConfigureAwait(false);
            _a0Prepared = true;
        }

        EnsureMtaWorkerStarted();

        FallbackTrigger? trigger = await InvokeOnMtaThreadAsync(
            () => InitializeOrRefreshOnMtaThread(normalizedPolicy, normalizedPrefix),
            ct).ConfigureAwait(false);

        if (trigger is not null)
        {
            await RaiseFallbackRequiredAsync(trigger, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void TrackProcess(int pid)
    {
        if (pid <= 4)
        {
            return;
        }

        lock (_queueLock)
        {
            if (_disposed)
            {
                return;
            }

            _trackedPids[pid] = 0;

            if (_workQueue is null || _workQueue.IsAddingCompleted || !_callbacksActive)
            {
                return;
            }

            _workQueue.Add(() =>
            {
                if (!_callbacksActive)
                {
                    return;
                }

                FallbackTrigger? trigger = ScanAndApplyPolicyToAllEndpointsOnMtaThread();
                if (trigger is not null)
                {
                    DispatchBackgroundFallback(trigger);
                }
            });
        }
    }

    /// <inheritdoc />
    public async Task ApplyPolicyAsync(TrainerAudioPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        TrainerAudioPolicy normalizedPolicy = NormalizePolicy(policy);

        if (_a0Prepared && !normalizedPolicy.EnableA0)
        {
            await RestoreA0BackupsAsync(ct).ConfigureAwait(false);
        }

        bool mtaRunning;
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            mtaRunning = _workQueue is not null && !_workQueue.IsAddingCompleted;
        }

        if (!mtaRunning)
        {
            _currentPolicy = normalizedPolicy;
            return;
        }

        FallbackTrigger? trigger = await InvokeOnMtaThreadAsync(
            () =>
            {
                _currentPolicy = normalizedPolicy;
                return ScanAndApplyPolicyToAllEndpointsOnMtaThread();
            },
            ct).ConfigureAwait(false);

        if (trigger is not null)
        {
            await RaiseFallbackRequiredAsync(trigger, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task RestoreA0BackupsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _a0Preparer.RestoreBackupsAsync(ct).ConfigureAwait(false);
        _a0Prepared = false;
    }

    /// <summary>
    /// Synchronizes with the dedicated MTA worker queue and any in-flight background fallback notifications.
    /// Intended for deterministic unit test assertions without sleep loops.
    /// </summary>
    internal async Task FlushAsync(CancellationToken ct = default)
    {
        bool canEnqueue;
        lock (_queueLock)
        {
            canEnqueue = !_disposed && _workQueue is not null && !_workQueue.IsAddingCompleted;
        }

        if (canEnqueue)
        {
            _ = await InvokeOnMtaThreadAsync(static () => true, ct).ConfigureAwait(false);
        }

        Task[] pending = _inFlightFallbackTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource? exitTcs = null;
        BlockingCollection<Action>? queueToDispose = null;

        lock (_queueLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _callbacksActive = false;

            if (_workQueue is not null && !_workQueue.IsAddingCompleted)
            {
                _workQueue.Add(TeardownCoreAudioOnMtaThread);
                _workQueue.CompleteAdding();
            }

            exitTcs = _mtaExitedTcs;
            queueToDispose = _workQueue;
        }

        if (exitTcs is not null)
        {
            try
            {
                await exitTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out waiting for trainer audio MTA worker thread teardown.");
            }
        }

        Task[] pendingFallbacks = _inFlightFallbackTasks.Keys.ToArray();
        if (pendingFallbacks.Length > 0)
        {
            try
            {
                await Task.WhenAll(pendingFallbacks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation/timeout during shutdown.
            }
        }

        queueToDispose?.Dispose();
    }

    private void EnsureMtaWorkerStarted()
    {
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mtaThread is not null)
            {
                return;
            }

            var queue = new BlockingCollection<Action>();
            var exitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() => RunMtaLoop(queue, exitTcs))
            {
                IsBackground = true,
                Name = "Spawn.TrainerAudio.MTA",
            };

            // Explicitly initialize the worker thread in the Multi-Threaded Apartment (COINIT_MULTITHREADED).
            thread.SetApartmentState(ApartmentState.MTA);

            _workQueue = queue;
            _mtaExitedTcs = exitTcs;
            _mtaThread = thread;
            _callbacksActive = true;

            thread.Start();
        }
    }

    private void RunMtaLoop(BlockingCollection<Action> queue, TaskCompletionSource exitTcs)
    {
        try
        {
            foreach (Action workItem in queue.GetConsumingEnumerable())
            {
                try
                {
                    workItem();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unhandled exception while executing trainer audio MTA work item.");
                }
            }
        }
        finally
        {
            // Guarantee COM cleanup even if the loop exits unexpectedly.
            TeardownCoreAudioOnMtaThread();
            exitTcs.TrySetResult();
        }
    }

    private Task<T> InvokeOnMtaThreadAsync<T>(Func<T> func, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool enqueued = TryEnqueue(() =>
        {
            try
            {
                T result = func();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(TrainerAudioController)));
        }

        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    private bool TryEnqueue(Action workItem)
    {
        lock (_queueLock)
        {
            if (_disposed || _workQueue is null || _workQueue.IsAddingCompleted)
            {
                return false;
            }

            _workQueue.Add(workItem);
            return true;
        }
    }

    private FallbackTrigger? InitializeOrRefreshOnMtaThread(TrainerAudioPolicy policy, string? normalizedPrefix)
    {
        if (!_callbacksActive)
        {
            return null;
        }

        _currentPolicy = policy;
        _normalizedSandboxPrefix = normalizedPrefix;

        if (_deviceEnumerator is null)
        {
            ICoreAudioDeviceEnumerator enumerator;
            try
            {
                enumerator = _enumeratorFactory.Create();
            }
            catch (COMException ex) when (IsTransientAudioServiceError(ex.HResult))
            {
                _logger.LogWarning(ex, "Windows Audio service unavailable during trainer audio controller initialization (0x{HResult:X8}).", ex.HResult);
                return null;
            }

            _deviceEnumerator = enumerator;

            // Keep a strong reference to the IMMNotificationClient sink for the entire controller lifetime.
            var endpointSink = new EndpointNotificationSink(this);
            _endpointNotificationSink = endpointSink;

            int regHr = _deviceEnumerator.RegisterEndpointNotificationCallback(endpointSink);
            if (regHr < 0)
            {
                _logger.LogWarning("RegisterEndpointNotificationCallback returned HRESULT 0x{HResult:X8}.", regHr);
            }

            IReadOnlyList<ICoreAudioEndpoint> activeRenderEndpoints;
            try
            {
                activeRenderEndpoints = _deviceEnumerator.EnumerateActiveRenderEndpoints();
            }
            catch (COMException ex) when (IsDeviceInvalidatedOrServiceError(ex.HResult))
            {
                _logger.LogWarning(ex, "Failed to enumerate active render endpoints (0x{HResult:X8}).", ex.HResult);
                return null;
            }

            var deniedPids = new HashSet<int>();
            foreach (ICoreAudioEndpoint endpoint in activeRenderEndpoints)
            {
                AttachEndpointOnMtaThread(endpoint, deniedPids);
            }

            return BuildFallbackTrigger(deniedPids, _currentPolicy);
        }

        return ScanAndApplyPolicyToAllEndpointsOnMtaThread();
    }

    private void AttachEndpointOnMtaThread(ICoreAudioEndpoint endpoint, HashSet<int> deniedPids)
    {
        if (!_callbacksActive)
        {
            SafeDispose(endpoint);
            return;
        }

        if (endpoint.DataFlow != CoreAudioDeviceFlow.Render || endpoint.State != CoreAudioDeviceState.Active)
        {
            SafeDispose(endpoint);
            return;
        }

        string deviceId = endpoint.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            SafeDispose(endpoint);
            return;
        }

        if (_attachedEndpoints.TryGetValue(deviceId, out AttachedEndpoint? existing))
        {
            SafeDispose(endpoint);
            ScanEndpointSessionsOnMtaThread(existing, deniedPids);
            return;
        }

        ICoreAudioSessionManager? sessionManager = null;
        try
        {
            int actHr = endpoint.ActivateSessionManager(out sessionManager);
            if (actHr < 0 || sessionManager is null)
            {
                LogIfUnexpectedError("ActivateSessionManager", deviceId, actHr);
                SafeDispose(sessionManager);
                SafeDispose(endpoint);
                return;
            }

            // Retain a strong reference to the session notification sink on the AttachedEndpoint record.
            var sessionSink = new SessionNotificationSink(this, deviceId);

            // CRITICAL Core Audio ordering: RegisterSessionNotification FIRST, BEFORE GetSessionEnumerator and GetCount().
            // Never call RefreshSessions() which unregisters first and opens a blind window.
            int regHr = sessionManager.RegisterSessionNotification(sessionSink);
            if (regHr < 0)
            {
                LogIfUnexpectedError("RegisterSessionNotification", deviceId, regHr);
                SafeDispose(sessionManager);
                SafeDispose(endpoint);
                return;
            }

            var attached = new AttachedEndpoint(deviceId, endpoint, sessionManager, sessionSink);
            _attachedEndpoints[deviceId] = attached;

            // Step 2 & Step 3: GetSessionEnumerator -> GetCount() immediately after RegisterSessionNotification.
            // This both activates Core Audio session notification delivery and catches pre-existing sessions.
            ScanEndpointSessionsOnMtaThread(attached, deniedPids);
        }
        catch (COMException ex) when (IsDeviceInvalidatedOrServiceError(ex.HResult))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Endpoint {DeviceId} invalidated during attachment (0x{HResult:X8}).", deviceId, ex.HResult);
            }

            DetachEndpointOnMtaThread(deviceId);
            if (!_attachedEndpoints.ContainsKey(deviceId))
            {
                SafeDispose(sessionManager);
                SafeDispose(endpoint);
            }
        }
    }

    private FallbackTrigger? ScanAndApplyPolicyToAllEndpointsOnMtaThread()
    {
        if (!_callbacksActive || _attachedEndpoints.Count == 0)
        {
            return null;
        }

        var deniedPids = new HashSet<int>();
        string[] endpointIds = _attachedEndpoints.Keys.ToArray();
        foreach (string deviceId in endpointIds)
        {
            if (!_attachedEndpoints.TryGetValue(deviceId, out AttachedEndpoint? attached))
            {
                continue;
            }

            ScanEndpointSessionsOnMtaThread(attached, deniedPids);
        }

        return BuildFallbackTrigger(deniedPids, _currentPolicy);
    }

    private void ScanEndpointSessionsOnMtaThread(AttachedEndpoint attached, HashSet<int> deniedPids)
    {
        ICoreAudioSessionEnumerator? enumerator = null;
        try
        {
            int enumHr = attached.SessionManager.GetSessionEnumerator(out enumerator);
            if (enumHr == CoreAudioHResults.DeviceInvalidated)
            {
                DetachEndpointOnMtaThread(attached.DeviceId);
                return;
            }

            if (enumHr < 0 || enumerator is null)
            {
                LogIfUnexpectedError("GetSessionEnumerator", attached.DeviceId, enumHr);
                return;
            }

            int countHr = enumerator.GetCount(out int count);
            if (countHr == CoreAudioHResults.DeviceInvalidated)
            {
                DetachEndpointOnMtaThread(attached.DeviceId);
                return;
            }

            if (countHr < 0 || count <= 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                int getHr = enumerator.GetSession(i, out ICoreAudioSession? session);
                if (getHr == CoreAudioHResults.DeviceInvalidated)
                {
                    SafeDispose(session);
                    DetachEndpointOnMtaThread(attached.DeviceId);
                    return;
                }

                if (getHr < 0 || session is null)
                {
                    SafeDispose(session);
                    continue;
                }

                try
                {
                    bool invalidated = EvaluateAndApplySessionOnMtaThread(session, deniedPids);
                    if (invalidated)
                    {
                        DetachEndpointOnMtaThread(attached.DeviceId);
                        return;
                    }
                }
                finally
                {
                    SafeDispose(session);
                }
            }
        }
        catch (COMException ex) when (ex.HResult == CoreAudioHResults.DeviceInvalidated)
        {
            DetachEndpointOnMtaThread(attached.DeviceId);
        }
        finally
        {
            SafeDispose(enumerator);
        }
    }

    /// <summary>
    /// Evaluates a single audio session against tracked PIDs and the sandbox root prefix, applying the active policy.
    /// Returns <see langword="true"/> if the endpoint became invalidated (<c>AUDCLNT_E_DEVICE_INVALIDATED</c>).
    /// </summary>
    private bool EvaluateAndApplySessionOnMtaThread(ICoreAudioSession session, HashSet<int> deniedPids)
    {
        if (_currentPolicy.Mode == TrainerAudioMode.Passthrough)
        {
            return false;
        }

        CoreAudioSessionProcessInfo info;
        try
        {
            info = session.GetProcessInfo();
        }
        catch (COMException ex) when (ex.HResult == CoreAudioHResults.DeviceInvalidated)
        {
            return true;
        }
        catch (COMException)
        {
            return false;
        }

        if (info.IsDeviceInvalidated)
        {
            return true;
        }

        // Safely ignore AUDCLNT_S_NO_SINGLE_PROCESS (0x0889000D) and Windows System Sounds sessions.
        if (!info.Succeeded || info.IsNoSingleProcess || info.IsSystemSoundsSession)
        {
            return false;
        }

        int pid = info.ProcessId;
        if (!IsTargetProcess(pid))
        {
            return false;
        }

        return ApplyPolicyToMatchedSessionOnMtaThread(session, pid, deniedPids);
    }

    private bool ApplyPolicyToMatchedSessionOnMtaThread(
        ICoreAudioSession session,
        int pid,
        HashSet<int> deniedPids)
    {
        if (_currentPolicy.Mode == TrainerAudioMode.FullMute)
        {
            int hr = InvokeVolumeOperation(() => session.SetMute(true));
            if (hr == CoreAudioHResults.DeviceInvalidated)
            {
                return true;
            }

            if (hr == CoreAudioHResults.AccessDenied)
            {
                deniedPids.Add(pid);
            }

            return false;
        }

        if (_currentPolicy.Mode == TrainerAudioMode.KeepCuesExperimental)
        {
            float cueVolume = (float)Math.Clamp(_currentPolicy.CueVolume, 0.0, 1.0);

            // Set volume before unmuting to avoid an audible full-volume transient.
            int volHr = InvokeVolumeOperation(() => session.SetMasterVolume(cueVolume));
            if (volHr == CoreAudioHResults.DeviceInvalidated)
            {
                return true;
            }

            if (volHr == CoreAudioHResults.AccessDenied)
            {
                deniedPids.Add(pid);
                return false;
            }

            int muteHr = InvokeVolumeOperation(() => session.SetMute(false));
            if (muteHr == CoreAudioHResults.DeviceInvalidated)
            {
                return true;
            }

            if (muteHr == CoreAudioHResults.AccessDenied)
            {
                deniedPids.Add(pid);
            }
        }

        return false;
    }

    private static int InvokeVolumeOperation(Func<int> operation)
    {
        try
        {
            return operation();
        }
        catch (UnauthorizedAccessException)
        {
            return CoreAudioHResults.AccessDenied;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    private bool IsTargetProcess(int pid)
    {
        if (pid <= 4)
        {
            return false;
        }

        if (_trackedPids.ContainsKey(pid))
        {
            return true;
        }

        if (string.IsNullOrEmpty(_normalizedSandboxPrefix))
        {
            return false;
        }

        string? executablePath;
        try
        {
            executablePath = _processPathResolver(pid);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            string normalizedExePath = Path.GetFullPath(executablePath);
            return normalizedExePath.StartsWith(_normalizedSandboxPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void HandleDeviceAddedOrActivatedOnMtaThread(string deviceId)
    {
        if (!_callbacksActive || _deviceEnumerator is null || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        if (_attachedEndpoints.TryGetValue(deviceId, out AttachedEndpoint? existing))
        {
            var existingDenied = new HashSet<int>();
            ScanEndpointSessionsOnMtaThread(existing, existingDenied);
            FallbackTrigger? existingTrigger = BuildFallbackTrigger(existingDenied, _currentPolicy);
            if (existingTrigger is not null)
            {
                DispatchBackgroundFallback(existingTrigger);
            }

            return;
        }

        ICoreAudioEndpoint? endpoint = null;
        try
        {
            endpoint = _deviceEnumerator.TryGetEndpoint(deviceId);
        }
        catch (COMException ex) when (IsDeviceInvalidatedOrServiceError(ex.HResult))
        {
            return;
        }

        if (endpoint is null)
        {
            return;
        }

        var deniedPids = new HashSet<int>();
        AttachEndpointOnMtaThread(endpoint, deniedPids);
        FallbackTrigger? trigger = BuildFallbackTrigger(deniedPids, _currentPolicy);
        if (trigger is not null)
        {
            DispatchBackgroundFallback(trigger);
        }
    }

    private void HandleDeviceStateChangedOnMtaThread(string deviceId, CoreAudioDeviceState newState)
    {
        if (!_callbacksActive || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        if (newState == CoreAudioDeviceState.Active)
        {
            HandleDeviceAddedOrActivatedOnMtaThread(deviceId);
        }
        else
        {
            DetachEndpointOnMtaThread(deviceId);
        }
    }

    private void HandleSessionCreatedOnMtaThread(string deviceId, ICoreAudioSession session)
    {
        try
        {
            if (!_callbacksActive)
            {
                return;
            }

            var deniedPids = new HashSet<int>();
            bool invalidated = EvaluateAndApplySessionOnMtaThread(session, deniedPids);
            if (invalidated)
            {
                DetachEndpointOnMtaThread(deviceId);
                return;
            }

            FallbackTrigger? trigger = BuildFallbackTrigger(deniedPids, _currentPolicy);
            if (trigger is not null)
            {
                DispatchBackgroundFallback(trigger);
            }
        }
        finally
        {
            SafeDispose(session);
        }
    }

    private void DetachEndpointOnMtaThread(string deviceId)
    {
        if (!_attachedEndpoints.Remove(deviceId, out AttachedEndpoint? attached))
        {
            return;
        }

        try
        {
            _ = attached.SessionManager.UnregisterSessionNotification(attached.NotificationSink);
        }
        catch
        {
            // Ignore AUDCLNT_E_DEVICE_INVALIDATED or teardown COM errors.
        }

        SafeDispose(attached.SessionManager);
        SafeDispose(attached.Endpoint);
    }

    private void TeardownCoreAudioOnMtaThread()
    {
        if (_deviceEnumerator is not null && _endpointNotificationSink is not null)
        {
            try
            {
                _ = _deviceEnumerator.UnregisterEndpointNotificationCallback(_endpointNotificationSink);
            }
            catch
            {
                // Best-effort unregister during shutdown.
            }
        }

        _endpointNotificationSink = null;

        string[] ids = _attachedEndpoints.Keys.ToArray();
        foreach (string id in ids)
        {
            DetachEndpointOnMtaThread(id);
        }

        SafeDispose(_deviceEnumerator);
        _deviceEnumerator = null;
    }

    private void DispatchBackgroundFallback(FallbackTrigger trigger)
    {
        if (!_callbacksActive)
        {
            return;
        }

        Task task = Task.Run(async () =>
        {
            if (!_callbacksActive)
            {
                return;
            }

            await RaiseFallbackRequiredAsync(trigger, CancellationToken.None).ConfigureAwait(false);
        });

        _inFlightFallbackTasks[task] = 0;
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var map = (ConcurrentDictionary<Task, byte>)state!;
                map.TryRemove(completed, out _);
            },
            _inFlightFallbackTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RaiseFallbackRequiredAsync(FallbackTrigger trigger, CancellationToken ct)
    {
        Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>? handler = FallbackRequired;
        if (handler is null || !_callbacksActive)
        {
            return;
        }

        foreach (Delegate d in handler.GetInvocationList())
        {
            if (!_callbacksActive)
            {
                break;
            }

            try
            {
                var typed = (Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>)d;
                await typed(trigger.Pids, trigger.Mute, trigger.Volume, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FallbackRequired subscriber threw while handling E_ACCESSDENIED for trainer audio.");
            }
        }
    }

    private static FallbackTrigger? BuildFallbackTrigger(HashSet<int> deniedPids, TrainerAudioPolicy policy)
    {
        if (deniedPids.Count == 0 || policy.Mode == TrainerAudioMode.Passthrough)
        {
            return null;
        }

        int[] ordered = deniedPids.OrderBy(static p => p).ToArray();
        bool mute = policy.Mode == TrainerAudioMode.FullMute;
        double volume = mute ? 0.0 : Math.Clamp(policy.CueVolume, 0.0, 1.0);
        return new FallbackTrigger(ordered, mute, volume);
    }

    private static TrainerAudioPolicy NormalizePolicy(TrainerAudioPolicy policy) =>
        policy with
        {
            CueVolume = Math.Clamp(policy.CueVolume, 0.0, 1.0),
        };

    private static string? NormalizeSandboxPrefix(string? sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot))
        {
            return null;
        }

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot));
        return fullPath + Path.DirectorySeparatorChar;
    }

    private void ThrowIfDisposed()
    {
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private void LogIfUnexpectedError(string operation, string deviceId, int hr)
    {
        if (hr != CoreAudioHResults.DeviceInvalidated && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Core Audio {Operation} on endpoint {DeviceId} returned HRESULT 0x{HResult:X8}.", operation, deviceId, hr);
        }
    }

    private static bool IsTransientAudioServiceError(int hr) =>
        hr == CoreAudioHResults.ServiceNotRunning
        || hr == unchecked((int)0x800706BA) // RPC_S_SERVER_UNAVAILABLE
        || hr == unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG

    private static bool IsDeviceInvalidatedOrServiceError(int hr) =>
        hr == CoreAudioHResults.DeviceInvalidated
        || hr == CoreAudioHResults.ElementNotFound
        || IsTransientAudioServiceError(hr);

    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Ignore disposal errors on invalidated COM objects.
        }
    }

    private sealed record FallbackTrigger(IReadOnlyList<int> Pids, bool Mute, double Volume);

    private sealed record AttachedEndpoint(
        string DeviceId,
        ICoreAudioEndpoint Endpoint,
        ICoreAudioSessionManager SessionManager,
        SessionNotificationSink NotificationSink);

    private sealed class EndpointNotificationSink(TrainerAudioController owner) : ICoreAudioEndpointNotificationSink
    {
        public void OnDeviceAdded(string deviceId)
        {
            if (!owner._callbacksActive || string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            _ = owner.TryEnqueue(() => owner.HandleDeviceAddedOrActivatedOnMtaThread(deviceId));
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (!owner._callbacksActive || string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            _ = owner.TryEnqueue(() => owner.DetachEndpointOnMtaThread(deviceId));
        }

        public void OnDeviceStateChanged(string deviceId, CoreAudioDeviceState newState)
        {
            if (!owner._callbacksActive || string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            _ = owner.TryEnqueue(() => owner.HandleDeviceStateChangedOnMtaThread(deviceId, newState));
        }

        public void OnDefaultRenderDeviceChanged(string defaultDeviceId)
        {
            if (!owner._callbacksActive || string.IsNullOrWhiteSpace(defaultDeviceId))
            {
                return;
            }

            _ = owner.TryEnqueue(() => owner.HandleDeviceAddedOrActivatedOnMtaThread(defaultDeviceId));
        }
    }

    private sealed class SessionNotificationSink(TrainerAudioController owner, string deviceId)
        : ICoreAudioSessionNotificationSink
    {
        public void OnSessionCreated(ICoreAudioSession session)
        {
            if (!owner._callbacksActive)
            {
                SafeDispose(session);
                return;
            }

            bool enqueued = owner.TryEnqueue(() => owner.HandleSessionCreatedOnMtaThread(deviceId, session));
            if (!enqueued)
            {
                SafeDispose(session);
            }
        }
    }
}
