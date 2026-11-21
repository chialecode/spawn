using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Spawn.Contracts;
using Spawn.Core.Ipc;

namespace Spawn.Modules.Progress.Runtime;

/// <summary>
/// Result of launching <c>Spawn.TrainerWorker.exe</c> at Level 1 (Medium IL) or Level 2 (High IL UAC).
/// </summary>
public sealed record WorkerProcessLaunchResult(
    bool Succeeded,
    int WorkerProcessId = 0,
    TrainerElevationLevel ElevationLevel = TrainerElevationLevel.Level1MediumIl,
    string? WorkerExecutablePath = null,
    bool WasCancelledByUser = false,
    bool WorkerBinaryMissing = false,
    int? Win32ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Launches <c>Spawn.TrainerWorker.exe</c> via tiered on-demand elevation (D-11 / ADR-0006):
/// Level 1 uses <c>CreateProcessW</c> (0 UAC); Level 2 uses <c>ShellExecuteExW(Verb="runas", SEE_MASK_NOCLOSEPROCESS)</c>.
/// Never registers scheduled tasks, Windows services, or HKLM Run entries.
/// </summary>
public interface ITrainerWorkerLauncher
{
    string? ResolveWorkerExecutablePath();

    Task<WorkerProcessLaunchResult> LaunchWorkerAsync(
        TrainerElevationLevel elevationLevel,
        string pipeName,
        string launchNonce,
        CancellationToken cancellationToken = default);

    void TryTerminateWorkerProcess(int workerProcessId);
}

/// <summary>
/// Clean-Host compliant Win32 launcher for <c>Spawn.TrainerWorker.exe</c>.
/// </summary>
public sealed partial class Win32TrainerWorkerLauncher : ITrainerWorkerLauncher
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const uint SeeMaskFlagNoUi = 0x00000400;
    private const int SwHide = 0;
    private const int ErrorCancelled = 1223;

    private readonly string? _explicitWorkerExecutablePath;
    private readonly string? _testExpectedHostPath;

    public Win32TrainerWorkerLauncher(
        string? explicitWorkerExecutablePath = null,
        string? testExpectedHostPath = null)
    {
        _explicitWorkerExecutablePath = explicitWorkerExecutablePath;
        _testExpectedHostPath = testExpectedHostPath;
    }

    public string? ResolveWorkerExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_explicitWorkerExecutablePath))
        {
            string full = Path.GetFullPath(_explicitWorkerExecutablePath.Trim());
            return File.Exists(full) ? full : null;
        }

        foreach (string candidate in TrainerPipePathResolver.ResolveDefaultWorkerExecutablePaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public Task<WorkerProcessLaunchResult> LaunchWorkerAsync(
        TrainerElevationLevel elevationLevel,
        string pipeName,
        string launchNonce,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchNonce);

        string? workerExePath = ResolveWorkerExecutablePath();
        if (string.IsNullOrWhiteSpace(workerExePath) || !File.Exists(workerExePath))
        {
            return Task.FromResult(new WorkerProcessLaunchResult(
                Succeeded: false,
                ElevationLevel: elevationLevel,
                WorkerBinaryMissing: true,
                ErrorMessage: "未找到 Spawn.TrainerWorker.exe（预期位于 Workers\\Trainer\\Spawn.TrainerWorker.exe）。"));
        }

        string normalizedPipeName = pipeName.Trim();
        const string pipePrefix = @"\\.\pipe\";
        if (normalizedPipeName.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPipeName = normalizedPipeName[pipePrefix.Length..];
        }

        string arguments = BuildArguments(Environment.ProcessId, normalizedPipeName, launchNonce, _testExpectedHostPath);
        string workingDirectory = Path.GetDirectoryName(workerExePath)!;

        WorkerProcessLaunchResult result = elevationLevel == TrainerElevationLevel.Level2HighIlUac
            ? LaunchLevel2RunAsUac(workerExePath, arguments, workingDirectory)
            : LaunchLevel1MediumDirect(workerExePath, arguments, workingDirectory);

        return Task.FromResult(result);
    }

    public void TryTerminateWorkerProcess(int workerProcessId)
    {
        if (workerProcessId <= 4)
        {
            return;
        }

        try
        {
            using var proc = Process.GetProcessById(workerProcessId);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: false);
                _ = proc.WaitForExit(1000);
            }
        }
        catch
        {
            // Process already exited or access denied (e.g. elevated worker after self-destruct).
        }
    }

    internal static string BuildArguments(
        int hostPid,
        string pipeName,
        string launchNonce,
        string? testExpectedHostPath = null)
    {
        string baseArgs = string.Create(
            CultureInfo.InvariantCulture,
            $"--host-pid {hostPid} --pipe \"{pipeName}\" --nonce \"{launchNonce}\"");

        if (!string.IsNullOrWhiteSpace(testExpectedHostPath))
        {
            return $"{baseArgs} --test-expected-host-path \"{testExpectedHostPath}\"";
        }

        return baseArgs;
    }

    private static unsafe WorkerProcessLaunchResult LaunchLevel1MediumDirect(
        string workerExePath,
        string arguments,
        string workingDirectory)
    {
        var si = new StartupInfoW
        {
            cb = (uint)Marshal.SizeOf<StartupInfoW>(),
            dwFlags = 0x00000001, // STARTF_USESHOWWINDOW
            wShowWindow = SwHide,
        };

        char[] commandLine = $"\"{workerExePath}\" {arguments}\0".ToCharArray();

        bool ok;
        ProcessInformation pi;
        fixed (char* pApp = workerExePath)
        fixed (char* pCmd = commandLine)
        fixed (char* pDir = workingDirectory)
        {
            ok = CreateProcessW(
                pApp,
                pCmd,
                nint.Zero,
                nint.Zero,
                bInheritHandles: false,
                dwCreationFlags: CreateNoWindow,
                lpEnvironment: nint.Zero,
                lpCurrentDirectory: pDir,
                ref si,
                out pi);
        }

        if (!ok)
        {
            int err = Marshal.GetLastPInvokeError();
            return new WorkerProcessLaunchResult(
                Succeeded: false,
                ElevationLevel: TrainerElevationLevel.Level1MediumIl,
                WorkerExecutablePath: workerExePath,
                Win32ErrorCode: err,
                ErrorMessage: $"以 Level 1（Medium IL）启动 Spawn.TrainerWorker 失败（Win32 错误 {err}）。");
        }

        _ = CloseHandle(pi.hThread);
        _ = CloseHandle(pi.hProcess);

        return new WorkerProcessLaunchResult(
            Succeeded: true,
            WorkerProcessId: checked((int)pi.dwProcessId),
            ElevationLevel: TrainerElevationLevel.Level1MediumIl,
            WorkerExecutablePath: workerExePath);
    }

    private static unsafe WorkerProcessLaunchResult LaunchLevel2RunAsUac(
        string workerExePath,
        string arguments,
        string workingDirectory)
    {
        const string verb = "runas";

        fixed (char* pVerb = verb)
        fixed (char* pFile = workerExePath)
        fixed (char* pParams = arguments)
        fixed (char* pDir = workingDirectory)
        {
            var sei = new ShellExecuteInfoW
            {
                cbSize = (uint)sizeof(ShellExecuteInfoW),
                fMask = SeeMaskNoCloseProcess | SeeMaskNoAsync | SeeMaskFlagNoUi,
                hwnd = nint.Zero,
                lpVerb = pVerb,
                lpFile = pFile,
                lpParameters = pParams,
                lpDirectory = pDir,
                nShow = SwHide,
            };

            if (!ShellExecuteExW(ref sei))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ErrorCancelled)
                {
                    return new WorkerProcessLaunchResult(
                        Succeeded: false,
                        ElevationLevel: TrainerElevationLevel.Level2HighIlUac,
                        WorkerExecutablePath: workerExePath,
                        WasCancelledByUser: true,
                        Win32ErrorCode: err,
                        ErrorMessage: "用户已取消 UAC 提权授权。");
                }

                return new WorkerProcessLaunchResult(
                    Succeeded: false,
                    ElevationLevel: TrainerElevationLevel.Level2HighIlUac,
                    WorkerExecutablePath: workerExePath,
                    Win32ErrorCode: err,
                    ErrorMessage: $"以 Level 2（High IL UAC）启动 Spawn.TrainerWorker 失败（Win32 错误 {err}）。");
            }

            if (sei.hProcess == nint.Zero)
            {
                return new WorkerProcessLaunchResult(
                    Succeeded: false,
                    ElevationLevel: TrainerElevationLevel.Level2HighIlUac,
                    WorkerExecutablePath: workerExePath,
                    ErrorMessage: "ShellExecuteExW 未返回有效的 Worker 进程句柄。");
            }

            try
            {
                uint pid = GetProcessId(sei.hProcess);
                if (pid <= 4)
                {
                    int err = Marshal.GetLastPInvokeError();
                    return new WorkerProcessLaunchResult(
                        Succeeded: false,
                        ElevationLevel: TrainerElevationLevel.Level2HighIlUac,
                        WorkerExecutablePath: workerExePath,
                        Win32ErrorCode: err,
                        ErrorMessage: $"无法读取提权后 Worker 进程 PID（Win32 错误 {err}）。");
                }

                return new WorkerProcessLaunchResult(
                    Succeeded: true,
                    WorkerProcessId: checked((int)pid),
                    ElevationLevel: TrainerElevationLevel.Level2HighIlUac,
                    WorkerExecutablePath: workerExePath);
            }
            finally
            {
                _ = CloseHandle(sei.hProcess);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoW
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShellExecuteInfoW
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public char* lpVerb;
        public char* lpFile;
        public char* lpParameters;
        public char* lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public char* lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        char* lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        char* lpCurrentDirectory,
        ref StartupInfoW lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ref ShellExecuteInfoW pExecInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
