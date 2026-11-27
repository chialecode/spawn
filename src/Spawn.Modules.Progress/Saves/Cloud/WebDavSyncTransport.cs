using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Pure BCL <see cref="HttpClient"/> WebDAV transport (<c>MKCOL</c>, <c>PROPFIND</c> Depth:1, <c>PUT</c>,
/// <c>GET</c>, <c>GET Range</c>, <c>HEAD</c>) supporting Basic and Bearer authentication.
/// </summary>
internal sealed class WebDavSyncTransport : ICloudSyncTransport
{
    private static readonly HttpMethod PropfindMethod = new("PROPFIND");
    private static readonly HttpMethod MkcolMethod = new("MKCOL");
    private static readonly XNamespace DavNamespace = "DAV:";

    private const string PropfindRequestBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<d:propfind xmlns:d=\"DAV:\"><d:prop>"
        + "<d:getcontentlength/><d:getlastmodified/><d:resourcetype/>"
        + "</d:prop></d:propfind>";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _syncRootUri;
    private readonly AuthenticationHeaderValue? _authHeader;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, byte> _ensuredCollections = new(StringComparer.Ordinal);

    public WebDavSyncTransport(
        string endpointUrl,
        string? rootSubPath,
        CloudSyncEndpointSecrets? secrets,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _syncRootUri = BuildSyncRootUri(endpointUrl, rootSubPath);
        _authHeader = BuildAuthHeader(secrets);
    }

    public CloudSyncProviderType ProviderType => CloudSyncProviderType.WebDav;

    public async Task EnsureRepoInitializedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCollectionPathAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await EnsureCollectionPathAsync("packs", cancellationToken).ConfigureAwait(false);
        await EnsureCollectionPathAsync("snapshots", cancellationToken).ConfigureAwait(false);
        await EnsureCollectionPathAsync("heads", cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync(CloudSyncTransportHelpers.RepoMetadataRelativePath, cancellationToken).ConfigureAwait(false))
        {
            byte[] repoJson = CloudSyncTransportHelpers.CreateRepoMetadataJsonBytes(_timeProvider.GetUtcNow());
            await PutBytesAsync(
                CloudSyncTransportHelpers.RepoMetadataRelativePath,
                repoJson,
                "application/json",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PutBytesAsync(
        string relativePath,
        ReadOnlyMemory<byte> data,
        string contentType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        string normalized = CloudSyncTransportHelpers.NormalizeRelativePath(relativePath);
        int lastSlash = normalized.LastIndexOf('/');
        if (lastSlash > 0)
        {
            await EnsureCollectionPathAsync(normalized[..lastSlash], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await EnsureCollectionPathAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        Uri targetUri = BuildObjectUri(normalized);
        using var request = CreateRequest(HttpMethod.Put, targetUri);
        request.Content = new ByteArrayContent(data.ToArray());
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using HttpResponseMessage response = await CloudSyncHttpDiagnostics
            .SendWithDiagnosticsAsync(_httpClient, request, "WebDAV", "上传文件", cancellationToken)
            .ConfigureAwait(false);
        CloudSyncHttpDiagnostics.EnsureSuccess(response, "WebDAV", "上传文件");
    }

    public async Task<byte[]?> GetBytesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        string normalized = CloudSyncTransportHelpers.NormalizeRelativePath(relativePath);
        using var request = CreateRequest(HttpMethod.Get, BuildObjectUri(normalized));
        using HttpResponseMessage response = await CloudSyncHttpDiagnostics
            .SendWithDiagnosticsAsync(_httpClient, request, "WebDAV", "下载文件", cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        CloudSyncHttpDiagnostics.EnsureSuccess(response, "WebDAV", "下载文件");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudSyncRangeResult?> GetRangeAsync(
        string relativePath,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        string normalized = CloudSyncTransportHelpers.NormalizeRelativePath(relativePath);
        using var request = CreateRequest(HttpMethod.Get, BuildObjectUri(normalized));
        request.Headers.Range = CloudSyncTransportHelpers.CreateRangeHeader(offset, length);

        using HttpResponseMessage response = await CloudSyncHttpDiagnostics
            .SendWithDiagnosticsAsync(_httpClient, request, "WebDAV", "分片读取", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new CloudSyncRangeResult([], response.Content.Headers.ContentRange?.Length ?? 0, Math.Max(0, offset));
        }

        CloudSyncHttpDiagnostics.EnsureSuccess(response, "WebDAV", "分片读取");
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return CloudSyncTransportHelpers.ParsePartialContentResponse(
                body,
                response.Content.Headers.ContentRange,
                offset);
        }

        // Fallback when WebDAV server ignores Range and responds with 200 OK full payload.
        return CloudSyncTransportHelpers.SliceFullBodyForRange(body, offset, length);
    }

    public async Task<IReadOnlyList<CloudSyncObjectInfo>> ListPrefixAsync(
        string relativePrefix,
        CancellationToken cancellationToken = default)
    {
        string normalizedPrefix = CloudSyncTransportHelpers.NormalizeRelativePath(relativePrefix, allowEmpty: true);
        string collectionRelative = normalizedPrefix.EndsWith('/')
            ? normalizedPrefix.TrimEnd('/')
            : normalizedPrefix.Contains('/')
                ? normalizedPrefix[..normalizedPrefix.LastIndexOf('/')]
                : string.Empty;

        Uri collectionUri = BuildCollectionUri(collectionRelative);
        using var request = CreateRequest(PropfindMethod, collectionUri);
        request.Headers.TryAddWithoutValidation("Depth", "1");
        request.Content = new StringContent(PropfindRequestBody, Encoding.UTF8, "application/xml");

        using HttpResponseMessage response = await CloudSyncHttpDiagnostics
            .SendWithDiagnosticsAsync(_httpClient, request, "WebDAV", "列举目录", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (response.StatusCode != HttpStatusCode.MultiStatus && !response.IsSuccessStatusCode)
        {
            CloudSyncHttpDiagnostics.EnsureSuccess(response, "WebDAV", "列举目录");
        }

        string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParsePropfindResponse(xml, normalizedPrefix);
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        string normalized = CloudSyncTransportHelpers.NormalizeRelativePath(relativePath);
        using var request = CreateRequest(HttpMethod.Head, BuildObjectUri(normalized));
        using HttpResponseMessage response = await CloudSyncHttpDiagnostics
            .SendWithDiagnosticsAsync(_httpClient, request, "WebDAV", "检查文件", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        CloudSyncHttpDiagnostics.EnsureSuccess(response, "WebDAV", "检查文件");
        return true;
    }

    public async Task<CloudSyncEndpointTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureRepoInitializedAsync(cancellationToken).ConfigureAwait(false);
            bool hasRepo = await ExistsAsync(CloudSyncTransportHelpers.RepoMetadataRelativePath, cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();
            return hasRepo
                ? new CloudSyncEndpointTestResult(true, "WebDAV 同步仓库连接与读写校验成功。", sw.ElapsedMilliseconds)
                : new CloudSyncEndpointTestResult(false, "WebDAV 仓库初始化后未读取到 repo.json。", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CloudSyncEndpointTestResult(false, $"WebDAV 连接测试失败：{ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task EnsureCollectionPathAsync(string relativeCollectionPath, CancellationToken cancellationToken)
    {
        if (_ensuredCollections.ContainsKey(relativeCollectionPath))
        {
            return;
        }

        await EnsureSingleCollectionAsync(string.Empty, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(relativeCollectionPath))
        {
            string[] segments = relativeCollectionPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cumulative = new StringBuilder();
            foreach (string seg in segments)
            {
                if (cumulative.Length > 0)
                {
                    cumulative.Append('/');
                }

                cumulative.Append(seg);
                await EnsureSingleCollectionAsync(cumulative.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureSingleCollectionAsync(string relativeCollectionPath, CancellationToken cancellationToken)
    {
        if (_ensuredCollections.ContainsKey(relativeCollectionPath))
        {
            return;
        }

        Uri uri = BuildCollectionUri(relativeCollectionPath);
        using var request = CreateRequest(MkcolMethod, uri);
        using HttpResponseMessage response = await CloudSyncHttpDiagnostics
            .SendWithDiagnosticsAsync(_httpClient, request, "WebDAV", "创建目录", cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode
            || response.StatusCode == HttpStatusCode.MethodNotAllowed
            || response.StatusCode == HttpStatusCode.MovedPermanently
            || response.StatusCode == HttpStatusCode.Conflict)
        {
            _ensuredCollections.TryAdd(relativeCollectionPath, 0);
            return;
        }

        CloudSyncHttpDiagnostics.EnsureSuccess(response, "WebDAV", "创建目录");
    }

    private List<CloudSyncObjectInfo> ParsePropfindResponse(string xmlText, string normalizedPrefix)
    {
        if (string.IsNullOrWhiteSpace(xmlText))
        {
            return [];
        }

        XDocument doc = XDocument.Parse(xmlText);
        string syncRootPath = Uri.UnescapeDataString(_syncRootUri.AbsolutePath).TrimEnd('/') + "/";
        var results = new List<CloudSyncObjectInfo>();

        foreach (XElement resp in doc.Descendants().Where(static e => e.Name == DavNamespace + "response" || e.Name.LocalName == "response"))
        {
            string? hrefRaw = resp.Elements().FirstOrDefault(static e => e.Name.LocalName == "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(hrefRaw))
            {
                continue;
            }

            XElement? prop = resp.Descendants().FirstOrDefault(static e => e.Name.LocalName == "prop");
            bool isCollection = prop?.Descendants().Any(static e => e.Name.LocalName == "collection") == true;
            if (isCollection || hrefRaw.EndsWith('/'))
            {
                continue;
            }

            string decodedPath = ExtractPathFromHref(hrefRaw);
            int rootIdx = decodedPath.IndexOf(syncRootPath, StringComparison.OrdinalIgnoreCase);
            if (rootIdx < 0)
            {
                continue;
            }

            string relPath = decodedPath[(rootIdx + syncRootPath.Length)..].TrimStart('/');
            if (string.IsNullOrWhiteSpace(relPath))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(normalizedPrefix)
                && !relPath.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            long? sizeBytes = null;
            string? sizeStr = prop?.Elements().FirstOrDefault(static e => e.Name.LocalName == "getcontentlength")?.Value?.Trim();
            if (long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize))
            {
                sizeBytes = parsedSize;
            }

            DateTimeOffset? lastModified = null;
            string? modStr = prop?.Elements().FirstOrDefault(static e => e.Name.LocalName == "getlastmodified")?.Value?.Trim();
            if (DateTimeOffset.TryParse(modStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedMod))
            {
                lastModified = parsedMod.ToUniversalTime();
            }

            results.Add(new CloudSyncObjectInfo(relPath, sizeBytes, lastModified));
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var req = new HttpRequestMessage(method, uri);
        if (_authHeader is not null)
        {
            req.Headers.Authorization = _authHeader;
        }

        return req;
    }

    private Uri BuildObjectUri(string normalizedRelativePath)
    {
        string escaped = string.Join(
            '/',
            normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(_syncRootUri, escaped);
    }

    private Uri BuildCollectionUri(string normalizedRelativeCollectionPath)
    {
        if (string.IsNullOrEmpty(normalizedRelativeCollectionPath))
        {
            return _syncRootUri;
        }

        string escaped = string.Join(
            '/',
            normalizedRelativeCollectionPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(_syncRootUri, escaped + "/");
    }

    private static Uri BuildSyncRootUri(string endpointUrl, string? rootSubPath)
    {
        string trimmed = endpointUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed + "/", UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid WebDAV endpoint URL: '{endpointUrl}'.", nameof(endpointUrl));
        }

        var pathParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rootSubPath))
        {
            string normalizedSub = CloudSyncTransportHelpers.NormalizeRelativePath(rootSubPath, allowEmpty: true).TrimEnd('/');
            if (!string.IsNullOrEmpty(normalizedSub))
            {
                pathParts.AddRange(normalizedSub.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            }
        }

        pathParts.Add(CloudSyncTransportHelpers.SyncRootFolderName);
        return new Uri(baseUri, string.Join('/', pathParts) + "/");
    }

    private static AuthenticationHeaderValue? BuildAuthHeader(CloudSyncEndpointSecrets? secrets)
    {
        if (secrets is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(secrets.UsernameOrAccessKey))
        {
            string raw = $"{secrets.UsernameOrAccessKey}:{secrets.PasswordOrSecretKey ?? string.Empty}";
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            return new AuthenticationHeaderValue("Basic", token);
        }

        if (!string.IsNullOrWhiteSpace(secrets.PasswordOrSecretKey))
        {
            return new AuthenticationHeaderValue("Bearer", secrets.PasswordOrSecretKey.Trim());
        }

        return null;
    }

    private static string ExtractPathFromHref(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out Uri? absUri))
        {
            return Uri.UnescapeDataString(absUri.AbsolutePath);
        }

        return Uri.UnescapeDataString(href);
    }
}
