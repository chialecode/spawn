using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Spawn.Core.Update;

public sealed class UpdatePolicyViolationException(string message) : Exception(message);

public sealed class UpdateNetworkException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record DownloadedFileResult(long BytesWritten, string Sha256LowerHex);

/// <summary>
/// Strict zero-telemetry HTTPS fetcher enforcing:
/// <list type="bullet">
/// <item>HTTPS-only on initial URL and every redirect hop;</item>
/// <item>Exact case-insensitive host allowlist on every hop;</item>
/// <item>Manual redirect following (<c>AllowAutoRedirect = false</c>) capped at <see cref="UpdateServiceOptions.MaxRedirects"/> (default 5);</item>
/// <item>Streaming byte caps enforcing both <c>Content-Length</c> and actual bytes read;</item>
/// <item>Honest <c>User-Agent: SPAWN-Updater/&lt;version&gt;</c> with zero cookies or machine/user identifiers.</item>
/// </list>
/// </summary>
public sealed class UpdateHttpFetcher : IDisposable
{
    private readonly UpdateServiceOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _userAgent;
    private int _disposed;

    public string UserAgent => _userAgent;

    public UpdateHttpFetcher(
        UpdateServiceOptions options,
        string currentVersion,
        HttpMessageHandler? messageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _userAgent = BuildHonestUserAgent(currentVersion);

        if (messageHandler is not null)
        {
            _httpClient = new HttpClient(messageHandler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsHttpClient = true;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsHttpClient = true;
        }
    }

    public static string BuildHonestUserAgent(string? currentVersion)
    {
        string raw = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();
        var sb = new StringBuilder("SPAWN-Updater/", 32);
        int appended = 0;
        foreach (char ch in raw)
        {
            if (appended >= 48)
            {
                break;
            }

            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_' or '+')
            {
                sb.Append(ch);
                appended++;
            }
        }

        if (appended == 0)
        {
            sb.Append("0.0.0");
        }

        return sb.ToString();
    }

    public void ValidateEndpointUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            throw new UpdatePolicyViolationException("更新请求 URL 必须为合法绝对地址。");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdatePolicyViolationException($"仅允许 HTTPS 更新传输，已拒绝协议：{uri.Scheme}。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new UpdatePolicyViolationException("更新 URL 严禁包含用户凭据片段。");
        }

        if (!uri.IsDefaultPort)
        {
            throw new UpdatePolicyViolationException($"仅允许默认 HTTPS 端口 443，已拒绝端口：{uri.Port}。");
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || !_options.AllowedHosts.Contains(uri.Host))
        {
            throw new UpdatePolicyViolationException($"主机不在更新白名单内，已拒绝：{uri.Host}。");
        }
    }

    public async Task<byte[]> DownloadBytesAsync(
        Uri initialUri,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.HttpRequestTimeout);

        try
        {
            using HttpResponseMessage response = await SendWithManualRedirectsAsync(initialUri, timeoutCts.Token)
                .ConfigureAwait(false);

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
            {
                throw new UpdatePolicyViolationException(
                    string.Create(CultureInfo.InvariantCulture, $"响应头声明大小 {contentLength.Value} 字节超过上限 {maxBytes} 字节。"));
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var ms = new MemoryStream(contentLength.HasValue && contentLength.Value > 0 && contentLength.Value <= maxBytes
                ? (int)contentLength.Value
                : 4096);

            byte[] buffer = new byte[8192];
            long totalRead = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new UpdatePolicyViolationException(
                        string.Create(CultureInfo.InvariantCulture, $"实际接收数据超过上限 {maxBytes} 字节，已中止读取。"));
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }
        catch (OperationCanceledException ex) when (IsInternalTimeout(timeoutCts, cancellationToken))
        {
            throw new UpdateNetworkException(TimeoutMessage, ex);
        }
        catch (HttpIOException ex)
        {
            throw new UpdateNetworkException($"网络传输中断：{ex.Message}", ex);
        }
    }

    public async Task<DownloadedFileResult> DownloadFileAsync(
        Uri initialUri,
        string destinationPartialPath,
        long maxBytes,
        long? expectedExactSize = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPartialPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        string? parentDir = Path.GetDirectoryName(destinationPartialPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.DownloadTimeout);

        try
        {
            using HttpResponseMessage response = await SendWithManualRedirectsAsync(initialUri, timeoutCts.Token)
                .ConfigureAwait(false);

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
            {
                throw new UpdatePolicyViolationException(
                    string.Create(CultureInfo.InvariantCulture, $"安装包声明大小 {contentLength.Value} 字节超过上限 {maxBytes} 字节。"));
            }

            long progressDenominator = expectedExactSize is > 0
                ? expectedExactSize.Value
                : (contentLength is > 0 ? contentLength.Value : 0);

            await using Stream source = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await using var dest = new FileStream(
                destinationPartialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                useAsync: true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[65536];
            long totalRead = 0;
            double lastReportedPercent = -1.0;

            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new UpdatePolicyViolationException(
                        string.Create(CultureInfo.InvariantCulture, $"安装包下载流超过上限 {maxBytes} 字节，已中止。"));
                }

                hasher.AppendData(buffer, 0, read);
                await dest.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token).ConfigureAwait(false);

                if (progress is not null && progressDenominator > 0)
                {
                    double pct = Math.Clamp((double)totalRead * 100.0 / progressDenominator, 0.0, 100.0);
                    if (pct - lastReportedPercent >= 1.0 || pct >= 100.0)
                    {
                        lastReportedPercent = pct;
                        progress.Report(Math.Round(pct, 1));
                    }
                }
            }

            await dest.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] hashBytes = hasher.GetHashAndReset();
            return new DownloadedFileResult(totalRead, Convert.ToHexStringLower(hashBytes));
        }
        catch (OperationCanceledException ex) when (IsInternalTimeout(timeoutCts, cancellationToken))
        {
            // Internal DownloadTimeout fired (send, header read, stream read or disk write) while the
            // caller token is still live: translate to a network deferral so the service never leaks
            // a raw cancellation and the stale .partial is always removed.
            TryDeleteFileQuietly(destinationPartialPath);
            throw new UpdateNetworkException(TimeoutMessage, ex);
        }
        catch (HttpIOException ex)
        {
            TryDeleteFileQuietly(destinationPartialPath);
            throw new UpdateNetworkException($"网络传输中断：{ex.Message}", ex);
        }
        catch
        {
            TryDeleteFileQuietly(destinationPartialPath);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendWithManualRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int hop = 0; ; hop++)
        {
            ValidateEndpointUri(currentUri);

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateNetworkException($"网络请求失败：{ex.Message}", ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Our (linked caller + internal timeout) token fired: let the public entry point classify
                // it as caller cancellation (rethrow) vs. internal timeout (UpdateNetworkException).
                throw;
            }
            catch (TaskCanceledException ex)
            {
                // Cancellation not tied to our token (e.g. a handler-level timeout) is a network timeout.
                throw new UpdateNetworkException(TimeoutMessage, ex);
            }

            // Defense-in-depth if an injected test handler altered RequestUri.
            if (response.RequestMessage?.RequestUri is Uri finalRequestUri)
            {
                try
                {
                    ValidateEndpointUri(finalRequestUri);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            if (IsRedirectStatus(response.StatusCode))
            {
                Uri? location = response.Headers.Location;
                response.Dispose();

                if (hop >= _options.MaxRedirects)
                {
                    throw new UpdatePolicyViolationException(
                        string.Create(CultureInfo.InvariantCulture, $"更新请求重定向超过上限（最多允许 {_options.MaxRedirects} 次）。"));
                }

                if (location is null)
                {
                    throw new UpdatePolicyViolationException("收到重定向响应但缺少 Location 响应头。");
                }

                currentUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(currentUri, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                string? reason = response.ReasonPhrase;
                response.Dispose();
                throw new UpdateNetworkException(
                    string.Create(CultureInfo.InvariantCulture, $"更新源返回 HTTP {statusCode} ({reason})。"));
            }

            return response;
        }
    }

    internal const string TimeoutMessage = "更新请求超时，已静默顺延。";

    /// <summary>
    /// True only when the internal timeout token fired while the caller token is still live,
    /// i.e. the cancellation is a timeout rather than a caller-requested abort.
    /// </summary>
    private static bool IsInternalTimeout(CancellationTokenSource timeoutCts, CancellationToken callerToken) =>
        timeoutCts.IsCancellationRequested && !callerToken.IsCancellationRequested;

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static void TryDeleteFileQuietly(string path)
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
            // Best-effort partial cleanup.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
