using System.Diagnostics;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Local folder / SMB NAS / third-party synced directory transport for decentralized <c>SPPK</c> repositories.
/// Guarantees atomic writes (temp file + flush + overwrite move) and zero-copy tail/chunk <c>FileStream.Seek</c> range reads.
/// </summary>
internal sealed class LocalFolderSyncTransport : ICloudSyncTransport
{
    private readonly string _syncRootDirectory;
    private readonly TimeProvider _timeProvider;

    public LocalFolderSyncTransport(
        string rootDirectory,
        string? subRoot = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;

        string baseRoot = Path.GetFullPath(rootDirectory.Trim());
        if (!string.IsNullOrWhiteSpace(subRoot))
        {
            string normalizedSub = CloudSyncTransportHelpers.NormalizeRelativePath(subRoot, allowEmpty: true);
            if (!string.IsNullOrEmpty(normalizedSub))
            {
                baseRoot = Path.GetFullPath(Path.Combine(baseRoot, normalizedSub.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        _syncRootDirectory = Path.GetFullPath(Path.Combine(baseRoot, CloudSyncTransportHelpers.SyncRootFolderName));
    }

    public CloudSyncProviderType ProviderType => CloudSyncProviderType.LocalFolder;

    public string SyncRootDirectory => _syncRootDirectory;

    public async Task EnsureRepoInitializedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_syncRootDirectory);
        Directory.CreateDirectory(Path.Combine(_syncRootDirectory, "packs"));
        Directory.CreateDirectory(Path.Combine(_syncRootDirectory, "snapshots"));
        Directory.CreateDirectory(Path.Combine(_syncRootDirectory, "heads"));

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
        string targetPath = ResolveSafeFullPath(relativePath);
        string parentDir = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Cannot determine parent directory for '{relativePath}'.");
        Directory.CreateDirectory(parentDir);

        string tempPath = Path.Combine(
            parentDir,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous))
            {
                await fs.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteSilent(tempPath);
            throw;
        }
    }

    public async Task<byte[]?> GetBytesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        string fullPath = ResolveSafeFullPath(relativePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudSyncRangeResult?> GetRangeAsync(
        string relativePath,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Range length must be positive.");
        }

        string fullPath = ResolveSafeFullPath(relativePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        await using var fs = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: Math.Min(length, 64 * 1024),
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        long totalLength = fs.Length;
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

        fs.Seek(actualStart, SeekOrigin.Begin);
        byte[] buffer = new byte[actualCount];
        await fs.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new CloudSyncRangeResult(buffer, totalLength, actualStart);
    }

    public Task<IReadOnlyList<CloudSyncObjectInfo>> ListPrefixAsync(
        string relativePrefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedPrefix = CloudSyncTransportHelpers.NormalizeRelativePath(relativePrefix, allowEmpty: true);

        if (!Directory.Exists(_syncRootDirectory))
        {
            return Task.FromResult<IReadOnlyList<CloudSyncObjectInfo>>([]);
        }

        string searchDirectory;
        if (string.IsNullOrEmpty(normalizedPrefix))
        {
            searchDirectory = _syncRootDirectory;
        }
        else if (normalizedPrefix.EndsWith('/'))
        {
            searchDirectory = ResolveSafeFullPath(normalizedPrefix.TrimEnd('/'));
        }
        else
        {
            string candidateDir = ResolveSafeFullPath(normalizedPrefix);
            if (Directory.Exists(candidateDir))
            {
                searchDirectory = candidateDir;
            }
            else
            {
                searchDirectory = Path.GetDirectoryName(candidateDir) ?? _syncRootDirectory;
            }
        }

        if (!Directory.Exists(searchDirectory))
        {
            return Task.FromResult<IReadOnlyList<CloudSyncObjectInfo>>([]);
        }

        var results = new List<CloudSyncObjectInfo>();
        foreach (string filePath in Directory.EnumerateFiles(searchDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            if (fileName.StartsWith('.') && fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rel = Path.GetRelativePath(_syncRootDirectory, filePath).Replace('\\', '/');
            if (!string.IsNullOrEmpty(normalizedPrefix)
                && !rel.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            results.Add(new CloudSyncObjectInfo(
                RelativePath: rel,
                SizeBytes: info.Length,
                LastModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return Task.FromResult<IReadOnlyList<CloudSyncObjectInfo>>(results);
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = ResolveSafeFullPath(relativePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public async Task<CloudSyncEndpointTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureRepoInitializedAsync(cancellationToken).ConfigureAwait(false);
            string probePath = Path.Combine(_syncRootDirectory, $".spawn-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(probePath, "ok"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                byte[] readBack = await File.ReadAllBytesAsync(probePath, cancellationToken).ConfigureAwait(false);
                if (readBack.Length != 2)
                {
                    return new CloudSyncEndpointTestResult(false, "本地同步目录读写校验失败。", sw.ElapsedMilliseconds);
                }
            }
            finally
            {
                TryDeleteSilent(probePath);
            }

            sw.Stop();
            return new CloudSyncEndpointTestResult(
                true,
                $"本地同步仓库就绪（{CloudSyncTransportHelpers.SyncRootFolderName}）。",
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CloudSyncEndpointTestResult(false, $"无法访问本地同步目录：{ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
        // No unmanaged handles retained between operations.
    }

    private string ResolveSafeFullPath(string relativePath)
    {
        string normalized = CloudSyncTransportHelpers.NormalizeRelativePath(relativePath, allowEmpty: false);
        string combined = Path.GetFullPath(Path.Combine(_syncRootDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string rootWithSep = _syncRootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _syncRootDirectory
            : _syncRootDirectory + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Resolved path escapes local sync root: '{relativePath}'.");
        }

        return combined;
    }

    private static void TryDeleteSilent(string path)
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
            // Best-effort cleanup.
        }
    }
}
