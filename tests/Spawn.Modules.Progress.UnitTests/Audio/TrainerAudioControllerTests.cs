using Microsoft.Extensions.DependencyInjection;
using Spawn.Contracts;
using Spawn.Modules.Progress.Audio;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Audio;

public sealed class TrainerAudioControllerTests : IDisposable
{
    private static readonly string[] ExpectedRegistrationSequence =
        ["RegisterSessionNotification", "GetSessionEnumerator", "GetCount"];

    private static readonly string[] ExpectedPolicyTransitionHistory =
        ["SetMute(True)", "SetMasterVolume(0.35)", "SetMute(False)"];

    private static readonly int[] ExpectedDeniedPids = [9001, 9002];

    private readonly string _tempRoot;
    private readonly string _sandboxRoot;

    public TrainerAudioControllerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-w06-wasapi-{Guid.NewGuid():N}");
        _sandboxRoot = Path.Combine(_tempRoot, "Sandbox", "Trainers");
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private TrainerAudioA0Preparer CreateTempA0Preparer()
    {
        string docs = Path.Combine(_tempRoot, "Docs");
        string local = Path.Combine(_tempRoot, "LocalAppData");
        Directory.CreateDirectory(docs);
        Directory.CreateDirectory(local);
        var resolver = new WindowsTrainerAudioA0DirectoryResolver(
            () => docs,
            () => Path.Combine(_tempRoot, "OtherProfile"),
            () => local);
        return new TrainerAudioA0Preparer(resolver);
    }

    [Fact]
    public void PrepareAsync_FromExplicitStaThread_RunsAllCoreAudioCallsStrictlyOnDedicatedMtaThread()
    {
        var fakeAudio = new FakeCoreAudioSystem();
        var endpoint = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-main"));
        var trainerSession = endpoint.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 4001));

        int staCallerThreadId = -1;
        Exception? staException = null;

        var staThread = new Thread(() =>
        {
            try
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                staCallerThreadId = Environment.CurrentManagedThreadId;

                var controller = new TrainerAudioController(
                    CreateTempA0Preparer(),
                    fakeAudio,
                    processPathResolver: _ => null);

                controller.TrackProcess(4001);
                controller.PrepareAsync(new TrainerAudioPolicy { EnableA0 = false }, _sandboxRoot).GetAwaiter().GetResult();
                controller.ApplyPolicyAsync(new TrainerAudioPolicy { Mode = TrainerAudioMode.KeepCuesExperimental, CueVolume = 0.35, EnableA0 = false }).GetAwaiter().GetResult();
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(staException);

        Assert.Equal(1, fakeAudio.FactoryCreateCount);
        Assert.Equal(ApartmentState.MTA, fakeAudio.FactoryApartmentState);
        Assert.NotEqual(staCallerThreadId, fakeAudio.FactoryThreadId);

        // Every single Core Audio call must have executed on the dedicated MTA thread (never on the STA caller thread).
        Assert.NotEmpty(fakeAudio.CallTrace);
        foreach (var (operation, threadId, apartment) in fakeAudio.CallTrace)
        {
            Assert.Equal(ApartmentState.MTA, apartment);
            Assert.Equal(fakeAudio.FactoryThreadId, threadId);
            Assert.NotEqual(staCallerThreadId, threadId);
        }

        Assert.Equal(false, trainerSession.LastMute);
        Assert.Equal(0.35f, trainerSession.LastVolume);
    }

    [Fact]
    public async Task AttachEndpoints_EnforcesStrictOrder_RegisterBeforeEnumeratorBeforeGetCount_OnInitialAndHotPlug()
    {
        var fakeAudio = new FakeCoreAudioSystem();
        FakeCoreAudioEndpoint ep1 = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-1"));
        FakeCoreAudioEndpoint ep2 = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-2"));

        await using var controller = new TrainerAudioController(
            CreateTempA0Preparer(),
            fakeAudio,
            processPathResolver: _ => null);

        await controller.PrepareAsync(new TrainerAudioPolicy { EnableA0 = false }, _sandboxRoot);

        Assert.Equal(ExpectedRegistrationSequence, ep1.OperationLog.Take(3).ToArray());
        Assert.Equal(ExpectedRegistrationSequence, ep2.OperationLog.Take(3).ToArray());

        // Hot-plug a third active render endpoint and verify identical strict registration ordering.
        FakeCoreAudioEndpoint usbHeadset = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "usb-headset"));
        fakeAudio.Enumerator.RaiseDeviceAdded("usb-headset");
        await controller.FlushAsync();

        Assert.Equal(ExpectedRegistrationSequence, usbHeadset.OperationLog.Take(3).ToArray());
    }

    [Fact]
    public async Task HotPlug_HandlesDeviceAddedStateChangedRemoved_IgnoresCapture_AndRetainsStrongSinkReferences()
    {
        var fakeAudio = new FakeCoreAudioSystem();
        FakeCoreAudioEndpoint speakers = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "spk-1"));

        await using var controller = new TrainerAudioController(
            CreateTempA0Preparer(),
            fakeAudio,
            processPathResolver: _ => null);

        controller.TrackProcess(5100);
        await controller.PrepareAsync(new TrainerAudioPolicy { EnableA0 = false }, _sandboxRoot);

        // Strong references to registered notification sinks must survive aggressive GC.
        WeakReference<ICoreAudioEndpointNotificationSink>? endpointSinkRef = fakeAudio.Enumerator.RegisteredEndpointSinkWeakRef;
        WeakReference<ICoreAudioSessionNotificationSink>? speakerSinkRef = speakers.RegisteredSessionSinkWeakRef;
        Assert.NotNull(endpointSinkRef);
        Assert.NotNull(speakerSinkRef);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(endpointSinkRef!.TryGetTarget(out _));
        Assert.True(speakerSinkRef!.TryGetTarget(out _));

        // Capture endpoint (microphone) hot-plug must be ignored.
        FakeCoreAudioEndpoint mic = fakeAudio.Enumerator.AddOrUpdateEndpoint(
            new FakeCoreAudioEndpoint(fakeAudio, "mic-1", CoreAudioDeviceFlow.Capture, CoreAudioDeviceState.Active));
        fakeAudio.Enumerator.RaiseDeviceAdded("mic-1");
        await controller.FlushAsync();
        Assert.Equal(0, mic.RegisterSessionNotificationCount);

        // Bluetooth render headset hot-plug -> attaches and mutes existing/newly created session.
        FakeCoreAudioEndpoint bt = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "bt-1"));
        FakeCoreAudioSession existingSession = bt.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 5100));
        fakeAudio.Enumerator.RaiseDeviceAdded("bt-1");
        await controller.FlushAsync();
        Assert.Equal(1, bt.RegisterSessionNotificationCount);
        Assert.Equal(true, existingSession.LastMute);

        // Unplugging Bluetooth detaches & unregisters notification.
        fakeAudio.Enumerator.RaiseDeviceStateChanged("bt-1", CoreAudioDeviceState.Unplugged);
        await controller.FlushAsync();
        Assert.Equal(1, bt.UnregisterSessionNotificationCount);
        Assert.Equal(1, bt.DisposedManagerCount);

        // Re-activating Bluetooth re-attaches and mutes newly created sessions.
        fakeAudio.Enumerator.RaiseDeviceStateChanged("bt-1", CoreAudioDeviceState.Active);
        await controller.FlushAsync();
        Assert.Equal(2, bt.RegisterSessionNotificationCount);

        var newSession = new FakeCoreAudioSession(fakeAudio, processId: 5100);
        bt.RaiseSessionCreated(newSession);
        await controller.FlushAsync();
        Assert.Equal(true, newSession.LastMute);

        // Removing speakers detaches & unregisters.
        fakeAudio.Enumerator.RaiseDeviceRemoved("spk-1");
        await controller.FlushAsync();
        Assert.Equal(1, speakers.UnregisterSessionNotificationCount);
    }

    [Fact]
    public async Task SessionMatching_MatchesTrackedMainAndChildPids_AndSandboxPrefixOnly()
    {
        var fakeAudio = new FakeCoreAudioSystem();
        FakeCoreAudioEndpoint ep = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-main"));

        var mainTrainerSession = ep.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 6001));
        var childTrainerSession = ep.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 6002));
        var sandboxOnlySession = ep.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 6003));
        var siblingPrefixSession = ep.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 6004));
        var gameSession = ep.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 6005));

        var pathMap = new Dictionary<int, string>
        {
            [6003] = Path.Combine(_sandboxRoot, "game-abc", "trainer-1", "EldenRing_Trainer.exe"),
            [6004] = Path.Combine(_tempRoot, "Sandbox", "Trainers2", "NotInSandbox.exe"),
            [6005] = Path.Combine(_tempRoot, "Games", "SinglePlayerGame", "game.exe"),
        };

        await using var controller = new TrainerAudioController(
            CreateTempA0Preparer(),
            fakeAudio,
            processPathResolver: pid => pathMap.TryGetValue(pid, out string? path) ? path : null);

        // Register main PID before PrepareAsync; child PID arrives after PrepareAsync via TrackProcess (simulating IOCP NEW_PROCESS).
        controller.TrackProcess(6001);
        await controller.PrepareAsync(new TrainerAudioPolicy { EnableA0 = false }, _sandboxRoot);

        Assert.Equal(true, mainTrainerSession.LastMute);
        Assert.Equal(true, sandboxOnlySession.LastMute);
        Assert.Null(childTrainerSession.LastMute);
        Assert.Null(siblingPrefixSession.LastMute);
        Assert.Null(gameSession.LastMute);

        // Child PID reported later must immediately mute existing session on active endpoints.
        controller.TrackProcess(6002);
        await controller.FlushAsync();

        Assert.Equal(true, childTrainerSession.LastMute);
        Assert.Equal(0, siblingPrefixSession.SetMuteCallCount);
        Assert.Equal(0, gameSession.SetMuteCallCount);
    }

    [Fact]
    public async Task PolicyStateMachine_FullMuteDefault_KeepCuesExperimental_AndPassthrough()
    {
        var defaultPolicy = new TrainerAudioPolicy();
        Assert.Equal(TrainerAudioMode.FullMute, defaultPolicy.Mode);
        Assert.Equal(0.35, defaultPolicy.CueVolume);
        Assert.True(defaultPolicy.EnableA0);

        var fakeAudio = new FakeCoreAudioSystem();
        FakeCoreAudioEndpoint ep = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-main"));
        FakeCoreAudioSession session = ep.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 7001));

        await using var controller = new TrainerAudioController(
            CreateTempA0Preparer(),
            fakeAudio,
            processPathResolver: _ => null);

        controller.TrackProcess(7001);

        // 1. Default FullMute -> SetMute(true), no volume change.
        await controller.PrepareAsync(defaultPolicy with { EnableA0 = false }, _sandboxRoot);
        Assert.Equal(true, session.LastMute);
        Assert.Equal(0, session.SetMasterVolumeCallCount);

        // 2. Switch to KeepCuesExperimental -> SetMasterVolume(0.35f) BEFORE SetMute(false).
        await controller.ApplyPolicyAsync(new TrainerAudioPolicy
        {
            Mode = TrainerAudioMode.KeepCuesExperimental,
            CueVolume = 0.35,
            EnableA0 = false,
        });
        Assert.Equal(false, session.LastMute);
        Assert.Equal(0.35f, session.LastVolume);
        Assert.Equal(ExpectedPolicyTransitionHistory, session.ActionHistory);

        // 3. Switch back to FullMute -> SetMute(true).
        await controller.ApplyPolicyAsync(new TrainerAudioPolicy { Mode = TrainerAudioMode.FullMute, EnableA0 = false });
        Assert.Equal(true, session.LastMute);

        // 4. Switch to Passthrough -> no further SetMute or SetMasterVolume calls even for newly created sessions.
        int prevMuteCalls = session.SetMuteCallCount;
        int prevVolCalls = session.SetMasterVolumeCallCount;
        await controller.ApplyPolicyAsync(new TrainerAudioPolicy { Mode = TrainerAudioMode.Passthrough, EnableA0 = false });
        var lateSession = new FakeCoreAudioSession(fakeAudio, processId: 7001);
        ep.RaiseSessionCreated(lateSession);
        await controller.FlushAsync();

        Assert.Equal(prevMuteCalls, session.SetMuteCallCount);
        Assert.Equal(prevVolCalls, session.SetMasterVolumeCallCount);
        Assert.Equal(0, lateSession.SetMuteCallCount);
        Assert.Equal(0, lateSession.SetMasterVolumeCallCount);
    }

    [Fact]
    public async Task DeviceInvalidated_AndNoSingleProcess_HandledWithoutDisruptingHealthyEndpoints()
    {
        var fakeAudio = new FakeCoreAudioSystem();

        // Endpoint A throws/returns AUDCLNT_E_DEVICE_INVALIDATED mid-enumeration.
        FakeCoreAudioEndpoint brokenEndpoint = fakeAudio.Enumerator.AddOrUpdateEndpoint(
            new FakeCoreAudioEndpoint(fakeAudio, "render-broken")
            {
                GetCountResultHResult = CoreAudioHResults.DeviceInvalidated,
            });

        // Endpoint B has AUDCLNT_S_NO_SINGLE_PROCESS (0x0889000D), System Sounds, and a real trainer session.
        FakeCoreAudioEndpoint healthyEndpoint = fakeAudio.Enumerator.AddOrUpdateEndpoint(
            new FakeCoreAudioEndpoint(fakeAudio, "render-healthy"));

        FakeCoreAudioSession noSingleProcessSession = healthyEndpoint.AddSession(
            new FakeCoreAudioSession(fakeAudio, processId: 8001, processInfoHResult: CoreAudioHResults.NoSingleProcess));
        FakeCoreAudioSession systemSoundsSession = healthyEndpoint.AddSession(
            new FakeCoreAudioSession(fakeAudio, processId: 8001, processInfoHResult: CoreAudioHResults.SOk, isSystemSounds: true));
        FakeCoreAudioSession trainerSession = healthyEndpoint.AddSession(
            new FakeCoreAudioSession(fakeAudio, processId: 8001));

        await using var controller = new TrainerAudioController(
            CreateTempA0Preparer(),
            fakeAudio,
            processPathResolver: _ => null);

        controller.TrackProcess(8001);
        await controller.PrepareAsync(new TrainerAudioPolicy { EnableA0 = false }, _sandboxRoot);

        // Broken endpoint was cleanly detached on AUDCLNT_E_DEVICE_INVALIDATED.
        Assert.Equal(1, brokenEndpoint.UnregisterSessionNotificationCount);
        Assert.Equal(1, brokenEndpoint.DisposedManagerCount);

        // Shared cross-process & system sounds sessions were safely skipped; trainer session was muted.
        Assert.Equal(0, noSingleProcessSession.SetMuteCallCount);
        Assert.Equal(0, systemSoundsSession.SetMuteCallCount);
        Assert.Equal(true, trainerSession.LastMute);
    }

    [Fact]
    public async Task AccessDenied_HResultAndException_RaisesFallbackRequiredWithPidsMuteAndVolume()
    {
        var fakeAudio = new FakeCoreAudioSystem();
        FakeCoreAudioEndpoint ep = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-main"));

        FakeCoreAudioSession highIlSessionA = ep.AddSession(
            new FakeCoreAudioSession(fakeAudio, processId: 9002) { SetMuteHResult = CoreAudioHResults.AccessDenied, SetMasterVolumeHResult = CoreAudioHResults.AccessDenied });
        FakeCoreAudioSession highIlSessionADup = ep.AddSession(
            new FakeCoreAudioSession(fakeAudio, processId: 9002) { SetMuteHResult = CoreAudioHResults.AccessDenied, SetMasterVolumeHResult = CoreAudioHResults.AccessDenied });
        FakeCoreAudioSession highIlSessionB = ep.AddSession(
            new FakeCoreAudioSession(fakeAudio, processId: 9001) { SetMuteHResult = CoreAudioHResults.AccessDenied, SetMasterVolumeHResult = CoreAudioHResults.AccessDenied });

        var fallbackEvents = new List<(IReadOnlyList<int> Pids, bool Mute, double Volume)>();

        await using var controller = new TrainerAudioController(
            CreateTempA0Preparer(),
            fakeAudio,
            processPathResolver: _ => null);

        controller.FallbackRequired += (pids, mute, volume, _) =>
        {
            lock (fallbackEvents)
            {
                fallbackEvents.Add((pids.ToArray(), mute, volume));
            }

            return Task.CompletedTask;
        };

        controller.TrackProcess(9001);
        controller.TrackProcess(9002);

        // 1. FullMute E_ACCESSDENIED -> deduplicated sorted PIDs, mute=true, volume=0.0.
        await controller.PrepareAsync(new TrainerAudioPolicy { Mode = TrainerAudioMode.FullMute, EnableA0 = false }, _sandboxRoot);
        Assert.Single(fallbackEvents);
        Assert.Equal(ExpectedDeniedPids, fallbackEvents[0].Pids);
        Assert.True(fallbackEvents[0].Mute);
        Assert.Equal(0.0, fallbackEvents[0].Volume);

        // 2. KeepCuesExperimental E_ACCESSDENIED -> mute=false, volume=0.35.
        await controller.ApplyPolicyAsync(new TrainerAudioPolicy
        {
            Mode = TrainerAudioMode.KeepCuesExperimental,
            CueVolume = 0.35,
            EnableA0 = false,
        });
        Assert.Equal(2, fallbackEvents.Count);
        Assert.Equal(ExpectedDeniedPids, fallbackEvents[1].Pids);
        Assert.False(fallbackEvents[1].Mute);
        Assert.Equal(0.35, fallbackEvents[1].Volume, precision: 3);

        // 3. UnauthorizedAccessException on dynamic OnSessionCreated -> also raises FallbackRequired.
        await controller.ApplyPolicyAsync(new TrainerAudioPolicy { Mode = TrainerAudioMode.FullMute, EnableA0 = false });
        int beforeDynamic = fallbackEvents.Count;
        var dynamicElevatedSession = new FakeCoreAudioSession(fakeAudio, processId: 9003)
        {
            ThrowUnauthorizedOnSetMute = true,
        };
        controller.TrackProcess(9003);
        ep.RaiseSessionCreated(dynamicElevatedSession);
        await controller.FlushAsync();

        Assert.True(fallbackEvents.Count > beforeDynamic);
        Assert.Contains(fallbackEvents.Skip(beforeDynamic), e => e.Pids.Contains(9003) && e.Mute);

        // Keep references asserted as exercised.
        Assert.True(highIlSessionA.SetMuteCallCount > 0);
        Assert.True(highIlSessionADup.SetMuteCallCount > 0);
        Assert.True(highIlSessionB.SetMuteCallCount > 0);
    }

    [Fact]
    public async Task DisposeAsync_UnregistersAllNotifications_IsIdempotent_AndSuppressesLateCallbacks()
    {
        var fakeAudio = new FakeCoreAudioSystem();
        FakeCoreAudioEndpoint ep = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-main"));

        var controller = new TrainerAudioController(
            CreateTempA0Preparer(),
            fakeAudio,
            processPathResolver: _ => null);

        int fallbackCount = 0;
        controller.FallbackRequired += (_, _, _, _) =>
        {
            Interlocked.Increment(ref fallbackCount);
            return Task.CompletedTask;
        };

        controller.TrackProcess(9500);
        await controller.PrepareAsync(new TrainerAudioPolicy { EnableA0 = false }, _sandboxRoot);

        // Capture sinks BEFORE disposal to simulate rogue late RPC callbacks arriving after DisposeAsync.
        ICoreAudioEndpointNotificationSink capturedEndpointSink = fakeAudio.Enumerator.RegisteredEndpointSink!;
        ICoreAudioSessionNotificationSink capturedSessionSink = ep.RegisteredSessionSink!;
        Assert.NotNull(capturedEndpointSink);
        Assert.NotNull(capturedSessionSink);

        await controller.DisposeAsync();
        await controller.DisposeAsync(); // Idempotent second dispose.

        Assert.Equal(1, fakeAudio.Enumerator.UnregisterEndpointCallbackCount);
        Assert.Equal(1, ep.UnregisterSessionNotificationCount);
        Assert.Equal(0, ep.ActiveHandleCount);
        Assert.Equal(1, ep.DisposedManagerCount);
        Assert.True(fakeAudio.Enumerator.IsDisposed);

        // Simulate late COM callbacks after disposal -> zero mutations, zero fallback events, late session disposed cleanly.
        var lateSession = new FakeCoreAudioSession(fakeAudio, processId: 9500)
        {
            SetMuteHResult = CoreAudioHResults.AccessDenied,
        };
        capturedSessionSink.OnSessionCreated(lateSession.CreateHandle());
        capturedEndpointSink.OnDeviceAdded("render-late");
        capturedEndpointSink.OnDeviceStateChanged("render-main", CoreAudioDeviceState.Active);
        capturedEndpointSink.OnDeviceRemoved("render-main");

        Assert.Equal(0, lateSession.SetMuteCallCount);
        Assert.Equal(1, lateSession.DisposedHandleCount);
        Assert.Equal(0, Volatile.Read(ref fallbackCount));

        // Post-dispose PrepareAsync / ApplyPolicyAsync reject cleanly.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.PrepareAsync(new TrainerAudioPolicy(), _sandboxRoot));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.ApplyPolicyAsync(new TrainerAudioPolicy()));
    }

    [Fact]
    public async Task DependencyInjection_DefaultConstructor_ResolvesCleanlyWithAndWithoutLogging()
    {
        var servicesNoLog = new ServiceCollection();
        servicesNoLog.AddSingleton<ITrainerAudioController, TrainerAudioController>();
        await using ServiceProvider spNoLog = servicesNoLog.BuildServiceProvider();
        ITrainerAudioController c1 = spNoLog.GetRequiredService<ITrainerAudioController>();
        Assert.IsType<TrainerAudioController>(c1);

        var servicesWithLog = new ServiceCollection();
        servicesWithLog.AddLogging();
        servicesWithLog.AddSingleton<ITrainerAudioController, TrainerAudioController>();
        await using ServiceProvider spWithLog = servicesWithLog.BuildServiceProvider();
        ITrainerAudioController c2 = spWithLog.GetRequiredService<ITrainerAudioController>();
        Assert.IsType<TrainerAudioController>(c2);
    }

    [Fact]
    public async Task PrepareAsync_WithA0Enabled_WritesA0FilesAndRestoresOnRestoreA0BackupsAsync()
    {
        string docs = Path.Combine(_tempRoot, "IntegratedDocs");
        string local = Path.Combine(_tempRoot, "IntegratedLocal");
        Directory.CreateDirectory(docs);
        Directory.CreateDirectory(local);

        var resolver = new WindowsTrainerAudioA0DirectoryResolver(
            () => docs,
            () => Path.Combine(_tempRoot, "OtherProfile"),
            () => local);
        var preparer = new TrainerAudioA0Preparer(resolver);
        var fakeAudio = new FakeCoreAudioSystem();
        FakeCoreAudioEndpoint ep = fakeAudio.Enumerator.AddOrUpdateEndpoint(new FakeCoreAudioEndpoint(fakeAudio, "render-main"));
        FakeCoreAudioSession session = ep.AddSession(new FakeCoreAudioSession(fakeAudio, processId: 9901));

        await using var controller = new TrainerAudioController(preparer, fakeAudio, _ => null);
        controller.TrackProcess(9901);

        await controller.PrepareAsync(new TrainerAudioPolicy { Mode = TrainerAudioMode.FullMute, EnableA0 = true }, _sandboxRoot);

        Assert.Equal(true, session.LastMute);
        string docsMidi = Path.Combine(docs, "FLiNGTrainer", TrainerAudioA0Preparer.TrainerBgmMidiFileName);
        string localIni = Path.Combine(local, "FLiNGTrainer", TrainerAudioA0Preparer.FlingTSettingsFileName);
        Assert.True(SilentMidiAssets.IsExactSilentSmf(File.ReadAllBytes(docsMidi)));
        Assert.Contains("OnLoadMusic = False", File.ReadAllText(localIni));

        await controller.RestoreA0BackupsAsync();
        Assert.False(File.Exists(docsMidi));
        Assert.False(File.Exists(localIni));
    }
}
