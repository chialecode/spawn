using System.Text.RegularExpressions;
using Blake3;
using Spawn.Contracts;
using ZstdSharp;

namespace Spawn.Core.Storage;

/// <summary>
/// Outcome of storing a single content-addressed chunk in <see cref="CasChunkStore"/>.
/// </summary>
public readonly record struct CasChunkStoreResult(
    string ChunkBlake3,
    long RawSizeBytes,
    long CompressedSizeBytes,
    bool WasDeduplicated);

/// <summary>
/// Outcome of chunking and ingesting a complete file or byte payload into <see cref="CasChunkStore"/>.
/// </summary>
public sealed record CasFileIngestResult(
    string FileBlake3,
    long RawSizeBytes,
    long StoredCompressedBytes,
    IReadOnlyList<string> ChunkHashes,
    int NewChunkCount,
    int ReusedChunkCount);

/// <summary>
/// Content-addressed chunk vault storing <see cref="FastCdcChunker"/> slices hashed with <c>BLAKE3</c>
/// and compressed with pure-managed <c>Zstd</c> (default level 3) under <c>vault\Chunks\aa\bb\&lt;blake3&gt;.zst</c>.
/// </summary>
public sealed class CasChunkStore
{
    public const int DefaultZstdCompressionLevel = 3;

    private static readonly Regex ValidBlake3HexRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly int _compressionLevel;

    public CasChunkStore(
        SpawnPaths paths,
        SpawnDatabase database,
        TimeProvider? timeProvider = null,
        int compressionLevel = DefaultZstdCompressionLevel)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(database);
        _paths = paths;
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _compressionLevel = Math.Clamp(compressionLevel, 1, 22);
    }

    public static string ComputeBlake3Hex(ReadOnlySpan<byte> data) =>
        Hasher.Hash(data).ToString();

    public static bool IsValidBlake3Hex(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) && ValidBlake3HexRegex.IsMatch(hash);

    public string GetChunkFilePath(string chunkBlake3)
    {
        string normalized = ValidateAndNormalizeHash(chunkBlake3);
        return _paths.GetCasChunkPath(normalized);
    }

    public bool ChunkFileExists(string chunkBlake3) =>
        IsValidBlake3Hex(chunkBlake3.Trim().ToLowerInvariant())
        && File.Exists(_paths.GetCasChunkPath(chunkBlake3.Trim().ToLowerInvariant()));

    /// <summary>
    /// Hashes <paramref name="rawData"/> with BLAKE3, compresses with Zstd if absent on disk, atomically moves it
    /// into the two-level shard path, and ensures a row exists in <c>cas_chunks</c> (with initial <c>ref_count = 0</c>
    /// until committed by a snapshot transaction).
    /// </summary>
    public async Task<CasChunkStoreResult> StoreChunkAsync(
        ReadOnlyMemory<byte> rawData,
        CancellationToken cancellationToken = default)
    {
        string hashHex = ComputeBlake3Hex(rawData.Span);
        string chunkPath = _paths.GetCasChunkPath(hashHex);

        if (File.Exists(chunkPath))
        {
            long existingCompressedSize = new FileInfo(chunkPath).Length;
            await _database.RegisterCasChunkIfAbsentAsync(
                hashHex,
                rawData.Length,
                existingCompressedSize,
                _timeProvider.GetUtcNow(),
                packId: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new CasChunkStoreResult(
                ChunkBlake3: hashHex,
                RawSizeBytes: rawData.Length,
                CompressedSizeBytes: existingCompressedSize,
                WasDeduplicated: true);
        }

        byte[] compressed;
        using (var compressor = new Compressor(_compressionLevel))
        {
            compressed = compressor.Wrap(rawData.Span).ToArray();
        }

        await WriteChunkAtomicallyAsync(chunkPath, compressed, cancellationToken).ConfigureAwait(false);

        await _database.RegisterCasChunkIfAbsentAsync(
            hashHex,
            rawData.Length,
            compressed.Length,
            _timeProvider.GetUtcNow(),
            packId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CasChunkStoreResult(
            ChunkBlake3: hashHex,
            RawSizeBytes: rawData.Length,
            CompressedSizeBytes: compressed.Length,
            WasDeduplicated: false);
    }

    /// <summary>
    /// Verifies a downloaded Zstd-compressed chunk against <paramref name="expectedChunkBlake3"/> by decompressing
    /// and re-hashing before atomically persisting it to the local CAS vault.
    /// </summary>
    public async Task<CasChunkStoreResult> StoreVerifiedCompressedChunkAsync(
        string expectedChunkBlake3,
        ReadOnlyMemory<byte> compressedZstdBytes,
        string? packId = null,
        CancellationToken cancellationToken = default)
    {
        string expected = ValidateAndNormalizeHash(expectedChunkBlake3);

        byte[] raw;
        using (var decompressor = new Decompressor())
        {
            raw = decompressor.Unwrap(compressedZstdBytes.Span).ToArray();
        }

        string actual = ComputeBlake3Hex(raw);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CAS chunk BLAKE3 verification failed: expected '{expected}', got '{actual}'.");
        }

        string chunkPath = _paths.GetCasChunkPath(expected);
        bool existed = File.Exists(chunkPath);
        if (!existed)
        {
            await WriteChunkAtomicallyAsync(chunkPath, compressedZstdBytes, cancellationToken).ConfigureAwait(false);
        }

        long compressedLen = existed ? new FileInfo(chunkPath).Length : compressedZstdBytes.Length;
        await _database.RegisterCasChunkIfAbsentAsync(
            expected,
            raw.Length,
            compressedLen,
            _timeProvider.GetUtcNow(),
            packId: packId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CasChunkStoreResult(
            ChunkBlake3: expected,
            RawSizeBytes: raw.Length,
            CompressedSizeBytes: compressedLen,
            WasDeduplicated: existed);
    }

    /// <summary>
    /// Streams <paramref name="stream"/> through <see cref="FastCdcChunker"/>, storing every unique chunk and
    /// computing the whole-file BLAKE3 digest in a single streaming pass.
    /// </summary>
    public async Task<CasFileIngestResult> StoreStreamAsync(
        Stream stream,
        long? knownLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var chunkHashes = new List<string>();
        long totalRawBytes = 0;
        long totalStoredBytes = 0;
        int newChunks = 0;
        int reusedChunks = 0;

        using var fileHasher = Hasher.New();

        await FastCdcChunker.ChunkStreamAsync(
            stream,
            knownLength,
            async (slice, _, ct) =>
            {
                fileHasher.Update(slice.Span);
                CasChunkStoreResult res = await StoreChunkAsync(slice, ct).ConfigureAwait(false);
                chunkHashes.Add(res.ChunkBlake3);
                totalRawBytes += res.RawSizeBytes;
                if (res.WasDeduplicated)
                {
                    reusedChunks++;
                }
                else
                {
                    newChunks++;
                    totalStoredBytes += res.CompressedSizeBytes;
                }
            },
            cancellationToken).ConfigureAwait(false);

        string fileBlake3 = fileHasher.Finalize().ToString();
        return new CasFileIngestResult(
            FileBlake3: fileBlake3,
            RawSizeBytes: totalRawBytes,
            StoredCompressedBytes: totalStoredBytes,
            ChunkHashes: chunkHashes,
            NewChunkCount: newChunks,
            ReusedChunkCount: reusedChunks);
    }

    public async Task<CasFileIngestResult> StoreBytesAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        return await StoreStreamAsync(ms, payload.Length, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CasFileIngestResult> StoreFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await StoreStreamAsync(fs, fs.Length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and decompresses a single CAS chunk, verifying its BLAKE3 digest against <paramref name="chunkBlake3"/>.
    /// </summary>
    public async Task<byte[]> ReadChunkRawAsync(
        string chunkBlake3,
        bool verifyHash = true,
        CancellationToken cancellationToken = default)
    {
        string normalized = ValidateAndNormalizeHash(chunkBlake3);
        string chunkPath = _paths.GetCasChunkPath(normalized);
        if (!File.Exists(chunkPath))
        {
            throw new FileNotFoundException($"CAS chunk '{normalized}' is missing from local vault.", chunkPath);
        }

        byte[] compressed = await File.ReadAllBytesAsync(chunkPath, cancellationToken).ConfigureAwait(false);
        byte[] raw;
        using (var decompressor = new Decompressor())
        {
            raw = decompressor.Unwrap(compressed).ToArray();
        }

        if (verifyHash)
        {
            string actual = ComputeBlake3Hex(raw);
            if (!string.Equals(actual, normalized, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"CAS chunk corruption detected for '{normalized}': actual BLAKE3 '{actual}'.");
            }
        }

        return raw;
    }

    /// <summary>
    /// Reads raw Zstd-compressed chunk bytes directly from disk (used by SPPK cloud packfile assembly).
    /// </summary>
    public async Task<byte[]> ReadChunkCompressedAsync(
        string chunkBlake3,
        CancellationToken cancellationToken = default)
    {
        string normalized = ValidateAndNormalizeHash(chunkBlake3);
        string chunkPath = _paths.GetCasChunkPath(normalized);
        if (!File.Exists(chunkPath))
        {
            throw new FileNotFoundException($"CAS chunk '{normalized}' is missing from local vault.", chunkPath);
        }

        return await File.ReadAllBytesAsync(chunkPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reassembles a sequence of verified CAS chunks into <paramref name="destination"/>, optionally verifying
    /// the resulting whole-file BLAKE3 digest.
    /// </summary>
    public async Task<long> ReconstructToStreamAsync(
        IReadOnlyList<string> chunkHashes,
        Stream destination,
        string? expectedFileBlake3 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkHashes);
        ArgumentNullException.ThrowIfNull(destination);

        long totalWritten = 0;
        using var fileHasher = Hasher.New();

        foreach (string hash in chunkHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] raw = await ReadChunkRawAsync(hash, verifyHash: true, cancellationToken).ConfigureAwait(false);
            fileHasher.Update(raw);
            await destination.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
            totalWritten += raw.Length;
        }

        if (!string.IsNullOrWhiteSpace(expectedFileBlake3))
        {
            string actualFileHash = fileHasher.Finalize().ToString();
            string expected = ValidateAndNormalizeHash(expectedFileBlake3);
            if (!string.Equals(actualFileHash, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Reconstructed file BLAKE3 mismatch: expected '{expected}', got '{actualFileHash}'.");
            }
        }

        return totalWritten;
    }

    /// <summary>
    /// Reclaims unreferenced CAS chunks whose <c>ref_count == 0</c> and grace period has elapsed.
    /// </summary>
    public async Task<SaveGcReport> RunGarbageCollectionAsync(
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - (gracePeriod < TimeSpan.Zero ? TimeSpan.Zero : gracePeriod);
        IReadOnlyList<CasChunkRecord> candidates = await _database
            .GetUnreferencedCasChunksBeforeAsync(cutoff, cancellationToken)
            .ConfigureAwait(false);

        int deletedCount = 0;
        long reclaimedBytes = 0;
        var deletedHashes = new List<string>(candidates.Count);

        foreach (CasChunkRecord chunk in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-verify ref_count == 0 via DB delete BEFORE removing the physical file, or delete file after DB confirms.
            // Deleting DB row first (WHERE ref_count <= 0) prevents racing with a concurrent snapshot reviving the chunk.
            int removedRows = await _database
                .DeleteUnreferencedCasChunksAsync([chunk.ChunkBlake3], cancellationToken)
                .ConfigureAwait(false);
            if (removedRows == 0)
            {
                continue;
            }

            deletedHashes.Add(chunk.ChunkBlake3);
            deletedCount++;

            string path = _paths.GetCasChunkPath(chunk.ChunkBlake3);
            try
            {
                if (File.Exists(path))
                {
                    long onDiskBytes = new FileInfo(path).Length;
                    File.Delete(path);
                    reclaimedBytes += onDiskBytes;
                    TryPruneEmptyShardParent(path);
                }
                else
                {
                    reclaimedBytes += Math.Max(0, chunk.CompressedSizeBytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: file can be cleaned on a future GC pass.
            }
        }

        CasVaultStatsDto remaining = await _database.GetCasVaultStatsAsync(cancellationToken).ConfigureAwait(false);
        return new SaveGcReport(
            DeletedChunkCount: deletedCount,
            ReclaimedBytes: reclaimedBytes,
            RemainingChunkCount: remaining.TotalChunkCount,
            RemainingStoredBytes: remaining.TotalCompressedBytes);
    }

    private void TryPruneEmptyShardParent(string chunkFilePath)
    {
        try
        {
            string chunksRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.VaultChunksDirectory));
            string? dir = Path.GetDirectoryName(chunkFilePath);
            for (int depth = 0; depth < 2 && !string.IsNullOrWhiteSpace(dir); depth++)
            {
                string fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
                if (string.Equals(fullDir, chunksRoot, StringComparison.OrdinalIgnoreCase)
                    || !fullDir.StartsWith(chunksRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (Directory.Exists(fullDir) && !Directory.EnumerateFileSystemEntries(fullDir).Any())
                {
                    Directory.Delete(fullDir, recursive: false);
                }
                else
                {
                    break;
                }

                dir = Path.GetDirectoryName(fullDir);
            }
        }
        catch
        {
            // Best-effort empty shard cleanup.
        }
    }

    private static async Task WriteChunkAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        string parentDir = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"Chunk path has no parent directory: {destinationPath}");
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

            if (!File.Exists(destinationPath))
            {
                File.Move(tempPath, destinationPath, overwrite: true);
            }
            else
            {
                TryDeleteSilent(tempPath);
            }
        }
        catch
        {
            TryDeleteSilent(tempPath);
            throw;
        }
    }

    private static string ValidateAndNormalizeHash(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("BLAKE3 chunk hash cannot be empty.", nameof(raw));
        }

        string normalized = raw.Trim().ToLowerInvariant();
        if (!ValidBlake3HexRegex.IsMatch(normalized))
        {
            throw new ArgumentException($"Invalid 64-character lowercase hex BLAKE3 hash: '{raw}'.", nameof(raw));
        }

        return normalized;
    }

    private static void TryDeleteSilent(string path)
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
            // Best-effort temp cleanup.
        }
    }
}
