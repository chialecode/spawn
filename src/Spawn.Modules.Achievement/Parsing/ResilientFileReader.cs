namespace Spawn.Modules.Achievement;

/// <summary>
/// Lightweight metadata fingerprint for the 500ms active-game micro-poller.
/// </summary>
public readonly record struct FileMetadataStamp(bool Exists, long LastWriteTicksUtc, long LengthBytes);

/// <summary>
/// Lock-tolerant file reader with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> and four-step exponential backoff.
/// </summary>
public static class ResilientFileReader
{
    private static readonly int[] RetryDelaysMs = [25, 60, 120, 250];

    public static FileMetadataStamp GetMetadataStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return default;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return default;
            }

            return new FileMetadataStamp(
                Exists: true,
                LastWriteTicksUtc: info.LastWriteTimeUtc.Ticks,
                LengthBytes: info.Length);
        }
        catch
        {
            return default;
        }
    }

    public static async ValueTask<byte[]?> ReadSharedBytesWithRetryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 8192,
                    useAsync: true);

                long length = stream.Length;
                if (length == 0)
                {
                    return [];
                }

                if (length > 64 * 1024 * 1024)
                {
                    return null;
                }

                byte[] buffer = new byte[(int)length];
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        Array.Resize(ref buffer, offset);
                        break;
                    }

                    offset += read;
                }

                return buffer;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[attempt], cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }
}
