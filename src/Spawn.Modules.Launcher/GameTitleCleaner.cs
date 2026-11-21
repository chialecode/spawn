using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Spawn.Modules.Launcher;

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
        @"(?i)(?:[\s\._\-]+)(?:steamrip(?:\.com)?|fitgirl(?:[\s\._\-]*repacks?)?|dodi(?:[\s\._\-]*repacks?)?|elamigos|tenoke|rune|codex|plaza|flt|fairlight|empress|goldberg|skidrow|razor1911|hoodlum|ali213|3dm(?:game)?|gog(?:\.com)?|repack|portable|pre[\s\._\-]*installed|goty|game[\s\._\-]+of[\s\._\-]+the[\s\._\-]+year(?:[\s\._\-]+edition)?|deluxe[\s\._\-]+edition|ultimate[\s\._\-]+edition|complete[\s\._\-]+edition|definitive[\s\._\-]+edition|anniversary[\s\._\-]+edition|免安装.*|绿色版.*|官中.*|简体中文.*|繁体中文.*|汉化.*|硬盘版.*|整合版.*|学习版.*|全DLC.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseGroupAndEditionTailRegex();

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
                string cleanedPart = CleanTitle(part);
                if (IsLikelyEnglishTitle(cleanedPart) && !string.Equals(cleanedPart, "Unknown Game", StringComparison.OrdinalIgnoreCase))
                {
                    return cleanedPart;
                }
            }
        }

        string cleaned = CleanTitle(working);
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
    public static string CleanTitle(string? rawName)
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
            working = ReleaseGroupAndEditionTailRegex().Replace(working, string.Empty).Trim();
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
        working = ReleaseGroupAndEditionTailRegex().Replace(working, string.Empty);
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
    /// Reads PE <see cref="FileVersionInfo.ProductName"/> / <see cref="FileVersionInfo.FileDescription"/>
    /// while filtering generic Unity/Unreal/compiler placeholder strings.
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
            foreach (string? candidate in new[] { info.ProductName, info.FileDescription })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string trimmed = candidate.Trim();
                if (trimmed.Length < 2 || GenericEnginePlaceholders.Contains(trimmed))
                {
                    continue;
                }

                if (trimmed.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("UnrealEngine", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Installer", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Crash", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string cleaned = CleanTitle(trimmed);
                if (!string.IsNullOrWhiteSpace(cleaned)
                    && !string.Equals(cleaned, "Unknown Game", StringComparison.OrdinalIgnoreCase)
                    && !GenericEnginePlaceholders.Contains(cleaned))
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

    /// <summary>
    /// Builds an ordered, deduplicated list of search title candidates from PE metadata and folder name.
    /// </summary>
    public static IReadOnlyList<string> BuildSearchCandidates(string installDir, string? mainExeFullPath)
    {
        var candidates = new List<string>(4);

        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(installDir));
        string cleanedFolder = CleanTitle(folderName);
        if (!string.IsNullOrWhiteSpace(cleanedFolder) && !string.Equals(cleanedFolder, "Unknown Game", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(cleanedFolder);
        }

        string? peProduct = TryGetCleanPeProductName(mainExeFullPath);
        if (!string.IsNullOrWhiteSpace(peProduct)
            && !candidates.Contains(peProduct, StringComparer.OrdinalIgnoreCase))
        {
            // Prefer PE ProductName first when folder name was heavily abbreviated or purely numeric.
            if (candidates.Count == 0 || cleanedFolder.Length <= 2)
            {
                candidates.Insert(0, peProduct);
            }
            else
            {
                candidates.Add(peProduct);
            }
        }

        if (!string.IsNullOrWhiteSpace(mainExeFullPath))
        {
            string exeStem = CleanTitle(Path.GetFileNameWithoutExtension(mainExeFullPath));
            if (!string.IsNullOrWhiteSpace(exeStem)
                && exeStem.Length >= 3
                && !GenericEnginePlaceholders.Contains(exeStem)
                && !candidates.Contains(exeStem, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(exeStem);
            }
        }

        return candidates;
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
