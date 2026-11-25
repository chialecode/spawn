namespace Spawn.Core.Storage;

/// <summary>
/// Single chunk slice produced by <see cref="FastCdcChunker"/>.
/// </summary>
public readonly record struct FastCdcChunkSlice(long Offset, ReadOnlyMemory<byte> Data);

/// <summary>
/// Deterministic Gear-Hash FastCDC content-defined chunker locked to SPAWN's cross-machine constants
/// (<c>data-model.md</c> §5, <c>A-01</c>):
/// <list type="bullet">
/// <item><description>Small files (<c>&lt; 2 MiB</c>) emit a single whole-file chunk.</description></item>
/// <item><description>Larger files use <c>MinSize = 256 KiB</c>, <c>NormalSize = 1 MiB</c>, <c>MaxSize = 4 MiB</c>.</description></item>
/// <item><description>256-entry Gear table deterministically generated via SplitMix64 from seed <c>0x535041574E5F4344UL</c> (<c>"SPAWN_CD"</c>).</description></item>
/// <item><description>Dual normalized masks: <c>MaskS = 0x0003590703530000UL</c> (21-bit) and <c>MaskL = 0x0000D90303530000UL</c> (19-bit).</description></item>
/// </list>
/// </summary>
public static class FastCdcChunker
{
    public const int MinSize = 256 * 1024;          // 262,144 B (256 KiB)
    public const int NormalSize = 1024 * 1024;      // 1,048,576 B (1 MiB)
    public const int MaxSize = 4 * 1024 * 1024;     // 4,194,304 B (4 MiB)
    public const int SmallFileSingleChunkThreshold = 2 * 1024 * 1024; // 2,097,152 B (2 MiB)

    public const ulong GearSeed = 0x535041574E5F4344UL; // ASCII "SPAWN_CD"
    public const ulong MaskS = 0x0003590703530000UL;    // 21-bit mask on [MinSize, NormalSize)
    public const ulong MaskL = 0x0000D90303530000UL;    // 19-bit mask on [NormalSize, MaxSize)

    private static readonly ulong[] GearTableBacking = BuildDeterministicGearTable();

    public static ReadOnlySpan<ulong> GearTable => GearTableBacking;

    /// <summary>
    /// Computes the next FastCDC cut length within <paramref name="buffer"/> using SPAWN's normalized dual-mask Gear hash.
    /// </summary>
    public static int FindNextBoundary(ReadOnlySpan<byte> buffer)
    {
        int length = buffer.Length;
        if (length <= MinSize)
        {
            return length;
        }

        int maxLimit = Math.Min(length, MaxSize);
        int normalLimit = Math.Min(maxLimit, NormalSize);
        ReadOnlySpan<ulong> gear = GearTableBacking;
        ulong fp = 0;

        int i = MinSize;
        for (; i < normalLimit; i++)
        {
            fp = (fp << 1) + gear[buffer[i]];
            if ((fp & MaskS) == 0)
            {
                return i + 1;
            }
        }

        for (; i < maxLimit; i++)
        {
            fp = (fp << 1) + gear[buffer[i]];
            if ((fp & MaskL) == 0)
            {
                return i + 1;
            }
        }

        return maxLimit;
    }

    /// <summary>
    /// Splits an in-memory buffer into deterministic FastCDC chunks (applying the <c>&lt; 2 MiB</c> single-chunk fast path).
    /// </summary>
    public static IReadOnlyList<FastCdcChunkSlice> ChunkBytes(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
        {
            return [];
        }

        if (data.Length < SmallFileSingleChunkThreshold)
        {
            return [new FastCdcChunkSlice(0, data)];
        }

        var slices = new List<FastCdcChunkSlice>(Math.Max(1, data.Length / NormalSize + 1));
        int offset = 0;
        while (offset < data.Length)
        {
            int remaining = data.Length - offset;
            int chunkLen = FindNextBoundary(data.Span.Slice(offset, Math.Min(remaining, MaxSize)));
            slices.Add(new FastCdcChunkSlice(offset, data.Slice(offset, chunkLen)));
            offset += chunkLen;
        }

        return slices;
    }

    /// <summary>
    /// Streams a file or payload through a bounded <c>4 MiB</c> sliding window, invoking <paramref name="onChunk"/>
    /// for every deterministic chunk without loading large save files fully into memory.
    /// </summary>
    public static async Task ChunkStreamAsync(
        Stream stream,
        long? knownTotalLength,
        Func<ReadOnlyMemory<byte>, long, CancellationToken, ValueTask> onChunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onChunk);

        long? effectiveLength = knownTotalLength ?? (stream.CanSeek ? stream.Length - stream.Position : null);

        // Fast path for files strictly smaller than 2 MiB: emit as one whole-file chunk.
        if (effectiveLength is >= 0 and < SmallFileSingleChunkThreshold)
        {
            if (effectiveLength == 0)
            {
                return;
            }

            byte[] smallBuf = new byte[(int)effectiveLength.Value];
            int totalRead = await ReadExactlyUpToAsync(stream, smallBuf, cancellationToken).ConfigureAwait(false);
            if (totalRead > 0)
            {
                await onChunk(smallBuf.AsMemory(0, totalRead), 0, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Unknown-length stream: read up to SmallFileSingleChunkThreshold first to decide fast path vs streaming CDC.
        byte[] window = new byte[MaxSize];
        int buffered = 0;
        bool reachedEof = false;

        while (buffered < MaxSize)
        {
            int n = await stream.ReadAsync(window.AsMemory(buffered, MaxSize - buffered), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                reachedEof = true;
                break;
            }

            buffered += n;
        }

        if (buffered == 0)
        {
            return;
        }

        if (reachedEof && buffered < SmallFileSingleChunkThreshold)
        {
            await onChunk(window.AsMemory(0, buffered), 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        long streamOffset = 0;
        while (buffered > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Refill window up to MaxSize unless stream reached EOF so boundary finding always sees a full window.
            while (!reachedEof && buffered < MaxSize)
            {
                int n = await stream.ReadAsync(window.AsMemory(buffered, MaxSize - buffered), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    reachedEof = true;
                    break;
                }

                buffered += n;
            }

            int cutLen = FindNextBoundary(window.AsSpan(0, buffered));
            await onChunk(window.AsMemory(0, cutLen), streamOffset, cancellationToken).ConfigureAwait(false);

            streamOffset += cutLen;
            int leftover = buffered - cutLen;
            if (leftover > 0)
            {
                Buffer.BlockCopy(window, cutLen, window, 0, leftover);
            }

            buffered = leftover;
        }
    }

    private static async Task<int> ReadExactlyUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static ulong[] BuildDeterministicGearTable()
    {
        var table = new ulong[256];
        ulong state = GearSeed;
        for (int i = 0; i < table.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }

        return table;
    }
}
