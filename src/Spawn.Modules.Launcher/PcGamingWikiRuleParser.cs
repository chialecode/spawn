using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Spawn.Contracts;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Configurable environment &amp; registry root overrides for <see cref="GameDataManagementService"/>
/// so unit tests can redirect all special folders and <c>HKCU</c> registry paths into isolated sandboxes.
/// </summary>
public sealed class GameDataManagementOptions
{
    public bool AllowOnlinePcGamingWikiLookup { get; set; } = true;
    public TimeSpan OnlineLookupTimeout { get; set; } = TimeSpan.FromSeconds(4.0);

    public string? AppDataRoamingOverride { get; set; }
    public string? LocalAppDataOverride { get; set; }
    public string? LocalLowOverride { get; set; }
    public string? PublicDocumentsOverride { get; set; }
    public string? UserDocumentsOverride { get; set; }
    public string? UserProfileOverride { get; set; }
    public string? SavedGamesOverride { get; set; }
    public string? ProgramDataOverride { get; set; }
    public string? SteamInstallDirOverride { get; set; }

    /// <summary>
    /// Relative prefix under <c>HKEY_CURRENT_USER</c> for <c>Software</c> registry keys.
    /// Defaults to <c>"Software"</c>; automated tests can set e.g. <c>@"Software\SpawnTests_xxxx"</c>.
    /// </summary>
    public string HkcuSoftwarePrefix { get; set; } = "Software";

    public string ResolveAppDataRoaming() =>
        !string.IsNullOrWhiteSpace(AppDataRoamingOverride)
            ? Path.GetFullPath(AppDataRoamingOverride)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string ResolveLocalAppData() =>
        !string.IsNullOrWhiteSpace(LocalAppDataOverride)
            ? Path.GetFullPath(LocalAppDataOverride)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string ResolveUserProfile() =>
        !string.IsNullOrWhiteSpace(UserProfileOverride)
            ? Path.GetFullPath(UserProfileOverride)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string ResolveLocalLow()
    {
        if (!string.IsNullOrWhiteSpace(LocalLowOverride))
        {
            return Path.GetFullPath(LocalLowOverride);
        }

        string profile = ResolveUserProfile();
        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, "AppData", "LocalLow");
    }

    public string ResolvePublicDocuments()
    {
        if (!string.IsNullOrWhiteSpace(PublicDocumentsOverride))
        {
            return Path.GetFullPath(PublicDocumentsOverride);
        }

        string commonDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (!string.IsNullOrWhiteSpace(commonDocs))
        {
            return commonDocs;
        }

        string? pub = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrWhiteSpace(pub))
        {
            return Path.Combine(pub, "Documents");
        }

        string userProfile = ResolveUserProfile();
        string usersRoot = Directory.GetParent(Path.TrimEndingDirectorySeparator(userProfile))?.FullName ?? Path.GetTempPath();
        return Path.Combine(usersRoot, "Public", "Documents");
    }

    public string ResolvePublicRoot()
    {
        string pubDocs = ResolvePublicDocuments();
        return Directory.GetParent(Path.TrimEndingDirectorySeparator(pubDocs))?.FullName
               ?? (Environment.GetEnvironmentVariable("PUBLIC") ?? Path.GetTempPath());
    }

    public string ResolveUserDocuments() =>
        !string.IsNullOrWhiteSpace(UserDocumentsOverride)
            ? Path.GetFullPath(UserDocumentsOverride)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string ResolveSavedGames()
    {
        if (!string.IsNullOrWhiteSpace(SavedGamesOverride))
        {
            return Path.GetFullPath(SavedGamesOverride);
        }

        string profile = ResolveUserProfile();
        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, "Saved Games");
    }

    public string ResolveProgramData() =>
        !string.IsNullOrWhiteSpace(ProgramDataOverride)
            ? Path.GetFullPath(ProgramDataOverride)
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public string? ResolveSteamInstallDir()
    {
        if (!string.IsNullOrWhiteSpace(SteamInstallDirOverride))
        {
            return Path.GetFullPath(SteamInstallDirOverride);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: false);
            string? steamPath = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                return Path.GetFullPath(steamPath.Replace('/', Path.DirectorySeparatorChar));
            }
        }
        catch
        {
            // Ignore missing Steam registry key.
        }

        return null;
    }
}

/// <summary>
/// Normalized template rule extracted from PCGamingWiki (<c>{{Game data/saves}}</c> / <c>{{Game data/config}}</c>).
/// </summary>
public sealed record PcGamingWikiRawRule(
    GameDataCategory Category,
    string RawTemplatePath);

/// <summary>
/// Queries PCGamingWiki Cargo + MediaWiki Parse APIs and expands <c>{{p|...}}</c> Windows path/registry templates.
/// </summary>
public static partial class PcGamingWikiRuleParser
{
    [GeneratedRegex(@"\{\{\s*[Pp]\|([^}|]+)(?:\|[^}]*)?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderTagRegex();

    /// <summary>
    /// Extracts Windows save and configuration template paths from raw PCGamingWiki article wikitext.
    /// </summary>
    public static IReadOnlyList<PcGamingWikiRawRule> ParseWikitext(string? wikitext)
    {
        if (string.IsNullOrWhiteSpace(wikitext))
        {
            return [];
        }

        var results = new List<PcGamingWikiRawRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string block in ExtractTemplateBlocks(wikitext, "Game data/saves"))
        {
            AddParsedBlock(block, GameDataCategory.SaveData, results, seen);
        }

        foreach (string block in ExtractTemplateBlocks(wikitext, "Game data/config"))
        {
            AddParsedBlock(block, GameDataCategory.ConfigAndLogs, results, seen);
        }

        return results;
    }

    /// <summary>
    /// Expands a PCGamingWiki template path (containing <c>{{p|appdata}}</c>, <c>{{p|locallow}}</c>, <c>{{p|hkcu}}</c>, <c>{{p|uid}}</c>, etc.)
    /// into one or more concrete filesystem paths or registry key paths.
    /// </summary>
    /// <param name="identity">
    /// When supplied, wildcard expansions ({{p|uid}} and trailing globs) are kept only if the resulting path is inside the
    /// install directory or carries a game-specific segment, so a glob can never widen into a shared parent directory.
    /// </param>
    public static IReadOnlyList<(GameDataCategory EffectiveCategory, string ResolvedPathOrKey)> ExpandTemplateRule(
        PcGamingWikiRawRule rule,
        string gameInstallDir,
        GameDataManagementOptions options,
        GameDataTargetIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(options);

        string raw = rule.RawTemplatePath.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        bool isRegistry = false;
        bool unsupportedPlaceholder = false;
        const string UidSentinel = "__SPAWN_UID_WILDCARD__";

        string expanded = PlaceholderTagRegex().Replace(raw, match =>
        {
            string token = match.Groups[1].Value.Trim().Replace('/', '\\').ToLowerInvariant();
            switch (token)
            {
                case "appdata":
                    return options.ResolveAppDataRoaming();
                case "localappdata":
                    return options.ResolveLocalAppData();
                case "locallow":
                    return options.ResolveLocalLow();
                case "public":
                    return options.ResolvePublicRoot();
                case "publicdocuments":
                    return options.ResolvePublicDocuments();
                case "documents":
                case "mydocuments":
                case @"userprofile\documents":
                    return options.ResolveUserDocuments();
                case "savedgames":
                case @"userprofile\saved games":
                    return options.ResolveSavedGames();
                case "userprofile":
                case "home":
                    return options.ResolveUserProfile();
                case "programdata":
                case "allusersprofile":
                    return options.ResolveProgramData();
                case "game":
                    return gameInstallDir;
                case "steam":
                {
                    string? steamDir = options.ResolveSteamInstallDir();
                    if (string.IsNullOrWhiteSpace(steamDir))
                    {
                        unsupportedPlaceholder = true;
                        return string.Empty;
                    }

                    return steamDir;
                }
                case "hkcu":
                    isRegistry = true;
                    return "HKCU";
                case "hklm":
                    isRegistry = true;
                    return "HKLM";
                case "uid":
                case "username":
                    return UidSentinel;
                default:
                    unsupportedPlaceholder = true;
                    return string.Empty;
            }
        });

        if (unsupportedPlaceholder || string.IsNullOrWhiteSpace(expanded))
        {
            return [];
        }

        // Strip leftover wiki markup or HTML comments if any.
        int commentIdx = expanded.IndexOf("<!--", StringComparison.Ordinal);
        if (commentIdx >= 0)
        {
            expanded = expanded[..commentIdx];
        }

        expanded = expanded.Replace('/', '\\').Trim();
        while (expanded.Contains(@"\\", StringComparison.Ordinal))
        {
            expanded = expanded.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        if (isRegistry || expanded.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || expanded.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
        {
            string regKey = RewriteHkcuSoftwarePrefix(expanded.TrimEnd('\\'), options.HkcuSoftwarePrefix);
            if (regKey.Contains(UidSentinel, StringComparison.Ordinal))
            {
                return [];
            }

            return [(GameDataCategory.RegistryData, regKey)];
        }

        // Expand {{p|uid}} wildcard against existing user subdirectories on disk if present.
        if (expanded.Contains(UidSentinel, StringComparison.Ordinal))
        {
            if (expanded.Contains('*') || expanded.Contains('?'))
            {
                return [];
            }

            return ExpandWildcardFilesystemPaths(expanded, UidSentinel, rule.Category)
                .Where(e => IsAcceptableWildcardResult(e.Item2, gameInstallDir, options, identity))
                .ToList();
        }

        // Trailing glob (e.g. \*.sav) collapses to its parent folder; globs in any non-leaf segment are rejected outright.
        bool hadGlob = false;
        if (expanded.Contains('*') || expanded.Contains('?'))
        {
            string? dirPart = Path.GetDirectoryName(expanded);
            if (string.IsNullOrWhiteSpace(dirPart) || dirPart.Contains('*') || dirPart.Contains('?'))
            {
                return [];
            }

            expanded = dirPart;
            hadGlob = true;
        }

        try
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            if (hadGlob && !IsAcceptableWildcardResult(fullPath, gameInstallDir, options, identity))
            {
                return [];
            }

            return [(rule.Category, fullPath)];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsAcceptableWildcardResult(
        string fullPath,
        string gameInstallDir,
        GameDataManagementOptions options,
        GameDataTargetIdentity? identity)
    {
        if (identity is null)
        {
            return true;
        }

        try
        {
            string installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameInstallDir));
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
            if (normalized.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GameDataSafetyFence.IsGameSpecificPathOutsideInstall(normalized, identity, options);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<PcGamingWikiRawRule>?> FetchOnlineRulesAsync(
        HttpClient httpClient,
        int? steamAppId,
        string? gogGameId,
        string gameTitle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(4.0) : timeout);
        var ct = linkedCts.Token;

        try
        {
            int? pageId = null;

            if (steamAppId is > 0)
            {
                string where = Uri.EscapeDataString($"Infobox_game.Steam_AppID HOLDS \"{steamAppId.Value}\"");
                pageId = await QueryCargoPageIdAsync(httpClient, where, ct).ConfigureAwait(false);
            }

            if (!pageId.HasValue && !string.IsNullOrWhiteSpace(gogGameId))
            {
                string where = Uri.EscapeDataString($"Infobox_game.GOGcom_ID HOLDS \"{gogGameId}\"");
                pageId = await QueryCargoPageIdAsync(httpClient, where, ct).ConfigureAwait(false);
            }

            string parseUrl = pageId.HasValue
                ? $"https://www.pcgamingwiki.com/w/api.php?action=parse&pageid={pageId.Value}&prop=wikitext&format=json"
                : (!string.IsNullOrWhiteSpace(gameTitle)
                    ? $"https://www.pcgamingwiki.com/w/api.php?action=parse&page={Uri.EscapeDataString(gameTitle)}&redirects=1&prop=wikitext&format=json"
                    : string.Empty);

            if (string.IsNullOrWhiteSpace(parseUrl))
            {
                return null;
            }

            using var resp = await httpClient.GetAsync(parseUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("parse", out var parseObj)
                && parseObj.TryGetProperty("wikitext", out var wtObj)
                && wtObj.TryGetProperty("*", out var starProp)
                && starProp.ValueKind == JsonValueKind.String)
            {
                string? wikitext = starProp.GetString();
                return ParseWikitext(wikitext);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
        {
            // Offline or timed out; return null so caller preserves existing cache or offline heuristics.
        }

        return null;
    }

    private static async Task<int?> QueryCargoPageIdAsync(
        HttpClient httpClient,
        string encodedWhereClause,
        CancellationToken cancellationToken)
    {
        string url = $"https://www.pcgamingwiki.com/w/api.php?action=cargoquery&tables=Infobox_game&fields=Infobox_game._pageID&where={encodedWhereClause}&limit=1&format=json";
        using var resp = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("cargoquery", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
        {
            return null;
        }

        var first = arr[0];
        if (first.TryGetProperty("title", out var titleObj))
        {
            foreach (var prop in titleObj.EnumerateObject())
            {
                if (prop.Name.Contains("pageID", StringComparison.OrdinalIgnoreCase))
                {
                    string? rawId = prop.Value.GetString();
                    if (int.TryParse(rawId, out int parsed) && parsed > 0)
                    {
                        return parsed;
                    }
                }
            }
        }

        return null;
    }

    private static void AddParsedBlock(
        string innerContent,
        GameDataCategory defaultCategory,
        List<PcGamingWikiRawRule> output,
        HashSet<string> seen)
    {
        var parts = SplitTopLevelPipes(innerContent);
        if (parts.Count < 2)
        {
            return;
        }

        string osName = parts[0].Trim();
        if (!osName.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            && !osName.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            && !osName.Equals("GOG.com", StringComparison.OrdinalIgnoreCase)
            && !osName.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (int i = 1; i < parts.Count; i++)
        {
            string candidate = parts[i].Trim();
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('='))
            {
                continue;
            }

            if (seen.Add($"{defaultCategory}:{candidate}"))
            {
                output.Add(new PcGamingWikiRawRule(defaultCategory, candidate));
            }
        }
    }

    private static List<string> ExtractTemplateBlocks(string text, string templatePrefix)
    {
        var blocks = new List<string>();
        int searchFrom = 0;

        while (searchFrom < text.Length)
        {
            int start = text.IndexOf("{{" + templatePrefix, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            int contentStart = start + 2 + templatePrefix.Length;
            if (contentStart >= text.Length || text[contentStart] != '|')
            {
                searchFrom = contentStart;
                continue;
            }

            contentStart++; // skip '|'
            int depth = 1;
            int pos = contentStart;
            while (pos < text.Length - 1 && depth > 0)
            {
                if (text[pos] == '{' && text[pos + 1] == '{')
                {
                    depth++;
                    pos += 2;
                }
                else if (text[pos] == '}' && text[pos + 1] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        blocks.Add(text[contentStart..pos]);
                        pos += 2;
                        break;
                    }

                    pos += 2;
                }
                else
                {
                    pos++;
                }
            }

            searchFrom = pos;
        }

        return blocks;
    }

    private static List<string> SplitTopLevelPipes(string templateBody)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int braceDepth = 0;

        for (int i = 0; i < templateBody.Length; i++)
        {
            if (i + 1 < templateBody.Length && templateBody[i] == '{' && templateBody[i + 1] == '{')
            {
                braceDepth++;
                sb.Append("{{");
                i++;
            }
            else if (i + 1 < templateBody.Length && templateBody[i] == '}' && templateBody[i + 1] == '}')
            {
                braceDepth = Math.Max(0, braceDepth - 1);
                sb.Append("}}");
                i++;
            }
            else if (templateBody[i] == '|' && braceDepth == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(templateBody[i]);
            }
        }

        parts.Add(sb.ToString());
        return parts;
    }

    private static List<(GameDataCategory, string)> ExpandWildcardFilesystemPaths(
        string patternWithSentinel,
        string sentinel,
        GameDataCategory category)
    {
        int idx = patternWithSentinel.IndexOf(sentinel, StringComparison.Ordinal);
        if (idx <= 0)
        {
            return [];
        }

        string prefixDir = patternWithSentinel[..idx].TrimEnd('\\');
        string suffix = patternWithSentinel[(idx + sentinel.Length)..].TrimStart('\\');

        if (!Directory.Exists(prefixDir))
        {
            return [];
        }

        var expanded = new List<(GameDataCategory, string)>();
        try
        {
            foreach (string childDir in Directory.EnumerateDirectories(prefixDir))
            {
                string combined = string.IsNullOrWhiteSpace(suffix)
                    ? childDir
                    : Path.Combine(childDir, suffix);

                if (combined.Contains('*') || combined.Contains('?'))
                {
                    combined = Path.GetDirectoryName(combined) ?? combined;
                }

                expanded.Add((category, Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined))));
            }
        }
        catch
        {
            // Ignore inaccessible directories.
        }

        return expanded;
    }

    internal static string RewriteHkcuSoftwarePrefix(string regKey, string customSoftwarePrefix)
    {
        string normalizedPrefix = string.IsNullOrWhiteSpace(customSoftwarePrefix)
            ? "Software"
            : customSoftwarePrefix.Trim('\\');

        if (string.Equals(normalizedPrefix, "Software", StringComparison.OrdinalIgnoreCase))
        {
            return regKey;
        }

        const string StandardPrefix = @"HKCU\Software\";
        if (regKey.StartsWith(StandardPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"HKCU\" + normalizedPrefix + @"\" + regKey[StandardPrefix.Length..];
        }

        return regKey;
    }
}
