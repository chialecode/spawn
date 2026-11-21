using System.Globalization;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core.AntiCheat;

/// <summary>
/// Persisted TTL cache payload for Steam Store <c>appdetails?filters=categories</c> responses.
/// </summary>
public sealed record CachedSteamAppCategories
{
    public int SteamAppId { get; init; }
    public IReadOnlyList<int> CategoryIds { get; init; } = [];
    public DateTimeOffset FetchedAtUtc { get; init; }
}

/// <summary>
/// Result of classifying a Steam title's store categories (Layer 3).
/// </summary>
public sealed record SteamCategoryClassificationResult
{
    public bool HasCategories { get; init; }
    public bool UsedFreshOrValidCache { get; init; }
    public bool UsedStaleCacheFallback { get; init; }
    public bool OfflineOrErrorFallback { get; init; }
    public bool? HasSinglePlayer { get; init; }
    public bool HasVac { get; init; }
    public bool HasMmo { get; init; }
    public bool IsMultiplayerOnly { get; init; }
    public AntiCheatTier FloorTier { get; init; } = AntiCheatTier.Tier0;
    public IReadOnlyList<int> CategoryIds { get; init; } = [];
    public IReadOnlyList<string> Indicators { get; init; } = [];

    public static SteamCategoryClassificationResult NotApplicable { get; } = new();
}

/// <summary>
/// Layer 3 classifier: queries Steam Store <c>appdetails?appids=&lt;id&gt;&amp;filters=categories</c>,
/// persists results in <see cref="SpawnDatabase"/> with a configurable TTL, and enforces the
/// <c>VAC (id=8)</c> / <c>MMO (id=20)</c> / missing <c>Single-player (id=2)</c> &gt;= <see cref="AntiCheatTier.Tier2"/> floor.
/// Accepts an injectable <see cref="HttpMessageHandler"/> for 100% offline deterministic unit tests.
/// </summary>
public sealed class SteamAppCategoryClassifier : IDisposable
{
    public const int SinglePlayerCategoryId = 2;
    public const int VacCategoryId = 8;
    public const int MmoCategoryId = 20;

    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(4);

    private readonly SpawnDatabase? _database;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _requestTimeout;

    public SteamAppCategoryClassifier(
        SpawnDatabase? database = null,
        HttpMessageHandler? httpMessageHandler = null,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheTtl = null,
        TimeSpan? requestTimeout = null)
    {
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = cacheTtl is { Ticks: > 0 } ttl ? ttl : DefaultCacheTtl;
        _requestTimeout = requestTimeout is { Ticks: > 0 } timeout ? timeout : DefaultRequestTimeout;

        if (httpMessageHandler is not null)
        {
            _httpClient = new HttpClient(httpMessageHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }

        _httpClient.Timeout = _requestTimeout;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnAntiCheatGate/1.0 (Windows NT 10.0; Win64; x64)");
    }

    public static string BuildCacheKey(int steamAppId) =>
        string.Create(CultureInfo.InvariantCulture, $"steam:appdetails:categories:{steamAppId}");

    public async Task<SteamCategoryClassificationResult> ClassifyAsync(
        int steamAppId,
        bool allowOnlineLookup = true,
        CancellationToken cancellationToken = default)
    {
        if (steamAppId <= 0)
        {
            return SteamCategoryClassificationResult.NotApplicable;
        }

        string cacheKey = BuildCacheKey(steamAppId);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        CachedSteamAppCategories? cached = null;

        if (_database is not null)
        {
            try
            {
                cached = await _database.GetCacheJsonAsync<CachedSteamAppCategories>(
                    cacheKey,
                    maxAge: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                cached = null;
            }
        }

        if (cached is not null && cached.FetchedAtUtc <= now + TimeSpan.FromMinutes(5) && now - cached.FetchedAtUtc <= _cacheTtl)
        {
            return BuildResultFromCategoryIds(
                cached.CategoryIds,
                usedFreshOrValidCache: true,
                usedStaleCacheFallback: false,
                offlineOrErrorFallback: false);
        }

        if (!allowOnlineLookup)
        {
            return BuildOfflineFallback(cached);
        }

        try
        {
            var fetchedIds = await FetchCategoryIdsAsync(steamAppId, cancellationToken).ConfigureAwait(false);
            if (fetchedIds is not null)
            {
                var cacheEntry = new CachedSteamAppCategories
                {
                    SteamAppId = steamAppId,
                    CategoryIds = fetchedIds,
                    FetchedAtUtc = _timeProvider.GetUtcNow(),
                };

                if (_database is not null)
                {
                    try
                    {
                        await _database.SetCacheJsonAsync(cacheKey, cacheEntry, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Cache write failure is non-fatal; return live classification result.
                    }
                }

                return BuildResultFromCategoryIds(
                    fetchedIds,
                    usedFreshOrValidCache: true,
                    usedStaleCacheFallback: false,
                    offlineOrErrorFallback: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Network unavailable, timeout, HTTP error, or malformed response: fall back conservatively.
        }

        return BuildOfflineFallback(cached);
    }

    private async Task<IReadOnlyList<int>?> FetchCategoryIdsAsync(int steamAppId, CancellationToken cancellationToken)
    {
        string url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://store.steampowered.com/api/appdetails?appids={steamAppId}&filters=categories");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        string appKey = steamAppId.ToString(CultureInfo.InvariantCulture);
        if (!doc.RootElement.TryGetProperty(appKey, out JsonElement appNode)
            || appNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!appNode.TryGetProperty("success", out JsonElement successProp)
            || successProp.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        if (!appNode.TryGetProperty("data", out JsonElement dataNode)
            || dataNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!dataNode.TryGetProperty("categories", out JsonElement categoriesNode)
            || categoriesNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (JsonElement item in categoriesNode.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out JsonElement idElem)
                && idElem.TryGetInt32(out int categoryId)
                && seen.Add(categoryId))
            {
                ids.Add(categoryId);
            }
        }

        return ids;
    }

    private static SteamCategoryClassificationResult BuildOfflineFallback(CachedSteamAppCategories? staleCache)
    {
        if (staleCache is not null && staleCache.CategoryIds.Count > 0)
        {
            return BuildResultFromCategoryIds(
                staleCache.CategoryIds,
                usedFreshOrValidCache: false,
                usedStaleCacheFallback: true,
                offlineOrErrorFallback: true);
        }

        return new SteamCategoryClassificationResult
        {
            HasCategories = false,
            UsedFreshOrValidCache = false,
            UsedStaleCacheFallback = false,
            OfflineOrErrorFallback = true,
            HasSinglePlayer = null,
            FloorTier = AntiCheatTier.Tier0,
        };
    }

    private static SteamCategoryClassificationResult BuildResultFromCategoryIds(
        IReadOnlyList<int> categoryIds,
        bool usedFreshOrValidCache,
        bool usedStaleCacheFallback,
        bool offlineOrErrorFallback)
    {
        if (categoryIds.Count == 0)
        {
            return new SteamCategoryClassificationResult
            {
                HasCategories = false,
                UsedFreshOrValidCache = usedFreshOrValidCache,
                UsedStaleCacheFallback = usedStaleCacheFallback,
                OfflineOrErrorFallback = offlineOrErrorFallback,
                HasSinglePlayer = null,
                CategoryIds = categoryIds,
                FloorTier = AntiCheatTier.Tier0,
            };
        }

        var idSet = new HashSet<int>(categoryIds);
        bool hasSinglePlayer = idSet.Contains(SinglePlayerCategoryId);
        bool hasVac = idSet.Contains(VacCategoryId);
        bool hasMmo = idSet.Contains(MmoCategoryId);
        bool isMultiplayerOnly = !hasSinglePlayer;

        var indicators = new List<string>();
        var floorTier = AntiCheatTier.Tier0;

        if (hasVac)
        {
            indicators.Add("SteamCategory:VAC(8)");
            floorTier = AntiCheatTier.Tier2;
        }

        if (hasMmo)
        {
            indicators.Add("SteamCategory:MMO(20)");
            floorTier = AntiCheatTier.Tier2;
        }

        if (isMultiplayerOnly)
        {
            indicators.Add("SteamCategory:NoSinglePlayer");
            floorTier = AntiCheatTier.Tier2;
        }

        return new SteamCategoryClassificationResult
        {
            HasCategories = true,
            UsedFreshOrValidCache = usedFreshOrValidCache,
            UsedStaleCacheFallback = usedStaleCacheFallback,
            OfflineOrErrorFallback = offlineOrErrorFallback,
            HasSinglePlayer = hasSinglePlayer,
            HasVac = hasVac,
            HasMmo = hasMmo,
            IsMultiplayerOnly = isMultiplayerOnly,
            FloorTier = floorTier,
            CategoryIds = categoryIds,
            Indicators = indicators,
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
