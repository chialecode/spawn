namespace Spawn.Modules.Progress.Audio;

/// <summary>
/// Relevant Windows Core Audio / WASAPI COM HRESULT codes handled explicitly by <see cref="TrainerAudioController"/>.
/// </summary>
public static class CoreAudioHResults
{
    public const int SOk = 0x00000000;
    public const int SFalse = 0x00000001;

    /// <summary>Returned when a Medium IL host attempts to modify a High IL process audio session.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>Returned when an audio endpoint is unplugged, disabled, or invalidated mid-operation.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>
    /// Positive success HRESULT (<c>AUDCLNT_S_NO_SINGLE_PROCESS</c>) indicating a cross-process or shared audio session
    /// that does not belong to a single process.
    /// </summary>
    public const int NoSingleProcess = 0x0889000D;

    /// <summary>Windows Audio service (<c>Audiosrv</c>) is temporarily unavailable.</summary>
    public const int ServiceNotRunning = unchecked((int)0x88890010);

    /// <summary>Requested endpoint ID was not found.</summary>
    public const int ElementNotFound = unchecked((int)0x80070490);
}

/// <summary>
/// Data-flow direction of a Core Audio endpoint.
/// </summary>
public enum CoreAudioDeviceFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

/// <summary>
/// Device state flags matching Windows Core Audio <c>DEVICE_STATE_*</c>.
/// </summary>
[Flags]
public enum CoreAudioDeviceState
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
}

/// <summary>
/// Result of querying <c>IAudioSessionControl2.GetProcessId</c> and <c>IsSystemSoundsSession</c>.
/// </summary>
public readonly record struct CoreAudioSessionProcessInfo(
    int HResult,
    int ProcessId,
    bool IsSystemSoundsSession)
{
    public bool Succeeded => HResult >= 0;
    public bool IsNoSingleProcess => HResult == CoreAudioHResults.NoSingleProcess;
    public bool IsDeviceInvalidated => HResult == CoreAudioHResults.DeviceInvalidated;
}

/// <summary>
/// Managed sink bridged to COM <c>IMMNotificationClient</c> for dynamic endpoint hot-plug handling.
/// </summary>
public interface ICoreAudioEndpointNotificationSink
{
    void OnDeviceAdded(string deviceId);
    void OnDeviceRemoved(string deviceId);
    void OnDeviceStateChanged(string deviceId, CoreAudioDeviceState newState);
    void OnDefaultRenderDeviceChanged(string defaultDeviceId);
}

/// <summary>
/// Managed sink bridged to COM <c>IAudioSessionNotification</c> for real-time session creation callbacks.
/// </summary>
public interface ICoreAudioSessionNotificationSink
{
    void OnSessionCreated(ICoreAudioSession session);
}

/// <summary>
/// Injectable abstraction over a single WASAPI audio session (<c>IAudioSessionControl2</c> + <c>ISimpleAudioVolume</c>).
/// </summary>
public interface ICoreAudioSession : IDisposable
{
    CoreAudioSessionProcessInfo GetProcessInfo();
    int SetMute(bool muted);
    int SetMasterVolume(float levelNormalized);
}

/// <summary>
/// Injectable abstraction over <c>IAudioSessionEnumerator</c>.
/// </summary>
public interface ICoreAudioSessionEnumerator : IDisposable
{
    int GetCount(out int count);
    int GetSession(int index, out ICoreAudioSession? session);
}

/// <summary>
/// Injectable abstraction over <c>IAudioSessionManager2</c>.
/// Intentionally exposes only <see cref="RegisterSessionNotification"/>, <see cref="UnregisterSessionNotification"/>,
/// and <see cref="GetSessionEnumerator"/> (never NAudio's <c>RefreshSessions()</c>) so callers strictly enforce
/// <c>RegisterSessionNotification → GetSessionEnumerator → GetCount()</c>.
/// </summary>
public interface ICoreAudioSessionManager : IDisposable
{
    int RegisterSessionNotification(ICoreAudioSessionNotificationSink sink);
    int UnregisterSessionNotification(ICoreAudioSessionNotificationSink sink);
    int GetSessionEnumerator(out ICoreAudioSessionEnumerator? enumerator);
}

/// <summary>
/// Injectable abstraction over a single Core Audio endpoint (<c>IMMDevice</c> + <c>IMMEndpoint</c>).
/// </summary>
public interface ICoreAudioEndpoint : IDisposable
{
    string DeviceId { get; }
    CoreAudioDeviceFlow DataFlow { get; }
    CoreAudioDeviceState State { get; }
    int ActivateSessionManager(out ICoreAudioSessionManager? sessionManager);
}

/// <summary>
/// Injectable abstraction over <c>IMMDeviceEnumerator</c>.
/// </summary>
public interface ICoreAudioDeviceEnumerator : IDisposable
{
    int RegisterEndpointNotificationCallback(ICoreAudioEndpointNotificationSink sink);
    int UnregisterEndpointNotificationCallback(ICoreAudioEndpointNotificationSink sink);
    IReadOnlyList<ICoreAudioEndpoint> EnumerateActiveRenderEndpoints();
    ICoreAudioEndpoint? TryGetEndpoint(string deviceId);
}

/// <summary>
/// Factory invoked strictly on the dedicated MTA background thread to construct Core Audio COM objects.
/// </summary>
public interface ICoreAudioDeviceEnumeratorFactory
{
    ICoreAudioDeviceEnumerator Create();
}
