using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Metadata entry returned when listing objects under a prefix inside <c>&lt;RemoteRoot&gt;/spawn-sync/</c>.
/// </summary>
internal sealed record CloudSyncObjectInfo(
    string RelativePath,
    long? SizeBytes,
    DateTimeOffset? LastModifiedUtc);

/// <summary>
/// Byte range slice returned by <see cref="ICloudSyncTransport.GetRangeAsync"/>.
/// </summary>
internal sealed record CloudSyncRangeResult(
    byte[] Data,
    long TotalLength,
    long Offset);

/// <summary>
/// Stateless transport abstraction operating exclusively beneath <c>&lt;RemoteRoot&gt;/spawn-sync/</c>
/// (<c>repo.json</c>, <c>packs/</c>, <c>snapshots/</c>, <c>heads/</c>).
/// </summary>
internal interface ICloudSyncTransport : IDisposable
{
    CloudSyncProviderType ProviderType { get; }

    /// <summary>
    /// Ensures the remote <c>spawn-sync/</c> repository layout and immutable <c>repo.json</c> exist.
    /// </summary>
    Task EnsureRepoInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes or atomically replaces an object at <paramref name="relativePath"/> under <c>spawn-sync/</c>.
    /// </summary>
    Task PutBytesAsync(
        string relativePath,
        ReadOnlyMemory<byte> data,
        string contentType = "application/octet-stream",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the complete object at <paramref name="relativePath"/>, or returns <see langword="null"/> if not found.
    /// </summary>
    Task<byte[]?> GetBytesAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a byte range from <paramref name="relativePath"/>, or returns <see langword="null"/> if not found.
    /// When <paramref name="offset"/> is negative, reads up to the trailing <paramref name="length"/> bytes
    /// (equivalent to HTTP <c>Range: bytes=-{length}</c>).
    /// </summary>
    Task<CloudSyncRangeResult?> GetRangeAsync(
        string relativePath,
        long offset,
        int length,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists objects beneath <paramref name="relativePrefix"/> (forward-slash normalized relative to <c>spawn-sync/</c>).
    /// </summary>
    Task<IReadOnlyList<CloudSyncObjectInfo>> ListPrefixAsync(
        string relativePrefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether <paramref name="relativePath"/> exists in the remote repository.
    /// </summary>
    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies remote connectivity, authentication, and repository access without mutating user save data.
    /// </summary>
    Task<CloudSyncEndpointTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
}

internal static class CloudSyncTransportHelpers
{
    public const string SyncRootFolderName = "spawn-sync";
    public const string RepoMetadataRelativePath = "repo.json";

    private static readonly JsonSerializerOptions RepoJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string NormalizeRelativePath(string relativePath, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw new ArgumentException("Relative path cannot be empty.", nameof(relativePath));
        }

        string normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        bool hadTrailingSlash = normalized.EndsWith('/');
        string trimmed = normalized.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw new ArgumentException("Relative path cannot be root-only.", nameof(relativePath));
        }

        string[] segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == "." || seg == ".." || seg.Contains(':') || seg.Any(char.IsControl))
            {
                throw new ArgumentException($"Unsafe cloud sync relative path: '{relativePath}'.", nameof(relativePath));
            }
        }

        string joined = string.Join('/', segments);
        return hadTrailingSlash && allowEmpty ? joined + "/" : joined;
    }

    public static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Path segment cannot be empty.", nameof(value));
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            if ((c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c is '-' or '_' or '.')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }

        string result = sb.ToString().Trim('.');
        return string.IsNullOrEmpty(result) ? "default" : result;
    }

    public static byte[] CreateRepoMetadataJsonBytes(DateTimeOffset createdAtUtc)
    {
        var doc = new
        {
            formatVersion = 1,
            packFormat = "SPPK-v1",
            createdAtUtc,
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, RepoJsonOptions);
    }

    public static RangeHeaderValue CreateRangeHeader(long offset, int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Range length must be positive.");
        }

        return offset < 0
            ? new RangeHeaderValue(from: null, to: length)
            : new RangeHeaderValue(from: offset, to: offset + length - 1);
    }

    public static CloudSyncRangeResult SliceFullBodyForRange(byte[] fullBody, long offset, int length)
    {
        long totalLength = fullBody.Length;
        if (totalLength == 0)
        {
            return new CloudSyncRangeResult([], 0, Math.Max(0, offset));
        }

        long actualStart;
        int actualCount;
        if (offset < 0)
        {
            actualStart = Math.Max(0, totalLength - length);
            actualCount = (int)Math.Min(length, totalLength - actualStart);
        }
        else if (offset >= totalLength)
        {
            return new CloudSyncRangeResult([], totalLength, offset);
        }
        else
        {
            actualStart = offset;
            actualCount = (int)Math.Min(length, totalLength - actualStart);
        }

        byte[] slice = new byte[actualCount];
        Buffer.BlockCopy(fullBody, (int)actualStart, slice, 0, actualCount);
        return new CloudSyncRangeResult(slice, totalLength, actualStart);
    }

    public static CloudSyncRangeResult ParsePartialContentResponse(
        byte[] bodyBytes,
        ContentRangeHeaderValue? contentRange,
        long requestedOffset)
    {
        long totalLength = contentRange?.Length ?? (contentRange?.To is long to ? to + 1 : bodyBytes.Length);
        long actualOffset = contentRange?.From ?? Math.Max(0, requestedOffset < 0 ? totalLength - bodyBytes.Length : requestedOffset);
        return new CloudSyncRangeResult(bodyBytes, totalLength, actualOffset);
    }
}
