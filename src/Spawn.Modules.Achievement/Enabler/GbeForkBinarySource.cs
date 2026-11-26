using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spawn.Core;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Distinct failure categories when acquiring the pinned <c>gbe_fork</c> release binaries.
/// </summary>
public enum GbeForkAcquisitionErrorKind
{
    None = 0,
    OfflineDisabled = 1,
    NetworkDownloadFailed = 2,
    Sha256VerificationFailed = 3,
    MissingSevenZipExtractor = 4,
    ExtractionOrPayloadFailed = 5,
}

/// <summary>
/// Outcome of attempting to extract a <c>.7z</c> archive via OS or 7-Zip tooling.
/// </summary>
public enum SevenZipExtractOutcome
{
    Succeeded = 0,
    MissingExtractor = 1,
    ExtractionFailed = 2,
}

/// <summary>
/// Structured outcome returned by <see cref="IGbeForkBinarySource.AcquireSteamApiDllAsync"/>.
/// </summary>
public sealed record GbeForkBinaryAcquisitionResult(
    byte[]? DllBytes,
    GbeForkAcquisitionErrorKind ErrorKind = GbeForkAcquisitionErrorKind.None,
    string? ErrorMessage = null)
{
    public bool Succeeded => DllBytes is { Length: > 0 } && ErrorKind == GbeForkAcquisitionErrorKind.None;
}

/// <summary>
/// Supplies the <c>gbe_fork</c> replacement <c>steam_api(64).dll</c> bytes used by <see cref="GbeForkActivatorService"/>.
/// Production uses <see cref="PinnedGbeForkReleaseSource"/>; tests inject their own deterministic source instead of
/// relying on any test-only branch inside production code.
/// </summary>
public interface IGbeForkBinarySource
{
    /// <summary>Returns the replacement DLL bytes, or <see langword="null"/> when unavailable (offline / integrity failure).</summary>
    Task<byte[]?> GetSteamApiDllAsync(bool is64Bit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns both the replacement DLL bytes and a classified error reason when acquisition fails.
    /// Default implementation preserves full backward compatibility for existing test stubs.
    /// </summary>
    async Task<GbeForkBinaryAcquisitionResult> AcquireSteamApiDllAsync(bool is64Bit, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await GetSteamApiDllAsync(is64Bit, cancellationToken).ConfigureAwait(false);
        return bytes is { Length: > 0 }
            ? new GbeForkBinaryAcquisitionResult(bytes)
            : new GbeForkBinaryAcquisitionResult(
                null,
                GbeForkAcquisitionErrorKind.NetworkDownloadFailed,
                $"未能获取经 SHA-256 校验的固定版本 gbe_fork {PinnedGbeForkReleaseSource.PinnedReleaseTag} 组件 {(is64Bit ? "steam_api64.dll" : "steam_api.dll")}。");
    }
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
    private readonly Func<ReadOnlyMemory<byte>, bool> _archiveDigestVerifier;
    private readonly Func<string, string, CancellationToken, Task<SevenZipExtractOutcome>> _sevenZipExtractor;

    public GbeForkAcquisitionErrorKind LastErrorKind { get; private set; }
    public string? LastErrorMessage { get; private set; }

    public PinnedGbeForkReleaseSource(
        SpawnPaths paths,
        AchievementModuleOptions? options = null,
        HttpClient? httpClient = null)
        : this(paths, options, httpClient, archiveDigestVerifier: null, sevenZipExtractor: null)
    {
    }

    public static PinnedGbeForkReleaseSource CreateForTesting(
        SpawnPaths paths,
        AchievementModuleOptions? options,
        HttpClient? httpClient,
        Func<ReadOnlyMemory<byte>, bool>? archiveDigestVerifier = null,
        Func<string, string, CancellationToken, Task<SevenZipExtractOutcome>>? sevenZipExtractor = null) =>
        new(paths, options, httpClient, archiveDigestVerifier, sevenZipExtractor);

    private PinnedGbeForkReleaseSource(
        SpawnPaths paths,
        AchievementModuleOptions? options,
        HttpClient? httpClient,
        Func<ReadOnlyMemory<byte>, bool>? archiveDigestVerifier,
        Func<string, string, CancellationToken, Task<SevenZipExtractOutcome>>? sevenZipExtractor)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _options = options ?? new AchievementModuleOptions();
        _archiveDigestVerifier = archiveDigestVerifier ?? (static bytes => IsPinnedArchiveDigest(bytes.Span));
        _sevenZipExtractor = sevenZipExtractor ?? ExtractSevenZipSilentlyAsync;

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
        GbeForkBinaryAcquisitionResult result = await AcquireSteamApiDllAsync(is64Bit, cancellationToken).ConfigureAwait(false);
        return result.DllBytes;
    }

    public async Task<GbeForkBinaryAcquisitionResult> AcquireSteamApiDllAsync(
        bool is64Bit,
        CancellationToken cancellationToken = default)
    {
        string dllName = is64Bit ? "steam_api64.dll" : "steam_api.dll";

        byte[]? cached = await TryReadVerifiedCachedDllAsync(dllName, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            RecordError(GbeForkAcquisitionErrorKind.None, null);
            return new GbeForkBinaryAcquisitionResult(cached);
        }

        if (!_options.AllowNetworkDownloads)
        {
            string msg = $"本地未缓存已校验的 gbe_fork {PinnedReleaseTag} 组件（{dllName}），且当前已禁用联网下载。";
            RecordError(GbeForkAcquisitionErrorKind.OfflineDisabled, msg);
            return new GbeForkBinaryAcquisitionResult(null, GbeForkAcquisitionErrorKind.OfflineDisabled, msg);
        }

        var (populateError, populateMessage) = await TryDownloadAndPopulateCacheAsync(cancellationToken).ConfigureAwait(false);
        if (populateError != GbeForkAcquisitionErrorKind.None)
        {
            RecordError(populateError, populateMessage);
            return new GbeForkBinaryAcquisitionResult(null, populateError, populateMessage);
        }

        byte[]? populatedDll = await TryReadVerifiedCachedDllAsync(dllName, cancellationToken).ConfigureAwait(false);
        if (populatedDll is { Length: > 0 })
        {
            RecordError(GbeForkAcquisitionErrorKind.None, null);
            return new GbeForkBinaryAcquisitionResult(populatedDll);
        }

        string missingDllMsg = $"已解压 gbe_fork {PinnedReleaseTag} 压缩包，但未在归档中找到所需的 {dllName}。";
        RecordError(GbeForkAcquisitionErrorKind.ExtractionOrPayloadFailed, missingDllMsg);
        return new GbeForkBinaryAcquisitionResult(null, GbeForkAcquisitionErrorKind.ExtractionOrPayloadFailed, missingDllMsg);
    }

    /// <summary>Returns <see langword="true"/> only when <paramref name="archiveBytes"/> hashes to <see cref="PinnedAssetSha256"/>.</summary>
    public static bool IsPinnedArchiveDigest(ReadOnlySpan<byte> archiveBytes) =>
        string.Equals(Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), PinnedAssetSha256, StringComparison.Ordinal);

    private void RecordError(GbeForkAcquisitionErrorKind kind, string? message)
    {
        LastErrorKind = kind;
        LastErrorMessage = message;
    }

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

    private async Task<(GbeForkAcquisitionErrorKind ErrorKind, string? ErrorMessage)> TryDownloadAndPopulateCacheAsync(CancellationToken ct)
    {
        string stagingRoot = Path.Combine(_paths.CacheDirectory, $"gbe_dl_{Guid.NewGuid():N}");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            byte[] archiveBytes;
            try
            {
                using var response = await _httpClient.GetAsync(PinnedAssetUrl, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (
                        GbeForkAcquisitionErrorKind.NetworkDownloadFailed,
                        $"网络下载 gbe_fork {PinnedReleaseTag} 失败（HTTP {(int)response.StatusCode} {response.ReasonPhrase}），请检查网络连接或代理设置后重试。");
                }

                archiveBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return (
                    GbeForkAcquisitionErrorKind.NetworkDownloadFailed,
                    $"网络下载 gbe_fork {PinnedReleaseTag} 失败：{ex.Message}。请检查网络连接或 GitHub 访问代理后重试。");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (
                    GbeForkAcquisitionErrorKind.NetworkDownloadFailed,
                    $"网络下载 gbe_fork {PinnedReleaseTag} 超时（超过 60 秒），请检查网络连接后重试。");
            }

            // Fail closed: never extract or execute anything whose digest differs from the pinned value.
            if (!_archiveDigestVerifier(archiveBytes))
            {
                string actualSha = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
                string actualPrefix = actualSha[..Math.Min(12, actualSha.Length)];
                string expectedPrefix = PinnedAssetSha256[..12];
                return (
                    GbeForkAcquisitionErrorKind.Sha256VerificationFailed,
                    $"gbe_fork {PinnedReleaseTag} 归档 SHA-256 校验失败（实际 {actualPrefix}…，期望 {expectedPrefix}…），已出于安全原因中止解包，请检查网络劫持或代理缓存后重试。");
            }

            Directory.CreateDirectory(stagingRoot);
            string archivePath = Path.Combine(stagingRoot, PinnedAssetName);
            string extractDir = Path.Combine(stagingRoot, "extracted");
            Directory.CreateDirectory(extractDir);
            await File.WriteAllBytesAsync(archivePath, archiveBytes, cts.Token).ConfigureAwait(false);

            SevenZipExtractOutcome extractOutcome = PinnedAssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? (TryExtractZip(archivePath, extractDir) ? SevenZipExtractOutcome.Succeeded : SevenZipExtractOutcome.ExtractionFailed)
                : await _sevenZipExtractor(archivePath, extractDir, cts.Token).ConfigureAwait(false);

            if (extractOutcome == SevenZipExtractOutcome.MissingExtractor)
            {
                return (
                    GbeForkAcquisitionErrorKind.MissingSevenZipExtractor,
                    $"系统缺少可用的 .7z 解压工具：当前系统的 tar.exe 不支持解压 {PinnedAssetName} 且未检测到 7-Zip。Windows 10 用户请先安装免费开源的 7-Zip（7-zip.org）后再重试激活。");
            }

            if (extractOutcome != SevenZipExtractOutcome.Succeeded)
            {
                return (
                    GbeForkAcquisitionErrorKind.ExtractionOrPayloadFailed,
                    $"解压 gbe_fork 归档 {PinnedAssetName} 失败，请检查磁盘空间或安全软件拦截后重试。");
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
                return (
                    GbeForkAcquisitionErrorKind.ExtractionOrPayloadFailed,
                    $"已解压 gbe_fork {PinnedReleaseTag} 归档，但未在内容中找到 steam_api64.dll 或 steam_api.dll。");
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

            return (GbeForkAcquisitionErrorKind.None, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (GbeForkAcquisitionErrorKind.NetworkDownloadFailed, "操作已取消。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (
                GbeForkAcquisitionErrorKind.ExtractionOrPayloadFailed,
                $"缓存或解压 gbe_fork 组件时发生磁盘错误：{ex.Message}");
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

    private static async Task<SevenZipExtractOutcome> ExtractSevenZipSilentlyAsync(string archivePath, string extractDir, CancellationToken ct)
    {
        // Primary: Windows 11 built-in System32\tar.exe (bsdtar / libarchive with .7z support).
        // Note: Windows 10 ships an older bsdtar without .7z support, so tar.exe failure without 7z.exe is classified as MissingExtractor.
        string sysTar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (File.Exists(sysTar) && await RunHiddenAsync(sysTar, ["-xf", archivePath, "-C", extractDir], ct).ConfigureAwait(false))
        {
            return SevenZipExtractOutcome.Succeeded;
        }

        var sevenZipCandidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        };

        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            foreach (string segment in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(segment, "7z.exe");
                    if (!sevenZipCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        sevenZipCandidates.Add(candidate);
                    }
                }
                catch
                {
                    // Ignore malformed PATH entry.
                }
            }
        }

        bool foundSevenZipCli = false;
        foreach (string sevenZip in sevenZipCandidates)
        {
            if (!File.Exists(sevenZip))
            {
                continue;
            }

            foundSevenZipCli = true;
            if (await RunHiddenAsync(sevenZip, ["x", archivePath, $"-o{extractDir}", "-y"], ct).ConfigureAwait(false))
            {
                return SevenZipExtractOutcome.Succeeded;
            }
        }

        return foundSevenZipCli
            ? SevenZipExtractOutcome.ExtractionFailed
            : SevenZipExtractOutcome.MissingExtractor;
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
