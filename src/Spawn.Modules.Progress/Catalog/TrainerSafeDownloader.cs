using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Tuning options for <see cref="TrainerSafeDownloader"/> and <see cref="TrainerCatalogService"/>.
/// Defaults follow progress-saves-and-trainer.md §4.2 (10 redirects, 15s connect, 30s read stall, 100 MB cap).
/// </summary>
public sealed class TrainerCatalogOptions
{
    public const int DefaultMaxRedirects = 10;
    public const long DefaultMaxDownloadBytes = 100L * 1024 * 1024; // 100 MB

    public int MaxRedirects { get; set; } = DefaultMaxRedirects;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReadStallTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public long MaxDownloadBytes { get; set; } = DefaultMaxDownloadBytes;
    public long MaxUncompressedBytes { get; set; } = 512L * 1024 * 1024; // 512 MB zip-bomb safety cap
    public int MaxArchiveEntryCount { get; set; } = 500;
}

/// <summary>
/// Outcome of streaming a user-triggered trainer download into a temporary staging file.
/// </summary>
public sealed record TrainerDownloadOutcome(
    bool Succeeded,
    TrainerImportFailureKind FailureKind,
    string? StagedFilePath,
    string? SuggestedFileName,
    long BytesReceived,
    string? FallbackBrowserUrl = null,
    int? Win32ErrorCode = null,
    string? Message = null);

/// <summary>
/// User-triggered single-download HTTP client strictly enforcing:
/// <list type="bullet">
///   <item><c>AllowAutoRedirect = false</c> with manual redirect handling up to 10 hops;</item>
///   <item>Mandatory HTTPS (rejects any HTTP downgrade) and FLiNG domain whitelist on every hop;</item>
///   <item>Same-origin <c>Referer</c> and honest non-spoofed User-Agent;</item>
///   <item>15s connect/header timeout, 30s per-read stall timeout, and 100 MB streaming byte cap;</item>
///   <item>Immediate abort + staging deletion on 403/503, <c>cf-mitigated: challenge</c>, <c>Just a moment...</c>, or HTML payload.</item>
/// </list>
/// </summary>
public sealed class TrainerSafeDownloader
{
    private readonly HttpClient _httpClient;
    private readonly TrainerCatalogOptions _options;
    private readonly ILogger<TrainerSafeDownloader> _logger;

    public TrainerSafeDownloader(
        HttpClient? httpClient = null,
        TrainerCatalogOptions? options = null,
        ILogger<TrainerSafeDownloader>? logger = null)
    {
        _options = options ?? new TrainerCatalogOptions();
        _httpClient = httpClient ?? CreateNonRedirectingHttpClient(_options.ConnectTimeout);
        _logger = logger ?? NullLogger<TrainerSafeDownloader>.Instance;
    }

    /// <summary>
    /// Creates a production <see cref="HttpClient"/> with <c>AllowAutoRedirect = false</c>,
    /// explicit per-download cookie management (<c>UseCookies = false</c>), <c>ConnectTimeout</c>,
    /// and bounded pooled connection lifetime to avoid stale edge connections.
    /// </summary>
    public static HttpClient CreateNonRedirectingHttpClient(TimeSpan? connectTimeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Downloads a single user-selected attachment URL into <paramref name="stagingDirectory"/>.
    /// Any failure deletes the partially written staging file immediately before returning.
    /// </summary>
    public async Task<TrainerDownloadOutcome> DownloadToStagingAsync(
        string downloadUrl,
        string? sourcePageUrl,
        string stagingDirectory,
        IProgress<TrainerDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

        string fallbackUrl = !string.IsNullOrWhiteSpace(sourcePageUrl) ? sourcePageUrl : downloadUrl;

        if (!FlingUrlPolicy.TryParseAllowedFlingUri(downloadUrl, baseUri: null, out Uri? currentUri, out string? initReject)
            || currentUri is null)
        {
            return new TrainerDownloadOutcome(
                Succeeded: false,
                FailureKind: TrainerImportFailureKind.NetworkError,
                StagedFilePath: null,
                SuggestedFileName: null,
                BytesReceived: 0,
                FallbackBrowserUrl: fallbackUrl,
                Message: initReject ?? "下载链接未通过安全白名单校验。");
        }

        Directory.CreateDirectory(stagingDirectory);
        string stagedPath = Path.Combine(stagingDirectory, "download.part");
        int maxRedirects = Math.Max(0, _options.MaxRedirects);
        long maxBytes = Math.Max(1, _options.MaxDownloadBytes);

        try
        {
            HttpResponseMessage? finalResponse = null;
            Uri finalUri = currentUri;
            var cookieJar = new CookieContainer();

            for (int hop = 0; ; hop++)
            {
                ct.ThrowIfCancellationRequested();

                if (!FlingUrlPolicy.IsAllowedFlingUri(currentUri, out string? hopReject))
                {
                    return new TrainerDownloadOutcome(
                        Succeeded: false,
                        FailureKind: TrainerImportFailureKind.NetworkError,
                        StagedFilePath: null,
                        SuggestedFileName: null,
                        BytesReceived: 0,
                        FallbackBrowserUrl: fallbackUrl,
                        Message: hopReject ?? "重定向目标未通过 HTTPS 或源站白名单校验。");
                }

                Uri refererUri = FlingUrlPolicy.BuildSameOriginReferer(currentUri, sourcePageUrl);
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                FlingUrlPolicy.ApplyHonestHeaders(request, refererUri, FlingUrlPolicy.DefaultAcceptHeader);

                string cookieHeader = cookieJar.GetCookieHeader(currentUri);
                if (!string.IsNullOrEmpty(cookieHeader))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                }

                HttpResponseMessage response;
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(_options.ConnectTimeout > TimeSpan.Zero
                        ? _options.ConnectTimeout
                        : TimeSpan.FromSeconds(15));
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                        .ConfigureAwait(false);
                }

                // Immediate abort on 403/503, cf-mitigated: challenge, or HTML Content-Type (zero retries).
                FlingBlockReason blockReason = FlingUrlPolicy.ClassifyBlockedResponse(
                    response,
                    expectBinaryPayload: !IsRedirectStatus(response.StatusCode));
                if (blockReason != FlingBlockReason.None)
                {
                    int statusCode = (int)response.StatusCode;
                    response.Dispose();
                    TryDeleteFile(stagedPath);
                    return new TrainerDownloadOutcome(
                        Succeeded: false,
                        FailureKind: TrainerImportFailureKind.ChallengeOrHtmlFallback,
                        StagedFilePath: null,
                        SuggestedFileName: null,
                        BytesReceived: 0,
                        FallbackBrowserUrl: fallbackUrl,
                        Message: FlingUrlPolicy.BuildFallbackMessage(blockReason, statusCode, isDownload: true));
                }

                if (IsRedirectStatus(response.StatusCode))
                {
                    StoreResponseCookies(cookieJar, currentUri, response);
                    bool hasLocation = TryResolveRedirectLocation(response, currentUri, out Uri? nextUri);
                    response.Dispose();

                    if (!hasLocation || nextUri is null)
                    {
                        return new TrainerDownloadOutcome(
                            Succeeded: false,
                            FailureKind: TrainerImportFailureKind.NetworkError,
                            StagedFilePath: null,
                            SuggestedFileName: null,
                            BytesReceived: 0,
                            FallbackBrowserUrl: fallbackUrl,
                            Message: "重定向响应缺少有效的 Location 头。");
                    }

                    if (hop >= maxRedirects)
                    {
                        return new TrainerDownloadOutcome(
                            Succeeded: false,
                            FailureKind: TrainerImportFailureKind.NetworkError,
                            StagedFilePath: null,
                            SuggestedFileName: null,
                            BytesReceived: 0,
                            FallbackBrowserUrl: fallbackUrl,
                            Message: $"重定向次数超过上限（{maxRedirects} 跳），已中止下载。");
                    }

                    if (!string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        return new TrainerDownloadOutcome(
                            Succeeded: false,
                            FailureKind: TrainerImportFailureKind.NetworkError,
                            StagedFilePath: null,
                            SuggestedFileName: null,
                            BytesReceived: 0,
                            FallbackBrowserUrl: fallbackUrl,
                            Message: $"检测到重定向协议降级（{nextUri.Scheme}），已拒绝继续下载。");
                    }

                    if (!FlingUrlPolicy.IsAllowedFlingUri(nextUri, out string? nextReject))
                    {
                        return new TrainerDownloadOutcome(
                            Succeeded: false,
                            FailureKind: TrainerImportFailureKind.NetworkError,
                            StagedFilePath: null,
                            SuggestedFileName: null,
                            BytesReceived: 0,
                            FallbackBrowserUrl: fallbackUrl,
                            Message: nextReject ?? "重定向至非白名单域名或推广链接，已拒绝。");
                    }

                    currentUri = nextUri;
                    finalUri = nextUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    response.Dispose();
                    return new TrainerDownloadOutcome(
                        Succeeded: false,
                        FailureKind: TrainerImportFailureKind.NetworkError,
                        StagedFilePath: null,
                        SuggestedFileName: null,
                        BytesReceived: 0,
                        FallbackBrowserUrl: fallbackUrl,
                        Message: $"下载请求返回 HTTP {statusCode}。");
                }

                finalResponse = response;
                break;
            }

            using (finalResponse)
            {
                long? declaredLength = finalResponse.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value > maxBytes)
                {
                    return new TrainerDownloadOutcome(
                        Succeeded: false,
                        FailureKind: TrainerImportFailureKind.SizeLimitExceeded,
                        StagedFilePath: null,
                        SuggestedFileName: null,
                        BytesReceived: 0,
                        FallbackBrowserUrl: fallbackUrl,
                        Message: $"文件体积 ({declaredLength.Value} 字节) 超过 {maxBytes / (1024 * 1024)} MB 安全上限。");
                }

                string suggestedName = ExtractSuggestedFileName(finalResponse, finalUri);
                await using Stream networkStream = await finalResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(
                    stagedPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 65536,
                    useAsync: true);

                byte[] buffer = new byte[65536];
                long totalReceived = 0;
                bool inspectedHeader = false;
                TimeSpan stallTimeout = _options.ReadStallTimeout > TimeSpan.Zero
                    ? _options.ReadStallTimeout
                    : TimeSpan.FromSeconds(30);

                while (true)
                {
                    int read;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        readCts.CancelAfter(stallTimeout);
                        read = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token)
                            .ConfigureAwait(false);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    if (!inspectedHeader)
                    {
                        inspectedHeader = true;
                        if (FlingUrlPolicy.LooksLikeHtmlOrChallengePayload(buffer.AsSpan(0, read), rejectAnyHtml: true))
                        {
                            await fileStream.DisposeAsync().ConfigureAwait(false);
                            TryDeleteFile(stagedPath);
                            return new TrainerDownloadOutcome(
                                Succeeded: false,
                                FailureKind: TrainerImportFailureKind.ChallengeOrHtmlFallback,
                                StagedFilePath: null,
                                SuggestedFileName: null,
                                BytesReceived: 0,
                                FallbackBrowserUrl: fallbackUrl,
                                Message: "下载流内容为 HTML 或 Cloudflare 验证页，已立即中止并清理暂存，请改用浏览器下载。");
                        }
                    }

                    totalReceived += read;
                    if (totalReceived > maxBytes)
                    {
                        await fileStream.DisposeAsync().ConfigureAwait(false);
                        TryDeleteFile(stagedPath);
                        return new TrainerDownloadOutcome(
                            Succeeded: false,
                            FailureKind: TrainerImportFailureKind.SizeLimitExceeded,
                            StagedFilePath: null,
                            SuggestedFileName: null,
                            BytesReceived: totalReceived,
                            FallbackBrowserUrl: fallbackUrl,
                            Message: $"流式下载体积超过 {maxBytes} 字节上限，已中止并删除暂存。");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(new TrainerDownloadProgress
                    {
                        BytesReceived = totalReceived,
                        TotalBytes = declaredLength,
                        StageLabel = "正在下载修改器",
                    });
                }

                await fileStream.FlushAsync(ct).ConfigureAwait(false);
                fileStream.Flush(flushToDisk: true);

                if (totalReceived == 0)
                {
                    await fileStream.DisposeAsync().ConfigureAwait(false);
                    TryDeleteFile(stagedPath);
                    return new TrainerDownloadOutcome(
                        Succeeded: false,
                        FailureKind: TrainerImportFailureKind.InvalidMagicOrFormat,
                        StagedFilePath: null,
                        SuggestedFileName: null,
                        BytesReceived: 0,
                        FallbackBrowserUrl: fallbackUrl,
                        Message: "下载返回空文件。");
                }

                return new TrainerDownloadOutcome(
                    Succeeded: true,
                    FailureKind: TrainerImportFailureKind.None,
                    StagedFilePath: stagedPath,
                    SuggestedFileName: suggestedName,
                    BytesReceived: totalReceived);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDeleteFile(stagedPath);
            return new TrainerDownloadOutcome(
                Succeeded: false,
                FailureKind: TrainerImportFailureKind.Cancelled,
                StagedFilePath: null,
                SuggestedFileName: null,
                BytesReceived: 0,
                FallbackBrowserUrl: fallbackUrl,
                Message: "用户已取消下载。");
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(stagedPath);
            return new TrainerDownloadOutcome(
                Succeeded: false,
                FailureKind: TrainerImportFailureKind.NetworkError,
                StagedFilePath: null,
                SuggestedFileName: null,
                BytesReceived: 0,
                FallbackBrowserUrl: fallbackUrl,
                Message: "下载连接或读取流停滞超时，已清理暂存。");
        }
        catch (Exception ex) when (PeBinaryInspector.ClassifyOsSecurityError(PeBinaryInspector.TryExtractWin32ErrorCode(ex)) != TrainerImportFailureKind.None)
        {
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
            TryDeleteFile(stagedPath);
            return new TrainerDownloadOutcome(
                Succeeded: false,
                FailureKind: kind,
                StagedFilePath: null,
                SuggestedFileName: null,
                BytesReceived: 0,
                FallbackBrowserUrl: fallbackUrl,
                Win32ErrorCode: win32,
                Message: kind == TrainerImportFailureKind.AntivirusQuarantined
                    ? $"下载写入暂存时被杀毒软件拦截或隔离（Win32 {win32}）。"
                    : $"下载文件被 Smart App Control 策略阻止（Win32 {win32}）。");
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Trainer download failed for {Url}.", downloadUrl);
            }

            TryDeleteFile(stagedPath);
            return new TrainerDownloadOutcome(
                Succeeded: false,
                FailureKind: TrainerImportFailureKind.NetworkError,
                StagedFilePath: null,
                SuggestedFileName: null,
                BytesReceived: 0,
                FallbackBrowserUrl: fallbackUrl,
                Message: $"下载失败：{ex.Message}");
        }
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool TryResolveRedirectLocation(HttpResponseMessage response, Uri currentUri, out Uri? nextUri)
    {
        nextUri = null;
        Uri? location = response.Headers.Location;
        if (location is not null)
        {
            nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            return true;
        }

        if (!response.Headers.TryGetValues("Location", out IEnumerable<string>? rawValues))
        {
            return false;
        }

        string? raw = rawValues.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (Uri.TryCreate(raw, UriKind.RelativeOrAbsolute, out Uri? parsed))
        {
            nextUri = parsed.IsAbsoluteUri ? parsed : new Uri(currentUri, parsed);
            return true;
        }

        // Fallback for non-RFC raw redirect targets containing unescaped spaces or brackets in trainer filenames.
        string sanitized = raw
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("[", "%5B", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal);

        if (Uri.TryCreate(sanitized, UriKind.RelativeOrAbsolute, out Uri? sanitizedParsed))
        {
            nextUri = sanitizedParsed.IsAbsoluteUri ? sanitizedParsed : new Uri(currentUri, sanitizedParsed);
            return true;
        }

        return false;
    }

    private static void StoreResponseCookies(CookieContainer cookieJar, Uri originUri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookieValues))
        {
            return;
        }

        foreach (string rawSetCookie in setCookieValues)
        {
            if (string.IsNullOrWhiteSpace(rawSetCookie))
            {
                continue;
            }

            try
            {
                cookieJar.SetCookies(originUri, rawSetCookie);
            }
            catch (CookieException)
            {
                // Ignore malformed individual cookies rather than aborting a valid redirect hop.
            }
        }
    }

    private static string ExtractSuggestedFileName(HttpResponseMessage response, Uri finalUri)
    {
        string? raw = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            string cleaned = Path.GetFileName(raw.Trim().Trim('"'));
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        string uriName = Path.GetFileName(Uri.UnescapeDataString(finalUri.AbsolutePath));
        return string.IsNullOrWhiteSpace(uriName) ? "trainer.exe" : uriName;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of partial staging files.
        }
    }
}
