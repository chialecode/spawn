using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Spawn.Core;

/// <summary>
/// Snapshot entry of a running OS process inspected strictly via limited query rights (<c>0x1000</c>).
/// </summary>
public sealed record SafeProcessEntry(
    int ProcessId,
    int ParentProcessId,
    string ExeFileName,
    string? FullExecutablePath);

/// <summary>
/// Clean-Host compliant process path inspector.
/// Uses ONLY <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION = 0x1000)</c> + <c>QueryFullProcessImageNameW</c>
/// and <c>CreateToolhelp32Snapshot</c>. Never invokes <c>Process.MainModule</c>, memory read/write, or hook APIs.
/// </summary>
public static partial class SafeProcessInspector
{
    public const uint ProcessQueryLimitedInformation = 0x00001000;
    public const uint Synchronize = 0x00100000;

    private const uint Th32csSnapProcess = 0x00000002;
    private const uint StillActiveExitCode = 259;
    private static readonly nint InvalidHandleValue = new(-1);

    [GeneratedRegex(
        @"^(UnityCrashHandler(32|64)|CrashReportClient|CrashSender\d*|ZFGameBrowser|UnrealCEFSubProcess|cef_?process|steamerrorreporter|EasyAntiCheat(_EOS)?|BEService(_x64)?|SGuard(Svc)?64|unins\d+|vc_redist.*|dxsetup)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuxiliaryProcessRegex();

    public static bool IsAuxiliaryOrCrashProcessName(string? exeFileName)
    {
        if (string.IsNullOrWhiteSpace(exeFileName))
        {
            return true;
        }

        string fileName = Path.GetFileName(exeFileName);
        return AuxiliaryProcessRegex().IsMatch(fileName);
    }

    /// <summary>
    /// Queries the full filesystem path of <paramref name="processId"/> using strictly <c>PROCESS_QUERY_LIMITED_INFORMATION (0x1000)</c>.
    /// </summary>
    public static unsafe string? TryGetProcessExecutablePath(int processId)
    {
        if (processId <= 4)
        {
            return null;
        }

        nint hProcess = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, (uint)processId);
        if (hProcess == nint.Zero)
        {
            return null;
        }

        try
        {
            Span<char> buffer = stackalloc char[1024];
            uint size = (uint)buffer.Length;
            fixed (char* pBuf = buffer)
            {
                if (QueryFullProcessImageNameW(hProcess, 0, pBuf, ref size) && size > 0)
                {
                    return new string(buffer[..(int)size]);
                }
            }

            return null;
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Checks whether <paramref name="processId"/> is currently alive using strictly <c>0x1000</c> limited access.
    /// </summary>
    public static bool IsProcessAlive(int processId)
    {
        if (processId <= 4)
        {
            return false;
        }

        nint hProcess = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, (uint)processId);
        if (hProcess == nint.Zero)
        {
            return false;
        }

        try
        {
            return GetExitCodeProcess(hProcess, out uint exitCode) && exitCode == StillActiveExitCode;
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Opens a waitable handle (<c>PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE</c>) for clean exit monitoring.
    /// Caller owns closing the returned handle if non-zero.
    /// </summary>
    public static nint TryOpenSynchronizeHandle(int processId)
    {
        if (processId <= 4)
        {
            return nint.Zero;
        }

        return OpenProcess(ProcessQueryLimitedInformation | Synchronize, bInheritHandle: false, (uint)processId);
    }

    public static void CloseSafeHandle(nint handle)
    {
        if (handle != nint.Zero && handle != InvalidHandleValue)
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>
    /// Enumerates running processes via <c>CreateToolhelp32Snapshot</c> and optionally resolves full paths via <c>0x1000</c>.
    /// </summary>
    public static unsafe IReadOnlyList<SafeProcessEntry> EnumerateProcesses(bool resolveFullPaths = false)
    {
        nint snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue || snapshot == nint.Zero)
        {
            return [];
        }

        var list = new List<SafeProcessEntry>(128);
        try
        {
            ProcessEntry32W entry = default;
            entry.dwSize = (uint)sizeof(ProcessEntry32W);

            if (!Process32FirstW(snapshot, ref entry))
            {
                return list;
            }

            do
            {
                int pid = (int)entry.th32ProcessID;
                int ppid = (int)entry.th32ParentProcessID;
                string exeName = ExtractNullTerminatedString(ref entry);
                string? fullPath = resolveFullPaths ? TryGetProcessExecutablePath(pid) : null;
                list.Add(new SafeProcessEntry(pid, ppid, exeName, fullPath));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return list;
    }

    /// <summary>
    /// Finds live processes whose executable path resides inside <paramref name="rootDirectory"/>,
    /// optionally excluding crash reporters and anti-cheat helper stubs.
    /// </summary>
    public static IReadOnlyList<SafeProcessEntry> FindProcessesUnderDirectory(
        string rootDirectory,
        bool excludeAuxiliary = true)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        string normalizedPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) + Path.DirectorySeparatorChar;
        var all = EnumerateProcesses(resolveFullPaths: false);
        var matches = new List<SafeProcessEntry>();

        foreach (var proc in all)
        {
            if (proc.ProcessId <= 4)
            {
                continue;
            }

            if (excludeAuxiliary && IsAuxiliaryOrCrashProcessName(proc.ExeFileName))
            {
                continue;
            }

            string? fullPath = TryGetProcessExecutablePath(proc.ProcessId);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                continue;
            }

            if (fullPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) && IsProcessAlive(proc.ProcessId))
            {
                matches.Add(proc with { FullExecutablePath = fullPath });
            }
        }

        return matches;
    }

    private static unsafe string ExtractNullTerminatedString(ref ProcessEntry32W entry)
    {
        fixed (char* ptr = entry.szExeFile)
        {
            int len = 0;
            while (len < 260 && ptr[len] != '\0')
            {
                len++;
            }

            return len == 0 ? string.Empty : new string(ptr, 0, len);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(
        nint hProcess,
        uint dwFlags,
        char* lpExeName,
        ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint hSnapshot, ref ProcessEntry32W lppe);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint hSnapshot, ref ProcessEntry32W lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
