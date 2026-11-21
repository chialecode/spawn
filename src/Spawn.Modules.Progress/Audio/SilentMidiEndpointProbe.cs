using System.Collections.Concurrent;

namespace Spawn.Modules.Progress.Audio;

/// <summary>
/// Diagnostic report from the opt-in <c>UNV-02</c> silent WASAPI/MIDI endpoint probe.
/// Never executed during normal automated unit tests or <c>quick_gate.py</c>.
/// </summary>
public sealed record SilentMidiEndpointProbeReport(
    int ActiveRenderEndpointCount,
    int CurrentProcessId,
    bool ObservedCurrentProcessAudioSession,
    IReadOnlyList<int> ObservedSessionProcessIds,
    SilentMidiMciVerificationResult MciResult);

/// <summary>
/// Opt-in local diagnostic probe for <c>UNV-02</c> (verifying whether GS Wavetable Synth MCI MIDI playback
/// produces a WASAPI session attributed to the calling process PID on the host machine).
/// Uses only the 27-byte pure-silence SMF so no audible sound is produced.
/// </summary>
public static class SilentMidiEndpointProbe
{
    public const string OptInEnvironmentVariable = "SPAWN_RUN_UNV02_AUDIO_PROBE";

    public static bool IsOptInEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable(OptInEnvironmentVariable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Runs the silent endpoint + MCI probe on a dedicated MTA thread. Caller should gate invocation behind
    /// <see cref="IsOptInEnabled"/> in test suites so normal CI / quick gates never touch real hardware endpoints.
    /// </summary>
    public static SilentMidiEndpointProbeReport RunSilentProbe(string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);
        Directory.CreateDirectory(tempDirectory);

        string midiPath = Path.Combine(tempDirectory, $"unv02-probe-{Guid.NewGuid():N}.mid");
        File.WriteAllBytes(midiPath, SilentMidiAssets.GetSilentSmfCopy());

        try
        {
            var observedPids = new ConcurrentDictionary<int, byte>();
            int endpointCount = 0;
            SilentMidiMciVerificationResult? mciResult = null;
            Exception? capturedException = null;

            var thread = new Thread(() =>
            {
                try
                {
                    using ICoreAudioDeviceEnumerator enumerator = new WindowsCoreAudioDeviceEnumeratorFactory().Create();
                    IReadOnlyList<ICoreAudioEndpoint> endpoints = enumerator.EnumerateActiveRenderEndpoints();
                    endpointCount = endpoints.Count;

                    var managers = new List<(ICoreAudioEndpoint Endpoint, ICoreAudioSessionManager Manager, ProbeSessionSink Sink)>();
                    try
                    {
                        foreach (ICoreAudioEndpoint endpoint in endpoints)
                        {
                            if (endpoint.ActivateSessionManager(out ICoreAudioSessionManager? manager) >= 0 && manager is not null)
                            {
                                var sink = new ProbeSessionSink(observedPids);
                                _ = manager.RegisterSessionNotification(sink);
                                RecordExistingSessions(manager, observedPids);
                                managers.Add((endpoint, manager, sink));
                            }
                            else
                            {
                                endpoint.Dispose();
                            }
                        }

                        mciResult = SilentMidiMciVerifier.VerifyPlaybackAndReplay(midiPath, settleDelayMs: 60);

                        foreach (var (_, manager, _) in managers)
                        {
                            RecordExistingSessions(manager, observedPids);
                        }
                    }
                    finally
                    {
                        foreach (var (endpoint, manager, sink) in managers)
                        {
                            try { _ = manager.UnregisterSessionNotification(sink); } catch { }
                            manager.Dispose();
                            endpoint.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }
            })
            {
                IsBackground = true,
                Name = "Spawn.TrainerAudio.UNV02Probe.MTA",
            };

            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(10));

            if (capturedException is not null)
            {
                throw new InvalidOperationException("UNV-02 silent endpoint probe failed.", capturedException);
            }

            int currentPid = Environment.ProcessId;
            int[] pids = observedPids.Keys.OrderBy(static p => p).ToArray();
            return new SilentMidiEndpointProbeReport(
                endpointCount,
                currentPid,
                observedPids.ContainsKey(currentPid),
                pids,
                mciResult ?? new SilentMidiMciVerificationResult { Succeeded = false, DiagnosticMessage = "Probe thread timed out." });
        }
        finally
        {
            try
            {
                if (File.Exists(midiPath))
                {
                    File.Delete(midiPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static void RecordExistingSessions(ICoreAudioSessionManager manager, ConcurrentDictionary<int, byte> sink)
    {
        if (manager.GetSessionEnumerator(out ICoreAudioSessionEnumerator? enumerator) < 0 || enumerator is null)
        {
            return;
        }

        using (enumerator)
        {
            if (enumerator.GetCount(out int count) < 0 || count <= 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (enumerator.GetSession(i, out ICoreAudioSession? session) >= 0 && session is not null)
                {
                    using (session)
                    {
                        CoreAudioSessionProcessInfo info = session.GetProcessInfo();
                        if (info.Succeeded && !info.IsNoSingleProcess && !info.IsSystemSoundsSession && info.ProcessId > 4)
                        {
                            sink[info.ProcessId] = 0;
                        }
                    }
                }
            }
        }
    }

    private sealed class ProbeSessionSink(ConcurrentDictionary<int, byte> observedPids) : ICoreAudioSessionNotificationSink
    {
        public void OnSessionCreated(ICoreAudioSession session)
        {
            using (session)
            {
                CoreAudioSessionProcessInfo info = session.GetProcessInfo();
                if (info.Succeeded && !info.IsNoSingleProcess && !info.IsSystemSoundsSession && info.ProcessId > 4)
                {
                    observedPids[info.ProcessId] = 0;
                }
            }
        }
    }
}
