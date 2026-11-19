using System.Security.Cryptography;

namespace Spawn.Core;

/// <summary>
/// Thread-safe, lexicographically time-sortable 26-character Crockford Base32 ULID generator.
/// </summary>
public static class SpawnId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly Lock SyncLock = new();
    private static long _lastTimestampMs;
    private static readonly byte[] LastRandom = new byte[10];

    public static string NewUlid() => NewUlid(DateTimeOffset.UtcNow);

    public static string NewUlid(DateTimeOffset timestamp)
    {
        long unixMs = timestamp.ToUnixTimeMilliseconds();
        if (unixMs < 0)
        {
            unixMs = 0;
        }

        Span<byte> randomness = stackalloc byte[10];
        lock (SyncLock)
        {
            if (unixMs == _lastTimestampMs)
            {
                IncrementBigEndian(LastRandom);
                LastRandom.CopyTo(randomness);
            }
            else
            {
                _lastTimestampMs = unixMs;
                RandomNumberGenerator.Fill(LastRandom);
                LastRandom.CopyTo(randomness);
            }
        }

        Span<char> chars = stackalloc char[26];

        // 48-bit timestamp -> 10 Base32 characters
        ulong ts = (ulong)unixMs;
        chars[0] = Alphabet[(int)((ts >> 45) & 0x1F)];
        chars[1] = Alphabet[(int)((ts >> 40) & 0x1F)];
        chars[2] = Alphabet[(int)((ts >> 35) & 0x1F)];
        chars[3] = Alphabet[(int)((ts >> 30) & 0x1F)];
        chars[4] = Alphabet[(int)((ts >> 25) & 0x1F)];
        chars[5] = Alphabet[(int)((ts >> 20) & 0x1F)];
        chars[6] = Alphabet[(int)((ts >> 15) & 0x1F)];
        chars[7] = Alphabet[(int)((ts >> 10) & 0x1F)];
        chars[8] = Alphabet[(int)((ts >> 5) & 0x1F)];
        chars[9] = Alphabet[(int)(ts & 0x1F)];

        // 80-bit randomness -> 16 Base32 characters
        EncodeTenBytes(randomness, chars[10..]);
        return new string(chars);
    }

    private static void IncrementBigEndian(byte[] bytes)
    {
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            if (++bytes[i] != 0)
            {
                return;
            }
        }
    }

    private static void EncodeTenBytes(ReadOnlySpan<byte> src, Span<char> dst)
    {
        // First 5 bytes (40 bits) -> 8 Base32 chars
        ulong hi = ((ulong)src[0] << 32)
                 | ((ulong)src[1] << 24)
                 | ((ulong)src[2] << 16)
                 | ((ulong)src[3] << 8)
                 | src[4];
        dst[0] = Alphabet[(int)((hi >> 35) & 0x1F)];
        dst[1] = Alphabet[(int)((hi >> 30) & 0x1F)];
        dst[2] = Alphabet[(int)((hi >> 25) & 0x1F)];
        dst[3] = Alphabet[(int)((hi >> 20) & 0x1F)];
        dst[4] = Alphabet[(int)((hi >> 15) & 0x1F)];
        dst[5] = Alphabet[(int)((hi >> 10) & 0x1F)];
        dst[6] = Alphabet[(int)((hi >> 5) & 0x1F)];
        dst[7] = Alphabet[(int)(hi & 0x1F)];

        // Next 5 bytes (40 bits) -> 8 Base32 chars
        ulong lo = ((ulong)src[5] << 32)
                 | ((ulong)src[6] << 24)
                 | ((ulong)src[7] << 16)
                 | ((ulong)src[8] << 8)
                 | src[9];
        dst[8] = Alphabet[(int)((lo >> 35) & 0x1F)];
        dst[9] = Alphabet[(int)((lo >> 30) & 0x1F)];
        dst[10] = Alphabet[(int)((lo >> 25) & 0x1F)];
        dst[11] = Alphabet[(int)((lo >> 20) & 0x1F)];
        dst[12] = Alphabet[(int)((lo >> 15) & 0x1F)];
        dst[13] = Alphabet[(int)((lo >> 10) & 0x1F)];
        dst[14] = Alphabet[(int)((lo >> 5) & 0x1F)];
        dst[15] = Alphabet[(int)(lo & 0x1F)];
    }
}
