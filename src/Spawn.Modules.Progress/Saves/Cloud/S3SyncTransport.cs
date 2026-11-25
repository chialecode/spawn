using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Pure BCL <see cref="HttpClient"/> S3 / Cloudflare R2 / MinIO transport using <see cref="S3SigV4Signer"/>
/// (<c>PUT</c>, <c>GET</c>, <c>GET Range</c>, <c>HEAD</c>, and paginated <c>ListObjectsV2</c> XML parsing).
/// </summary>
internal sealed class S3SyncTransport : ICloudSyncTransport
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _endpointUrl;
    private readonly string _bucketName;
    private readonly string _syncKeyPrefix; // always ends with "spawn-sync/"
    private readonly string _region;
    private readonly bool _pathStyleAddressing;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly TimeProvider _timeProvider;

    public S3SyncTransport(
        string endpointUrl,
        string bucketOrRoot,
        string? region,
        bool pathStyleAddressing,
        CloudSyncEndpointSecrets? secrets,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketOrRoot);

        (string bucket, string? optionalPrefix) = SplitBucketAndPrefix(bucketOrRoot);
        _endpointUrl = endpointUrl.Trim();
        _bucketName = bucket;
        _syncKeyPrefix = string.IsNullOrEmpty(optionalPrefix)
            ? $"{CloudSyncTransportHelpers.SyncRootFolderName}/"
            : $"{optionalPrefix}/{CloudSyncTransportHelpers.SyncRootFolderName}/";
        _region = string.IsNullOrWhiteSpace(region) ? "auto" : region.Trim();
        _pathStyleAddressing = pathStyleAddressing;
        _accessKey = secrets?.UsernameOrAccessKey?.Trim() ?? string.Empty;
        _secretKey = secrets?.PasswordOrSecretKey?.Trim() ?? string.Empty;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();

        if (string.IsNullOrEmpty(_accessKey) || string.IsNullOrEmpty(_secretKey))
        {
            throw new InvalidOperationException("S3 / R2 同步端点缺少 Access Key 或 Secret Key。");
        }
    }

    public CloudSyncProviderType ProviderType => CloudSyncProviderType.S3;

    public async Task EnsureRepoInitializedAsync(CancellationToken cancellationToken = default)
    {
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
        string key = BuildObjectKey(relativePath);
        Uri uri = S3SigV4Signer.BuildUri(_endpointUrl, _bucketName, key, _pathStyleAddressing);
        byte[] payloadBytes = data.ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Content = new ByteArrayContent(payloadBytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        S3SigV4Signer.SignRequest(request, payloadBytes, _accessKey, _secretKey, _region, _timeProvider.GetUtcNow());

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]?> GetBytesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        string key = BuildObjectKey(relativePath);
        Uri uri = S3SigV4Signer.BuildUri(_endpointUrl, _bucketName, key, _pathStyleAddressing);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        S3SigV4Signer.SignRequest(request, ReadOnlySpan<byte>.Empty, _accessKey, _secretKey, _region, _timeProvider.GetUtcNow());

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudSyncRangeResult?> GetRangeAsync(
        string relativePath,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        string key = BuildObjectKey(relativePath);
        Uri uri = S3SigV4Signer.BuildUri(_endpointUrl, _bucketName, key, _pathStyleAddressing);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = CloudSyncTransportHelpers.CreateRangeHeader(offset, length);
        S3SigV4Signer.SignRequest(request, ReadOnlySpan<byte>.Empty, _accessKey, _secretKey, _region, _timeProvider.GetUtcNow());

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new CloudSyncRangeResult([], response.Content.Headers.ContentRange?.Length ?? 0, Math.Max(0, offset));
        }

        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return CloudSyncTransportHelpers.ParsePartialContentResponse(
                body,
                response.Content.Headers.ContentRange,
                offset);
        }

        return CloudSyncTransportHelpers.SliceFullBodyForRange(body, offset, length);
    }

    public async Task<IReadOnlyList<CloudSyncObjectInfo>> ListPrefixAsync(
        string relativePrefix,
        CancellationToken cancellationToken = default)
    {
        string normalizedPrefix = CloudSyncTransportHelpers.NormalizeRelativePath(relativePrefix, allowEmpty: true);
        string fullPrefix = _syncKeyPrefix + normalizedPrefix;
        var results = new List<CloudSyncObjectInfo>();
        string? continuationToken = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string query = $"list-type=2&prefix={S3SigV4Signer.AwsUriEncode(fullPrefix, encodeSlash: true)}";
            if (!string.IsNullOrEmpty(continuationToken))
            {
                query += $"&continuation-token={S3SigV4Signer.AwsUriEncode(continuationToken, encodeSlash: true)}";
            }

            Uri uri = S3SigV4Signer.BuildUri(_endpointUrl, _bucketName, objectKey: null, _pathStyleAddressing, query);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            S3SigV4Signer.SignRequest(request, ReadOnlySpan<byte>.Empty, _accessKey, _secretKey, _region, _timeProvider.GetUtcNow());

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            (IReadOnlyList<CloudSyncObjectInfo> pageItems, string? nextToken) = ParseListObjectsV2Xml(xml);
            results.AddRange(pageItems);

            if (string.IsNullOrEmpty(nextToken))
            {
                break;
            }

            continuationToken = nextToken;
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        string key = BuildObjectKey(relativePath);
        Uri uri = S3SigV4Signer.BuildUri(_endpointUrl, _bucketName, key, _pathStyleAddressing);

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        S3SigV4Signer.SignRequest(request, ReadOnlySpan<byte>.Empty, _accessKey, _secretKey, _region, _timeProvider.GetUtcNow());

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
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
                ? new CloudSyncEndpointTestResult(true, $"S3/R2 存储桶 '{_bucketName}' 连接与仓库校验成功。", sw.ElapsedMilliseconds)
                : new CloudSyncEndpointTestResult(false, $"S3/R2 存储桶 '{_bucketName}' 未读取到 repo.json。", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CloudSyncEndpointTestResult(false, $"S3/R2 连接测试失败：{ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private string BuildObjectKey(string relativePath)
    {
        string normalized = CloudSyncTransportHelpers.NormalizeRelativePath(relativePath);
        return _syncKeyPrefix + normalized;
    }

    private (IReadOnlyList<CloudSyncObjectInfo> Items, string? NextContinuationToken) ParseListObjectsV2Xml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return ([], null);
        }

        XDocument doc = XDocument.Parse(xml);
        XElement? root = doc.Root;
        if (root is null)
        {
            return ([], null);
        }

        var items = new List<CloudSyncObjectInfo>();
        foreach (XElement content in root.Elements().Where(static e => e.Name.LocalName == "Contents"))
        {
            string? key = content.Elements().FirstOrDefault(static e => e.Name.LocalName == "Key")?.Value?.Trim();
            if (string.IsNullOrEmpty(key) || key.EndsWith('/'))
            {
                continue;
            }

            if (!key.StartsWith(_syncKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relPath = key[_syncKeyPrefix.Length..];
            if (string.IsNullOrEmpty(relPath))
            {
                continue;
            }

            long? size = null;
            string? sizeStr = content.Elements().FirstOrDefault(static e => e.Name.LocalName == "Size")?.Value?.Trim();
            if (long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize))
            {
                size = parsedSize;
            }

            DateTimeOffset? lastModified = null;
            string? modStr = content.Elements().FirstOrDefault(static e => e.Name.LocalName == "LastModified")?.Value?.Trim();
            if (DateTimeOffset.TryParse(modStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedMod))
            {
                lastModified = parsedMod.ToUniversalTime();
            }

            items.Add(new CloudSyncObjectInfo(relPath, size, lastModified));
        }

        bool isTruncated = string.Equals(
            root.Elements().FirstOrDefault(static e => e.Name.LocalName == "IsTruncated")?.Value?.Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);

        string? nextToken = isTruncated
            ? root.Elements().FirstOrDefault(static e => e.Name.LocalName == "NextContinuationToken")?.Value?.Trim()
            : null;

        return (items, string.IsNullOrEmpty(nextToken) ? null : nextToken);
    }

    private static (string Bucket, string? Prefix) SplitBucketAndPrefix(string bucketOrRoot)
    {
        string normalized = bucketOrRoot.Replace('\\', '/').Trim().Trim('/');
        int slash = normalized.IndexOf('/');
        if (slash < 0)
        {
            return (normalized, null);
        }

        string bucket = normalized[..slash];
        string rawPrefix = normalized[(slash + 1)..];
        string cleanPrefix = CloudSyncTransportHelpers.NormalizeRelativePath(rawPrefix, allowEmpty: true).TrimEnd('/');
        return (bucket, string.IsNullOrEmpty(cleanPrefix) ? null : cleanPrefix);
    }
}
