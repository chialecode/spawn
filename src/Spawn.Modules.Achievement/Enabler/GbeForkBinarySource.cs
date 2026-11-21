using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spawn.Core;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Supplies the <c>gbe_fork</c> replacement <c>steam_api(64).dll</c> bytes used by <see cref="GbeForkActivatorService"/>.
/// Production uses <see cref="PinnedGbeForkReleaseSource"/>; tests inject their own deterministic source instead of
/// relying on any test-only branch inside production code.
/// </summary>
public interface IGbeForkBinarySource
{
    /// <summary>Returns the replacement DLL bytes, or <see langword="null"/> when unavailable (offline / integrity failure).</summary>
    Task<byte[]?> GetSteamApiDllAsync(bool is64Bit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads exactly one pinned official <c>Detanup01/gbe_fork</c> Windows release asset, verifies its SHA-256 before
/// extraction, and caches the extracted DLLs in a tag-scoped folder guarded by a SHA-256 sidecar manifest.
/// Upgrading the pinned version is a deliberate code change (new tag + new digest), never an implicit "latest".
/// </summary>
public sealed class PinnedGbeForkReleaseSource : IGbeForkBinarySource, IDisposable
{
    /// <summary>Pinned upstream release tag (published 2026-09-27, past the 7-day dependency cooldown).</summary>
    public const string PinnedReleaseTag = "release-2026_09_27";

    /// <summary>Pinned Windows regular release archive name within <see cref="PinnedReleaseTag"/>.</summary>
    public const string PinnedAssetName = "emu-win-release-vs22.7z";

    /// <summary>SHA-256 of <see cref="PinnedAssetName"/>, matching the GitHub release asset digest and independently re-hashed.</summary>
    public const string PinnedAssetSha256 = "300186c34f49a9ea75b33ce2af299267cc1dad0225cf45e81a229578d4e11c85";

    public static readonly string PinnedAssetUrl =
        $"https://github.com/Detanup01/gbe_fork/releases/download/{PinnedReleaseTag}/{PinnedAssetName}";

    private const string CacheManifestFileName = "pinned-cache.json";

    private readonly SpawnPaths _paths;
    private readonly AchievementModuleOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public PinnedGbeForkReleaseSource(SpawnPaths paths, AchievementModuleOptions? options = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _options = options ?? new AchievementModuleOptions();

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SPAWN/2.0 (Windows NT 10.0; Native Desktop)");
            _ownsHttpClient = true;
        }
    }

    /// <summary>Tag-scoped cache folder, e.g. <c>cache\gbe_fork\release-2026_09_27</c>.</summary>
    public string PinnedCacheDirectory => Path.Combine(_paths.GbeForkCacheDirectory, PinnedReleaseTag);

    public async Task<byte[]?> GetSteamApiDllAsync(bool is64Bit, CancellationToken cancellationToken = default)
    {
        string dllName = is64Bit ? "steam_api64.dll" : "steam_api.dll";

        byte[]? cached = await TryReadVerifiedCachedDllAsync(dllName, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        if (!_options.AllowNetworkDownloads)
        {
            return null;
        }

        bool populated = await TryDownloadAndPopulateCacheAsync(cancellationToken).ConfigureAwait(false);
        return populated
            ? await TryReadVerifiedCachedDllAsync(dllName, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>Returns <see langword="true"/> only when <paramref name="archiveBytes"/> hashes to <see cref="PinnedAssetSha256"/>.</summary>
    public static bool IsPinnedArchiveDigest(ReadOnlySpan<byte> archiveBytes) =>
        string.Equals(Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), PinnedAssetSha256, StringComparison.Ordinal);

    private async Task<byte[]?> TryReadVerifiedCachedDllAsync(string dllName, CancellationToken ct)
    {
        string dllPath = Path.Combine(PinnedCacheDirectory, dllName);
        string manifestPath = Path.Combine(PinnedCacheDirectory, CacheManifestFileName);
        if (!File.Exists(dllPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            await using var ms = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<PinnedCacheManifest>(ms, cancellationToken: ct).ConfigureAwait(false);
            if (manifest is null
                || !string.Equals(manifest.ReleaseTag, PinnedReleaseTag, StringComparison.Ordinal)
                || !string.Equals(manifest.AssetSha256, PinnedAssetSha256, StringComparison.OrdinalIgnoreCase)
                || !manifest.DllSha256.TryGetValue(dllName, out string? expectedDllSha))
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(dllPath, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedDllSha, StringComparison.OrdinalIgnoreCase)
                ? bytes
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task<bool> TryDownloadAndPopulateCacheAsync(CancellationToken ct)
    {
        string stagingRoot = Path.Combine(_paths.CacheDirectory, $"gbe_dl_{Guid.NewGuid():N}");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            byte[] archiveBytes = await _httpClient.GetByteArrayAsync(PinnedAssetUrl, cts.Token).ConfigureAwait(false);

            // Fail closed: never extract or execute anything whose digest differs from the pinned value.
            if (!IsPinnedArchiveDigest(archiveBytes))
            {
                return false;
            }

            Directory.CreateDirectory(stagingRoot);
            string archivePath = Path.Combine(stagingRoot, PinnedAssetName);
            string extractDir = Path.Combine(stagingRoot, "extracted");
            Directory.CreateDirectory(extractDir);
            await File.WriteAllBytesAsync(archivePath, archiveBytes, cts.Token).ConfigureAwait(false);

            bool extracted = PinnedAssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? TryExtractZip(archivePath, extractDir)
                : await ExtractSevenZipSilentlyAsync(archivePath, extractDir, cts.Token).ConfigureAwait(false);
            if (!extracted)
            {
                return false;
            }

            string[] allDlls = Directory.GetFiles(extractDir, "steam_api*.dll", SearchOption.AllDirectories);
            Directory.CreateDirectory(PinnedCacheDirectory);
            var dllHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string want in new[] { "steam_api64.dll", "steam_api.dll" })
            {
                string? bestMatch = allDlls
                    .Where(f => string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase)
                                && f.Contains("regular", StringComparison.OrdinalIgnoreCase)
                                && !f.Contains("experimental", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault()
                    ?? allDlls.FirstOrDefault(f => string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase));

                if (bestMatch is null)
                {
                    continue;
                }

                string savePath = Path.Combine(PinnedCacheDirectory, want);
                string tempSave = savePath + ".tmp";
                File.Copy(bestMatch, tempSave, overwrite: true);
                File.Move(tempSave, savePath, overwrite: true);
                dllHashes[want] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(savePath, cts.Token).ConfigureAwait(false)));
            }

            if (dllHashes.Count == 0)
            {
                return false;
            }

            var manifest = new PinnedCacheManifest
            {
                ReleaseTag = PinnedReleaseTag,
                AssetName = PinnedAssetName,
                AssetSha256 = PinnedAssetSha256,
                DllSha256 = dllHashes,
            };
            string manifestPath = Path.Combine(PinnedCacheDirectory, CacheManifestFileName);
            await File.WriteAllTextAsync(manifestPath + ".tmp", JsonSerializer.Serialize(manifest), Encoding.UTF8, cts.Token).ConfigureAwait(false);
            File.Move(manifestPath + ".tmp", manifestPath, overwrite: true);

            string cacheNotice = Path.Combine(PinnedCacheDirectory, "THIRD-PARTY-NOTICES-gbe_fork.txt");
            if (!File.Exists(cacheNotice))
            {
                await File.WriteAllTextAsync(
                    cacheNotice,
                    $"Detanup01/gbe_fork {PinnedReleaseTag} ({PinnedAssetName}, sha256 {PinnedAssetSha256}) is licensed under GNU LGPL v3.0 (https://github.com/Detanup01/gbe_fork).",
                    Encoding.UTF8,
                    cts.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup race.
            }
        }
    }

    private static bool TryExtractZip(string archivePath, string extractDir)
    {
        try
        {
            ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> ExtractSevenZipSilentlyAsync(string archivePath, string extractDir, CancellationToken ct)
    {
        // Primary: Windows 11 built-in System32\tar.exe (bsdtar / libarchive with .7z support); fallback: installed 7-Zip CLI.
        string sysTar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (File.Exists(sysTar) && await RunHiddenAsync(sysTar, ["-xf", archivePath, "-C", extractDir], ct).ConfigureAwait(false))
        {
            return true;
        }

        string[] sevenZipCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        ];

        foreach (string sevenZip in sevenZipCandidates)
        {
            if (File.Exists(sevenZip) && await RunHiddenAsync(sevenZip, ["x", archivePath, $"-o{extractDir}", "-y"], ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> RunHiddenAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return false;
        }

        try
        {
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process may have already exited.
        }

        Task<string> drainOut = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> drainErr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
        return proc.ExitCode == 0;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class PinnedCacheManifest
    {
        public string ReleaseTag { get; init; } = string.Empty;
        public string AssetName { get; init; } = string.Empty;
        public string AssetSha256 { get; init; } = string.Empty;
        public Dictionary<string, string> DllSha256 { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
