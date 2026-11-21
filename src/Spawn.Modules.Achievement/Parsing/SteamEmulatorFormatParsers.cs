using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// High-compatibility byte/text parsers for 15+ offline Steam emulator and GOG emulator save formats.
/// </summary>
public static partial class SteamEmulatorFormatParsers
{
    private static readonly HashSet<string> IgnoredIniSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "SteamAchievements",
        "Steam",
        "Settings",
        "GameSettings",
        "Stats",
        "ACHIEVEMENTS",
        "State",
        "Time",
        "AchievementsUnlockTimes",
    };

    /// <summary>
    /// Parses Goldberg, <c>gbe_fork</c> (<c>GSE Saves</c>), and EMPRESS <c>achievements.json</c> player save files.
    /// Safely ignores JSON Array files (<c>[...]</c>) which represent static schema definitions rather than player unlocks.
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseGoldbergOrGbeForkJson(ReadOnlySpan<byte> rawUtf8)
    {
        ReadOnlySpan<byte> span = TrimBomAndWhitespace(rawUtf8);
        if (span.IsEmpty || span[0] == (byte)'[')
        {
            // Top-level array is a schema definition file (steam_settings/achievements.json), NOT player unlocks.
            return [];
        }

        using var doc = ParseJsonDocumentTolerant(span);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var results = new List<ParsedAchievementEntry>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string apiName = CleanIdentifier(prop.Name);
            if (string.IsNullOrWhiteSpace(apiName))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                bool earned = ReadFlexibleBool(prop.Value, "earned", "achieved", "unlocked");
                long earnedTime = ReadFlexibleInt64(prop.Value, "earned_time", "unlock_time", "timestamp", "time");
                double? progress = ReadFlexibleDouble(prop.Value, "progress", "cur_progress", "value");
                double? maxProgress = ReadFlexibleDouble(prop.Value, "max_progress", "max");

                results.Add(new ParsedAchievementEntry
                {
                    ApiName = apiName,
                    IsUnlocked = earned,
                    UnlockedUtc = earned ? UnixTimestampToUtc(earnedTime) : null,
                    CurrentProgress = progress,
                    MaxProgress = maxProgress,
                });
            }
            else if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
            {
                bool earned = prop.Value.ValueKind == JsonValueKind.True
                              || (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int v) && v != 0);
                results.Add(new ParsedAchievementEntry
                {
                    ApiName = apiName,
                    IsUnlocked = earned,
                    UnlockedUtc = null,
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Parses <c>steam_settings/achievements.json</c> (JSON Array) into localized <see cref="AchievementDefinitionDto"/> entries.
    /// </summary>
    public static IReadOnlyList<AchievementDefinitionDto> ParseLocalGbeSchemaJson(
        ReadOnlySpan<byte> rawUtf8,
        string? baseSettingsDirectory = null,
        string preferredLanguage = "schinese")
    {
        ReadOnlySpan<byte> span = TrimBomAndWhitespace(rawUtf8);
        if (span.IsEmpty || span[0] != (byte)'[')
        {
            return [];
        }

        using var doc = ParseJsonDocumentTolerant(span);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var defs = new List<AchievementDefinitionDto>();
        int sortOrder = 0;

        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            if (elem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? apiName = TryGetString(elem, "name") ?? TryGetString(elem, "id") ?? TryGetString(elem, "apiname");
            if (string.IsNullOrWhiteSpace(apiName))
            {
                continue;
            }

            string displayName = ExtractLocalizedField(elem, "displayName", preferredLanguage)
                                 ?? TryGetString(elem, "defaultvalue")
                                 ?? apiName;
            string description = ExtractLocalizedField(elem, "description", preferredLanguage) ?? string.Empty;
            bool isHidden = ReadFlexibleBool(elem, "hidden");

            string? iconRel = TryGetString(elem, "icon");
            string? iconGrayRel = TryGetString(elem, "icon_gray") ?? TryGetString(elem, "icongray");

            string? iconFull = ResolveOptionalLocalIcon(baseSettingsDirectory, iconRel);
            string? iconGrayFull = ResolveOptionalLocalIcon(baseSettingsDirectory, iconGrayRel);

            defs.Add(new AchievementDefinitionDto
            {
                ApiName = apiName.Trim(),
                DisplayName = displayName.Trim(),
                Description = description.Trim(),
                HiddenSpoilerDescription = isHidden && !string.IsNullOrWhiteSpace(description) ? description.Trim() : null,
                IsHidden = isHidden,
                IconPath = iconFull,
                IconGrayPath = iconGrayFull,
                SortOrder = sortOrder++,
            });
        }

        return defs;
    }

    /// <summary>
    /// Extracts custom save directory settings from <c>steam_settings/configs.user.ini</c>.
    /// </summary>
    public static (string? SavesFolderName, string? LocalSavePath) ParseGbeConfigsUserIni(string iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return (null, null);
        }

        var sections = ParseLooseIniSections(iniText);
        string? folderName = null;
        string? localSave = null;

        foreach (var (secName, kv) in sections)
        {
            if (secName.Equals("user::saves", StringComparison.OrdinalIgnoreCase) || secName.Length == 0)
            {
                if (kv.TryGetValue("saves_folder_name", out string? sfn) && !string.IsNullOrWhiteSpace(sfn))
                {
                    folderName = Unquote(sfn);
                }

                if (kv.TryGetValue("local_save_path", out string? lsp) && !string.IsNullOrWhiteSpace(lsp))
                {
                    localSave = Unquote(lsp);
                }
            }
        }

        return (folderName, localSave);
    }

    /// <summary>
    /// Parses CODEX, RUNE, and PLAZA <c>achievements.ini</c> files, skipping <c>[SteamAchievements]</c> index headers.
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseCodexRunePlazaIni(string iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return [];
        }

        var sections = ParseLooseIniSections(iniText);
        var results = new List<ParsedAchievementEntry>();

        foreach (var (section, kv) in sections)
        {
            string apiName = CleanIdentifier(section);
            if (string.IsNullOrWhiteSpace(apiName) || IgnoredIniSections.Contains(apiName))
            {
                continue;
            }

            if (!kv.TryGetValue("Achieved", out string? achRaw)
                && !kv.TryGetValue("achieved", out achRaw)
                && !kv.TryGetValue("Unlocked", out achRaw))
            {
                continue;
            }

            bool unlocked = ParseBoolString(achRaw);
            long unlockTime = 0;
            if (kv.TryGetValue("UnlockTime", out string? timeRaw) || kv.TryGetValue("Timestamp", out timeRaw))
            {
                long.TryParse(Unquote(timeRaw), NumberStyles.Integer, CultureInfo.InvariantCulture, out unlockTime);
            }

            double? curProg = TryParseDoubleDict(kv, "CurProgress");
            double? maxProg = TryParseDoubleDict(kv, "MaxProgress");

            results.Add(new ParsedAchievementEntry
            {
                ApiName = apiName,
                IsUnlocked = unlocked,
                UnlockedUtc = unlocked ? UnixTimestampToUtc(unlockTime) : null,
                CurrentProgress = curProg,
                MaxProgress = maxProg,
            });
        }

        return results;
    }

    /// <summary>
    /// Parses TENOKE <c>SteamData/user_stats.ini</c>, supporting both TOML inline tables
    /// (<c>"ACH_NAME" = {unlocked = true, time = 1712345678}</c>) and legacy <c>ACH_NAME = true/1</c>.
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseTenokeIni(string iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return [];
        }

        var results = new List<ParsedAchievementEntry>();
        string currentSection = string.Empty;

        foreach (string rawLine in iniText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = StripIniComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (currentSection.Length > 0
                && !currentSection.Equals("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int eq = FindTopLevelEquals(line);
            if (eq <= 0)
            {
                continue;
            }

            string apiName = CleanIdentifier(line[..eq]);
            string rhs = line[(eq + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(apiName))
            {
                continue;
            }

            if (rhs.StartsWith('{') && rhs.EndsWith('}'))
            {
                string body = rhs[1..^1];
                bool unlocked = false;
                long time = 0;
                double? progress = null;

                foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    int peq = part.IndexOf('=');
                    if (peq <= 0)
                    {
                        continue;
                    }

                    string k = CleanIdentifier(part[..peq]);
                    string v = Unquote(part[(peq + 1)..].Trim());
                    if (k.Equals("unlocked", StringComparison.OrdinalIgnoreCase)
                        || k.Equals("achieved", StringComparison.OrdinalIgnoreCase)
                        || k.Equals("earned", StringComparison.OrdinalIgnoreCase))
                    {
                        unlocked = ParseBoolString(v);
                    }
                    else if (k.Equals("time", StringComparison.OrdinalIgnoreCase)
                             || k.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
                             || k.Equals("unlock_time", StringComparison.OrdinalIgnoreCase))
                    {
                        long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out time);
                    }
                    else if (k.Equals("progress", StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        {
                            progress = d;
                        }
                    }
                }

                results.Add(new ParsedAchievementEntry
                {
                    ApiName = apiName,
                    IsUnlocked = unlocked,
                    UnlockedUtc = unlocked ? UnixTimestampToUtc(time) : null,
                    CurrentProgress = progress,
                });
            }
            else
            {
                bool unlocked = ParseBoolString(Unquote(rhs));
                results.Add(new ParsedAchievementEntry
                {
                    ApiName = apiName,
                    IsUnlocked = unlocked,
                    UnlockedUtc = null,
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Parses Online-Fix (<c>Stats/Achievements.ini</c>) with lowercase <c>achieved=true</c> and <c>timestamp=...</c>.
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseOnlineFixIni(string iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return [];
        }

        var sections = ParseLooseIniSections(iniText);
        var results = new List<ParsedAchievementEntry>();

        foreach (var (section, kv) in sections)
        {
            string apiName = CleanIdentifier(section);
            if (string.IsNullOrWhiteSpace(apiName) || IgnoredIniSections.Contains(apiName))
            {
                continue;
            }

            if (!kv.TryGetValue("achieved", out string? achRaw)
                && !kv.TryGetValue("unlocked", out achRaw))
            {
                continue;
            }

            bool unlocked = ParseBoolString(achRaw);
            long ts = 0;
            if (kv.TryGetValue("timestamp", out string? tsRaw) || kv.TryGetValue("unlocktime", out tsRaw))
            {
                long.TryParse(Unquote(tsRaw), NumberStyles.Integer, CultureInfo.InvariantCulture, out ts);
            }

            results.Add(new ParsedAchievementEntry
            {
                ApiName = apiName,
                IsUnlocked = unlocked,
                UnlockedUtc = unlocked ? UnixTimestampToUtc(ts) : null,
            });
        }

        return results;
    }

    /// <summary>
    /// Parses ALI213 disguised <c>Achievements.Bin</c> (UTF-8/ASCII INI with optional BOM, <c>HaveAchieved=1</c> or flat <c>ACH=1</c>).
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseAli213BinIni(ReadOnlySpan<byte> rawBytes)
    {
        ReadOnlySpan<byte> span = TrimBomAndWhitespace(rawBytes);
        if (span.IsEmpty)
        {
            return [];
        }

        string iniText = Encoding.UTF8.GetString(span);
        var sections = ParseLooseIniSections(iniText);
        var results = new List<ParsedAchievementEntry>();

        foreach (var (section, kv) in sections)
        {
            if (section.Equals("Steam", StringComparison.OrdinalIgnoreCase)
                || section.Equals("Settings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (section.Length == 0 || section.Equals("Achievements", StringComparison.OrdinalIgnoreCase))
            {
                // Flat historical variant: ACH_ID=1
                foreach (var (k, v) in kv)
                {
                    string apiName = CleanIdentifier(k);
                    if (string.IsNullOrWhiteSpace(apiName) || apiName.Equals("Count", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool unlocked = ParseBoolString(v);
                    results.Add(new ParsedAchievementEntry
                    {
                        ApiName = apiName,
                        IsUnlocked = unlocked,
                        UnlockedUtc = null,
                    });
                }

                continue;
            }

            string secApi = CleanIdentifier(section);
            if (string.IsNullOrWhiteSpace(secApi))
            {
                continue;
            }

            if (kv.TryGetValue("HaveAchieved", out string? haRaw) || kv.TryGetValue("Achieved", out haRaw))
            {
                bool unlocked = ParseBoolString(haRaw);
                long ts = 0;
                if (kv.TryGetValue("HaveAchievedTime", out string? timeRaw) || kv.TryGetValue("UnlockTime", out timeRaw))
                {
                    long.TryParse(Unquote(timeRaw), NumberStyles.Integer, CultureInfo.InvariantCulture, out ts);
                }

                results.Add(new ParsedAchievementEntry
                {
                    ApiName = secApi,
                    IsUnlocked = unlocked,
                    UnlockedUtc = unlocked ? UnixTimestampToUtc(ts) : null,
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Parses 3DM and Reloaded (<c>RLD!</c>) <c>achievements.ini</c> files, automatically decoding
    /// 8-character Little-Endian hexadecimal <see cref="uint"/> Unix timestamps (e.g. <c>80513b65</c> -&gt; <c>0x653b5180</c>).
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseThreeDmAndReloadedIni(string iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return [];
        }

        var sections = ParseLooseIniSections(iniText);
        var byApi = new Dictionary<string, ParsedAchievementEntry>(StringComparer.OrdinalIgnoreCase);

        // Variant A: [State] + [Time] split sections (3DM & RLD!)
        sections.TryGetValue("State", out var stateSec);
        sections.TryGetValue("Time", out var timeSec);

        if (stateSec is not null && stateSec.Count > 0)
        {
            foreach (var (rawKey, rawState) in stateSec)
            {
                string apiName = CleanIdentifier(rawKey);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                bool unlocked = ParseHexOrBoolState(rawState);
                long ts = 0;
                if (timeSec is not null && timeSec.TryGetValue(rawKey, out string? rawTime))
                {
                    ts = ParseThreeDmTimestamp(rawTime);
                }

                byApi[apiName] = new ParsedAchievementEntry
                {
                    ApiName = apiName,
                    IsUnlocked = unlocked,
                    UnlockedUtc = unlocked ? UnixTimestampToUtc(ts) : null,
                };
            }
        }

        // Variant B: Per-achievement [<ApiName>] section with State=01000000 / Time=<LE Hex>
        foreach (var (section, kv) in sections)
        {
            string apiName = CleanIdentifier(section);
            if (string.IsNullOrWhiteSpace(apiName) || IgnoredIniSections.Contains(apiName))
            {
                continue;
            }

            if (!kv.TryGetValue("State", out string? stateVal) && !kv.TryGetValue("Achieved", out stateVal))
            {
                continue;
            }

            bool unlocked = ParseHexOrBoolState(stateVal);
            long ts = 0;
            if (kv.TryGetValue("Time", out string? timeVal) || kv.TryGetValue("UnlockTime", out timeVal))
            {
                ts = ParseThreeDmTimestamp(timeVal);
            }

            double? curProg = null;
            if (kv.TryGetValue("CurProgress", out string? cpRaw))
            {
                uint cpU32 = (uint)ParseThreeDmTimestamp(cpRaw);
                curProg = cpU32;
            }

            byApi[apiName] = new ParsedAchievementEntry
            {
                ApiName = apiName,
                IsUnlocked = unlocked,
                UnlockedUtc = unlocked ? UnixTimestampToUtc(ts) : null,
                CurrentProgress = curProg,
            };
        }

        return [.. byApi.Values];
    }

    /// <summary>
    /// Parses SKIDROW, Hoodlum, and DARKSiDERS INI/JSON files (<c>[Achievements]</c> + <c>[AchievementsUnlockTimes]</c> or per-section INI).
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseSkidrowHoodlum(ReadOnlySpan<byte> rawBytes)
    {
        ReadOnlySpan<byte> span = TrimBomAndWhitespace(rawBytes);
        if (span.IsEmpty)
        {
            return [];
        }

        if (span[0] == (byte)'{')
        {
            return ParseGoldbergOrGbeForkJson(span);
        }

        string iniText = Encoding.UTF8.GetString(span);
        var sections = ParseLooseIniSections(iniText);
        var byApi = new Dictionary<string, ParsedAchievementEntry>(StringComparer.OrdinalIgnoreCase);

        if (sections.TryGetValue("Achievements", out var achSec))
        {
            sections.TryGetValue("AchievementsUnlockTimes", out var timeSec);
            foreach (var (rawKey, rawVal) in achSec)
            {
                string apiName = CleanIdentifier(rawKey);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                bool unlocked = ParseBoolString(rawVal);
                long ts = 0;
                if (timeSec is not null && timeSec.TryGetValue(rawKey, out string? tRaw))
                {
                    long.TryParse(Unquote(tRaw), NumberStyles.Integer, CultureInfo.InvariantCulture, out ts);
                }

                byApi[apiName] = new ParsedAchievementEntry
                {
                    ApiName = apiName,
                    IsUnlocked = unlocked,
                    UnlockedUtc = unlocked ? UnixTimestampToUtc(ts) : null,
                };
            }
        }

        foreach (var entry in ParseCodexRunePlazaIni(iniText))
        {
            byApi.TryAdd(entry.ApiName, entry);
        }

        return [.. byApi.Values];
    }

    /// <summary>
    /// Parses CreamAPI local <c>CreamAPI.Achievements.cfg</c> (fixing historical 7-digit truncated timestamps).
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseCreamApiCfg(string iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return [];
        }

        var sections = ParseLooseIniSections(iniText);
        var results = new List<ParsedAchievementEntry>();

        foreach (var (section, kv) in sections)
        {
            string apiName = CleanIdentifier(section);
            if (string.IsNullOrWhiteSpace(apiName) || IgnoredIniSections.Contains(apiName))
            {
                continue;
            }

            if (!kv.TryGetValue("achieved", out string? achRaw))
            {
                continue;
            }

            bool unlocked = ParseBoolString(achRaw);
            long ts = 0;
            if (kv.TryGetValue("unlocktime", out string? tsRaw))
            {
                if (long.TryParse(Unquote(tsRaw), NumberStyles.Integer, CultureInfo.InvariantCulture, out ts)
                    && ts is >= 1_000_000 and <= 9_999_999)
                {
                    ts *= 1000;
                }
            }

            results.Add(new ParsedAchievementEntry
            {
                ApiName = apiName,
                IsUnlocked = unlocked,
                UnlockedUtc = unlocked ? UnixTimestampToUtc(ts) : null,
            });
        }

        return results;
    }

    /// <summary>
    /// Parses SmartSteamEmu (<c>stats.bin</c>) compact binary records:
    /// 4-byte LE <c>EntryCount</c> header + <c>N × 24-byte</c> fixed records (<c>+0x00 Crc32</c>, <c>+0x08 UnlockTime</c>, <c>+0x14 Value</c>).
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseSmartSteamEmuStatsBin(
        ReadOnlySpan<byte> rawBytes,
        IReadOnlyDictionary<uint, string>? crc32ToApiName)
    {
        if (rawBytes.Length < 4)
        {
            return [];
        }

        int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(rawBytes[..4]);
        int maxPossibleCount = (rawBytes.Length - 4) / 24;
        if (declaredCount <= 0 || maxPossibleCount <= 0)
        {
            return [];
        }

        int count = Math.Min(declaredCount, maxPossibleCount);
        var results = new List<ParsedAchievementEntry>(count);

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> rec = rawBytes.Slice(4 + (i * 24), 24);
            uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(rec[..4]);
            long unlockTime64 = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(8, 8));
            uint unlockTime32 = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(8, 4));
            int value = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(20, 4));

            // In SSE stats.bin: 0 = locked achievement, 1 = unlocked achievement, > 1 = cumulative stat counter.
            if (value is not (0 or 1))
            {
                continue;
            }

            string? apiName = null;
            if (crc32ToApiName is not null && crc32ToApiName.TryGetValue(crc32, out string? mapped))
            {
                apiName = mapped;
            }

            if (string.IsNullOrWhiteSpace(apiName))
            {
                continue;
            }

            long effectiveSec = unlockTime64 is > 0 and < 4_102_444_800L
                ? unlockTime64
                : unlockTime32;

            bool isUnlocked = value == 1;
            results.Add(new ParsedAchievementEntry
            {
                ApiName = apiName,
                IsUnlocked = isUnlocked,
                UnlockedUtc = isUnlocked ? UnixTimestampToUtc(effectiveSec) : null,
            });
        }

        return results;
    }

    /// <summary>
    /// Parses NemirtingasGalaxyEmu JSON files (<c>achievements.json</c>).
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> ParseNemirtingasGalaxyJson(ReadOnlySpan<byte> rawUtf8)
    {
        ReadOnlySpan<byte> span = TrimBomAndWhitespace(rawUtf8);
        if (span.IsEmpty)
        {
            return [];
        }

        using var doc = ParseJsonDocumentTolerant(span);
        if (doc is null)
        {
            return [];
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (doc.RootElement.TryGetProperty("achievements", out var inner))
            {
                if (inner.ValueKind == JsonValueKind.Object)
                {
                    return ParseGoldbergOrGbeForkJson(Encoding.UTF8.GetBytes(inner.GetRawText()));
                }

                if (inner.ValueKind == JsonValueKind.Array)
                {
                    return ParseJsonAchievementArray(inner);
                }
            }

            return ParseGoldbergOrGbeForkJson(span);
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return ParseJsonAchievementArray(doc.RootElement);
        }

        return [];
    }

    /// <summary>
    /// Decodes a 3DM / Reloaded timestamp string (supports 8-char Little-Endian hex <see cref="uint"/> and standard decimal).
    /// </summary>
    public static long ParseThreeDmTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        string s = Unquote(raw.Trim());
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        // 8-char Little-Endian hex UInt32 (e.g., "80513b65" -> bytes [0x80, 0x51, 0x3b, 0x65] -> LE uint32 = 0x653b5180)
        if (s.Length == 8 && IsAllHexChars(s))
        {
            bool hasAlphaHex = false;
            foreach (char ch in s)
            {
                if (ch is (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))
                {
                    hasAlphaHex = true;
                    break;
                }
            }

            byte[] bytes = Convert.FromHexString(s);
            uint leCandidate = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (hasAlphaHex)
            {
                return leCandidate;
            }

            // Pure digits of length 8: check whether LE hex gives a realistic 2005..2099 Unix timestamp vs decimal.
            if (leCandidate is >= 1_104_537_600u and <= 4_102_444_800u)
            {
                return leCandidate;
            }
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dec))
        {
            return dec;
        }

        if (long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long beHex))
        {
            return beHex;
        }

        return 0;
    }

    private static readonly HashSet<string> SkippedScanDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_OST",
        "StreamingAssets",
        "usd",
        "locales",
        "MonoBleedingEdge",
        "il2cpp_data",
        "ShaderCache",
        ".git",
    };

    /// <summary>
    /// Detects nested scene emulator markers (<c>steam_api64.cdx</c> -&gt; CODEX, <c>steam_api64.rne</c> -&gt; RUNE, <c>steam_emu.ini</c>)
    /// inside Unity <c>*_Data/Plugins/x86_64</c>, <c>x86</c>, and Unreal <c>Engine/Binaries/ThirdParty/Steamworks</c> subdirectories.
    /// </summary>
    public static (EmulatorFlavor Flavor, int? AppId, bool LocalSave, string? EmulatorDir) DetectSceneSteamEmulatorInfo(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return (EmulatorFlavor.Unknown, null, false, null);
        }

        EmulatorFlavor detectedFlavor = EmulatorFlavor.Unknown;
        string? emuDir = null;

        string? cdxFile = EnumerateFilesSafe(installDir, "*.cdx", maxDepth: 6).FirstOrDefault();
        if (cdxFile is not null)
        {
            detectedFlavor = EmulatorFlavor.Codex;
            emuDir = Path.GetDirectoryName(cdxFile);
        }

        if (detectedFlavor == EmulatorFlavor.Unknown)
        {
            string? rneFile = EnumerateFilesSafe(installDir, "*.rne", maxDepth: 6).FirstOrDefault();
            if (rneFile is not null)
            {
                detectedFlavor = EmulatorFlavor.Rune;
                emuDir = Path.GetDirectoryName(rneFile);
            }
        }

        int? appId = null;
        bool localSave = false;

        foreach (string iniFile in EnumerateFilesSafe(installDir, "steam_emu.ini", maxDepth: 6))
        {
            emuDir ??= Path.GetDirectoryName(iniFile);
            try
            {
                string rawText = File.ReadAllText(iniFile);
                if (detectedFlavor == EmulatorFlavor.Unknown)
                {
                    if (rawText.Contains(@"Steam\CODEX", StringComparison.OrdinalIgnoreCase)
                        || rawText.Contains("CODEX", StringComparison.OrdinalIgnoreCase))
                    {
                        detectedFlavor = EmulatorFlavor.Codex;
                    }
                    else if (rawText.Contains(@"Steam\RUNE", StringComparison.OrdinalIgnoreCase)
                             || rawText.Contains("=-RUNE-", StringComparison.OrdinalIgnoreCase)
                             || rawText.Contains("RUNE", StringComparison.OrdinalIgnoreCase))
                    {
                        detectedFlavor = EmulatorFlavor.Rune;
                    }
                    else if (rawText.Contains(@"Steam\PLAZA", StringComparison.OrdinalIgnoreCase))
                    {
                        detectedFlavor = EmulatorFlavor.Plaza;
                    }
                    else
                    {
                        detectedFlavor = EmulatorFlavor.Rune;
                    }
                }

                var sections = ParseLooseIniSections(rawText);
                foreach (var (_, kv) in sections)
                {
                    if (appId is null
                        && (kv.TryGetValue("AppId", out string? aidRaw) || kv.TryGetValue("AppID", out aidRaw))
                        && int.TryParse(Unquote(aidRaw), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedAid)
                        && parsedAid > 0)
                    {
                        appId = parsedAid;
                    }

                    if (kv.TryGetValue("LocalSave", out string? lsRaw) && ParseBoolString(lsRaw))
                    {
                        localSave = true;
                    }
                }
            }
            catch
            {
                // Ignore inaccessible INI.
            }
        }

        return (detectedFlavor, appId, localSave, emuDir);
    }

    /// <summary>
    /// Resolves Steam AppID from explicit <c>steam_appid.txt</c> or emulator configuration INIs inside a game directory.
    /// </summary>
    public static int? TryDetectSteamAppIdFromDirectory(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        // 1. Direct steam_appid.txt files (root & steam_settings & subdirs up to depth 6)
        foreach (string file in EnumerateFilesSafe(installDir, "steam_appid.txt", maxDepth: 6))
        {
            try
            {
                string txt = File.ReadAllText(file).Trim();
                if (int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int appId) && appId > 0 && appId != 480)
                {
                    return appId;
                }
            }
            catch
            {
                // Ignore inaccessible file.
            }
        }

        // 2. Emulator INI files (OnlineFix.ini RealAppId first to avoid 480 Spacewar!)
        string[] iniNames =
        [
            "OnlineFix.ini",
            "tenoke.ini",
            "steam_emu.ini",
            "codex.ini",
            "ColdClientLoader.ini",
            "ALI213.ini",
            "valve.ini",
            "SteamConfig.ini",
            "hlm.ini",
            "ds.ini",
            "steam_api.ini",
            "SmartSteamEmu.ini",
            "3DMGAME.ini",
            "cream_api.ini",
        ];

        int? fallback480 = null;
        foreach (string iniName in iniNames)
        {
            foreach (string file in EnumerateFilesSafe(installDir, iniName, maxDepth: 6))
            {
                try
                {
                    string content = File.ReadAllText(file);
                    var sections = ParseLooseIniSections(content);
                    foreach (var (_, kv) in sections)
                    {
                        string[] keys = ["RealAppId", "AppId", "AppID", "appid", "id", "GameId"];
                        foreach (string k in keys)
                        {
                            if (kv.TryGetValue(k, out string? raw)
                                && int.TryParse(Unquote(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                                && parsed > 0)
                            {
                                if (parsed == 480)
                                {
                                    fallback480 = 480;
                                }
                                else
                                {
                                    return parsed;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore malformed INI.
                }
            }
        }

        return fallback480;
    }

    internal static Dictionary<string, Dictionary<string, string>> ParseLooseIniSections(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string currentSection = string.Empty;

        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = StripIniComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!result.ContainsKey(currentSection))
                {
                    result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            if (!result.TryGetValue(currentSection, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[currentSection] = dict;
            }

            dict[key] = val;
        }

        return result;
    }

    internal static IEnumerable<string> EnumerateFilesSafe(string rootDir, string searchPattern, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            yield break;
        }

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootDir, 0));

        while (queue.Count > 0)
        {
            var (curDir, depth) = queue.Dequeue();
            string[] files;
            try
            {
                files = Directory.GetFiles(curDir, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (string f in files)
            {
                yield return f;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(curDir);
            }
            catch
            {
                continue;
            }

            foreach (string sub in subDirs)
            {
                string subName = Path.GetFileName(sub);
                if (SkippedScanDirectoryNames.Contains(subName))
                {
                    continue;
                }

                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    internal static DateTimeOffset? UnixTimestampToUtc(long rawTimestamp)
    {
        if (rawTimestamp <= 0)
        {
            return null;
        }

        try
        {
            // Milliseconds vs Seconds auto-normalization
            if (rawTimestamp > 10_000_000_000L)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(rawTimestamp);
            }

            return DateTimeOffset.FromUnixTimeSeconds(rawTimestamp);
        }
        catch
        {
            return null;
        }
    }

    internal static bool ParseBoolString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string s = Unquote(raw.Trim());
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || s.Equals("unlocked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
        {
            return n != 0;
        }

        return false;
    }

    private static bool ParseHexOrBoolState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string s = Unquote(raw.Trim());
        if (ParseBoolString(s))
        {
            return true;
        }

        if (IsAllHexChars(s))
        {
            foreach (char ch in s)
            {
                if (ch != '0')
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static List<ParsedAchievementEntry> ParseJsonAchievementArray(JsonElement arrayElem)
    {
        var list = new List<ParsedAchievementEntry>();
        foreach (var item in arrayElem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? key = TryGetString(item, "key")
                          ?? TryGetString(item, "achievement_key")
                          ?? TryGetString(item, "name")
                          ?? TryGetString(item, "id");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            bool unlocked = ReadFlexibleBool(item, "earned", "unlocked", "achieved");
            long time = ReadFlexibleInt64(item, "earned_time", "unlock_time", "timestamp", "date_unlocked");
            DateTimeOffset? dto = unlocked ? UnixTimestampToUtc(time) : null;
            if (unlocked && dto is null)
            {
                string? iso = TryGetString(item, "date_unlocked") ?? TryGetString(item, "unlock_time");
                if (!string.IsNullOrWhiteSpace(iso) && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDto))
                {
                    dto = parsedDto.ToUniversalTime();
                }
            }

            list.Add(new ParsedAchievementEntry
            {
                ApiName = CleanIdentifier(key),
                IsUnlocked = unlocked,
                UnlockedUtc = dto,
            });
        }

        return list;
    }

    private static JsonDocument? ParseJsonDocumentTolerant(ReadOnlySpan<byte> utf8)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        try
        {
            return JsonDocument.Parse(utf8.ToArray(), options);
        }
        catch (JsonException)
        {
            // EMPRESS quirk fallback: sanitize single-quoted JSON keys like `'ACH_01':`
            try
            {
                string text = Encoding.UTF8.GetString(utf8);
                string fixedText = SingleQuotedJsonKeyRegex().Replace(text, "\"$1\":");
                return JsonDocument.Parse(fixedText, options);
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? ExtractLocalizedField(JsonElement obj, string propName, string preferredLanguage)
    {
        if (!obj.TryGetProperty(propName, out var val))
        {
            return null;
        }

        if (val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }

        if (val.ValueKind == JsonValueKind.Object)
        {
            string[] candidates = [preferredLanguage, "schinese", "SC", "zh-CN", "tchinese", "english", "en"];
            foreach (string lang in candidates)
            {
                string? hit = TryGetStringCaseInsensitive(val, lang);
                if (!string.IsNullOrWhiteSpace(hit))
                {
                    return hit;
                }
            }

            foreach (var p in val.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                {
                    return p.Value.GetString();
                }
            }
        }

        return null;
    }

    private static string? ResolveOptionalLocalIcon(string? baseDir, string? relPath)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(relPath))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadFlexibleBool(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (string key in keys)
        {
            if (TryGetPropertyCaseInsensitive(obj, key, out var prop))
            {
                return prop.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.TryGetInt64(out long v) && v != 0,
                    JsonValueKind.String => ParseBoolString(prop.GetString()),
                    _ => false,
                };
            }
        }

        return false;
    }

    private static long ReadFlexibleInt64(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (string key in keys)
        {
            if (TryGetPropertyCaseInsensitive(obj, key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long n))
                {
                    return n;
                }

                if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sn))
                {
                    return sn;
                }
            }
        }

        return 0;
    }

    private static double? ReadFlexibleDouble(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (string key in keys)
        {
            if (TryGetPropertyCaseInsensitive(obj, key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double d))
                {
                    return d;
                }

                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd))
                {
                    return sd;
                }
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement obj, string key) =>
        TryGetPropertyCaseInsensitive(obj, key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? TryGetStringCaseInsensitive(JsonElement obj, string key) => TryGetString(obj, key);

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(key, out value))
            {
                return true;
            }

            foreach (var p in obj.EnumerateObject())
            {
                if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static double? TryParseDoubleDict(Dictionary<string, string> dict, string key) =>
        dict.TryGetValue(key, out string? raw)
        && double.TryParse(Unquote(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
            ? val
            : null;

    private static int FindTopLevelEquals(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == '=' && !inQuotes)
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripIniComment(string line)
    {
        bool inQuotes = false;
        int braceDepth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '{')
                {
                    braceDepth++;
                }
                else if (c == '}' && braceDepth > 0)
                {
                    braceDepth--;
                }
                else if ((c == ';' || c == '#') && braceDepth == 0)
                {
                    return line[..i];
                }
            }
        }

        return line;
    }

    private static string CleanIdentifier(string raw) =>
        Unquote(raw.Trim()).Trim('\'', '"', ' ', '\t');

    private static string Unquote(string raw)
    {
        string s = raw.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            return s[1..^1].Trim();
        }

        return s;
    }

    private static bool IsAllHexChars(string s)
    {
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    private static ReadOnlySpan<byte> TrimBomAndWhitespace(ReadOnlySpan<byte> span)
    {
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        int start = 0;
        while (start < span.Length && span[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        return span[start..];
    }

    [GeneratedRegex(@"'([^']+)'\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedJsonKeyRegex();
}
