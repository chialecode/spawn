using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Metadata parsed from a Unity game's <c>*_Data\app.info</c> file.
/// </summary>
public sealed record UnityAppInfo(
    string DataDirectoryPath,
    string CompanyName,
    string ProductName);

/// <summary>
/// Detected offline Steam emulator flavor and resolved <c>SteamAppId</c> inside a game directory.
/// </summary>
public sealed record DetectedSteamEmulatorInfo(
    string EmulatorFlavor,
    string ChannelLabel,
    int? SteamAppId,
    string? ConfigFilePath,
    string? LocalStorageCustomPath);

/// <summary>
/// Inspects Unity (<c>*_Data\app.info</c>), Unreal Engine (<c>Engine\</c> + <c>Binaries\</c>),
/// and nested Steam emulator plugins (<c>*_Data\Plugins\x86_64\steam_api64.cdx|.rne</c>, <c>steam_emu.ini</c>, etc.).
/// </summary>
public static partial class GameEngineInspector
{
    private static readonly HashSet<string> SkippedAssetDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        ".local",
        "$RECYCLE.BIN",
        "System Volume Information",
        "_CommonRedist",
        "CommonRedist",
        "Redist",
        "Redistributables",
        "DirectX",
        "__Installer",
        "Support",
        "MonoBleedingEdge",
        "SPAWN_Vault",
        "_OST",
        "OST",
        "Soundtrack",
        "StreamingAssets",
        "usd",
        "locales",
        "ShaderCache",
        "Shaders",
        "DerivedDataCache",
        "Paks",
        "Movies",
        "Videos",
        "CrashReportClient",
    };

    private static readonly HashSet<string> GenericUnityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DefaultCompany",
        "New Unity Project",
        "UnityProject",
        "Unity",
        "ProductName",
        "CompanyName",
    };

    private static readonly HashSet<string> GenericUnrealProjectNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gameface",
        "UE4Game",
        "UE5Game",
        "UnrealGame",
        "UnrealEngine",
        "Engine",
        "Game",
        "Project",
        "WindowsNoEditor",
        "Template",
    };

    private static readonly string[] EmulatorConfigNames =
    [
        "OnlineFix.ini",
        "steam_emu.ini",
        "tenoke.ini",
        "codex.ini",
        "rune.ini",
        "flt.ini",
        "ColdClientLoader.ini",
        "ALI213.ini",
        "Valve.ini",
        "3dmgame.ini",
        "hlm.ini",
        "skidrow.ini",
        "SmartSteamEmu.ini",
        "configs.main.ini",
        "configs.app.ini",
        "configs.user.ini",
    ];

    [GeneratedRegex(@"^\s*RealAppId\s*=\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IniRealAppIdRegex();

    [GeneratedRegex(@"^\s*(?:AppId|app_id|GameId|id)\s*=\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IniGenericAppIdRegex();

    [GeneratedRegex(@"^\s*LocalStorage\s*=\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IniLocalStorageRegex();

    public static bool ShouldSkipAssetDirectory(string directoryName) =>
        SkippedAssetDirectoryNames.Contains(directoryName);

    /// <summary>
    /// Reads Unity <c>*_Data\app.info</c> (line 1 = <c>CompanyName</c>, line 2 = <c>ProductName</c>).
    /// </summary>
    public static UnityAppInfo? TryReadUnityAppInfo(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };

            foreach (string subDir in Directory.EnumerateDirectories(installDir, "*_Data", opts))
            {
                string appInfoPath = Path.Combine(subDir, "app.info");
                if (!File.Exists(appInfoPath))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(appInfoPath, Encoding.UTF8);
                if (lines.Length < 2)
                {
                    continue;
                }

                string company = lines[0].Trim();
                string product = lines[1].Trim();
                if (!string.IsNullOrWhiteSpace(product))
                {
                    return new UnityAppInfo(subDir, company, product);
                }
            }
        }
        catch
        {
            // Ignore unreadable app.info.
        }

        return null;
    }

    public static bool IsUsefulUnityProductName(string? productName) =>
        !string.IsNullOrWhiteSpace(productName)
        && productName.Trim().Length >= 2
        && !GenericUnityNames.Contains(productName.Trim());

    /// <summary>
    /// Converts invalid NTFS filename characters (such as <c>:</c> in <c>The Stanley Parable: Ultra Deluxe</c>)
    /// to <c>_</c> matching Unity's Windows <c>Application.persistentDataPath</c> (<c>LocalLow</c>) behavior.
    /// </summary>
    public static string SanitizeUnityPathSegment(string rawSegment)
    {
        if (string.IsNullOrWhiteSpace(rawSegment))
        {
            return string.Empty;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(rawSegment.Length);
        foreach (char ch in rawSegment.Trim())
        {
            if (ch == ':' || Array.IndexOf(invalidChars, ch) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().TrimEnd('.', ' ');
    }

    /// <summary>
    /// Detects Unreal Engine project root folder name (e.g. <c>&lt;InstallDir&gt;\Engine</c> + <c>&lt;ProjectName&gt;\Content\Paks</c>).
    /// </summary>
    public static string? TryDetectUnrealProjectName(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        if (!Directory.Exists(Path.Combine(installDir, "Engine")))
        {
            return null;
        }

        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(installDir))
            {
                string name = Path.GetFileName(subDir);
                if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase) || ShouldSkipAssetDirectory(name))
                {
                    continue;
                }

                if (Directory.Exists(Path.Combine(subDir, "Content"))
                    || Directory.Exists(Path.Combine(subDir, "Binaries")))
                {
                    return name;
                }
            }
        }
        catch
        {
            // Ignore IO errors.
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="projectName"/> is a generic shared Unreal internal project codename
    /// (such as <c>Gameface</c>, <c>UE4Game</c>, <c>UE5Game</c>, or <c>UnrealGame</c>) rather than a single-game identifier.
    /// </summary>
    public static bool IsGenericUnrealProjectName(string? projectName) =>
        string.IsNullOrWhiteSpace(projectName)
        || projectName.Trim().Length < 2
        || GenericUnrealProjectNames.Contains(projectName.Trim());

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="projectName"/> is a non-generic game-specific Unreal project name.
    /// </summary>
    public static bool IsUsefulUnrealProjectName(string? projectName) =>
        !string.IsNullOrWhiteSpace(projectName) && !IsGenericUnrealProjectName(projectName);

    /// <summary>
    /// Statically checks whether <paramref name="installDir"/> contains Rockstar Games Launcher / Social Club markers
    /// (<c>title.rgl</c>, <c>socialclub.dll</c>, <c>Deftones.dll</c>, <c>PlayGTA*.exe</c>).
    /// </summary>
    public static bool HasRockstarOrSocialClubFiles(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return false;
        }

        if (File.Exists(Path.Combine(installDir, "title.rgl")))
        {
            return true;
        }

        foreach (string filePath in EnumerateMetadataFilesBounded(installDir, maxDepth: 4))
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName.Equals("socialclub.dll", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("socialclub64.dll", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("Deftones.dll", StringComparison.OrdinalIgnoreCase)
                || (fileName.StartsWith("PlayGTA", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates files up to <paramref name="maxDepth"/> while skipping heavy media/bundle directories
    /// (<c>_OST</c>, <c>StreamingAssets</c>, <c>usd</c>, <c>locales</c>, etc.), allowing an extra depth hop inside
    /// <c>Plugins</c> and <c>ThirdParty\Steamworks</c> folders.
    /// </summary>
    public static List<string> EnumerateMetadataFilesBounded(string installDir, int maxDepth = 4)
    {
        var results = new List<string>(96);
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return results;
        }

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        void Walk(string currentDir, int depth, int effectiveMaxDepth)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(currentDir, "*", opts));
                if (depth >= effectiveMaxDepth)
                {
                    return;
                }

                foreach (string sub in Directory.EnumerateDirectories(currentDir, "*", opts))
                {
                    string name = Path.GetFileName(sub);
                    if (ShouldSkipAssetDirectory(name))
                    {
                        continue;
                    }

                    // Skip numeric depot/manifest cache folders or deep asset bundles unless Plugins/Binaries/Engine/steam_settings.
                    int nextMax = effectiveMaxDepth;
                    if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Plugins", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Steamworks", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("steam_settings", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Steamv", StringComparison.OrdinalIgnoreCase))
                    {
                        nextMax = Math.Max(effectiveMaxDepth, 7);
                    }

                    Walk(sub, depth + 1, nextMax);
                }
            }
            catch
            {
                // Ignore inaccessible subdirectories.
            }
        }

        Walk(installDir, 0, maxDepth);
        return results;
    }

    /// <summary>
    /// Inspects all relevant files in <paramref name="installDir"/> (including Unity <c>*_Data\Plugins\x86_64\</c>
    /// and Unreal <c>Engine\Binaries\ThirdParty\Steamworks\**</c>) to identify Steam emulator flavor,
    /// <c>SteamAppId</c>, and <c>steam_api[64].dll</c> presence.
    /// </summary>
    public static (
        DetectedSteamEmulatorInfo? Emulator,
        int? DeterministicTxtAppId,
        bool HasSteamApiDll,
        bool HasGbeForkBackup) InspectSteamFiles(string installDir)
    {
        var files = EnumerateMetadataFilesBounded(installDir, maxDepth: 4);

        bool hasSteamApiDll = false;
        bool hasGbeForkBackup = false;
        bool hasCdxFile = false;
        bool hasRneFile = false;
        bool hasFltFile = false;
        bool hasSteamSettingsFolder = false;
        bool hasGbeForkConfig = false;
        bool hasSteamDataFolder = Directory.Exists(Path.Combine(installDir, "SteamData"));

        int? txtAppId = null;
        int? realIniAppId = null;
        int? genericIniAppId = null;
        int? spacewarFallbackId = null;
        string? primaryConfigPath = null;
        string? detectedIniFlavor = null;
        string? localStorageCustomPath = null;

        foreach (string filePath in files)
        {
            string fileName = Path.GetFileName(filePath);
            string? parentName = Path.GetFileName(Path.GetDirectoryName(filePath));

            if (fileName.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase))
            {
                hasSteamApiDll = true;
            }
            else if (fileName.EndsWith(".cdx", StringComparison.OrdinalIgnoreCase)
                     && fileName.StartsWith("steam_api", StringComparison.OrdinalIgnoreCase))
            {
                hasCdxFile = true;
            }
            else if (fileName.EndsWith(".rne", StringComparison.OrdinalIgnoreCase)
                     && fileName.StartsWith("steam_api", StringComparison.OrdinalIgnoreCase))
            {
                hasRneFile = true;
            }
            else if (fileName.Equals("flt.ini", StringComparison.OrdinalIgnoreCase))
            {
                hasFltFile = true;
            }
            else if (fileName.EndsWith(".spawn_vanilla_bak", StringComparison.OrdinalIgnoreCase)
                     || fileName.EndsWith(".spawn_orig_bak", StringComparison.OrdinalIgnoreCase))
            {
                hasGbeForkBackup = true;
            }

            if (string.Equals(parentName, "steam_settings", StringComparison.OrdinalIgnoreCase))
            {
                hasSteamSettingsFolder = true;
                if (fileName.StartsWith("configs.", StringComparison.OrdinalIgnoreCase))
                {
                    hasGbeForkConfig = true;
                }
            }

            if (fileName.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase))
            {
                int? parsed = TryParseSteamAppIdTxt(filePath);
                if (parsed is 480)
                {
                    spacewarFallbackId ??= 480;
                }
                else if (parsed is > 0)
                {
                    txtAppId ??= parsed;
                }
            }
            else if (EmulatorConfigNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                primaryConfigPath ??= filePath;
                var (realId, genId, iniFlavorHint, localStorage) = ParseEmulatorIniDetails(filePath, fileName);
                if (realId is > 0 and not 480)
                {
                    realIniAppId ??= realId;
                }

                if (genId is 480)
                {
                    spacewarFallbackId ??= 480;
                }
                else if (genId is > 0)
                {
                    genericIniAppId ??= genId;
                }

                detectedIniFlavor ??= iniFlavorHint;
                localStorageCustomPath ??= localStorage;
            }
        }

        int? resolvedAppId = realIniAppId
            ?? txtAppId
            ?? genericIniAppId
            ?? spacewarFallbackId;

        string? flavor = null;
        if (hasRneFile || string.Equals(detectedIniFlavor, "RUNE", StringComparison.OrdinalIgnoreCase))
        {
            flavor = "RUNE";
        }
        else if (hasCdxFile || string.Equals(detectedIniFlavor, "CODEX", StringComparison.OrdinalIgnoreCase))
        {
            flavor = "CODEX";
        }
        else if (detectedIniFlavor is not null)
        {
            flavor = detectedIniFlavor;
        }
        else if (hasGbeForkConfig)
        {
            flavor = "gbe_fork";
        }
        else if (hasSteamSettingsFolder)
        {
            flavor = "Goldberg";
        }
        else if (hasSteamDataFolder)
        {
            flavor = "TENOKE";
        }
        else if (hasFltFile)
        {
            flavor = "FLT";
        }

        DetectedSteamEmulatorInfo? emuInfo = null;
        if (flavor is not null && !string.Equals(flavor, "FLT", StringComparison.OrdinalIgnoreCase))
        {
            string label = flavor switch
            {
                "CODEX" => "CODEX 离线成就监听 · 就绪",
                "RUNE" => "RUNE 离线成就监听 · 就绪",
                "gbe_fork" => "Goldberg GSE · 就绪",
                "Goldberg" => "Goldberg 离线成就监听 · 就绪",
                "TENOKE" => "TENOKE 离线成就监听 · 就绪",
                "OnlineFix" => "Online-Fix 离线成就监听 · 就绪",
                "ALI213" => "ALI213 离线成就监听 · 就绪",
                "3DM" => "3DM 离线成就监听 · 就绪",
                "SSE" => "SmartSteamEmu 监听 · 就绪",
                _ => $"{flavor} 离线成就监听 · 就绪",
            };

            emuInfo = new DetectedSteamEmulatorInfo(
                EmulatorFlavor: flavor,
                ChannelLabel: label,
                SteamAppId: resolvedAppId,
                ConfigFilePath: primaryConfigPath,
                LocalStorageCustomPath: localStorageCustomPath);
        }

        return (emuInfo, resolvedAppId, hasSteamApiDll, hasGbeForkBackup);
    }

    private static int? TryParseSteamAppIdTxt(string filePath)
    {
        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                {
                    continue;
                }

                int len = 0;
                while (len < trimmed.Length && char.IsAsciiDigit(trimmed[len]))
                {
                    len++;
                }

                if (len > 0
                    && int.TryParse(trimmed.AsSpan(0, len), NumberStyles.None, CultureInfo.InvariantCulture, out int appId)
                    && appId > 0)
                {
                    return appId;
                }
            }
        }
        catch
        {
            // Ignore unreadable file.
        }

        return null;
    }

    private static (int? RealId, int? GenericId, string? FlavorHint, string? LocalStorage) ParseEmulatorIniDetails(
        string filePath,
        string fileName)
    {
        string? flavorHint = fileName.ToLowerInvariant() switch
        {
            "onlinefix.ini" => "OnlineFix",
            "tenoke.ini" => "TENOKE",
            "codex.ini" => "CODEX",
            "rune.ini" => "RUNE",
            "ali213.ini" or "valve.ini" => "ALI213",
            "3dmgame.ini" => "3DM",
            "smartsteamemu.ini" => "SSE",
            "coldclientloader.ini" or "configs.main.ini" or "configs.app.ini" or "configs.user.ini" => "gbe_fork",
            _ => null,
        };

        try
        {
            string content = File.ReadAllText(filePath);

            if (flavorHint is null && fileName.Equals("steam_emu.ini", StringComparison.OrdinalIgnoreCase))
            {
                if (content.Contains(@"\Steam\RUNE\", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("=-RUNE-", StringComparison.OrdinalIgnoreCase))
                {
                    flavorHint = "RUNE";
                }
                else if (content.Contains(@"\Steam\CODEX\", StringComparison.OrdinalIgnoreCase))
                {
                    flavorHint = "CODEX";
                }
                else
                {
                    flavorHint = "CODEX";
                }
            }

            int? realId = null;
            var realMatch = IniRealAppIdRegex().Match(content);
            if (realMatch.Success
                && int.TryParse(realMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedReal)
                && parsedReal > 0)
            {
                realId = parsedReal;
            }

            int? genericId = null;
            foreach (Match m in IniGenericAppIdRegex().Matches(content))
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedGen)
                    && parsedGen > 0)
                {
                    if (parsedGen != 480)
                    {
                        genericId = parsedGen;
                        break;
                    }

                    genericId ??= 480;
                }
            }

            string? localStorage = null;
            var lsMatch = IniLocalStorageRegex().Match(content);
            if (lsMatch.Success)
            {
                string rawLs = lsMatch.Groups[1].Value.Trim();
                if (!rawLs.StartsWith('#') && !rawLs.StartsWith(';') && rawLs.Length > 0)
                {
                    localStorage = rawLs;
                }
            }

            return (realId, genericId, flavorHint, localStorage);
        }
        catch
        {
            return (null, null, flavorHint, null);
        }
    }
}
