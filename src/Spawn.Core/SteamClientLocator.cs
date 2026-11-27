using System.Globalization;
using System.Text;
using Microsoft.Win32;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Injectable read-only OS/filesystem/registry probe backing <see cref="SteamClientLocator"/>
/// so unit tests stay 100% deterministic, offline, and isolated from the host machine's Steam installation.
/// </summary>
public interface ISteamEnvironmentProbe
{
    string? ReadRegistryString(RegistryHive hive, RegistryView view, string subKeyPath, string valueName);
    int? ReadRegistryDword(RegistryHive hive, RegistryView view, string subKeyPath, string valueName);
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    string? TryReadAllText(string path);
    DateTimeOffset? TryGetLastWriteTimeUtc(string path);
    string GetFolderPath(Environment.SpecialFolder folder);
    bool IsSteamProcessRunning(int? candidatePid, string? expectedSteamExePath, out int? resolvedPid);
}

/// <summary>
/// Production Clean-Host <see cref="ISteamEnvironmentProbe"/> backed by read-only Registry access and
/// <see cref="SafeProcessInspector"/> (<c>PROCESS_QUERY_LIMITED_INFORMATION = 0x1000</c>).
/// </summary>
public sealed class DefaultSteamEnvironmentProbe : ISteamEnvironmentProbe
{
    public static DefaultSteamEnvironmentProbe Instance { get; } = new();

    private DefaultSteamEnvironmentProbe()
    {
    }

    public string? ReadRegistryString(RegistryHive hive, RegistryView view, string subKeyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var subKey = baseKey.OpenSubKey(subKeyPath, writable: false);
            return subKey?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public int? ReadRegistryDword(RegistryHive hive, RegistryView view, string subKeyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var subKey = baseKey.OpenSubKey(subKeyPath, writable: false);
            object? value = subKey?.GetValue(valueName);
            return value switch
            {
                int i => i,
                uint ui => unchecked((int)ui),
                long l => unchecked((int)l),
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public bool DirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public bool FileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return [];
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return [];
            }

            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public string? TryReadAllText(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    public DateTimeOffset? TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (Directory.Exists(path))
            {
                return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            }

            if (File.Exists(path))
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public string GetFolderPath(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);

    public bool IsSteamProcessRunning(int? candidatePid, string? expectedSteamExePath, out int? resolvedPid)
    {
        if (candidatePid is > 4 && SafeProcessInspector.IsProcessAlive(candidatePid.Value))
        {
            string? fullPath = SafeProcessInspector.TryGetProcessExecutablePath(candidatePid.Value);
            if (!string.IsNullOrWhiteSpace(fullPath)
                && string.Equals(Path.GetFileName(fullPath), "steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                resolvedPid = candidatePid.Value;
                return true;
            }
        }

        IReadOnlyList<SafeProcessEntry> processes = SafeProcessInspector.EnumerateProcesses(resolveFullPaths: false);
        foreach (SafeProcessEntry entry in processes)
        {
            if (entry.ProcessId <= 4
                || !string.Equals(entry.ExeFileName, "steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!SafeProcessInspector.IsProcessAlive(entry.ProcessId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(expectedSteamExePath))
            {
                string? fullPath = SafeProcessInspector.TryGetProcessExecutablePath(entry.ProcessId);
                if (!string.IsNullOrWhiteSpace(fullPath)
                    && string.Equals(NormalizePathSafe(fullPath), NormalizePathSafe(expectedSteamExePath), StringComparison.OrdinalIgnoreCase))
                {
                    resolvedPid = entry.ProcessId;
                    return true;
                }
            }

            resolvedPid = entry.ProcessId;
            return true;
        }

        resolvedPid = null;
        return false;
    }

    private static string? NormalizePathSafe(string raw)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return raw;
        }
    }
}

/// <summary>
/// Unified Clean-Host Steam client, executable, running-process, and account locator (<see cref="ISteamClientLocator"/>).
/// </summary>
public sealed class SteamClientLocator : ISteamClientLocator
{
    public const string SteamExecutableFileName = "steam.exe";
    private const string HkcuSteamSubKey = @"Software\Valve\Steam";
    private const string HkcuActiveProcessSubKey = @"Software\Valve\Steam\ActiveProcess";

    public static SteamClientLocator Default { get; } = new(DefaultSteamEnvironmentProbe.Instance);

    private readonly ISteamEnvironmentProbe _probe;

    public SteamClientLocator(ISteamEnvironmentProbe? probe = null)
    {
        _probe = probe ?? DefaultSteamEnvironmentProbe.Instance;
    }

    public bool TryNormalizeInstallDirectory(
        string? rawPathOrExe,
        out string? normalizedInstallDir,
        out string? steamExePath,
        out string? errorMessage)
    {
        normalizedInstallDir = null;
        steamExePath = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawPathOrExe))
        {
            errorMessage = "Steam 路径不能为空。";
            return false;
        }

        string? candidateDir = NormalizeInstallDirectoryInput(rawPathOrExe);
        if (string.IsNullOrWhiteSpace(candidateDir))
        {
            errorMessage = "输入的 Steam 路径格式无效。";
            return false;
        }

        if (!_probe.DirectoryExists(candidateDir))
        {
            errorMessage = $"找不到指定的 Steam 目录：{candidateDir}";
            return false;
        }

        string exeCandidate = Path.Combine(candidateDir, SteamExecutableFileName);
        if (!_probe.FileExists(exeCandidate))
        {
            errorMessage = $"所选目录中未找到 {SteamExecutableFileName}：{candidateDir}";
            return false;
        }

        normalizedInstallDir = candidateDir;
        steamExePath = exeCandidate;
        return true;
    }

    public SteamClientDiscoveryResult Discover(
        string? explicitInstallDirOverride = null,
        uint? preferredAccountId = null)
    {
        bool hasExplicitOverride = !string.IsNullOrWhiteSpace(explicitInstallDirOverride);
        string? resolvedDir = null;
        string? resolvedExe = null;
        SteamPathDiscoverySource resolvedSource = SteamPathDiscoverySource.NotFound;
        string? warningMessage = null;

        if (hasExplicitOverride)
        {
            string? overrideDir = NormalizeInstallDirectoryInput(explicitInstallDirOverride!);
            if (string.IsNullOrWhiteSpace(overrideDir) || !_probe.DirectoryExists(overrideDir))
            {
                return new SteamClientDiscoveryResult
                {
                    IsDetected = false,
                    InstallDirectory = overrideDir,
                    SteamExecutablePath = null,
                    Source = SteamPathDiscoverySource.UserOverride,
                    IsUserOverride = true,
                    WarningMessage = "手动指定的 Steam 目录不存在，请检查路径或恢复自动检测。",
                };
            }

            string overrideExe = Path.Combine(overrideDir, SteamExecutableFileName);
            if (!_probe.FileExists(overrideExe))
            {
                return new SteamClientDiscoveryResult
                {
                    IsDetected = false,
                    InstallDirectory = overrideDir,
                    SteamExecutablePath = null,
                    Source = SteamPathDiscoverySource.UserOverride,
                    IsUserOverride = true,
                    WarningMessage = $"手动指定的 Steam 目录中未找到 {SteamExecutableFileName}，请选择正确的 Steam 安装目录。",
                };
            }

            resolvedDir = overrideDir;
            resolvedExe = overrideExe;
            resolvedSource = SteamPathDiscoverySource.UserOverride;
        }
        else
        {
            IReadOnlyList<(string Directory, SteamPathDiscoverySource Source)> candidates = EnumerateCandidateInstallDirectories(explicitInstallDirOverride: null);
            string? firstDirWithoutExe = null;
            SteamPathDiscoverySource firstDirWithoutExeSource = SteamPathDiscoverySource.NotFound;

            for (int i = 0; i < candidates.Count; i++)
            {
                (string candidateDir, SteamPathDiscoverySource source) = candidates[i];
                string exeCandidate = Path.Combine(candidateDir, SteamExecutableFileName);
                if (_probe.FileExists(exeCandidate))
                {
                    resolvedDir = candidateDir;
                    resolvedExe = exeCandidate;
                    resolvedSource = source;
                    break;
                }

                if (firstDirWithoutExe is null)
                {
                    firstDirWithoutExe = candidateDir;
                    firstDirWithoutExeSource = source;
                }
            }

            if (resolvedDir is null)
            {
                return new SteamClientDiscoveryResult
                {
                    IsDetected = false,
                    InstallDirectory = firstDirWithoutExe,
                    SteamExecutablePath = null,
                    Source = firstDirWithoutExe is not null ? firstDirWithoutExeSource : SteamPathDiscoverySource.NotFound,
                    IsUserOverride = false,
                    WarningMessage = firstDirWithoutExe is not null
                        ? $"检测到 Steam 目录（{firstDirWithoutExe}），但未找到 {SteamExecutableFileName}，请在设置中手动指定。"
                        : "未在本机自动检测到 Steam 客户端，如使用便携版或自定义路径请在设置中手动指定。",
                };
            }
        }

        // Probe live Steam process & ActiveProcess registry keys.
        int? regActivePid = _probe.ReadRegistryDword(RegistryHive.CurrentUser, RegistryView.Default, HkcuActiveProcessSubKey, "pid");
        int? regActiveUserRaw = _probe.ReadRegistryDword(RegistryHive.CurrentUser, RegistryView.Default, HkcuActiveProcessSubKey, "ActiveUser");

        bool isRunning = _probe.IsSteamProcessRunning(regActivePid, resolvedExe, out int? livePid);
        uint? activeProcessUserId = isRunning && regActiveUserRaw is > 0
            ? unchecked((uint)regActiveUserRaw.Value)
            : null;

        SteamAccountInfo[] accounts = DiscoverAccounts(resolvedDir, activeProcessUserId);
        uint? activeAccountId = ResolveEffectiveAccountId(accounts, preferredAccountId, activeProcessUserId, out string? accountWarning);
        if (!string.IsNullOrWhiteSpace(accountWarning))
        {
            warningMessage = string.IsNullOrWhiteSpace(warningMessage)
                ? accountWarning
                : $"{warningMessage} {accountWarning}";
        }

        return new SteamClientDiscoveryResult
        {
            IsDetected = true,
            InstallDirectory = resolvedDir,
            SteamExecutablePath = resolvedExe,
            Source = resolvedSource,
            IsUserOverride = hasExplicitOverride,
            IsSteamRunning = isRunning,
            SteamProcessId = livePid,
            ActiveAccountId = activeAccountId,
            Accounts = accounts,
            WarningMessage = warningMessage,
        };
    }

    /// <summary>
    /// Enumerates existing candidate Steam root directories in strict precedence order (without requiring <c>steam.exe</c>
    /// to exist, supporting sound-only and directory-level consumers such as <see cref="SteamClientSoundLocator"/>).
    /// </summary>
    public IReadOnlyList<(string Directory, SteamPathDiscoverySource Source)> EnumerateCandidateInstallDirectories(
        string? explicitInstallDirOverride = null)
    {
        var results = new List<(string Directory, SteamPathDiscoverySource Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? rawPath, SteamPathDiscoverySource source)
        {
            string? normalized = NormalizeInstallDirectoryInput(rawPath);
            if (!string.IsNullOrWhiteSpace(normalized)
                && _probe.DirectoryExists(normalized)
                && seen.Add(normalized))
            {
                results.Add((normalized, source));
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitInstallDirOverride))
        {
            TryAdd(explicitInstallDirOverride, SteamPathDiscoverySource.UserOverride);
            return results;
        }

        // 1. HKCU\Software\Valve\Steam ("SteamPath" and "SteamExe")
        TryAdd(_probe.ReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath"), SteamPathDiscoverySource.RegistryCurrentUser);
        TryAdd(_probe.ReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamExe"), SteamPathDiscoverySource.RegistryCurrentUser);

        // 2. HKLM 32-bit, WOW6432Node, and 64-bit ("InstallPath")
        TryAdd(_probe.ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"), SteamPathDiscoverySource.RegistryLocalMachine);
        TryAdd(_probe.ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"), SteamPathDiscoverySource.RegistryLocalMachine);
        TryAdd(_probe.ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"), SteamPathDiscoverySource.RegistryLocalMachine);

        // 3. Standard ProgramFiles(x86)\Steam & ProgramFiles\Steam fallbacks
        string progX86 = _probe.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
        {
            TryAdd(Path.Combine(progX86, "Steam"), SteamPathDiscoverySource.DefaultDirectory);
        }

        string progX64 = _probe.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(progX64))
        {
            TryAdd(Path.Combine(progX64, "Steam"), SteamPathDiscoverySource.DefaultDirectory);
        }

        return results;
    }

    private SteamAccountInfo[] DiscoverAccounts(string steamInstallDir, uint? activeProcessUserId)
    {
        var merged = new Dictionary<uint, AccountAccumulator>();

        AccountAccumulator GetOrCreate(uint accountId)
        {
            if (!merged.TryGetValue(accountId, out AccountAccumulator? acc))
            {
                acc = new AccountAccumulator(accountId)
                {
                    SteamId64 = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(accountId),
                    ShortcutsVdfPath = BuildShortcutsVdfPath(steamInstallDir, accountId),
                };
                merged[accountId] = acc;
            }

            return acc;
        }

        // 1. Parse config\loginusers.vdf
        string loginUsersPath = Path.Combine(steamInstallDir, "config", "loginusers.vdf");
        string? loginUsersText = _probe.TryReadAllText(loginUsersPath);
        if (!string.IsNullOrWhiteSpace(loginUsersText))
        {
            foreach (SteamLoginUserEntry entry in SteamLoginUsersVdfParser.Parse(loginUsersText))
            {
                AccountAccumulator acc = GetOrCreate(entry.AccountId);
                acc.SteamId64 = entry.SteamId64;
                acc.AccountName = entry.AccountName ?? acc.AccountName;
                acc.PersonaName = entry.PersonaName ?? acc.PersonaName;
                acc.IsMostRecent = acc.IsMostRecent || entry.MostRecent;
                if (entry.Timestamp.HasValue && (!acc.LastTimestamp.HasValue || entry.Timestamp.Value > acc.LastTimestamp.Value))
                {
                    acc.LastTimestamp = entry.Timestamp;
                }
            }
        }

        // 2. Enumerate userdata\<numeric accountId> folders (excluding 0)
        string userdataDir = Path.Combine(steamInstallDir, "userdata");
        if (_probe.DirectoryExists(userdataDir))
        {
            foreach (string childDir in _probe.EnumerateDirectories(userdataDir))
            {
                string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(childDir));
                if (!uint.TryParse(folderName, NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) || uid == 0)
                {
                    continue;
                }

                AccountAccumulator acc = GetOrCreate(uid);
                acc.HasUserdataDirectory = true;
                acc.ShortcutsVdfPath = BuildShortcutsVdfPath(steamInstallDir, uid);
                acc.UserdataLastWriteUtc = _probe.TryGetLastWriteTimeUtc(childDir);
            }
        }

        // 3. Apply ActiveProcess ActiveUser signal if Steam is running & logged in
        if (activeProcessUserId is > 0)
        {
            AccountAccumulator acc = GetOrCreate(activeProcessUserId.Value);
            acc.IsActiveProcessUser = true;
        }

        return merged.Values
            .OrderByDescending(static a => a.IsActiveProcessUser)
            .ThenByDescending(static a => a.IsMostRecent)
            .ThenByDescending(static a => a.HasUserdataDirectory)
            .ThenByDescending(static a => a.LastTimestamp ?? 0L)
            .ThenByDescending(static a => a.UserdataLastWriteUtc ?? DateTimeOffset.MinValue)
            .ThenBy(static a => a.AccountId)
            .Select(static a => new SteamAccountInfo
            {
                AccountId = a.AccountId,
                SteamId64 = a.SteamId64,
                AccountName = a.AccountName,
                PersonaName = a.PersonaName,
                IsMostRecent = a.IsMostRecent,
                IsActiveProcessUser = a.IsActiveProcessUser,
                LastTimestamp = a.LastTimestamp,
                ShortcutsVdfPath = a.ShortcutsVdfPath,
            })
            .ToArray();
    }

    private static uint? ResolveEffectiveAccountId(
        SteamAccountInfo[] accounts,
        uint? preferredAccountId,
        uint? activeProcessUserId,
        out string? warningMessage)
    {
        warningMessage = null;

        if (preferredAccountId is > 0)
        {
            if (accounts.Any(a => a.AccountId == preferredAccountId.Value))
            {
                return preferredAccountId.Value;
            }

            warningMessage = $"偏好 Steam 账号 ({preferredAccountId.Value.ToString(CultureInfo.InvariantCulture)}) 未在当前 Steam 目录中发现，已自动回退到可用账号。";
        }

        if (activeProcessUserId is > 0 && accounts.Any(a => a.AccountId == activeProcessUserId.Value))
        {
            return activeProcessUserId.Value;
        }

        SteamAccountInfo? mostRecent = accounts.FirstOrDefault(static a => a.IsMostRecent);
        if (mostRecent is not null)
        {
            return mostRecent.AccountId;
        }

        return accounts.Length > 0 ? accounts[0].AccountId : null;
    }

    private string? NormalizeInstallDirectoryInput(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            string trimmed = rawPath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            string full = Path.GetFullPath(trimmed);
            if (string.Equals(Path.GetFileName(full), SteamExecutableFileName, StringComparison.OrdinalIgnoreCase)
                || (_probe.FileExists(full) && !_probe.DirectoryExists(full)))
            {
                full = Path.GetDirectoryName(full) ?? full;
            }

            return Path.TrimEndingDirectorySeparator(full);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildShortcutsVdfPath(string steamInstallDir, uint accountId) =>
        Path.Combine(steamInstallDir, "userdata", accountId.ToString(CultureInfo.InvariantCulture), "config", "shortcuts.vdf");

    private sealed class AccountAccumulator(uint accountId)
    {
        public uint AccountId { get; } = accountId;
        public ulong SteamId64 { get; set; }
        public string? AccountName { get; set; }
        public string? PersonaName { get; set; }
        public bool IsMostRecent { get; set; }
        public bool IsActiveProcessUser { get; set; }
        public long? LastTimestamp { get; set; }
        public bool HasUserdataDirectory { get; set; }
        public DateTimeOffset? UserdataLastWriteUtc { get; set; }
        public string? ShortcutsVdfPath { get; set; }
    }
}
