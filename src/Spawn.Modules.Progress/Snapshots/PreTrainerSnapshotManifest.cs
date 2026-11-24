using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Snapshots;

/// <summary>
/// Atomic JSON manifest persisted as <c>manifest.json</c> inside
/// <c>SpawnPaths.PreTrainerSnapshotsDirectory\&lt;GameId&gt;\&lt;SnapshotId&gt;\</c>.
/// Preserves raw copies with logical relative paths, sizes, UTC timestamps, and SHA-256 digests so
/// M4 Save Time-Machine can import M3 snapshots losslessly.
/// </summary>
public sealed record PreTrainerSnapshotManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string SnapshotId { get; init; }
    public required string GameId { get; init; }
    public string? TrainerId { get; init; }
    public PreTrainerSnapshotTriggerKind TriggerKind { get; init; } = PreTrainerSnapshotTriggerKind.PreTrainer;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public int TotalFileCount { get; init; }
    public long TotalBytes { get; init; }
    public string? RegistrySnapshotRelativePath { get; init; }
    public string? RegistrySnapshotSha256 { get; init; }
    public int RegistryKeyCount { get; init; }
    public IReadOnlyList<string> RegistryKeys { get; init; } = [];
    public IReadOnlyList<PreTrainerSnapshotFileEntry> Files { get; init; } = [];
}

/// <summary>
/// Single file entry recorded in <see cref="PreTrainerSnapshotManifest"/>.
/// </summary>
public sealed record PreTrainerSnapshotFileEntry
{
    /// <summary>Forward-slash normalized logical relative path within the snapshot payload (e.g., <c>loc_000/slot0.sav</c>).</summary>
    public required string LogicalRelativePath { get; init; }

    /// <summary>Forward-slash normalized path relative to the snapshot directory (e.g., <c>files/loc_000/slot0.sav</c>).</summary>
    public required string SnapshotRelativePath { get; init; }

    /// <summary>Original absolute filesystem path of the save file on disk.</summary>
    public required string OriginalPath { get; init; }

    /// <summary>Discovered parent save root path reported by <see cref="IGameDataManagementService"/>.</summary>
    public required string SourceLocationPath { get; init; }

    public long SizeBytes { get; init; }
    public DateTimeOffset LastWriteTimeUtc { get; init; }

    /// <summary>Lowercase 64-character hex SHA-256 digest of the file payload.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>
/// Atomic JSON/byte file persistence and path containment helpers for pre-trainer snapshots.
/// </summary>
public static class PreTrainerSnapshotManifestStore
{
    public const string ManifestFileName = "manifest.json";
    public const string RegistrySnapshotFileName = "registry_snapshot.json";
    public const string FilesDirectoryName = "files";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task WriteManifestAtomicAsync(
        string manifestPath,
        PreTrainerSnapshotManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(manifest);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        return WriteBytesAtomicAsync(manifestPath, bytes, cancellationToken);
    }

    public static async Task WriteBytesAtomicAsync(
        string destinationPath,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string parentDir = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"Destination path has no parent directory: {destinationPath}");
        Directory.CreateDirectory(parentDir);

        string tempPath = Path.Combine(
            parentDir,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

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
                await fs.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    public static async Task<PreTrainerSnapshotManifest?> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        await using var fs = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PreTrainerSnapshotManifest>(fs, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<string> ComputeFileSha256HexAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Resolves a manifest-relative path strictly inside <paramref name="rootDirectory"/>, rejecting rooted paths,
    /// drive qualifiers, NUL characters, and <c>.</c>/<c>..</c> traversal segments.
    /// </summary>
    public static bool TryResolveContainedPath(
        string rootDirectory,
        string? relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string trimmed = relativePath.Trim();
        if (trimmed.Contains('\0', StringComparison.Ordinal)
            || trimmed.Contains(':', StringComparison.Ordinal)
            || Path.IsPathRooted(trimmed)
            || trimmed.StartsWith('\\')
            || trimmed.StartsWith('/'))
        {
            return false;
        }

        string[] segments = trimmed.Split(['\\', '/'], StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(static s => string.IsNullOrWhiteSpace(s) || s is "." or ".."))
        {
            return false;
        }

        try
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            string combined = Path.GetFullPath(Path.Combine(normalizedRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
            if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = combined;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDeleteTempFile(string path)
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
            // Ignore cleanup failure for transient temp file.
        }
    }
}
