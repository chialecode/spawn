using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Spawn.Core;

/// <summary>
/// Read-only PE version metadata subset inspected to recognize renamed Cheat Engine kernel drivers
/// (<c>dbk64.sys</c> / <c>dbk32.sys</c>) without generalizing to unsigned drivers.
/// </summary>
public sealed record DriverVersionMetadata(
    string? OriginalFilename,
    string? InternalName,
    string? ProductName,
    string? FileDescription);

/// <summary>
/// Read-only descriptor of an active Windows <c>SERVICE_DRIVER</c> entry enumerated via SCM.
/// </summary>
public sealed record ActiveKernelDriverInfo(
    string ServiceName,
    string? RawImagePath,
    string? ResolvedFilePath,
    string? DriverFileName);

/// <summary>
/// Read-only detector for high-risk tools that must not coexist with a <c>Tier 2/3</c> protected game launch:
/// Cheat Engine user-mode processes, the Cheat Engine kernel driver (<c>dbk64.sys</c> / <c>dbk32.sys</c> and renamed
/// variants identified via read-only <see cref="FileVersionInfo"/> or <c>QueryDosDeviceW</c> <c>CEDRIVER*</c> prefix),
/// raw port/MSR I/O drivers (<c>WinRing0x64.sys</c>, <c>inpoutx64.sys</c>), SPAWN's own sensitive Worker executables,
/// and any orphan processes still running under the trainer sandbox directory prefix.
/// Uses ONLY Toolhelp snapshots, <c>0x1000</c> limited process query, <c>EnumDeviceDrivers</c>, SCM active-driver
/// enumeration, read-only <c>FileVersionInfo</c>, and <c>QueryDosDeviceW</c>. Never kills user tools.
/// </summary>
public static partial class HighRiskEnvironmentProbe
{
    private const int ErrorInsufficientBuffer = 122;

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

    private static readonly HashSet<string> CheatEngineDriverExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbk64.sys",
        "dbk32.sys",
        "dbk64",
        "dbk32",
        "dbk",
    };

    [GeneratedRegex(@"(?:^|[^A-Za-z0-9])DBK(?:32|64)?(?:$|[^A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DbkTokenRegex();

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

    /// <summary>Returns <see langword="true"/> when a driver image / service binary path matches a known high-risk kernel driver.</summary>
    public static bool IsHighRiskDriverName(string? driverFileOrImagePath)
    {
        if (string.IsNullOrWhiteSpace(driverFileOrImagePath))
        {
            return false;
        }

        string trimmed = driverFileOrImagePath.Trim().Trim('"');
        return HighRiskDriverFileNames.Contains(Path.GetFileName(trimmed));
    }

    /// <summary>
    /// Returns <see langword="true"/> when a DOS device name returned by <c>QueryDosDeviceW</c> matches
    /// Cheat Engine's exact <c>CEDRIVER*</c> prefix (e.g., <c>CEDRIVER73</c>, <c>CEDRIVER75</c>).
    /// </summary>
    public static bool IsCheatEngineDosDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        return deviceName.Trim().StartsWith("CEDRIVER", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates read-only PE <see cref="FileVersionInfo"/> fields (<c>OriginalFilename</c>, <c>InternalName</c>,
    /// <c>ProductName</c>, <c>FileDescription</c>) to identify a renamed Cheat Engine DBK kernel driver with zero
    /// false positives on standard OS or hardware drivers.
    /// </summary>
    public static bool IsRenamedCheatEngineDriverMetadata(
        DriverVersionMetadata? metadata,
        out string? matchedIndicator)
    {
        matchedIndicator = null;
        if (metadata is null)
        {
            return false;
        }

        if (MatchesCheatEngineVersionField(metadata.OriginalFilename, "OriginalFilename", out matchedIndicator))
        {
            return true;
        }

        if (MatchesCheatEngineVersionField(metadata.InternalName, "InternalName", out matchedIndicator))
        {
            return true;
        }

        if (MatchesCheatEngineVersionField(metadata.ProductName, "ProductName", out matchedIndicator))
        {
            return true;
        }

        if (MatchesCheatEngineVersionField(metadata.FileDescription, "FileDescription", out matchedIndicator))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a Windows SCM registry <c>ImagePath</c> (handling <c>\??\</c>, <c>\SystemRoot\</c>,
    /// <c>%SystemRoot%\</c>, relative <c>System32\drivers\...</c>, and quoted values) into a local filesystem path.
    /// </summary>
    public static string? ResolveDriverImagePath(string? rawImagePath, string? windowsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(rawImagePath))
        {
            return null;
        }

        string candidate = rawImagePath.Trim().Trim('"');
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        if (candidate.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            candidate = candidate[4..];
        }
        else if (candidate.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            candidate = candidate[4..];
        }

        string winDir = !string.IsNullOrWhiteSpace(windowsDirectory)
            ? windowsDirectory.TrimEnd('\\', '/')
            : Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (candidate.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(winDir, candidate[@"\SystemRoot\".Length..]);
        }
        else if (candidate.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(winDir, candidate[@"SystemRoot\".Length..]);
        }
        else if (candidate.Contains('%', StringComparison.Ordinal))
        {
            candidate = Environment.ExpandEnvironmentVariables(candidate);
        }
        else if (candidate.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)
                 || candidate.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(winDir, candidate);
        }

        if (candidate.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Path.IsPathFullyQualified(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Read-only <see cref="FileVersionInfo"/> reader for a local driver <c>.sys</c> path.</summary>
    public static DriverVersionMetadata? ReadDriverVersionMetadata(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            if (string.IsNullOrWhiteSpace(info.OriginalFilename)
                && string.IsNullOrWhiteSpace(info.InternalName)
                && string.IsNullOrWhiteSpace(info.ProductName)
                && string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return null;
            }

            return new DriverVersionMetadata(
                OriginalFilename: info.OriginalFilename,
                InternalName: info.InternalName,
                ProductName: info.ProductName,
                FileDescription: info.FileDescription);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Distinct running high-risk process names (Toolhelp snapshot, names only).</summary>
    public static IReadOnlyList<string> DetectHighRiskProcesses() =>
        DetectHighRiskProcesses(sandboxRoots: null);

    /// <summary>
    /// Distinct running high-risk process names plus any live orphan processes whose <c>0x1000</c> image path
    /// resides beneath any configured trainer sandbox directory prefix.
    /// </summary>
    public static IReadOnlyList<string> DetectHighRiskProcesses(
        IEnumerable<string>? sandboxRoots,
        Func<string, IReadOnlyList<SafeProcessEntry>>? directoryProcessEnumerator = null)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in SafeProcessInspector.EnumerateProcesses(resolveFullPaths: false))
        {
            if (IsHighRiskProcessName(entry.ExeFileName))
            {
                found.Add(entry.ExeFileName);
            }
        }

        if (sandboxRoots is not null)
        {
            foreach (string root in sandboxRoots)
            {
                foreach (string orphan in DetectSandboxOrphanProcesses(root, directoryProcessEnumerator))
                {
                    found.Add(orphan);
                }
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Read-only <c>0x1000</c> probe that finds any live process executing from inside <paramref name="sandboxRoot"/>
    /// (used by LOOP-06 secondary clean-field verification before Tier 2/3 protected game launches).
    /// </summary>
    public static IReadOnlyList<string> DetectSandboxOrphanProcesses(
        string? sandboxRoot,
        Func<string, IReadOnlyList<SafeProcessEntry>>? directoryProcessEnumerator = null)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot))
        {
            return [];
        }

        IReadOnlyList<SafeProcessEntry> entries;
        if (directoryProcessEnumerator is not null)
        {
            entries = directoryProcessEnumerator(sandboxRoot);
        }
        else
        {
            if (!Directory.Exists(sandboxRoot))
            {
                return [];
            }

            entries = SafeProcessInspector.FindProcessesUnderDirectory(sandboxRoot, excludeAuxiliary: false);
        }

        if (entries.Count == 0)
        {
            return [];
        }

        int selfPid = Environment.ProcessId;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in entries)
        {
            if (proc.ProcessId == selfPid || proc.ProcessId <= 4)
            {
                continue;
            }

            string exeName = !string.IsNullOrWhiteSpace(proc.ExeFileName)
                ? Path.GetFileName(proc.ExeFileName)
                : (!string.IsNullOrWhiteSpace(proc.FullExecutablePath) ? Path.GetFileName(proc.FullExecutablePath) : $"pid:{proc.ProcessId}");
            found.Add($"{exeName} (沙箱孤儿进程 PID {proc.ProcessId})");
        }

        return [.. found];
    }

    /// <summary>
    /// Distinct loaded high-risk kernel drivers. Combines:
    /// <list type="number">
    /// <item><description><c>K32EnumDeviceDrivers</c> base names;</description></item>
    /// <item><description>Active <c>SERVICE_DRIVER</c> registry <c>ImagePath</c> filename matches;</description></item>
    /// <item><description>Read-only <see cref="FileVersionInfo"/> metadata inspection of active driver <c>.sys</c> files for renamed CE DBK drivers;</description></item>
    /// <item><description><c>QueryDosDeviceW</c> exact <c>CEDRIVER*</c> prefix device detection.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> DetectHighRiskDrivers() =>
        DetectHighRiskDrivers(
            loadedDriverBaseNamesProvider: null,
            activeDriverServicesProvider: null,
            versionMetadataReader: null,
            dosDeviceNamesProvider: null);

    /// <summary>
    /// Testable overload of <see cref="DetectHighRiskDrivers()"/> accepting injectable read-only providers.
    /// </summary>
    public static IReadOnlyList<string> DetectHighRiskDrivers(
        Func<IReadOnlyList<string>>? loadedDriverBaseNamesProvider = null,
        Func<IReadOnlyList<ActiveKernelDriverInfo>>? activeDriverServicesProvider = null,
        Func<string, DriverVersionMetadata?>? versionMetadataReader = null,
        Func<IReadOnlyList<string>>? dosDeviceNamesProvider = null)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var loadedNames = loadedDriverBaseNamesProvider is not null
            ? loadedDriverBaseNamesProvider()
            : EnumerateLoadedDriverBaseNames();

        foreach (string baseName in loadedNames)
        {
            if (IsHighRiskDriverName(baseName))
            {
                found.Add(Path.GetFileName(baseName.Trim().Trim('"')));
            }
        }

        var activeServices = activeDriverServicesProvider is not null
            ? activeDriverServicesProvider()
            : EnumerateActiveDriverServices();

        var metadataReader = versionMetadataReader ?? ReadDriverVersionMetadata;

        foreach (var svc in activeServices)
        {
            string? driverFile = svc.DriverFileName ?? svc.RawImagePath;
            if (IsHighRiskDriverName(driverFile))
            {
                found.Add(Path.GetFileName(driverFile!.Trim().Trim('"')));
                continue;
            }

            string? resolvedPath = svc.ResolvedFilePath ?? ResolveDriverImagePath(svc.RawImagePath);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                var metadata = metadataReader(resolvedPath);
                if (IsRenamedCheatEngineDriverMetadata(metadata, out string? indicator))
                {
                    string displayFile = Path.GetFileName(resolvedPath);
                    found.Add($"{displayFile} (改名 CE 驱动: {indicator})");
                }
            }
        }

        var dosDevices = dosDeviceNamesProvider is not null
            ? dosDeviceNamesProvider()
            : EnumerateDosDeviceNames();

        foreach (string deviceName in dosDevices)
        {
            if (IsCheatEngineDosDeviceName(deviceName))
            {
                found.Add($"DOS:{deviceName.Trim()}");
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Read-only enumeration of loaded kernel driver base names via <c>K32EnumDeviceDrivers</c> / <c>K32GetDeviceDriverBaseNameW</c>.
    /// </summary>
    public static unsafe IReadOnlyList<string> EnumerateLoadedDriverBaseNames()
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
            // API unavailable; SCM fallback still runs.
        }

        return names;
    }

    /// <summary>
    /// Read-only enumeration of active <c>SERVICE_DRIVER</c> services from Windows SCM together with their
    /// registry <c>ImagePath</c> and resolved local <c>.sys</c> path.
    /// </summary>
    public static IReadOnlyList<ActiveKernelDriverInfo> EnumerateActiveDriverServices()
    {
        var serviceNames = EnumerateActiveDriverServiceNames();
        if (serviceNames.Count == 0)
        {
            return [];
        }

        var results = new List<ActiveKernelDriverInfo>(serviceNames.Count);
        foreach (string serviceName in serviceNames)
        {
            string? rawImagePath = TryReadServiceImagePath(serviceName);
            string? resolvedPath = ResolveDriverImagePath(rawImagePath);
            string? driverFileName = !string.IsNullOrWhiteSpace(resolvedPath)
                ? Path.GetFileName(resolvedPath)
                : (!string.IsNullOrWhiteSpace(rawImagePath) ? Path.GetFileName(rawImagePath.Trim().Trim('"')) : null);

            results.Add(new ActiveKernelDriverInfo(
                ServiceName: serviceName,
                RawImagePath: rawImagePath,
                ResolvedFilePath: resolvedPath,
                DriverFileName: driverFileName));
        }

        return results;
    }

    /// <summary>
    /// Read-only enumeration of active MS-DOS device names via <c>QueryDosDeviceW(null, ...)</c>.
    /// </summary>
    public static unsafe IReadOnlyList<string> EnumerateDosDeviceNames()
    {
        var devices = new List<string>();
        try
        {
            int capacity = 65536;
            const int maxCapacity = 1024 * 1024;

            while (capacity <= maxCapacity)
            {
                char[] buffer = new char[capacity];
                uint returned;
                int lastError;

                fixed (char* pBuf = buffer)
                {
                    returned = QueryDosDeviceW(null, pBuf, (uint)capacity);
                    lastError = returned == 0 ? Marshal.GetLastPInvokeError() : 0;
                }

                if (returned == 0)
                {
                    if (lastError == ErrorInsufficientBuffer)
                    {
                        capacity *= 2;
                        continue;
                    }

                    return devices;
                }

                int limit = Math.Min(capacity, (int)returned);
                int start = 0;
                for (int i = 0; i < limit; i++)
                {
                    if (buffer[i] != '\0')
                    {
                        continue;
                    }

                    if (i == start)
                    {
                        break;
                    }

                    devices.Add(new string(buffer, start, i - start));
                    start = i + 1;
                }

                return devices;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Ignore on unsupported environments.
        }

        return devices;
    }

    private static bool MatchesCheatEngineVersionField(
        string? rawValue,
        string fieldLabel,
        out string? matchedIndicator)
    {
        matchedIndicator = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        string trimmed = rawValue.Trim();

        if (CheatEngineDriverExactNames.Contains(trimmed))
        {
            matchedIndicator = $"{fieldLabel}={trimmed}";
            return true;
        }

        if (trimmed.Contains("dbk64.sys", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("dbk32.sys", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("cheat engine", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("cheatengine", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("dark byte", StringComparison.OrdinalIgnoreCase)
            || DbkTokenRegex().IsMatch(trimmed))
        {
            matchedIndicator = $"{fieldLabel}={trimmed}";
            return true;
        }

        return false;
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

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDeviceW(string? lpDeviceName, char* lpTargetPath, uint ucchMax);

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
