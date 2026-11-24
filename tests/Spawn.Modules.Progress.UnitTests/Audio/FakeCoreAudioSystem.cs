using System.Collections.Concurrent;
using Spawn.Modules.Progress.Audio;

namespace Spawn.Modules.Progress.UnitTests.Audio;

/// <summary>
/// Deterministic in-memory Core Audio test double capturing thread apartments, exact COM call ordering
/// (<c>RegisterSessionNotification → GetSessionEnumerator → GetCount</c>), sink lifetimes, hot-plug events,
/// and injected HRESULT failures (<c>E_ACCESSDENIED</c>, <c>AUDCLNT_E_DEVICE_INVALIDATED</c>, <c>AUDCLNT_S_NO_SINGLE_PROCESS</c>).
/// </summary>
internal sealed class FakeCoreAudioSystem : ICoreAudioDeviceEnumeratorFactory
{
    public ConcurrentQueue<(string Operation, int ThreadId, ApartmentState Apartment)> CallTrace { get; } = new();
    public FakeCoreAudioDeviceEnumerator Enumerator { get; }

    public FakeCoreAudioSystem()
    {
        Enumerator = new FakeCoreAudioDeviceEnumerator(this);
    }

    public int FactoryCreateCount { get; private set; }
    public ApartmentState? FactoryApartmentState { get; private set; }
    public int? FactoryThreadId { get; private set; }

    public void RecordCall(string operation)
    {
        CallTrace.Enqueue((operation, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
    }

    public ICoreAudioDeviceEnumerator Create()
    {
        FactoryCreateCount++;
        FactoryApartmentState = Thread.CurrentThread.GetApartmentState();
        FactoryThreadId = Environment.CurrentManagedThreadId;
        RecordCall("Factory.Create");
        return Enumerator;
    }
}

internal sealed class FakeCoreAudioDeviceEnumerator : ICoreAudioDeviceEnumerator
{
    private readonly FakeCoreAudioSystem _system;
    private readonly object _sync = new();
    private readonly Dictionary<string, FakeCoreAudioEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public FakeCoreAudioDeviceEnumerator(FakeCoreAudioSystem system)
    {
        _system = system;
    }

    public ICoreAudioEndpointNotificationSink? RegisteredEndpointSink { get; private set; }
    public WeakReference<ICoreAudioEndpointNotificationSink>? RegisteredEndpointSinkWeakRef { get; private set; }
    public int RegisterEndpointCallbackCount { get; private set; }
    public int UnregisterEndpointCallbackCount { get; private set; }
    public bool IsDisposed { get; private set; }

    public FakeCoreAudioEndpoint AddOrUpdateEndpoint(FakeCoreAudioEndpoint endpoint)
    {
        lock (_sync)
        {
            _endpoints[endpoint.DeviceId] = endpoint;
            return endpoint;
        }
    }

    public int RegisterEndpointNotificationCallback(ICoreAudioEndpointNotificationSink sink)
    {
        _system.RecordCall("Enumerator.RegisterEndpointNotificationCallback");
        lock (_sync)
        {
            RegisteredEndpointSink = sink;
            RegisteredEndpointSinkWeakRef = new WeakReference<ICoreAudioEndpointNotificationSink>(sink);
            RegisterEndpointCallbackCount++;
            return CoreAudioHResults.SOk;
        }
    }

    public int UnregisterEndpointNotificationCallback(ICoreAudioEndpointNotificationSink sink)
    {
        _system.RecordCall("Enumerator.UnregisterEndpointNotificationCallback");
        lock (_sync)
        {
            if (ReferenceEquals(RegisteredEndpointSink, sink))
            {
                RegisteredEndpointSink = null;
            }

            UnregisterEndpointCallbackCount++;
            return CoreAudioHResults.SOk;
        }
    }

    public IReadOnlyList<ICoreAudioEndpoint> EnumerateActiveRenderEndpoints()
    {
        _system.RecordCall("Enumerator.EnumerateActiveRenderEndpoints");
        lock (_sync)
        {
            return _endpoints.Values
                .Where(static e => e.DataFlow == CoreAudioDeviceFlow.Render && e.State == CoreAudioDeviceState.Active)
                .Select(e => (ICoreAudioEndpoint)e.CreateHandle())
                .ToList();
        }
    }

    public ICoreAudioEndpoint? TryGetEndpoint(string deviceId)
    {
        _system.RecordCall($"Enumerator.TryGetEndpoint({deviceId})");
        lock (_sync)
        {
            return _endpoints.TryGetValue(deviceId, out FakeCoreAudioEndpoint? endpoint)
                ? endpoint.CreateHandle()
                : null;
        }
    }

    public void RaiseDeviceAdded(string deviceId) => RegisteredEndpointSink?.OnDeviceAdded(deviceId);
    public void RaiseDeviceRemoved(string deviceId) => RegisteredEndpointSink?.OnDeviceRemoved(deviceId);
    public void RaiseDeviceStateChanged(string deviceId, CoreAudioDeviceState newState)
    {
        lock (_sync)
        {
            if (_endpoints.TryGetValue(deviceId, out FakeCoreAudioEndpoint? ep))
            {
                ep.State = newState;
            }
        }

        RegisteredEndpointSink?.OnDeviceStateChanged(deviceId, newState);
    }

    public void RaiseDefaultRenderDeviceChanged(string defaultDeviceId) =>
        RegisteredEndpointSink?.OnDefaultRenderDeviceChanged(defaultDeviceId);

    public void Dispose()
    {
        _system.RecordCall("Enumerator.Dispose");
        IsDisposed = true;
    }
}

internal sealed class FakeCoreAudioEndpoint
{
    private readonly FakeCoreAudioSystem _system;
    private readonly object _sync = new();
    private readonly List<FakeCoreAudioSession> _sessions = [];
    private readonly List<string> _operationLog = [];

    public FakeCoreAudioEndpoint(
        FakeCoreAudioSystem system,
        string deviceId,
        CoreAudioDeviceFlow dataFlow = CoreAudioDeviceFlow.Render,
        CoreAudioDeviceState state = CoreAudioDeviceState.Active)
    {
        _system = system;
        DeviceId = deviceId;
        DataFlow = dataFlow;
        State = state;
    }

    public string DeviceId { get; }
    public CoreAudioDeviceFlow DataFlow { get; }
    public CoreAudioDeviceState State { get; set; }

    public int ActivateResultHResult { get; set; } = CoreAudioHResults.SOk;
    public int GetSessionEnumeratorResultHResult { get; set; } = CoreAudioHResults.SOk;
    public int GetCountResultHResult { get; set; } = CoreAudioHResults.SOk;

    public ICoreAudioSessionNotificationSink? RegisteredSessionSink { get; private set; }
    public WeakReference<ICoreAudioSessionNotificationSink>? RegisteredSessionSinkWeakRef { get; private set; }
    public int RegisterSessionNotificationCount { get; private set; }
    public int UnregisterSessionNotificationCount { get; private set; }
    public int ActiveHandleCount { get; private set; }
    public int DisposedManagerCount { get; private set; }

    public IReadOnlyList<string> OperationLog
    {
        get
        {
            lock (_sync)
            {
                return _operationLog.ToArray();
            }
        }
    }

    public FakeCoreAudioSession AddSession(FakeCoreAudioSession session)
    {
        lock (_sync)
        {
            _sessions.Add(session);
            return session;
        }
    }

    public void RaiseSessionCreated(FakeCoreAudioSession session, bool addToEnumerator = true)
    {
        if (addToEnumerator)
        {
            AddSession(session);
        }

        ICoreAudioSessionNotificationSink? sink;
        lock (_sync)
        {
            sink = RegisteredSessionSink;
        }

        sink?.OnSessionCreated(session.CreateHandle());
    }

    internal EndpointHandle CreateHandle()
    {
        lock (_sync)
        {
            ActiveHandleCount++;
            return new EndpointHandle(this);
        }
    }

    private void RecordOp(string op)
    {
        _system.RecordCall($"Endpoint[{DeviceId}].{op}");
        lock (_sync)
        {
            _operationLog.Add(op);
        }
    }

    internal sealed class EndpointHandle(FakeCoreAudioEndpoint owner) : ICoreAudioEndpoint
    {
        private bool _disposed;

        public string DeviceId => owner.DeviceId;
        public CoreAudioDeviceFlow DataFlow => owner.DataFlow;
        public CoreAudioDeviceState State => owner.State;

        public int ActivateSessionManager(out ICoreAudioSessionManager? sessionManager)
        {
            owner._system.RecordCall($"Endpoint[{owner.DeviceId}].ActivateSessionManager");
            if (owner.ActivateResultHResult < 0)
            {
                sessionManager = null;
                return owner.ActivateResultHResult;
            }

            sessionManager = new SessionManagerHandle(owner);
            return CoreAudioHResults.SOk;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (owner._sync)
            {
                owner.ActiveHandleCount--;
            }
        }
    }

    private sealed class SessionManagerHandle(FakeCoreAudioEndpoint owner) : ICoreAudioSessionManager
    {
        private bool _disposed;

        public int RegisterSessionNotification(ICoreAudioSessionNotificationSink sink)
        {
            owner.RecordOp("RegisterSessionNotification");
            lock (owner._sync)
            {
                owner.RegisteredSessionSink = sink;
                owner.RegisteredSessionSinkWeakRef = new WeakReference<ICoreAudioSessionNotificationSink>(sink);
                owner.RegisterSessionNotificationCount++;
            }

            return CoreAudioHResults.SOk;
        }

        public int UnregisterSessionNotification(ICoreAudioSessionNotificationSink sink)
        {
            owner.RecordOp("UnregisterSessionNotification");
            lock (owner._sync)
            {
                if (ReferenceEquals(owner.RegisteredSessionSink, sink))
                {
                    owner.RegisteredSessionSink = null;
                }

                owner.UnregisterSessionNotificationCount++;
            }

            return CoreAudioHResults.SOk;
        }

        public int GetSessionEnumerator(out ICoreAudioSessionEnumerator? enumerator)
        {
            owner.RecordOp("GetSessionEnumerator");
            if (owner.GetSessionEnumeratorResultHResult < 0)
            {
                enumerator = null;
                return owner.GetSessionEnumeratorResultHResult;
            }

            FakeCoreAudioSession[] snapshot;
            lock (owner._sync)
            {
                snapshot = owner._sessions.ToArray();
            }

            enumerator = new SessionEnumeratorHandle(owner, snapshot);
            return CoreAudioHResults.SOk;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (owner._sync)
            {
                owner.DisposedManagerCount++;
            }
        }
    }

    private sealed class SessionEnumeratorHandle(FakeCoreAudioEndpoint owner, FakeCoreAudioSession[] snapshot)
        : ICoreAudioSessionEnumerator
    {
        public int GetCount(out int count)
        {
            owner.RecordOp("GetCount");
            if (owner.GetCountResultHResult < 0)
            {
                count = 0;
                return owner.GetCountResultHResult;
            }

            count = snapshot.Length;
            return CoreAudioHResults.SOk;
        }

        public int GetSession(int index, out ICoreAudioSession? session)
        {
            if (index < 0 || index >= snapshot.Length)
            {
                session = null;
                return unchecked((int)0x80070057); // E_INVALIDARG
            }

            session = snapshot[index].CreateHandle();
            return CoreAudioHResults.SOk;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class FakeCoreAudioSession
{
    private readonly FakeCoreAudioSystem _system;
    private readonly object _sync = new();
    private readonly List<string> _actions = [];

    public FakeCoreAudioSession(
        FakeCoreAudioSystem system,
        int processId,
        int processInfoHResult = CoreAudioHResults.SOk,
        bool isSystemSounds = false)
    {
        _system = system;
        ProcessId = processId;
        ProcessInfoHResult = processInfoHResult;
        IsSystemSounds = isSystemSounds;
    }

    public int ProcessId { get; }
    public int ProcessInfoHResult { get; set; }
    public bool IsSystemSounds { get; set; }
    public int SetMuteHResult { get; set; } = CoreAudioHResults.SOk;
    public int SetMasterVolumeHResult { get; set; } = CoreAudioHResults.SOk;
    public bool ThrowUnauthorizedOnSetMute { get; set; }

    public bool? LastMute { get; private set; }
    public float? LastVolume { get; private set; }
    public int SetMuteCallCount { get; private set; }
    public int SetMasterVolumeCallCount { get; private set; }
    public int DisposedHandleCount { get; private set; }

    public IReadOnlyList<string> ActionHistory
    {
        get
        {
            lock (_sync)
            {
                return _actions.ToArray();
            }
        }
    }

    public ICoreAudioSession CreateHandle() => new SessionHandle(this);

    private sealed class SessionHandle(FakeCoreAudioSession owner) : ICoreAudioSession
    {
        private bool _disposed;

        public CoreAudioSessionProcessInfo GetProcessInfo()
        {
            owner._system.RecordCall($"Session[{owner.ProcessId}].GetProcessInfo");
            return new CoreAudioSessionProcessInfo(owner.ProcessInfoHResult, owner.ProcessId, owner.IsSystemSounds);
        }

        public int SetMute(bool muted)
        {
            owner._system.RecordCall($"Session[{owner.ProcessId}].SetMute({muted})");
            if (owner.ThrowUnauthorizedOnSetMute)
            {
                throw new UnauthorizedAccessException("Simulated High IL E_ACCESSDENIED.");
            }

            lock (owner._sync)
            {
                owner.SetMuteCallCount++;
                owner._actions.Add($"SetMute({muted})");
                if (owner.SetMuteHResult >= 0)
                {
                    owner.LastMute = muted;
                }
            }

            return owner.SetMuteHResult;
        }

        public int SetMasterVolume(float levelNormalized)
        {
            owner._system.RecordCall($"Session[{owner.ProcessId}].SetMasterVolume({levelNormalized:F2})");
            lock (owner._sync)
            {
                owner.SetMasterVolumeCallCount++;
                owner._actions.Add($"SetMasterVolume({levelNormalized:F2})");
                if (owner.SetMasterVolumeHResult >= 0)
                {
                    owner.LastVolume = levelNormalized;
                }
            }

            return owner.SetMasterVolumeHResult;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (owner._sync)
            {
                owner.DisposedHandleCount++;
            }
        }
    }
}
