using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

public sealed record ResolvedSchemaWaterfallResult(
    string SchemaKey,
    AchievementSchemaSource Source,
    int? ResolvedSteamAppId,
    string? ResolvedGogGameId,
    IReadOnlyList<AchievementDefinitionDto> Definitions);

/// <summary>
/// Multi-source Chinese achievement schema &amp; hidden spoiler waterfall engine:
/// 1) GOG public achievement endpoint (<c>gameplay.gog.com</c> / <c>gogdb.org</c>)
/// 2) Local <c>steam_settings/achievements.json</c> &amp; local Steam <c>UserGameStatsSchema_&lt;AppId&gt;.bin</c>
/// 3) Official Steam Web API (<c>GetSchemaForGame/v2?l=schinese</c>) via <see cref="ISteamApiKeyProvider"/> (zero key logging!)
/// 4) Keyless <c>GetGlobalAchievementPercentagesForApp/v2</c> global rarity percentages
/// 5) Hidden achievement (<c>hidden=1</c>) spoiler truth backfill via local Binary VDF, built-in rules, and community encyclopedias
/// 6) Icon download caching + <see cref="SkiaSharp"/> Rec.709 grayscale synthesis.
/// </summary>
public sealed partial class AchievementSchemaService : IDisposable
{
    private readonly ISteamApiKeyProvider _steamApiKeyProvider;
    private readonly NativeSaveAchievementProvider _nativeSaveProvider;
    private readonly AchievementIconProcessor _iconProcessor;
    private readonly AchievementModuleOptions _options;
    private readonly ILogger<AchievementSchemaService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AchievementSchemaService(
        ISteamApiKeyProvider steamApiKeyProvider,
        NativeSaveAchievementProvider nativeSaveProvider,
        AchievementIconProcessor iconProcessor,
        AchievementModuleOptions? options = null,
        HttpClient? httpClient = null,
        ILogger<AchievementSchemaService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(steamApiKeyProvider);
        ArgumentNullException.ThrowIfNull(nativeSaveProvider);
        ArgumentNullException.ThrowIfNull(iconProcessor);

        _steamApiKeyProvider = steamApiKeyProvider;
        _nativeSaveProvider = nativeSaveProvider;
        _iconProcessor = iconProcessor;
        _options = options ?? new AchievementModuleOptions();
        _logger = logger ?? NullLogger<AchievementSchemaService>.Instance;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            // Dedicated handler without Microsoft.Extensions.Http URI logging so secret query parameters never enter logs.
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SPAWN/2.0 (Windows NT 10.0; Native Desktop)");
            _ownsHttpClient = true;
        }
    }

    public Task<ResolvedSchemaWaterfallResult> FetchAndBuildSchemaAsync(
        GameRecord game,
        string preferredLanguage = "schinese",
        CancellationToken cancellationToken = default) =>
        FetchAndBuildSchemaAsync(game, preferredLanguage, forceRefreshIcons: false, cancellationToken);

    public async Task<ResolvedSchemaWaterfallResult> FetchAndBuildSchemaAsync(
        GameRecord game,
        string preferredLanguage,
        bool forceRefreshIcons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        var (detectedGogId, detectedGogClient) = GogAchievementProvider.TryParseGogGameInfo(game.InstallDir);
        string? gogGameId = !string.IsNullOrWhiteSpace(game.GogGameId) ? game.GogGameId : detectedGogId;
        string? gogClientId = !string.IsNullOrWhiteSpace(game.GogClientId) ? game.GogClientId : detectedGogClient;

        var matchedRule = _nativeSaveProvider.FindMatchingRule(game, game.SteamAppId, gogGameId);
        int? deterministicSteamAppId = game.SteamAppId
                                       ?? matchedRule?.MappedSteamAppId
                                       ?? SteamEmulatorFormatParsers.TryDetectSteamAppIdFromDirectory(game.InstallDir);
        int? steamAppId = deterministicSteamAppId;

        IReadOnlyList<AchievementDefinitionDto> primaryDefs = [];
        var primarySource = AchievementSchemaSource.None;

        // Stage 1: GOG Public Achievements API (when game has GOG IDs)
        if (_options.AllowNetworkDownloads && (!string.IsNullOrWhiteSpace(gogClientId) || !string.IsNullOrWhiteSpace(gogGameId)))
        {
            var gogDefs = await TryFetchGogPublicAchievementsAsync(gogGameId, gogClientId, preferredLanguage, cancellationToken).ConfigureAwait(false);
            if (gogDefs.Count > 0)
            {
                primaryDefs = gogDefs;
                primarySource = AchievementSchemaSource.GogPublicApi;
            }
        }

        // Cross-platform SteamAppId discovery if GOG had no achievements or title is Steamrip without explicit AppID
        if (steamAppId is null && _options.AllowNetworkDownloads && !string.IsNullOrWhiteSpace(game.Title))
        {
            steamAppId = await TrySearchSteamAppIdByTitleAsync(game.Title, cancellationToken).ConfigureAwait(false);
        }

        // Stage 2: Local emulator steam_settings/achievements.json & Local Steam UserGameStatsSchema_<AppId>.bin
        var localEmuDefs = TryLoadLocalEmulatorSchema(game.InstallDir, preferredLanguage);
        var localSteamBinDefs = steamAppId is > 0
            ? await TryLoadLocalSteamSchemaBinAsync(steamAppId.Value, preferredLanguage, cancellationToken).ConfigureAwait(false)
            : [];

        // Stage 3: Official Steam Web API (ISteamUserStats/GetSchemaForGame/v2?l=schinese) using DPAPI / .local key
        IReadOnlyList<AchievementDefinitionDto> steamWebDefs = [];
        if (_options.AllowNetworkDownloads && steamAppId is > 0)
        {
            string? apiKey = await _steamApiKeyProvider.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                steamWebDefs = await TryFetchSteamWebSchemaAsync(steamAppId.Value, apiKey, preferredLanguage, cancellationToken).ConfigureAwait(false);
            }
        }

        // Pick richest primary base (prefer SteamWebApi schinese > LocalSteamBin > LocalEmu > GogPublic > BuiltInRule)
        if (steamWebDefs.Count > 0 && (primaryDefs.Count == 0 || !ContainsCjk(primaryDefs)))
        {
            primaryDefs = steamWebDefs;
            primarySource = AchievementSchemaSource.SteamWebApi;
        }
        else if (primaryDefs.Count == 0 && localSteamBinDefs.Count > 0)
        {
            primaryDefs = localSteamBinDefs;
            primarySource = AchievementSchemaSource.SteamLocalBin;
        }
        else if (primaryDefs.Count == 0 && localEmuDefs.Count > 0)
        {
            primaryDefs = localEmuDefs;
            primarySource = AchievementSchemaSource.LocalEmulatorSchema;
        }

        // Stage 4: Keyless Global Unlock Percentages (`GetGlobalAchievementPercentagesForApp/v2`)
        IReadOnlyDictionary<string, double> globalPercents = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_options.AllowNetworkDownloads && steamAppId is > 0)
        {
            globalPercents = await TryFetchGlobalPercentagesAsync(steamAppId.Value, cancellationToken).ConfigureAwait(false);
        }

        // Stage 5: Community encyclopedia fallback (SteamHunters / Exophase) when primary schema is empty OR has stripped hidden descriptions
        bool needsCommunity = primaryDefs.Count == 0
                              || primaryDefs.Any(d => d.IsHidden && string.IsNullOrWhiteSpace(d.Description) && string.IsNullOrWhiteSpace(d.HiddenSpoilerDescription));

        IReadOnlyList<AchievementDefinitionDto> communityDefs = [];
        if (needsCommunity && _options.AllowNetworkDownloads && _options.EnableCommunitySpoilerRequests && steamAppId is > 0)
        {
            communityDefs = await TryFetchCommunityAchievementsAsync(steamAppId.Value, cancellationToken).ConfigureAwait(false);
            if (primaryDefs.Count == 0 && communityDefs.Count > 0)
            {
                primaryDefs = communityDefs;
                primarySource = AchievementSchemaSource.CommunityMirror;
            }
        }

        // Stage 6: Built-in rule fallback (e.g., offline LIMBO 13 Chinese achievements + ACH_13 spoiler truth!)
        var builtInFallback = matchedRule?.FallbackChineseSchema ?? [];
        if (primaryDefs.Count == 0 && builtInFallback.Count > 0)
        {
            primaryDefs = builtInFallback;
            primarySource = AchievementSchemaSource.BuiltInRule;
        }

        // Merge descriptions, hidden spoiler truths, icons, and global rarity percentages across all waterfall layers
        var merged = MergeWaterfallLayers(
            primaryDefs,
            localSteamBinDefs,
            localEmuDefs,
            communityDefs,
            builtInFallback,
            globalPercents);

        int? verifiedSteamAppId = deterministicSteamAppId ?? (merged.Count > 0 ? steamAppId : null);
        string schemaKey = verifiedSteamAppId is > 0
            ? $"Steam_{verifiedSteamAppId.Value.ToString(CultureInfo.InvariantCulture)}"
            : (!string.IsNullOrWhiteSpace(gogGameId) ? $"GOG_{gogGameId}" : $"Game_{game.GameId}");

        // Stage 7: Cache color icons & synthesize SkiaSharp Rec.709 grayscale locked icons
        if (merged.Count > 0)
        {
            merged = await _iconProcessor.CacheAndProcessIconsAsync(
                schemaKey, merged, _httpClient, _options.AllowNetworkDownloads, forceRefreshIcons, cancellationToken).ConfigureAwait(false);
        }

        return new ResolvedSchemaWaterfallResult(
            SchemaKey: schemaKey,
            Source: primarySource,
            ResolvedSteamAppId: verifiedSteamAppId,
            ResolvedGogGameId: gogGameId,
            Definitions: merged);
    }

    public static IReadOnlyList<AchievementDefinitionDto> MergeWaterfallLayers(
        IReadOnlyList<AchievementDefinitionDto> primary,
        IReadOnlyList<AchievementDefinitionDto> localSteamBin,
        IReadOnlyList<AchievementDefinitionDto> localEmu,
        IReadOnlyList<AchievementDefinitionDto> community,
        IReadOnlyList<AchievementDefinitionDto> builtInRule,
        IReadOnlyDictionary<string, double> globalPercents)
    {
        if (primary.Count == 0)
        {
            return [];
        }

        var binByApi = localSteamBin.ToDictionary(d => d.ApiName, StringComparer.OrdinalIgnoreCase);
        var emuByApi = localEmu.ToDictionary(d => d.ApiName, StringComparer.OrdinalIgnoreCase);
        var commByApi = community.ToDictionary(d => d.ApiName, StringComparer.OrdinalIgnoreCase);
        var ruleByApi = builtInRule.ToDictionary(d => d.ApiName, StringComparer.OrdinalIgnoreCase);

        var result = new List<AchievementDefinitionDto>(primary.Count);
        for (int i = 0; i < primary.Count; i++)
        {
            var cur = primary[i];
            binByApi.TryGetValue(cur.ApiName, out var binDef);
            emuByApi.TryGetValue(cur.ApiName, out var emuDef);
            commByApi.TryGetValue(cur.ApiName, out var commDef);
            ruleByApi.TryGetValue(cur.ApiName, out var ruleDef);

            // Prefer Chinese display name if primary returned English
            string displayName = cur.DisplayName;
            if (!ContainsCjkText(displayName))
            {
                displayName = FirstWithCjk(binDef?.DisplayName, ruleDef?.DisplayName, emuDef?.DisplayName, commDef?.DisplayName)
                              ?? displayName;
            }

            // Backfill stripped hidden achievement description (Valve's GetSchemaForGame strips `description` when `hidden=1`)
            string? spoiler = FirstNonEmpty(
                cur.HiddenSpoilerDescription,
                binDef?.HiddenSpoilerDescription,
                binDef?.Description,
                ruleDef?.HiddenSpoilerDescription,
                ruleDef?.Description,
                emuDef?.Description,
                commDef?.HiddenSpoilerDescription,
                commDef?.Description);

            string description = FirstNonEmpty(
                cur.Description,
                binDef?.Description,
                ruleDef?.Description,
                emuDef?.Description,
                commDef?.Description,
                spoiler) ?? string.Empty;

            if (!ContainsCjkText(description))
            {
                description = FirstWithCjk(binDef?.Description, ruleDef?.Description, emuDef?.Description) ?? description;
            }

            string? iconPath = FirstNonEmpty(cur.IconPath, binDef?.IconPath, emuDef?.IconPath, commDef?.IconPath);
            string? iconGrayPath = FirstNonEmpty(cur.IconGrayPath, binDef?.IconGrayPath, emuDef?.IconGrayPath, commDef?.IconGrayPath);

            double? percent = cur.GlobalPercent;
            if (globalPercents.TryGetValue(cur.ApiName, out double gp))
            {
                percent = Math.Round(gp, 2);
            }
            else
            {
                percent ??= commDef?.GlobalPercent ?? ruleDef?.GlobalPercent;
            }

            result.Add(cur with
            {
                DisplayName = displayName.Trim(),
                Description = description.Trim(),
                HiddenSpoilerDescription = cur.IsHidden ? (spoiler?.Trim() ?? description.Trim()) : cur.HiddenSpoilerDescription,
                IconPath = iconPath,
                IconGrayPath = iconGrayPath,
                GlobalPercent = percent,
                SortOrder = i,
            });
        }

        return result;
    }

    private async Task<IReadOnlyList<AchievementDefinitionDto>> TryFetchSteamWebSchemaAsync(
        int appId,
        string apiKey,
        string language,
        CancellationToken ct)
    {
        // Construct URI only inside local scope; NEVER pass URL or exception message containing `key=` to any logger.
        string requestUri = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={Uri.EscapeDataString(apiKey)}&appid={appId.ToString(CultureInfo.InvariantCulture)}&l={Uri.EscapeDataString(language)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Steam GetSchemaForGame returned HTTP {StatusCode} for AppId {AppId}.", (int)resp.StatusCode, appId);
                }
                return [];
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("game", out var gameObj)
                || !gameObj.TryGetProperty("availableGameStats", out var statsObj)
                || !statsObj.TryGetProperty("achievements", out var achArr)
                || achArr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var defs = new List<AchievementDefinitionDto>();
            int order = 0;
            foreach (var elem in achArr.EnumerateArray())
            {
                string? name = TryGetStr(elem, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string displayName = TryGetStr(elem, "displayName") ?? name;
                string desc = TryGetStr(elem, "description") ?? string.Empty;
                bool hidden = TryGetInt(elem, "hidden") != 0;
                string? icon = TryGetStr(elem, "icon");
                string? iconGray = TryGetStr(elem, "icongray");

                defs.Add(new AchievementDefinitionDto
                {
                    ApiName = name.Trim(),
                    DisplayName = displayName.Trim(),
                    Description = desc.Trim(),
                    HiddenSpoilerDescription = hidden && !string.IsNullOrWhiteSpace(desc) ? desc.Trim() : null,
                    IsHidden = hidden,
                    IconPath = icon,
                    IconGrayPath = iconGray,
                    SortOrder = order++,
                });
            }

            return defs;
        }
        catch
        {
            // Intentionally swallow raw exception details so request URI with API key can never leak.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Steam GetSchemaForGame request failed for AppId {AppId}; continuing waterfall.", appId);
            }
            return [];
        }
    }

    private async Task<IReadOnlyDictionary<string, double>> TryFetchGlobalPercentagesAsync(int appId, CancellationToken ct)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string url = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v0002/?gameid={appId.ToString(CultureInfo.InvariantCulture)}&format=json";

        try
        {
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return map;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("achievementpercentages", out var apObj)
                || !apObj.TryGetProperty("achievements", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var item in arr.EnumerateArray())
            {
                string? name = TryGetStr(item, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (item.TryGetProperty("percent", out var pElem))
                {
                    if (pElem.ValueKind == JsonValueKind.Number && pElem.TryGetDouble(out double d))
                    {
                        map[name] = Math.Clamp(d, 0.0, 100.0);
                    }
                    else if (pElem.ValueKind == JsonValueKind.String
                             && double.TryParse(pElem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd))
                    {
                        map[name] = Math.Clamp(sd, 0.0, 100.0);
                    }
                }
            }
        }
        catch
        {
            // Optional global percentages; never block schema sync.
        }

        return map;
    }

    private async Task<IReadOnlyList<AchievementDefinitionDto>> TryFetchGogPublicAchievementsAsync(
        string? gogGameId,
        string? gogClientId,
        string preferredLanguage,
        CancellationToken ct)
    {
        string locale = preferredLanguage.Contains("chinese", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en-US";
        string[] candidateUrls =
        [
            !string.IsNullOrWhiteSpace(gogClientId) ? $"https://gameplay.gog.com/clients/{Uri.EscapeDataString(gogClientId)}/users/0/achievements" : string.Empty,
            !string.IsNullOrWhiteSpace(gogGameId) ? $"https://gameplay.gog.com/clients/{Uri.EscapeDataString(gogGameId)}/users/0/achievements" : string.Empty,
        ];

        foreach (string url in candidateUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.AcceptLanguage.ParseAdd($"{locale},zh-CN;q=0.9,en;q=0.8");

                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = new List<AchievementDefinitionDto>();
                int sort = 0;
                foreach (var item in items.EnumerateArray())
                {
                    string? key = TryGetStr(item, "achievement_key") ?? TryGetStr(item, "achievement_id") ?? TryGetStr(item, "key");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    string name = TryGetStr(item, "name") ?? key;
                    string desc = TryGetStr(item, "description") ?? string.Empty;
                    bool visible = !item.TryGetProperty("visible", out var vProp) || vProp.ValueKind != JsonValueKind.False;
                    double? rarity = item.TryGetProperty("rarity", out var rProp) && rProp.TryGetDouble(out double r) ? r : null;

                    list.Add(new AchievementDefinitionDto
                    {
                        ApiName = key.Trim(),
                        DisplayName = name.Trim(),
                        Description = desc.Trim(),
                        HiddenSpoilerDescription = !visible && !string.IsNullOrWhiteSpace(desc) ? desc.Trim() : null,
                        IsHidden = !visible,
                        IconPath = TryGetStr(item, "image_url_unlocked"),
                        IconGrayPath = TryGetStr(item, "image_url_locked"),
                        GlobalPercent = rarity,
                        SortOrder = sort++,
                    });
                }

                if (list.Count > 0)
                {
                    return list;
                }
            }
            catch
            {
                // Ignore GOG endpoint timeout/404 and fall through to Steam mapping.
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<AchievementDefinitionDto>> TryFetchCommunityAchievementsAsync(int appId, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.CommunitySpoilerTimeout);

            string url = $"https://steamhunters.com/api/apps/{appId.ToString(CultureInfo.InvariantCulture)}/achievements";
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<AchievementDefinitionDto>();
            int sort = 0;
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                string? apiName = TryGetStr(elem, "apiName") ?? TryGetStr(elem, "name");
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                string title = TryGetStr(elem, "name") ?? apiName;
                string desc = TryGetStr(elem, "description") ?? string.Empty;
                bool hidden = elem.TryGetProperty("hidden", out var hp) && hp.ValueKind == JsonValueKind.True;
                double? pct = elem.TryGetProperty("steamPercentage", out var sp) && sp.TryGetDouble(out double d) ? d : null;

                list.Add(new AchievementDefinitionDto
                {
                    ApiName = apiName.Trim(),
                    DisplayName = title.Trim(),
                    Description = desc.Trim(),
                    HiddenSpoilerDescription = hidden && !string.IsNullOrWhiteSpace(desc) ? desc.Trim() : null,
                    IsHidden = hidden,
                    IconPath = TryGetStr(elem, "icon"),
                    IconGrayPath = TryGetStr(elem, "iconGray"),
                    GlobalPercent = pct,
                    SortOrder = sort++,
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    private static readonly Dictionary<string, string> RomanInstallmentMap = new(StringComparer.OrdinalIgnoreCase)
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

    private async Task<int?> TrySearchSteamAppIdByTitleAsync(string rawTitle, CancellationToken ct)
    {
        string cleaned = NormalizeSearchTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        string expanded = ExpandLeadingAbbreviations(cleaned);
        var queries = new List<string>(2);
        if (!string.Equals(expanded, cleaned, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add(expanded);
        }

        queries.Add(cleaned);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            (int AppId, double Score)? bestMatch = null;

            foreach (string query in queries)
            {
                foreach (string locale in new[] { "l=schinese&cc=CN", "l=english&cc=US" })
                {
                    string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&{locale}";
                    using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
                    if (!doc.RootElement.TryGetProperty("items", out var items)
                        || items.ValueKind != JsonValueKind.Array
                        || items.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    foreach (var item in items.EnumerateArray())
                    {
                        string? name = TryGetStr(item, "name")?.Trim();
                        int id = TryGetInt(item, "id");
                        if (id <= 0 || string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        if (HasConflictingSequelOrAnthologyTokens(query, name))
                        {
                            continue;
                        }

                        double sim = Math.Max(
                            ComputeJaroWinkler(query, name),
                            ComputeJaroWinkler(expanded, ExpandLeadingAbbreviations(NormalizeSearchTitle(name))));

                        if (RemasterEditionMarkerRegex().IsMatch(query) != RemasterEditionMarkerRegex().IsMatch(name))
                        {
                            sim -= 0.12;
                        }

                        if (sim >= 0.98)
                        {
                            return id;
                        }

                        if (sim >= 0.90 && (bestMatch is null || sim > bestMatch.Value.Score))
                        {
                            bestMatch = (id, sim);
                        }
                    }

                    if (bestMatch is { Score: >= 0.93 })
                    {
                        return bestMatch.Value.AppId;
                    }
                }
            }

            return bestMatch?.AppId;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeSearchTitle(string rawTitle)
    {
        string cleaned = NoiseSuffixRegex().Replace(rawTitle, " ").Trim();
        if (ShorthandDeSuffixRegex().IsMatch(cleaned))
        {
            cleaned = ShorthandDeSuffixRegex().Replace(cleaned, " - Definitive Edition");
        }

        return MultiWhitespaceRegex().Replace(cleaned, " ").Trim(' ', '-', '_', '.', '–', '—');
    }

    private static string ExpandLeadingAbbreviations(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string trimmed = title.Trim();
        return LeadingGtaRegex().IsMatch(trimmed)
            ? LeadingGtaRegex().Replace(trimmed, "Grand Theft Auto")
            : trimmed;
    }

    private static bool HasConflictingSequelOrAnthologyTokens(string query, string candidate)
    {
        if (AnthologyWordRegex().IsMatch(query) && !CollectionWordRegex().IsMatch(candidate))
        {
            return true;
        }

        if (AnthologyWordRegex().IsMatch(candidate) && !CollectionWordRegex().IsMatch(query))
        {
            return true;
        }

        var leftTokens = ExtractInstallmentNumbers(query);
        var rightTokens = ExtractInstallmentNumbers(candidate);
        return !leftTokens.SetEquals(rightTokens);
    }

    private static HashSet<string> ExtractInstallmentNumbers(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordTokenRegex().Matches(text))
        {
            string token = match.Groups[1].Value;
            if (RomanInstallmentMap.TryGetValue(token, out string? romanNum))
            {
                result.Add(romanNum);
            }
            else if (token.All(char.IsAsciiDigit))
            {
                string trimmed = token.TrimStart('0');
                result.Add(trimmed.Length == 0 ? "0" : trimmed);
            }
        }

        return result;
    }

    private static double ComputeJaroWinkler(string left, string right)
    {
        string s1 = NormalizeAlphanumeric(left);
        string s2 = NormalizeAlphanumeric(right);
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

    private static string NormalizeAlphanumeric(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyList<AchievementDefinitionDto> TryLoadLocalEmulatorSchema(string? installDir, string preferredLanguage)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return [];
        }

        foreach (string file in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "achievements.json", maxDepth: 4))
        {
            try
            {
                byte[] raw = File.ReadAllBytes(file);
                string? settingsDir = Path.GetDirectoryName(file);
                var parsed = SteamEmulatorFormatParsers.ParseLocalGbeSchemaJson(raw, settingsDir, preferredLanguage);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch
            {
                // Ignore.
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<AchievementDefinitionDto>> TryLoadLocalSteamSchemaBinAsync(
        int appId,
        string preferredLanguage,
        CancellationToken ct)
    {
        string? steamRoot = SteamLocalCacheAchievementProvider.ResolveSteamInstallPath(_options.EnvironmentOverrides);
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return [];
        }

        string schemaBinPath = Path.Combine(steamRoot, "appcache", "stats", $"UserGameStatsSchema_{appId.ToString(CultureInfo.InvariantCulture)}.bin");
        byte[]? raw = await ResilientFileReader.ReadSharedBytesWithRetryAsync(schemaBinPath, ct).ConfigureAwait(false);
        if (raw is null || raw.Length == 0)
        {
            return [];
        }

        var mappings = SteamBinaryVdfParser.ParseSchemaBin(raw, appId, preferredLanguage);
        return [.. mappings.Select(m => m.Definition)];
    }

    private static string? TryGetStr(JsonElement elem, string prop) =>
        elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int TryGetInt(JsonElement elem, string prop)
    {
        if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(prop, out var p))
        {
            return 0;
        }

        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v))
        {
            return v;
        }

        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sv))
        {
            return sv;
        }

        return 0;
    }

    private static string? FirstNonEmpty(params ReadOnlySpan<string?> values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return null;
    }

    private static string? FirstWithCjk(params ReadOnlySpan<string?> values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v) && ContainsCjkText(v))
            {
                return v;
            }
        }

        return null;
    }

    private static bool ContainsCjk(IReadOnlyList<AchievementDefinitionDto> defs) =>
        defs.Any(d => ContainsCjkText(d.DisplayName) || ContainsCjkText(d.Description));

    private static bool ContainsCjkText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (c is >= '\u4E00' and <= '\u9FFF')
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"(\bSteamRIP(\.com)?\b|\bFitGirl[- ]?Repacks?\b|\bGOTY\b|\b(?:The\s+)?Deluxe\s+Edition\b|\b(?:The\s+)?Ultimate\s+Edition\b|\b(?:The\s+)?Complete\s+Edition\b|\bv\d+(\.\d+)+\b|\bBuild[\.\s]?\d+\b|\[[^\]]+\]|\([^\)]+\))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseSuffixRegex();

    [GeneratedRegex(@"(?:[\s\._]*[-–—][\s\._]*)(?:(?:[Tt]he)[\s\._\-]+)?DE$", RegexOptions.CultureInvariant)]
    private static partial Regex ShorthandDeSuffixRegex();

    [GeneratedRegex(@"^GTA(?=\s*(?:[-:–—]|\b(?:III|II|IV|V|VI|1|2|3|4|5|6|Vice\s+City|San\s+Andreas|Liberty\s+City|The\s+Trilogy|Trilogy|Episodes|Chinatown|Online)\b|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingGtaRegex();

    [GeneratedRegex(@"\b([A-Za-z0-9]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"\b(?:trilogy|anthology)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnthologyWordRegex();

    [GeneratedRegex(@"\b(?:trilogy|anthology|collection|bundle)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectionWordRegex();

    [GeneratedRegex(@"\b(?:definitive(?:\s+edition)?|remaster(?:ed)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemasterEditionMarkerRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespaceRegex();

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
