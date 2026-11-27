using System.Net;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Translates S3 / R2 / WebDAV HTTP status codes and transport errors into clear, actionable Chinese
/// troubleshooting messages so users never face opaque raw <c>HttpRequestException</c> strings.
/// </summary>
internal static class CloudSyncHttpDiagnostics
{
    public static void EnsureSuccess(HttpResponseMessage response, string providerLabel, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message = FormatStatusMessage(response.StatusCode, providerLabel, operation);
        throw new HttpRequestException(message, inner: null, response.StatusCode);
    }

    public static string FormatStatusMessage(HttpStatusCode statusCode, string providerLabel, string operation)
    {
        int code = (int)statusCode;
        return statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"{providerLabel} {operation}认证失败 (HTTP 401)：请检查 Access Key / Secret Key（或 WebDAV 用户名与应用专用密码）是否正确。",
            HttpStatusCode.Forbidden =>
                $"{providerLabel} {operation}被拒绝 (HTTP 403)：请检查存储桶/目录读写权限、Bucket 名称、Region 设置，或尝试切换 Path-Style 寻址模式。",
            HttpStatusCode.NotFound =>
                $"{providerLabel} {operation}目标不存在 (HTTP 404)：请检查 Endpoint URL、存储桶名称，或确认 WebDAV 根目录路径已提前创建。",
            HttpStatusCode.MethodNotAllowed =>
                $"{providerLabel} {operation}方式不被允许 (HTTP 405)：请确认填写的是标准 WebDAV / S3 接口地址，而非网盘网页版管理地址。",
            HttpStatusCode.Conflict =>
                $"{providerLabel} {operation}发生路径冲突 (HTTP 409)：请确认上级目录已创建或存储桶区域配置正确。",
            HttpStatusCode.TooManyRequests =>
                $"{providerLabel} {operation}触发限流 (HTTP 429)：云存储请求过于频繁，请稍后重试。",
            _ when code >= 500 =>
                $"{providerLabel} {operation}服务端异常 (HTTP {code})：远端云存储暂时不可用，请稍后重试。",
            _ =>
                $"{providerLabel} {operation}请求失败 (HTTP {code} {statusCode})：请检查端点配置与网络连接。",
        };
    }

    public static async Task<HttpResponseMessage> SendWithDiagnosticsAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string providerLabel,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{providerLabel} {operation}连接超时：请检查网络连通性、代理设置或 Endpoint 地址是否可达。",
                ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode code)
        {
            throw new HttpRequestException(FormatStatusMessage(code, providerLabel, operation), ex, code);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                $"{providerLabel} {operation}网络连接失败（{ex.Message}）：请检查 Endpoint URL、DNS 解析、TLS 证书或代理配置。",
                ex);
        }
    }
}
