using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

internal sealed class FakeSteamEnvironmentProbe : ISteamEnvironmentProbe
{
    private readonly Dictionary<(RegistryHive Hive, RegistryView View, string SubKey, string Value), string> _regStrings = new();
    private readonly Dictionary<(RegistryHive Hive, RegistryView View, string SubKey, string Value), int> _regDwords = new();
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastWriteTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Environment.SpecialFolder, string> _specialFolders = new();
    private readonly string? _realDiskSandboxRoot;

    public FakeSteamEnvironmentProbe(string? realDiskSandboxRoot = null)
    {
        _realDiskSandboxRoot = string.IsNullOrWhiteSpace(realDiskSandboxRoot)
            ? null
            : NormalizeKey(realDiskSandboxRoot);
    }

    public bool SteamRunning { get; set; }
    public int? LiveSteamPid { get; set; }
    public int? LastCandidatePidChecked { get; private set; }
    public string? LastExpectedSteamExeChecked { get; private set; }
    public int IsSteamProcessRunningCallCount { get; private set; }

    public FakeSteamEnvironmentProbe SetRegistryString(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        string valueName,
        string? value)
    {
        var key = (hive, view, subKeyPath.ToLowerInvariant(), valueName.ToLowerInvariant());
        if (value is null)
        {
            _regStrings.Remove(key);
        }
        else
        {
            _regStrings[key] = value;
        }

        return this;
    }

    public FakeSteamEnvironmentProbe SetRegistryDword(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        string valueName,
        int? value)
    {
        var key = (hive, view, subKeyPath.ToLowerInvariant(), valueName.ToLowerInvariant());
        if (!value.HasValue)
        {
            _regDwords.Remove(key);
        }
        else
        {
            _regDwords[key] = value.Value;
        }

        return this;
    }

    public FakeSteamEnvironmentProbe SetSpecialFolder(Environment.SpecialFolder folder, string path)
    {
        _specialFolders[folder] = path;
        return this;
    }

    public FakeSteamEnvironmentProbe AddDirectory(string path, DateTimeOffset? lastWriteUtc = null)
    {
        string normalized = NormalizeKey(path);
        _directories.Add(normalized);
        if (lastWriteUtc.HasValue)
        {
            _lastWriteTimesUtc[normalized] = lastWriteUtc.Value;
        }

        return this;
    }

    public FakeSteamEnvironmentProbe AddFile(string path, string? textContent = null, DateTimeOffset? lastWriteUtc = null)
    {
        string normalized = NormalizeKey(path);
        string? parent = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            _directories.Add(NormalizeKey(parent));
        }

        _files.Add(normalized);
        if (textContent is not null)
        {
            _fileTexts[normalized] = textContent;
        }

        if (lastWriteUtc.HasValue)
        {
            _lastWriteTimesUtc[normalized] = lastWriteUtc.Value;
        }

        return this;
    }

    public FakeSteamEnvironmentProbe AddSteamInstall(string steamDir, bool includeSteamExe = true)
    {
        AddDirectory(steamDir);
        if (includeSteamExe)
        {
            AddFile(Path.Combine(steamDir, "steam.exe"));
        }

        return this;
    }

    public string? ReadRegistryString(RegistryHive hive, RegistryView view, string subKeyPath, string valueName) =>
        _regStrings.TryGetValue((hive, view, subKeyPath.ToLowerInvariant(), valueName.ToLowerInvariant()), out string? value)
            ? value
            : null;

    public int? ReadRegistryDword(RegistryHive hive, RegistryView view, string subKeyPath, string valueName) =>
        _regDwords.TryGetValue((hive, view, subKeyPath.ToLowerInvariant(), valueName.ToLowerInvariant()), out int value)
            ? value
            : null;

    public bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string key = NormalizeKey(path);
        if (_directories.Contains(key))
        {
            return true;
        }

        return IsInsideRealSandbox(key) && Directory.Exists(key);
    }

    public bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string key = NormalizeKey(path);
        if (_files.Contains(key))
        {
            return true;
        }

        return IsInsideRealSandbox(key) && File.Exists(key);
    }

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        string parentKey = NormalizeKey(path);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in _directories)
        {
            string? dirParent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrWhiteSpace(dirParent)
                && string.Equals(NormalizeKey(dirParent), parentKey, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(dir);
            }
        }

        if (IsInsideRealSandbox(parentKey) && Directory.Exists(parentKey))
        {
            foreach (string child in Directory.EnumerateDirectories(parentKey))
            {
                found.Add(NormalizeKey(child));
            }
        }

        return found.ToArray();
    }

    public string? TryReadAllText(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string key = NormalizeKey(path);
        if (_fileTexts.TryGetValue(key, out string? text))
        {
            return text;
        }

        if (IsInsideRealSandbox(key) && File.Exists(key))
        {
            return File.ReadAllText(key);
        }

        return null;
    }

    public DateTimeOffset? TryGetLastWriteTimeUtc(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string key = NormalizeKey(path);
        if (_lastWriteTimesUtc.TryGetValue(key, out DateTimeOffset ts))
        {
            return ts;
        }

        if (IsInsideRealSandbox(key))
        {
            if (Directory.Exists(key))
            {
                return new DateTimeOffset(Directory.GetLastWriteTimeUtc(key), TimeSpan.Zero);
            }

            if (File.Exists(key))
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(key), TimeSpan.Zero);
            }
        }

        return null;
    }

    public string GetFolderPath(Environment.SpecialFolder folder) =>
        _specialFolders.TryGetValue(folder, out string? value) ? value : string.Empty;

    public bool IsSteamProcessRunning(int? candidatePid, string? expectedSteamExePath, out int? resolvedPid)
    {
        IsSteamProcessRunningCallCount++;
        LastCandidatePidChecked = candidatePid;
        LastExpectedSteamExeChecked = expectedSteamExePath;

        if (SteamRunning)
        {
            resolvedPid = LiveSteamPid ?? candidatePid ?? 4242;
            return true;
        }

        resolvedPid = null;
        return false;
    }

    private bool IsInsideRealSandbox(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(_realDiskSandboxRoot))
        {
            return false;
        }

        if (string.Equals(normalizedPath, _realDiskSandboxRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = _realDiskSandboxRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar)));
}

public sealed class SteamClientLocatorTests
{
    private const string HkcuSteamSubKey = @"Software\Valve\Steam";
    private const string HkcuActiveProcessSubKey = @"Software\Valve\Steam\ActiveProcess";

    [Fact]
    public void Discover_ExplicitOverride_TakesPrecedenceOverRegistryAndNormalizesDirectoryOrSteamExePath()
    {
        string overrideDir = @"D:\PortableApps\SteamClient";
        string registryDir = @"C:\RegistrySteam";

        var probe = new FakeSteamEnvironmentProbe()
            .AddSteamInstall(overrideDir)
            .AddSteamInstall(registryDir)
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", "C:/RegistrySteam");

        var locator = new SteamClientLocator(probe);

        // 1. Directory path with forward slashes, quotes, and trailing slash
        SteamClientDiscoveryResult fromDir = locator.Discover("\"D:/PortableApps/SteamClient/\"");
        Assert.True(fromDir.IsDetected);
        Assert.True(fromDir.IsUserOverride);
        Assert.Equal(SteamPathDiscoverySource.UserOverride, fromDir.Source);
        Assert.Equal(overrideDir, fromDir.InstallDirectory);
        Assert.Equal(Path.Combine(overrideDir, "steam.exe"), fromDir.SteamExecutablePath);
        Assert.Null(fromDir.WarningMessage);

        // 2. Direct steam.exe path (mixed case + forward slashes)
        SteamClientDiscoveryResult fromExe = locator.Discover("D:/PortableApps/SteamClient/Steam.EXE");
        Assert.True(fromExe.IsDetected);
        Assert.True(fromExe.IsUserOverride);
        Assert.Equal(SteamPathDiscoverySource.UserOverride, fromExe.Source);
        Assert.Equal(overrideDir, fromExe.InstallDirectory);
        Assert.Equal(Path.Combine(overrideDir, "steam.exe"), fromExe.SteamExecutablePath);

        // 3. TryNormalizeInstallDirectory accepts both directory and direct steam.exe path
        Assert.True(locator.TryNormalizeInstallDirectory(
            "\"D:/PortableApps/SteamClient/steam.exe\"",
            out string? normalizedDir,
            out string? exePath,
            out string? errorMessage));
        Assert.Equal(overrideDir, normalizedDir);
        Assert.Equal(Path.Combine(overrideDir, "steam.exe"), exePath);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void Discover_AndTryNormalize_InvalidOverrides_FailCleanlyWithoutFallingBackToRegistry()
    {
        string dirWithoutExe = @"D:\EmptySteamDir";
        string registryDir = @"C:\ValidRegistrySteam";

        var probe = new FakeSteamEnvironmentProbe()
            .AddDirectory(dirWithoutExe)
            .AddSteamInstall(registryDir)
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", registryDir);

        var locator = new SteamClientLocator(probe);

        // Missing directory override must NOT fall back to registry
        SteamClientDiscoveryResult missing = locator.Discover(@"Z:\DoesNotExist\Steam");
        Assert.False(missing.IsDetected);
        Assert.True(missing.IsUserOverride);
        Assert.Equal(SteamPathDiscoverySource.UserOverride, missing.Source);
        Assert.Null(missing.SteamExecutablePath);
        Assert.False(string.IsNullOrWhiteSpace(missing.WarningMessage));

        // Existing directory missing steam.exe must report IsDetected = false
        SteamClientDiscoveryResult noExe = locator.Discover(dirWithoutExe);
        Assert.False(noExe.IsDetected);
        Assert.True(noExe.IsUserOverride);
        Assert.Equal(SteamPathDiscoverySource.UserOverride, noExe.Source);
        Assert.Equal(dirWithoutExe, noExe.InstallDirectory);
        Assert.Null(noExe.SteamExecutablePath);
        Assert.Contains("steam.exe", noExe.WarningMessage, StringComparison.OrdinalIgnoreCase);

        // TryNormalizeInstallDirectory error cases
        Assert.False(locator.TryNormalizeInstallDirectory("   ", out _, out _, out string? emptyErr));
        Assert.False(string.IsNullOrWhiteSpace(emptyErr));

        Assert.False(locator.TryNormalizeInstallDirectory(@"Z:\Missing\Steam", out _, out _, out string? missingErr));
        Assert.False(string.IsNullOrWhiteSpace(missingErr));

        Assert.False(locator.TryNormalizeInstallDirectory(dirWithoutExe, out _, out _, out string? noExeErr));
        Assert.Contains("steam.exe", noExeErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_RegistryAndDefaultPrecedence_FollowsExactFallbackOrder()
    {
        string hkcuPathDir = @"D:\Steam_HkcuPath";
        string hkcuExeDir = @"D:\Steam_HkcuExe";
        string hklm32Dir = @"D:\Steam_Hklm32";
        string hklmWow64Dir = @"D:\Steam_HklmWow64";
        string hklm64Dir = @"D:\Steam_Hklm64";
        string progX86Root = @"C:\FakeProgramFilesX86";
        string progX86Steam = Path.Combine(progX86Root, "Steam");
        string progX64Root = @"C:\FakeProgramFiles";
        string progX64Steam = Path.Combine(progX64Root, "Steam");

        var probe = new FakeSteamEnvironmentProbe()
            .AddSteamInstall(hkcuPathDir)
            .AddSteamInstall(hkcuExeDir)
            .AddSteamInstall(hklm32Dir)
            .AddSteamInstall(hklmWow64Dir)
            .AddSteamInstall(hklm64Dir)
            .AddSteamInstall(progX86Steam)
            .AddSteamInstall(progX64Steam)
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", "D:/Steam_HkcuPath")
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamExe", "D:/Steam_HkcuExe/steam.exe")
            .SetRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath", hklm32Dir)
            .SetRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", hklmWow64Dir)
            .SetRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath", hklm64Dir)
            .SetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86, progX86Root)
            .SetSpecialFolder(Environment.SpecialFolder.ProgramFiles, progX64Root);

        var locator = new SteamClientLocator(probe);

        // Step 1: HKCU SteamPath (forward slashes normalized)
        var r1 = locator.Discover();
        Assert.True(r1.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.RegistryCurrentUser, r1.Source);
        Assert.Equal(hkcuPathDir, r1.InstallDirectory);

        // Step 2: HKCU SteamExe parent dir
        probe.SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", null);
        var r2 = locator.Discover();
        Assert.True(r2.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.RegistryCurrentUser, r2.Source);
        Assert.Equal(hkcuExeDir, r2.InstallDirectory);

        // Step 3: HKLM Registry32 InstallPath
        probe.SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamExe", null);
        var r3 = locator.Discover();
        Assert.True(r3.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.RegistryLocalMachine, r3.Source);
        Assert.Equal(hklm32Dir, r3.InstallDirectory);

        // Step 4: HKLM Registry64 WOW6432Node InstallPath
        probe.SetRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath", null);
        var r4 = locator.Discover();
        Assert.True(r4.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.RegistryLocalMachine, r4.Source);
        Assert.Equal(hklmWow64Dir, r4.InstallDirectory);

        // Step 5: HKLM Registry64 SOFTWARE\Valve\Steam InstallPath
        probe.SetRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null);
        var r5 = locator.Discover();
        Assert.True(r5.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.RegistryLocalMachine, r5.Source);
        Assert.Equal(hklm64Dir, r5.InstallDirectory);

        // Step 6: Default ProgramFiles(x86)\Steam
        probe.SetRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath", null);
        var r6 = locator.Discover();
        Assert.True(r6.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.DefaultDirectory, r6.Source);
        Assert.Equal(progX86Steam, r6.InstallDirectory);

        // Step 7: Default ProgramFiles\Steam
        probe.SetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86, string.Empty);
        var r7 = locator.Discover();
        Assert.True(r7.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.DefaultDirectory, r7.Source);
        Assert.Equal(progX64Steam, r7.InstallDirectory);

        // Step 8: Not found anywhere
        probe.SetSpecialFolder(Environment.SpecialFolder.ProgramFiles, string.Empty);
        var r8 = locator.Discover();
        Assert.False(r8.IsDetected);
        Assert.Equal(SteamPathDiscoverySource.NotFound, r8.Source);
        Assert.Null(r8.InstallDirectory);
        Assert.Null(r8.SteamExecutablePath);
        Assert.False(string.IsNullOrWhiteSpace(r8.WarningMessage));
    }

    [Fact]
    public void Discover_SkipsCandidateDirectoryMissingSteamExeAndFallsThroughToNextValidCandidate()
    {
        string brokenHkcuDir = @"D:\BrokenSteamDir";
        string validHklmDir = @"D:\HealthySteamDir";

        var probe = new FakeSteamEnvironmentProbe()
            .AddDirectory(brokenHkcuDir) // no steam.exe
            .AddSteamInstall(validHklmDir)
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", brokenHkcuDir)
            .SetRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath", validHklmDir);

        var locator = new SteamClientLocator(probe);
        var result = locator.Discover();
        Assert.True(result.IsDetected);
        Assert.Equal(validHklmDir, result.InstallDirectory);
        Assert.Equal(SteamPathDiscoverySource.RegistryLocalMachine, result.Source);

        // If only the exe-less directory exists, report directory + warning cleanly.
        var brokenOnlyProbe = new FakeSteamEnvironmentProbe()
            .AddDirectory(brokenHkcuDir)
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", brokenHkcuDir);
        var brokenOnlyResult = new SteamClientLocator(brokenOnlyProbe).Discover();
        Assert.False(brokenOnlyResult.IsDetected);
        Assert.Equal(brokenHkcuDir, brokenOnlyResult.InstallDirectory);
        Assert.Equal(SteamPathDiscoverySource.RegistryCurrentUser, brokenOnlyResult.Source);
        Assert.Contains("steam.exe", brokenOnlyResult.WarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoginUsersVdfParser_ConvertsSteam64ToSteam32AndBack_WithBoundaryValidation()
    {
        const uint accountId = 123456789u;
        ulong steamId64 = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(accountId);
        Assert.Equal(76561197960265728UL + 123456789UL, steamId64);

        Assert.True(SteamLoginUsersVdfParser.TryConvertSteamId64ToAccountId(steamId64, out uint parsed));
        Assert.Equal(accountId, parsed);

        // Zero / invalid boundaries
        Assert.Equal(0UL, SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(0u));
        Assert.False(SteamLoginUsersVdfParser.TryConvertSteamId64ToAccountId(0UL, out _));
        Assert.False(SteamLoginUsersVdfParser.TryConvertSteamId64ToAccountId(SteamLoginUsersVdfParser.SteamId64BaseOffset, out _));
        Assert.False(SteamLoginUsersVdfParser.TryConvertSteamId64ToAccountId(
            SteamLoginUsersVdfParser.SteamId64BaseOffset + (ulong)uint.MaxValue + 1UL,
            out _));

        // Max valid uint account ID
        ulong maxSteam64 = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(uint.MaxValue);
        Assert.True(SteamLoginUsersVdfParser.TryConvertSteamId64ToAccountId(maxSteam64, out uint maxParsed));
        Assert.Equal(uint.MaxValue, maxParsed);
    }

    [Fact]
    public void LoginUsersVdfParser_ParsesMostRecentTimestampEscapesCommentsAndMalformedInput()
    {
        ulong idA = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(1001u);
        ulong idB = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(2002u);
        ulong idC = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(3003u);

        string vdf = $$"""
            // Steam loginusers header comment
            "users"
            {
                "{{idA}}"
                {
                    "AccountName"   "alpha_user" // trailing comment
                    "PersonaName"   "Alpha \"The Hero\" \\ One"
                    "RememberPassword"  "1"
                    "MostRecent"    "0"
                    "Timestamp"     "1760000999"
                    "NestedExtra"
                    {
                        "foo" "bar"
                    }
                }
                "{{idB}}"
                {
                    "AccountName"   "beta_user"
                    "PersonaName"   "Beta Player"
                    "MostRecent"    "1"
                    "Timestamp"     "1760000100"
                }
                "{{idC}}"
                {
                    "AccountName"   "gamma_user"
                    "PersonaName"   "Gamma"
                    "MostRecent"    "0"
                    "Timestamp"     "1760000500"
                }
                "not_a_steam_id"
                {
                    "AccountName"   "ignored"
                }
            }
            """;

        // Include UTF-8 BOM prefix to verify BOM handling
        IReadOnlyList<SteamLoginUserEntry> parsed = SteamLoginUsersVdfParser.Parse("\uFEFF" + vdf);
        Assert.Equal(3, parsed.Count);

        // MostRecent="1" wins even with older timestamp
        Assert.Equal(2002u, parsed[0].AccountId);
        Assert.True(parsed[0].MostRecent);
        Assert.Equal("beta_user", parsed[0].AccountName);
        Assert.Equal("Beta Player", parsed[0].PersonaName);

        // Remaining sorted by Timestamp descending
        Assert.Equal(1001u, parsed[1].AccountId);
        Assert.Equal("Alpha \"The Hero\" \\ One", parsed[1].PersonaName);
        Assert.Equal(1760000999L, parsed[1].Timestamp);

        Assert.Equal(3003u, parsed[2].AccountId);
        Assert.Equal(1760000500L, parsed[2].Timestamp);

        // Fallback to latest Timestamp when nobody has MostRecent="1"
        string noMostRecentVdf = $$"""
            users
            {
                "{{idC}}" { "AccountName" "older" "MostRecent" "0" "Timestamp" "1700000100" }
                "{{idA}}" { "AccountName" "newest" "MostRecent" "0" "Timestamp" "1700000900" }
            }
            """;
        IReadOnlyList<SteamLoginUserEntry> fallbackParsed = SteamLoginUsersVdfParser.Parse(noMostRecentVdf);
        Assert.Equal(2, fallbackParsed.Count);
        Assert.Equal(1001u, fallbackParsed[0].AccountId);
        Assert.Equal(3003u, fallbackParsed[1].AccountId);

        // Empty / malformed resilience
        Assert.Empty(SteamLoginUsersVdfParser.Parse(null));
        Assert.Empty(SteamLoginUsersVdfParser.Parse("   \r\n // comment only"));
        Assert.Empty(SteamLoginUsersVdfParser.Parse("\"other_root\" { \"k\" \"v\" }"));
        Assert.Empty(SteamLoginUsersVdfParser.Parse("\"users\" { { { \"unterminated"));
    }

    [Fact]
    public void Discover_ResolvesActiveProcessUser_PreferredAccountOverride_AndUserdataFallbackExcludingZero()
    {
        string steamDir = @"D:\SteamMain";
        ulong id100 = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(100u);
        ulong id200 = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(200u);

        string loginUsers = $$"""
            "users"
            {
                "{{id100}}"
                {
                    "AccountName" "main_acc"
                    "PersonaName" "Main Persona"
                    "MostRecent"  "1"
                    "Timestamp"   "1760001000"
                }
                "{{id200}}"
                {
                    "AccountName" "alt_acc"
                    "PersonaName" "Alt Persona"
                    "MostRecent"  "0"
                    "Timestamp"   "1760000500"
                }
            }
            """;

        var probe = new FakeSteamEnvironmentProbe()
            .AddSteamInstall(steamDir)
            .AddFile(Path.Combine(steamDir, "config", "loginusers.vdf"), loginUsers)
            .AddDirectory(Path.Combine(steamDir, "userdata"))
            .AddDirectory(Path.Combine(steamDir, "userdata", "0"), new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero))
            .AddDirectory(Path.Combine(steamDir, "userdata", "non_numeric"))
            .AddDirectory(Path.Combine(steamDir, "userdata", "100"), new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero))
            .AddDirectory(Path.Combine(steamDir, "userdata", "200"), new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero))
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", steamDir)
            .SetRegistryDword(RegistryHive.CurrentUser, RegistryView.Default, HkcuActiveProcessSubKey, "pid", 8888)
            .SetRegistryDword(RegistryHive.CurrentUser, RegistryView.Default, HkcuActiveProcessSubKey, "ActiveUser", 200);

        var locator = new SteamClientLocator(probe);

        // 1. Steam running -> ActiveUser (200) overrides MostRecent (100) and excludes userdata\0
        probe.SteamRunning = true;
        probe.LiveSteamPid = 8888;
        var running = locator.Discover();
        Assert.True(running.IsSteamRunning);
        Assert.Equal(8888, running.SteamProcessId);
        Assert.Equal(200u, running.ActiveAccountId);
        Assert.DoesNotContain(running.Accounts, a => a.AccountId == 0u);
        Assert.Equal(2, running.Accounts.Count);
        Assert.Equal(200u, running.Accounts[0].AccountId);
        Assert.True(running.Accounts[0].IsActiveProcessUser);
        Assert.Equal(Path.Combine(steamDir, "userdata", "200", "config", "shortcuts.vdf"), running.Accounts[0].ShortcutsVdfPath);
        Assert.Equal("Alt Persona (alt_acc · 200)", running.Accounts[0].DisplayLabel);

        // 2. preferredAccountId overrides ActiveUser cleanly
        var preferred = locator.Discover(preferredAccountId: 100u);
        Assert.Equal(100u, preferred.ActiveAccountId);
        Assert.Null(preferred.WarningMessage);

        // 3. Unknown preferredAccountId warns and falls back to ActiveUser (200)
        var unknownPreferred = locator.Discover(preferredAccountId: 999999u);
        Assert.Equal(200u, unknownPreferred.ActiveAccountId);
        Assert.False(string.IsNullOrWhiteSpace(unknownPreferred.WarningMessage));

        // 4. Steam closed -> ignores stale ActiveUser registry DWORD and uses MostRecent (100)
        probe.SteamRunning = false;
        probe.LiveSteamPid = null;
        var stopped = locator.Discover();
        Assert.False(stopped.IsSteamRunning);
        Assert.Null(stopped.SteamProcessId);
        Assert.Equal(100u, stopped.ActiveAccountId);
        Assert.All(stopped.Accounts, a => Assert.False(a.IsActiveProcessUser));

        // 5. userdata-only fallback (no loginusers.vdf) excludes 0 and orders by last-write descending
        string userdataOnlySteam = @"D:\SteamUserdataOnly";
        var userdataProbe = new FakeSteamEnvironmentProbe()
            .AddSteamInstall(userdataOnlySteam)
            .AddDirectory(Path.Combine(userdataOnlySteam, "userdata"))
            .AddDirectory(Path.Combine(userdataOnlySteam, "userdata", "0"), new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero))
            .AddDirectory(Path.Combine(userdataOnlySteam, "userdata", "ac"))
            .AddDirectory(Path.Combine(userdataOnlySteam, "userdata", "5001"), new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero))
            .AddDirectory(Path.Combine(userdataOnlySteam, "userdata", "5002"), new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero))
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", userdataOnlySteam);

        var userdataResult = new SteamClientLocator(userdataProbe).Discover();
        Assert.True(userdataResult.IsDetected);
        Assert.Equal(2, userdataResult.Accounts.Count);
        Assert.Equal(5002u, userdataResult.ActiveAccountId);
        Assert.Equal(5002u, userdataResult.Accounts[0].AccountId);
        Assert.Equal("Steam 账号 5002", userdataResult.Accounts[0].DisplayLabel);
        Assert.Equal(5001u, userdataResult.Accounts[1].AccountId);
    }

    [Fact]
    public async Task SteamClientSoundLocator_DelegatesToSteamClientLocator_ForOverrideExeAndMissingSteam()
    {
        await using var sandbox = new TestSandbox();
        string fakeSteamDir = sandbox.CreateSubDirectory("delegated-steam");
        string fakeSteamExe = Path.Combine(fakeSteamDir, "steam.exe");
        await File.WriteAllBytesAsync(fakeSteamExe, [0x4D, 0x5A]);

        string soundsDir = Directory.CreateDirectory(Path.Combine(fakeSteamDir, "steamui", "sounds")).FullName;
        string desktopWav = Path.Combine(soundsDir, "desktop_toast_default.wav");
        await File.WriteAllBytesAsync(desktopWav, [0x52, 0x49, 0x46, 0x46]);

        // Passing direct steam.exe path normalizes to parent install dir via SteamClientLocator delegation
        SteamClientSoundProbeResult fromExe = SteamClientSoundLocator.Probe(fakeSteamExe);
        Assert.True(fromExe.IsSteamInstalled);
        Assert.Equal(Path.GetFullPath(fakeSteamDir), fromExe.SteamInstallDirectory);
        Assert.Equal(Path.GetFullPath(desktopWav), fromExe.AchievementToastWavPath);
        Assert.True(fromExe.HasAuthenticAchievementSound);

        // Non-existent override isolates from host Steam and reports not installed
        string missingSteamDir = Path.Combine(sandbox.RootDirectory, "definitely-missing-steam");
        Assert.Empty(SteamClientSoundLocator.EnumerateCandidateSteamDirectories(missingSteamDir));

        SteamClientSoundProbeResult missing = SteamClientSoundLocator.Probe(missingSteamDir);
        Assert.False(missing.IsSteamInstalled);
        Assert.Null(missing.SteamInstallDirectory);
        Assert.Null(missing.EffectiveSoundPath);
        Assert.False(missing.HasAuthenticAchievementSound);
    }
}
