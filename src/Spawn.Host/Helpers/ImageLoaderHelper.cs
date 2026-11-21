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

    public static BitmapImage? LoadFrozen(string? filePath, int maxDecodeWidth = 256)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        string fullPath;
        DateTime lastWriteUtc;
        try
        {
            fullPath = Path.GetFullPath(filePath);
            lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        }
        catch
        {
            return null;
        }

        string cacheKey = $"{fullPath}|{maxDecodeWidth}|{lastWriteUtc.Ticks}";
        if (Cache.TryGetValue(cacheKey, out BitmapImage? cached))
        {
            return cached;
        }

        BitmapImage? decoded = TryDecodeWithWic(fullPath, maxDecodeWidth) ?? TryDecodeWithSkia(fullPath);
        if (Cache.Count > 512)
        {
            Cache.Clear();
        }

        Cache[cacheKey] = decoded;
        return decoded;
    }

    public static void ClearCache() => Cache.Clear();

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
