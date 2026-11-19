using System.Security.Cryptography;
using System.Text;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Transparently decodes (and optionally encodes for tests) GOG / PC <c>LIMBO</c> <c>savegame.txt</c> files,
/// supporting both plain-text key-value saves and Playdead's native AES-128 + 16-byte XOR mask binary container.
/// </summary>
public static class LimboSaveDecoder
{
    // Derived from static binary inspection of Playdead's StorageEmulator cipher parameters:
    // Key[0..15]  = "lt2Z0W82CuOb5srn"[i] ^ "wmex0UkRLYKUD0C5"[i]
    // Mask[0..15] = "L6mMCfcb1EwqyoKF"[i] ^ "Qmvub06mrlgvMxLs"[i]
    private const string KeyPartA = "lt2Z0W82CuOb5srnd4Feck52Bl0Hw2lH";
    private const string KeyPartB = "wmex0UkRLYKUD0C5BzxU60xKcxGHhOU9";
    private const string MaskPartA = "L6mMCfcb1EwqyoKF";
    private const string MaskPartB = "Qmvub06mrlgvMxLs";

    private static readonly byte[] AesKey16 = BuildXorBytes(KeyPartA, KeyPartB, 16);
    private static readonly byte[] BlockMask16 = BuildXorBytes(MaskPartA, MaskPartB, 16);

    public static string? DecodeToText(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.IsEmpty)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> span = TrimUtf8Bom(rawBytes);

        if (LooksLikePlainText(span))
        {
            return Encoding.UTF8.GetString(span).TrimEnd('\0');
        }

        if (rawBytes.Length >= 16 && (rawBytes.Length % 16) == 0)
        {
            string? decrypted = TryDecryptAesBlocks(rawBytes);
            if (!string.IsNullOrWhiteSpace(decrypted))
            {
                return decrypted;
            }
        }

        return Encoding.UTF8.GetString(span).TrimEnd('\0');
    }

    /// <summary>
    /// Encrypts plaintext <c>savegame.txt</c> content using LIMBO's native AES-128 block + XOR mask format.
    /// Useful for roundtrip verification in automated tests.
    /// </summary>
    public static byte[] EncryptFromText(string plainText, byte userProfileSeed = 0)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        byte[] utf8 = Encoding.UTF8.GetBytes(plainText);
        int paddedLen = (utf8.Length & 0x0F) == 0
            ? Math.Max(16, utf8.Length)
            : (utf8.Length - (utf8.Length & 0x0F) + 16);

        byte[] working = new byte[paddedLen];
        Buffer.BlockCopy(utf8, 0, working, 0, utf8.Length);

        Span<byte> mask = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            mask[i] = (byte)(BlockMask16[i] ^ userProfileSeed);
        }

        for (int offset = 0; offset < working.Length; offset += 16)
        {
            for (int i = 0; i < 16; i++)
            {
                working[offset + i] ^= mask[i];
            }
        }

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = AesKey16;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        return aes.EncryptEcb(working, PaddingMode.None);
    }

    private static string? TryDecryptAesBlocks(ReadOnlySpan<byte> cipherBytes)
    {
        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Key = AesKey16;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            byte[] decrypted = aes.DecryptEcb(cipherBytes, PaddingMode.None);
            for (int offset = 0; offset < decrypted.Length; offset += 16)
            {
                for (int i = 0; i < 16; i++)
                {
                    decrypted[offset + i] ^= BlockMask16[i];
                }
            }

            // If userProfileSeed was non-zero on another profile slot, detect via trailing zero padding or '=' delimiter.
            if (!ContainsAsciiKeyword(decrypted, "achievements"u8)
                && !ContainsAsciiKeyword(decrypted, "savepoint"u8)
                && decrypted.Length >= 16)
            {
                byte candidateSeed = decrypted[^1];
                if (candidateSeed != 0)
                {
                    byte[] alt = (byte[])decrypted.Clone();
                    for (int k = 0; k < alt.Length; k++)
                    {
                        alt[k] ^= candidateSeed;
                    }

                    if (ContainsAsciiKeyword(alt, "achievements"u8) || ContainsAsciiKeyword(alt, "savepoint"u8))
                    {
                        decrypted = alt;
                    }
                }
            }

            int nullIndex = Array.IndexOf(decrypted, (byte)0);
            int validLen = nullIndex >= 0 ? nullIndex : decrypted.Length;
            return Encoding.UTF8.GetString(decrypted, 0, validLen);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikePlainText(ReadOnlySpan<byte> bytes)
    {
        if (ContainsAsciiKeyword(bytes, "achievements"u8)
            || ContainsAsciiKeyword(bytes, "lastsavepoint"u8)
            || ContainsAsciiKeyword(bytes, "savepointreached"u8))
        {
            return true;
        }

        int inspectLen = Math.Min(bytes.Length, 128);
        int printable = 0;
        for (int i = 0; i < inspectLen; i++)
        {
            byte b = bytes[i];
            if ((b >= 32 && b < 127) || b is (byte)'\r' or (byte)'\n' or (byte)'\t')
            {
                printable++;
            }
        }

        return inspectLen > 0 && printable == inspectLen;
    }

    private static bool ContainsAsciiKeyword(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerAsciiNeedle)
    {
        if (haystack.Length < lowerAsciiNeedle.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - lowerAsciiNeedle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < lowerAsciiNeedle.Length; j++)
            {
                byte c = haystack[i + j];
                if (c is >= (byte)'A' and <= (byte)'Z')
                {
                    c = (byte)(c + 32);
                }

                if (c != lowerAsciiNeedle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<byte> TrimUtf8Bom(ReadOnlySpan<byte> span) =>
        span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF
            ? span[3..]
            : span;

    private static byte[] BuildXorBytes(string a, string b, int count)
    {
        byte[] result = new byte[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = (byte)(a[i] ^ b[i]);
        }

        return result;
    }
}
