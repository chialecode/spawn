using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Spawn.Core;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Result of attaching a newly started game process to <see cref="JobObjectProcessTracker"/>.
/// </summary>
public enum JobAttachOutcome
{
    Attached = 0,
    ProcessAlreadyExited = 1,
    FallbackRequired = 2,
}

/// <summary>
/// Clean-Host compliant Windows Job Object + I/O Completion Port (IOCP) process tree tracker.
/// <para>
/// <b>Safety Invariant</b>: Explicitly configures <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK (0x0800)</c> and
/// <b>NEVER</b> sets <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000)</c>, guaranteeing that restarting or
/// closing SPAWN never terminates a player's running game.
/// </para>
/// </summary>
public sealed partial class JobObjectProcessTracker : IDisposable
{
    public const uint JobObjectMsgEndOfJobTime = 1;
    public const uint JobObjectMsgEndOfProcessTime = 2;
    public const uint JobObjectMsgActiveProcessLimit = 3;
    public const uint JobObjectMsgActiveProcessZero = 4;
    public const uint JobObjectMsgNewProcess = 6;
    public const uint JobObjectMsgExitProcess = 7;
    public const uint JobObjectMsgAbnormalExitProcess = 8;

    public const uint JobObjectLimitBreakawayOk = 0x00000800;
    public const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectAssociateCompletionPortInformationClass = 7;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly nuint ShutdownCompletionKey = 0;
    private static readonly nuint JobEventCompletionKey = 1;

    private readonly nint _jobHandle;
    private readonly nint _iocpHandle;
    private readonly ConcurrentDictionary<int, byte> _trackedPids = new();
    private readonly TaskCompletionSource<bool> _activeProcessZeroTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _iocpReaderTask;
    private int _hasEverAttachedProcess;
    private int _disposed;

    public event Action<int>? ProcessAdded;
    public event Action<int>? ProcessExited;

    public bool IsActiveProcessZero => _activeProcessZeroTcs.Task.IsCompleted;

    public JobObjectProcessTracker()
    {
        _jobHandle = CreateJobObjectW(nint.Zero, null);
        if (_jobHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObjectW failed.");
        }

        try
        {
            // Explicitly configure limit flags WITHOUT JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000).
            JobObjectExtendedLimitInformation extLimits = default;
            extLimits.BasicLimitInformation.LimitFlags = JobObjectLimitBreakawayOk;

            if (!SetInformationJobObjectExtendedLimit(
                    _jobHandle,
                    JobObjectExtendedLimitInformationClass,
                    ref extLimits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject(ExtendedLimitInformation) failed.");
            }

            _iocpHandle = CreateIoCompletionPort(InvalidHandleValue, nint.Zero, nuint.Zero, 1);
            if (_iocpHandle == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed.");
            }

            var assoc = new JobObjectAssociateCompletionPort
            {
                CompletionKey = JobEventCompletionKey,
                CompletionPort = _iocpHandle,
            };

            if (!SetInformationJobObjectCompletionPort(
                    _jobHandle,
                    JobObjectAssociateCompletionPortInformationClass,
                    ref assoc,
                    (uint)Marshal.SizeOf<JobObjectAssociateCompletionPort>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject(AssociateCompletionPort) failed.");
            }
        }
        catch
        {
            if (_iocpHandle != nint.Zero)
            {
                _ = CloseHandle(_iocpHandle);
            }

            _ = CloseHandle(_jobHandle);
            throw;
        }

        _iocpReaderTask = Task.Factory.StartNew(
            RunIocpLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Queries the underlying kernel Job Object to verify <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> is NOT set.
    /// </summary>
    public bool VerifyKillOnJobCloseDisabled()
    {
        uint flags = QueryConfiguredLimitFlags();
        return (flags & JobObjectLimitKillOnJobClose) == 0;
    }

    /// <summary>
    /// Queries the configured <c>BasicLimitInformation.LimitFlags</c> directly from the OS Job Object.
    /// </summary>
    public uint QueryConfiguredLimitFlags()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        JobObjectExtendedLimitInformation info = default;
        if (!QueryInformationJobObjectExtendedLimit(
                _jobHandle,
                JobObjectExtendedLimitInformationClass,
                out info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed.");
        }

        return info.BasicLimitInformation.LimitFlags;
    }

    /// <summary>
    /// Queries the current active process count inside this Job Object from the kernel.
    /// </summary>
    public uint QueryActiveProcessCount()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        if (!QueryInformationJobObjectBasicAccounting(
                _jobHandle,
                JobObjectBasicAccountingInformationClass,
                out var accounting,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                out _))
        {
            return 0;
        }

        return accounting.ActiveProcesses;
    }

    public JobAttachOutcome TryAttachProcess(SafeProcessHandle processHandle, int processId)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        if (processHandle.IsInvalid || processHandle.IsClosed)
        {
            return JobAttachOutcome.ProcessAlreadyExited;
        }

        bool addedRef = false;
        try
        {
            processHandle.DangerousAddRef(ref addedRef);
            return TryAttachProcess(processHandle.DangerousGetHandle(), processId);
        }
        finally
        {
            if (addedRef)
            {
                processHandle.DangerousRelease();
            }
        }
    }

    public JobAttachOutcome TryAttachProcess(nint processHandle, int processId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (processHandle == nint.Zero)
        {
            return JobAttachOutcome.FallbackRequired;
        }

        if (AssignProcessToJobObject(_jobHandle, processHandle))
        {
            Interlocked.Exchange(ref _hasEverAttachedProcess, 1);
            if (processId > 0)
            {
                _trackedPids[processId] = 0;
            }

            // Guard ultra-fast sub-millisecond exit race where the process terminated right as it was assigned.
            if (QueryActiveProcessCount() == 0 && !SafeProcessInspector.IsProcessAlive(processId))
            {
                _trackedPids.Clear();
                _activeProcessZeroTcs.TrySetResult(true);
            }

            return JobAttachOutcome.Attached;
        }

        // AssignProcessToJobObject returns false if the target process ALREADY exited before assignment,
        // or if the target process runs at a higher integrity level / non-nestable job.
        if (processId > 0 && !SafeProcessInspector.IsProcessAlive(processId))
        {
            _activeProcessZeroTcs.TrySetResult(true);
            return JobAttachOutcome.ProcessAlreadyExited;
        }

        return JobAttachOutcome.FallbackRequired;
    }

    public IReadOnlyCollection<int> GetTrackedProcessIds() => [.. _trackedPids.Keys];

    /// <summary>
    /// Asynchronously waits until the OS kernel delivers <c>JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO (4)</c>
    /// (zero polling, zero CPU usage while game runs).
    /// </summary>
    public async Task WaitForActiveProcessZeroAsync(CancellationToken cancellationToken = default)
    {
        if (_activeProcessZeroTcs.Task.IsCompleted)
        {
            await _activeProcessZeroTcs.Task.ConfigureAwait(false);
            return;
        }

        await _activeProcessZeroTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RunIocpLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            bool dequeued = GetQueuedCompletionStatus(
                _iocpHandle,
                out uint messageId,
                out nuint completionKey,
                out nint overlappedPid,
                uint.MaxValue);

            if (!dequeued)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }

                continue;
            }

            if (completionKey == ShutdownCompletionKey)
            {
                break;
            }

            if (completionKey != JobEventCompletionKey)
            {
                continue;
            }

            int pid = (int)overlappedPid;
            switch (messageId)
            {
                case JobObjectMsgNewProcess:
                    if (pid > 0)
                    {
                        Interlocked.Exchange(ref _hasEverAttachedProcess, 1);
                        _trackedPids[pid] = 0;
                        ProcessAdded?.Invoke(pid);
                    }

                    break;

                case JobObjectMsgExitProcess:
                case JobObjectMsgAbnormalExitProcess:
                    if (pid > 0)
                    {
                        _trackedPids.TryRemove(pid, out _);
                        ProcessExited?.Invoke(pid);
                    }

                    break;

                case JobObjectMsgActiveProcessZero:
                    _trackedPids.Clear();
                    _activeProcessZeroTcs.TrySetResult(true);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_iocpHandle != nint.Zero)
        {
            _ = PostQueuedCompletionStatus(_iocpHandle, 0, ShutdownCompletionKey, nint.Zero);
        }

        if (Volatile.Read(ref _hasEverAttachedProcess) == 0)
        {
            _activeProcessZeroTcs.TrySetCanceled();
        }
        else
        {
            _activeProcessZeroTcs.TrySetResult(false);
        }

        try
        {
            _ = _iocpReaderTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Ignore shutdown wait race.
        }

        if (_iocpHandle != nint.Zero)
        {
            _ = CloseHandle(_iocpHandle);
        }

        if (_jobHandle != nint.Zero)
        {
            // Closing _jobHandle NEVER terminates any game process because KILL_ON_JOB_CLOSE is unset.
            _ = CloseHandle(_jobHandle);
        }
    }

    // -------------------------------------------------------------------------
    // Native Win32 Structs & LibraryImport Declarations (Clean Host Safe)
    // -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectAssociateCompletionPort
    {
        public nuint CompletionKey;
        public nint CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateIoCompletionPort(
        nint fileHandle,
        nint existingCompletionPort,
        nuint completionKey,
        uint numberOfConcurrentThreads);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObjectCompletionPort(
        nint hJob,
        int jobObjectInformationClass,
        ref JobObjectAssociateCompletionPort lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObjectExtendedLimit(
        nint hJob,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObjectExtendedLimit(
        nint hJob,
        int jobObjectInformationClass,
        out JobObjectExtendedLimitInformation lpJobObjectInformation,
        uint cbJobObjectInformationLength,
        out uint lpReturnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObjectBasicAccounting(
        nint hJob,
        int jobObjectInformationClass,
        out JobObjectBasicAccountingInformation lpJobObjectInformation,
        uint cbJobObjectInformationLength,
        out uint lpReturnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetQueuedCompletionStatus(
        nint completionPort,
        out uint lpNumberOfBytesTransferred,
        out nuint lpCompletionKey,
        out nint lpOverlapped,
        uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostQueuedCompletionStatus(
        nint completionPort,
        uint dwNumberOfBytesTransferred,
        nuint dwCompletionKey,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
