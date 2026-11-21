using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Spawn.Contracts;

namespace Spawn.TrainerWorker;

/// <summary>
/// Worker-side WASAPI mute/volume fallback invoked when Medium IL <c>Spawn.Host</c> receives <c>E_ACCESSDENIED</c>
/// attempting to control a High IL trainer audio session.
/// </summary>
internal interface IWorkerAudioFallbackExecutor
{
    Task<TrainerSetAudioFallbackResponse> ApplyAsync(
        TrainerSetAudioFallbackRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes <c>NAudio.Wasapi</c> <c>ISimpleAudioVolume</c> mute/volume mutations on a dedicated MTA thread
/// across all active render endpoints for the requested process IDs.
/// </summary>
internal sealed class WasapiWorkerAudioFallbackExecutor : IWorkerAudioFallbackExecutor
{
    public static WasapiWorkerAudioFallbackExecutor Instance { get; } = new();

    private WasapiWorkerAudioFallbackExecutor()
    {
    }

    public Task<TrainerSetAudioFallbackResponse> ApplyAsync(
        TrainerSetAudioFallbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Pids.Count == 0)
        {
            return Task.FromResult(new TrainerSetAudioFallbackResponse
            {
                Succeeded = true,
                AffectedSessionCount = 0,
            });
        }

        var tcs = new TaskCompletionSource<TrainerSetAudioFallbackResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mtaThread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                TrainerSetAudioFallbackResponse result = ApplyOnCurrentMtaThread(request);
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new TrainerSetAudioFallbackResponse
                {
                    Succeeded = false,
                    AffectedSessionCount = 0,
                    ErrorMessage = ex.Message,
                });
            }
        })
        {
            Name = "Spawn.TrainerWorker.WasapiMta",
            IsBackground = true,
        };

        mtaThread.SetApartmentState(ApartmentState.MTA);
        mtaThread.Start();
        return tcs.Task;
    }

    private static TrainerSetAudioFallbackResponse ApplyOnCurrentMtaThread(TrainerSetAudioFallbackRequest request)
    {
        var targetPids = new HashSet<uint>();
        foreach (int pid in request.Pids)
        {
            if (pid > 4)
            {
                targetPids.Add(checked((uint)pid));
            }
        }

        if (targetPids.Count == 0)
        {
            return new TrainerSetAudioFallbackResponse
            {
                Succeeded = true,
                AffectedSessionCount = 0,
            };
        }

        float clampedVolume = (float)Math.Clamp(request.Volume, 0.0, 1.0);
        int affected = 0;

        using var enumerator = new MMDeviceEnumerator();
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (MMDevice device in devices)
        {
            using (device)
            {
                try
                {
                    AudioSessionManager manager = device.AudioSessionManager;
                    SessionCollection sessions = manager.Sessions;
                    int count = sessions.Count;
                    for (int i = 0; i < count; i++)
                    {
                        using AudioSessionControl session = sessions[i];
                        try
                        {
                            uint sessionPid = session.GetProcessID;
                            if (!targetPids.Contains(sessionPid))
                            {
                                continue;
                            }

                            SimpleAudioVolume volume = session.SimpleAudioVolume;
                            volume.Mute = request.Mute;
                            if (!request.Mute)
                            {
                                volume.Volume = clampedVolume;
                            }

                            affected++;
                        }
                        catch (COMException)
                        {
                            // Ignore transient AUDCLNT_E_DEVICE_INVALIDATED or session teardown races.
                        }
                    }
                }
                catch (COMException)
                {
                    // Ignore disconnected audio endpoints.
                }
            }
        }

        return new TrainerSetAudioFallbackResponse
        {
            Succeeded = true,
            AffectedSessionCount = affected,
        };
    }
}
