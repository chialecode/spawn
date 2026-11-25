using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Classification of an executable's PE version-resource role during heuristic discovery.
/// </summary>
public enum PeExecutableRole
{
    Unknown = 0,
    GameBinary = 1,
    LauncherOrMenuWrapper = 2,
    RedirectorOrPrerequisiteStub = 3,
}

/// <summary>
/// Multi-stage game title denoiser, PE <see cref="FileVersionInfo"/> product name extractor,
/// DLsite code detector, and Jaro-Winkler similarity scorer for offline game directories.
/// </summary>
public static partial class GameTitleCleaner
{
    private static readonly HashSet<string> GenericEnginePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application",
        "BootstrapPackagedGame",
        "CompactAutoRunner",
        "RockstarRedirector",
        "Rockstar Games Launcher",
        "Rockstar Games Launcher Redirector",
        "UEPrereqSetup",
        "UE4PrereqSetup",
        "UnityPlayer",
        "Unity Player",
        "UnrealGame",
        "UE4Game",
        "UE5Game",
        "WindowsApplication",
        "Windows Application",
        "Game",
        "Launcher",
        "Launch",
        "Main",
        "Client",
        "nwjs",
        "Electron",
        "RPG Maker MV",
        "RPG Maker MZ",
        "GameMaker",
        "Godot Engine",
        "TODO: <Product name>",
    };

    private static readonly Dictionary<string, string> RomanToArabicInstallments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["II"] = "2",
        ["III"] = "3",
        ["IV"] = "4",
        ["V"] = "5",
        ["VI"] = "6",
        ["VII"] = "7",
        ["VIII"] = "8",
        ["IX"] = "9",
        ["X"] = "10",
        ["XI"] = "11",
        ["XII"] = "12",
        ["XIII"] = "13",
        ["XIV"] = "14",
        ["XV"] = "15",
        ["XVI"] = "16",
    };

    [GeneratedRegex(@"\b([RVB]J\d{6,8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DlsiteCodeRegex();

    [GeneratedRegex(@"^(?:\s*(?:\[[^\]]*\]|【[^】]*】|\(\d{4}[\.\-_]?\d{2}[\.\-_]?\d{2}\)|\(\d{6,8}\))\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingBracketNoiseRegex();

    [GeneratedRegex(@"(?:\s*(?:\[[^\]]*\]|【[^】]*】)\s*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingBracketNoiseRegex();

    [GeneratedRegex(
        @"(?i)(?:[\s\._\-]+|^)(?:v?\d+(?:\.\d+){1,4}[a-z]?|ver\.?\s*\d+(?:\.\d+)*|build[\s\._\-]*\d+|r\d{4,}|update[\s\._\-]*\d+|hotfix[\s\._\-]*\d+)\b.*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionTailRegex();

    [GeneratedRegex(
        @"(?i)(?:[\s\._\-–—:]+)(?:steamrip(?:\.com)?|fitgirl(?:[\s\._\-]*repacks?)?|dodi(?:[\s\._\-]*repacks?)?|elamigos|tenoke|rune|codex|plaza|flt|fairlight|empress|goldberg|skidrow|razor1911|hoodlum|ali213|3dm(?:game)?|gog(?:\.com)?|repack|portable|pre[\s\._\-]*installed|(?:the[\s\._\-]+)?goty|(?:the[\s\._\-]+)?game[\s\._\-]+of[\s\._\-]+the[\s\._\-]+year(?:[\s\._\-]+edition)?|(?:the[\s\._\-]+)?deluxe[\s\._\-]+edition|(?:the[\s\._\-]+)?ultimate[\s\._\-]+edition|(?:the[\s\._\-]+)?complete[\s\._\-]+edition|免安装.*|绿色版.*|官中.*|简体中文.*|繁体中文.*|汉化.*|硬盘版.*|整合版.*|学习版.*|全DLC.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseGroupAndBundleTailRegex();

    [GeneratedRegex(
        @"(?i)(?:[\s\._\-–—:]+)(?:the[\s\._\-]+)?(?:definitive[\s\._\-]+edition|anniversary[\s\._\-]+edition)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RemasterEditionTailRegex();

    [GeneratedRegex(
        @"(?:[\s\._]*[-–—][\s\._]*)(?:(?:[Tt]he)[\s\._\-]+)?DE$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ShorthandDeTailRegex();

    [GeneratedRegex(
        @"^GTA(?=\s*(?:[-:–—]|\b(?:III|II|IV|V|VI|1|2|3|4|5|6|Vice\s+City|San\s+Andreas|Liberty\s+City|The\s+Trilogy|Trilogy|Episodes|Chinatown|Online)\b|$))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingGtaAbbreviationRegex();

    [GeneratedRegex(
        @"^RDR(?:(?=\s*(?:[-:–—]|\b(?:II|1|2|Online|Undead\s+Nightmare|Remastered|Definitive)\b))|(?=\d+\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingRdrAbbreviationRegex();

    [GeneratedRegex(@"\b([A-Za-z0-9]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AlphanumericWordTokenRegex();

    [GeneratedRegex(@"\b(?:trilogy|anthology)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnthologyKeywordRegex();

    [GeneratedRegex(@"\b(?:trilogy|anthology|collection|bundle)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectionKeywordRegex();

    [GeneratedRegex(@"\b(?:definitive(?:\s+edition)?|remaster(?:ed)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemasterMarkerRegex();

    [GeneratedRegex(@"\((?:[^)]*(?:SteamRIP|Repack|Portable|GOG|x64|x86|Win64|Win32|免安装|官中|汉化|硬盘版|整合版|Build\s*\d+)[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedNoiseRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^(?:The|A|An)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingArticleRegex();

    [GeneratedRegex(@"^v?(?:20\d{2}\.\d+(?:\.\d+)*(?:[abfp]\d+)?|6000\.\d+(?:\.\d+)*(?:[abfp]\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnityEngineVersionRegex();

    [GeneratedRegex(@"^v?(?:4\.27\.\d+(?:\.\d+)*|5\.\d+\.\d+(?:\.\d+)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnrealEngineVersionRegex();

    [GeneratedRegex(@"^v?0+(?:\.0+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllZeroVersionRegex();

    [GeneratedRegex(@"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF]", RegexOptions.CultureInvariant)]
    private static partial Regex CjkCharacterRegex();

    [GeneratedRegex(@"[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex LatinLetterRegex();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="versionText"/> is an engine runtime version noise
    /// string (Unity <c>20xx.x.*</c> / <c>6000.x.*</c> or Unreal <c>4.27.*</c> / <c>5.x.*</c>) or an empty/zero placeholder.
    /// </summary>
    public static bool IsEngineVersionNoise(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return true;
        }

        string normalized = NormalizeVersionToken(versionText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        return AllZeroVersionRegex().IsMatch(normalized)
            || UnityEngineVersionRegex().IsMatch(normalized)
            || UnrealEngineVersionRegex().IsMatch(normalized);
    }

    /// <summary>
    /// Extracts a non-noise PE <c>ProductVersion</c>/<c>FileVersion</c> string from <paramref name="exeFullPath"/>,
    /// filtering Unity (<c>20xx.x.*</c>) and Unreal (<c>4.27.*</c>/<c>5.x.*</c>) engine runtime version noise.
    /// </summary>
    public static string? TryGetUsefulPeGameVersion(string? exeFullPath)
    {
        if (string.IsNullOrWhiteSpace(exeFullPath) || !File.Exists(exeFullPath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exeFullPath);
            foreach (string? raw in new[] { info.ProductVersion, info.FileVersion })
            {
                string normalized = NormalizeVersionToken(raw);
                if (!string.IsNullOrWhiteSpace(normalized) && !IsEngineVersionNoise(normalized))
                {
                    return normalized;
                }
            }
        }
        catch
        {
            // Ignore unreadable or malformed PE version blocks.
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="text"/> contains CJK ideographs, Kana, or Hangul characters.
    /// </summary>
    public static bool ContainsCjkCharacters(string? text) =>
        !string.IsNullOrWhiteSpace(text) && CjkCharacterRegex().IsMatch(text);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="title"/> is suitable as a canonical English search title
    /// (contains Latin letters and does not contain CJK characters).
    /// </summary>
    public static bool IsLikelyEnglishTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && LatinLetterRegex().IsMatch(title)
        && !CjkCharacterRegex().IsMatch(title);

    /// <summary>
    /// Extracts and cleans a canonical English title candidate from a raw folder name, executable stem, or bilingual display title.
    /// Returns <see langword="null"/> if no usable English title remains after denoising.
    /// </summary>
    public static string? CleanEnglishTitle(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        // Handle bilingual display strings like "黑神话：悟空 / Black Myth: Wukong" or "艾尔登法环 | ELDEN RING".
        string working = rawName.Trim();
        string[] bilingualParts = working.Split(['/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bilingualParts.Length > 1)
        {
            foreach (string part in bilingualParts)
            {
                string cleanedPart = CleanTitle(part, preserveRemasterEdition: true);
                if (IsLikelyEnglishTitle(cleanedPart) && !string.Equals(cleanedPart, "Unknown Game", StringComparison.OrdinalIgnoreCase))
                {
                    return cleanedPart;
                }
            }
        }

        string cleaned = CleanTitle(working, preserveRemasterEdition: true);
        if (string.Equals(cleaned, "Unknown Game", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IsLikelyEnglishTitle(cleaned))
        {
            return cleaned;
        }

        // Strip residual CJK runs (e.g., "蔚蓝 Celeste" -> "Celeste") while preserving English words/digits.
        string strippedCjk = CjkCharacterRegex().Replace(cleaned, " ");
        strippedCjk = MultiSpaceRegex().Replace(strippedCjk, " ").Trim(' ', '-', '_', '.', ':', '：', '–', '—', '(', ')', '[', ']', '【', '】');
        return IsLikelyEnglishTitle(strippedCjk) && strippedCjk.Length >= 2
            ? strippedCjk
            : null;
    }

    private static string NormalizeVersionToken(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return string.Empty;
        }

        string trimmed = rawVersion.Trim();
        int spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx > 0 && !trimmed.Contains(','))
        {
            trimmed = trimmed[..spaceIdx];
        }

        // Convert comma-formatted Win32 FILEVERSION strings ("1, 0, 2, 0") to dotted form.
        if (trimmed.Contains(','))
        {
            string[] parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            trimmed = string.Join('.', parts);
        }

        return trimmed.Trim();
    }

    /// <summary>
    /// Extracts a DLsite work code (e.g. <c>RJ01123456</c>, <c>RJ398765</c>) if present in the path or folder name.
    /// </summary>
    public static string? TryExtractDlsiteCode(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = DlsiteCodeRegex().Match(rawText);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Cleans a dirty game directory or executable base name by stripping scene release tags,
    /// version numbers, bracketed localization/repack suffixes, and dot/underscore separators.
    /// </summary>
    public static string CleanTitle(string? rawName) =>
        CleanTitle(rawName, preserveRemasterEdition: false);

    /// <summary>
    /// Cleans a dirty game directory or executable base name, optionally preserving distinct remaster
    /// subtitles (<c>Definitive Edition</c>, <c>Anniversary Edition</c>) and expanding shorthand <c>- DE</c>
    /// so remasters are not conflated with original releases.
    /// </summary>
    public static string CleanTitle(string? rawName, bool preserveRemasterEdition)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "Unknown Game";
        }

        string original = rawName.Trim();
        string working = original;

        // Strip .exe extension if passed directly.
        if (working.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            working = working[..^4];
        }

        // Strip Unreal shipping suffix if cleaning an executable stem.
        int shippingIdx = working.IndexOf("-Win64-Shipping", StringComparison.OrdinalIgnoreCase);
        if (shippingIdx > 0)
        {
            working = working[..shippingIdx];
        }

        shippingIdx = working.IndexOf("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase);
        if (shippingIdx > 0)
        {
            working = working[..shippingIdx];
        }

        // Remove parenthesized noise tags first.
        working = ParenthesizedNoiseRegex().Replace(working, " ");

        // Remove leading and trailing bracket blocks, unless removing them leaves nothing.
        string strippedLeading = LeadingBracketNoiseRegex().Replace(working, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(strippedLeading))
        {
            working = strippedLeading;
        }

        string strippedTrailing = TrailingBracketNoiseRegex().Replace(working, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(strippedTrailing))
        {
            working = strippedTrailing;
        }

        // Normalize dot/underscore scene naming before or after tail stripping:
        // first strip scene/version tails while dots/underscores are still intact.
        for (int pass = 0; pass < 3; pass++)
        {
            string before = working;
            working = ReleaseGroupAndBundleTailRegex().Replace(working, string.Empty).Trim();
            if (!preserveRemasterEdition)
            {
                working = RemasterEditionTailRegex().Replace(working, string.Empty).Trim();
                working = ShorthandDeTailRegex().Replace(working, string.Empty).Trim();
            }

            working = VersionTailRegex().Replace(working, string.Empty).Trim();
            working = TrailingBracketNoiseRegex().Replace(working, string.Empty).Trim();
            if (string.Equals(before, working, StringComparison.Ordinal))
            {
                break;
            }
        }

        // Convert scene-style '.' or '_' separators to spaces when appropriate.
        bool hasNaturalSpaces = working.Contains(' ');
        if (!hasNaturalSpaces && (working.Contains('.') || working.Contains('_')))
        {
            working = working.Replace('.', ' ').Replace('_', ' ');
        }
        else if (working.Contains('_'))
        {
            working = working.Replace('_', ' ');
        }

        // Run tail cleanup once more after separator normalization.
        working = ReleaseGroupAndBundleTailRegex().Replace(working, string.Empty);
        if (preserveRemasterEdition)
        {
            if (ShorthandDeTailRegex().IsMatch(working))
            {
                working = ShorthandDeTailRegex().Replace(working, " - Definitive Edition");
            }
        }
        else
        {
            working = RemasterEditionTailRegex().Replace(working, string.Empty);
            working = ShorthandDeTailRegex().Replace(working, string.Empty);
        }

        working = VersionTailRegex().Replace(working, string.Empty);
        working = MultiSpaceRegex().Replace(working, " ").Trim(' ', '-', '_', '.', '–', '—');

        if (string.IsNullOrWhiteSpace(working))
        {
            // Fallback: strip brackets characters only from original.
            working = original.Trim('[', ']', '【', '】', '(', ')', ' ', '.', '_', '-');
        }

        return string.IsNullOrWhiteSpace(working) ? "Unknown Game" : working;
    }

    /// <summary>
    /// Expands well-known series acronyms (such as leading <c>GTA</c> -&gt; <c>Grand Theft Auto</c>
    /// and <c>RDR</c> -&gt; <c>Red Dead Redemption</c>) for store and metadata search candidates
    /// without altering non-matching titles.
    /// </summary>
    public static string ExpandSeriesAbbreviations(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string trimmed = title.Trim();
        if (LeadingGtaAbbreviationRegex().IsMatch(trimmed))
        {
            return LeadingGtaAbbreviationRegex().Replace(trimmed, "Grand Theft Auto");
        }

        if (LeadingRdrAbbreviationRegex().IsMatch(trimmed))
        {
            string rest = trimmed[3..];
            return rest.Length > 0 && char.IsLetterOrDigit(rest[0])
                ? "Red Dead Redemption " + rest
                : "Red Dead Redemption" + rest;
        }

        return trimmed;
    }

    /// <summary>
    /// Classifies PE version metadata strings into redirector/prerequisite stubs, menu/launcher wrappers, or valid game binaries.
    /// </summary>
    public static PeExecutableRole ClassifyPeVersionMetadata(
        string? originalFilename,
        string? internalName,
        string? productName,
        string? fileDescription)
    {
        foreach (string? raw in new[] { originalFilename, internalName, productName, fileDescription })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string text = raw.Trim();
            if (text.Contains("Redirector", StringComparison.OrdinalIgnoreCase)
                || text.Contains("BootstrapPackagedGame", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Bootstrapper", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Prerequisite", StringComparison.OrdinalIgnoreCase)
                || text.Contains("PrereqSetup", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Social-Club-Setup", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SocialClubSetup", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Uninstaller", StringComparison.OrdinalIgnoreCase)
                || text.Contains("CrashReportClient", StringComparison.OrdinalIgnoreCase)
                || text.Contains("UnrealCEFSubProcess", StringComparison.OrdinalIgnoreCase)
                || text.Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Installer", StringComparison.OrdinalIgnoreCase))
            {
                return PeExecutableRole.RedirectorOrPrerequisiteStub;
            }
        }

        foreach (string? raw in new[] { originalFilename, internalName, productName, fileDescription })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string text = raw.Trim();
            if (text.Contains("CompactAutoRunner", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Customizable CD Menu", StringComparison.OrdinalIgnoreCase)
                || text.Contains("AutoRunner", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                return PeExecutableRole.LauncherOrMenuWrapper;
            }
        }

        foreach (string? candidate in new[] { productName, fileDescription })
        {
            if (TryNormalizeValidPeTitleCandidate(candidate) is not null)
            {
                return PeExecutableRole.GameBinary;
            }
        }

        return PeExecutableRole.Unknown;
    }

    /// <summary>
    /// Inspects PE version resources of <paramref name="exeFullPath"/> to determine whether it is a redirector/prerequisite stub,
    /// a menu/launcher wrapper, or a game binary.
    /// </summary>
    public static PeExecutableRole InspectPeExecutableRole(string? exeFullPath)
    {
        if (string.IsNullOrWhiteSpace(exeFullPath) || !File.Exists(exeFullPath))
        {
            return PeExecutableRole.Unknown;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exeFullPath);
            return ClassifyPeVersionMetadata(
                info.OriginalFilename,
                info.InternalName,
                info.ProductName,
                info.FileDescription);
        }
        catch
        {
            return PeExecutableRole.Unknown;
        }
    }

    /// <summary>
    /// Reads PE <see cref="FileVersionInfo.ProductName"/> / <see cref="FileVersionInfo.FileDescription"/>
    /// while filtering generic Unity/Unreal/compiler placeholder strings, redirector stubs, and menu launchers.
    /// </summary>
    public static string? TryGetCleanPeProductName(string? exeFullPath)
    {
        if (string.IsNullOrWhiteSpace(exeFullPath) || !File.Exists(exeFullPath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exeFullPath);
            var role = ClassifyPeVersionMetadata(
                info.OriginalFilename,
                info.InternalName,
                info.ProductName,
                info.FileDescription);

            if (role is PeExecutableRole.RedirectorOrPrerequisiteStub or PeExecutableRole.LauncherOrMenuWrapper)
            {
                return null;
            }

            foreach (string? candidate in new[] { info.ProductName, info.FileDescription })
            {
                string? cleaned = TryNormalizeValidPeTitleCandidate(candidate);
                if (cleaned is not null)
                {
                    return cleaned;
                }
            }
        }
        catch
        {
            // Ignore malformed PE version resource blocks.
        }

        return null;
    }

    private static string? TryNormalizeValidPeTitleCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string trimmed = candidate.Trim();
        if (trimmed.Length < 2 || GenericEnginePlaceholders.Contains(trimmed))
        {
            return null;
        }

        if (trimmed.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("UnrealEngine", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Redirector", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Bootstrapper", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Prerequisite", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("CompactAutoRunner", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Customizable CD Menu", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("AutoRunner", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Setup", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Installer", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Crash", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string cleaned = CleanTitle(trimmed, preserveRemasterEdition: true);
        if (!string.IsNullOrWhiteSpace(cleaned)
            && !string.Equals(cleaned, "Unknown Game", StringComparison.OrdinalIgnoreCase)
            && !GenericEnginePlaceholders.Contains(cleaned))
        {
            return cleaned;
        }

        return null;
    }

    /// <summary>
    /// Builds an ordered, deduplicated list of search title candidates from PE metadata, folder name,
    /// remaster edition disambiguation, and series abbreviation expansion.
    /// </summary>
    public static IReadOnlyList<string> BuildSearchCandidates(string installDir, string? mainExeFullPath)
    {
        var candidates = new List<string>(6);

        static void AddUnique(List<string> list, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !string.Equals(value, "Unknown Game", StringComparison.OrdinalIgnoreCase)
                && !GenericEnginePlaceholders.Contains(value)
                && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(value);
            }
        }

        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(installDir));
        string folderPreserved = CleanTitle(folderName, preserveRemasterEdition: true);
        string folderExpandedPreserved = ExpandSeriesAbbreviations(folderPreserved);
        bool folderWasAbbreviated = !string.Equals(folderPreserved, folderExpandedPreserved, StringComparison.OrdinalIgnoreCase);

        string? peProduct = TryGetCleanPeProductName(mainExeFullPath);
        string peExpanded = ExpandSeriesAbbreviations(peProduct);

        if (!string.IsNullOrWhiteSpace(peProduct) && (folderWasAbbreviated || folderPreserved.Length <= 2))
        {
            AddUnique(candidates, peExpanded);
            AddUnique(candidates, peProduct);
        }

        if (folderWasAbbreviated)
        {
            AddUnique(candidates, folderExpandedPreserved);
        }

        AddUnique(candidates, folderPreserved);
        AddUnique(candidates, peExpanded);
        AddUnique(candidates, peProduct);

        string folderBase = CleanTitle(folderName, preserveRemasterEdition: false);
        string folderExpandedBase = ExpandSeriesAbbreviations(folderBase);
        if (!string.Equals(folderBase, folderPreserved, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(folderBase, folderExpandedBase, StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(candidates, folderExpandedBase);
            }

            AddUnique(candidates, folderBase);
        }

        if (!string.IsNullOrWhiteSpace(mainExeFullPath))
        {
            string exeStem = CleanTitle(Path.GetFileNameWithoutExtension(mainExeFullPath), preserveRemasterEdition: true);
            if (exeStem.Length >= 3)
            {
                AddUnique(candidates, ExpandSeriesAbbreviations(exeStem));
                AddUnique(candidates, exeStem);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="query"/> and <paramref name="candidate"/>
    /// disagree on sequel/installment numbering (e.g. <c>III</c> vs <c>V</c>, or no number vs <c>V</c>)
    /// or conflate a multi-game trilogy/anthology with a single title.
    /// </summary>
    public static bool HasConflictingSequelOrAnthologyTokens(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        bool queryIsAnthology = AnthologyKeywordRegex().IsMatch(query);
        bool candidateIsCollection = CollectionKeywordRegex().IsMatch(candidate);
        if (queryIsAnthology && !candidateIsCollection)
        {
            return true;
        }

        bool candidateIsAnthology = AnthologyKeywordRegex().IsMatch(candidate);
        bool queryIsCollection = CollectionKeywordRegex().IsMatch(query);
        if (candidateIsAnthology && !queryIsCollection)
        {
            return true;
        }

        var leftInstallments = ExtractNormalizedInstallmentTokens(query);
        var rightInstallments = ExtractNormalizedInstallmentTokens(candidate);
        return !leftInstallments.SetEquals(rightInstallments);
    }

    /// <summary>
    /// Returns <see langword="true"/> when one title explicitly designates a remaster/definitive edition
    /// and the other does not.
    /// </summary>
    public static bool HasRemasterEditionMismatch(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string normalizedQuery = CleanTitle(query, preserveRemasterEdition: true);
        string normalizedCandidate = CleanTitle(candidate, preserveRemasterEdition: true);
        return RemasterMarkerRegex().IsMatch(normalizedQuery) != RemasterMarkerRegex().IsMatch(normalizedCandidate);
    }

    private static HashSet<string> ExtractNormalizedInstallmentTokens(string text)
    {
        string cleaned = CleanTitle(text, preserveRemasterEdition: true);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in AlphanumericWordTokenRegex().Matches(cleaned))
        {
            string token = m.Groups[1].Value;
            if (RomanToArabicInstallments.TryGetValue(token, out string? mapped))
            {
                set.Add(mapped);
            }
            else if (token.All(char.IsAsciiDigit))
            {
                string trimmedDigits = token.TrimStart('0');
                set.Add(trimmedDigits.Length == 0 ? "0" : trimmedDigits);
            }
        }

        return set;
    }

    /// <summary>
    /// Generates a sort-friendly title by stripping leading English articles (<c>The</c>, <c>A</c>, <c>An</c>).
    /// </summary>
    public static string BuildSortTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string trimmed = title.Trim();
        string stripped = LeadingArticleRegex().Replace(trimmed, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? trimmed : stripped;
    }

    /// <summary>
    /// Computes normalized case-insensitive Jaro-Winkler similarity in <c>[0.0, 1.0]</c>.
    /// </summary>
    public static double ComputeJaroWinklerSimilarity(string? left, string? right)
    {
        string s1 = NormalizeForComparison(left);
        string s2 = NormalizeForComparison(right);

        if (s1.Length == 0 && s2.Length == 0)
        {
            return 1.0;
        }

        if (s1.Length == 0 || s2.Length == 0)
        {
            return 0.0;
        }

        if (string.Equals(s1, s2, StringComparison.Ordinal))
        {
            return 1.0;
        }

        int matchDistance = Math.Max(Math.Max(s1.Length, s2.Length) / 2 - 1, 0);
        bool[] s1Matches = new bool[s1.Length];
        bool[] s2Matches = new bool[s2.Length];

        int matches = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            int start = Math.Max(0, i - matchDistance);
            int end = Math.Min(i + matchDistance + 1, s2.Length);

            for (int j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j])
                {
                    continue;
                }

                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0.0;
        }

        double transpositions = 0;
        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i])
            {
                continue;
            }

            while (!s2Matches[k])
            {
                k++;
            }

            if (s1[i] != s2[k])
            {
                transpositions++;
            }

            k++;
        }

        double m = matches;
        double jaro = ((m / s1.Length) + (m / s2.Length) + ((m - (transpositions / 2.0)) / m)) / 3.0;

        int prefix = 0;
        int maxPrefix = Math.Min(4, Math.Min(s1.Length, s2.Length));
        while (prefix < maxPrefix && s1[prefix] == s2[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * 0.1 * (1.0 - jaro));
    }

    private static string NormalizeForComparison(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (char ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }
}
