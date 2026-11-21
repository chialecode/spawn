using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Bitmask location of a single Steam achievement inside <c>UserGameStatsSchema_&lt;AppId&gt;.bin</c>.
/// </summary>
public sealed record SteamBinAchievementBitMapping(
    string ApiName,
    int StatBlockId,
    int BitIndex,
    AchievementDefinitionDto Definition);

/// <summary>
/// Pure managed recursive Binary VDF (Valve KeyValues) parser for local Steam client
/// <c>UserGameStatsSchema_&lt;AppId&gt;.bin</c> and <c>UserGameStats_&lt;Id32&gt;_&lt;AppId&gt;.bin</c> caches.
/// Zero process handle / zero <c>SteamAPI_Init</c> intrusion.
/// </summary>
public static class SteamBinaryVdfParser
{
    private const byte TypeObject = 0x00;
    private const byte TypeString = 0x01;
    private const byte TypeInt32 = 0x02;
    private const byte TypeFloat32 = 0x03;
    private const byte TypePtr = 0x04;
    private const byte TypeWideString = 0x05;
    private const byte TypeColor = 0x06;
    private const byte TypeUInt64 = 0x07;
    private const byte TypeEnd = 0x08;
    private const byte TypeEndAlt = 0x0B;

    public static Dictionary<string, object?> ParseRoot(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        return ReadMapUntilEnd(data, ref offset, depth: 0);
    }

    /// <summary>
    /// Extracts localized definitions (including full unstripped <c>hidden=1</c> spoiler descriptions!)
    /// and <c>(StatBlockId, BitIndex)</c> bitmask coordinates from <c>UserGameStatsSchema_&lt;AppId&gt;.bin</c>.
    /// </summary>
    public static IReadOnlyList<SteamBinAchievementBitMapping> ParseSchemaBin(
        ReadOnlySpan<byte> rawSchemaBytes,
        int appId,
        string preferredLanguage = "schinese")
    {
        if (rawSchemaBytes.IsEmpty)
        {
            return [];
        }

        var root = ParseRoot(rawSchemaBytes);
        var statsNode = FindChildObject(root, "stats");
        if (statsNode is null)
        {
            // Often wrapped inside "<AppId>" -> "stats"
            foreach (var (_, val) in root)
            {
                if (val is Dictionary<string, object?> inner)
                {
                    statsNode = FindChildObject(inner, "stats");
                    if (statsNode is not null)
                    {
                        break;
                    }
                }
            }
        }

        if (statsNode is null)
        {
            return [];
        }

        var mappings = new List<SteamBinAchievementBitMapping>();
        int sortOrder = 0;

        foreach (var (statKey, statVal) in statsNode)
        {
            if (statVal is not Dictionary<string, object?> statObj)
            {
                continue;
            }

            int statId = TryGetInt(statObj, "id")
                         ?? (int.TryParse(statKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sk) ? sk : -1);
            if (statId < 0)
            {
                continue;
            }

            // Type 4 or "ACHIEVEMENTS" or presence of "bits" child object indicates an achievement bitpack block.
            var bitsObj = FindChildObject(statObj, "bits");
            if (bitsObj is null)
            {
                continue;
            }

            foreach (var (bitKey, bitVal) in bitsObj)
            {
                if (bitVal is not Dictionary<string, object?> bitObj)
                {
                    continue;
                }

                int bitIndex = TryGetInt(bitObj, "bit")
                               ?? (int.TryParse(bitKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bk) ? bk : -1);
                string? apiName = TryGetStr(bitObj, "name");
                if (bitIndex is < 0 or > 31 || string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var displayObj = FindChildObject(bitObj, "display");
                string displayName = apiName;
                string description = string.Empty;
                bool isHidden = false;
                string? iconHash = null;
                string? iconGrayHash = null;

                if (displayObj is not null)
                {
                    displayName = PickLocalizedString(FindChildObject(displayObj, "name"), preferredLanguage)
                                  ?? TryGetStr(displayObj, "name")
                                  ?? apiName;
                    description = PickLocalizedString(FindChildObject(displayObj, "desc"), preferredLanguage)
                                  ?? TryGetStr(displayObj, "desc")
                                  ?? string.Empty;
                    isHidden = (TryGetInt(displayObj, "hidden") ?? 0) != 0
                               || string.Equals(TryGetStr(displayObj, "hidden"), "1", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(TryGetStr(displayObj, "hidden"), "true", StringComparison.OrdinalIgnoreCase);
                    iconHash = TryGetStr(displayObj, "icon");
                    iconGrayHash = TryGetStr(displayObj, "icon_gray");
                }

                string? iconUrl = !string.IsNullOrWhiteSpace(iconHash) && appId > 0
                    ? BuildSteamCdnIconUrl(appId, iconHash)
                    : null;
                string? iconGrayUrl = !string.IsNullOrWhiteSpace(iconGrayHash) && appId > 0
                    ? BuildSteamCdnIconUrl(appId, iconGrayHash)
                    : null;

                var def = new AchievementDefinitionDto
                {
                    ApiName = apiName.Trim(),
                    DisplayName = displayName.Trim(),
                    Description = description.Trim(),
                    HiddenSpoilerDescription = isHidden && !string.IsNullOrWhiteSpace(description) ? description.Trim() : null,
                    IsHidden = isHidden,
                    IconPath = iconUrl,
                    IconGrayPath = iconGrayUrl,
                    SortOrder = sortOrder++,
                };

                mappings.Add(new SteamBinAchievementBitMapping(def.ApiName, statId, bitIndex, def));
            }
        }

        return mappings;
    }

    /// <summary>
    /// Parses <c>UserGameStats_&lt;AccountId32&gt;_&lt;AppId&gt;.bin</c> bitmasks and <c>AchievementTimes</c>.
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseUserStatsBin(
        ReadOnlySpan<byte> rawStatsBytes,
        IReadOnlyList<SteamBinAchievementBitMapping> bitMappings)
    {
        if (rawStatsBytes.IsEmpty)
        {
            return [];
        }

        var root = ParseRoot(rawStatsBytes);
        var statsNode = FindChildObject(root, "stats");
        if (statsNode is null)
        {
            foreach (var (_, val) in root)
            {
                if (val is Dictionary<string, object?> inner)
                {
                    statsNode = FindChildObject(inner, "stats") ?? inner;
                    break;
                }
            }
        }

        statsNode ??= root;
        var results = new List<ParsedAchievementEntry>();

        if (bitMappings.Count > 0)
        {
            foreach (var mapping in bitMappings)
            {
                string blockKey = mapping.StatBlockId.ToString(CultureInfo.InvariantCulture);
                if (!statsNode.TryGetValue(blockKey, out object? blockVal)
                    || blockVal is not Dictionary<string, object?> blockObj)
                {
                    results.Add(new ParsedAchievementEntry
                    {
                        ApiName = mapping.ApiName,
                        IsUnlocked = false,
                        UnlockedUtc = null,
                    });
                    continue;
                }

                uint mask = unchecked((uint)(TryGetInt(blockObj, "data") ?? 0));
                bool isEarned = ((mask >> mapping.BitIndex) & 1u) == 1u;
                long unlockSec = 0;

                if (isEarned)
                {
                    var timesObj = FindChildObject(blockObj, "AchievementTimes");
                    if (timesObj is not null)
                    {
                        unlockSec = TryGetInt(timesObj, mapping.BitIndex.ToString(CultureInfo.InvariantCulture)) ?? 0;
                    }
                }

                results.Add(new ParsedAchievementEntry
                {
                    ApiName = mapping.ApiName,
                    IsUnlocked = isEarned,
                    UnlockedUtc = isEarned ? SteamEmulatorFormatParsers.UnixTimestampToUtc(unlockSec) : null,
                });
            }

            return results;
        }

        // Direct named-achievement fallback when no bitmapping table is supplied
        var directAchievements = FindChildObject(statsNode, "achievements") ?? statsNode;
        foreach (var (key, val) in directAchievements)
        {
            if (val is Dictionary<string, object?> achObj)
            {
                int? unlockedFlag = TryGetInt(achObj, "achieved") ?? TryGetInt(achObj, "earned") ?? TryGetInt(achObj, "data");
                if (unlockedFlag.HasValue)
                {
                    bool unlocked = unlockedFlag.Value != 0;
                    long ts = TryGetInt(achObj, "unlock_time") ?? TryGetInt(achObj, "timestamp") ?? 0;
                    results.Add(new ParsedAchievementEntry
                    {
                        ApiName = key,
                        IsUnlocked = unlocked,
                        UnlockedUtc = unlocked ? SteamEmulatorFormatParsers.UnixTimestampToUtc(ts) : null,
                    });
                }
            }
        }

        return results;
    }

    private static Dictionary<string, object?> ReadMapUntilEnd(ReadOnlySpan<byte> data, ref int offset, int depth)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (depth > 32)
        {
            return map;
        }

        while (offset < data.Length)
        {
            byte type = data[offset++];
            if (type is TypeEnd or TypeEndAlt)
            {
                break;
            }

            string key = ReadNullTerminatedUtf8(data, ref offset);
            switch (type)
            {
                case TypeObject:
                    map[key] = ReadMapUntilEnd(data, ref offset, depth + 1);
                    break;

                case TypeString:
                    map[key] = ReadNullTerminatedUtf8(data, ref offset);
                    break;

                case TypeInt32:
                case TypePtr:
                case TypeColor:
                    if (offset + 4 > data.Length)
                    {
                        offset = data.Length;
                        break;
                    }

                    map[key] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    break;

                case TypeFloat32:
                    if (offset + 4 > data.Length)
                    {
                        offset = data.Length;
                        break;
                    }

                    map[key] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
                    offset += 4;
                    break;

                case TypeWideString:
                    map[key] = ReadNullTerminatedUtf16(data, ref offset);
                    break;

                case TypeUInt64:
                    if (offset + 8 > data.Length)
                    {
                        offset = data.Length;
                        break;
                    }

                    map[key] = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                    offset += 8;
                    break;

                default:
                    // Unrecognized tag; stop safely rather than mis-aligning stream.
                    offset = data.Length;
                    break;
            }
        }

        return map;
    }

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        string s = offset > start ? Encoding.UTF8.GetString(data.Slice(start, offset - start)) : string.Empty;
        if (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }

        return s;
    }

    private static string ReadNullTerminatedUtf16(ReadOnlySpan<byte> data, ref int offset)
    {
        int start = offset;
        while (offset + 1 < data.Length && (data[offset] != 0 || data[offset + 1] != 0))
        {
            offset += 2;
        }

        string s = offset > start ? Encoding.Unicode.GetString(data.Slice(start, offset - start)) : string.Empty;
        if (offset + 1 < data.Length)
        {
            offset += 2;
        }
        else
        {
            offset = data.Length;
        }

        return s;
    }

    private static Dictionary<string, object?>? FindChildObject(Dictionary<string, object?> parent, string key) =>
        parent.TryGetValue(key, out object? val) && val is Dictionary<string, object?> child ? child : null;

    private static int? TryGetInt(Dictionary<string, object?> obj, string key)
    {
        if (!obj.TryGetValue(key, out object? val) || val is null)
        {
            return null;
        }

        return val switch
        {
            int i => i,
            long l => (int)l,
            ulong ul => (int)ul,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null,
        };
    }

    private static string? TryGetStr(Dictionary<string, object?> obj, string key) =>
        obj.TryGetValue(key, out object? val) ? val?.ToString() : null;

    private static string? PickLocalizedString(Dictionary<string, object?>? langObj, string preferredLanguage)
    {
        if (langObj is null)
        {
            return null;
        }

        string[] order = [preferredLanguage, "schinese", "tchinese", "english", "token"];
        foreach (string lang in order)
        {
            if (langObj.TryGetValue(lang, out object? v) && v is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        foreach (var (_, v) in langObj)
        {
            if (v is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return null;
    }

    private static string BuildSteamCdnIconUrl(int appId, string iconHashOrUrl)
    {
        if (iconHashOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || iconHashOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return iconHashOrUrl;
        }

        return $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{iconHashOrUrl.TrimStart('/')}";
    }
}
