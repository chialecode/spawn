using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Categorizes why an upstream FLiNG HTTP response triggered immediate non-retry browser fallback.
/// </summary>
public enum FlingBlockReason
{
    None = 0,
    CloudflareChallenge = 1,
    HttpForbidden = 2,
    HttpServiceUnavailable = 3,
    UnexpectedHtmlPayload = 4,
}

/// <summary>
/// Clean-room URL, domain whitelist, promo-link filter, robots.txt compliance guard,
/// and Cloudflare challenge / HTML payload detector for FLiNG discovery and user-triggered downloads.
/// </summary>
public static partial class FlingUrlPolicy
{
    public const string MainHost = "flingtrainer.com";
    public const string ArchiveHost = "archive.flingtrainer.com";
    public const string AllTrainersIndexUrl = "https://flingtrainer.com/all-trainers/";
    public const string ArchiveIndexUrl = "https://archive.flingtrainer.com/";
    public const string WpJsonPostsEndpoint = "https://flingtrainer.com/wp-json/wp/v2/posts";

    /// <summary>
    /// Honest, transparent client User-Agent (never spoofs a desktop browser).
    /// </summary>
    public const string HonestUserAgent = "SPAWN-TrainerCatalog/1.0 (+https://github.com/chialecode/spawn)";

    public const string DefaultAcceptHeader = "*/*";
    public const string HtmlAcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    public const string JsonAcceptHeader = "application/json,text/plain;q=0.8,*/*;q=0.5";
    public const string DefaultAcceptLanguageHeader = "en-US,en;q=0.9";

    private const int TitleScanPrefixLength = 8192;
    private const int ShortInterstitialMaxLength = 16384;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        MainHost,
        ArchiveHost,
    };

    private static readonly string[] SubstringPromotionalTokens =
    [
        "wemod",
        "auto-updat",
        "autoupdat",
        "installer",
        "setup",
    ];

    private static readonly string[] DirectDownloadExtensions =
    [
        ".rar",
        ".zip",
        ".7z",
        ".exe",
    ];

    // Active Cloudflare / Turnstile interstitial DOM & script markers.
    // Deliberately excludes bare "challenge-platform" because Cloudflare injects the passive
    // "/cdn-cgi/challenge-platform/scripts/jsd/main.js" telemetry beacon on normal HTTP 200 pages.
    private static readonly string[] ActiveChallengeInterstitialMarkers =
    [
        "cf-browser-verification",
        "cf-challenge-running",
        "id=\"challenge-form\"",
        "id='challenge-form'",
        "window._cf_chl_opt",
        "_cf_chl_opt=",
        "enable javascript and cookies to continue",
        "challenge-error-text",
        "/cdn-cgi/challenge-platform/h/",
        "id=\"cf-wrapper\"",
        "id=\"cf-error-details\"",
        "challenges.cloudflare.com/turnstile",
        "checking if the site connection is secure",
    ];

    private static readonly string[] ShortStubChallengeMarkers =
    [
        "just a moment",
        "attention required!",
        "cf-challenge",
    ];

    // Matches the promotional app name "wand" only as a standalone token (avoiding false positives on "Wandering Sword").
    [GeneratedRegex(@"(?<![a-z0-9])wand(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WandPromotionalTokenRegex();

    [GeneratedRegex(@"<title[^>]*>(?<title>[\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTitleRegex();

    /// <summary>
    /// Validates that <paramref name="uri"/> uses HTTPS on a strictly whitelisted FLiNG domain,
    /// contains no promotional tokens, and does not violate FLiNG <c>robots.txt</c> disallowed paths/queries.
    /// </summary>
    public static bool IsAllowedFlingUri(Uri? uri, out string? rejectionReason)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            rejectionReason = "URL 必须是有效的绝对地址。";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"仅允许 HTTPS 协议，已拒绝 '{uri.Scheme}'。";
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            rejectionReason = $"域名 '{uri.Host}' 不在受信任的源站白名单内。";
            return false;
        }

        if (ContainsPromotionalToken(uri.AbsoluteUri))
        {
            rejectionReason = "已过滤推广或安装器链接。";
            return false;
        }

        if (IsDisallowedByRobotsPolicy(uri))
        {
            rejectionReason = "请求路径不符合源站 robots.txt 协议规范。";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    public static bool TryParseAllowedFlingUri(string? rawUrl, Uri? baseUri, out Uri? resolvedUri, out string? rejectionReason)
    {
        resolvedUri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            rejectionReason = "空链接。";
            return false;
        }

        string trimmed = rawUrl.Trim();
        Uri? parsed;
        if (baseUri is not null)
        {
            if (!Uri.TryCreate(baseUri, trimmed, out parsed))
            {
                rejectionReason = "无法解析相对或绝对链接。";
                return false;
            }
        }
        else if (!Uri.TryCreate(trimmed, UriKind.Absolute, out parsed))
        {
            rejectionReason = "无法解析绝对链接。";
            return false;
        }

        if (!IsAllowedFlingUri(parsed, out rejectionReason))
        {
            return false;
        }

        resolvedUri = parsed;
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="textOrUrl"/> contains blocked promotional / ad / installer markers
    /// (<c>wemod</c>, standalone <c>wand</c>, <c>auto-updat</c>, <c>installer</c>, <c>setup</c>).
    /// </summary>
    public static bool ContainsPromotionalToken(string? textOrUrl)
    {
        if (string.IsNullOrWhiteSpace(textOrUrl))
        {
            return false;
        }

        foreach (string token in SubstringPromotionalTokens)
        {
            if (textOrUrl.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return WandPromotionalTokenRegex().IsMatch(textOrUrl);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="uri"/> points directly to a downloadable trainer archive
    /// or executable (<c>.rar</c>, <c>.zip</c>, <c>.7z</c>, <c>.exe</c>), such as entries under
    /// <c>archive.flingtrainer.com/files/</c>.
    /// </summary>
    public static bool IsDirectDownloadFileUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        foreach (string ext in DirectDownloadExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enforces FLiNG <c>robots.txt</c> constraints: disallows WordPress root search query parameter <c>s</c>
    /// and disallows paths containing an <c>attachments</c> segment.
    /// </summary>
    public static bool IsDisallowedByRobotsPolicy(Uri uri)
    {
        if (uri is null)
        {
            return true;
        }

        // Check path segments for disallowed "attachments" endpoint.
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (string.Equals(seg, "attachments", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check query parameters for disallowed WordPress search parameter "s".
        if (!string.IsNullOrEmpty(uri.Query))
        {
            string query = uri.Query.TrimStart('?');
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair[..eq] : pair;
                if (string.Equals(key, "s", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a strictly same-origin Referer URI for a whitelisted FLiNG request target.
    /// Direct binary/archive file URLs normalize to their same-origin index root so <c>Referer</c>
    /// always references an HTML origin page.
    /// </summary>
    public static Uri BuildSameOriginReferer(Uri targetUri, string? preferredPageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(targetUri);

        if (!string.IsNullOrWhiteSpace(preferredPageUrl)
            && Uri.TryCreate(preferredPageUrl.Trim(), UriKind.Absolute, out Uri? preferred)
            && string.Equals(preferred.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(preferred.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase)
            && !IsDirectDownloadFileUri(preferred)
            && IsAllowedFlingUri(preferred, out _))
        {
            return preferred;
        }

        return new Uri($"https://{targetUri.Host}/", UriKind.Absolute);
    }

    /// <summary>
    /// Applies the honest SPAWN User-Agent and standard RFC Accept / Accept-Language / Referer headers
    /// to an outbound HTTP request (never spoofs a desktop browser).
    /// </summary>
    public static void ApplyHonestHeaders(HttpRequestMessage request, Uri? referer = null, string? accept = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", HonestUserAgent);

        if (request.Headers.Accept.Count == 0)
        {
            request.Headers.TryAddWithoutValidation("Accept", string.IsNullOrWhiteSpace(accept) ? DefaultAcceptHeader : accept);
        }

        if (request.Headers.AcceptLanguage.Count == 0)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", DefaultAcceptLanguageHeader);
        }

        if (referer is not null)
        {
            request.Headers.Referrer = referer;
        }
    }

    /// <summary>
    /// Classifies Cloudflare challenge, HTTP 403/503, or unexpected HTML responses from response headers
    /// before reading the full body.
    /// </summary>
    public static FlingBlockReason ClassifyBlockedResponse(HttpResponseMessage response, bool expectBinaryPayload = false)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Check cf-mitigated first so Cloudflare Managed Challenge (HTTP 403 + cf-mitigated: challenge)
        // is classified accurately as CloudflareChallenge rather than a generic 403.
        if (response.Headers.TryGetValues("cf-mitigated", out var cfValues))
        {
            foreach (string val in cfValues)
            {
                if (val.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                {
                    return FlingBlockReason.CloudflareChallenge;
                }
            }
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return FlingBlockReason.HttpForbidden;
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return FlingBlockReason.HttpServiceUnavailable;
        }

        if (expectBinaryPayload)
        {
            MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
            string? mediaType = contentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType)
                && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
            {
                return FlingBlockReason.UnexpectedHtmlPayload;
            }
        }

        return FlingBlockReason.None;
    }

    /// <summary>
    /// Detects Cloudflare challenge or HTTP 403/503 responses from response headers before reading full body.
    /// </summary>
    public static bool IsChallengeOrBlockedResponse(HttpResponseMessage response, bool expectBinaryPayload = false) =>
        ClassifyBlockedResponse(response, expectBinaryPayload) != FlingBlockReason.None;

    /// <summary>
    /// Builds clear, honest Chinese fallback guidance explaining why automatic fetching aborted without retry.
    /// </summary>
    public static string BuildFallbackMessage(FlingBlockReason reason, int statusCode, bool isDownload = false) =>
        reason switch
        {
            FlingBlockReason.CloudflareChallenge => isDownload
                ? $"下载触发 Cloudflare 人机验证（HTTP {statusCode}），已安全中止并清理暂存。请点击「在浏览器打开」完成验证并下载后，直接拖放文件至此窗口导入。"
                : $"源站触发 Cloudflare 人机验证（HTTP {statusCode}），已停止自动请求。请点击「在浏览器打开」下载后，直接拖放本地文件导入。",
            FlingBlockReason.HttpForbidden => isDownload
                ? "源站拒绝下载访问（HTTP 403，可能受 Cloudflare 防护、防盗链或当前网络节点限制），已安全中止并清理暂存。请点击「在浏览器打开」手动下载后直接拖放导入。"
                : "源站拒绝访问（HTTP 403，可能受 Cloudflare 防护或当前网络节点限制），已停止自动请求。请点击「在浏览器打开」手动下载后直接拖放导入。",
            FlingBlockReason.HttpServiceUnavailable => isDownload
                ? "源站暂时不可用或正在执行访问验证（HTTP 503），已安全中止下载。请稍后重试，或点击「在浏览器打开」下载后拖放导入。"
                : "源站暂时不可用或正在执行访问验证（HTTP 503），请稍后重试或在浏览器打开源页下载后拖放导入。",
            FlingBlockReason.UnexpectedHtmlPayload =>
                $"下载响应返回网页（HTML，HTTP {statusCode}）而非修改器二进制文件，已立即中止并清理暂存。请点击「在浏览器打开」下载后拖放导入。",
            _ => $"源站返回异常响应（HTTP {statusCode}），请在浏览器打开源页下载后拖放本地文件导入。",
        };

    /// <summary>
    /// Inspects the leading bytes of a response payload to detect Cloudflare challenge text or disguised HTML payloads.
    /// </summary>
    public static bool LooksLikeHtmlOrChallengePayload(ReadOnlySpan<byte> leadingBytes, bool rejectAnyHtml)
    {
        if (leadingBytes.IsEmpty)
        {
            return false;
        }

        // Skip optional UTF-8 BOM.
        int offset = 0;
        if (leadingBytes.Length >= 3
            && leadingBytes[0] == 0xEF
            && leadingBytes[1] == 0xBB
            && leadingBytes[2] == 0xBF)
        {
            offset = 3;
        }

        int inspectLen = Math.Min(leadingBytes.Length - offset, 4096);
        if (inspectLen <= 0)
        {
            return false;
        }

        string sample = Encoding.UTF8.GetString(leadingBytes.Slice(offset, inspectLen));
        string trimmed = sample.TrimStart();

        if (ContainsChallengeMarkerText(trimmed))
        {
            return true;
        }

        if (rejectAnyHtml)
        {
            if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<script", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<title", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks HTML/text content returned by a discovery endpoint for genuine Cloudflare challenge or WAF block
    /// interstitials while avoiding false positives on Cloudflare's passive JS Detection telemetry script
    /// (<c>/cdn-cgi/challenge-platform/scripts/jsd/main.js</c>) embedded in normal HTTP 200 pages.
    /// </summary>
    public static bool ContainsChallengeMarkerText(string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return false;
        }

        // 1. Inspect <title> in the document head prefix for well-known Cloudflare interstitial titles.
        string headPrefix = bodyText.Length > TitleScanPrefixLength
            ? bodyText[..TitleScanPrefixLength]
            : bodyText;

        Match titleMatch = HtmlTitleRegex().Match(headPrefix);
        if (titleMatch.Success)
        {
            string title = titleMatch.Groups["title"].Value.Trim();
            if (IsChallengeTitle(title))
            {
                return true;
            }
        }

        // 2. Check unambiguous active challenge / WAF interstitial DOM & script markers.
        foreach (string marker in ActiveChallengeInterstitialMarkers)
        {
            if (bodyText.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 3. For short interstitial/error shells without a normal site layout (e.g. minimal HTML or text),
        //    also detect compact challenge markers without false-positiving on large 100KB+ trainer catalogs.
        if (bodyText.Length <= ShortInterstitialMaxLength)
        {
            foreach (string stubMarker in ShortStubChallengeMarkers)
            {
                if (bodyText.Contains(stubMarker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsChallengeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return title.Contains("just a moment", StringComparison.OrdinalIgnoreCase)
            || title.Contains("attention required!", StringComparison.OrdinalIgnoreCase)
            || title.Contains("checking your browser", StringComparison.OrdinalIgnoreCase)
            || (title.Contains("please wait", StringComparison.OrdinalIgnoreCase)
                && title.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
            || (title.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                && title.Contains("cloudflare", StringComparison.OrdinalIgnoreCase));
    }
}
