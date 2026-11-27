using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Computed AWS Signature Version 4 signing artifacts for S3 / Cloudflare R2 / MinIO requests.
/// </summary>
internal sealed record S3SigV4SignatureResult(
    string AuthorizationHeader,
    string AmzDate,
    string PayloadSha256,
    string CredentialScope,
    string CanonicalRequest,
    string StringToSign,
    string Signature);

/// <summary>
/// Pure-BCL AWS Signature Version 4 (<c>AWS4-HMAC-SHA256</c>) request signer and endpoint URI builder
/// supporting both path-style (MinIO, Cloudflare R2, LocalStack) and virtual-hosted-style (AWS S3) addressing.
/// </summary>
internal static class S3SigV4Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string DefaultService = "s3";
    public const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// Builds the target S3 URI for an object key (or bucket root when <paramref name="objectKey"/> is null/empty)
    /// using either path-style or virtual-hosted-style addressing.
    /// </summary>
    public static Uri BuildUri(
        string endpointUrl,
        string bucketName,
        string? objectKey,
        bool pathStyleAddressing,
        string? queryString = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);

        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid S3 endpoint URL: '{endpointUrl}'.", nameof(endpointUrl));
        }

        string trimmedBucket = bucketName.Trim().Trim('/');
        string trimmedKey = string.IsNullOrWhiteSpace(objectKey) ? string.Empty : objectKey.TrimStart('/');
        string encodedKey = string.IsNullOrEmpty(trimmedKey) ? string.Empty : AwsUriEncode(trimmedKey, encodeSlash: false);

        var builder = new UriBuilder(baseUri);
        string basePath = builder.Path.TrimEnd('/');

        if (pathStyleAddressing)
        {
            string encodedBucket = AwsUriEncode(trimmedBucket, encodeSlash: true);
            builder.Path = string.IsNullOrEmpty(encodedKey)
                ? $"{basePath}/{encodedBucket}"
                : $"{basePath}/{encodedBucket}/{encodedKey}";
        }
        else
        {
            builder.Host = $"{trimmedBucket}.{baseUri.Host}";
            builder.Path = string.IsNullOrEmpty(encodedKey)
                ? (string.IsNullOrEmpty(basePath) ? "/" : basePath + "/")
                : $"{basePath}/{encodedKey}";
        }

        builder.Query = string.IsNullOrWhiteSpace(queryString) ? string.Empty : queryString.TrimStart('?');
        return builder.Uri;
    }

    /// <summary>
    /// Computes AWS Signature V4 headers and mutates <paramref name="request"/> with <c>x-amz-date</c>,
    /// <c>x-amz-content-sha256</c>, and <c>Authorization</c>.
    /// </summary>
    public static S3SigV4SignatureResult SignRequest(
        HttpRequestMessage request,
        ReadOnlySpan<byte> payload,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset timestampUtc,
        string service = DefaultService)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
        {
            throw new ArgumentException("HttpRequestMessage must have an absolute RequestUri.", nameof(request));
        }

        string payloadSha256 = payload.IsEmpty
            ? EmptyPayloadSha256
            : Convert.ToHexStringLower(SHA256.HashData(payload));

        var extraHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Headers.Range is not null)
        {
            extraHeaders["range"] = request.Headers.Range.ToString();
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            string lower = header.Key.ToLowerInvariant();
            if (lower.StartsWith("x-amz-", StringComparison.Ordinal)
                && lower != "x-amz-date"
                && lower != "x-amz-content-sha256")
            {
                extraHeaders[lower] = NormalizeHeaderValue(string.Join(",", header.Value));
            }
        }

        S3SigV4SignatureResult result = CreateSignature(
            httpMethod: request.Method.Method,
            requestUri: request.RequestUri,
            payloadSha256: payloadSha256,
            accessKey: accessKey,
            secretKey: secretKey,
            region: region,
            timestampUtc: timestampUtc,
            service: service,
            additionalSignedHeaders: extraHeaders);

        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.Remove("Authorization");

        request.Headers.TryAddWithoutValidation("x-amz-date", result.AmzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", result.PayloadSha256);
        request.Headers.TryAddWithoutValidation("Authorization", result.AuthorizationHeader);

        return result;
    }

    /// <summary>
    /// Deterministically computes an AWS Signature V4 signature for the given HTTP method, URI, payload SHA-256,
    /// credentials, region, timestamp, and optional extra signed headers.
    /// </summary>
    public static S3SigV4SignatureResult CreateSignature(
        string httpMethod,
        Uri requestUri,
        string payloadSha256,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset timestampUtc,
        string service = DefaultService,
        IReadOnlyDictionary<string, string>? additionalSignedHeaders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(httpMethod);
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        DateTimeOffset utc = timestampUtc.ToUniversalTime();
        string amzDate = utc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string trimmedRegion = region.Trim();
        string trimmedService = service.Trim();

        string canonicalUri = BuildCanonicalUri(requestUri);
        string canonicalQuery = BuildCanonicalQueryString(requestUri.Query);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = FormatHostHeader(requestUri),
            ["x-amz-content-sha256"] = payloadSha256.Trim().ToLowerInvariant(),
            ["x-amz-date"] = amzDate,
        };

        if (additionalSignedHeaders is not null)
        {
            foreach (var (name, value) in additionalSignedHeaders)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    headers[name.Trim().ToLowerInvariant()] = NormalizeHeaderValue(value);
                }
            }
        }

        var canonicalHeadersBuilder = new StringBuilder();
        var signedHeadersBuilder = new StringBuilder();
        foreach (var (name, value) in headers)
        {
            canonicalHeadersBuilder.Append(name).Append(':').Append(value).Append('\n');
            if (signedHeadersBuilder.Length > 0)
            {
                signedHeadersBuilder.Append(';');
            }

            signedHeadersBuilder.Append(name);
        }

        string canonicalHeaders = canonicalHeadersBuilder.ToString();
        string signedHeaders = signedHeadersBuilder.ToString();

        string canonicalRequest = string.Join(
            '\n',
            httpMethod.Trim().ToUpperInvariant(),
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            headers["x-amz-content-sha256"]);

        string canonicalRequestHash = ComputeSha256Hex(canonicalRequest);
        string credentialScope = $"{dateStamp}/{trimmedRegion}/{trimmedService}/aws4_request";
        string stringToSign = $"{Algorithm}\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

        byte[] kDate = ComputeHmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        byte[] kRegion = ComputeHmacSha256(kDate, trimmedRegion);
        byte[] kService = ComputeHmacSha256(kRegion, trimmedService);
        byte[] kSigning = ComputeHmacSha256(kService, "aws4_request");
        string signature = Convert.ToHexStringLower(ComputeHmacSha256(kSigning, stringToSign));

        string authHeader =
            $"{Algorithm} Credential={accessKey.Trim()}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        return new S3SigV4SignatureResult(
            AuthorizationHeader: authHeader,
            AmzDate: amzDate,
            PayloadSha256: headers["x-amz-content-sha256"],
            CredentialScope: credentialScope,
            CanonicalRequest: canonicalRequest,
            StringToSign: stringToSign,
            Signature: signature);
    }

    public static string AwsUriEncode(string value, bool encodeSlash)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            if ((b >= 'A' && b <= 'Z')
                || (b >= 'a' && b <= 'z')
                || (b >= '0' && b <= '9')
                || b == '_' || b == '-' || b == '~' || b == '.')
            {
                sb.Append((char)b);
            }
            else if (b == '/' && !encodeSlash)
            {
                sb.Append('/');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static string BuildCanonicalUri(Uri uri)
    {
        string rawPath = uri.AbsolutePath;
        if (string.IsNullOrEmpty(rawPath) || rawPath == "/")
        {
            return "/";
        }

        // Decode percent-encoded segments and re-encode strictly per AWS SigV4 rules.
        string[] segments = rawPath.Split('/');
        var sb = new StringBuilder(rawPath.Length + 8);
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('/');
            }

            if (segments[i].Length > 0)
            {
                string decoded = Uri.UnescapeDataString(segments[i]);
                sb.Append(AwsUriEncode(decoded, encodeSlash: true));
            }
        }

        return sb.Length == 0 ? "/" : sb.ToString();
    }

    private static string BuildCanonicalQueryString(string rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
        {
            return string.Empty;
        }

        string trimmed = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string[] pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var encodedPairs = new List<(string Key, string Value)>(pairs.Length);
        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=');
            string rawKey = eq >= 0 ? pair[..eq] : pair;
            string rawVal = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
            string encodedKey = AwsUriEncode(Uri.UnescapeDataString(rawKey), encodeSlash: true);
            string encodedVal = AwsUriEncode(Uri.UnescapeDataString(rawVal), encodeSlash: true);
            encodedPairs.Add((encodedKey, encodedVal));
        }

        encodedPairs.Sort(static (a, b) =>
        {
            int cmp = string.CompareOrdinal(a.Key, b.Key);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Value, b.Value);
        });

        return string.Join('&', encodedPairs.Select(static p => $"{p.Key}={p.Value}"));
    }

    private static string FormatHostHeader(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        return uri.IsDefaultPort
            ? host
            : $"{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        var sb = new StringBuilder(trimmed.Length);
        bool prevWhitespace = false;
        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevWhitespace)
                {
                    sb.Append(' ');
                    prevWhitespace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevWhitespace = false;
            }
        }

        return sb.ToString();
    }

    private static string ComputeSha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    private static byte[] ComputeHmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
