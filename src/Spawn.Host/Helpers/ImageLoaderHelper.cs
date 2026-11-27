using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Spawn.Host.Helpers;

/// <summary>
/// Loads local artwork and achievement icons (including <c>.webp</c>, <c>.png</c>, <c>.jpg</c>, <c>.ico</c>)
/// into frozen <see cref="BitmapImage"/> instances without holding file locks on disk.
/// </summary>
public static class ImageLoaderHelper
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, BitmapImage?> FastPathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim DecodeGate = new(initialCount: 4, maxCount: 4);

    /// <summary>
    /// Fast zero-disk-I/O cache probe safe to call on the UI thread when constructing lists of ViewModels.
    /// </summary>
    public static bool TryGetCached(string? filePath, int maxDecodeWidth, out BitmapImage? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string fastKey = BuildFastPathKey(filePath, maxDecodeWidth);
        return FastPathCache.TryGetValue(fastKey, out image) && image is not null;
    }

    public static BitmapImage? LoadFrozen(string? filePath, int maxDecodeWidth = 256)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string fullPath;
        long fileLength;
        DateTime lastWriteUtc;
        try
        {
            fullPath = Path.GetFullPath(filePath);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                FastPathCache.TryRemove($"{fullPath}|{maxDecodeWidth}", out _);
                return null;
            }

            fileLength = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch
        {
            return null;
        }

        string fastKey = $"{fullPath}|{maxDecodeWidth}";
        string cacheKey = $"{fastKey}|{fileLength}|{lastWriteUtc.Ticks}";
        if (Cache.TryGetValue(cacheKey, out BitmapImage? cached))
        {
            if (cached is not null)
            {
                FastPathCache[fastKey] = cached;
            }
            else
            {
                FastPathCache.TryRemove(fastKey, out _);
            }

            return cached;
        }

        BitmapImage? decoded = TryDecodeWithWic(fullPath, maxDecodeWidth) ?? TryDecodeWithSkia(fullPath);
        if (Cache.Count > 512)
        {
            Cache.Clear();
            FastPathCache.Clear();
        }

        Cache[cacheKey] = decoded;
        if (decoded is not null)
        {
            FastPathCache[fastKey] = decoded;
        }
        else
        {
            FastPathCache.TryRemove(fastKey, out _);
        }

        return decoded;
    }

    /// <summary>
    /// Decodes a local image into a frozen <see cref="BitmapImage"/> on a bounded background worker pool
    /// so UI threads never stall on disk I/O or WIC/Skia decoding.
    /// </summary>
    public static async Task<BitmapImage?> LoadFrozenAsync(
        string? filePath,
        int maxDecodeWidth = 256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LoadFrozen(filePath, maxDecodeWidth);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    public static void ClearCache()
    {
        Cache.Clear();
        FastPathCache.Clear();
    }

    private static string BuildFastPathKey(string filePath, int maxDecodeWidth)
    {
        try
        {
            return $"{Path.GetFullPath(filePath)}|{maxDecodeWidth}";
        }
        catch
        {
            return $"{filePath}|{maxDecodeWidth}";
        }
    }

    private static BitmapImage? TryDecodeWithWic(string fullPath, int maxDecodeWidth)
    {
        string ext = Path.GetExtension(fullPath);
        if (string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer SkiaSharp for deterministic cross-build WebP decoding.
            return null;
        }

        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (maxDecodeWidth > 0)
            {
                bmp.DecodePixelWidth = maxDecodeWidth;
            }

            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? TryDecodeWithSkia(string fullPath)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(fullPath);
            using var skBitmap = SKBitmap.Decode(raw);
            if (skBitmap is null)
            {
                return null;
            }

            using var skImage = SKImage.FromBitmap(skBitmap);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null)
            {
                return null;
            }

            using var pngStream = encoded.AsStream();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = pngStream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
