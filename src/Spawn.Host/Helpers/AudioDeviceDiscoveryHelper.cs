using NAudio.CoreAudioApi;

namespace Spawn.Host.Helpers;

public sealed record AudioRenderDeviceSnapshot(
    string? SystemDefaultDeviceId,
    string? SystemDefaultFriendlyName,
    IReadOnlyList<AudioRenderEndpointInfo> ActiveEndpoints);

public sealed record AudioRenderEndpointInfo(
    string DeviceId,
    string FriendlyName,
    bool IsSystemDefault);

/// <summary>
/// Enumerates active Windows CoreAudio render endpoints (<c>DeviceState.Active</c>) and queries
/// the current Windows default multimedia output device for Settings UI display and optional manual locking.
/// </summary>
public static class AudioDeviceDiscoveryHelper
{
    public static AudioRenderDeviceSnapshot QueryActiveRenderDevices()
    {
        string? defaultId = null;
        string? defaultName = null;
        var endpoints = new List<AudioRenderEndpointInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                using MMDevice defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = defaultDev.ID;
                defaultName = defaultDev.FriendlyName;
            }
            catch
            {
                try
                {
                    using MMDevice consoleDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    defaultId = consoleDev.ID;
                    defaultName = consoleDev.FriendlyName;
                }
                catch
                {
                    // No default render device present.
                }
            }

            var activeCollection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (MMDevice dev in activeCollection)
            {
                using (dev)
                {
                    string id = dev.ID;
                    string name = dev.FriendlyName;
                    bool isDef = !string.IsNullOrWhiteSpace(defaultId)
                                 && string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);

                    endpoints.Add(new AudioRenderEndpointInfo(id, name, isDef));
                }
            }
        }
        catch
        {
            // Headless environment without audio subsystem.
        }

        return new AudioRenderDeviceSnapshot(
            SystemDefaultDeviceId: defaultId,
            SystemDefaultFriendlyName: defaultName,
            ActiveEndpoints: endpoints);
    }
}
