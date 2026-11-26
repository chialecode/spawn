using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;
using Spawn.Core.Ipc;
using Xunit;

namespace Spawn.TrainerWorker.UnitTests;

/// <summary>
/// Regression tests for Issue #63 P1-4:
/// post-launch 2.5-second auto-hide re-sweep for delayed top-level windows inside the sandbox job,
/// immediate cancellation when the user explicitly requests showing the original window,
/// and honest <see cref="TrainerSetWindowVisibilityResponse.AffectedWindowCount"/> reporting when zero windows exist.
/// </summary>
public sealed class TrainerWorkerAutoHideAndVisibilityTests
{
    [Fact]
    public async Task TrainerWorkerApplication_AutoHideSweep_HidesDelayedWindowAndStopsWhenUserRequestsShow()
    {
        using var temp = new TempSandbox();
        string pidDir = temp.CreateSubDirectory("pids-autohide");
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

        var dynamicWindowInterop = new DynamicDelayedWindowInterop();
        var app = new TrainerWorkerApplication(
            new TrainerWorkerCliOptions(currentPid, pipeName, server.LaunchNonce, currentExe),
            windowInterop: dynamicWindowInterop,
            antiCheatProbe: new CleanAntiCheatProbe(),
            autoHideWindowDuration: TimeSpan.FromMilliseconds(2500),
            autoHidePollInterval: TimeSpan.FromMilliseconds(40));

        Task<int> workerRunTask = app.RunAsync();
        _ = await server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));

        TrainerLaunchResponse? launch = await server.InvokeAsync<TrainerLaunchRequest, TrainerLaunchResponse>(
            TrainerIpcMethods.Launch,
            new TrainerLaunchRequest
            {
                SessionId = "sess-autohide",
                SandboxRoot = temp.SandboxRoot,
                SandboxRelativePath = Path.GetRelativePath(temp.SandboxRoot, stubExe),
                Sha256 = TrainerLaunchValidator.ComputeFileSha256Hex(stubExe),
                HideOriginalWindow = true,
            });
        Assert.NotNull(launch);
        Assert.True(launch!.Succeeded, launch.ErrorMessage);

        // Initially no windows exist -> honest zero AffectedWindowCount!
        TrainerSetWindowVisibilityResponse? zeroResp = await server.InvokeAsync<TrainerSetWindowVisibilityRequest, TrainerSetWindowVisibilityResponse>(
            TrainerIpcMethods.SetWindowVisibility,
            new TrainerSetWindowVisibilityRequest { SessionId = "sess-autohide", Visible = false });
        Assert.True(zeroResp?.Succeeded);
        Assert.Equal(0, zeroResp!.AffectedWindowCount);

        // Simulate a delayed GUI trainer window appearing 120ms AFTER process launch.
        await Task.Delay(120);
        nint delayedHwnd = new(0x7788);
        dynamicWindowInterop.AddWindow(delayedHwnd);

        // Auto-hide sweep must detect and hide the delayed window promptly within the 2.5s window!
        bool hiddenBySweep = await WaitUntilAsync(
            () => dynamicWindowInterop.Calls.Any(c => c.Hwnd == delayedHwnd && !c.Visible),
            TimeSpan.FromSeconds(2));
        Assert.True(hiddenBySweep, "Expected post-launch auto-hide re-sweep to hide newly appearing top-level window.");

        // User explicitly clicks "显示原窗口" (Visible = true) -> sweep MUST cancel immediately and NEVER re-hide!
        TrainerSetWindowVisibilityResponse? showResp = await server.InvokeAsync<TrainerSetWindowVisibilityRequest, TrainerSetWindowVisibilityResponse>(
            TrainerIpcMethods.SetWindowVisibility,
            new TrainerSetWindowVisibilityRequest { SessionId = "sess-autohide", Visible = true });
        Assert.True(showResp?.Succeeded);
        Assert.Equal(1, showResp!.AffectedWindowCount);

        int hideCountAfterUserShow = dynamicWindowInterop.Calls.Count(c => c.Hwnd == delayedHwnd && !c.Visible);
        await Task.Delay(220);
        int hideCountLater = dynamicWindowInterop.Calls.Count(c => c.Hwnd == delayedHwnd && !c.Visible);
        Assert.Equal(hideCountAfterUserShow, hideCountLater);
        Assert.True(dynamicWindowInterop.Calls.Last(c => c.Hwnd == delayedHwnd).Visible);

        _ = await server.InvokeAsync<TrainerStopRequest, TrainerStopResponse>(
            TrainerIpcMethods.Stop,
            new TrainerStopRequest { SessionId = "sess-autohide", GracefulTimeoutMs = 150 });
        Assert.Equal(0, await workerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }

    private sealed class DynamicDelayedWindowInterop : ITrainerWindowInterop
    {
        private readonly ConcurrentQueue<nint> _windows = new();
        private readonly ConcurrentQueue<(nint Hwnd, bool Visible)> _calls = new();

        public IReadOnlyList<(nint Hwnd, bool Visible)> Calls => _calls.ToArray();

        public void AddWindow(nint hwnd) => _windows.Enqueue(hwnd);

        public IReadOnlyList<nint> EnumerateTopLevelWindowsForProcesses(IReadOnlySet<int> processIds) =>
            _windows.ToArray();

        public bool PostCloseMessage(nint hwnd) => true;

        public bool SetWindowVisibility(nint hwnd, bool visible)
        {
            _calls.Enqueue((hwnd, visible));
            return true;
        }
    }

    private sealed class CleanAntiCheatProbe : IAntiCheatSystemProbe
    {
        public IReadOnlyList<SafeProcessEntry> EnumerateProcesses(bool resolveFullPaths) => [];
        public IReadOnlyList<string> EnumerateLoadedDriverBaseNames() => [];
        public IReadOnlyList<ActiveKernelDriverInfo> EnumerateActiveDriverServices() => [];
    }

    private sealed class TempSandbox : IDisposable
    {
        public string RootDir { get; }
        public SpawnPaths Paths { get; }
        public string SandboxRoot => Paths.TrainerSandboxDirectory;

        public TempSandbox()
        {
            RootDir = Path.Combine(Path.GetTempPath(), $"spawn-autohide-{Guid.NewGuid():N}");
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

            File.WriteAllText(Path.Combine(targetDir, "teststub.config.json"), JsonSerializer.Serialize(config), Encoding.UTF8);
            return Path.Combine(targetDir, "Spawn.TrainerWorker.TestStub.exe");
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
                    Thread.Sleep(60);
                }
            }
        }
    }
}
