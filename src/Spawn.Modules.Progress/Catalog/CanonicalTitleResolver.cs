using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Resolves a game's canonical English search title following the M3 waterfall:
/// User override → permanent <c>trainer_title_map</c> cache → Steam <c>appmanifest_&lt;id&gt;.acf</c> <c>"name"</c> →
/// lazy <c>store.steampowered.com/api/appdetails?appids=&lt;id&gt;&amp;l=english&amp;filters=basic</c> →
/// GOG <c>goggame-*.info</c> <c>"name"</c> → clean-room denoiser fallback.
/// Never alters <see cref="GameRecord.Title"/> so localized Chinese UI display titles remain untouched.
/// </summary>
public sealed partial class CanonicalTitleResolver
{
    private readonly SpawnDatabase _database;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CanonicalTitleResolver> _logger;
    private readonly TimeSpan _steamLookupTimeout;

    [GeneratedRegex(@"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfInstallDirRegex();

    [GeneratedRegex(@"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfNameRegex();

    [GeneratedRegex(@"^goggame-(\d+)\.info$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GogInfoFileNameRegex();

    [GeneratedRegex(@"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF]", RegexOptions.CultureInvariant)]
    private static partial Regex CjkRegex();

    [GeneratedRegex(@"[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex LatinLetterRegex();

    [GeneratedRegex(@"^(?:\s*(?:\[[^\]]*\]|【[^】]*】|\(\d{4}[\.\-_]?\d{2}[\.\-_]?\d{2}\)|\(\d{6,8}\))\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingBracketRegex();

    [GeneratedRegex(@"(?:\s*(?:\[[^\]]*\]|【[^】]*】)\s*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingBracketRegex();

    [GeneratedRegex(@"(?i)(?:[\s\._\-]+|^)(?:v?\d+(?:\.\d+){1,4}[a-z]?|ver\.?\s*\d+(?:\.\d+)*|build[\s\._\-]*\d+|r\d{4,}|update[\s\._\-]*\d+|hotfix[\s\._\-]*\d+)\b.*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTailRegex();

    [GeneratedRegex(@"(?i)(?:[\s\._\-]+)(?:steamrip(?:\.com)?|fitgirl(?:[\s\._\-]*repacks?)?|dodi(?:[\s\._\-]*repacks?)?|elamigos|tenoke|rune|codex|plaza|flt|fairlight|empress|goldberg|skidrow|razor1911|hoodlum|ali213|3dm(?:game)?|gog(?:\.com)?|repack|portable|pre[\s\._\-]*installed|goty|game[\s\._\-]+of[\s\._\-]+the[\s\._\-]+year(?:[\s\._\-]+edition)?|deluxe[\s\._\-]+edition|ultimate[\s\._\-]+edition|complete[\s\._\-]+edition|definitive[\s\._\-]+edition|anniversary[\s\._\-]+edition|免安装.*|绿色版.*|官中.*|简体中文.*|繁体中文.*|汉化.*|硬盘版.*|整合版.*|学习版.*|全DLC.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SceneTailRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    public CanonicalTitleResolver(
        SpawnDatabase database,
        HttpClient httpClient,
        TimeProvider? timeProvider = null,
        TimeSpan? steamLookupTimeout = null,
        ILogger<CanonicalTitleResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(httpClient);

        _database = database;
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _steamLookupTimeout = steamLookupTimeout ?? TimeSpan.FromSeconds(5);
        _logger = logger ?? NullLogger<CanonicalTitleResolver>.Instance;
    }

    /// <summary>
    /// Resolves the effective English search query and source for <paramref name="game"/>,
    /// caching resolved canonical English titles permanently in <c>trainer_title_map</c>.
    /// </summary>
    public async Task<(string EffectiveQuery, TrainerTitleSource Source, TrainerTitleMapRecord MapRecord)> ResolveAsync(
        GameRecord game,
        string? userSearchOverride = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        var existingMap = await _database.GetTrainerTitleMapAsync(game.GameId, ct).ConfigureAwait(false);
        string? trimmedOverride = string.IsNullOrWhiteSpace(userSearchOverride) ? null : userSearchOverride.Trim();
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // 1. Explicit user search override supplied on this call.
        if (!string.IsNullOrWhiteSpace(trimmedOverride))
        {
            var saved = new TrainerTitleMapRecord
            {
                GameId = game.GameId,
                EnglishTitle = !string.IsNullOrWhiteSpace(existingMap?.EnglishTitle) ? existingMap.EnglishTitle : trimmedOverride,
                TitleSource = existingMap is not null && existingMap.TitleSource != TrainerTitleSource.Unknown
                    ? existingMap.TitleSource
                    : TrainerTitleSource.User,
                UserSearchOverride = trimmedOverride,
                FlingPageUrl = existingMap?.FlingPageUrl,
                UpdatedAtUtc = now,
            };
            await _database.UpsertTrainerTitleMapAsync(saved, ct).ConfigureAwait(false);
            return (trimmedOverride, TrainerTitleSource.User, saved);
        }

        // 2. Existing user override saved previously in trainer_title_map.
        if (existingMap is not null && !string.IsNullOrWhiteSpace(existingMap.UserSearchOverride))
        {
            return (existingMap.UserSearchOverride.Trim(), TrainerTitleSource.User, existingMap);
        }

        // 3. Permanent high-confidence cache hit (AcfName / SteamAppDetails / GogInfo / User).
        if (existingMap is not null
            && !string.IsNullOrWhiteSpace(existingMap.EnglishTitle)
            && existingMap.TitleSource is TrainerTitleSource.AcfName
                or TrainerTitleSource.SteamAppDetails
                or TrainerTitleSource.GogInfo
                or TrainerTitleSource.User)
        {
            return (existingMap.EnglishTitle.Trim(), existingMap.TitleSource, existingMap);
        }

        string? englishTitle = null;
        TrainerTitleSource source = TrainerTitleSource.Cleaner;

        // 4. Steam appmanifest_<id>.acf "name".
        string? acfName = TryReadSteamAcfName(game.InstallDir, game.SteamAppId);
        if (!string.IsNullOrWhiteSpace(acfName) && IsLikelyEnglish(acfName))
        {
            englishTitle = acfName.Trim();
            source = TrainerTitleSource.AcfName;
        }

        // 5. Lazy Steam appdetails?appids=<id>&l=english&filters=basic (permanently cached once resolved).
        if (string.IsNullOrWhiteSpace(englishTitle) && game.SteamAppId.HasValue && game.SteamAppId.Value > 0)
        {
            string? steamEnglish = await TryQuerySteamEnglishNameAsync(game.SteamAppId.Value, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(steamEnglish))
            {
                englishTitle = steamEnglish;
                source = TrainerTitleSource.SteamAppDetails;
            }
        }

        // 6. GOG goggame-*.info "name".
        if (string.IsNullOrWhiteSpace(englishTitle))
        {
            string? gogName = TryReadGogInfoName(game.InstallDir);
            if (!string.IsNullOrWhiteSpace(gogName))
            {
                englishTitle = gogName.Trim();
                source = TrainerTitleSource.GogInfo;
            }
        }

        // 7. Clean-room title denoiser fallback (folder name / PE ProductName / GameRecord.Title).
        if (string.IsNullOrWhiteSpace(englishTitle))
        {
            string? folderName = string.IsNullOrWhiteSpace(game.InstallDir)
                ? null
                : Path.GetFileName(Path.TrimEndingDirectorySeparator(game.InstallDir));

            englishTitle = CleanEnglishFallback(folderName)
                ?? CleanEnglishFallback(TryReadPeProductName(game.ExecutableFullPath))
                ?? CleanEnglishFallback(game.Title)
                ?? (!string.IsNullOrWhiteSpace(acfName) ? acfName.Trim() : null)
                ?? game.Title.Trim();
            source = TrainerTitleSource.Cleaner;
        }

        var record = new TrainerTitleMapRecord
        {
            GameId = game.GameId,
            EnglishTitle = englishTitle,
            TitleSource = source,
            UserSearchOverride = existingMap?.UserSearchOverride,
            FlingPageUrl = existingMap?.FlingPageUrl,
            UpdatedAtUtc = now,
        };
        await _database.UpsertTrainerTitleMapAsync(record, ct).ConfigureAwait(false);

        return (record.EnglishTitle, record.TitleSource, record);
    }

    private async Task<string?> TryQuerySteamEnglishNameAsync(int appId, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(_steamLookupTimeout > TimeSpan.Zero ? _steamLookupTimeout : TimeSpan.FromSeconds(5));

        try
        {
            string url = $"https://store.steampowered.com/api/appdetails?appids={appId.ToString(CultureInfo.InvariantCulture)}&l=english&filters=basic";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            FlingUrlPolicy.ApplyHonestHeaders(req);

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linkedCts.Token).ConfigureAwait(false);
            string key = appId.ToString(CultureInfo.InvariantCulture);
            if (doc.RootElement.TryGetProperty(key, out JsonElement appNode)
                && appNode.TryGetProperty("success", out JsonElement okProp)
                && okProp.ValueKind == JsonValueKind.True
                && appNode.TryGetProperty("data", out JsonElement dataNode)
                && dataNode.TryGetProperty("name", out JsonElement nameProp)
                && nameProp.ValueKind == JsonValueKind.String)
            {
                string? name = nameProp.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Steam English appdetails query failed for AppId {AppId}.", appId);
            }
        }

        return null;
    }

    private static string? TryReadSteamAcfName(string? installDir, int? steamAppId)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
            var parentDir = Directory.GetParent(normalized);
            if (parentDir is null || !string.Equals(parentDir.Name, "common", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var steamAppsDir = parentDir.Parent;
            if (steamAppsDir is null || !string.Equals(steamAppsDir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string folderName = Path.GetFileName(normalized);
            if (steamAppId.HasValue && steamAppId.Value > 0)
            {
                string directPath = Path.Combine(steamAppsDir.FullName, $"appmanifest_{steamAppId.Value.ToString(CultureInfo.InvariantCulture)}.acf");
                if (File.Exists(directPath))
                {
                    Match nameMatch = AcfNameRegex().Match(File.ReadAllText(directPath));
                    if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                    {
                        return nameMatch.Groups[1].Value.Trim();
                    }
                }
            }

            foreach (string acfPath in Directory.EnumerateFiles(steamAppsDir.FullName, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(acfPath);
                Match installMatch = AcfInstallDirRegex().Match(text);
                if (!installMatch.Success
                    || !string.Equals(installMatch.Groups[1].Value, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match nameMatch = AcfNameRegex().Match(text);
                if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                {
                    return nameMatch.Groups[1].Value.Trim();
                }
            }
        }
        catch
        {
            // Ignore inaccessible Steam manifest paths.
        }

        return null;
    }

    private static string? TryReadGogInfoName(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(installDir, "goggame-*.info", SearchOption.TopDirectoryOnly))
            {
                if (!GogInfoFileNameRegex().IsMatch(Path.GetFileName(file)))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("name", out JsonElement nameProp)
                    && nameProp.ValueKind == JsonValueKind.String)
                {
                    string? name = nameProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed goggame-*.info files.
        }

        return null;
    }

    private static string? TryReadPeProductName(string? exeFullPath)
    {
        if (string.IsNullOrWhiteSpace(exeFullPath) || !File.Exists(exeFullPath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exeFullPath);
            string? product = info.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(product)
                && !product.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
                && !product.StartsWith("Unreal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(product, "Application", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(product, "Game", StringComparison.OrdinalIgnoreCase))
            {
                return product;
            }
        }
        catch
        {
            // Ignore malformed PE resources.
        }

        return null;
    }

    private static bool IsLikelyEnglish(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && LatinLetterRegex().IsMatch(value)
        && !CjkRegex().IsMatch(value);

    private static string? CleanEnglishFallback(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string working = raw.Trim();
        string[] parts = working.Split(['/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1)
        {
            foreach (string part in parts)
            {
                string? cleanedPart = CleanSingleSegment(part);
                if (IsLikelyEnglish(cleanedPart))
                {
                    return cleanedPart;
                }
            }
        }

        string? cleaned = CleanSingleSegment(working);
        if (IsLikelyEnglish(cleaned))
        {
            return cleaned;
        }

        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            string strippedCjk = CjkRegex().Replace(cleaned, " ");
            strippedCjk = MultiSpaceRegex().Replace(strippedCjk, " ").Trim(' ', '-', '_', '.', ':', '：', '–', '—');
            if (IsLikelyEnglish(strippedCjk) && strippedCjk.Length >= 2)
            {
                return strippedCjk;
            }
        }

        return null;
    }

    private static string? CleanSingleSegment(string input)
    {
        string working = input.Trim();
        if (working.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            working = working[..^4];
        }

        working = LeadingBracketRegex().Replace(working, string.Empty).Trim();
        working = TrailingBracketRegex().Replace(working, string.Empty).Trim();
        for (int i = 0; i < 3; i++)
        {
            string before = working;
            working = SceneTailRegex().Replace(working, string.Empty).Trim();
            working = VersionTailRegex().Replace(working, string.Empty).Trim();
            working = TrailingBracketRegex().Replace(working, string.Empty).Trim();
            if (string.Equals(before, working, StringComparison.Ordinal))
            {
                break;
            }
        }

        if (!working.Contains(' ') && (working.Contains('.') || working.Contains('_')))
        {
            working = working.Replace('.', ' ').Replace('_', ' ');
        }
        else if (working.Contains('_'))
        {
            working = working.Replace('_', ' ');
        }

        working = SceneTailRegex().Replace(working, string.Empty);
        working = VersionTailRegex().Replace(working, string.Empty);
        working = MultiSpaceRegex().Replace(working, " ").Trim(' ', '-', '_', '.', '–', '—');
        return string.IsNullOrWhiteSpace(working) ? null : working;
    }
}
