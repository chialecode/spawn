using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Spawn.Modules.Progress.Audio;

/// <summary>
/// Production <see cref="ICoreAudioDeviceEnumeratorFactory"/> backed by Windows Core Audio COM interfaces
/// (<c>MMDeviceEnumerator</c>, <c>IMMDevice</c>, <c>IAudioSessionManager2</c>, <c>ISimpleAudioVolume</c>)
/// and NAudio's public COM callback contracts (<see cref="IMMNotificationClient"/>, <see cref="IAudioSessionNotification"/>).
/// Never constructs NAudio's <c>AudioSessionManager</c> or invokes <c>RefreshSessions()</c>.
/// </summary>
public sealed class WindowsCoreAudioDeviceEnumeratorFactory : ICoreAudioDeviceEnumeratorFactory
{
    public ICoreAudioDeviceEnumerator Create() => new WindowsCoreAudioDeviceEnumerator();
}

internal sealed class WindowsCoreAudioDeviceEnumerator : ICoreAudioDeviceEnumerator
{
    private readonly IMMDeviceEnumeratorCom _enumerator;
    private readonly Dictionary<ICoreAudioEndpointNotificationSink, EndpointNotificationAdapter> _adapters = new();
    private bool _disposed;

    public WindowsCoreAudioDeviceEnumerator()
    {
        _enumerator = (IMMDeviceEnumeratorCom)(object)new MMDeviceEnumeratorComObject();
    }

    public int RegisterEndpointNotificationCallback(ICoreAudioEndpointNotificationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_adapters.TryGetValue(sink, out EndpointNotificationAdapter? existing))
        {
            return _enumerator.RegisterEndpointNotificationCallback(existing);
        }

        // Keep a strong reference to the COM callback adapter until unregistered to prevent premature GC.
        var adapter = new EndpointNotificationAdapter(sink);
        int hr = _enumerator.RegisterEndpointNotificationCallback(adapter);
        if (hr >= 0)
        {
            _adapters[sink] = adapter;
        }

        return hr;
    }

    public int UnregisterEndpointNotificationCallback(ICoreAudioEndpointNotificationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_disposed || !_adapters.Remove(sink, out EndpointNotificationAdapter? adapter))
        {
            return CoreAudioHResults.SOk;
        }

        return _enumerator.UnregisterEndpointNotificationCallback(adapter);
    }

    public IReadOnlyList<ICoreAudioEndpoint> EnumerateActiveRenderEndpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = _enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active, out IMMDeviceCollectionCom? collection);
        if (hr < 0 || collection is null)
        {
            return [];
        }

        var endpoints = new List<ICoreAudioEndpoint>();
        try
        {
            if (collection.GetCount(out int count) < 0 || count <= 0)
            {
                return endpoints;
            }

            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDeviceCom? device) >= 0 && device is not null)
                {
                    WindowsCoreAudioEndpoint? wrapped = WindowsCoreAudioEndpoint.TryCreate(device);
                    if (wrapped is not null)
                    {
                        endpoints.Add(wrapped);
                    }
                    else
                    {
                        ComObjectHelper.ReleaseSafe(device);
                    }
                }
            }
        }
        finally
        {
            ComObjectHelper.ReleaseSafe(collection);
        }

        return endpoints;
    }

    public ICoreAudioEndpoint? TryGetEndpoint(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        int hr = _enumerator.GetDevice(deviceId, out IMMDeviceCom? device);
        if (hr < 0 || device is null)
        {
            return null;
        }

        WindowsCoreAudioEndpoint? wrapped = WindowsCoreAudioEndpoint.TryCreate(device);
        if (wrapped is null)
        {
            ComObjectHelper.ReleaseSafe(device);
        }

        return wrapped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (EndpointNotificationAdapter adapter in _adapters.Values)
        {
            try
            {
                _ = _enumerator.UnregisterEndpointNotificationCallback(adapter);
            }
            catch
            {
                // Best-effort unregister on shutdown.
            }
        }

        _adapters.Clear();
        ComObjectHelper.ReleaseSafe(_enumerator);
    }

    private sealed class EndpointNotificationAdapter(ICoreAudioEndpointNotificationSink sink)
        : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                sink.OnDeviceStateChanged(deviceId, (CoreAudioDeviceState)(int)newState);
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            if (!string.IsNullOrWhiteSpace(pwstrDeviceId))
            {
                sink.OnDeviceAdded(pwstrDeviceId);
            }
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                sink.OnDeviceRemoved(deviceId);
            }
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && !string.IsNullOrWhiteSpace(defaultDeviceId))
            {
                sink.OnDefaultRenderDeviceChanged(defaultDeviceId);
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Property metadata updates do not alter active session routing.
        }
    }
}

internal sealed class WindowsCoreAudioEndpoint : ICoreAudioEndpoint
{
    private const uint ClsCtxAll = 0x17;
    private static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private readonly IMMDeviceCom _device;
    private bool _disposed;

    private WindowsCoreAudioEndpoint(
        IMMDeviceCom device,
        string deviceId,
        CoreAudioDeviceFlow dataFlow,
        CoreAudioDeviceState state)
    {
        _device = device;
        DeviceId = deviceId;
        DataFlow = dataFlow;
        State = state;
    }

    public string DeviceId { get; }
    public CoreAudioDeviceFlow DataFlow { get; }
    public CoreAudioDeviceState State { get; }

    public static WindowsCoreAudioEndpoint? TryCreate(IMMDeviceCom device)
    {
        if (device.GetId(out string? id) < 0 || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        CoreAudioDeviceState state = CoreAudioDeviceState.Active;
        if (device.GetState(out DeviceState rawState) >= 0)
        {
            state = (CoreAudioDeviceState)(int)rawState;
        }

        CoreAudioDeviceFlow flow = CoreAudioDeviceFlow.Render;
        if (device is IMMEndpointCom endpoint && endpoint.GetDataFlow(out DataFlow rawFlow) >= 0)
        {
            flow = (CoreAudioDeviceFlow)(int)rawFlow;
        }

        return new WindowsCoreAudioEndpoint(device, id, flow, state);
    }

    public int ActivateSessionManager(out ICoreAudioSessionManager? sessionManager)
    {
        sessionManager = null;
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = _device.Activate(IidAudioSessionManager2, ClsCtxAll, nint.Zero, out object? rawObject);
        if (hr < 0 || rawObject is null)
        {
            return hr < 0 ? hr : unchecked((int)0x80004002); // E_NOINTERFACE
        }

        if (rawObject is IAudioSessionManager2Com comManager)
        {
            sessionManager = new WindowsCoreAudioSessionManager(comManager);
            return hr;
        }

        ComObjectHelper.ReleaseSafe(rawObject);
        return unchecked((int)0x80004002);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComObjectHelper.ReleaseSafe(_device);
    }
}

internal sealed class WindowsCoreAudioSessionManager : ICoreAudioSessionManager
{
    private readonly IAudioSessionManager2Com _manager;
    private readonly Dictionary<ICoreAudioSessionNotificationSink, SessionNotificationAdapter> _adapters = new();
    private bool _disposed;

    public WindowsCoreAudioSessionManager(IAudioSessionManager2Com manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public int RegisterSessionNotification(ICoreAudioSessionNotificationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_adapters.TryGetValue(sink, out SessionNotificationAdapter? existing))
        {
            return _manager.RegisterSessionNotification(existing);
        }

        // Retain strong reference to prevent GC of COM callback sink while registered.
        var adapter = new SessionNotificationAdapter(sink);
        int hr = _manager.RegisterSessionNotification(adapter);
        if (hr >= 0)
        {
            _adapters[sink] = adapter;
        }

        return hr;
    }

    public int UnregisterSessionNotification(ICoreAudioSessionNotificationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_disposed || !_adapters.Remove(sink, out SessionNotificationAdapter? adapter))
        {
            return CoreAudioHResults.SOk;
        }

        return _manager.UnregisterSessionNotification(adapter);
    }

    public int GetSessionEnumerator(out ICoreAudioSessionEnumerator? enumerator)
    {
        enumerator = null;
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = _manager.GetSessionEnumerator(out IAudioSessionEnumeratorCom? comEnumerator);
        if (hr < 0 || comEnumerator is null)
        {
            return hr;
        }

        enumerator = new WindowsCoreAudioSessionEnumerator(comEnumerator);
        return hr;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SessionNotificationAdapter adapter in _adapters.Values)
        {
            try
            {
                _ = _manager.UnregisterSessionNotification(adapter);
            }
            catch
            {
                // Ignore AUDCLNT_E_DEVICE_INVALIDATED or transient COM errors on shutdown.
            }
        }

        _adapters.Clear();
        ComObjectHelper.ReleaseSafe(_manager);
    }

    private sealed class SessionNotificationAdapter(ICoreAudioSessionNotificationSink sink)
        : IAudioSessionNotification
    {
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            if (newSession is null)
            {
                return CoreAudioHResults.SOk;
            }

            try
            {
                sink.OnSessionCreated(new WindowsCoreAudioSession(newSession));
            }
            catch
            {
                // Never propagate managed exceptions across COM callback boundaries.
                ComObjectHelper.ReleaseSafe(newSession);
            }

            return CoreAudioHResults.SOk;
        }
    }
}

internal sealed class WindowsCoreAudioSessionEnumerator : ICoreAudioSessionEnumerator
{
    private readonly IAudioSessionEnumeratorCom _enumerator;
    private bool _disposed;

    public WindowsCoreAudioSessionEnumerator(IAudioSessionEnumeratorCom enumerator)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
    }

    public int GetCount(out int count)
    {
        count = 0;
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _enumerator.GetCount(out count);
    }

    public int GetSession(int index, out ICoreAudioSession? session)
    {
        session = null;
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = _enumerator.GetSession(index, out IAudioSessionControl? control);
        if (hr < 0 || control is null)
        {
            return hr;
        }

        session = new WindowsCoreAudioSession(control);
        return hr;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComObjectHelper.ReleaseSafe(_enumerator);
    }
}

internal sealed class WindowsCoreAudioSession : ICoreAudioSession
{
    private static readonly Guid EmptyEventContext = Guid.Empty;

    private readonly IAudioSessionControl _sessionControl;
    private bool _disposed;

    public WindowsCoreAudioSession(IAudioSessionControl sessionControl)
    {
        _sessionControl = sessionControl ?? throw new ArgumentNullException(nameof(sessionControl));
    }

    public CoreAudioSessionProcessInfo GetProcessInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sessionControl is not IAudioSessionControl2 control2)
        {
            return new CoreAudioSessionProcessInfo(unchecked((int)0x80004002), 0, false);
        }

        try
        {
            int sysHr = control2.IsSystemSoundsSession();
            bool isSystemSounds = sysHr == CoreAudioHResults.SOk;

            int pidHr = control2.GetProcessId(out uint rawPid);
            return new CoreAudioSessionProcessInfo(pidHr, unchecked((int)rawPid), isSystemSounds);
        }
        catch (COMException ex)
        {
            return new CoreAudioSessionProcessInfo(ex.HResult, 0, false);
        }
    }

    public int SetMute(bool muted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sessionControl is not ISimpleAudioVolumeCom volume)
        {
            return unchecked((int)0x80004002);
        }

        try
        {
            return volume.SetMute(muted, in EmptyEventContext);
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

    public int SetMasterVolume(float levelNormalized)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sessionControl is not ISimpleAudioVolumeCom volume)
        {
            return unchecked((int)0x80004002);
        }

        try
        {
            float clamped = Math.Clamp(levelNormalized, 0.0f, 1.0f);
            return volume.SetMasterVolume(clamped, in EmptyEventContext);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComObjectHelper.ReleaseSafe(_sessionControl);
    }
}

internal static class ComObjectHelper
{
    public static void ReleaseSafe(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                _ = Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // Best-effort COM release.
        }
    }
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorCom
{
    [PreserveSig]
    int EnumAudioEndpoints(
        DataFlow dataFlow,
        DeviceState stateMask,
        out IMMDeviceCollectionCom? devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(
        DataFlow dataFlow,
        Role role,
        out IMMDeviceCom? endpoint);

    [PreserveSig]
    int GetDevice(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        out IMMDeviceCom? device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollectionCom
{
    [PreserveSig]
    int GetCount(out int numDevices);

    [PreserveSig]
    int Item(int deviceNumber, out IMMDeviceCom? device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCom
{
    [PreserveSig]
    int Activate(
        in Guid iid,
        uint dwClsCtx,
        nint activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out nint properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[ComImport]
[Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMEndpointCom
{
    [PreserveSig]
    int GetDataFlow(out DataFlow dataFlow);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2Com
{
    // Inherited from IAudioSessionManager
    [PreserveSig]
    int GetAudioSessionControl(
        in Guid audioSessionGuid,
        uint streamFlags,
        out IAudioSessionControl? sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(
        in Guid audioSessionGuid,
        uint streamFlags,
        out ISimpleAudioVolumeCom? audioVolume);

    // IAudioSessionManager2
    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumeratorCom? sessionEnum);

    [PreserveSig]
    int RegisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification(
        [MarshalAs(UnmanagedType.LPWStr)] string sessionId,
        nint duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(nint duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumeratorCom
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionIndex, out IAudioSessionControl? session);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolumeCom
{
    [PreserveSig]
    int SetMasterVolume(float levelNorm, in Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float levelNorm);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, in Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
}
