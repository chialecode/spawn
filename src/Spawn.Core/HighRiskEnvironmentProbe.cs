using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Spawn.Core;

/// <summary>
/// Read-only detector for high-risk tools that must not coexist with a <c>Tier 2/3</c> protected game launch:
/// Cheat Engine user-mode processes, the Cheat Engine kernel driver (<c>dbk64.sys</c> / <c>dbk32.sys</c>), the raw port/MSR I/O
/// drivers named by the launcher spec (<c>WinRing0x64.sys</c>, <c>inpoutx64.sys</c>) and SPAWN's own
/// sensitive Worker executables. Uses only Toolhelp snapshots, <c>EnumDeviceDrivers</c> base names and the Service Control
/// Manager active-driver list (no handles to foreign processes beyond <c>PROCESS_QUERY_LIMITED_INFORMATION</c>,
/// no memory access, no hooks).
/// </summary>
public static partial class HighRiskEnvironmentProbe
{
    private static readonly HashSet<string> HighRiskProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cheatengine.exe",
        "cheatengine-x86_64.exe",
        "cheatengine-x86_64-SSE4-AVX2.exe",
        "cheatengine-i386.exe",
        "Cheat Engine.exe",
        "Spawn.TrainerWorker.exe",
        "Spawn.BlueprintWorker.exe",
        "Spawn.WorkflowWorker.exe",
    };

    private static readonly HashSet<string> HighRiskDriverFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbk64.sys",
        "dbk32.sys",
        "WinRing0x64.sys",
        "WinRing0.sys",
        "inpoutx64.sys",
    };

    /// <summary>Returns <see langword="true"/> when the executable file name belongs to Cheat Engine or a SPAWN sensitive Worker.</summary>
    public static bool IsHighRiskProcessName(string? exeFileName)
    {
        if (string.IsNullOrWhiteSpace(exeFileName))
        {
            return false;
        }

        string name = Path.GetFileName(exeFileName.Trim());
        return HighRiskProcessNames.Contains(name)
               || (name.StartsWith("cheatengine", StringComparison.OrdinalIgnoreCase)
                   && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns <see langword="true"/> when a driver image / service binary path is the Cheat Engine kernel driver.</summary>
    public static bool IsHighRiskDriverName(string? driverFileOrImagePath)
    {
        if (string.IsNullOrWhiteSpace(driverFileOrImagePath))
        {
            return false;
        }

        string trimmed = driverFileOrImagePath.Trim().Trim('"');
        return HighRiskDriverFileNames.Contains(Path.GetFileName(trimmed));
    }

    /// <summary>Distinct running high-risk process names (Toolhelp snapshot, names only).</summary>
    public static IReadOnlyList<string> DetectHighRiskProcesses()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in SafeProcessInspector.EnumerateProcesses(resolveFullPaths: false))
        {
            if (IsHighRiskProcessName(entry.ExeFileName))
            {
                found.Add(entry.ExeFileName);
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Distinct loaded high-risk kernel drivers. Combines <c>EnumDeviceDrivers</c> base names (complete when permitted)
    /// with active <c>SERVICE_DRIVER</c> entries whose registry <c>ImagePath</c> resolves to a high-risk driver file
    /// (works for standard users on builds that hide kernel module lists).
    /// </summary>
    public static IReadOnlyList<string> DetectHighRiskDrivers()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string baseName in EnumerateLoadedDriverBaseNames())
        {
            if (IsHighRiskDriverName(baseName))
            {
                found.Add(Path.GetFileName(baseName));
            }
        }

        foreach (string serviceName in EnumerateActiveDriverServiceNames())
        {
            string? imagePath = TryReadServiceImagePath(serviceName);
            if (IsHighRiskDriverName(imagePath))
            {
                found.Add(Path.GetFileName(imagePath!.Trim().Trim('"')));
            }
        }

        return [.. found];
    }

    private static unsafe List<string> EnumerateLoadedDriverBaseNames()
    {
        var names = new List<string>();
        try
        {
            uint needed = 0;
            if (!K32EnumDeviceDrivers(null, 0, &needed) || needed == 0)
            {
                return names;
            }

            int count = (int)(needed / (uint)sizeof(nint)) + 16;
            nint[] bases = new nint[count];
            fixed (nint* pBases = bases)
            {
                if (!K32EnumDeviceDrivers(pBases, (uint)(count * sizeof(nint)), &needed))
                {
                    return names;
                }
            }

            int returned = Math.Min(count, (int)(needed / (uint)sizeof(nint)));
            char* buffer = stackalloc char[260];
            for (int i = 0; i < returned; i++)
            {
                if (bases[i] == nint.Zero)
                {
                    continue;
                }

                uint len = K32GetDeviceDriverBaseNameW(bases[i], buffer, 260);
                if (len > 0)
                {
                    names.Add(new string(buffer, 0, (int)len));
                }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // API unavailable; SCM fallback below still runs.
        }

        return names;
    }

    private static unsafe List<string> EnumerateActiveDriverServiceNames()
    {
        var names = new List<string>();
        nint scm = OpenSCManagerW(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == nint.Zero)
        {
            return names;
        }

        try
        {
            uint resume = 0;
            uint bytesNeeded = 0;
            uint returned = 0;
            _ = EnumServicesStatusExW(scm, ScEnumProcessInfo, ServiceDriver, ServiceActive, null, 0, &bytesNeeded, &returned, &resume, null);
            if (bytesNeeded == 0)
            {
                return names;
            }

            uint bufferSize = bytesNeeded + 4096;
            byte[] buffer = new byte[bufferSize];
            resume = 0;
            fixed (byte* pBuf = buffer)
            {
                if (!EnumServicesStatusExW(scm, ScEnumProcessInfo, ServiceDriver, ServiceActive, pBuf, bufferSize, &bytesNeeded, &returned, &resume, null))
                {
                    return names;
                }

                var entries = (EnumServiceStatusProcessW*)pBuf;
                for (int i = 0; i < returned; i++)
                {
                    string? name = Marshal.PtrToStringUni(entries[i].ServiceName);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }

        return names;
    }

    private static string? TryReadServiceImagePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
            return key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceDriver = 0x0000000B;
    private const uint ServiceActive = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcessW
    {
        public nint ServiceName;
        public nint DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool K32EnumDeviceDrivers(nint* lpImageBase, uint cb, uint* lpcbNeeded);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint K32GetDeviceDriverBaseNameW(nint imageBase, char* lpBaseName, uint nSize);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumServicesStatusExW(
        nint hScManager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        byte* services,
        uint bufSize,
        uint* bytesNeeded,
        uint* servicesReturned,
        uint* resumeHandle,
        string? groupName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint hScObject);
}
