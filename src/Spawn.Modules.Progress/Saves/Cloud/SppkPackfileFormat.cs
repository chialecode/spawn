using System.Buffers.Binary;
using System.Security.Cryptography;
using Blake3;
using ZstdSharp;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Input chunk descriptor used when assembling a 32 MiB immutable <c>SPPK</c> v1 packfile.
/// </summary>
public sealed record SppkChunkInput(
    string ChunkBlake3Hex,
    ReadOnlyMemory<byte> CompressedBytes,
    uint RawLength);

/// <summary>
/// Single fixed 48-byte entry parsed from an <c>SPPK</c> v1 Footer Directory Index.
/// </summary>
public sealed record SppkIndexEntry(
    string ChunkBlake3Hex,
    ulong PackOffset,
    uint ZstdLength,
    uint RawLength);

/// <summary>
/// Result of writing an immutable <c>SPPK</c> v1 packfile in memory.
/// </summary>
public sealed record SppkPackBuildResult(
    string PackBlake3Hex,
    string PackId,
    byte[] PackBytes,
    IReadOnlyList<SppkIndexEntry> Entries)
{
    public string RelativePackPath => $"packs/{PackId}.sppk";
}

/// <summary>
/// Parsed <c>SPPK</c> v1 Footer Directory Index and Trailer metadata.
/// </summary>
public sealed record SppkParsedFooter(
    byte Version,
    uint Flags,
    uint FooterByteLength,
    string PackBlake3Hex,
    IReadOnlyList<SppkIndexEntry> Entries);

/// <summary>
/// Pure-managed <c>SPPK</c> (SPAWN Packfile v1) binary writer, full-stream verifier,
/// tail-only HTTP <c>Range</c> footer parser, and single-chunk extractor.
/// </summary>
/// <remarks>
/// Binary layout (little-endian):
/// <list type="bullet">
/// <item>Header (8 B): Magic <c>"SPPK"</c> (<c>0x53 0x50 0x50 0x4B</c>), Version <c>0x01</c>, Flags (3 B, default <c>0x000000</c>).</item>
/// <item>Payload Body: concatenated Zstd-compressed chunks (target &lt;= 32 MiB per pack; single chunk &gt; 32 MiB allowed solo).</item>
/// <item>Footer Directory Index (<c>4 + N * 48</c> B): <c>EntryCount</c> (<c>uint32 LE</c>) + <c>N</c> entries of
/// <c>[32B ChunkBlake3, 8B PackOffset (uint64 LE), 4B ZstdLength (uint32 LE), 4B RawLength (uint32 LE)]</c>.</item>
/// <item>Trailer (36 B): <c>FooterByteLength</c> (<c>uint32 LE</c>) + <c>PackBlake3</c> (32 B BLAKE3 over <c>0x0000..End-32</c>).</item>
/// </list>
/// </remarks>
public static class SppkPackfileFormat
{
    public const int DefaultMaxPackPayloadBytes = 32 * 1024 * 1024;
    public const int HeaderSize = 8;
    public const int FooterCountSize = 4;
    public const int FooterEntrySize = 48;
    public const int FooterLengthFieldSize = 4;
    public const int PackHashSize = 32;
    public const int TrailerSize = FooterLengthFieldSize + PackHashSize; // 36 bytes
    public const int MinimumPackSize = HeaderSize + FooterCountSize + TrailerSize; // 48 bytes
    public const int DefaultTailReadBytes = 4096;
    public const byte FormatVersion = 0x01;

    private static ReadOnlySpan<byte> MagicBytes => "SPPK"u8;

    /// <summary>
    /// Partitions unique compressed chunks into ordered batches targeting <paramref name="maxPayloadBytes"/>
    /// (default 32 MiB). Any single chunk larger than <paramref name="maxPayloadBytes"/> is placed in its own solo pack.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SppkChunkInput>> PartitionChunksForPacks(
        IReadOnlyList<SppkChunkInput> chunks,
        long maxPayloadBytes = DefaultMaxPackPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (maxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes), "Max payload size must be positive.");
        }

        var batches = new List<IReadOnlyList<SppkChunkInput>>();
        var currentBatch = new List<SppkChunkInput>();
        long currentPayloadBytes = 0;
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (SppkChunkInput chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            string normalizedHash = NormalizeBlake3Hex(chunk.ChunkBlake3Hex);
            if (!seenHashes.Add(normalizedHash))
            {
                continue;
            }

            SppkChunkInput normalizedChunk = chunk with { ChunkBlake3Hex = normalizedHash };
            long chunkSize = normalizedChunk.CompressedBytes.Length;

            if (chunkSize > maxPayloadBytes)
            {
                if (currentBatch.Count > 0)
                {
                    batches.Add(currentBatch);
                    currentBatch = [];
                    currentPayloadBytes = 0;
                }

                batches.Add([normalizedChunk]);
                continue;
            }

            if (currentBatch.Count > 0 && currentPayloadBytes + chunkSize > maxPayloadBytes)
            {
                batches.Add(currentBatch);
                currentBatch = [];
                currentPayloadBytes = 0;
            }

            currentBatch.Add(normalizedChunk);
            currentPayloadBytes += chunkSize;
        }

        if (currentBatch.Count > 0)
        {
            batches.Add(currentBatch);
        }

        return batches;
    }

    /// <summary>
    /// Serializes a non-empty list of Zstd-compressed chunks into an immutable <c>SPPK</c> v1 packfile.
    /// </summary>
    public static SppkPackBuildResult WritePack(
        IReadOnlyList<SppkChunkInput> chunks,
        uint flags = 0)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("SPPK packfile requires at least one chunk.", nameof(chunks));
        }

        if (flags > 0x00FF_FFFFu)
        {
            throw new ArgumentOutOfRangeException(nameof(flags), "SPPK v1 flags field is 24 bits (3 bytes).");
        }

        long payloadLength = 0;
        var entries = new List<SppkIndexEntry>(chunks.Count);
        var hashBytesList = new List<byte[]>(chunks.Count);

        foreach (SppkChunkInput chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            string normalizedHex = NormalizeBlake3Hex(chunk.ChunkBlake3Hex);
            byte[] hashBytes = Convert.FromHexString(normalizedHex);
            if (chunk.CompressedBytes.Length <= 0 || chunk.CompressedBytes.Length > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(chunks), "Compressed chunk length must be positive.");
            }

            ulong offset = checked((ulong)(HeaderSize + payloadLength));
            uint zstdLength = checked((uint)chunk.CompressedBytes.Length);
            entries.Add(new SppkIndexEntry(normalizedHex, offset, zstdLength, chunk.RawLength));
            hashBytesList.Add(hashBytes);
            payloadLength = checked(payloadLength + zstdLength);
        }

        long footerByteLengthLong = checked(FooterCountSize + (long)entries.Count * FooterEntrySize);
        if (footerByteLengthLong > uint.MaxValue)
        {
            throw new InvalidOperationException("SPPK footer exceeds maximum 32-bit length.");
        }

        uint footerByteLength = (uint)footerByteLengthLong;
        long totalLengthLong = checked(HeaderSize + payloadLength + footerByteLengthLong + TrailerSize);
        if (totalLengthLong > int.MaxValue)
        {
            throw new InvalidOperationException("SPPK packfile exceeds in-memory buffer limit.");
        }

        byte[] buffer = new byte[(int)totalLengthLong];
        Span<byte> span = buffer;

        // 1. Header (8 bytes)
        MagicBytes.CopyTo(span[..4]);
        span[4] = FormatVersion;
        span[5] = (byte)(flags & 0xFF);
        span[6] = (byte)((flags >> 8) & 0xFF);
        span[7] = (byte)((flags >> 16) & 0xFF);

        // 2. Payload Body
        int cursor = HeaderSize;
        for (int i = 0; i < chunks.Count; i++)
        {
            ReadOnlySpan<byte> compressed = chunks[i].CompressedBytes.Span;
            compressed.CopyTo(span.Slice(cursor, compressed.Length));
            cursor += compressed.Length;
        }

        // 3. Footer Directory Index (4 + N * 48 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(cursor, 4), (uint)entries.Count);
        cursor += 4;

        for (int i = 0; i < entries.Count; i++)
        {
            SppkIndexEntry entry = entries[i];
            hashBytesList[i].CopyTo(span.Slice(cursor, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(cursor + 32, 8), entry.PackOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(cursor + 40, 4), entry.ZstdLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(cursor + 44, 4), entry.RawLength);
            cursor += FooterEntrySize;
        }

        // 4. Trailer (36 bytes): FooterByteLength (4B LE) + PackBlake3 (32B over 0x0000..End-32)
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(cursor, FooterLengthFieldSize), footerByteLength);
        cursor += FooterLengthFieldSize;

        Hash digest = Hasher.Hash(span[..cursor]);
        digest.AsSpan().CopyTo(span.Slice(cursor, PackHashSize));
        string packBlake3Hex = digest.ToString();
        string packId = $"pack-{packBlake3Hex}";

        return new SppkPackBuildResult(packBlake3Hex, packId, buffer, entries);
    }

    /// <summary>
    /// Fully validates an <c>SPPK</c> v1 packfile (magic, version, full-stream BLAKE3 trailer, footer length,
    /// and every entry's payload offset bounds) and returns its parsed footer index.
    /// </summary>
    public static SppkParsedFooter VerifyAndReadFullPack(ReadOnlySpan<byte> packBytes)
    {
        if (packBytes.Length < MinimumPackSize)
        {
            throw new InvalidDataException(
                $"SPPK packfile is too small ({packBytes.Length} bytes; minimum is {MinimumPackSize} bytes).");
        }

        if (!packBytes[..4].SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Invalid SPPK magic header.");
        }

        byte version = packBytes[4];
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported SPPK version: 0x{version:X2}.");
        }

        uint flags = (uint)packBytes[5]
            | ((uint)packBytes[6] << 8)
            | ((uint)packBytes[7] << 16);

        int hashStart = packBytes.Length - PackHashSize;
        Hash computed = Hasher.Hash(packBytes[..hashStart]);
        ReadOnlySpan<byte> expectedHash = packBytes.Slice(hashStart, PackHashSize);
        if (!CryptographicOperations.FixedTimeEquals(computed.AsSpan(), expectedHash))
        {
            throw new InvalidDataException("SPPK PackBlake3 integrity verification failed.");
        }

        SppkParsedFooter footer = ParseTailFooter(packBytes, packBytes.Length, version, flags);
        return footer;
    }

    /// <summary>
    /// Attempts to parse an <c>SPPK</c> v1 Footer Directory Index from a trailing slice (e.g., an HTTP <c>Range: bytes=-4096</c> response).
    /// When the tail slice is shorter than <c>FooterByteLength + 36</c>, returns <see langword="false"/> and reports the exact
    /// <paramref name="requiredTailBytes"/> needed for a single follow-up range request.
    /// </summary>
    public static bool TryParseTailFooter(
        ReadOnlySpan<byte> tailBytes,
        long totalPackLength,
        out SppkParsedFooter? footer,
        out int requiredTailBytes)
    {
        footer = null;
        requiredTailBytes = 0;

        if (totalPackLength < MinimumPackSize)
        {
            throw new InvalidDataException(
                $"SPPK packfile length ({totalPackLength} bytes) is smaller than minimum valid size ({MinimumPackSize} bytes).");
        }

        if (tailBytes.Length < TrailerSize)
        {
            requiredTailBytes = (int)Math.Min(totalPackLength, Math.Max(DefaultTailReadBytes, TrailerSize));
            return false;
        }

        uint footerByteLength = BinaryPrimitives.ReadUInt32LittleEndian(
            tailBytes.Slice(tailBytes.Length - TrailerSize, FooterLengthFieldSize));

        ValidateFooterByteLength(footerByteLength, totalPackLength);

        long totalTailNeededLong = (long)footerByteLength + TrailerSize;
        if (totalTailNeededLong > int.MaxValue)
        {
            throw new InvalidDataException($"SPPK footer tail size ({totalTailNeededLong} bytes) exceeds supported limit.");
        }

        int totalTailNeeded = (int)totalTailNeededLong;
        if (tailBytes.Length < totalTailNeeded)
        {
            requiredTailBytes = totalTailNeeded;
            return false;
        }

        footer = ParseTailFooter(tailBytes, totalPackLength, FormatVersion, flags: 0);
        return true;
    }

    /// <summary>
    /// Parses the Footer Directory Index and Trailer from a buffer containing at least the trailing
    /// <c>FooterByteLength + 36</c> bytes of an <c>SPPK</c> packfile.
    /// </summary>
    public static SppkParsedFooter ParseTailFooter(
        ReadOnlySpan<byte> tailBytes,
        long totalPackLength,
        byte version = FormatVersion,
        uint flags = 0)
    {
        if (totalPackLength < MinimumPackSize)
        {
            throw new InvalidDataException(
                $"SPPK packfile length ({totalPackLength} bytes) is smaller than minimum valid size ({MinimumPackSize} bytes).");
        }

        if (tailBytes.Length < TrailerSize)
        {
            throw new InvalidDataException(
                $"SPPK tail buffer is too short ({tailBytes.Length} bytes; need at least {TrailerSize} bytes).");
        }

        uint footerByteLength = BinaryPrimitives.ReadUInt32LittleEndian(
            tailBytes.Slice(tailBytes.Length - TrailerSize, FooterLengthFieldSize));
        ValidateFooterByteLength(footerByteLength, totalPackLength);

        long requiredTail = (long)footerByteLength + TrailerSize;
        if (tailBytes.Length < requiredTail)
        {
            throw new InvalidDataException(
                $"SPPK tail buffer ({tailBytes.Length} bytes) does not contain full footer ({requiredTail} bytes).");
        }

        int footerStart = tailBytes.Length - (int)requiredTail;
        ReadOnlySpan<byte> footerSpan = tailBytes.Slice(footerStart, (int)footerByteLength);

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(footerSpan[..FooterCountSize]);
        long expectedFooterBytes = FooterCountSize + (long)entryCount * FooterEntrySize;
        if (expectedFooterBytes != footerByteLength)
        {
            throw new InvalidDataException(
                $"SPPK footer entry count ({entryCount}) does not match FooterByteLength ({footerByteLength}).");
        }

        ulong payloadEndOffset = checked((ulong)(totalPackLength - requiredTail));
        var entries = new List<SppkIndexEntry>((int)entryCount);
        int cursor = FooterCountSize;

        for (uint i = 0; i < entryCount; i++)
        {
            string chunkHex = Convert.ToHexStringLower(footerSpan.Slice(cursor, 32));
            ulong packOffset = BinaryPrimitives.ReadUInt64LittleEndian(footerSpan.Slice(cursor + 32, 8));
            uint zstdLength = BinaryPrimitives.ReadUInt32LittleEndian(footerSpan.Slice(cursor + 40, 4));
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(footerSpan.Slice(cursor + 44, 4));

            if (packOffset < HeaderSize || zstdLength == 0 || packOffset > payloadEndOffset || zstdLength > payloadEndOffset - packOffset)
            {
                throw new InvalidDataException(
                    $"SPPK entry {i} ('{chunkHex}') has out-of-bounds payload slice [offset={packOffset}, len={zstdLength}, payloadEnd={payloadEndOffset}].");
            }

            entries.Add(new SppkIndexEntry(chunkHex, packOffset, zstdLength, rawLength));
            cursor += FooterEntrySize;
        }

        string packBlake3Hex = Convert.ToHexStringLower(tailBytes.Slice(tailBytes.Length - PackHashSize, PackHashSize));
        return new SppkParsedFooter(version, flags, footerByteLength, packBlake3Hex, entries);
    }

    /// <summary>
    /// Extracts a single chunk slice from a complete in-memory packfile, decompresses it with Zstd,
    /// and verifies both its BLAKE3 hash and raw byte length.
    /// </summary>
    public static byte[] ExtractAndVerifyChunk(ReadOnlySpan<byte> packBytes, SppkIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.PackOffset > (ulong)packBytes.Length
            || entry.ZstdLength > (ulong)packBytes.Length - entry.PackOffset)
        {
            throw new InvalidDataException(
                $"SPPK chunk '{entry.ChunkBlake3Hex}' slice [offset={entry.PackOffset}, len={entry.ZstdLength}] exceeds pack bounds ({packBytes.Length}).");
        }

        ReadOnlySpan<byte> compressed = packBytes.Slice((int)entry.PackOffset, (int)entry.ZstdLength);
        return VerifyCompressedChunkBytes(compressed, entry.ChunkBlake3Hex, entry.RawLength);
    }

    /// <summary>
    /// Decompresses a Zstd chunk slice obtained via full read or HTTP <c>Range</c> request and verifies its
    /// BLAKE3 digest (and optional raw byte length).
    /// </summary>
    public static byte[] VerifyCompressedChunkBytes(
        ReadOnlySpan<byte> compressedBytes,
        string expectedChunkBlake3Hex,
        uint? expectedRawLength = null)
    {
        string normalizedExpected = NormalizeBlake3Hex(expectedChunkBlake3Hex);
        byte[] raw;
        try
        {
            using var decompressor = new Decompressor();
            raw = decompressor.Unwrap(compressedBytes).ToArray();
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"Failed to Zstd-decompress SPPK chunk '{normalizedExpected}': {ex.Message}",
                ex);
        }

        if (expectedRawLength.HasValue && (uint)raw.Length != expectedRawLength.Value)
        {
            throw new InvalidDataException(
                $"SPPK chunk '{normalizedExpected}' raw length mismatch: expected {expectedRawLength.Value}, got {raw.Length}.");
        }

        string actualHex = Hasher.Hash(raw).ToString();
        if (!string.Equals(actualHex, normalizedExpected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SPPK chunk BLAKE3 verification failed: expected '{normalizedExpected}', got '{actualHex}'.");
        }

        return raw;
    }

    private static void ValidateFooterByteLength(uint footerByteLength, long totalPackLength)
    {
        if (footerByteLength < FooterCountSize)
        {
            throw new InvalidDataException($"Invalid SPPK FooterByteLength ({footerByteLength} < {FooterCountSize}).");
        }

        if ((footerByteLength - FooterCountSize) % FooterEntrySize != 0)
        {
            throw new InvalidDataException(
                $"Invalid SPPK FooterByteLength ({footerByteLength}): not aligned to 4 + N * {FooterEntrySize}.");
        }

        long minimumRequiredPackBytes = HeaderSize + (long)footerByteLength + TrailerSize;
        if (minimumRequiredPackBytes > totalPackLength)
        {
            throw new InvalidDataException(
                $"Invalid SPPK FooterByteLength ({footerByteLength}): exceeds packfile size ({totalPackLength}).");
        }
    }

    private static string NormalizeBlake3Hex(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("BLAKE3 chunk hash cannot be empty.", nameof(raw));
        }

        string normalized = raw.Trim().ToLowerInvariant();
        if (normalized.Length != 64)
        {
            throw new ArgumentException($"Invalid 64-character hex BLAKE3 hash: '{raw}'.", nameof(raw));
        }

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                throw new ArgumentException($"Invalid hex character in BLAKE3 hash: '{raw}'.", nameof(raw));
            }
        }

        return normalized;
    }
}
