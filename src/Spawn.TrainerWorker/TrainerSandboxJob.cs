using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Spawn.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace Spawn.TrainerWorker;

/// <summary>
/// Result of atomically spawning an external trainer process inside <see cref="TrainerSandboxJob"/>.
/// </summary>
internal sealed record SandboxLaunchResult(
    bool Succeeded,
    int RootProcessId = 0,
    int Win32ErrorCode = 0,
    string? ErrorMessage = null);

/// <summary>
/// Result of stopping all processes inside <see cref="TrainerSandboxJob"/>.
/// </summary>
internal sealed record SandboxStopResult(
    bool Succeeded,
    bool TerminatedForcefully,
    int TerminatedProcessCount);

/// <summary>
/// Dedicated external-trainer Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000) = ON</c>,
/// <c>BREAKAWAY_OK = OFF</c>, <c>SILENT_BREAKAWAY_OK = OFF</c>, atomic <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>
/// process enrollment, and dedicated IOCP listener (<c>NEW_PROCESS</c> / <c>ACTIVE_PROCESS_ZERO</c>).
/// Intentionally separate from the non-killing game-tracking Job Object in <c>Spawn.Modules.Launcher</c>.
/// </summary>
internal sealed class TrainerSandboxJob : IDisposable
{
    private const uint JobObjectMsgActiveProcessZero = 4;
    private const uint JobObjectMsgNewProcess = 6;
    private const uint JobObjectMsgExitProcess = 7;
    private const uint JobObjectMsgAbnormalExitProcess = 8;

    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const nuint ShutdownCompletionKey = 0x53504157u; // "SPAW" sentinel (fits 32-bit & 64-bit nuint)
    private const uint ErrorMoreData = 234;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint StillActive = 259;

    private static unsafe HANDLE InvalidHandleValue => new((void*)-1);
    private static bool IsValidHandle(HANDLE handle) => !handle.IsNull && handle != InvalidHandleValue;

    private readonly Lock _stateGate = new();
    private readonly ConcurrentDictionary<int, byte> _trackedPids = new();
    private readonly TaskCompletionSource<int?> _activeProcessZeroTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HANDLE _jobHandle;
    private HANDLE _iocpHandle;
    private HANDLE _rootProcessHandle;
    private Thread? _iocpThread;
    private int _disposed;
    private int _launched;

    public int RootProcessId { get; private set; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public Task<int?> ActiveProcessZeroTask => _activeProcessZeroTcs.Task;

    public event Action<int, string?>? ProcessCreated;
    public event Action<int?>? ActiveProcessZero;

    public unsafe TrainerSandboxJob()
    {
        _jobHandle = PInvoke.CreateJobObject(null, default(PCWSTR));
        if (!IsValidHandle(_jobHandle))
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"CreateJobObjectW failed with Win32 error {err}.");
        }

        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
            // Strictly KILL_ON_JOB_CLOSE = ON; BREAKAWAY_OK = OFF; SILENT_BREAKAWAY_OK = OFF.
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            if (!PInvoke.SetInformationJobObject(
                _jobHandle,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                &limits,
                (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"SetInformationJobObject(ExtendedLimitInformation) failed with Win32 error {err}.");
            }

            _iocpHandle = PInvoke.CreateIoCompletionPort(InvalidHandleValue, HANDLE.Null, 0, 1);
            if (!IsValidHandle(_iocpHandle))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"CreateIoCompletionPort failed with Win32 error {err}.");
            }

            JOBOBJECT_ASSOCIATE_COMPLETION_PORT assoc = new()
            {
                CompletionKey = (void*)_jobHandle.Value,
                CompletionPort = _iocpHandle,
            };

            if (!PInvoke.SetInformationJobObject(
                _jobHandle,
                JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation,
                &assoc,
                (uint)sizeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT)))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"SetInformationJobObject(AssociateCompletionPort) failed with Win32 error {err}.");
            }

            _iocpThread = new Thread(IocpPumpLoop)
            {
                Name = "Spawn.TrainerSandboxJob.IOCP",
                IsBackground = true,
            };
            _iocpThread.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Queries the live OS kernel <c>LimitFlags</c> configured on this Job Object (for verification &amp; tests).
    /// </summary>
    public unsafe uint QueryLimitFlags()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
        uint returned = 0;
        if (!PInvoke.QueryInformationJobObject(
            _jobHandle,
            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
            &info,
            (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION),
            &returned))
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"QueryInformationJobObject(ExtendedLimitInformation) failed with Win32 error {err}.");
        }

        return (uint)info.BasicLimitInformation.LimitFlags;
    }

    /// <summary>
    /// Queries all currently active process IDs assigned to this Job Object directly from the kernel.
    /// </summary>
    public unsafe IReadOnlySet<int> QueryActiveProcessIds()
    {
        var result = new HashSet<int>();
        if (IsDisposed || _jobHandle.IsNull)
        {
            return result;
        }

        uint capacity = 64;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            nuint bytes = (nuint)(sizeof(uint) * 2) + (nuint)capacity * (nuint)sizeof(nuint);
            byte* buffer = (byte*)NativeMemory.AllocZeroed(bytes);
            try
            {
                uint returned = 0;
                bool ok = PInvoke.QueryInformationJobObject(
                    _jobHandle,
                    JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList,
                    buffer,
                    checked((uint)bytes),
                    &returned);

                if (!ok)
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err == ErrorMoreData)
                    {
                        uint assigned = *(uint*)buffer;
                        capacity = Math.Max(capacity * 2, assigned + 16);
                        continue;
                    }

                    break;
                }

                uint count = *((uint*)buffer + 1);
                nuint* pids = (nuint*)(buffer + 8);
                for (uint i = 0; i < count; i++)
                {
                    if (pids[i] > 4 && pids[i] <= int.MaxValue)
                    {
                        result.Add(checked((int)pids[i]));
                    }
                }

                return result;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        foreach (int tracked in _trackedPids.Keys)
        {
            result.Add(tracked);
        }

        return result;
    }

    /// <summary>
    /// Atomically launches <paramref name="executableFullPath"/> inside this Job Object using
    /// <c>InitializeProcThreadAttributeList</c> + <c>UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_JOB_LIST)</c>
    /// + <c>CreateProcessW(EXTENDED_STARTUPINFO_PRESENT)</c>. Passes zero arbitrary command-line arguments.
    /// </summary>
    public unsafe SandboxLaunchResult LaunchAtomically(
        string executableFullPath,
        string workingDirectory,
        bool hideOriginalWindow = false)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (Interlocked.Exchange(ref _launched, 1) != 0)
        {
            return new SandboxLaunchResult(false, ErrorMessage: "TrainerSandboxJob already launched a root process.");
        }

        nuint attrListSize = 0;
        _ = PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &attrListSize);
        if (attrListSize == 0)
        {
            int err = Marshal.GetLastPInvokeError();
            return new SandboxLaunchResult(false, Win32ErrorCode: err, ErrorMessage: $"InitializeProcThreadAttributeList size query failed ({err}).");
        }

        void* attrListMem = NativeMemory.AllocZeroed(attrListSize);
        bool attrListInitialized = false;
        try
        {
            var attrList = new LPPROC_THREAD_ATTRIBUTE_LIST(attrListMem);
            if (!PInvoke.InitializeProcThreadAttributeList(attrList, 1, 0, &attrListSize))
            {
                int err = Marshal.GetLastPInvokeError();
                return new SandboxLaunchResult(false, Win32ErrorCode: err, ErrorMessage: $"InitializeProcThreadAttributeList failed ({err}).");
            }

            attrListInitialized = true;

            // Keep the job handle value pinned on the stack for the lifetime of the attribute list.
            nint jobHandleValue = (nint)_jobHandle.Value;
            if (!PInvoke.UpdateProcThreadAttribute(
                attrList,
                0,
                ProcThreadAttributeJobList,
                &jobHandleValue,
                (nuint)sizeof(nint),
                null,
                null))
            {
                int err = Marshal.GetLastPInvokeError();
                return new SandboxLaunchResult(false, Win32ErrorCode: err, ErrorMessage: $"UpdateProcThreadAttribute(JOB_LIST) failed ({err}).");
            }

            STARTUPINFOEXW siEx = default;
            siEx.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            siEx.lpAttributeList = attrList;
            if (hideOriginalWindow)
            {
                siEx.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESHOWWINDOW;
                siEx.StartupInfo.wShowWindow = 0; // SW_HIDE
            }

            // After long-path whitelist & SHA-256 verification have succeeded, opportunistically map non-ASCII paths
            // to 8.3 short paths (when enabled on the NTFS volume) so legacy ANSI trainers launch cleanly.
            string launchExePath = TryResolveShortPathIfNonAscii(executableFullPath);
            string launchWorkDir = TryResolveShortPathIfNonAscii(workingDirectory);

            // Defense-in-depth: lpCommandLine contains strictly the quoted executable path, zero injected arguments.
            char[] cmdLineBuffer = $"\"{launchExePath}\"\0".ToCharArray();
            PROCESS_INFORMATION pi = default;
            PROCESS_CREATION_FLAGS creationFlags =
                PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT
                | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;

            bool created;
            fixed (char* pAppName = launchExePath)
            fixed (char* pCmdLine = cmdLineBuffer)
            fixed (char* pWorkDir = launchWorkDir)
            {
                created = PInvoke.CreateProcess(
                    new PCWSTR(pAppName),
                    new PWSTR(pCmdLine),
                    null,
                    null,
                    false,
                    creationFlags,
                    null,
                    new PCWSTR(pWorkDir),
                    &siEx.StartupInfo,
                    &pi);
            }

            if (!created)
            {
                int err = Marshal.GetLastPInvokeError();
                return new SandboxLaunchResult(
                    Succeeded: false,
                    Win32ErrorCode: err,
                    ErrorMessage: $"CreateProcessW failed with Win32 error {err}.");
            }

            _ = PInvoke.CloseHandle(pi.hThread);

            int rootPid = checked((int)pi.dwProcessId);
            lock (_stateGate)
            {
                RootProcessId = rootPid;
                _rootProcessHandle = pi.hProcess;
            }

            _trackedPids.TryAdd(rootPid, 0);
            return new SandboxLaunchResult(Succeeded: true, RootProcessId: rootPid);
        }
        finally
        {
            if (attrListInitialized)
            {
                PInvoke.DeleteProcThreadAttributeList(new LPPROC_THREAD_ATTRIBUTE_LIST(attrListMem));
            }

            NativeMemory.Free(attrListMem);
        }
    }

    /// <summary>
    /// Shows or hides all top-level windows belonging to processes currently inside this Job Object.
    /// </summary>
    public int SetWindowVisibility(bool visible, ITrainerWindowInterop? windowInterop = null)
    {
        if (IsDisposed)
        {
            return 0;
        }

        ITrainerWindowInterop interop = windowInterop ?? Win32TrainerWindowInterop.Instance;
        IReadOnlySet<int> pids = QueryActiveProcessIds();
        if (pids.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<nint> windows = interop.EnumerateTopLevelWindowsForProcesses(pids);
        int affected = 0;
        foreach (nint hwnd in windows)
        {
            if (interop.SetWindowVisibility(hwnd, visible))
            {
                affected++;
            }
        }

        return affected;
    }

    /// <summary>
    /// Stops all processes inside the Job Object: posts <c>WM_CLOSE</c> to top-level windows, waits up to
    /// <paramref name="gracefulTimeoutMs"/> (<c>1500ms</c> normal / <c>500ms</c> emergency), then calls
    /// <c>TerminateJobObject</c> and closes the job handle.
    /// </summary>
    public async Task<SandboxStopResult> StopAsync(
        int gracefulTimeoutMs,
        ITrainerWindowInterop? windowInterop = null,
        CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
        {
            return new SandboxStopResult(Succeeded: true, TerminatedForcefully: false, TerminatedProcessCount: 0);
        }

        ITrainerWindowInterop interop = windowInterop ?? Win32TrainerWindowInterop.Instance;
        IReadOnlySet<int> pidsBefore = QueryActiveProcessIds();
        int initialCount = pidsBefore.Count;

        if (initialCount == 0 && _activeProcessZeroTcs.Task.IsCompleted)
        {
            Dispose();
            return new SandboxStopResult(Succeeded: true, TerminatedForcefully: false, TerminatedProcessCount: 0);
        }

        int postedWindows = 0;
        if (pidsBefore.Count > 0 && gracefulTimeoutMs > 0)
        {
            IReadOnlyList<nint> windows = interop.EnumerateTopLevelWindowsForProcesses(pidsBefore);
            foreach (nint hwnd in windows)
            {
                if (interop.PostCloseMessage(hwnd))
                {
                    postedWindows++;
                }
            }
        }

        bool exitedGracefully = false;
        if (postedWindows > 0 && gracefulTimeoutMs > 0)
        {
            try
            {
                await _activeProcessZeroTcs.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(gracefulTimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
                exitedGracefully = true;
            }
            catch (TimeoutException)
            {
                exitedGracefully = false;
            }
            catch (OperationCanceledException)
            {
                exitedGracefully = false;
            }
        }

        bool terminatedForcefully = false;
        if (!exitedGracefully)
        {
            IReadOnlySet<int> remainingBeforeKill = QueryActiveProcessIds();
            if (remainingBeforeKill.Count > 0 || !_activeProcessZeroTcs.Task.IsCompleted)
            {
                terminatedForcefully = true;
                initialCount = Math.Max(initialCount, remainingBeforeKill.Count);
                TerminateNow();

                try
                {
                    await _activeProcessZeroTcs.Task
                        .WaitAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Proceed to CloseHandle which also enforces KILL_ON_JOB_CLOSE.
                }
            }
        }

        Dispose();
        return new SandboxStopResult(
            Succeeded: true,
            TerminatedForcefully: terminatedForcefully,
            TerminatedProcessCount: initialCount);
    }

    /// <summary>
    /// Immediately terminates all processes inside the Job Object via <c>TerminateJobObject</c>.
    /// </summary>
    public void TerminateNow()
    {
        lock (_stateGate)
        {
            if (IsValidHandle(_jobHandle))
            {
                _ = PInvoke.TerminateJobObject(_jobHandle, 1);
            }
        }
    }

    private unsafe void IocpPumpLoop()
    {
        while (true)
        {
            uint msgId = 0;
            nuint completionKey = 0;
            NativeOverlapped* overlapped = null;

            bool ok = PInvoke.GetQueuedCompletionStatus(
                _iocpHandle,
                &msgId,
                &completionKey,
                &overlapped,
                Infinite);

            if (!ok || completionKey == ShutdownCompletionKey)
            {
                break;
            }

            switch (msgId)
            {
                case JobObjectMsgNewProcess:
                {
                    int pid = checked((int)(nint)overlapped);
                    if (pid > 4)
                    {
                        _trackedPids.TryAdd(pid, 0);
                        string? imagePath = SafeProcessInspector.TryGetProcessExecutablePath(pid);
                        try
                        {
                            ProcessCreated?.Invoke(pid, imagePath);
                        }
                        catch
                        {
                            // Never break IOCP loop on subscriber exceptions.
                        }
                    }

                    break;
                }

                case JobObjectMsgExitProcess:
                case JobObjectMsgAbnormalExitProcess:
                {
                    int pid = checked((int)(nint)overlapped);
                    _trackedPids.TryRemove(pid, out _);
                    break;
                }

                case JobObjectMsgActiveProcessZero:
                {
                    _trackedPids.Clear();
                    int? exitCode = TryReadRootExitCode();
                    if (_activeProcessZeroTcs.TrySetResult(exitCode))
                    {
                        try
                        {
                            ActiveProcessZero?.Invoke(exitCode);
                        }
                        catch
                        {
                            // Never break IOCP loop on subscriber exceptions.
                        }
                    }

                    break;
                }
            }
        }
    }

    private unsafe int? TryReadRootExitCode()
    {
        lock (_stateGate)
        {
            if (!IsValidHandle(_rootProcessHandle))
            {
                return null;
            }

            uint code = 0;
            if (PInvoke.GetExitCodeProcess(_rootProcessHandle, &code) && code != StillActive)
            {
                return unchecked((int)code);
            }

            return null;
        }
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        HANDLE jobToClose;
        HANDLE rootToClose;
        HANDLE iocpToSignal;

        lock (_stateGate)
        {
            jobToClose = _jobHandle;
            _jobHandle = HANDLE.Null;
            rootToClose = _rootProcessHandle;
            _rootProcessHandle = HANDLE.Null;
            iocpToSignal = _iocpHandle;
        }

        if (IsValidHandle(jobToClose))
        {
            _ = PInvoke.TerminateJobObject(jobToClose, 1);
            _ = PInvoke.CloseHandle(jobToClose);
        }

        if (IsValidHandle(rootToClose))
        {
            _ = PInvoke.CloseHandle(rootToClose);
        }

        if (IsValidHandle(iocpToSignal))
        {
            _ = PInvoke.PostQueuedCompletionStatus(iocpToSignal, 0, ShutdownCompletionKey, null);
        }

        if (_iocpThread is { IsAlive: true } && Thread.CurrentThread != _iocpThread)
        {
            _ = _iocpThread.Join(TimeSpan.FromSeconds(1));
        }

        lock (_stateGate)
        {
            HANDLE iocpToClose = _iocpHandle;
            _iocpHandle = HANDLE.Null;
            if (IsValidHandle(iocpToClose))
            {
                _ = PInvoke.CloseHandle(iocpToClose);
            }
        }

        _activeProcessZeroTcs.TrySetResult(null);
    }

    internal static unsafe string TryResolveShortPathIfNonAscii(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || SpawnPaths.IsPureAsciiPath(path))
        {
            return path;
        }

        try
        {
            fixed (char* pLong = path)
            {
                uint required = PInvoke.GetShortPathName(new PCWSTR(pLong), default, 0);
                if (required == 0 || required > 32768)
                {
                    return path;
                }

                char[] buffer = new char[required];
                fixed (char* pShort = buffer)
                {
                    uint written = PInvoke.GetShortPathName(new PCWSTR(pLong), new PWSTR(pShort), required);
                    if (written > 0 && written < required)
                    {
                        string shortPath = new string(buffer, 0, checked((int)written));
                        if (SpawnPaths.IsPureAsciiPath(shortPath) && (File.Exists(shortPath) || Directory.Exists(shortPath)))
                        {
                            return shortPath;
                        }
                    }
                }
            }
        }
        catch
        {
            // Volume may have 8.3 short names disabled; keep verified long path safely.
        }

        return path;
    }
}
