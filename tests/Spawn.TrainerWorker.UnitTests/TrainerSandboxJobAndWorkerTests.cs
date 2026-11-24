using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;
using Spawn.Core.Ipc;
using Xunit;

namespace Spawn.TrainerWorker.UnitTests;

/// <summary>
/// M3-A-09 verification: atomic <c>KILL_ON_JOB_CLOSE</c> JobObject enrollment, multi-generation <c>TestStub</c>
/// process-tree cleanup on Job close / Worker kill / Host disconnect, IOCP <c>NEW_PROCESS</c> + <c>ACTIVE_PROCESS_ZERO</c>,
/// command whitelist, and Defender WMI mock handling. Zero visible windows, zero audio, zero real Defender writes.
/// </summary>
public sealed class TrainerSandboxJobAndWorkerTests
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitSilentBreakawayOk = 0x00001000;

    [Fact]
    public void TrainerSandboxJob_LimitFlags_EnforceKillOnJobCloseAndDisableBreakaway()
    {
        using var job = new TrainerSandboxJob();
        uint flags = job.QueryLimitFlags();

        Assert.NotEqual(0u, flags & JobObjectLimitKillOnJobClose);
        Assert.Equal(0u, flags & JobObjectLimitBreakawayOk);
        Assert.Equal(0u, flags & JobObjectLimitSilentBreakawayOk);
    }

    [Fact]
    public async Task TrainerSandboxJob_TestStubProcessTree_AtomicallyEnrolledAndPurgedOnDispose()
    {
        using var temp = new TempWorkerSandbox();
        string pidDir = temp.CreateSubDirectory("pids");
        string stubExe = temp.StageTestStubWithConfig(new
        {
            depth = 2,
            childrenPerNode = 1,
            pidDirectory = pidDir,
            maxLifetimeMs = 25000,
        });

        var observedPids = new ConcurrentBag<int>();
        using (var job = new TrainerSandboxJob())
        {
            job.ProcessCreated += (pid, _) => observedPids.Add(pid);

            SandboxLaunchResult launch = job.LaunchAtomically(stubExe, Path.GetDirectoryName(stubExe)!, hideOriginalWindow: true);
            Assert.True(launch.Succeeded, launch.ErrorMessage);
            Assert.True(launch.RootProcessId > 4);

            // Wait for root + child + grandchild (3 processes total) to record their PIDs.
            int[] treePids = await WaitForPidCountAsync(pidDir, expectedCount: 3, TimeSpan.FromSeconds(8));
            Assert.Equal(3, treePids.Length);
            Assert.All(treePids, pid => Assert.True(SafeProcessInspector.IsProcessAlive(pid), $"Expected PID {pid} to be alive before job close."));

            IReadOnlySet<int> jobPids = job.QueryActiveProcessIds();
            foreach (int pid in treePids)
            {
                Assert.Contains(pid, jobPids);
            }

            // Disposing the Job closes the handle (KILL_ON_JOB_CLOSE) -> kernel terminates entire tree.
            job.Dispose();

            await WaitUntilAllDeadAsync(treePids, TimeSpan.FromSeconds(5));
            Assert.All(treePids, pid => Assert.False(SafeProcessInspector.IsProcessAlive(pid), $"PID {pid} survived job close!"));
        }

        Assert.True(observedPids.Count >= 3, $"Expected IOCP NEW_PROCESS notifications for process tree, observed {observedPids.Count}.");
    }

    [Fact]
    public async Task TrainerSandboxJob_RootExitsEarlyWhileChildRuns_ActiveProcessZeroWaitsForChild()
    {
        using var temp = new TempWorkerSandbox();
        string pidDir = temp.CreateSubDirectory("pids-early");
        string stubExe = temp.StageTestStubWithConfig(new
        {
            depth = 1,
            childrenPerNode = 1,
            pidDirectory = pidDir,
            rootExitAfterMs = 250,
            childExitAfterMs = 1100,
            maxLifetimeMs = 10000,
        });

        using var job = new TrainerSandboxJob();
        SandboxLaunchResult launch = job.LaunchAtomically(stubExe, Path.GetDirectoryName(stubExe)!, hideOriginalWindow: true);
        Assert.True(launch.Succeeded, launch.ErrorMessage);

        int[] pids = await WaitForPidCountAsync(pidDir, expectedCount: 2, TimeSpan.FromSeconds(8));
        Assert.Equal(2, pids.Length);

        // Wait for root to exit first, verifying ActiveProcessZero has NOT fired while child is still alive.
        await WaitUntilAllDeadAsync([launch.RootProcessId], TimeSpan.FromSeconds(5));
        int childPid = pids.Single(p => p != launch.RootProcessId);
        if (SafeProcessInspector.IsProcessAlive(childPid))
        {
            Assert.False(job.ActiveProcessZeroTask.IsCompleted, "ACTIVE_PROCESS_ZERO must not fire while child process is still alive.");
        }

        // Once child exits naturally, ACTIVE_PROCESS_ZERO must fire cleanly.
        _ = await job.ActiveProcessZeroTask.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.False(SafeProcessInspector.IsProcessAlive(childPid));
    }

    [Fact]
    public void TrainerLaunchValidator_VerifiedImageLock_DeniesSwapUntilLaunchAndStillAllowsAtomicLaunch()
    {
        using var temp = new TempWorkerSandbox();
        string stubExe = temp.StageTestStubWithConfig(new { depth = 0, maxLifetimeMs = 100 });
        string realSha = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe);

        Assert.Null(TrainerLaunchValidator.OpenVerifiedImageLock(stubExe, new string('0', 64)));

        using (FileStream? imageLock = TrainerLaunchValidator.OpenVerifiedImageLock(stubExe, realSha))
        {
            Assert.NotNull(imageLock);

            // Same-user writers / renamers are denied while the verified handle is held.
            Assert.ThrowsAny<IOException>(() => new FileStream(stubExe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            Assert.ThrowsAny<IOException>(() => File.Move(stubExe, stubExe + ".swapped"));

            // CreateProcessW can still map the locked image.
            using var job = new TrainerSandboxJob();
            SandboxLaunchResult launch = job.LaunchAtomically(stubExe, Path.GetDirectoryName(stubExe)!, hideOriginalWindow: true);
            Assert.True(launch.Succeeded, launch.ErrorMessage);
        }
    }

    [Fact]
    public void TrainerLaunchValidator_EnforcesSandboxWhitelistAndPinnedSha256()
    {
        using var temp = new TempWorkerSandbox();
        string stubExe = temp.StageTestStubWithConfig(new { depth = 0, maxLifetimeMs = 100 });
        string relPath = Path.GetRelativePath(temp.SandboxRoot, stubExe);
        string realSha = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe);

        // 1. Valid request passes.
        ValidatedTrainerLaunchTarget ok = TrainerLaunchValidator.Validate(new TrainerLaunchRequest
        {
            SessionId = "s1",
            SandboxRoot = temp.SandboxRoot,
            SandboxRelativePath = relPath,
            Sha256 = realSha,
        });
        Assert.True(ok.Succeeded, ok.ErrorMessage);

        // 2. Path traversal outside sandbox rejected.
        string outsideExe = Path.Combine(temp.RootDir, "outside.exe");
        File.Copy(stubExe, outsideExe);
        ValidatedTrainerLaunchTarget traversal = TrainerLaunchValidator.Validate(new TrainerLaunchRequest
        {
            SessionId = "s1",
            SandboxRoot = temp.SandboxRoot,
            SandboxRelativePath = @"..\outside.exe",
            Sha256 = realSha,
        });
        Assert.False(traversal.Succeeded);
        Assert.Equal("PATH_TRAVERSAL_REJECTED", traversal.ErrorCode);

        // 3. Absolute path rejected.
        ValidatedTrainerLaunchTarget rooted = TrainerLaunchValidator.Validate(new TrainerLaunchRequest
        {
            SessionId = "s1",
            SandboxRoot = temp.SandboxRoot,
            SandboxRelativePath = outsideExe,
            Sha256 = realSha,
        });
        Assert.False(rooted.Succeeded);
        Assert.Equal("PATH_OUTSIDE_SANDBOX", rooted.ErrorCode);

        // 4. WorkingDirectory outside sandbox rejected.
        ValidatedTrainerLaunchTarget badWorkDir = TrainerLaunchValidator.Validate(new TrainerLaunchRequest
        {
            SessionId = "s1",
            SandboxRoot = temp.SandboxRoot,
            SandboxRelativePath = relPath,
            WorkingDirectory = temp.RootDir,
            Sha256 = realSha,
        });
        Assert.False(badWorkDir.Succeeded);
        Assert.Equal("WORKDIR_OUTSIDE_SANDBOX", badWorkDir.ErrorCode);

        // 5. SHA-256 mismatch rejected.
        string tamperedSha = new string('a', 64);
        ValidatedTrainerLaunchTarget badHash = TrainerLaunchValidator.Validate(new TrainerLaunchRequest
        {
            SessionId = "s1",
            SandboxRoot = temp.SandboxRoot,
            SandboxRelativePath = relPath,
            Sha256 = tamperedSha,
        });
        Assert.False(badHash.Succeeded);
        Assert.Equal("SHA256_MISMATCH", badHash.ErrorCode);
    }

    [NonElevatedFact]
    public async Task RealWorkerProcess_ForceKilled_KernelKillOnJobCloseWipesEntireTestStubTree()
    {
        using var temp = new TempWorkerSandbox();
        string pidDir = temp.CreateSubDirectory("pids-worker-kill");
        string stubExe = temp.StageTestStubWithConfig(new
        {
            depth = 2,
            childrenPerNode = 1,
            pidDirectory = pidDir,
            maxLifetimeMs = 30000,
        });

        string workerExe = LocateWorkerExecutable();
        string pipeName = temp.Paths.CreateTrainerSessionPipeName();
        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [workerExe],
        });
        server.StartListening();

        using Process workerProc = StartWorkerProcess(workerExe, pipeName, server.LaunchNonce);
        try
        {
            AuthenticatedTrainerWorkerInfo auth = await server.WaitForAuthenticatedClientAsync(workerProc.Id, TimeSpan.FromSeconds(8));
            Assert.Equal(workerProc.Id, auth.WorkerPid);

            TrainerLaunchResponse? launchReply = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
                TrainerIpcMethods.Launch,
                new TrainerLaunchRequest
                {
                    SessionId = "sess-kill",
                    SandboxRoot = temp.SandboxRoot,
                    SandboxRelativePath = Path.GetRelativePath(temp.SandboxRoot, stubExe),
                    Sha256 = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe),
                    HideOriginalWindow = true,
                },
                TimeSpan.FromSeconds(8));

            Assert.NotNull(launchReply);
            Assert.True(launchReply!.Succeeded, launchReply.ErrorMessage);

            int[] treePids = await WaitForPidCountAsync(pidDir, expectedCount: 3, TimeSpan.FromSeconds(8));
            Assert.All(treePids, pid => Assert.True(SafeProcessInspector.IsProcessAlive(pid)));

            // Forcefully kill Spawn.TrainerWorker.exe directly (no RPC stop!) -> OS kernel KILL_ON_JOB_CLOSE must wipe tree.
            workerProc.Kill(entireProcessTree: false);
            Assert.True(workerProc.WaitForExit(5000));

            await WaitUntilAllDeadAsync(treePids, TimeSpan.FromSeconds(5));
            Assert.All(treePids, pid => Assert.False(SafeProcessInspector.IsProcessAlive(pid), $"PID {pid} leaked after Worker kill!"));
        }
        finally
        {
            if (!workerProc.HasExited)
            {
                try { workerProc.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    [NonElevatedFact]
    public async Task RealWorkerProcess_HostDisconnectsPipe_WorkerSelfDestructsAndPurgesTestStubTree()
    {
        using var temp = new TempWorkerSandbox();
        string pidDir = temp.CreateSubDirectory("pids-pipe-drop");
        string stubExe = temp.StageTestStubWithConfig(new
        {
            depth = 2,
            childrenPerNode = 1,
            pidDirectory = pidDir,
            maxLifetimeMs = 30000,
        });

        string workerExe = LocateWorkerExecutable();
        string pipeName = temp.Paths.CreateTrainerSessionPipeName();
        var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [workerExe],
        });
        server.StartListening();

        using Process workerProc = StartWorkerProcess(workerExe, pipeName, server.LaunchNonce);
        try
        {
            _ = await server.WaitForAuthenticatedClientAsync(workerProc.Id, TimeSpan.FromSeconds(8));

            TrainerLaunchResponse? launchReply = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
                TrainerIpcMethods.Launch,
                new TrainerLaunchRequest
                {
                    SessionId = "sess-drop",
                    SandboxRoot = temp.SandboxRoot,
                    SandboxRelativePath = Path.GetRelativePath(temp.SandboxRoot, stubExe),
                    Sha256 = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe),
                    HideOriginalWindow = true,
                },
                TimeSpan.FromSeconds(8));

            Assert.NotNull(launchReply);
            Assert.True(launchReply!.Succeeded, launchReply.ErrorMessage);

            int[] treePids = await WaitForPidCountAsync(pidDir, expectedCount: 3, TimeSpan.FromSeconds(8));
            Assert.All(treePids, pid => Assert.True(SafeProcessInspector.IsProcessAlive(pid)));

            // Abruptly disconnect Host pipe without sending trainer.stop -> Worker must self-destruct & wipe entire tree.
            await server.DisposeAsync();

            Assert.True(workerProc.WaitForExit(5000), "Spawn.TrainerWorker did not self-destruct after Host pipe disconnection.");
            await WaitUntilAllDeadAsync(treePids, TimeSpan.FromSeconds(5));
            Assert.All(treePids, pid => Assert.False(SafeProcessInspector.IsProcessAlive(pid), $"PID {pid} survived Host pipe disconnect!"));
        }
        finally
        {
            await server.DisposeAsync();
            if (!workerProc.HasExited)
            {
                try { workerProc.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task TrainerWorkerApplication_InProcessRpc_HandlesVisibilityAudioFallbackDefenderWmiMockAndStop()
    {
        using var temp = new TempWorkerSandbox();
        string pidDir = temp.CreateSubDirectory("pids-inproc");
        string stubExe = temp.StageTestStubWithConfig(new
        {
            depth = 0,
            childrenPerNode = 0,
            pidDirectory = pidDir,
            maxLifetimeMs = 20000,
        });

        string pipeName = temp.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [currentExe],
        });
        server.StartListening();

        var fakeWindow = new FakeWindowInterop([new nint(0x1111), new nint(0x2222)]);
        var fakeAudio = new FakeAudioFallbackExecutor();
        var fakeDefender = new FakeDefenderWmiExecutor();
        var fakeSender = new FakeInputSender();
        var cleanProbe = new FakeAntiCheatSystemProbe();

        var app = new TrainerWorkerApplication(
            new TrainerWorkerCliOptions(currentPid, pipeName, server.LaunchNonce, currentExe),
            hotkeyProxy: new TrainerHotkeyProxy(fakeSender, Win32VirtualKeyScanCodeMapper.Instance, new NoOpWaiter()),
            windowInterop: fakeWindow,
            audioFallbackExecutor: fakeAudio,
            defenderWmiExecutor: fakeDefender,
            antiCheatProbe: cleanProbe);

        Task<int> workerRunTask = app.RunAsync();
        _ = await server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));

        // 1. Launch stub
        TrainerLaunchResponse? launch = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
            TrainerIpcMethods.Launch,
            new TrainerLaunchRequest
            {
                SessionId = "sess-rpc",
                SandboxRoot = temp.SandboxRoot,
                SandboxRelativePath = Path.GetRelativePath(temp.SandboxRoot, stubExe),
                Sha256 = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe),
            });
        Assert.True(launch?.Succeeded);

        // 2. Window visibility (uses fake interop, zero visible windows)
        TrainerSetWindowVisibilityResponse? vis = await server.InvokeAsync<TrainerSetWindowVisibilityRequest, TrainerSetWindowVisibilityResponse>(
            TrainerIpcMethods.SetWindowVisibility,
            new TrainerSetWindowVisibilityRequest { SessionId = "sess-rpc", Visible = false });
        Assert.True(vis?.Succeeded);
        Assert.Equal(2, vis!.AffectedWindowCount);
        Assert.Equal([(new nint(0x1111), false), (new nint(0x2222), false)], fakeWindow.VisibilityCalls);

        // 3. Audio fallback (mock)
        TrainerSetAudioFallbackResponse? audio = await server.InvokeAsync<TrainerSetAudioFallbackRequest, TrainerSetAudioFallbackResponse>(
            TrainerIpcMethods.SetAudioFallback,
            new TrainerSetAudioFallbackRequest { SessionId = "sess-rpc", Pids = [launch!.TrainerPid], Mute = true, Volume = 0 });
        Assert.True(audio?.Succeeded);
        Assert.Equal(1, audio!.AffectedSessionCount);

        // 4. Defender WMI exclusion (mock, zero real Defender write)
        TrainerAddDefenderExclusionResponse? defResp = await server.InvokeAsync<TrainerAddDefenderExclusionRequest, TrainerAddDefenderExclusionResponse>(
            TrainerIpcMethods.AddDefenderExclusion,
            new TrainerAddDefenderExclusionRequest { SandboxPath = temp.SandboxRoot });
        Assert.True(defResp?.Succeeded);
        Assert.True(defResp!.VerifiedByReadback);
        Assert.Equal(DefenderExclusionStatus.AddedAndVerified, defResp.Status);
        Assert.Equal(temp.SandboxRoot, fakeDefender.LastRequestedPath);

        // 5. Hotkey proxy (mock, zero real SendInput)
        TrainerTriggerHotkeyResponse? hk = await server.InvokeAsync<TrainerTriggerHotkeyRequest, TrainerTriggerHotkeyResponse>(
            TrainerIpcMethods.TriggerHotkey,
            new TrainerTriggerHotkeyRequest { SessionId = "sess-rpc", EntryId = "hp", VirtualKey = 0x61 });
        Assert.True(hk?.Succeeded);
        Assert.Equal(2, fakeSender.SentCount);

        // 6. Stop cleanly (WM_CLOSE posted to fake windows, then TerminateJobObject since fake windows don't exit stub)
        TrainerStopResponse? stop = await server.InvokeAsync<TrainerStopRequest, TrainerStopResponse>(
            TrainerIpcMethods.Stop,
            new TrainerStopRequest { SessionId = "sess-rpc", GracefulTimeoutMs = 150 });
        Assert.True(stop?.Succeeded);
        Assert.Equal(2, fakeWindow.CloseCalls.Count);

        int exitCode = await workerRunTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exitCode);
        Assert.False(SafeProcessInspector.IsProcessAlive(launch.TrainerPid));
    }

    [Fact]
    public async Task RealWorkerProcess_WrongExpectedHostPath_RefusesConnectionAndExitsNonZeroImmediately()
    {
        using var temp = new TempWorkerSandbox();
        string workerExe = LocateWorkerExecutable();
        string pipeName = temp.Paths.CreateTrainerSessionPipeName();

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [workerExe],
        });
        server.StartListening();

        // Do NOT pass --test-expected-host-path -> Worker strictly requires Spawn.Host.exe / spawn.exe / spawn-dev.exe,
        // so connecting to testhost.exe MUST be rejected and Worker MUST exit immediately with exit code 4.
        var psi = new ProcessStartInfo
        {
            FileName = workerExe,
            WorkingDirectory = Path.GetDirectoryName(workerExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--host-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--nonce");
        psi.ArgumentList.Add(server.LaunchNonce);

        using Process proc = Process.Start(psi)!;
        Assert.True(proc.WaitForExit(5000), "Worker should exit immediately when parent Host path does not match Spawn.Host.");
        Assert.Equal(4, proc.ExitCode);
        Assert.False(server.IsAuthenticated);
    }

    [Fact]
    public void ComDefenderWmiExecutor_PathValidation_RejectsDriveRootsAndBroadUserDirectories()
    {
        Assert.False(ComDefenderWmiExecutor.TryValidateSandboxExclusionPath(@"C:\", out _, out _));
        Assert.False(ComDefenderWmiExecutor.TryValidateSandboxExclusionPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), out _, out _));
        Assert.False(ComDefenderWmiExecutor.TryValidateSandboxExclusionPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), out _, out _));

        string validSandbox = Path.Combine(Path.GetTempPath(), "spawn-tests-defender", "vault", "Sandbox", "Trainers");
        Assert.True(ComDefenderWmiExecutor.TryValidateSandboxExclusionPath(validSandbox, out string normalized, out _));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(validSandbox)), normalized);
    }

    [Fact]
    public void TrainerWorkerApplication_TryParseArguments_RestrictsTestFlagsToSourceNonElevatedTestPipesOnly()
    {
        string testPipe = "spawn-test-ipc-12345678-trainer-abc";
        string prodPipe = "spawn-dev-ipc-12345678-trainer-abc";
        string nonce = Convert.ToHexString(new byte[32]);

        Assert.True(TrainerWorkerApplication.TryParseArguments(
            ["--host-pid", "1234", "--pipe", testPipe, "--nonce", nonce, "--test-expected-host-path", @"C:\Fake\testhost.exe", "--test-skip-live-ac-probe"],
            out TrainerWorkerCliOptions? testOpts));
        Assert.NotNull(testOpts);
        if (WorkerIntegrityHelper.IsCurrentProcessElevated())
        {
            // Elevated (High IL) processes must refuse the test-only flags even on a test pipe.
            Assert.Null(testOpts!.ExpectedHostExecutablePath);
            Assert.False(testOpts.SkipLiveAntiCheatProbeForTest);
        }
        else
        {
            Assert.Equal(@"C:\Fake\testhost.exe", testOpts!.ExpectedHostExecutablePath);
            Assert.True(testOpts.SkipLiveAntiCheatProbeForTest);
        }

        // Non-test pipe MUST strip BOTH --test-expected-host-path and --test-skip-live-ac-probe!
        Assert.True(TrainerWorkerApplication.TryParseArguments(
            ["--host-pid", "1234", "--pipe", prodPipe, "--nonce", nonce, "--test-expected-host-path", @"C:\Fake\testhost.exe", "--test-skip-live-ac-probe"],
            out TrainerWorkerCliOptions? prodOpts));
        Assert.NotNull(prodOpts);
        Assert.Null(prodOpts!.ExpectedHostExecutablePath);
        Assert.False(prodOpts.SkipLiveAntiCheatProbeForTest);
    }

    [Fact]
    public async Task TrainerWorkerApplication_IndependentSafetyGate_RejectsActiveAntiCheatProcessSessionDriverAndStrictVanguard()
    {
        using var temp = new TempWorkerSandbox();
        string pidDir = temp.CreateSubDirectory("pids-ac-gate");
        string stubExe = temp.StageTestStubWithConfig(new
        {
            depth = 0,
            childrenPerNode = 0,
            pidDirectory = pidDir,
            maxLifetimeMs = 15000,
        });
        string relPath = Path.GetRelativePath(temp.SandboxRoot, stubExe);
        string sha256 = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe);

        string pipeName = temp.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [currentExe],
        });
        server.StartListening();

        var probe = new FakeAntiCheatSystemProbe();
        var app = new TrainerWorkerApplication(
            new TrainerWorkerCliOptions(currentPid, pipeName, server.LaunchNonce, currentExe),
            antiCheatProbe: probe);

        Task<int> workerRunTask = app.RunAsync();
        _ = await server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));

        // 1. Active AC daemon process -> ANTI_CHEAT_BLOCKED
        probe.Processes = [new SafeProcessEntry(4242, 4, "SGuard64.exe", null)];
        TrainerLaunchResponse? procBlocked = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
            TrainerIpcMethods.Launch,
            new TrainerLaunchRequest
            {
                SessionId = "ac-proc",
                SandboxRoot = temp.SandboxRoot,
                SandboxRelativePath = relPath,
                Sha256 = sha256,
            });
        Assert.NotNull(procBlocked);
        Assert.False(procBlocked!.Succeeded);
        Assert.Equal("ANTI_CHEAT_BLOCKED", procBlocked.ErrorCode);

        // 2. Active session kernel driver (BEDaisy.sys) -> ANTI_CHEAT_BLOCKED
        probe.Processes = [];
        probe.LoadedDrivers = ["BEDaisy.sys"];
        TrainerLaunchResponse? driverBlocked = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
            TrainerIpcMethods.Launch,
            new TrainerLaunchRequest
            {
                SessionId = "ac-drv",
                SandboxRoot = temp.SandboxRoot,
                SandboxRelativePath = relPath,
                Sha256 = sha256,
            });
        Assert.NotNull(driverBlocked);
        Assert.False(driverBlocked!.Succeeded);
        Assert.Equal("ANTI_CHEAT_BLOCKED", driverBlocked.ErrorCode);

        // 3. vgk.sys idle loaded + StrictVanguardMode = true -> ANTI_CHEAT_BLOCKED
        probe.LoadedDrivers = ["vgk.sys"];
        TrainerLaunchResponse? strictVgkBlocked = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
            TrainerIpcMethods.Launch,
            new TrainerLaunchRequest
            {
                SessionId = "ac-vgk-strict",
                SandboxRoot = temp.SandboxRoot,
                SandboxRelativePath = relPath,
                Sha256 = sha256,
                StrictVanguardMode = true,
            });
        Assert.NotNull(strictVgkBlocked);
        Assert.False(strictVgkBlocked!.Succeeded);
        Assert.Equal("ANTI_CHEAT_BLOCKED", strictVgkBlocked.ErrorCode);

        // 4. Same vgk.sys idle loaded (no vgc.exe) + StrictVanguardMode = false -> allowed
        TrainerLaunchResponse? nonStrictAllowed = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
            TrainerIpcMethods.Launch,
            new TrainerLaunchRequest
            {
                SessionId = "ac-vgk-relaxed",
                SandboxRoot = temp.SandboxRoot,
                SandboxRelativePath = relPath,
                Sha256 = sha256,
                StrictVanguardMode = false,
                HideOriginalWindow = true,
            });
        Assert.NotNull(nonStrictAllowed);
        Assert.True(nonStrictAllowed!.Succeeded, nonStrictAllowed.ErrorMessage);

        _ = await server.InvokeAsync<TrainerStopRequest, TrainerStopResponse>(
            TrainerIpcMethods.Stop,
            new TrainerStopRequest { SessionId = "ac-vgk-relaxed", GracefulTimeoutMs = 150 });
        Assert.Equal(0, await workerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TrainerWorkerApplication_IndependentSafetyGate_EnforcesPersistedGuardLockFailClosed()
    {
        using var temp = new TempWorkerSandbox();
        string pidDir = temp.CreateSubDirectory("pids-guard-lock");
        string stubExe = temp.StageTestStubWithConfig(new
        {
            depth = 0,
            childrenPerNode = 0,
            pidDirectory = pidDir,
            maxLifetimeMs = 15000,
        });
        string relPath = Path.GetRelativePath(temp.SandboxRoot, stubExe);
        string sha256 = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe);
        string guardFile = temp.Paths.AntiCheatGuardStateFilePath;

        string pipeName = temp.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [currentExe],
        });
        server.StartListening();

        var fixedNow = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedWorkerTimeProvider(fixedNow);
        var app = new TrainerWorkerApplication(
            new TrainerWorkerCliOptions(currentPid, pipeName, server.LaunchNonce, currentExe),
            antiCheatProbe: new FakeAntiCheatSystemProbe(),
            timeProvider: clock);

        Task<int> workerRunTask = app.RunAsync();
        _ = await server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));

        async Task<TrainerLaunchResponse?> InvokeWithGuardJsonAsync(string sessionId, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guardFile)!);
            File.WriteAllText(guardFile, json, Encoding.UTF8);
            return await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
                TrainerIpcMethods.Launch,
                new TrainerLaunchRequest
                {
                    SessionId = sessionId,
                    SandboxRoot = temp.SandboxRoot,
                    SandboxRelativePath = relPath,
                    Sha256 = sha256,
                    GuardStateFilePath = guardFile,
                    HideOriginalWindow = true,
                });
        }

        // 1. ProtectedRunning -> GUARD_LOCKED
        TrainerLaunchResponse? r1 = await InvokeWithGuardJsonAsync("g1", JsonSerializer.Serialize(new
        {
            Phase = AntiCheatGuardPhase.ProtectedRunning,
            ActiveProtectedGameId = "valorant",
            WrittenAtUtc = fixedNow,
        }));
        Assert.False(r1!.Succeeded);
        Assert.Equal("GUARD_LOCKED", r1.ErrorCode);

        // 2. ResidualGuardWatch -> GUARD_LOCKED
        TrainerLaunchResponse? r2 = await InvokeWithGuardJsonAsync("g2", JsonSerializer.Serialize(new
        {
            Phase = AntiCheatGuardPhase.ResidualGuardWatch,
            WrittenAtUtc = fixedNow,
        }));
        Assert.False(r2!.Succeeded);
        Assert.Equal("GUARD_LOCKED", r2.ErrorCode);

        // 3. Active CooldownLocked (+10s remaining) -> GUARD_LOCKED
        TrainerLaunchResponse? r3 = await InvokeWithGuardJsonAsync("g3", JsonSerializer.Serialize(new
        {
            Phase = AntiCheatGuardPhase.CooldownLocked,
            CooldownEndsAtUtc = fixedNow.AddSeconds(10),
            WrittenAtUtc = fixedNow,
        }));
        Assert.False(r3!.Succeeded);
        Assert.Equal("GUARD_LOCKED", r3.ErrorCode);

        // 4. Corrupted JSON -> fail-closed GUARD_LOCKED
        TrainerLaunchResponse? r4 = await InvokeWithGuardJsonAsync("g4", "{ corrupted guard json");
        Assert.False(r4!.Succeeded);
        Assert.Equal("GUARD_LOCKED", r4.ErrorCode);

        // 5. Expired CooldownLocked (-1s) -> allowed!
        TrainerLaunchResponse? r5 = await InvokeWithGuardJsonAsync("g5", JsonSerializer.Serialize(new
        {
            Phase = AntiCheatGuardPhase.CooldownLocked,
            CooldownEndsAtUtc = fixedNow.AddSeconds(-1),
            WrittenAtUtc = fixedNow.AddSeconds(-16),
        }));
        Assert.True(r5!.Succeeded, r5.ErrorMessage);

        _ = await server.InvokeAsync<TrainerStopRequest, TrainerStopResponse>(
            TrainerIpcMethods.Stop,
            new TrainerStopRequest { SessionId = "g5", GracefulTimeoutMs = 150 });
        Assert.Equal(0, await workerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void TrainerSandboxJob_TryResolveShortPathIfNonAscii_PreservesAsciiAndSafelyHandlesNonAscii()
    {
        using var temp = new TempWorkerSandbox();
        string asciiDir = temp.CreateSubDirectory("ascii-only");
        Assert.Equal(asciiDir, TrainerSandboxJob.TryResolveShortPathIfNonAscii(asciiDir));

        string nonAsciiDir = temp.CreateSubDirectory("中文修改器目录");
        string resolved = TrainerSandboxJob.TryResolveShortPathIfNonAscii(nonAsciiDir);
        Assert.True(Directory.Exists(resolved), $"Resolved path must exist: {resolved}");
    }

    private static string LocateWorkerExecutable()
    {
        string coLocated = Path.Combine(AppContext.BaseDirectory, "Spawn.TrainerWorker.exe");
        if (File.Exists(coLocated))
        {
            return coLocated;
        }

        throw new FileNotFoundException($"Spawn.TrainerWorker.exe not found in test output directory {AppContext.BaseDirectory}");
    }

    private static Process StartWorkerProcess(string workerExePath, string pipeName, string nonce)
    {
        var psi = new ProcessStartInfo
        {
            FileName = workerExePath,
            WorkingDirectory = Path.GetDirectoryName(workerExePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--host-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--nonce");
        psi.ArgumentList.Add(nonce);
        psi.ArgumentList.Add("--test-expected-host-path");
        psi.ArgumentList.Add(Environment.ProcessPath!);
        psi.ArgumentList.Add("--test-skip-live-ac-probe");

        Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Spawn.TrainerWorker.exe.");
        try
        {
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Ignore priority race if process exited immediately.
        }

        return proc;
    }

    private static async Task<int[]> WaitForPidCountAsync(string pidDir, int expectedCount, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (Directory.Exists(pidDir))
            {
                string[] files = Directory.GetFiles(pidDir, "*.pid");
                if (files.Length >= expectedCount)
                {
                    var pids = new List<int>(files.Length);
                    foreach (string file in files)
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                        {
                            pids.Add(pid);
                        }
                    }

                    if (pids.Count >= expectedCount)
                    {
                        return pids.ToArray();
                    }
                }
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} TestStub PIDs in {pidDir}.");
    }

    private static async Task WaitUntilAllDeadAsync(IReadOnlyList<int> pids, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (pids.All(pid => !SafeProcessInspector.IsProcessAlive(pid)))
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private sealed class TempWorkerSandbox : IDisposable
    {
        public string RootDir { get; }
        public SpawnPaths Paths { get; }
        public string SandboxRoot => Paths.TrainerSandboxDirectory;

        public TempWorkerSandbox()
        {
            RootDir = Path.Combine(Path.GetTempPath(), $"spawn-w05-worker-{Guid.NewGuid():N}");
            string dataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(dataDir);
            Paths = SpawnPaths.CreateIsolated(dataDir);
            Paths.EnsureDirectoriesExist();
        }

        public string CreateSubDirectory(string name)
        {
            string dir = Path.Combine(RootDir, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string StageTestStubWithConfig(object config)
        {
            string sourceDir = Path.Combine(AppContext.BaseDirectory, "TestStub");
            string targetDir = Path.Combine(SandboxRoot, "game-test", $"trainer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir, "Spawn.TrainerWorker.TestStub.*"))
            {
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
            }

            string configPath = Path.Combine(targetDir, "teststub.config.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(config), Encoding.UTF8);

            string stagedExe = Path.Combine(targetDir, "Spawn.TrainerWorker.TestStub.exe");
            Assert.True(File.Exists(stagedExe), $"Staged TestStub missing at {stagedExe}");
            return stagedExe;
        }

        public void Dispose()
        {
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    if (Directory.Exists(RootDir))
                    {
                        Directory.Delete(RootDir, recursive: true);
                    }

                    return;
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }
        }
    }

    private sealed class FakeWindowInterop(IReadOnlyList<nint> windows) : ITrainerWindowInterop
    {
        public List<nint> CloseCalls { get; } = [];
        public List<(nint Hwnd, bool Visible)> VisibilityCalls { get; } = [];

        public IReadOnlyList<nint> EnumerateTopLevelWindowsForProcesses(IReadOnlySet<int> processIds) => windows;

        public bool PostCloseMessage(nint hwnd)
        {
            CloseCalls.Add(hwnd);
            return true;
        }

        public bool SetWindowVisibility(nint hwnd, bool visible)
        {
            VisibilityCalls.Add((hwnd, visible));
            return true;
        }
    }

    private sealed class FakeAudioFallbackExecutor : IWorkerAudioFallbackExecutor
    {
        public Task<TrainerSetAudioFallbackResponse> ApplyAsync(TrainerSetAudioFallbackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrainerSetAudioFallbackResponse
            {
                Succeeded = true,
                AffectedSessionCount = request.Pids.Count,
            });
    }

    private sealed class FakeDefenderWmiExecutor : IDefenderWmiExecutor
    {
        public string? LastRequestedPath { get; private set; }

        public Task<TrainerAddDefenderExclusionResponse> AddAndVerifyExclusionAsync(string sandboxPath, CancellationToken cancellationToken = default)
        {
            LastRequestedPath = sandboxPath;
            return Task.FromResult(new TrainerAddDefenderExclusionResponse
            {
                Succeeded = true,
                VerifiedByReadback = true,
                Status = DefenderExclusionStatus.AddedAndVerified,
                WmiReturnValue = 0,
            });
        }
    }

    private sealed class FakeInputSender : IHotkeyInputSender
    {
        public int SentCount { get; private set; }
        public uint SendKeyboardInput(KeyboardInputEvent inputEvent)
        {
            SentCount++;
            return 1;
        }
    }

    private sealed class NoOpWaiter : IPrecisionWaiter
    {
        public void WaitMilliseconds(int milliseconds, CancellationToken cancellationToken) { }
    }

    private sealed class FakeAntiCheatSystemProbe : IAntiCheatSystemProbe
    {
        public IReadOnlyList<SafeProcessEntry> Processes { get; set; } = [];
        public IReadOnlyList<string> LoadedDrivers { get; set; } = [];
        public IReadOnlyList<ActiveKernelDriverInfo> ActiveDriverServices { get; set; } = [];

        public IReadOnlyList<SafeProcessEntry> EnumerateProcesses(bool resolveFullPaths) => Processes;
        public IReadOnlyList<string> EnumerateLoadedDriverBaseNames() => LoadedDrivers;
        public IReadOnlyList<ActiveKernelDriverInfo> EnumerateActiveDriverServices() => ActiveDriverServices;
    }

    private sealed class FixedWorkerTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
