using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Spawn.Modules.Progress.Saves.Rules;

/// <summary>
/// Injectable Windows known-folder and store-account root provider used by
/// <see cref="LudusaviPlaceholderExpander"/> and <see cref="LudusaviRuleResolver"/>
/// so headless unit tests remain 100% isolated from real user profile directories.
/// </summary>
public interface IKnownFolderProvider
{
    string Home { get; }
    string WinAppData { get; }
    string WinLocalAppData { get; }
    string WinLocalAppDataLow { get; }
    string WinDocuments { get; }
    string WinPublic { get; }
    string WinProgramData { get; }
    string WinDir { get; }
    string OsUserName { get; }

    /// <summary>
    /// Known Steam installation roots (containing <c>userdata\&lt;uid&gt;</c>).
    /// </summary>
    IReadOnlyList<string> GetSteamRootDirectories();

    /// <summary>
    /// Known store user IDs (e.g., Steam <c>userdata</c> account IDs, Goldberg default ID,
    /// or <c>gbe_fork</c> configured user IDs), ordered by most recently active first.
    /// </summary>
    IReadOnlyList<string> GetStoreUserIds();
}

/// <summary>
/// Deterministic in-memory <see cref="IKnownFolderProvider"/> for headless testing or custom path overrides.
/// </summary>
public sealed record StaticKnownFolderProvider : IKnownFolderProvider
{
    public required string Home { get; init; }
    public required string WinAppData { get; init; }
    public required string WinLocalAppData { get; init; }
    public required string WinLocalAppDataLow { get; init; }
    public required string WinDocuments { get; init; }
    public required string WinPublic { get; init; }
    public required string WinProgramData { get; init; }
    public required string WinDir { get; init; }
    public required string OsUserName { get; init; }

    public IReadOnlyList<string> SteamRootDirectories { get; init; } = [];
    public IReadOnlyList<string> StoreUserIds { get; init; } = [];

    public IReadOnlyList<string> GetSteamRootDirectories() => SteamRootDirectories;

    public IReadOnlyList<string> GetStoreUserIds() => StoreUserIds;
}

/// <summary>
/// Production Windows known-folder resolver backed by safe read-only <c>shell32.dll!SHGetKnownFolderPath</c>
/// calls (automatically tracking redirected OneDrive / custom-volume <c>Documents</c> and <c>LocalLow</c>)
/// plus Steam <c>userdata</c> and Goldberg / <c>gbe_fork</c> user ID discovery.
/// </summary>
public sealed partial class WindowsKnownFolderResolver : IKnownFolderProvider
{
    /// <summary>Default Goldberg Steam Emulator 64-bit account ID (<c>76561197960265728</c>).</summary>
    public const string DefaultGoldbergUserId = "76561197960265728";

    /// <summary>Standard Steam64 base offset (<c>76561197960265728</c>) used to convert Steam64 to Steam32 account IDs.</summary>
    public const long Steam64BaseOffset = 76561197960265728L;

    /// <summary>Windows <c>FOLDERID_LocalAppDataLow</c> (<c>{A520A1A4-1780-4FF6-BD18-167343C5AF16}</c>).</summary>
    public static readonly Guid FolderIdLocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    /// <summary>Windows <c>FOLDERID_Documents</c> (<c>{FDD39AD0-238F-46AF-ADB4-6C85480369C7}</c>).</summary>
    public static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    /// <summary>Windows <c>FOLDERID_RoamingAppData</c> (<c>{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}</c>).</summary>
    public static readonly Guid FolderIdRoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");

    /// <summary>Windows <c>FOLDERID_LocalAppData</c> (<c>{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}</c>).</summary>
    public static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    /// <summary>Windows <c>FOLDERID_Public</c> (<c>{DFDF76A2-C82A-4D63-906A-5644AC457385}</c>).</summary>
    public static readonly Guid FolderIdPublic = new("DFDF76A2-C82A-4D63-906A-5644AC457385");

    /// <summary>Windows <c>FOLDERID_ProgramData</c> (<c>{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}</c>).</summary>
    public static readonly Guid FolderIdProgramData = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");

    /// <summary>Windows <c>FOLDERID_Profile</c> (<c>{5E6C858F-0E22-4760-9AFE-EA3317B67173}</c>).</summary>
    public static readonly Guid FolderIdProfile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");

    public static WindowsKnownFolderResolver Instance { get; } = new();

    [GeneratedRegex(@"^\s*account_steamid\s*=\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GbeForkSteamIdRegex();

    public string Home =>
        TryResolveKnownFolder(FolderIdProfile)
        ?? NonEmptyOrFallback(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        ?? NonEmptyOrFallback(Environment.GetEnvironmentVariable("USERPROFILE"))
        ?? string.Empty;

    public string WinAppData =>
        TryResolveKnownFolder(FolderIdRoamingAppData)
        ?? NonEmptyOrFallback(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        ?? NonEmptyOrFallback(Environment.GetEnvironmentVariable("APPDATA"))
        ?? string.Empty;

    public string WinLocalAppData =>
        TryResolveKnownFolder(FolderIdLocalAppData)
        ?? NonEmptyOrFallback(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        ?? NonEmptyOrFallback(Environment.GetEnvironmentVariable("LOCALAPPDATA"))
        ?? string.Empty;

    public string WinLocalAppDataLow =>
        TryResolveKnownFolder(FolderIdLocalAppDataLow)
        ?? (string.IsNullOrWhiteSpace(Home) ? string.Empty : NormalizePath(Path.Combine(Home, "AppData", "LocalLow")));

    public string WinDocuments =>
        TryResolveKnownFolder(FolderIdDocuments)
        ?? NonEmptyOrFallback(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        ?? (string.IsNullOrWhiteSpace(Home) ? string.Empty : NormalizePath(Path.Combine(Home, "Documents")));

    public string WinPublic =>
        TryResolveKnownFolder(FolderIdPublic)
        ?? NonEmptyOrFallback(Environment.GetEnvironmentVariable("PUBLIC"))
        ?? string.Empty;

    public string WinProgramData =>
        TryResolveKnownFolder(FolderIdProgramData)
        ?? NonEmptyOrFallback(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
        ?? NonEmptyOrFallback(Environment.GetEnvironmentVariable("PROGRAMDATA"))
        ?? string.Empty;

    public string WinDir =>
        NonEmptyOrFallback(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        ?? NonEmptyOrFallback(Environment.GetEnvironmentVariable("SystemRoot"))
        ?? NonEmptyOrFallback(Environment.GetEnvironmentVariable("WINDIR"))
        ?? string.Empty;

    public string OsUserName => Environment.UserName ?? string.Empty;

    /// <summary>
    /// Resolves a Windows Known Folder path via read-only <c>shell32.dll!SHGetKnownFolderPath</c>.
    /// Never creates directories and returns <see langword="null"/> if unavailable.
    /// </summary>
    public static string? TryResolveKnownFolder(Guid folderId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        int hr = SHGetKnownFolderPath(folderId, 0, nint.Zero, out nint pszPath);
        if (hr != 0 || pszPath == nint.Zero)
        {
            if (pszPath != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pszPath);
            }

            return null;
        }

        try
        {
            string? path = Marshal.PtrToStringUni(pszPath);
            return NonEmptyOrFallback(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pszPath);
        }
    }

    public IReadOnlyList<string> GetSteamRootDirectories()
    {
        var results = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            try
            {
                string normalized = NormalizePath(candidate);
                if (Directory.Exists(normalized) && seen.Add(normalized))
                {
                    results.Add(normalized);
                }
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        if (OperatingSystem.IsWindows())
        {
            TryAdd(TryReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
            TryAdd(TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            TryAdd(TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
        }

        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
        {
            TryAdd(Path.Combine(pf86, "Steam"));
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf))
        {
            TryAdd(Path.Combine(pf, "Steam"));
        }

        return results;
    }

    public IReadOnlyList<string> GetStoreUserIds() =>
        DiscoverStoreUserIds(GetSteamRootDirectories(), WinAppData);

    /// <summary>
    /// Discovers store user IDs across Steam <c>userdata\&lt;uid&gt;</c> roots, Goldberg SteamEmu,
    /// and <c>gbe_fork</c> settings, sorted with the most recently modified active user branch first.
    /// </summary>
    public static IReadOnlyList<string> DiscoverStoreUserIds(
        IEnumerable<string> steamRoots,
        string? winAppDataRoot)
    {
        var ranked = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        void Record(string? id, DateTime activityUtc)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            string trimmed = id.Trim();
            if (trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            {
                return;
            }

            if (!ranked.TryGetValue(trimmed, out DateTime existing) || activityUtc > existing)
            {
                ranked[trimmed] = activityUtc;
            }
        }

        foreach (string steamRoot in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(steamRoot))
            {
                continue;
            }

            string userdataDir = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdataDir) || IsReparsePoint(userdataDir))
            {
                continue;
            }

            try
            {
                foreach (string userDir in Directory.EnumerateDirectories(userdataDir))
                {
                    if (IsReparsePoint(userDir))
                    {
                        continue;
                    }

                    string name = Path.GetFileName(userDir);
                    if (string.IsNullOrWhiteSpace(name) || name == "0")
                    {
                        continue;
                    }

                    DateTime lastWrite = GetLatestActivityUtc(userDir);
                    Record(name, lastWrite);

                    // Also synthesize corresponding Steam64 ID if Steam32 numeric folder exists (used by FromSoftware / SEGA rules).
                    if (long.TryParse(name, out long id32) && id32 > 0 && id32 < 0x1_0000_0000L)
                    {
                        Record((Steam64BaseOffset + id32).ToString(System.Globalization.CultureInfo.InvariantCulture), lastWrite.AddMilliseconds(-1));
                    }
                }
            }
            catch
            {
                // Ignore inaccessible userdata directories.
            }
        }

        // Goldberg SteamEmu & gbe_fork custom user ID discovery.
        if (!string.IsNullOrWhiteSpace(winAppDataRoot))
        {
            TryCollectGoldbergAndGbeForkIds(winAppDataRoot, Record);
        }

        // Always include the canonical Goldberg fallback ID at lowest priority if Goldberg settings exist or as fallback.
        if (!ranked.ContainsKey(DefaultGoldbergUserId))
        {
            Record(DefaultGoldbergUserId, DateTime.MinValue);
        }

        return ranked
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToArray();
    }

    private static void TryCollectGoldbergAndGbeForkIds(
        string winAppDataRoot,
        Action<string?, DateTime> record)
    {
        string goldbergIdFile = Path.Combine(winAppDataRoot, "Goldberg SteamEmu Saves", "settings", "user_steam_id.txt");
        TryReadSingleIdFile(goldbergIdFile, record);

        string gseUserIni = Path.Combine(winAppDataRoot, "GSE Saves", "settings", "configs.user.ini");
        if (File.Exists(gseUserIni) && !IsReparsePoint(gseUserIni))
        {
            try
            {
                string text = File.ReadAllText(gseUserIni);
                Match match = GbeForkSteamIdRegex().Match(text);
                if (match.Success)
                {
                    DateTime mtime = File.GetLastWriteTimeUtc(gseUserIni);
                    string rawId = match.Groups[1].Value;
                    record(rawId, mtime);
                    if (long.TryParse(rawId, out long steam64) && steam64 > Steam64BaseOffset)
                    {
                        record((steam64 - Steam64BaseOffset).ToString(System.Globalization.CultureInfo.InvariantCulture), mtime.AddMilliseconds(-1));
                    }
                }
            }
            catch
            {
                // Ignore unreadable emulator config.
            }
        }

        string gseIdFile = Path.Combine(winAppDataRoot, "GSE Saves", "settings", "user_steam_id.txt");
        TryReadSingleIdFile(gseIdFile, record);
    }

    private static void TryReadSingleIdFile(string filePath, Action<string?, DateTime> record)
    {
        if (!File.Exists(filePath) || IsReparsePoint(filePath))
        {
            return;
        }

        try
        {
            string rawId = File.ReadAllText(filePath).Trim();
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                DateTime mtime = File.GetLastWriteTimeUtc(filePath);
                record(rawId, mtime);
                if (long.TryParse(rawId, out long steam64) && steam64 > Steam64BaseOffset)
                {
                    record((steam64 - Steam64BaseOffset).ToString(System.Globalization.CultureInfo.InvariantCulture), mtime.AddMilliseconds(-1));
                }
            }
        }
        catch
        {
            // Ignore unreadable file.
        }
    }

    private static DateTime GetLatestActivityUtc(string directoryPath)
    {
        DateTime latest = Directory.GetLastWriteTimeUtc(directoryPath);
        try
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                DateTime childTime = File.GetLastWriteTimeUtc(child);
                if (childTime > latest)
                {
                    latest = childTime;
                }
            }
        }
        catch
        {
            // Best-effort timestamp probe.
        }

        return latest;
    }

    private static string? TryReadRegistryString(RegistryKey hive, string subKeyPath, string valueName)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? NonEmptyOrFallback(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    [LibraryImport("shell32.dll", SetLastError = false)]
    private static partial int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        nint hToken,
        out nint ppszPath);
}
