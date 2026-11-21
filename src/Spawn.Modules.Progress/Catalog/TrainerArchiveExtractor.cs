using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Single manifest entry declared by an archive before extraction.
/// </summary>
public sealed record ArchiveManifestEntry(
    string RawPath,
    string NormalizedRelativePath,
    long ExpectedSize,
    bool IsDirectory,
    bool IsSymlinkOrReparsePoint,
    bool IsEncrypted);

/// <summary>
/// Outcome of listing archive entries via an external <c>7z.exe</c> Mode C fallback runner.
/// </summary>
public sealed record ExternalSevenZipListResult(
    bool Succeeded,
    IReadOnlyList<ArchiveManifestEntry> Entries,
    string? ErrorMessage = null);

/// <summary>
/// Outcome of extracting an archive via an external <c>7z.exe</c> Mode C fallback runner.
/// </summary>
public sealed record ExternalSevenZipExtractResult(
    bool Succeeded,
    int ExitCode,
    string? ErrorMessage = null);

/// <summary>
/// Injectable Mode C fallback process executor invoking user-installed <c>7z.exe</c>
/// strictly when SharpCompress throws an unsupported archive format error.
/// </summary>
public interface IExternalSevenZipRunner
{
    bool IsAvailable { get; }
    Task<ExternalSevenZipListResult> ListEntriesAsync(string archivePath, CancellationToken ct = default);
    Task<ExternalSevenZipExtractResult> ExtractAsync(string archivePath, string destinationDirectory, CancellationToken ct = default);
}

/// <summary>
/// Outcome of safely extracting and verifying an archive into a staging directory.
/// </summary>
public sealed record ArchiveExtractionOutcome(
    bool Succeeded,
    TrainerImportFailureKind FailureKind,
    IReadOnlyList<string> ExtractedFileRelativePaths,
    bool UsedSevenZipFallback = false,
    int? Win32ErrorCode = null,
    string? Message = null);

/// <summary>
/// Production Mode C <c>7z.exe</c> runner enforcing foreground silence (<c>CreateNoWindow = true</c>,
/// <c>UseShellExecute = false</c>, <c>BelowNormal</c> priority, safe <c>ArgumentList</c>, and bounded timeout).
/// </summary>
public sealed class ProcessExternalSevenZipRunner : IExternalSevenZipRunner
{
    private readonly string? _executablePath;
    private readonly TimeSpan _timeout;

    public ProcessExternalSevenZipRunner(string? explicitExecutablePath = null, TimeSpan? timeout = null)
    {
        _executablePath = !string.IsNullOrWhiteSpace(explicitExecutablePath)
            ? (File.Exists(explicitExecutablePath) ? explicitExecutablePath : null)
            : DiscoverSystemSevenZipPath();
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_executablePath) && File.Exists(_executablePath);

    public async Task<ExternalSevenZipListResult> ListEntriesAsync(string archivePath, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return new ExternalSevenZipListResult(false, [], "本机未安装 7z.exe。");
        }

        var (exitCode, stdout, stderr) = await RunSevenZipAsync(["l", "-slt", "-ba", "--", archivePath], ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return new ExternalSevenZipListResult(false, [], string.IsNullOrWhiteSpace(stderr) ? $"7z 列表失败 (退出码 {exitCode})" : stderr.Trim());
        }

        var entries = ParseSevenZipTechnicalListing(stdout);
        return new ExternalSevenZipListResult(true, entries);
    }

    public async Task<ExternalSevenZipExtractResult> ExtractAsync(string archivePath, string destinationDirectory, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return new ExternalSevenZipExtractResult(false, -1, "本机未安装 7z.exe。");
        }

        Directory.CreateDirectory(destinationDirectory);
        var (exitCode, _, stderr) = await RunSevenZipAsync(
            ["x", "-y", "-bd", $"-o{destinationDirectory}", "--", archivePath],
            ct).ConfigureAwait(false);

        return new ExternalSevenZipExtractResult(
            Succeeded: exitCode == 0,
            ExitCode: exitCode,
            ErrorMessage: exitCode == 0 ? null : (string.IsNullOrWhiteSpace(stderr) ? $"7z 解压失败 (退出码 {exitCode})" : stderr.Trim()));
    }

    internal static IReadOnlyList<ArchiveManifestEntry> ParseSevenZipTechnicalListing(string stdout)
    {
        var list = new List<ArchiveManifestEntry>();
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return list;
        }

        string? path = null;
        long size = 0;
        bool isFolder = false;
        bool isLink = false;
        bool isEncrypted = false;

        void FlushBlock()
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            list.Add(new ArchiveManifestEntry(
                RawPath: path,
                NormalizedRelativePath: path,
                ExpectedSize: size,
                IsDirectory: isFolder,
                IsSymlinkOrReparsePoint: isLink,
                IsEncrypted: isEncrypted));

            path = null;
            size = 0;
            isFolder = false;
            isLink = false;
            isEncrypted = false;
        }

        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushBlock();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                path = val;
            }
            else if (key.Equals("Size", StringComparison.OrdinalIgnoreCase)
                     && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize))
            {
                size = parsedSize;
            }
            else if (key.Equals("Folder", StringComparison.OrdinalIgnoreCase))
            {
                isFolder = val == "+";
            }
            else if (key.Equals("Encrypted", StringComparison.OrdinalIgnoreCase))
            {
                isEncrypted = val == "+";
            }
            else if (key.Equals("Symbolic Link", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("Hard Link", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(val))
                {
                    isLink = true;
                }
            }
            else if (key.Equals("Attributes", StringComparison.OrdinalIgnoreCase))
            {
                if (val.Contains('L', StringComparison.OrdinalIgnoreCase) || val.Contains('l'))
                {
                    isLink = true;
                }

                if (val.Contains('D', StringComparison.OrdinalIgnoreCase) && !val.Contains('A'))
                {
                    // Keep explicit Folder flag authoritative, only note directory markers.
                }
            }
        }

        FlushBlock();
        return list;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunSevenZipAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executablePath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return (-1, string.Empty, "无法启动 7z.exe 进程。");
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Process may exit rapidly before priority adjustment.
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout > TimeSpan.Zero ? _timeout : TimeSpan.FromSeconds(30));

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore race if process already exited.
            }

            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }

    private static string? DiscoverSystemSevenZipPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        ];

        foreach (string path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            foreach (string dir in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, "7z.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Safe multi-format trainer archive extractor:
/// <list type="bullet">
///   <item><c>.zip</c> via BCL <see cref="System.IO.Compression.ZipArchive"/>;</item>
///   <item><c>.rar</c> (RAR4 / RAR5 / Solid) and <c>.7z</c> via <c>SharpCompress 0.50.9</c>;</item>
///   <item>Manifest pre-inspection + strict Zip-Slip / reserved device name / ReparsePoint rejection;</item>
///   <item>Post-extraction file-by-file byte-length verification (missing/truncated =&gt; <see cref="TrainerImportFailureKind.AntivirusQuarantined"/>);</item>
///   <item>Win32 225/226 quarantine classification and Mode C <c>7z.exe</c> fallback on unsupported formats.</item>
/// </list>
/// </summary>
public sealed class TrainerArchiveExtractor
{
    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly TrainerCatalogOptions _options;
    private readonly IExternalSevenZipRunner _sevenZipRunner;
    private readonly ILogger<TrainerArchiveExtractor> _logger;

    /// <summary>
    /// Internal deterministic test hook invoked immediately after extraction completes and immediately before
    /// post-extraction manifest byte verification (enables race-free AV silent-deletion / truncation unit tests).
    /// </summary>
    internal Action<string>? OnBeforePostExtractVerification { get; set; }

    public TrainerArchiveExtractor(
        TrainerCatalogOptions? options = null,
        IExternalSevenZipRunner? sevenZipRunner = null,
        ILogger<TrainerArchiveExtractor>? logger = null)
    {
        _options = options ?? new TrainerCatalogOptions();
        _sevenZipRunner = sevenZipRunner ?? new ProcessExternalSevenZipRunner();
        _logger = logger ?? NullLogger<TrainerArchiveExtractor>.Instance;
    }

    /// <summary>
    /// Validates a single archive entry path against Zip-Slip traversal, absolute paths, drive/UNC prefixes,
    /// ADS colons, Windows reserved device names, trailing dots/spaces, and invalid characters.
    /// </summary>
    public static bool TryValidateArchiveEntryPath(
        string? rawEntryPath,
        string destinationRoot,
        out string normalizedRelativePath,
        out string fullTargetPath,
        out string? rejectionReason)
    {
        normalizedRelativePath = string.Empty;
        fullTargetPath = string.Empty;
        rejectionReason = null;

        if (string.IsNullOrWhiteSpace(rawEntryPath))
        {
            rejectionReason = "压缩包条目路径为空。";
            return false;
        }

        foreach (char ch in rawEntryPath)
        {
            if (ch == '\0' || ch < 0x20)
            {
                rejectionReason = "压缩包条目包含非法控制字符。";
                return false;
            }
        }

        string trimmed = rawEntryPath.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\') || trimmed.Contains(':'))
        {
            rejectionReason = $"检测到绝对路径、盘符或 ADS 冒号：{rawEntryPath}";
            return false;
        }

        if (Path.IsPathRooted(trimmed))
        {
            rejectionReason = $"检测到根路径条目：{rawEntryPath}";
            return false;
        }

        string[] rawSegments = trimmed.Split(['/', '\\'], StringSplitOptions.None);
        var cleanSegments = new List<string>(rawSegments.Length);

        for (int i = 0; i < rawSegments.Length; i++)
        {
            string seg = rawSegments[i];
            if (seg.Length == 0)
            {
                // Allow a single trailing slash for directory entries; reject empty interior segments.
                if (i == rawSegments.Length - 1)
                {
                    continue;
                }

                rejectionReason = $"压缩包条目包含非法空路径段：{rawEntryPath}";
                return false;
            }

            if (seg == "." || seg == "..")
            {
                rejectionReason = $"检测到目录穿越路径段（Zip-Slip）：{rawEntryPath}";
                return false;
            }

            if (seg.EndsWith('.') || seg.EndsWith(' '))
            {
                rejectionReason = $"检测到尾部点号/空格路径伪装：{rawEntryPath}";
                return false;
            }

            if (seg.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                rejectionReason = $"压缩包条目包含非法文件名字符：{rawEntryPath}";
                return false;
            }

            string stemBeforeDot = seg.Contains('.') ? seg[..seg.IndexOf('.')] : seg;
            if (WindowsReservedDeviceNames.Contains(seg) || WindowsReservedDeviceNames.Contains(stemBeforeDot))
            {
                rejectionReason = $"检测到 Windows 保留设备名：{rawEntryPath}";
                return false;
            }

            cleanSegments.Add(seg);
        }

        if (cleanSegments.Count == 0)
        {
            rejectionReason = $"压缩包条目未包含有效文件名：{rawEntryPath}";
            return false;
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        string relPath = Path.Combine([.. cleanSegments]);
        string combinedFull = Path.GetFullPath(Path.Combine(normalizedRoot, relPath));

        if (!combinedFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"目标路径越界沙箱暂存目录（Zip-Slip）：{rawEntryPath}";
            return false;
        }

        normalizedRelativePath = relPath;
        fullTargetPath = combinedFull;
        return true;
    }

    /// <summary>
    /// Extracts an archive identified by <paramref name="format"/> into <paramref name="destinationDirectory"/>
    /// with pre-extraction manifest inspection, Zip-Slip validation, byte-count verification, and Mode C fallback.
    /// </summary>
    public async Task<ArchiveExtractionOutcome> ExtractAndVerifyAsync(
        string archivePath,
        TrainerPayloadFormat format,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);

        try
        {
            return format switch
            {
                TrainerPayloadFormat.ZipArchive => await ExtractZipAsync(archivePath, destinationDirectory, ct).ConfigureAwait(false),
                TrainerPayloadFormat.RarArchive => await ExtractSharpCompressWithFallbackAsync(archivePath, isRar: true, destinationDirectory, ct).ConfigureAwait(false),
                TrainerPayloadFormat.SevenZipArchive => await ExtractSharpCompressWithFallbackAsync(archivePath, isRar: false, destinationDirectory, ct).ConfigureAwait(false),
                _ => new ArchiveExtractionOutcome(
                    Succeeded: false,
                    FailureKind: TrainerImportFailureKind.InvalidMagicOrFormat,
                    ExtractedFileRelativePaths: [],
                    Message: "不支持的压缩包格式。"),
            };
        }
        catch (OperationCanceledException)
        {
            return new ArchiveExtractionOutcome(false, TrainerImportFailureKind.Cancelled, [], Message: "解压已取消。");
        }
        catch (Exception ex) when (PeBinaryInspector.ClassifyOsSecurityError(PeBinaryInspector.TryExtractWin32ErrorCode(ex)) != TrainerImportFailureKind.None)
        {
            int? win32 = PeBinaryInspector.TryExtractWin32ErrorCode(ex);
            var kind = PeBinaryInspector.ClassifyOsSecurityError(win32);
            return new ArchiveExtractionOutcome(
                Succeeded: false,
                FailureKind: kind,
                ExtractedFileRelativePaths: [],
                Win32ErrorCode: win32,
                Message: kind == TrainerImportFailureKind.AntivirusQuarantined
                    ? $"解压修改器时触发杀毒软件拦截/隔离（Win32 {win32}）。"
                    : $"解压修改器时被 Smart App Control 阻止（Win32 {win32}）。");
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Archive extraction failed for {ArchivePath}.", archivePath);
            }

            return new ArchiveExtractionOutcome(
                Succeeded: false,
                FailureKind: TrainerImportFailureKind.InvalidMagicOrFormat,
                ExtractedFileRelativePaths: [],
                Message: $"压缩包解析或解压失败：{ex.Message}");
        }
    }

    private async Task<ArchiveExtractionOutcome> ExtractZipAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken ct)
    {
        await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        // Pass 1: Read manifest & validate every entry BEFORE writing any file.
        var manifest = new List<(ZipArchiveEntry Entry, string RelPath, string FullPath, long ExpectedSize)>();
        long totalDeclaredBytes = 0;

        if (zip.Entries.Count > _options.MaxArchiveEntryCount)
        {
            return new ArchiveExtractionOutcome(
                false,
                TrainerImportFailureKind.SizeLimitExceeded,
                [],
                Message: $"压缩包条目数 ({zip.Entries.Count}) 超过安全上限 ({_options.MaxArchiveEntryCount})。");
        }

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // Check Unix symlink mode (S_IFLNK = 0xA000 in upper 16 bits of ExternalAttributes) or Windows ReparsePoint (0x0400).
            int unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            bool isUnixSymlink = unixMode == 0xA000;
            bool isWinReparse = (entry.ExternalAttributes & 0x0400) != 0;
            if (isUnixSymlink || isWinReparse)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.ZipSlipRejected,
                    [],
                    Message: $"压缩包包含符号链接或重解析点条目，已拒绝：{entry.FullName}");
            }

            bool isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (!TryValidateArchiveEntryPath(entry.FullName, destinationDirectory, out string relPath, out string fullPath, out string? reject))
            {
                return new ArchiveExtractionOutcome(false, TrainerImportFailureKind.ZipSlipRejected, [], Message: reject);
            }

            if (isDir)
            {
                continue;
            }

            totalDeclaredBytes += Math.Max(0, entry.Length);
            if (totalDeclaredBytes > _options.MaxUncompressedBytes)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.SizeLimitExceeded,
                    [],
                    Message: "压缩包解压总体积超过安全上限。");
            }

            manifest.Add((entry, relPath, fullPath, entry.Length));
        }

        // Pass 2: Stream-extract validated entries with cumulative byte cap and ReparsePoint directory guards.
        long cumulativeWritten = 0;
        var expectedFiles = new List<(string RelPath, string FullPath, long ExpectedSize)>(manifest.Count);

        foreach (var (entry, relPath, fullPath, expectedSize) in manifest)
        {
            ct.ThrowIfCancellationRequested();
            EnsureParentDirectorySafe(destinationDirectory, fullPath);

            await using Stream entryStream = entry.Open();
            long written = await CopyStreamBoundedAsync(
                entryStream,
                fullPath,
                _options.MaxUncompressedBytes - cumulativeWritten,
                ct).ConfigureAwait(false);

            cumulativeWritten += written;
            expectedFiles.Add((relPath, fullPath, expectedSize >= 0 ? expectedSize : written));
        }

        // Pass 3: Post-extraction existence, ReparsePoint, and byte-count verification.
        return VerifyExtractedManifest(destinationDirectory, expectedFiles, usedSevenZipFallback: false);
    }

    private async Task<ArchiveExtractionOutcome> ExtractSharpCompressWithFallbackAsync(
        string archivePath,
        bool isRar,
        string destinationDirectory,
        CancellationToken ct)
    {
        try
        {
            return await ExtractWithSharpCompressCoreAsync(archivePath, isRar, destinationDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSharpCompressUnsupportedOrFormatException(ex))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(ex, "SharpCompress could not extract {ArchivePath}; checking Mode C 7z.exe fallback.", archivePath);
            }

            if (!_sevenZipRunner.IsAvailable)
            {
                return new ArchiveExtractionOutcome(
                    Succeeded: false,
                    FailureKind: TrainerImportFailureKind.UnsupportedArchiveFormat,
                    ExtractedFileRelativePaths: [],
                    UsedSevenZipFallback: false,
                    Message: $"压缩包格式不受内置解压器支持，且未检测到本机 7z.exe：{ex.Message}");
            }

            return await ExtractWithSevenZipFallbackAsync(archivePath, destinationDirectory, ct).ConfigureAwait(false);
        }
    }

    private async Task<ArchiveExtractionOutcome> ExtractWithSharpCompressCoreAsync(
        string archivePath,
        bool isRar,
        string destinationDirectory,
        CancellationToken ct)
    {
        await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var readerOptions = new ReaderOptions { LeaveStreamOpen = true };
        using IArchive archive = isRar
            ? RarArchive.OpenArchive(fs, readerOptions)
            : SevenZipArchive.OpenArchive(fs, readerOptions);

        if (archive.IsEncrypted)
        {
            return new ArchiveExtractionOutcome(
                false,
                TrainerImportFailureKind.UnsupportedArchiveFormat,
                [],
                Message: "不支持自动解压加密压缩包，请先手动解压后再导入。");
        }

        // Pass 1: Inspect full manifest BEFORE extracting any file.
        var manifestByKey = new Dictionary<string, (string RelPath, string FullPath, long ExpectedSize)>(StringComparer.OrdinalIgnoreCase);
        var expectedFiles = new List<(string RelPath, string FullPath, long ExpectedSize)>();
        int entryCount = 0;
        long totalDeclaredBytes = 0;

        foreach (IArchiveEntry entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > _options.MaxArchiveEntryCount)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.SizeLimitExceeded,
                    [],
                    Message: $"压缩包条目数超过安全上限 ({_options.MaxArchiveEntryCount})。");
            }

            if (entry.IsEncrypted)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.UnsupportedArchiveFormat,
                    [],
                    Message: "压缩包内包含加密条目，请手动解压后本地导入。");
            }

            if (!string.IsNullOrWhiteSpace(entry.LinkTarget)
                || (entry.Attrib.HasValue && (entry.Attrib.Value & 0x0400) != 0))
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.ZipSlipRejected,
                    [],
                    Message: $"压缩包包含符号链接或重解析点条目，已拒绝：{entry.Key}");
            }

            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.ZipSlipRejected,
                    [],
                    Message: "压缩包存在空路径条目。");
            }

            if (!TryValidateArchiveEntryPath(entry.Key, destinationDirectory, out string relPath, out string fullPath, out string? reject))
            {
                return new ArchiveExtractionOutcome(false, TrainerImportFailureKind.ZipSlipRejected, [], Message: reject);
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            totalDeclaredBytes += Math.Max(0, entry.Size);
            if (totalDeclaredBytes > _options.MaxUncompressedBytes)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.SizeLimitExceeded,
                    [],
                    Message: "压缩包解压总体积超过安全上限。");
            }

            manifestByKey[entry.Key] = (relPath, fullPath, entry.Size);
            expectedFiles.Add((relPath, fullPath, entry.Size));
        }

        // Pass 2: Extract validated entries.
        // SharpCompress 0.50.9 requires ExtractAllEntries() for solid archives / 7z and direct entry streams for non-solid RAR.
        long cumulativeWritten = 0;
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using IReader reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                IEntry current = reader.Entry;
                if (current.IsDirectory)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(current.LinkTarget)
                    || (current.Attrib.HasValue && (current.Attrib.Value & 0x0400) != 0))
                {
                    return new ArchiveExtractionOutcome(
                        false,
                        TrainerImportFailureKind.ZipSlipRejected,
                        [],
                        Message: $"解压期间检测到符号链接/重解析点条目，已拒绝：{current.Key}");
                }

                string relPath;
                string fullPath;
                if (current.Key is not null && manifestByKey.TryGetValue(current.Key, out var cached))
                {
                    relPath = cached.RelPath;
                    fullPath = cached.FullPath;
                }
                else if (!TryValidateArchiveEntryPath(current.Key, destinationDirectory, out relPath, out fullPath, out string? reject))
                {
                    return new ArchiveExtractionOutcome(false, TrainerImportFailureKind.ZipSlipRejected, [], Message: reject);
                }

                EnsureParentDirectorySafe(destinationDirectory, fullPath);

                await using Stream entryStream = reader.OpenEntryStream();
                long written = await CopyStreamBoundedAsync(
                    entryStream,
                    fullPath,
                    _options.MaxUncompressedBytes - cumulativeWritten,
                    ct).ConfigureAwait(false);
                cumulativeWritten += written;
            }
        }
        else
        {
            foreach (IArchiveEntry entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                var (_, fullPath, _) = manifestByKey[entry.Key];
                EnsureParentDirectorySafe(destinationDirectory, fullPath);

                await using Stream entryStream = entry.OpenEntryStream();
                long written = await CopyStreamBoundedAsync(
                    entryStream,
                    fullPath,
                    _options.MaxUncompressedBytes - cumulativeWritten,
                    ct).ConfigureAwait(false);
                cumulativeWritten += written;
            }
        }

        // Pass 3: Post-extraction existence & byte-length verification against manifest.
        return VerifyExtractedManifest(destinationDirectory, expectedFiles, usedSevenZipFallback: false);
    }

    private async Task<ArchiveExtractionOutcome> ExtractWithSevenZipFallbackAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken ct)
    {
        // Clean any partial files written before fallback.
        CleanDirectoryContents(destinationDirectory);

        ExternalSevenZipListResult listing = await _sevenZipRunner.ListEntriesAsync(archivePath, ct).ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            return new ArchiveExtractionOutcome(
                false,
                TrainerImportFailureKind.UnsupportedArchiveFormat,
                [],
                UsedSevenZipFallback: true,
                Message: listing.ErrorMessage ?? "7z.exe 读取压缩包清单失败。");
        }

        if (listing.Entries.Count > _options.MaxArchiveEntryCount)
        {
            return new ArchiveExtractionOutcome(
                false,
                TrainerImportFailureKind.SizeLimitExceeded,
                [],
                UsedSevenZipFallback: true,
                Message: $"压缩包条目数超过安全上限 ({_options.MaxArchiveEntryCount})。");
        }

        var expectedFiles = new List<(string RelPath, string FullPath, long ExpectedSize)>();
        long totalDeclaredBytes = 0;

        foreach (ArchiveManifestEntry entry in listing.Entries)
        {
            if (entry.IsEncrypted)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.UnsupportedArchiveFormat,
                    [],
                    UsedSevenZipFallback: true,
                    Message: "压缩包已加密，请先手动解压后再导入。");
            }

            if (entry.IsSymlinkOrReparsePoint)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.ZipSlipRejected,
                    [],
                    UsedSevenZipFallback: true,
                    Message: $"检测到符号链接/重解析点条目，已拒绝：{entry.RawPath}");
            }

            if (!TryValidateArchiveEntryPath(entry.RawPath, destinationDirectory, out string relPath, out string fullPath, out string? reject))
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.ZipSlipRejected,
                    [],
                    UsedSevenZipFallback: true,
                    Message: reject);
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            totalDeclaredBytes += Math.Max(0, entry.ExpectedSize);
            if (totalDeclaredBytes > _options.MaxUncompressedBytes)
            {
                return new ArchiveExtractionOutcome(
                    false,
                    TrainerImportFailureKind.SizeLimitExceeded,
                    [],
                    UsedSevenZipFallback: true,
                    Message: "压缩包解压总体积超过安全上限。");
            }

            expectedFiles.Add((relPath, fullPath, entry.ExpectedSize));
        }

        ExternalSevenZipExtractResult extractResult = await _sevenZipRunner.ExtractAsync(archivePath, destinationDirectory, ct).ConfigureAwait(false);
        if (!extractResult.Succeeded)
        {
            return new ArchiveExtractionOutcome(
                false,
                TrainerImportFailureKind.UnsupportedArchiveFormat,
                [],
                UsedSevenZipFallback: true,
                Message: extractResult.ErrorMessage ?? $"7z.exe 解压失败 (退出码 {extractResult.ExitCode})。");
        }

        // Verify no ReparsePoints were created anywhere inside destinationDirectory.
        if (ContainsAnyReparsePoint(destinationDirectory))
        {
            return new ArchiveExtractionOutcome(
                false,
                TrainerImportFailureKind.ZipSlipRejected,
                [],
                UsedSevenZipFallback: true,
                Message: "7z.exe 解压产物包含重解析点/符号链接，已拒绝。");
        }

        return VerifyExtractedManifest(destinationDirectory, expectedFiles, usedSevenZipFallback: true);
    }

    private ArchiveExtractionOutcome VerifyExtractedManifest(
        string destinationDirectory,
        List<(string RelPath, string FullPath, long ExpectedSize)> expectedFiles,
        bool usedSevenZipFallback)
    {
        OnBeforePostExtractVerification?.Invoke(destinationDirectory);

        var verifiedRelPaths = new List<string>(expectedFiles.Count);
        foreach (var (relPath, fullPath, expectedSize) in expectedFiles)
        {
            if (!File.Exists(fullPath))
            {
                return new ArchiveExtractionOutcome(
                    Succeeded: false,
                    FailureKind: TrainerImportFailureKind.AntivirusQuarantined,
                    ExtractedFileRelativePaths: [],
                    UsedSevenZipFallback: usedSevenZipFallback,
                    Message: $"解压后文件 '{relPath}' 缺失，疑似已被杀毒软件静默隔离。");
            }

            FileAttributes attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return new ArchiveExtractionOutcome(
                    Succeeded: false,
                    FailureKind: TrainerImportFailureKind.ZipSlipRejected,
                    ExtractedFileRelativePaths: [],
                    UsedSevenZipFallback: usedSevenZipFallback,
                    Message: $"解压后检测到重解析点文件 '{relPath}'，已拒绝。");
            }

            long actualSize = new FileInfo(fullPath).Length;
            if (expectedSize >= 0 && actualSize != expectedSize)
            {
                return new ArchiveExtractionOutcome(
                    Succeeded: false,
                    FailureKind: TrainerImportFailureKind.AntivirusQuarantined,
                    ExtractedFileRelativePaths: [],
                    UsedSevenZipFallback: usedSevenZipFallback,
                    Message: $"解压后文件 '{relPath}' 字节数不符（期望 {expectedSize}，实际 {actualSize}），疑似被杀毒软件截断或隔离。");
            }

            verifiedRelPaths.Add(relPath);
        }

        return new ArchiveExtractionOutcome(
            Succeeded: true,
            FailureKind: TrainerImportFailureKind.None,
            ExtractedFileRelativePaths: verifiedRelPaths,
            UsedSevenZipFallback: usedSevenZipFallback);
    }

    private static void EnsureParentDirectorySafe(string rootDirectory, string fullFilePath)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        string? targetDir = Path.GetDirectoryName(fullFilePath);
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return;
        }

        string relDir = Path.GetRelativePath(normalizedRoot, targetDir);
        if (relDir == ".")
        {
            return;
        }

        string current = normalizedRoot;
        foreach (string seg in relDir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, seg);
            if (Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"拒绝穿越重解析点目录：{current}");
                }
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }
    }

    private static async Task<long> CopyStreamBoundedAsync(
        Stream source,
        string targetPath,
        long remainingBudgetBytes,
        CancellationToken ct)
    {
        if (remainingBudgetBytes < 0)
        {
            throw new InvalidOperationException("解压体积超过安全上限。");
        }

        await using var outStream = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            useAsync: true);

        byte[] buffer = new byte[65536];
        long total = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > remainingBudgetBytes)
            {
                throw new InvalidOperationException("解压流体积超过安全上限。");
            }

            await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await outStream.FlushAsync(ct).ConfigureAwait(false);
        outStream.Flush(flushToDisk: true);
        return total;
    }

    private static bool IsSharpCompressUnsupportedOrFormatException(Exception ex)
    {
        if (PeBinaryInspector.ClassifyOsSecurityError(PeBinaryInspector.TryExtractWin32ErrorCode(ex)) != TrainerImportFailureKind.None)
        {
            return false;
        }

        return ex is NotSupportedException
            or InvalidFormatException
            or ArchiveException
            or IncompleteArchiveException
            or InvalidDataException;
    }

    private static bool ContainsAnyReparsePoint(string rootDirectory)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.None,
        };

        foreach (string path in Directory.EnumerateFileSystemEntries(rootDirectory, "*", opts))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void CleanDirectoryContents(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                else if ((attrs & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch
            {
                // Best-effort staging reset.
            }
        }
    }
}
