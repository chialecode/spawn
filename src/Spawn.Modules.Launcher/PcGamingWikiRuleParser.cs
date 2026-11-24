using System.Globalization;
using System.Net;
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

    /// <summary>
    /// Controls whether <see cref="ResolveSteamInstallDir"/> may inspect the host machine's Steam registry/default paths
    /// when <see cref="SteamInstallDirOverride"/> is unset. Unit tests can set this to <see langword="false"/> to guarantee
    /// zero host Steam probing.
    /// </summary>
    public bool AllowSystemSteamDiscovery { get; set; } = true;

    /// <summary>
    /// Optional <see cref="HttpMessageHandler"/> injected for 100% offline fixture testing when no explicit
    /// <see cref="HttpClient"/> is passed to <see cref="GameDataManagementService"/>.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandlerOverride { get; set; }

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

        if (!AllowSystemSteamDiscovery)
        {
            return null;
        }

        string? hkcuPath = TryReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath");
        if (TryNormalizeExistingOrCandidateDir(hkcuPath, requireExists: false, out string? resolved))
        {
            return resolved;
        }

        string? hkcuExe = TryReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamExe");
        if (!string.IsNullOrWhiteSpace(hkcuExe))
        {
            try
            {
                string? exeDir = Path.GetDirectoryName(hkcuExe.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar));
                if (TryNormalizeExistingOrCandidateDir(exeDir, requireExists: false, out resolved))
                {
                    return resolved;
                }
            }
            catch
            {
                // Ignore malformed SteamExe value.
            }
        }

        string?[] hklmCandidates =
        [
            TryReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            TryReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            TryReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
        ];

        foreach (string? candidate in hklmCandidates)
        {
            if (TryNormalizeExistingOrCandidateDir(candidate, requireExists: false, out resolved))
            {
                return resolved;
            }
        }

        string progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86)
            && TryNormalizeExistingOrCandidateDir(Path.Combine(progX86, "Steam"), requireExists: true, out resolved))
        {
            return resolved;
        }

        string progX64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(progX64)
            && TryNormalizeExistingOrCandidateDir(Path.Combine(progX64, "Steam"), requireExists: true, out resolved))
        {
            return resolved;
        }

        return null;
    }

    private static string? TryReadRegistryString(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        string valueName)
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

    private static bool TryNormalizeExistingOrCandidateDir(string? rawPath, bool requireExists, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        try
        {
            string full = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(rawPath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar)));
            if (requireExists && !Directory.Exists(full))
            {
                return false;
            }

            normalized = full;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Normalized template rule extracted from PCGamingWiki (<c>{{Game data/saves}}</c> / <c>{{Game data/config}}</c>).
/// </summary>
public sealed record PcGamingWikiRawRule(
    GameDataCategory Category,
    string RawTemplatePath);

/// <summary>
/// Queries PCGamingWiki MediaWiki Parse + Search APIs (replacing anonymous <c>action=cargoquery</c> which returns
/// <c>permissiondenied</c>) and expands <c>{{p|...}}</c> Windows path/registry templates.
/// </summary>
public static partial class PcGamingWikiRuleParser
{
    private const string ApiEndpoint = "https://www.pcgamingwiki.com/w/api.php";

    private static readonly string[] IgnoredWikiNamespacePrefixes =
    [
        "Talk:",
        "User:",
        "User talk:",
        "PCGamingWiki:",
        "File:",
        "MediaWiki:",
        "Template:",
        "Help:",
        "Category:",
        "Special:",
        "Module:",
    ];

    [GeneratedRegex(@"\{\{\s*(?:[Pp]|[Pp]ath)\|([^}|]+)(?:\|[^}]*)?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderTagRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"<ref\b[^>]*/>|<ref\b[^>]*>.*?</ref>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex WikiRefTagRegex();

    [GeneratedRegex(@"\{\{\s*(?:cn|citation\s+needed|note|ref|verify|check|clarify)(?:\|[^}]*)?\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnnotationTemplateRegex();

    /// <summary>
    /// Creates an <see cref="HttpClient"/> backed by a caller-supplied <see cref="HttpMessageHandler"/>
    /// (for 100% offline fixture unit testing without touching the network).
    /// </summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler handler, bool disposeHandler = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var client = new HttpClient(handler, disposeHandler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnLauncher/1.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

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
    /// Expands a PCGamingWiki template path (containing <c>{{p|appdata}}</c>, <c>{{p|localappdata}}</c>,
    /// <c>{{p|locallow}}</c>, <c>{{p|userprofile}}</c>, <c>{{p|game}}</c>, <c>{{p|steam}}</c>, <c>{{p|uid}}</c>,
    /// <c>{{p|hkcu}}</c>, etc.) into one or more concrete filesystem paths or registry key paths.
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

        string raw = StripWikiAnnotations(rule.RawTemplatePath);
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
                case "localappdatalow":
                case @"userprofile\appdata\locallow":
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
                case "wow64":
                    isRegistry = true;
                    return @"HKLM\Software\Wow6432Node";
                case "uid":
                case "user-id":
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

        // Strip any leftover HTML comments or unexpanded markup.
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

        if (isRegistry
            || expanded.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
            || expanded.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
            || expanded.StartsWith(@"HKEY_", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Fetches and parses PCGamingWiki save/config rules using a caller-provided <see cref="HttpMessageHandler"/>
    /// (convenience overload for 100% offline fixture testing).
    /// </summary>
    public static async Task<IReadOnlyList<PcGamingWikiRawRule>?> FetchOnlineRulesAsync(
        HttpMessageHandler messageHandler,
        int? steamAppId,
        string? gogGameId,
        string gameTitle,
        TimeSpan timeout,
        bool tryCargoQueryFirst = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        using var client = CreateHttpClient(messageHandler, disposeHandler: false);
        return await FetchOnlineRulesAsync(
            client,
            steamAppId,
            gogGameId,
            gameTitle,
            timeout,
            tryCargoQueryFirst,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches and parses PCGamingWiki save/config rules prioritizing <c>action=parse&amp;redirects=1&amp;prop=wikitext</c>
    /// with automatic <c>action=query&amp;list=search</c> fallback when the page title is unknown or returns <c>missingtitle</c>.
    /// </summary>
    public static Task<IReadOnlyList<PcGamingWikiRawRule>?> FetchOnlineRulesAsync(
        HttpClient httpClient,
        int? steamAppId,
        string? gogGameId,
        string gameTitle,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        FetchOnlineRulesAsync(
            httpClient,
            steamAppId,
            gogGameId,
            gameTitle,
            timeout,
            tryCargoQueryFirst: false,
            cancellationToken);

    /// <summary>
    /// Fetches and parses PCGamingWiki save/config rules with configurable <c>cargoquery</c> probing and automatic
    /// fallback to <c>action=parse&amp;redirects=1&amp;prop=wikitext</c> and <c>action=query&amp;list=search</c> whenever
    /// <c>cargoquery</c> returns <c>permissiondenied</c> or the page title returns <c>missingtitle</c>.
    /// </summary>
    public static async Task<IReadOnlyList<PcGamingWikiRawRule>?> FetchOnlineRulesAsync(
        HttpClient httpClient,
        int? steamAppId,
        string? gogGameId,
        string gameTitle,
        TimeSpan timeout,
        bool tryCargoQueryFirst,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(4.0) : timeout);
        var ct = linkedCts.Token;

        string normalizedTitle = gameTitle?.Trim() ?? string.Empty;
        var attemptedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attemptedPageIds = new HashSet<int>();
        bool cargoTried = false;

        try
        {
            // Optional legacy path: attempt cargoquery first when explicitly requested, falling back cleanly on permissiondenied.
            if (tryCargoQueryFirst && (steamAppId is > 0 || !string.IsNullOrWhiteSpace(gogGameId)))
            {
                cargoTried = true;
                var cargoRules = await TryResolveViaCargoAsync(
                    httpClient,
                    steamAppId,
                    gogGameId,
                    attemptedPageIds,
                    ct).ConfigureAwait(false);
                if (cargoRules is not null)
                {
                    return cargoRules;
                }
            }

            // 1. Primary path: action=parse&redirects=1&prop=wikitext directly by title.
            if (!string.IsNullOrWhiteSpace(normalizedTitle) && attemptedTitles.Add(normalizedTitle))
            {
                var directAttempt = await TryParseByTitleAsync(httpClient, normalizedTitle, ct).ConfigureAwait(false);
                if (directAttempt.Found)
                {
                    return ParseWikitext(directAttempt.Wikitext);
                }
            }

            // 2. Fallback path: action=query&list=search to discover the canonical PCGW page title, then action=parse.
            var searchQueries = BuildSearchQueries(normalizedTitle, steamAppId, gogGameId);
            foreach (string query in searchQueries)
            {
                var candidates = await SearchPageCandidatesAsync(httpClient, query, normalizedTitle, ct).ConfigureAwait(false);
                int parsedAttemptsForQuery = 0;

                foreach (var candidate in candidates)
                {
                    if (parsedAttemptsForQuery >= 2)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(candidate.Title) && attemptedTitles.Add(candidate.Title))
                    {
                        parsedAttemptsForQuery++;
                        var attempt = await TryParseByTitleAsync(httpClient, candidate.Title, ct).ConfigureAwait(false);
                        if (attempt.Found)
                        {
                            return ParseWikitext(attempt.Wikitext);
                        }
                    }
                    else if (candidate.PageId is > 0 && attemptedPageIds.Add(candidate.PageId.Value))
                    {
                        parsedAttemptsForQuery++;
                        var attempt = await TryParseByPageIdAsync(httpClient, candidate.PageId.Value, ct).ConfigureAwait(false);
                        if (attempt.Found)
                        {
                            return ParseWikitext(attempt.Wikitext);
                        }
                    }
                }
            }

            // 3. Last-resort compatibility fallback if neither parse nor search resolved a page (handles permissiondenied silently).
            if (!cargoTried && (steamAppId is > 0 || !string.IsNullOrWhiteSpace(gogGameId)))
            {
                var cargoRules = await TryResolveViaCargoAsync(
                    httpClient,
                    steamAppId,
                    gogGameId,
                    attemptedPageIds,
                    ct).ConfigureAwait(false);
                if (cargoRules is not null)
                {
                    return cargoRules;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
        {
            // Offline or timed out; return null so caller preserves existing cache or offline heuristics.
        }

        return null;
    }

    private static async Task<IReadOnlyList<PcGamingWikiRawRule>?> TryResolveViaCargoAsync(
        HttpClient httpClient,
        int? steamAppId,
        string? gogGameId,
        HashSet<int> attemptedPageIds,
        CancellationToken ct)
    {
        int? pageId = null;

        if (steamAppId is > 0)
        {
            string where = Uri.EscapeDataString($"Infobox_game.Steam_AppID HOLDS \"{steamAppId.Value}\"");
            var cargo = await QueryCargoPageIdAsync(httpClient, where, ct).ConfigureAwait(false);
            if (cargo.PermissionDenied)
            {
                return null;
            }

            pageId = cargo.PageId;
        }

        if (!pageId.HasValue && !string.IsNullOrWhiteSpace(gogGameId))
        {
            string where = Uri.EscapeDataString($"Infobox_game.GOGcom_ID HOLDS \"{gogGameId.Trim()}\"");
            var cargo = await QueryCargoPageIdAsync(httpClient, where, ct).ConfigureAwait(false);
            if (cargo.PermissionDenied)
            {
                return null;
            }

            pageId = cargo.PageId;
        }

        if (pageId is > 0 && attemptedPageIds.Add(pageId.Value))
        {
            var attempt = await TryParseByPageIdAsync(httpClient, pageId.Value, ct).ConfigureAwait(false);
            if (attempt.Found)
            {
                return ParseWikitext(attempt.Wikitext);
            }
        }

        return null;
    }

    private static List<string> BuildSearchQueries(string normalizedTitle, int? steamAppId, string? gogGameId)
    {
        var queries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(normalizedTitle) && seen.Add(normalizedTitle))
        {
            queries.Add(normalizedTitle);
        }

        if (steamAppId is > 0)
        {
            string appIdStr = steamAppId.Value.ToString(CultureInfo.InvariantCulture);
            if (seen.Add(appIdStr))
            {
                queries.Add(appIdStr);
            }
        }

        if (!string.IsNullOrWhiteSpace(gogGameId))
        {
            string trimmedGog = gogGameId.Trim();
            if (seen.Add(trimmedGog))
            {
                queries.Add(trimmedGog);
            }
        }

        return queries;
    }

    private static async Task<ParseAttemptResult> TryParseByTitleAsync(
        HttpClient httpClient,
        string pageTitle,
        CancellationToken cancellationToken)
    {
        string url = $"{ApiEndpoint}?action=parse&page={Uri.EscapeDataString(pageTitle)}&redirects=1&prop=wikitext&format=json";
        return await ExecuteParseRequestAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ParseAttemptResult> TryParseByPageIdAsync(
        HttpClient httpClient,
        int pageId,
        CancellationToken cancellationToken)
    {
        string url = $"{ApiEndpoint}?action=parse&pageid={pageId}&redirects=1&prop=wikitext&format=json";
        return await ExecuteParseRequestAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ParseAttemptResult> ExecuteParseRequestAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        using var resp = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return new ParseAttemptResult(Found: false, ErrorCode: ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture), Wikitext: null);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (TryExtractApiErrorCode(doc.RootElement, out string? errorCode))
        {
            return new ParseAttemptResult(Found: false, ErrorCode: errorCode, Wikitext: null);
        }

        if (doc.RootElement.TryGetProperty("parse", out var parseObj)
            && parseObj.ValueKind == JsonValueKind.Object
            && parseObj.TryGetProperty("wikitext", out var wtObj))
        {
            if (wtObj.ValueKind == JsonValueKind.String)
            {
                return new ParseAttemptResult(Found: true, ErrorCode: null, Wikitext: wtObj.GetString());
            }

            if (wtObj.ValueKind == JsonValueKind.Object
                && wtObj.TryGetProperty("*", out var starProp)
                && starProp.ValueKind == JsonValueKind.String)
            {
                return new ParseAttemptResult(Found: true, ErrorCode: null, Wikitext: starProp.GetString());
            }
        }

        return new ParseAttemptResult(Found: false, ErrorCode: null, Wikitext: null);
    }

    private static async Task<IReadOnlyList<SearchPageCandidate>> SearchPageCandidatesAsync(
        HttpClient httpClient,
        string searchQuery,
        string preferredTitle,
        CancellationToken cancellationToken)
    {
        string url = $"{ApiEndpoint}?action=query&list=search&srsearch={Uri.EscapeDataString(searchQuery)}&srlimit=5&format=json";
        using var resp = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (TryExtractApiErrorCode(doc.RootElement, out _))
        {
            return [];
        }

        if (!doc.RootElement.TryGetProperty("query", out var queryObj)
            || queryObj.ValueKind != JsonValueKind.Object
            || !queryObj.TryGetProperty("search", out var searchArr)
            || searchArr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rawCandidates = new List<SearchPageCandidate>();
        foreach (var item in searchArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? title = item.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                ? titleProp.GetString()?.Trim()
                : null;

            int? pageId = null;
            if (item.TryGetProperty("pageid", out var idProp))
            {
                if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt32(out int numId) && numId > 0)
                {
                    pageId = numId;
                }
                else if (idProp.ValueKind == JsonValueKind.String && int.TryParse(idProp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int strId) && strId > 0)
                {
                    pageId = strId;
                }
            }

            if (string.IsNullOrWhiteSpace(title) && !pageId.HasValue)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(title) && IsIgnoredWikiNamespace(title))
            {
                continue;
            }

            rawCandidates.Add(new SearchPageCandidate(title, pageId));
        }

        if (rawCandidates.Count <= 1 || string.IsNullOrWhiteSpace(preferredTitle))
        {
            return rawCandidates;
        }

        string normPreferred = NormalizeTitleForMatch(preferredTitle);
        return rawCandidates
            .OrderByDescending(c => ScoreSearchCandidate(c.Title, preferredTitle, normPreferred))
            .ToList();
    }

    private static async Task<(int? PageId, bool PermissionDenied)> QueryCargoPageIdAsync(
        HttpClient httpClient,
        string encodedWhereClause,
        CancellationToken cancellationToken)
    {
        string url = $"{ApiEndpoint}?action=cargoquery&tables=Infobox_game&fields=Infobox_game._pageID&where={encodedWhereClause}&limit=1&format=json";
        using var resp = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return (null, PermissionDenied: true);
        }

        if (!resp.IsSuccessStatusCode)
        {
            return (null, PermissionDenied: false);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (TryExtractApiErrorCode(doc.RootElement, out string? errorCode))
        {
            bool denied = string.Equals(errorCode, "permissiondenied", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(errorCode, "readapidenied", StringComparison.OrdinalIgnoreCase);
            return (null, PermissionDenied: denied);
        }

        if (!doc.RootElement.TryGetProperty("cargoquery", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
        {
            return (null, PermissionDenied: false);
        }

        var first = arr[0];
        if (first.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in titleObj.EnumerateObject())
            {
                if (prop.Name.Contains("pageID", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int numVal) && numVal > 0)
                    {
                        return (numVal, PermissionDenied: false);
                    }

                    if (prop.Value.ValueKind == JsonValueKind.String
                        && int.TryParse(prop.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                        && parsed > 0)
                    {
                        return (parsed, PermissionDenied: false);
                    }
                }
            }
        }

        return (null, PermissionDenied: false);
    }

    private static bool TryExtractApiErrorCode(JsonElement root, out string? errorCode)
    {
        errorCode = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error", out var errorObj)
            && errorObj.ValueKind == JsonValueKind.Object)
        {
            if (errorObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
            {
                errorCode = codeProp.GetString();
            }

            return true;
        }

        return false;
    }

    private static bool IsIgnoredWikiNamespace(string title)
    {
        foreach (string prefix in IgnoredWikiNamespacePrefixes)
        {
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ScoreSearchCandidate(string? candidateTitle, string preferredTitle, string normPreferred)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return 0;
        }

        if (string.Equals(candidateTitle, preferredTitle, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        string normCandidate = NormalizeTitleForMatch(candidateTitle);
        if (normPreferred.Length > 0 && string.Equals(normCandidate, normPreferred, StringComparison.Ordinal))
        {
            return 2;
        }

        if (normPreferred.Length >= 4 && normCandidate.Contains(normPreferred, StringComparison.Ordinal))
        {
            return 1;
        }

        return 0;
    }

    private static string NormalizeTitleForMatch(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static string StripWikiAnnotations(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string cleaned = HtmlCommentRegex().Replace(raw, string.Empty);
        cleaned = WikiRefTagRegex().Replace(cleaned, string.Empty);
        cleaned = AnnotationTemplateRegex().Replace(cleaned, string.Empty);
        return cleaned.Trim();
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
            && !osName.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase)
            && !osName.Equals("Epic Games Launcher", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (int i = 1; i < parts.Count; i++)
        {
            string candidate = StripWikiAnnotations(parts[i]);
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
            int openIdx = text.IndexOf("{{", searchFrom, StringComparison.Ordinal);
            if (openIdx < 0)
            {
                break;
            }

            int posAfterOpen = openIdx + 2;
            while (posAfterOpen < text.Length && char.IsWhiteSpace(text[posAfterOpen]))
            {
                posAfterOpen++;
            }

            if (posAfterOpen + templatePrefix.Length > text.Length
                || !text.AsSpan(posAfterOpen, templatePrefix.Length).Equals(templatePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                searchFrom = openIdx + 2;
                continue;
            }

            int contentStart = posAfterOpen + templatePrefix.Length;
            while (contentStart < text.Length && char.IsWhiteSpace(text[contentStart]))
            {
                contentStart++;
            }

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
                var info = new DirectoryInfo(childDir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

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

    private readonly record struct ParseAttemptResult(bool Found, string? ErrorCode, string? Wikitext);

    private readonly record struct SearchPageCandidate(string? Title, int? PageId);
}
