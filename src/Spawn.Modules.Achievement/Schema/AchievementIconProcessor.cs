using System.Security.Cryptography;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Downloads/caches achievement color icons and synthesizes ITU-R BT.709 (Rec.709) grayscale locked icons via <see cref="SkiaSharp"/>
/// whenever a game omits <c>icongray</c> or uploads an identical color image as its gray icon.
/// </summary>
public sealed class AchievementIconProcessor(SpawnPaths paths)
{
    // Rec.709 luminance weights (0.2126 R + 0.7152 G + 0.0722 B) slightly dimmed (0.78x) for clear locked distinction, alpha * 0.90
    private static readonly float[] Rec709GrayscaleColorMatrix =
    [
        0.2126f * 0.78f, 0.7152f * 0.78f, 0.0722f * 0.78f, 0f, 0f,
        0.2126f * 0.78f, 0.7152f * 0.78f, 0.0722f * 0.78f, 0f, 0f,
        0.2126f * 0.78f, 0.7152f * 0.78f, 0.0722f * 0.78f, 0f, 0f,
        0f,              0f,              0f,              0.90f, 0f,
    ];

    public async Task<IReadOnlyList<AchievementDefinitionDto>> CacheAndProcessIconsAsync(
        string schemaKey,
        IReadOnlyList<AchievementDefinitionDto> definitions,
        HttpClient httpClient,
        bool allowNetworkDownloads = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKey);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (definitions.Count == 0)
        {
            return definitions;
        }

        string iconDir = paths.GetAchievementIconDirectory(schemaKey);
        var updated = new AchievementDefinitionDto[definitions.Count];

        // Bounded concurrency (max 16 concurrent HTTP/2 icon fetches for 200+ achievement titles like Vampire Survivors)
        using var gate = new SemaphoreSlim(16, 16);
        var tasks = definitions.Select(async (def, idx) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                updated[idx] = await ProcessSingleDefinitionIconAsync(
                    iconDir, def, httpClient, allowNetworkDownloads, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Converts encoded color image bytes (PNG/JPEG/BMP/WebP) into a Rec.709 grayscale PNG byte array using <see cref="SkiaSharp"/>.
    /// </summary>
    public static byte[]? SynthesizeRec709GrayscalePng(ReadOnlySpan<byte> colorImageBytes)
    {
        if (colorImageBytes.IsEmpty)
        {
            return null;
        }

        try
        {
            using var sourceBitmap = SKBitmap.Decode(colorImageBytes);
            if (sourceBitmap is null || sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            {
                return null;
            }

            var info = new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var grayBitmap = new SKBitmap(info);
            using (var canvas = new SKCanvas(grayBitmap))
            {
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    ColorFilter = SKColorFilter.CreateColorMatrix(Rec709GrayscaleColorMatrix),
                };
                canvas.DrawBitmap(sourceBitmap, 0, 0, paint);
            }

            using var image = SKImage.FromBitmap(grayBitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
            return encoded?.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a clean 64x64 procedural vector badge PNG when offline and no remote/local icon is available.
    /// </summary>
    public static byte[] GenerateProceduralFallbackBadgePng(bool grayscale, bool isRare = false)
    {
        var info = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            SKColor bg = grayscale
                ? new SKColor(58, 62, 68)
                : (isRare ? new SKColor(108, 62, 198) : new SKColor(34, 114, 212));
            SKColor fg = grayscale
                ? new SKColor(150, 155, 162)
                : (isRare ? new SKColor(255, 218, 106) : new SKColor(255, 255, 255));

            using var bgPaint = new SKPaint { IsAntialias = true, Color = bg, Style = SKPaintStyle.Fill };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(2, 2, 62, 62), 12, 12), bgPaint);

            using var ringPaint = new SKPaint
            {
                IsAntialias = true,
                Color = fg.WithAlpha(grayscale ? (byte)90 : (byte)210),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f,
            };
            canvas.DrawCircle(32, 32, 16, ringPaint);
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    private static async Task<AchievementDefinitionDto> ProcessSingleDefinitionIconAsync(
        string iconDir,
        AchievementDefinitionDto def,
        HttpClient httpClient,
        bool allowNetworkDownloads,
        CancellationToken ct)
    {
        string safeApi = SanitizeFileStem(def.ApiName);
        string colorPath = Path.Combine(iconDir, $"{safeApi}_color.png");
        string grayPath = Path.Combine(iconDir, $"{safeApi}_gray.png");

        byte[]? colorBytes = null;
        if (File.Exists(colorPath))
        {
            colorBytes = await ResilientFileReader.ReadSharedBytesWithRetryAsync(colorPath, ct).ConfigureAwait(false);
        }

        if (colorBytes is null || colorBytes.Length == 0)
        {
            colorBytes = await ResolveSourceImageBytesAsync(def.IconPath, httpClient, allowNetworkDownloads, ct).ConfigureAwait(false);
            if (colorBytes is { Length: > 0 })
            {
                colorBytes = NormalizeToPngBytes(colorBytes) ?? colorBytes;
                await WriteBytesAtomicallyAsync(colorPath, colorBytes, ct).ConfigureAwait(false);
            }
        }

        byte[]? grayBytes = null;
        if (File.Exists(grayPath))
        {
            grayBytes = await ResilientFileReader.ReadSharedBytesWithRetryAsync(grayPath, ct).ConfigureAwait(false);
        }

        if (grayBytes is null || grayBytes.Length == 0)
        {
            bool sameUrlAsColor = !string.IsNullOrWhiteSpace(def.IconPath)
                                 && string.Equals(def.IconPath, def.IconGrayPath, StringComparison.OrdinalIgnoreCase);

            byte[]? candidateGray = null;
            if (!sameUrlAsColor && !string.IsNullOrWhiteSpace(def.IconGrayPath))
            {
                candidateGray = await ResolveSourceImageBytesAsync(def.IconGrayPath, httpClient, allowNetworkDownloads, ct).ConfigureAwait(false);
            }

            bool identicalToColor = candidateGray is { Length: > 0 }
                                    && colorBytes is { Length: > 0 }
                                    && CryptographicOperations.FixedTimeEquals(
                                        SHA256.HashData(candidateGray),
                                        SHA256.HashData(colorBytes));

            if (candidateGray is { Length: > 0 } && !identicalToColor)
            {
                grayBytes = NormalizeToPngBytes(candidateGray) ?? candidateGray;
            }
            else if (colorBytes is { Length: > 0 })
            {
                grayBytes = SynthesizeRec709GrayscalePng(colorBytes);
            }

            if (grayBytes is { Length: > 0 })
            {
                await WriteBytesAtomicallyAsync(grayPath, grayBytes, ct).ConfigureAwait(false);
            }
        }

        // Procedural badge fallback if offline and no cached icons exist yet
        if (colorBytes is null || colorBytes.Length == 0)
        {
            colorBytes = GenerateProceduralFallbackBadgePng(grayscale: false, isRare: def.IsRare);
            await WriteBytesAtomicallyAsync(colorPath, colorBytes, ct).ConfigureAwait(false);
        }

        if (grayBytes is null || grayBytes.Length == 0)
        {
            grayBytes = SynthesizeRec709GrayscalePng(colorBytes)
                        ?? GenerateProceduralFallbackBadgePng(grayscale: true, isRare: def.IsRare);
            await WriteBytesAtomicallyAsync(grayPath, grayBytes, ct).ConfigureAwait(false);
        }

        return def with
        {
            IconPath = File.Exists(colorPath) ? colorPath : def.IconPath,
            IconGrayPath = File.Exists(grayPath) ? grayPath : def.IconGrayPath,
        };
    }

    private static async Task<byte[]?> ResolveSourceImageBytesAsync(
        string? urlOrPath,
        HttpClient httpClient,
        bool allowNetworkDownloads,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
        {
            return null;
        }

        if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowNetworkDownloads)
            {
                return null;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                using var resp = await httpClient.GetAsync(urlOrPath, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        if (File.Exists(urlOrPath))
        {
            return await ResilientFileReader.ReadSharedBytesWithRetryAsync(urlOrPath, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static byte[]? NormalizeToPngBytes(ReadOnlySpan<byte> encodedBytes)
    {
        try
        {
            using var bmp = SKBitmap.Decode(encodedBytes);
            if (bmp is null)
            {
                return null;
            }

            using var img = SKImage.FromBitmap(bmp);
            using var png = img.Encode(SKEncodedImageFormat.Png, 95);
            return png?.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteBytesAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static string SanitizeFileStem(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => Array.IndexOf(invalid, c) >= 0 || c is ':' or '/' or '\\' ? '_' : c).ToArray();
        string s = new string(chars).Trim();
        return s.Length > 0 ? s : "ach";
    }
}
