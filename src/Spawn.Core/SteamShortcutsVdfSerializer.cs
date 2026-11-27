using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Spawn.Core;

/// <summary>
/// Valve Binary KeyValues1 field type byte tags used in <c>shortcuts.vdf</c>.
/// </summary>
public enum BinaryVdfValueType : byte
{
    Map = 0x00,
    String = 0x01,
    Int32 = 0x02,
    Float32 = 0x03,
    Pointer = 0x04,
    WideString = 0x05,
    Color = 0x06,
    UInt64 = 0x07,
    MapEnd = 0x08,
}

/// <summary>
/// Single ordered key-value entry inside a <see cref="BinaryVdfMap"/>.
/// Preserves exact key casing, binary wire type, and unknown fields on round-trip.
/// </summary>
public sealed class BinaryVdfField
{
    public BinaryVdfField(string key, BinaryVdfValueType type, object value)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Type = type;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Key { get; }
    public BinaryVdfValueType Type { get; set; }
    public object Value { get; set; }
}

/// <summary>
/// Ordered map node representing a Binary KeyValues1 object (<c>0x00 ... 0x08</c>).
/// </summary>
public sealed class BinaryVdfMap
{
    public List<BinaryVdfField> Fields { get; } = [];

    public BinaryVdfField? FindField(string key)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return Fields[i];
            }
        }

        return null;
    }

    public string? GetString(string key) =>
        FindField(key) is { Type: BinaryVdfValueType.String or BinaryVdfValueType.WideString, Value: string s } ? s : null;

    public int? GetInt32(string key)
    {
        BinaryVdfField? field = FindField(key);
        if (field is null)
        {
            return null;
        }

        return field.Value switch
        {
            int i => i,
            uint ui => unchecked((int)ui),
            ulong ul => unchecked((int)ul),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null,
        };
    }

    public BinaryVdfMap? GetMap(string key) =>
        FindField(key) is { Type: BinaryVdfValueType.Map, Value: BinaryVdfMap m } ? m : null;

    public void SetString(string key, string value)
    {
        string safeVal = value ?? string.Empty;
        BinaryVdfField? existing = FindField(key);
        if (existing is not null)
        {
            existing.Type = BinaryVdfValueType.String;
            existing.Value = safeVal;
            return;
        }

        Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.String, safeVal));
    }

    public void SetInt32(string key, int value)
    {
        BinaryVdfField? existing = FindField(key);
        if (existing is not null)
        {
            existing.Type = BinaryVdfValueType.Int32;
            existing.Value = value;
            return;
        }

        Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.Int32, value));
    }

    public BinaryVdfMap GetOrCreateMap(string key)
    {
        BinaryVdfField? existing = FindField(key);
        if (existing is { Type: BinaryVdfValueType.Map, Value: BinaryVdfMap map })
        {
            return map;
        }

        var created = new BinaryVdfMap();
        if (existing is not null)
        {
            existing.Type = BinaryVdfValueType.Map;
            existing.Value = created;
        }
        else
        {
            Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.Map, created));
        }

        return created;
    }
}

/// <summary>
/// Desired fields for a non-Steam shortcut entry managed by SPAWN in <c>shortcuts.vdf</c>.
/// </summary>
public sealed record SteamShortcutDesiredSpec(
    string AppName,
    string NormalizedExeFullPath,
    string QuotedExe,
    string NormalizedStartDir,
    string QuotedStartDir,
    string IconPath,
    string LaunchOptions);

/// <summary>
/// Pure managed Binary KeyValues1 reader/writer for Steam's <c>userdata\&lt;accountId&gt;\config\shortcuts.vdf</c>.
/// Losslessly preserves all existing entries, field order, and unknown Valve field types (<c>0x00</c>–<c>0x07</c>).
/// </summary>
public static class SteamShortcutsVdfSerializer
{
    private const byte TypeEnd = 0x08;
    private const byte TypeEndAlt = 0x0B;
    private const int MaxNestingDepth = 64;

    /// <summary>
    /// Creates an empty canonical Steam <c>shortcuts.vdf</c> root document (<c>\x00shortcuts\x00\x08\x08</c>).
    /// </summary>
    public static BinaryVdfMap CreateEmptyShortcutsDocument()
    {
        var root = new BinaryVdfMap();
        root.Fields.Add(new BinaryVdfField("shortcuts", BinaryVdfValueType.Map, new BinaryVdfMap()));
        return root;
    }

    /// <summary>
    /// Computes the non-Steam shortcut 32-bit <c>appid</c> using Valve's CRC32 formula with high bit <c>0x80000000</c> set.
    /// <paramref name="storedExe"/> must match the exact stored <c>Exe</c> string (including quotes when quoted).
    /// </summary>
    public static int ComputeShortcutAppId(string storedExe, string appName)
    {
        byte[] payload = Encoding.UTF8.GetBytes((storedExe ?? string.Empty) + (appName ?? string.Empty));
        uint crc = Crc32.HashToUInt32(payload);
        return unchecked((int)(crc | 0x80000000u));
    }

    /// <summary>
    /// Computes the 64-bit <c>steam://rungameid/&lt;id&gt;</c> identifier from a non-Steam shortcut's 32-bit <c>appid</c>.
    /// </summary>
    public static ulong ComputeRunGameId(int appId) =>
        ((ulong)unchecked((uint)appId) << 32) | 0x02000000UL;

    /// <summary>
    /// Builds a <c>steam://rungameid/&lt;runGameId&gt;</c> launch URI for a non-Steam shortcut.
    /// </summary>
    public static string BuildRunGameIdUri(int shortcutAppId) =>
        $"steam://rungameid/{ComputeRunGameId(shortcutAppId).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Builds a <c>steam://rungameid/&lt;steamAppId&gt;</c> launch URI for an official Steam Store application.
    /// </summary>
    public static string BuildOfficialRunGameIdUri(int steamAppId) =>
        $"steam://rungameid/{steamAppId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Formats a filesystem path with surrounding quotes matching Steam's native <c>Exe</c>/<c>StartDir</c> convention.
    /// </summary>
    public static string QuoteShortcutPath(string path)
    {
        string trimmed = (path ?? string.Empty).Trim().Trim('"');
        return $"\"{trimmed}\"";
    }

    /// <summary>
    /// Normalizes a path stored in <c>Exe</c> or <c>StartDir</c> for case-insensitive comparison.
    /// </summary>
    public static string NormalizePathForComparison(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        string cleaned = rawPath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cleaned));
        }
        catch
        {
            return cleaned.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    /// <summary>
    /// Parses a Binary KeyValues1 <c>shortcuts.vdf</c> payload into an ordered lossless <see cref="BinaryVdfMap"/>.
    /// </summary>
    public static BinaryVdfMap Read(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return CreateEmptyShortcutsDocument();
        }

        int offset = 0;
        return ReadMapBody(data, ref offset, depth: 0);
    }

    /// <summary>
    /// Reads and parses a <c>shortcuts.vdf</c> file from disk using shared read access.
    /// Returns an empty canonical document if the file does not exist or is 0 bytes.
    /// </summary>
    public static BinaryVdfMap ReadFile(string shortcutsVdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutsVdfPath);
        if (!File.Exists(shortcutsVdfPath))
        {
            return CreateEmptyShortcutsDocument();
        }

        using var fs = new FileStream(
            shortcutsVdfPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length == 0)
        {
            return CreateEmptyShortcutsDocument();
        }

        byte[] bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return Read(bytes);
    }

    /// <summary>
    /// Serializes a <see cref="BinaryVdfMap"/> root document into canonical Valve Binary KeyValues1 wire bytes.
    /// </summary>
    public static byte[] Write(BinaryVdfMap document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var ms = new MemoryStream(512);
        WriteMapBody(ms, document, depth: 0);
        return ms.ToArray();
    }

    /// <summary>
    /// Backs up any existing <c>shortcuts.vdf</c> to <c>shortcuts.vdf.spawn.bak</c>, writes the serialized bytes
    /// to a temporary file with <see cref="FileOptions.WriteThrough"/>, and atomically replaces the target file.
    /// </summary>
    public static void WriteFileAtomically(string shortcutsVdfPath, BinaryVdfMap document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutsVdfPath);
        ArgumentNullException.ThrowIfNull(document);

        string fullPath = Path.GetFullPath(shortcutsVdfPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] payload = Write(document);

        if (File.Exists(fullPath))
        {
            string backupPath = fullPath + ".spawn.bak";
            File.Copy(fullPath, backupPath, overwrite: true);
        }

        string tempPath = fullPath + ".spawn.tmp";
        try
        {
            using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                fs.Write(payload, 0, payload.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore temp cleanup failure.
            }

            throw;
        }
    }

    /// <summary>
    /// Locates an existing shortcut entry in <paramref name="document"/> by <paramref name="registeredShortcutAppId"/>
    /// first, then by case-insensitive normalized <c>Exe</c> path.
    /// </summary>
    public static BinaryVdfMap? FindShortcutEntry(
        BinaryVdfMap document,
        int? registeredShortcutAppId,
        string normalizedExeFullPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        BinaryVdfMap? shortcuts = document.GetMap("shortcuts");
        if (shortcuts is null || shortcuts.Fields.Count == 0)
        {
            return null;
        }

        if (registeredShortcutAppId is { } targetAppId && targetAppId != 0)
        {
            foreach (BinaryVdfField field in shortcuts.Fields)
            {
                if (field.Type == BinaryVdfValueType.Map
                    && field.Value is BinaryVdfMap entry
                    && entry.GetInt32("appid") == targetAppId)
                {
                    return entry;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedExeFullPath))
        {
            string normalizedTarget = NormalizePathForComparison(normalizedExeFullPath);
            foreach (BinaryVdfField field in shortcuts.Fields)
            {
                if (field.Type == BinaryVdfValueType.Map
                    && field.Value is BinaryVdfMap entry)
                {
                    string existingExe = NormalizePathForComparison(entry.GetString("Exe"));
                    if (!string.IsNullOrWhiteSpace(existingExe)
                        && string.Equals(existingExe, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether <paramref name="existingEntry"/> already matches <paramref name="desired"/> with a valid non-zero <c>appid</c>.
    /// </summary>
    public static bool IsEntryUpToDate(BinaryVdfMap existingEntry, SteamShortcutDesiredSpec desired)
    {
        ArgumentNullException.ThrowIfNull(existingEntry);
        ArgumentNullException.ThrowIfNull(desired);

        int? appId = existingEntry.GetInt32("appid");
        if (appId is null or 0)
        {
            return false;
        }

        string existingExe = NormalizePathForComparison(existingEntry.GetString("Exe"));
        if (!string.Equals(existingExe, desired.NormalizedExeFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string existingStartDir = NormalizePathForComparison(existingEntry.GetString("StartDir"));
        if (!string.Equals(existingStartDir, desired.NormalizedStartDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string existingName = existingEntry.GetString("AppName") ?? string.Empty;
        if (!string.Equals(existingName, desired.AppName, StringComparison.Ordinal))
        {
            return false;
        }

        string existingLaunchOpts = existingEntry.GetString("LaunchOptions") ?? string.Empty;
        if (!string.Equals(existingLaunchOpts, desired.LaunchOptions, StringComparison.Ordinal))
        {
            return false;
        }

        if ((existingEntry.GetInt32("AllowOverlay") ?? 0) != 1)
        {
            return false;
        }

        if ((existingEntry.GetInt32("AllowDesktopConfig") ?? 0) != 1)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Inserts or updates a non-Steam shortcut inside <paramref name="document"/> while preserving all other entries,
    /// existing <c>appid</c>, field order, and unknown Valve fields.
    /// </summary>
    public static (int ShortcutAppId, bool Modified) UpsertShortcutEntry(
        BinaryVdfMap document,
        SteamShortcutDesiredSpec desired,
        int? registeredShortcutAppId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(desired);

        BinaryVdfMap shortcuts = document.GetOrCreateMap("shortcuts");
        BinaryVdfMap? existing = FindShortcutEntry(document, registeredShortcutAppId, desired.NormalizedExeFullPath);

        if (existing is not null)
        {
            if (IsEntryUpToDate(existing, desired))
            {
                return (existing.GetInt32("appid")!.Value, false);
            }

            int appId = existing.GetInt32("appid") is { } storedId && storedId != 0
                ? storedId
                : ComputeShortcutAppId(desired.QuotedExe, desired.AppName);

            existing.SetInt32("appid", appId);
            existing.SetString("AppName", desired.AppName);
            existing.SetString("Exe", desired.QuotedExe);
            existing.SetString("StartDir", desired.QuotedStartDir);
            if (string.IsNullOrWhiteSpace(existing.GetString("Icon")) && !string.IsNullOrWhiteSpace(desired.IconPath))
            {
                existing.SetString("Icon", desired.IconPath);
            }

            existing.SetString("LaunchOptions", desired.LaunchOptions);
            existing.SetInt32("AllowDesktopConfig", 1);
            existing.SetInt32("AllowOverlay", 1);
            _ = existing.GetOrCreateMap("tags");

            return (appId, true);
        }

        int newAppId = ComputeShortcutAppId(desired.QuotedExe, desired.AppName);
        int nextIndex = ComputeNextEntryIndex(shortcuts);

        var entry = new BinaryVdfMap();
        entry.SetInt32("appid", newAppId);
        entry.SetString("AppName", desired.AppName);
        entry.SetString("Exe", desired.QuotedExe);
        entry.SetString("StartDir", desired.QuotedStartDir);
        entry.SetString("Icon", desired.IconPath ?? string.Empty);
        entry.SetString("ShortcutPath", string.Empty);
        entry.SetString("LaunchOptions", desired.LaunchOptions ?? string.Empty);
        entry.SetInt32("IsHidden", 0);
        entry.SetInt32("AllowDesktopConfig", 1);
        entry.SetInt32("AllowOverlay", 1);
        entry.SetInt32("OpenVR", 0);
        entry.SetInt32("Devkit", 0);
        entry.SetString("DevkitGameID", string.Empty);
        entry.SetInt32("DevkitOverrideAppID", 0);
        entry.SetInt32("LastPlayTime", 0);
        entry.SetString("FlatpakAppID", string.Empty);
        _ = entry.GetOrCreateMap("tags");

        shortcuts.Fields.Add(new BinaryVdfField(nextIndex.ToString(CultureInfo.InvariantCulture), BinaryVdfValueType.Map, entry));
        return (newAppId, true);
    }

    private static int ComputeNextEntryIndex(BinaryVdfMap shortcuts)
    {
        int maxIndex = -1;
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BinaryVdfField field in shortcuts.Fields)
        {
            usedKeys.Add(field.Key);
            if (int.TryParse(field.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > maxIndex)
            {
                maxIndex = parsed;
            }
        }

        int candidate = maxIndex + 1;
        while (usedKeys.Contains(candidate.ToString(CultureInfo.InvariantCulture)))
        {
            candidate++;
        }

        return candidate;
    }

    private static BinaryVdfMap ReadMapBody(ReadOnlySpan<byte> data, ref int offset, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new InvalidDataException($"Binary VDF nesting depth exceeded {MaxNestingDepth}.");
        }

        var map = new BinaryVdfMap();
        while (offset < data.Length)
        {
            byte typeByte = data[offset++];
            if (typeByte is TypeEnd or TypeEndAlt)
            {
                return map;
            }

            string key = ReadNullTerminatedUtf8(data, ref offset);
            switch ((BinaryVdfValueType)typeByte)
            {
                case BinaryVdfValueType.Map:
                    BinaryVdfMap childMap = ReadMapBody(data, ref offset, depth + 1);
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.Map, childMap));
                    break;

                case BinaryVdfValueType.String:
                    string strVal = ReadNullTerminatedUtf8(data, ref offset);
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.String, strVal));
                    break;

                case BinaryVdfValueType.Int32:
                    EnsureRemaining(data, offset, 4, key);
                    int intVal = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.Int32, intVal));
                    break;

                case BinaryVdfValueType.Float32:
                    EnsureRemaining(data, offset, 4, key);
                    float floatVal = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.Float32, floatVal));
                    break;

                case BinaryVdfValueType.Pointer:
                    EnsureRemaining(data, offset, 4, key);
                    uint ptrVal = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.Pointer, ptrVal));
                    break;

                case BinaryVdfValueType.WideString:
                    string wideVal = ReadNullTerminatedUtf16(data, ref offset);
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.WideString, wideVal));
                    break;

                case BinaryVdfValueType.Color:
                    EnsureRemaining(data, offset, 4, key);
                    uint colorVal = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.Color, colorVal));
                    break;

                case BinaryVdfValueType.UInt64:
                    EnsureRemaining(data, offset, 8, key);
                    ulong ulongVal = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                    offset += 8;
                    map.Fields.Add(new BinaryVdfField(key, BinaryVdfValueType.UInt64, ulongVal));
                    break;

                default:
                    throw new InvalidDataException($"Unsupported Binary VDF field type 0x{typeByte:X2} for key '{key}' at offset {offset - 1}.");
            }
        }

        return map;
    }

    private static void WriteMapBody(Stream stream, BinaryVdfMap map, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new InvalidDataException($"Binary VDF nesting depth exceeded {MaxNestingDepth}.");
        }

        Span<byte> buf4 = stackalloc byte[4];
        Span<byte> buf8 = stackalloc byte[8];

        foreach (BinaryVdfField field in map.Fields)
        {
            stream.WriteByte((byte)field.Type);
            WriteNullTerminatedUtf8(stream, field.Key);

            switch (field.Type)
            {
                case BinaryVdfValueType.Map:
                    WriteMapBody(stream, (BinaryVdfMap)field.Value, depth + 1);
                    break;

                case BinaryVdfValueType.String:
                    WriteNullTerminatedUtf8(stream, (string)field.Value);
                    break;

                case BinaryVdfValueType.Int32:
                    BinaryPrimitives.WriteInt32LittleEndian(buf4, Convert.ToInt32(field.Value, CultureInfo.InvariantCulture));
                    stream.Write(buf4);
                    break;

                case BinaryVdfValueType.Float32:
                    BinaryPrimitives.WriteSingleLittleEndian(buf4, Convert.ToSingle(field.Value, CultureInfo.InvariantCulture));
                    stream.Write(buf4);
                    break;

                case BinaryVdfValueType.Pointer:
                    BinaryPrimitives.WriteUInt32LittleEndian(buf4, Convert.ToUInt32(field.Value, CultureInfo.InvariantCulture));
                    stream.Write(buf4);
                    break;

                case BinaryVdfValueType.WideString:
                    WriteNullTerminatedUtf16(stream, (string)field.Value);
                    break;

                case BinaryVdfValueType.Color:
                    BinaryPrimitives.WriteUInt32LittleEndian(buf4, Convert.ToUInt32(field.Value, CultureInfo.InvariantCulture));
                    stream.Write(buf4);
                    break;

                case BinaryVdfValueType.UInt64:
                    BinaryPrimitives.WriteUInt64LittleEndian(buf8, Convert.ToUInt64(field.Value, CultureInfo.InvariantCulture));
                    stream.Write(buf8);
                    break;

                default:
                    throw new InvalidDataException($"Cannot serialize unsupported Binary VDF type 0x{(byte)field.Type:X2} for key '{field.Key}'.");
            }
        }

        stream.WriteByte(TypeEnd);
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> data, int offset, int required, string key)
    {
        if (offset + required > data.Length)
        {
            throw new InvalidDataException($"Unexpected end of Binary VDF stream reading {required} bytes for key '{key}'.");
        }
    }

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= data.Length)
        {
            throw new InvalidDataException($"Unterminated UTF-8 string in Binary VDF starting at offset {start}.");
        }

        string value = offset > start ? Encoding.UTF8.GetString(data.Slice(start, offset - start)) : string.Empty;
        offset++; // consume NUL terminator
        return value;
    }

    private static string ReadNullTerminatedUtf16(ReadOnlySpan<byte> data, ref int offset)
    {
        int start = offset;
        while (offset + 1 < data.Length && (data[offset] != 0 || data[offset + 1] != 0))
        {
            offset += 2;
        }

        if (offset + 1 >= data.Length)
        {
            throw new InvalidDataException($"Unterminated UTF-16 string in Binary VDF starting at offset {start}.");
        }

        string value = offset > start ? Encoding.Unicode.GetString(data.Slice(start, offset - start)) : string.Empty;
        offset += 2; // consume UTF-16 NUL terminator
        return value;
    }

    private static void WriteNullTerminatedUtf8(Stream stream, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            stream.Write(utf8, 0, utf8.Length);
        }

        stream.WriteByte(0x00);
    }

    private static void WriteNullTerminatedUtf16(Stream stream, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            byte[] utf16 = Encoding.Unicode.GetBytes(value);
            stream.Write(utf16, 0, utf16.Length);
        }

        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
    }
}
