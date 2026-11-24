using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spawn.Modules.Progress.Audio;

/// <summary>
/// Resolves target FLiNG configuration directories for A0 pre-mute preparation and backup restoration.
/// Injectable so unit tests never touch real user <c>Documents</c> or <c>%LOCALAPPDATA%</c> folders.
/// </summary>
public interface ITrainerAudioA0DirectoryResolver
{
    /// <summary>
    /// Returns deduplicated target directories for A0 pre-mute (<c>&lt;Documents&gt;\FLiNGTrainer</c>,
    /// legacy <c>%USERPROFILE%\Documents\FLiNGTrainer</c> if Documents was redirected and that legacy folder already exists,
    /// and <c>%LOCALAPPDATA%\FLiNGTrainer</c>). Never includes the trainer EXE directory or <c>%APPDATA%</c>.
    /// </summary>
    IReadOnlyList<string> ResolveTargetDirectories();
}

/// <summary>
/// Production Windows directory resolver using <c>SHGetKnownFolderPath(FOLDERID_Documents)</c>
/// (<c>{FDD39AD0-238F-46AF-ADB4-6C85480369C7}</c>) instead of hardcoding <c>%USERPROFILE%\Documents</c>.
/// </summary>
public sealed partial class WindowsTrainerAudioA0DirectoryResolver : ITrainerAudioA0DirectoryResolver
{
    public const string FlingTrainerFolderName = "FLiNGTrainer";

    /// <summary>Windows <c>FOLDERID_Documents</c> GUID (<c>{FDD39AD0-238F-46AF-ADB4-6C85480369C7}</c>).</summary>
    public static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    private readonly Func<string?> _documentsFolderProvider;
    private readonly Func<string?> _userProfileFolderProvider;
    private readonly Func<string?> _localAppDataFolderProvider;

    public WindowsTrainerAudioA0DirectoryResolver()
        : this(
            TryResolveKnownDocumentsFolder,
            static () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            static () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal WindowsTrainerAudioA0DirectoryResolver(
        Func<string?> documentsFolderProvider,
        Func<string?> userProfileFolderProvider,
        Func<string?> localAppDataFolderProvider)
    {
        _documentsFolderProvider = documentsFolderProvider ?? throw new ArgumentNullException(nameof(documentsFolderProvider));
        _userProfileFolderProvider = userProfileFolderProvider ?? throw new ArgumentNullException(nameof(userProfileFolderProvider));
        _localAppDataFolderProvider = localAppDataFolderProvider ?? throw new ArgumentNullException(nameof(localAppDataFolderProvider));
    }

    /// <summary>
    /// Resolves the true Windows Documents folder via <c>shell32.dll!SHGetKnownFolderPath(FOLDERID_Documents)</c>.
    /// Read-only; never creates or modifies any directory.
    /// </summary>
    public static string? TryResolveKnownDocumentsFolder()
    {
        int hr = SHGetKnownFolderPath(FolderIdDocuments, 0, nint.Zero, out nint pszPath);
        if (hr != 0 || pszPath == nint.Zero)
        {
            if (pszPath != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pszPath);
            }

            return null;
        }

        try
        {
            string? path = Marshal.PtrToStringUni(pszPath);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pszPath);
        }
    }

    public IReadOnlyList<string> ResolveTargetDirectories()
    {
        var results = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                return;
            }

            string combined = NormalizeDirectory(Path.Combine(rootDir, FlingTrainerFolderName));
            if (seen.Add(combined))
            {
                results.Add(combined);
            }
        }

        string? documentsDir = _documentsFolderProvider();
        string? normalizedDocuments = string.IsNullOrWhiteSpace(documentsDir) ? null : NormalizeDirectory(documentsDir);
        if (normalizedDocuments is not null)
        {
            TryAdd(normalizedDocuments);
        }

        // If Documents is redirected (e.g. OneDrive or custom volume) AND %USERPROFILE%\Documents\FLiNGTrainer already exists,
        // include that legacy directory as well so older trainers probing UserProfile\Documents are also muted.
        string? userProfile = _userProfileFolderProvider();
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string legacyDocuments = NormalizeDirectory(Path.Combine(userProfile, "Documents"));
            bool isRedirected = normalizedDocuments is null
                || !string.Equals(normalizedDocuments, legacyDocuments, StringComparison.OrdinalIgnoreCase);

            if (isRedirected)
            {
                string legacyFlingDir = NormalizeDirectory(Path.Combine(legacyDocuments, FlingTrainerFolderName));
                if (Directory.Exists(legacyFlingDir) && seen.Add(legacyFlingDir))
                {
                    results.Add(legacyFlingDir);
                }
            }
        }

        string? localAppData = _localAppDataFolderProvider();
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            TryAdd(localAppData);
        }

        return results;
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [LibraryImport("shell32.dll", SetLastError = false)]
    private static partial int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        nint hToken,
        out nint ppszPath);
}

/// <summary>
/// Summary report of an A0 pre-mute preparation pass.
/// </summary>
public sealed record TrainerAudioA0PreparationReport(
    IReadOnlyList<string> ProcessedDirectories,
    int UpdatedFileCount,
    int CreatedFileCount,
    int BackedUpFileCount);

/// <summary>
/// Summary report of an A0 backup restoration pass.
/// </summary>
public sealed record TrainerAudioA0RestoreReport(
    IReadOnlyList<string> ProcessedDirectories,
    int RestoredFileCount,
    int CleanedCreatedFileCount);

/// <summary>
/// A0 zero-injection pre-launch mute preparer (INI <c>OnLoadMusic</c> + 27-byte silent <c>TrainerBGM.mid</c>).
/// </summary>
public interface ITrainerAudioA0Preparer
{
    Task<TrainerAudioA0PreparationReport> PrepareAsync(CancellationToken ct = default);
    Task<TrainerAudioA0RestoreReport> RestoreBackupsAsync(CancellationToken ct = default);
}

/// <summary>
/// Implements A0 pre-launch silent preparation, non-overwriting <c>.spawn_bak</c> backup, and clean restoration:
/// <list type="bullet">
/// <item><description><c>FLiNGTSettings.ini</c>: writes <c>OnLoadMusic = False</c> (spaces around <c>=</c>).</description></item>
/// <item><description><c>TrainerSettings.ini</c>: writes <c>OnLoadMusic=False</c> (no spaces around <c>=</c>).</description></item>
/// <item><description><c>TrainerBGM.mid</c>: writes exact 27-byte 5.0-second pure-silence SMF (<see cref="SilentMidiAssets.SilentSmfBytes"/>).</description></item>
/// </list>
/// Preserves other INI sections/keys/comments and original file encoding. Idempotent across repeated calls.
/// </summary>
public sealed class TrainerAudioA0Preparer : ITrainerAudioA0Preparer
{
    public const string FlingTSettingsFileName = "FLiNGTSettings.ini";
    public const string TrainerSettingsFileName = "TrainerSettings.ini";
    public const string TrainerBgmMidiFileName = "TrainerBGM.mid";
    public const string BackupExtension = ".spawn_bak";

    private const string SettingsSectionName = "Settings";
    private const string OnLoadMusicKeyName = "OnLoadMusic";
    private const string FlingTSettingsTargetLine = "OnLoadMusic = False";
    private const string TrainerSettingsTargetLine = "OnLoadMusic=False";
    private const string CreatedStateManifestFileName = ".spawn_a0_state.json";

    private static readonly string[] ManagedFileNames =
    [
        FlingTSettingsFileName,
        TrainerSettingsFileName,
        TrainerBgmMidiFileName,
    ];

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ITrainerAudioA0DirectoryResolver _directoryResolver;
    private readonly ILogger<TrainerAudioA0Preparer> _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public TrainerAudioA0Preparer(
        ITrainerAudioA0DirectoryResolver? directoryResolver = null,
        ILogger<TrainerAudioA0Preparer>? logger = null)
    {
        _directoryResolver = directoryResolver ?? new WindowsTrainerAudioA0DirectoryResolver();
        _logger = logger ?? NullLogger<TrainerAudioA0Preparer>.Instance;
    }

    public async Task<TrainerAudioA0PreparationReport> PrepareAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> directories = _directoryResolver.ResolveTargetDirectories();
            var processed = new List<string>(directories.Count);
            int updatedCount = 0;
            int createdCount = 0;
            int backedUpCount = 0;

            foreach (string dir in directories)
            {
                ct.ThrowIfCancellationRequested();
                bool directoryCreatedBySpawn = false;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    directoryCreatedBySpawn = true;
                }

                A0DirectoryState state = ReadOrCreateState(dir, directoryCreatedBySpawn);
                bool stateChanged = directoryCreatedBySpawn;

                ApplyIniFile(dir, FlingTSettingsFileName, FlingTSettingsTargetLine, state, ref updatedCount, ref createdCount, ref backedUpCount, ref stateChanged);
                ApplyIniFile(dir, TrainerSettingsFileName, TrainerSettingsTargetLine, state, ref updatedCount, ref createdCount, ref backedUpCount, ref stateChanged);
                ApplySilentMidiFile(dir, state, ref updatedCount, ref createdCount, ref backedUpCount, ref stateChanged);

                if (stateChanged || state.CreatedFiles.Count > 0 || state.DirectoryCreatedBySpawn)
                {
                    WriteState(dir, state);
                }

                processed.Add(dir);
            }

            return new TrainerAudioA0PreparationReport(processed, updatedCount, createdCount, backedUpCount);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<TrainerAudioA0RestoreReport> RestoreBackupsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> directories = _directoryResolver.ResolveTargetDirectories();
            var processed = new List<string>(directories.Count);
            int restoredCount = 0;
            int cleanedCount = 0;

            foreach (string dir in directories)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                A0DirectoryState? state = TryReadState(dir);
                var createdSet = state is not null
                    ? new HashSet<string>(state.CreatedFiles, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string fileName in ManagedFileNames)
                {
                    string targetPath = Path.Combine(dir, fileName);
                    string backupPath = targetPath + BackupExtension;

                    if (File.Exists(backupPath))
                    {
                        File.Move(backupPath, targetPath, overwrite: true);
                        restoredCount++;
                    }
                    else if (createdSet.Contains(fileName) && File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                        cleanedCount++;
                    }
                }

                string manifestPath = Path.Combine(dir, CreatedStateManifestFileName);
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }

                if (state is { DirectoryCreatedBySpawn: true })
                {
                    TryDeleteDirectoryIfEmpty(dir);
                }

                processed.Add(dir);
            }

            return new TrainerAudioA0RestoreReport(processed, restoredCount, cleanedCount);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private static void ApplyIniFile(
        string directory,
        string fileName,
        string targetKeyLine,
        A0DirectoryState state,
        ref int updatedCount,
        ref int createdCount,
        ref int backedUpCount,
        ref bool stateChanged)
    {
        string targetPath = Path.Combine(directory, fileName);
        string backupPath = targetPath + BackupExtension;
        bool exists = File.Exists(targetPath);
        bool trackedAsCreated = state.CreatedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase);

        if (!exists)
        {
            string freshContent = $"[{SettingsSectionName}]\r\n{targetKeyLine}\r\n";
            byte[] freshBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(freshContent);
            WriteFileAtomically(targetPath, freshBytes);
            if (!trackedAsCreated)
            {
                state.CreatedFiles.Add(fileName);
                stateChanged = true;
            }

            createdCount++;
            return;
        }

        byte[] existingBytes = File.ReadAllBytes(targetPath);
        (Encoding encoding, int preambleLength) = DetectFileEncoding(existingBytes);
        string existingText = encoding.GetString(existingBytes, preambleLength, existingBytes.Length - preambleLength);
        string updatedText = UpdateIniOnLoadMusic(existingText, targetKeyLine);
        byte[] updatedBytes = EncodeWithPreamble(encoding, preambleLength > 0, updatedText);

        // Back up pre-existing user file only once (never overwrite an existing .spawn_bak and never back up a SPAWN-created file).
        if (!trackedAsCreated && !File.Exists(backupPath))
        {
            WriteFileAtomically(backupPath, existingBytes);
            backedUpCount++;
        }

        if (!existingBytes.AsSpan().SequenceEqual(updatedBytes))
        {
            WriteFileAtomically(targetPath, updatedBytes);
            updatedCount++;
        }
    }

    private static void ApplySilentMidiFile(
        string directory,
        A0DirectoryState state,
        ref int updatedCount,
        ref int createdCount,
        ref int backedUpCount,
        ref bool stateChanged)
    {
        string targetPath = Path.Combine(directory, TrainerBgmMidiFileName);
        string backupPath = targetPath + BackupExtension;
        bool exists = File.Exists(targetPath);
        bool trackedAsCreated = state.CreatedFiles.Contains(TrainerBgmMidiFileName, StringComparer.OrdinalIgnoreCase);

        if (!exists)
        {
            WriteFileAtomically(targetPath, SilentMidiAssets.GetSilentSmfCopy());
            if (!trackedAsCreated)
            {
                state.CreatedFiles.Add(TrainerBgmMidiFileName);
                stateChanged = true;
            }

            createdCount++;
            return;
        }

        byte[] existingBytes = File.ReadAllBytes(targetPath);
        if (!trackedAsCreated && !File.Exists(backupPath))
        {
            WriteFileAtomically(backupPath, existingBytes);
            backedUpCount++;
        }

        if (!SilentMidiAssets.IsExactSilentSmf(existingBytes))
        {
            WriteFileAtomically(targetPath, SilentMidiAssets.GetSilentSmfCopy());
            updatedCount++;
        }
    }

    /// <summary>
    /// Updates or inserts <c>OnLoadMusic</c> inside <c>[Settings]</c> while preserving all other sections, keys,
    /// comments, blank lines, and line-ending style.
    /// </summary>
    internal static string UpdateIniOnLoadMusic(string content, string targetKeyLine)
    {
        string newline = DetectNewline(content);
        bool hadTrailingNewline = content.EndsWith('\n');

        if (string.IsNullOrEmpty(content))
        {
            return $"[{SettingsSectionName}]{newline}{targetKeyLine}{newline}";
        }

        List<string> lines = SplitLines(content);

        int settingsHeaderIndex = -1;
        int settingsEndExclusive = lines.Count;
        int topLevelKeyIndex = -1;
        bool sawAnySection = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (TryParseSectionName(trimmed, out string sectionName))
            {
                sawAnySection = true;
                if (settingsHeaderIndex >= 0 && settingsEndExclusive == lines.Count)
                {
                    settingsEndExclusive = i;
                    break;
                }

                if (string.Equals(sectionName, SettingsSectionName, StringComparison.OrdinalIgnoreCase))
                {
                    settingsHeaderIndex = i;
                }
            }
            else if (!sawAnySection && topLevelKeyIndex < 0 && IsTargetKeyLine(trimmed))
            {
                topLevelKeyIndex = i;
            }
        }

        if (settingsHeaderIndex >= 0)
        {
            // Look for existing OnLoadMusic inside [Settings].
            int existingKeyIndex = -1;
            for (int i = settingsHeaderIndex + 1; i < settingsEndExclusive; i++)
            {
                if (IsTargetKeyLine(lines[i].Trim()))
                {
                    existingKeyIndex = i;
                    break;
                }
            }

            if (existingKeyIndex >= 0)
            {
                lines[existingKeyIndex] = targetKeyLine;
            }
            else
            {
                // Insert right after the last non-blank line in [Settings] (before any trailing blank lines preceding the next section).
                int insertAt = settingsEndExclusive;
                while (insertAt > settingsHeaderIndex + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                {
                    insertAt--;
                }

                lines.Insert(insertAt, targetKeyLine);
            }
        }
        else if (!sawAnySection && topLevelKeyIndex >= 0)
        {
            // Flat key=value INI without any section header.
            lines[topLevelKeyIndex] = targetKeyLine;
        }
        else
        {
            // Existing INI has other sections (or content) but no [Settings] section: append [Settings].
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{SettingsSectionName}]");
            lines.Add(targetKeyLine);
        }

        string joined = string.Join(newline, lines);
        return hadTrailingNewline ? joined + newline : joined;
    }

    private static bool TryParseSectionName(string trimmedLine, out string sectionName)
    {
        sectionName = string.Empty;
        if (trimmedLine.Length < 3 || trimmedLine[0] != '[')
        {
            return false;
        }

        int closeBracket = trimmedLine.IndexOf(']');
        if (closeBracket <= 1)
        {
            return false;
        }

        // Trailing text after ']' must be empty or a comment (';' or '#').
        ReadOnlySpan<char> tail = trimmedLine.AsSpan(closeBracket + 1).Trim();
        if (!tail.IsEmpty && tail[0] != ';' && tail[0] != '#')
        {
            return false;
        }

        sectionName = trimmedLine.Substring(1, closeBracket - 1).Trim();
        return sectionName.Length > 0;
    }

    private static bool IsTargetKeyLine(string trimmedLine)
    {
        if (trimmedLine.Length == 0 || trimmedLine[0] == ';' || trimmedLine[0] == '#' || trimmedLine[0] == '[')
        {
            return false;
        }

        int eqIndex = trimmedLine.IndexOf('=');
        if (eqIndex <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> key = trimmedLine.AsSpan(0, eqIndex).Trim();
        return key.Equals(OnLoadMusicKeyName, StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectNewline(string content)
    {
        if (content.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        if (content.Contains('\n'))
        {
            return "\n";
        }

        return "\r\n";
    }

    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r')
            {
                lines.Add(content.Substring(start, i - start));
                if (i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
            else if (content[i] == '\n')
            {
                lines.Add(content.Substring(start, i - start));
                start = i + 1;
            }
        }

        if (start < content.Length)
        {
            lines.Add(content.Substring(start));
        }

        return lines;
    }

    private static (Encoding Encoding, int PreambleLength) DetectFileEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        // No BOM: validate strict UTF-8; if non-UTF-8 legacy ANSI/DBCS bytes are present, Latin-1 preserves every byte 1:1 losslessly.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            _ = strictUtf8.GetString(bytes);
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1, 0);
        }
    }

    private static byte[] EncodeWithPreamble(Encoding encoding, bool emitPreamble, string text)
    {
        byte[] payload = encoding.GetBytes(text);
        if (!emitPreamble)
        {
            return payload;
        }

        byte[] preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            return payload;
        }

        byte[] combined = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, combined, preamble.Length, payload.Length);
        return combined;
    }

    private static void WriteFileAtomically(string targetPath, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private A0DirectoryState ReadOrCreateState(string dir, bool directoryCreatedBySpawn)
    {
        A0DirectoryState? existing = TryReadState(dir);
        if (existing is not null)
        {
            return existing;
        }

        return new A0DirectoryState
        {
            DirectoryCreatedBySpawn = directoryCreatedBySpawn,
            CreatedFiles = [],
        };
    }

    private A0DirectoryState? TryReadState(string dir)
    {
        string manifestPath = Path.Combine(dir, CreatedStateManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(manifestPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<A0DirectoryState>(json);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not deserialize A0 state manifest at {ManifestPath}; treating as absent.", manifestPath);
            }

            return null;
        }
    }

    private static void WriteState(string dir, A0DirectoryState state)
    {
        string manifestPath = Path.Combine(dir, CreatedStateManifestFileName);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, ManifestJsonOptions));
        WriteFileAtomically(manifestPath, bytes);
    }

    private static void TryDeleteFile(string path)
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
            // Best-effort cleanup of temp file.
        }
    }

    private void TryDeleteDirectoryIfEmpty(string dir)
    {
        try
        {
            // Never follow reparse points; remove only if completely empty.
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Skipped removing A0 directory {Directory} after restore.", dir);
            }
        }
    }

    private sealed class A0DirectoryState
    {
        public bool DirectoryCreatedBySpawn { get; set; }
        public List<string> CreatedFiles { get; set; } = [];
    }
}
