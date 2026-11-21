using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Clean-room FLiNG trainer discovery client strictly honoring FLiNG <c>robots.txt</c>:
/// <list type="bullet">
///   <item>24h disk-cached <c>https://flingtrainer.com/all-trainers/</c>;</item>
///   <item>Single <c>wp-json/wp/v2/posts?search=&lt;query&gt;&amp;per_page=&lt;n&gt;</c> query;</item>
///   <item>Optional 7-day disk-cached <c>https://archive.flingtrainer.com/</c> only when enabled in settings;</item>
///   <item>On-demand single detail page fetch only when opening/downloading a candidate;</item>
///   <item>Strict concurrency locked to 1 via <see cref="SemaphoreSlim"/>(1, 1);</item>
///   <item>Immediate non-retry fallback to browser on 403/503 or Cloudflare challenge.</item>
/// </list>
/// </summary>
public sealed partial class FlingCleanRoomDiscoveryClient : IDisposable
{
    public const string AllTrainersCacheFileName = "fling-all-trainers.html";
    public const string ArchiveIndexCacheFileName = "fling-archive-index.html";

    private static readonly TimeSpan AllTrainersCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ArchiveIndexCacheTtl = TimeSpan.FromDays(7);
    private const int WpJsonPerPage = 10;
    private const double MinMatchScoreThreshold = 0.48;

    private readonly SpawnPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FlingCleanRoomDiscoveryClient> _logger;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly TimeSpan _requestTimeout;

    [GeneratedRegex(@"\b(?:Trainer|Plus\s*\d+|v\d+(?:\.\d+)*|Early\s*Access|Updated|FLiNG)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrainerSuffixNoiseRegex();

    [GeneratedRegex(@"[^a-z0-9\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    public FlingCleanRoomDiscoveryClient(
        SpawnPaths paths,
        HttpClient httpClient,
        TimeProvider? timeProvider = null,
        TimeSpan? requestTimeout = null,
        ILogger<FlingCleanRoomDiscoveryClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(httpClient);

        _paths = paths;
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _logger = logger ?? NullLogger<FlingCleanRoomDiscoveryClient>.Instance;
    }

    public void Dispose()
    {
        _requestGate.Dispose();
    }

    /// <summary>
    /// Searches clean-room FLiNG sources for candidates matching <paramref name="canonicalEnglishQuery"/>.
    /// </summary>
    public async Task<TrainerMatchResult> SearchAsync(
        string gameId,
        string canonicalEnglishQuery,
        TrainerTitleSource querySource,
        bool includeArchive,
        GameVersionFingerprint? localFingerprint = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalEnglishQuery);

        string trimmedQuery = canonicalEnglishQuery.Trim();
        var candidatesByUrl = new Dictionary<string, TrainerCandidate>(StringComparer.OrdinalIgnoreCase);
        bool sawChallenge = false;
        string? challengeFallbackUrl = null;
        string? challengeMessage = null;
        string? networkWarning = null;

        // 1. 24h cached /all-trainers/ index.
        var allTrainersFetch = await GetCachedIndexHtmlAsync(
            FlingUrlPolicy.AllTrainersIndexUrl,
            Path.Combine(_paths.TrainerCacheDirectory, AllTrainersCacheFileName),
            AllTrainersCacheTtl,
            ct).ConfigureAwait(false);

        if (allTrainersFetch.ChallengeDetected)
        {
            sawChallenge = true;
            challengeFallbackUrl ??= FlingUrlPolicy.AllTrainersIndexUrl;
            challengeMessage ??= allTrainersFetch.ErrorMessage;
        }
        else if (!string.IsNullOrWhiteSpace(allTrainersFetch.Html))
        {
            var entries = FlingHtmlParser.ParseTrainerIndex(
                allTrainersFetch.Html,
                new Uri(FlingUrlPolicy.AllTrainersIndexUrl, UriKind.Absolute),
                TrainerProviderKind.FlingMain);

            foreach (var entry in entries)
            {
                double score = ComputeTitleMatchScore(trimmedQuery, entry.Title);
                if (score < MinMatchScoreThreshold)
                {
                    continue;
                }

                var (confidence, reason) = GameVersionFingerprintService.EvaluateConfidence(
                    entry.VersionLabel,
                    entry.UpdatedDateText,
                    localFingerprint);

                MergeCandidate(
                    candidatesByUrl,
                    new TrainerCandidate
                    {
                        CandidateId = BuildCandidateId(entry.PageUrl),
                        Provider = TrainerProviderKind.FlingMain,
                        Title = entry.Title,
                        PageUrl = entry.PageUrl,
                        VersionLabel = entry.VersionLabel,
                        UpdatedDateText = entry.UpdatedDateText,
                        TitleMatchScore = score,
                        VersionConfidence = confidence,
                        ConfidenceReason = reason,
                    });
            }
        }
        else if (!string.IsNullOrWhiteSpace(allTrainersFetch.ErrorMessage))
        {
            networkWarning = allTrainersFetch.ErrorMessage;
        }

        // 2. Single wp-json/wp/v2/posts?search=<query>&per_page=<n> request (no pagination crawl).
        if (!sawChallenge)
        {
            var wpResult = await QueryWpJsonPostsAsync(trimmedQuery, localFingerprint, ct).ConfigureAwait(false);
            if (wpResult.ChallengeDetected)
            {
                sawChallenge = true;
                challengeFallbackUrl ??= FlingUrlPolicy.AllTrainersIndexUrl;
                challengeMessage ??= wpResult.ErrorMessage;
            }
            else if (wpResult.Candidates.Count > 0)
            {
                foreach (var candidate in wpResult.Candidates)
                {
                    MergeCandidate(candidatesByUrl, candidate);
                }
            }
            else if (!string.IsNullOrWhiteSpace(wpResult.ErrorMessage))
            {
                networkWarning ??= wpResult.ErrorMessage;
            }
        }

        // 3. Optional 7-day cached archive.flingtrainer.com index (strictly opt-in via settings).
        if (includeArchive && !sawChallenge)
        {
            var archiveFetch = await GetCachedIndexHtmlAsync(
                FlingUrlPolicy.ArchiveIndexUrl,
                Path.Combine(_paths.TrainerCacheDirectory, ArchiveIndexCacheFileName),
                ArchiveIndexCacheTtl,
                ct).ConfigureAwait(false);

            if (archiveFetch.ChallengeDetected)
            {
                sawChallenge = true;
                challengeFallbackUrl ??= FlingUrlPolicy.ArchiveIndexUrl;
                challengeMessage ??= archiveFetch.ErrorMessage;
            }
            else if (!string.IsNullOrWhiteSpace(archiveFetch.Html))
            {
                var archiveEntries = FlingHtmlParser.ParseTrainerIndex(
                    archiveFetch.Html,
                    new Uri(FlingUrlPolicy.ArchiveIndexUrl, UriKind.Absolute),
                    TrainerProviderKind.FlingArchive);

                foreach (var entry in archiveEntries)
                {
                    double score = ComputeTitleMatchScore(trimmedQuery, entry.Title);
                    if (score < MinMatchScoreThreshold)
                    {
                        continue;
                    }

                    var (confidence, reason) = GameVersionFingerprintService.EvaluateConfidence(
                        entry.VersionLabel,
                        entry.UpdatedDateText,
                        localFingerprint);

                    TrainerAttachmentCandidate? directAttachment = null;
                    if (Uri.TryCreate(entry.PageUrl, UriKind.Absolute, out Uri? archiveEntryUri)
                        && FlingUrlPolicy.IsDirectDownloadFileUri(archiveEntryUri))
                    {
                        directAttachment = BuildDirectArchiveAttachment(
                            archiveEntryUri,
                            entry.Title,
                            entry.VersionLabel,
                            entry.UpdatedDateText,
                            entry.ApproximateSizeBytes,
                            confidence,
                            reason);
                    }

                    MergeCandidate(
                        candidatesByUrl,
                        new TrainerCandidate
                        {
                            CandidateId = BuildCandidateId(entry.PageUrl),
                            Provider = TrainerProviderKind.FlingArchive,
                            Title = entry.Title,
                            PageUrl = entry.PageUrl,
                            VersionLabel = entry.VersionLabel,
                            UpdatedDateText = entry.UpdatedDateText,
                            TitleMatchScore = score,
                            VersionConfidence = confidence,
                            ConfidenceReason = reason,
                            Attachments = directAttachment is not null ? [directAttachment] : [],
                            SelectedAttachment = directAttachment,
                        });
                }
            }
        }

        var ordered = candidatesByUrl.Values
            .OrderByDescending(c => c.TitleMatchScore)
            .ThenBy(c => c.Provider)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();

        if (sawChallenge && ordered.Count == 0)
        {
            return new TrainerMatchResult
            {
                GameId = gameId,
                EffectiveQuery = trimmedQuery,
                QuerySource = querySource,
                Succeeded = false,
                FallbackToBrowserRecommended = true,
                FallbackBrowserUrl = challengeFallbackUrl ?? FlingUrlPolicy.AllTrainersIndexUrl,
                Message = challengeMessage ?? "源站触发访问验证或返回 403/503，请在浏览器打开源页并拖放本地文件导入。",
                Candidates = [],
            };
        }

        return new TrainerMatchResult
        {
            GameId = gameId,
            EffectiveQuery = trimmedQuery,
            QuerySource = querySource,
            Succeeded = ordered.Count > 0 || string.IsNullOrWhiteSpace(networkWarning),
            FallbackToBrowserRecommended = sawChallenge,
            FallbackBrowserUrl = sawChallenge ? (challengeFallbackUrl ?? FlingUrlPolicy.AllTrainersIndexUrl) : null,
            Message = ordered.Count > 0
                ? $"已匹配到 {ordered.Count} 个候选修改器页面。"
                : (networkWarning ?? "未找到完全匹配的修改器，可调整英文搜索词或导入本地文件。"),
            Candidates = ordered,
        };
    }

    /// <summary>
    /// Fetches a single candidate's detail page HTML on demand (concurrency locked to 1) to populate
    /// filtered attachment download rows and structured preview hotkeys.
    /// Direct archive file entries (<c>archive.flingtrainer.com/files/*.rar</c>) synthesize their attachment
    /// immediately without fetching the binary payload as HTML.
    /// </summary>
    public async Task<(TrainerCandidate Candidate, bool ChallengeDetected, string? ErrorMessage)> LoadCandidateDetailsAsync(
        TrainerCandidate candidate,
        GameVersionFingerprint? localFingerprint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!FlingUrlPolicy.TryParseAllowedFlingUri(candidate.PageUrl, baseUri: null, out Uri? pageUri, out string? reason)
            || pageUri is null)
        {
            return (candidate, false, reason ?? "候选页面 URL 不在白名单内。");
        }

        // Archive index entries link directly to downloadable .rar/.zip/.7z/.exe files rather than HTML detail pages.
        if (candidate.Provider == TrainerProviderKind.FlingArchive && FlingUrlPolicy.IsDirectDownloadFileUri(pageUri))
        {
            var (confidence, confReason) = GameVersionFingerprintService.EvaluateConfidence(
                candidate.VersionLabel,
                candidate.UpdatedDateText,
                localFingerprint);

            TrainerAttachmentCandidate directAttachment = candidate.SelectedAttachment
                ?? (candidate.Attachments.Count > 0
                    ? candidate.Attachments[0]
                    : BuildDirectArchiveAttachment(
                        pageUri,
                        candidate.Title,
                        candidate.VersionLabel,
                        candidate.UpdatedDateText,
                        approximateSizeBytes: null,
                        confidence,
                        confReason));

            var directCandidate = candidate with
            {
                VersionConfidence = directAttachment.VersionConfidence,
                ConfidenceReason = directAttachment.ConfidenceReason,
                Attachments = [directAttachment],
                SelectedAttachment = directAttachment,
            };

            return (directCandidate, false, null);
        }

        var fetch = await FetchTextSerializedAsync(pageUri, FlingUrlPolicy.HtmlAcceptHeader, ct).ConfigureAwait(false);
        if (fetch.ChallengeDetected)
        {
            return (candidate, true, fetch.ErrorMessage ?? "详情页触发 Cloudflare 验证或返回 403/503，请在浏览器打开。");
        }

        if (string.IsNullOrWhiteSpace(fetch.Body))
        {
            return (candidate, false, fetch.ErrorMessage ?? "无法读取详情页内容。");
        }

        Uri effectivePageUri = fetch.FinalUri ?? pageUri;
        FlingDetailPageParseResult parsed = FlingHtmlParser.ParseDetailPage(
            fetch.Body,
            effectivePageUri,
            trainerIdPlaceholder: string.Empty,
            localFingerprint: localFingerprint);

        TrainerAttachmentCandidate? defaultSelected = candidate.SelectedAttachment;
        if (defaultSelected is null && parsed.Attachments.Count > 0)
        {
            defaultSelected = parsed.Attachments[0];
        }

        string? versionLabel = parsed.VersionLabel ?? candidate.VersionLabel;
        string? updatedDate = parsed.UpdatedDateText ?? candidate.UpdatedDateText;
        var (evalConfidence, evalConfReason) = GameVersionFingerprintService.EvaluateConfidence(
            versionLabel,
            updatedDate,
            localFingerprint);

        var enriched = candidate with
        {
            Title = !string.IsNullOrWhiteSpace(parsed.PageTitle) ? parsed.PageTitle : candidate.Title,
            PageUrl = effectivePageUri.AbsoluteUri,
            VersionLabel = versionLabel,
            UpdatedDateText = updatedDate,
            VersionConfidence = defaultSelected?.VersionConfidence ?? evalConfidence,
            ConfidenceReason = defaultSelected?.ConfidenceReason ?? evalConfReason,
            Attachments = parsed.Attachments,
            SelectedAttachment = defaultSelected,
            PreviewHotkeys = parsed.Hotkeys.Count > 0 ? parsed.Hotkeys : candidate.PreviewHotkeys,
        };

        return (enriched, false, null);
    }

    private async Task<(string? Html, bool ChallengeDetected, string? ErrorMessage)> GetCachedIndexHtmlAsync(
        string indexUrl,
        string cacheFilePath,
        TimeSpan ttl,
        CancellationToken ct)
    {
        if (TryReadFreshCacheFile(cacheFilePath, ttl, out string? cachedHtml))
        {
            return (cachedHtml, false, null);
        }

        if (!FlingUrlPolicy.TryParseAllowedFlingUri(indexUrl, baseUri: null, out Uri? uri, out string? reason) || uri is null)
        {
            return (null, false, reason);
        }

        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-checked cache read inside SemaphoreSlim(1,1) prevents thundering-herd duplicate index fetches.
            if (TryReadFreshCacheFile(cacheFilePath, ttl, out string? refreshedCache))
            {
                return (refreshedCache, false, null);
            }

            var fetched = await FetchTextCoreUnderLockAsync(uri, FlingUrlPolicy.HtmlAcceptHeader, ct).ConfigureAwait(false);
            if (fetched.ChallengeDetected)
            {
                return (null, true, fetched.ErrorMessage ?? "源站返回访问验证，已中止并切换浏览器回退。");
            }

            if (!string.IsNullOrWhiteSpace(fetched.Body))
            {
                TryWriteCacheFile(cacheFilePath, fetched.Body);
                return (fetched.Body, false, null);
            }

            // Offline resilience: if cache file exists even if stale, reuse it gracefully when network fails.
            if (File.Exists(cacheFilePath))
            {
                try
                {
                    return (await File.ReadAllTextAsync(cacheFilePath, Encoding.UTF8, ct).ConfigureAwait(false), false, null);
                }
                catch
                {
                    // Ignore read failure and propagate original error.
                }
            }

            return (null, false, fetched.ErrorMessage);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<(List<TrainerCandidate> Candidates, bool ChallengeDetected, string? ErrorMessage)> QueryWpJsonPostsAsync(
        string query,
        GameVersionFingerprint? localFingerprint,
        CancellationToken ct)
    {
        string wpUrl = $"{FlingUrlPolicy.WpJsonPostsEndpoint}?search={Uri.EscapeDataString(query)}&per_page={WpJsonPerPage.ToString(CultureInfo.InvariantCulture)}";
        if (!FlingUrlPolicy.TryParseAllowedFlingUri(wpUrl, baseUri: null, out Uri? uri, out string? reason) || uri is null)
        {
            return ([], false, reason);
        }

        var fetched = await FetchTextSerializedAsync(uri, FlingUrlPolicy.JsonAcceptHeader, ct).ConfigureAwait(false);
        if (fetched.ChallengeDetected)
        {
            return ([], true, fetched.ErrorMessage ?? "wp-json 接口触发访问验证。");
        }

        if (string.IsNullOrWhiteSpace(fetched.Body))
        {
            return ([], false, fetched.ErrorMessage);
        }

        var results = new List<TrainerCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(fetched.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ([], false, null);
            }

            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string? link = item.TryGetProperty("link", out JsonElement linkEl) && linkEl.ValueKind == JsonValueKind.String
                    ? linkEl.GetString()
                    : null;

                string? rawTitle = item.TryGetProperty("title", out JsonElement titleObj)
                                   && titleObj.ValueKind == JsonValueKind.Object
                                   && titleObj.TryGetProperty("rendered", out JsonElement renderedTitle)
                                   && renderedTitle.ValueKind == JsonValueKind.String
                    ? WebUtility.HtmlDecode(renderedTitle.GetString())
                    : null;

                if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(rawTitle))
                {
                    continue;
                }

                if (FlingUrlPolicy.ContainsPromotionalToken(link) || FlingUrlPolicy.ContainsPromotionalToken(rawTitle))
                {
                    continue;
                }

                if (!FlingUrlPolicy.TryParseAllowedFlingUri(link, baseUri: null, out Uri? resolvedLink, out _) || resolvedLink is null)
                {
                    continue;
                }

                double score = ComputeTitleMatchScore(query, rawTitle);
                if (score < MinMatchScoreThreshold)
                {
                    continue;
                }

                string? modified = item.TryGetProperty("modified_gmt", out JsonElement modEl) && modEl.ValueKind == JsonValueKind.String
                    ? modEl.GetString()
                    : (item.TryGetProperty("date_gmt", out JsonElement dateEl) && dateEl.ValueKind == JsonValueKind.String
                        ? dateEl.GetString()
                        : null);

                string? updatedDateText = GameVersionFingerprintService.TryParseDateOnly(modified)
                    ?.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

                string? contentHtml = item.TryGetProperty("content", out JsonElement contentObj)
                                      && contentObj.ValueKind == JsonValueKind.Object
                                      && contentObj.TryGetProperty("rendered", out JsonElement contentRendered)
                                      && contentRendered.ValueKind == JsonValueKind.String
                    ? contentRendered.GetString()
                    : null;

                IReadOnlyList<TrainerHotkeyBindingRecord> previewHotkeys = [];
                if (!string.IsNullOrWhiteSpace(contentHtml))
                {
                    var (hotkeys, _) = FlingHtmlParser.ParseHotkeysFromHtml(contentHtml);
                    previewHotkeys = hotkeys;
                }

                string? versionLabel = ExtractVersionFromTitle(rawTitle);
                var (confidence, confReason) = GameVersionFingerprintService.EvaluateConfidence(
                    versionLabel,
                    updatedDateText,
                    localFingerprint);

                results.Add(new TrainerCandidate
                {
                    CandidateId = BuildCandidateId(resolvedLink.AbsoluteUri),
                    Provider = string.Equals(resolvedLink.Host, FlingUrlPolicy.ArchiveHost, StringComparison.OrdinalIgnoreCase)
                        ? TrainerProviderKind.FlingArchive
                        : TrainerProviderKind.FlingMain,
                    Title = rawTitle.Trim(),
                    PageUrl = resolvedLink.AbsoluteUri,
                    VersionLabel = versionLabel,
                    UpdatedDateText = updatedDateText,
                    TitleMatchScore = score,
                    VersionConfidence = confidence,
                    ConfidenceReason = confReason,
                    PreviewHotkeys = previewHotkeys,
                });
            }
        }
        catch (JsonException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to parse FLiNG wp-json response.");
            }

            return ([], false, "解析源站 JSON 搜索结果失败。");
        }

        return (results, false, null);
    }

    private async Task<(string? Body, Uri? FinalUri, bool ChallengeDetected, string? ErrorMessage)> FetchTextSerializedAsync(
        Uri uri,
        string acceptHeader,
        CancellationToken ct)
    {
        if (!FlingUrlPolicy.IsAllowedFlingUri(uri, out string? rejection))
        {
            return (null, null, false, rejection);
        }

        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FetchTextCoreUnderLockAsync(uri, acceptHeader, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<(string? Body, Uri? FinalUri, bool ChallengeDetected, string? ErrorMessage)> FetchTextCoreUnderLockAsync(
        Uri uri,
        string acceptHeader,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_requestTimeout > TimeSpan.Zero ? _requestTimeout : TimeSpan.FromSeconds(15));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            FlingUrlPolicy.ApplyHonestHeaders(request, FlingUrlPolicy.BuildSameOriginReferer(uri), acceptHeader);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            FlingBlockReason blockReason = FlingUrlPolicy.ClassifyBlockedResponse(response, expectBinaryPayload: false);
            if (blockReason != FlingBlockReason.None)
            {
                return (null, null, true, FlingUrlPolicy.BuildFallbackMessage(blockReason, (int)response.StatusCode, isDownload: false));
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, null, false, $"源站返回 HTTP {(int)response.StatusCode}。");
            }

            Uri effectiveUri = response.RequestMessage?.RequestUri ?? uri;
            if (!FlingUrlPolicy.IsAllowedFlingUri(effectiveUri, out string? redirectReject))
            {
                return (null, null, false, redirectReject ?? "页面重定向至非白名单地址，已拒绝读取。");
            }

            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (FlingUrlPolicy.ContainsChallengeMarkerText(body))
            {
                return (null, null, true, FlingUrlPolicy.BuildFallbackMessage(FlingBlockReason.CloudflareChallenge, (int)response.StatusCode, isDownload: false));
            }

            return (body, effectiveUri, false, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null, false, "连接源站超时。");
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "HTTP request failed for {Uri}.", uri);
            }

            return (null, null, false, $"网络请求失败：{ex.Message}");
        }
    }

    private bool TryReadFreshCacheFile(string cachePath, TimeSpan ttl, out string? content)
    {
        content = null;
        string metaPath = cachePath + ".utc";
        try
        {
            if (!File.Exists(cachePath))
            {
                return false;
            }

            DateTimeOffset cachedAtUtc;
            if (File.Exists(metaPath)
                && DateTimeOffset.TryParse(
                    File.ReadAllText(metaPath, Encoding.UTF8).Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsedMeta))
            {
                cachedAtUtc = parsedMeta;
            }
            else
            {
                cachedAtUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath), TimeSpan.Zero);
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (now - cachedAtUtc > ttl || cachedAtUtc > now + TimeSpan.FromMinutes(5))
            {
                return false;
            }

            content = File.ReadAllText(cachePath, Encoding.UTF8);
            return !string.IsNullOrWhiteSpace(content);
        }
        catch
        {
            return false;
        }
    }

    private void TryWriteCacheFile(string cachePath, string content)
    {
        try
        {
            string? dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = cachePath + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, cachePath, overwrite: true);

            string metaTmp = cachePath + ".utc.tmp";
            File.WriteAllText(metaTmp, _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), Encoding.UTF8);
            File.Move(metaTmp, cachePath + ".utc", overwrite: true);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist FLiNG index cache file {CachePath}.", cachePath);
            }
        }
    }

    private static void MergeCandidate(
        Dictionary<string, TrainerCandidate> map,
        TrainerCandidate incoming)
    {
        string key = NormalizeUrlKey(incoming.PageUrl);
        if (!map.TryGetValue(key, out TrainerCandidate? existing))
        {
            map[key] = incoming;
            return;
        }

        IReadOnlyList<TrainerAttachmentCandidate> attachments = existing.Attachments.Count >= incoming.Attachments.Count
            ? existing.Attachments
            : incoming.Attachments;

        map[key] = existing with
        {
            Title = incoming.Title.Length > existing.Title.Length ? incoming.Title : existing.Title,
            VersionLabel = existing.VersionLabel ?? incoming.VersionLabel,
            UpdatedDateText = existing.UpdatedDateText ?? incoming.UpdatedDateText,
            TitleMatchScore = Math.Max(existing.TitleMatchScore, incoming.TitleMatchScore),
            Attachments = attachments,
            SelectedAttachment = existing.SelectedAttachment ?? incoming.SelectedAttachment ?? (attachments.Count > 0 ? attachments[0] : null),
            PreviewHotkeys = existing.PreviewHotkeys.Count >= incoming.PreviewHotkeys.Count
                ? existing.PreviewHotkeys
                : incoming.PreviewHotkeys,
        };
    }

    private static TrainerAttachmentCandidate BuildDirectArchiveAttachment(
        Uri archiveFileUri,
        string fallbackTitle,
        string? versionLabel,
        string? updatedDateText,
        long? approximateSizeBytes,
        TrainerVersionConfidence confidence,
        string? confidenceReason)
    {
        string fileName = Path.GetFileName(Uri.UnescapeDataString(archiveFileUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = string.IsNullOrWhiteSpace(fallbackTitle) ? "archive-trainer.rar" : fallbackTitle.Trim();
        }

        byte[] idHash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(archiveFileUri.AbsoluteUri.ToLowerInvariant()));

        return new TrainerAttachmentCandidate
        {
            AttachmentId = "arch_" + Convert.ToHexStringLower(idHash.AsSpan(0, 8)),
            FileName = fileName,
            DownloadUrl = archiveFileUri.AbsoluteUri,
            VersionLabel = versionLabel,
            UpdatedDateText = updatedDateText,
            ApproximateSizeBytes = approximateSizeBytes,
            VersionConfidence = confidence,
            ConfidenceReason = confidenceReason,
        };
    }

    private static string NormalizeUrlKey(string url) =>
        url.Trim().TrimEnd('/').ToLowerInvariant();

    private static string BuildCandidateId(string pageUrl)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeUrlKey(pageUrl)));
        return "cand_" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static string? ExtractVersionFromTitle(string title)
    {
        var parsed = GameVersionFingerprintService.ParseFlingVersionString(title);
        if (parsed.Kind == FlingVersionStringKind.Unknown)
        {
            return null;
        }

        Match m = Regex.Match(
            title,
            @"(v?\d+(?:\.\d+){1,3}\s*[-–—~]\s*v?20\d{6}|v?\d+(?:\.\d+){1,3}(?:\s*[-–—~]\s*v?\d+(?:\.\d+){1,3})?\+?\s+Updated\s+20\d{2}[.\-/]\d{1,2}[.\-/]\d{1,2}|v?\d+(?:\.\d+){1,3}\s*[-–—~]\s*v?\d+(?:\.\d+){1,3}\s+Updated\b|v?\d+(?:\.\d+){1,3}\s*[-–—~]\s*v?\d+(?:\.\d+){1,3}|v?\d+(?:\.\d+){1,3}\+|Early\s*Access\+?|v\d+(?:\.\d+){1,3})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Value.Trim() : null;
    }

    /// <summary>
    /// Clean-room title matcher combining normalized token overlap and Jaro-Winkler similarity.
    /// </summary>
    public static double ComputeTitleMatchScore(string query, string candidateTitle)
    {
        string normQ = NormalizeTitleForSearch(query);
        string normC = NormalizeTitleForSearch(candidateTitle);
        if (string.IsNullOrEmpty(normQ) || string.IsNullOrEmpty(normC))
        {
            return 0.0;
        }

        if (string.Equals(normQ, normC, StringComparison.Ordinal))
        {
            return 1.0;
        }

        string[] qTokens = normQ.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] cTokens = normC.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (qTokens.Length == 0 || cTokens.Length == 0)
        {
            return 0.0;
        }

        var cSet = new HashSet<string>(cTokens, StringComparer.Ordinal);
        int matched = qTokens.Count(t => cSet.Contains(t));
        double queryCoverage = (double)matched / qTokens.Length;
        double jaccard = (double)matched / (qTokens.Length + cTokens.Length - matched);

        string compactQ = normQ.Replace(" ", string.Empty, StringComparison.Ordinal);
        string compactC = normC.Replace(" ", string.Empty, StringComparison.Ordinal);
        double jw = ComputeJaroWinkler(compactQ, compactC);

        double bonus = 0.0;
        if (normC.Contains(normQ, StringComparison.Ordinal) || normQ.Contains(normC, StringComparison.Ordinal))
        {
            bonus = 0.12;
        }

        double combined = (queryCoverage * 0.48) + (jaccard * 0.22) + (jw * 0.30) + bonus;
        return Math.Clamp(combined, 0.0, 1.0);
    }

    private static string NormalizeTitleForSearch(string raw)
    {
        string cleaned = TrainerSuffixNoiseRegex().Replace(raw, " ");
        cleaned = NonAlphaNumRegex().Replace(cleaned.ToLowerInvariant(), " ");
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim();

        if (cleaned.StartsWith("the ", StringComparison.Ordinal))
        {
            cleaned = cleaned[4..].Trim();
        }

        // Normalize common Roman numeral sequel tokens to Arabic digits for consistent matching.
        string[] tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            tokens[i] = tokens[i] switch
            {
                "ii" => "2",
                "iii" => "3",
                "iv" => "4",
                "vi" => "6",
                "vii" => "7",
                "viii" => "8",
                _ => tokens[i],
            };
        }

        return string.Join(' ', tokens);
    }

    private static double ComputeJaroWinkler(string s1, string s2)
    {
        if (s1.Length == 0 && s2.Length == 0) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;
        if (string.Equals(s1, s2, StringComparison.Ordinal)) return 1.0;

        int matchDist = Math.Max(Math.Max(s1.Length, s2.Length) / 2 - 1, 0);
        bool[] m1 = new bool[s1.Length];
        bool[] m2 = new bool[s2.Length];

        int matches = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            int start = Math.Max(0, i - matchDist);
            int end = Math.Min(i + matchDist + 1, s2.Length);
            for (int j = start; j < end; j++)
            {
                if (!m2[j] && s1[i] == s2[j])
                {
                    m1[i] = true;
                    m2[j] = true;
                    matches++;
                    break;
                }
            }
        }

        if (matches == 0) return 0.0;

        double transpositions = 0;
        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!m1[i]) continue;
            while (!m2[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        double m = matches;
        double jaro = ((m / s1.Length) + (m / s2.Length) + ((m - (transpositions / 2.0)) / m)) / 3.0;
        int prefix = 0;
        int maxPrefix = Math.Min(4, Math.Min(s1.Length, s2.Length));
        while (prefix < maxPrefix && s1[prefix] == s2[prefix]) prefix++;
        return jaro + (prefix * 0.1 * (1.0 - jaro));
    }
}
