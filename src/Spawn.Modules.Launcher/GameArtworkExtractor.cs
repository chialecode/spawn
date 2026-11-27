using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Core;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Resolved local artwork file paths cached inside <c>%LOCALAPPDATA%\spawn[-dev]\artwork\&lt;GameId&gt;\</c>.
/// </summary>
public sealed record ExtractedGameArtwork(
    string? CoverImagePath,
    string? IconPath,
    string? HeroImagePath,
    string? LogoImagePath = null,
    string? SidebarIconPath = null);

/// <summary>
/// Extracts zero-network offline GOG <c>webcache.zip</c> / <c>.ico</c> / PE <c>.exe</c> icons
/// and optionally enriches vertical boxart &amp; hero banners from official GOG / Steam CDNs.
/// </summary>
public sealed partial class GameArtworkExtractor
{
    private const uint ShgfiSysIconIndex = 0x00004000;
    private const int ShilExtraLarge = 0x2; // 48x48
    private const int ShilJumbo = 0x4;      // 256x256
    private const uint IldTransparent = 0x00000001;
    private const uint IldImage = 0x00000020;

    private static readonly Guid IidIImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private readonly SpawnPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GameArtworkExtractor> _logger;

    public GameArtworkExtractor(
        SpawnPaths paths,
        HttpClient? httpClient = null,
        ILogger<GameArtworkExtractor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _logger = logger ?? NullLogger<GameArtworkExtractor>.Instance;
    }

    /// <summary>
    /// Extracts offline artwork directly from a GOG offline backup directory (<c>webcache.zip</c> -&gt; <c>goggame-*.ico</c> -&gt; EXE icon).
    /// Zero network calls are required.
    /// </summary>
    public ExtractedGameArtwork ExtractGogOfflineArtwork(
        string installDir,
        string gameId,
        string? gogGameId,
        string? mainExeFullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        string artworkDir = _paths.GetGameArtworkDirectory(gameId);

        string? iconPath = null;
        string? sidebarIconPath = null;
        string? logoPath = null;
        string? backgroundPath = null;
        string? verticalCoverPath = null;

        string webcachePath = Path.Combine(installDir, "webcache.zip");
        if (File.Exists(webcachePath))
        {
            try
            {
                using var fs = new FileStream(webcachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

                var resourcesEntry = FindZipEntry(archive, "resources.json");
                if (resourcesEntry is not null)
                {
                    using var stream = resourcesEntry.Open();
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                            {
                                string normalizedKey = prop.Name.Replace('\\', '/').Trim('/');
                                string? targetName = prop.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(targetName))
                                {
                                    map[normalizedKey] = targetName;
                                }
                            }
                        }

                        iconPath = TryExtractMappedZipAsset(archive, map, ["images/icon", "icon"], artworkDir, "icon");
                        sidebarIconPath = TryExtractMappedZipAsset(archive, map, ["images/sidebarIcon2x", "images/sidebarIcon", "sidebarIcon2x", "sidebarIcon"], artworkDir, "sidebarIcon2x");
                        logoPath = TryExtractMappedZipAsset(archive, map, ["images/logo2x", "images/logo", "logo2x", "logo"], artworkDir, "logo2x");
                        backgroundPath = TryExtractMappedZipAsset(archive, map, ["images/background", "background"], artworkDir, "background");
                        verticalCoverPath = TryExtractMappedZipAsset(archive, map, ["images/boxArtImage", "images/verticalCover", "boxArtImage", "verticalCover"], artworkDir, "cover");
                    }
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed reading webcache.zip in {InstallDir}; falling back to .ico / EXE icon.", installDir);
                }
            }
        }

        // Fallback icon from goggame-<id>.ico or goggame-*.ico if webcache.zip lacked an icon.
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            iconPath = sidebarIconPath
                ?? TryCopyGogIcoFallback(installDir, gogGameId, artworkDir)
                ?? TryExtractExeIconToPng(mainExeFullPath, artworkDir);
        }

        string? coverPath = verticalCoverPath ?? logoPath ?? backgroundPath ?? iconPath;
        string? heroPath = backgroundPath ?? logoPath ?? coverPath;

        return new ExtractedGameArtwork(
            CoverImagePath: coverPath,
            IconPath: iconPath,
            HeroImagePath: heroPath,
            LogoImagePath: logoPath,
            SidebarIconPath: sidebarIconPath);
    }

    /// <summary>
    /// Extracts local icon / sidecar cover files (e.g. <c>cover.jpg</c>, <c>poster.png</c>, or PE EXE icon) for non-GOG games.
    /// </summary>
    public ExtractedGameArtwork ExtractLocalStandaloneArtwork(
        string installDir,
        string gameId,
        string? mainExeFullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        string artworkDir = _paths.GetGameArtworkDirectory(gameId);

        string? coverPath = TryCopyExistingLocalImage(
            installDir,
            artworkDir,
            targetStem: "cover",
            candidateFileStems: ["cover", "poster", "folder", "boxart", "header"]);

        string? heroPath = TryCopyExistingLocalImage(
            installDir,
            artworkDir,
            targetStem: "hero",
            candidateFileStems: ["hero", "background", "fanart", "header"]);

        string? iconPath = TryExtractExeIconToPng(mainExeFullPath, artworkDir);

        return new ExtractedGameArtwork(
            CoverImagePath: coverPath ?? heroPath ?? iconPath,
            IconPath: iconPath,
            HeroImagePath: heroPath ?? coverPath);
    }

    /// <summary>
    /// Attempts to enrich missing or non-vertical cover / hero artwork from Steam CDN and GOG Product API.
    /// Respects short network timeouts and silently preserves offline artwork when offline.
    /// </summary>
    public async Task<ExtractedGameArtwork> EnrichOnlineArtworkAsync(
        string gameId,
        int? steamAppId,
        string? gogGameId,
        ExtractedGameArtwork existing,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(existing);

        string artworkDir = _paths.GetGameArtworkDirectory(gameId);
        string? coverPath = existing.CoverImagePath;
        string? heroPath = existing.HeroImagePath;
        string? iconPath = existing.IconPath;
        string? logoPath = existing.LogoImagePath;

        bool hasDedicatedVerticalPoster = !string.IsNullOrWhiteSpace(coverPath)
            && Path.GetFileNameWithoutExtension(coverPath).StartsWith("poster", StringComparison.OrdinalIgnoreCase)
            && File.Exists(coverPath);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3.5) : timeout);
        var ct = linkedCts.Token;

        // 1. Official Steam CDN vertical 600x900 cover & hero banner when SteamAppId is known.
        if (steamAppId is > 0 && !hasDedicatedVerticalPoster)
        {
            int appId = steamAppId.Value;
            string[] coverUrls =
            [
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
            ];

            string? downloadedPoster = await TryDownloadFirstAvailableImageAsync(coverUrls, artworkDir, "poster_600x900", ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(downloadedPoster))
            {
                coverPath = downloadedPoster;
                hasDedicatedVerticalPoster = true;
            }

            if (string.IsNullOrWhiteSpace(heroPath) || string.Equals(heroPath, iconPath, StringComparison.OrdinalIgnoreCase))
            {
                string[] heroUrls =
                [
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg",
                    $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
                ];

                string? downloadedHero = await TryDownloadFirstAvailableImageAsync(heroUrls, artworkDir, "hero", ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(downloadedHero))
                {
                    heroPath = downloadedHero;
                }
            }
        }

        // 2. GOG Product API fallback when cover or hero is still missing (e.g., GOG folder without webcache.zip).
        if (!string.IsNullOrWhiteSpace(gogGameId)
            && (!hasDedicatedVerticalPoster || string.IsNullOrWhiteSpace(heroPath) || string.IsNullOrWhiteSpace(iconPath)))
        {
            try
            {
                string productUrl = $"https://api.gog.com/products/{Uri.EscapeDataString(gogGameId)}";
                using var response = await _httpClient.GetAsync(productUrl, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object)
                    {
                        if (string.IsNullOrWhiteSpace(logoPath))
                        {
                            string? logoUrl = NormalizeProtocolRelativeUrl(TryGetJsonString(images, "logo2x") ?? TryGetJsonString(images, "logo"));
                            if (!string.IsNullOrWhiteSpace(logoUrl))
                            {
                                logoPath = await TryDownloadFirstAvailableImageAsync([logoUrl], artworkDir, "logo2x_online", ct)
                                    .ConfigureAwait(false);
                                coverPath ??= logoPath;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(heroPath))
                        {
                            string? bgUrl = NormalizeProtocolRelativeUrl(TryGetJsonString(images, "background"));
                            if (!string.IsNullOrWhiteSpace(bgUrl))
                            {
                                heroPath = await TryDownloadFirstAvailableImageAsync([bgUrl], artworkDir, "background_online", ct)
                                    .ConfigureAwait(false);
                            }
                        }

                        if (string.IsNullOrWhiteSpace(iconPath))
                        {
                            string? icoUrl = NormalizeProtocolRelativeUrl(TryGetJsonString(images, "icon") ?? TryGetJsonString(images, "sidebarIcon2x"));
                            if (!string.IsNullOrWhiteSpace(icoUrl))
                            {
                                iconPath = await TryDownloadFirstAvailableImageAsync([icoUrl], artworkDir, "icon_online", ct)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "GOG Product API enrichment skipped/timed out for GOG ID {GogGameId}.", gogGameId);
                }
            }
        }

        return existing with
        {
            CoverImagePath = coverPath ?? heroPath ?? iconPath,
            IconPath = iconPath,
            HeroImagePath = heroPath ?? coverPath,
            LogoImagePath = logoPath,
        };
    }

    public static string? TryExtractExeIconToPng(string? exeFullPath, string artworkDir)
    {
        if (string.IsNullOrWhiteSpace(exeFullPath) || !File.Exists(exeFullPath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(artworkDir);
            string destPath = Path.Combine(artworkDir, "icon.png");

            using Bitmap? highResBmp = TryExtractShellImageListIconBitmap(exeFullPath);
            if (highResBmp is not null)
            {
                highResBmp.Save(destPath, ImageFormat.Png);
                return destPath;
            }

            using Icon? associatedIcon = Icon.ExtractAssociatedIcon(exeFullPath);
            if (associatedIcon is null)
            {
                return null;
            }

            using Bitmap bmp = associatedIcon.ToBitmap();
            bmp.Save(destPath, ImageFormat.Png);
            return destPath;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? TryExtractShellImageListIconBitmap(string exeFullPath)
    {
        if (!TryGetSystemIconIndex(exeFullPath, out int iconIndex))
        {
            return null;
        }

        // 1. Prefer SHIL_JUMBO (256x256) when the executable embeds a genuinely high-resolution icon.
        Bitmap? jumboBmp = TryGetIconBitmapFromShellImageList(iconIndex, ShilJumbo);
        if (jumboBmp is not null)
        {
            if (TryMeasureOpaqueBounds(jumboBmp, out Rectangle jumboBounds))
            {
                bool isSmallIconPaddedInJumboCorner =
                    jumboBmp.Width >= 128
                    && jumboBounds.Right <= jumboBmp.Width / 2
                    && jumboBounds.Bottom <= jumboBmp.Height / 2
                    && jumboBounds.Width <= 64
                    && jumboBounds.Height <= 64;

                if (!isSmallIconPaddedInJumboCorner)
                {
                    return jumboBmp;
                }
            }

            // EXE only carries a small icon (Windows places it in the top-left corner of the 256x256 JUMBO slot).
            // Fall through to SHIL_EXTRALARGE (48x48) or cleanly crop the padded JUMBO region.
            Bitmap? extraLargeBmp = TryGetIconBitmapFromShellImageList(iconIndex, ShilExtraLarge);
            if (extraLargeBmp is not null)
            {
                jumboBmp.Dispose();
                return NormalizePaddedIconBitmap(extraLargeBmp);
            }

            return NormalizePaddedIconBitmap(jumboBmp);
        }

        // 2. Fallback to SHIL_EXTRALARGE (48x48) if SHIL_JUMBO is unavailable.
        Bitmap? xlFallback = TryGetIconBitmapFromShellImageList(iconIndex, ShilExtraLarge);
        return xlFallback is null ? null : NormalizePaddedIconBitmap(xlFallback);
    }

    private static unsafe bool TryGetSystemIconIndex(string exeFullPath, out int iconIndex)
    {
        iconIndex = -1;
        SHFILEINFOW shfi = default;
        nint result = SHGetFileInfoW(exeFullPath, 0, &shfi, (uint)sizeof(SHFILEINFOW), ShgfiSysIconIndex);
        if (result == nint.Zero || shfi.iIcon < 0)
        {
            return false;
        }

        iconIndex = shfi.iIcon;
        return true;
    }

    private static unsafe Bitmap? TryGetIconBitmapFromShellImageList(int iconIndex, int shilSize)
    {
        nint imageListPtr = nint.Zero;
        nint hIcon = nint.Zero;
        try
        {
            int hr = SHGetImageList(shilSize, in IidIImageList, out imageListPtr);
            if (hr != 0 || imageListPtr == nint.Zero)
            {
                return null;
            }

            // IImageList (GUID 46EB5926-582E-4017-9FDF-E8998DAA0950) vtable layout:
            // [0] QueryInterface, [1] AddRef, [2] Release, [3..9] list ops, [10] GetIcon(int i, uint flags, out nint picon).
            nint* vtable = *(nint**)imageListPtr;
            var getIcon = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)vtable[10];
            int iconHr = getIcon(imageListPtr, iconIndex, IldTransparent | IldImage, &hIcon);
            if (iconHr != 0 || hIcon == nint.Zero)
            {
                return null;
            }

            using Icon managedIcon = Icon.FromHandle(hIcon);
            return managedIcon.ToBitmap();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != nint.Zero)
            {
                _ = DestroyIcon(hIcon);
            }

            if (imageListPtr != nint.Zero)
            {
                nint* vtable = *(nint**)imageListPtr;
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
                _ = release(imageListPtr);
            }
        }
    }

    private static Bitmap NormalizePaddedIconBitmap(Bitmap source)
    {
        if (!TryMeasureOpaqueBounds(source, out Rectangle bounds))
        {
            return source;
        }

        // If Windows Shell placed a smaller icon in the top-left corner of a larger slot, crop to a centered square.
        bool isTopLeftPadded = bounds.Right < (int)(source.Width * 0.78)
                               && bounds.Bottom < (int)(source.Height * 0.78);
        if (!isTopLeftPadded)
        {
            return source;
        }

        int side = Math.Max(bounds.Width, bounds.Height);
        side = Math.Clamp(side, 16, Math.Min(source.Width, source.Height));
        int destX = (side - bounds.Width) / 2;
        int destY = (side - bounds.Height) / 2;

        var cropped = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(cropped))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(
                source,
                new Rectangle(destX, destY, bounds.Width, bounds.Height),
                bounds,
                GraphicsUnit.Pixel);
        }

        source.Dispose();
        return cropped;
    }

    private static unsafe bool TryMeasureOpaqueBounds(Bitmap bitmap, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int minX = bitmap.Width;
            int minY = bitmap.Height;
            int maxX = -1;
            int maxY = -1;

            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            for (int y = 0; y < bitmap.Height; y++)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    byte alpha = row[(x * 4) + 3];
                    if (alpha > 8)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return false;
            }

            bounds = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            return true;
        }
        finally
        {
            if (data is not null)
            {
                bitmap.UnlockBits(data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        SHFILEINFOW* psfi,
        uint cbFileInfo,
        uint uFlags);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetImageList")]
    private static partial int SHGetImageList(int iImageList, in Guid riid, out nint ppvObj);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    private static string? TryExtractMappedZipAsset(
        ZipArchive archive,
        Dictionary<string, string> resourceMap,
        ReadOnlySpan<string> candidateKeys,
        string artworkDir,
        string outputStem)
    {
        foreach (string key in candidateKeys)
        {
            if (!resourceMap.TryGetValue(key, out string? entryRef) || string.IsNullOrWhiteSpace(entryRef))
            {
                continue;
            }

            var entry = FindZipEntry(archive, entryRef);
            if (entry is null || entry.Length <= 0)
            {
                continue;
            }

            string ext = Path.GetExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".png";
            }

            string destPath = Path.Combine(artworkDir, outputStem + ext.ToLowerInvariant());
            using var src = entry.Open();
            using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
            return destPath;
        }

        return null;
    }

    private static ZipArchiveEntry? FindZipEntry(ZipArchive archive, string targetPathOrName)
    {
        string normalized = targetPathOrName.Replace('\\', '/').TrimStart('/');
        string fileNameOnly = Path.GetFileName(normalized);

        foreach (var entry in archive.Entries)
        {
            string entryNorm = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.Equals(entryNorm, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.Name, fileNameOnly, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string? TryCopyGogIcoFallback(string installDir, string? gogGameId, string artworkDir)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(gogGameId))
            {
                string exactIco = Path.Combine(installDir, $"goggame-{gogGameId}.ico");
                if (File.Exists(exactIco))
                {
                    string dest = Path.Combine(artworkDir, "icon.ico");
                    File.Copy(exactIco, dest, overwrite: true);
                    return dest;
                }
            }

            foreach (string icoFile in Directory.EnumerateFiles(installDir, "goggame-*.ico", SearchOption.TopDirectoryOnly))
            {
                string dest = Path.Combine(artworkDir, "icon.ico");
                File.Copy(icoFile, dest, overwrite: true);
                return dest;
            }
        }
        catch
        {
            // Ignore IO errors on read-only media.
        }

        return null;
    }

    private static string? TryCopyExistingLocalImage(
        string installDir,
        string artworkDir,
        string targetStem,
        ReadOnlySpan<string> candidateFileStems)
    {
        string[] extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
        try
        {
            foreach (string stem in candidateFileStems)
            {
                foreach (string ext in extensions)
                {
                    string candidate = Path.Combine(installDir, stem + ext);
                    if (File.Exists(candidate))
                    {
                        string dest = Path.Combine(artworkDir, targetStem + ext);
                        File.Copy(candidate, dest, overwrite: true);
                        return dest;
                    }
                }
            }
        }
        catch
        {
            // Ignore inaccessible files.
        }

        return null;
    }

    private async Task<string?> TryDownloadFirstAvailableImageAsync(
        IEnumerable<string> urls,
        string artworkDir,
        string outputStem,
        CancellationToken cancellationToken)
    {
        foreach (string url in urls)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length < 256)
                {
                    continue;
                }

                string ext = GuessImageExtension(url, mediaType);
                string destPath = Path.Combine(artworkDir, outputStem + ext);
                await File.WriteAllBytesAsync(destPath, bytes, cancellationToken).ConfigureAwait(false);
                return destPath;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                // Try next mirror URL or bail out cleanly if cancelled.
            }
        }

        return null;
    }

    private static string GuessImageExtension(string url, string? mediaType)
    {
        if (string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        if (string.Equals(mediaType, "image/webp", StringComparison.OrdinalIgnoreCase))
        {
            return ".webp";
        }

        try
        {
            var uri = new Uri(url);
            string ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 5)
            {
                return ext.ToLowerInvariant();
            }
        }
        catch
        {
            // Ignore URI parse failure.
        }

        return ".jpg";
    }

    private static string? NormalizeProtocolRelativeUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        string trimmed = rawUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        return trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
    }

    private static string? TryGetJsonString(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    internal static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnLauncher/1.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }
}
