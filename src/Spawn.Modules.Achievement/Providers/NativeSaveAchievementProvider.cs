using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Declarative extraction mechanisms supported by <see cref="NativeSaveAchievementProvider"/> for pure DRM-Free games without <c>Galaxy.dll</c>.
/// </summary>
public enum NativeSaveExtractorKind
{
    BitmaskFromRegexInt = 0,
    RegexKeyValue = 1,
    JsonPath = 2,
    BinaryBitOffset = 3,
    JsonStringArray = 4,
}

/// <summary>
/// Special folder anchor for declarative native save path templates (never hardcodes machine-specific paths).
/// </summary>
public enum NativeSaveRootKind
{
    GameInstallDir = 0,
    AppDataRoaming = 1,
    LocalAppData = 2,
    UserDocuments = 3,
    PublicDocuments = 4,
    AppDataLocalLow = 5,
}

public sealed record NativeSavePathTemplate(NativeSaveRootKind Root, string RelativePath);

public sealed record NativeSaveSingleMapping
{
    public required string ApiName { get; init; }
    public string? RegexPattern { get; init; }
    public string? ExpectedValue { get; init; }
    public double? MinNumericThreshold { get; init; }
    public string? JsonPathExpression { get; init; }
    public int ByteOffset { get; init; }
    public int BitOffset { get; init; }
}

/// <summary>
/// Declarative native save achievement rule mapping a DRM-Free game's raw save file to canonical Steam/GOG achievements.
/// </summary>
public sealed record NativeSaveAchievementRule
{
    public required string RuleId { get; init; }
    public required string TitleHint { get; init; }
    public string? GogGameId { get; init; }
    public int? MappedSteamAppId { get; init; }
    public string? SignatureFileName { get; init; }
    public bool AllowAlongsideSteamEmulator { get; init; }
    public required NativeSaveExtractorKind ExtractorKind { get; init; }
    public required IReadOnlyList<NativeSavePathTemplate> SavePathTemplates { get; init; }
    public bool UseLimboAesOrPlainDecoder { get; init; }
    public string? BitmaskRegexPattern { get; init; }
    public string? JsonArrayPropertyPath { get; init; }
    public IReadOnlyDictionary<int, string> BitIndexToApiName { get; init; } = new Dictionary<int, string>();
    public IReadOnlyList<NativeSaveSingleMapping> Mappings { get; init; } = [];
    public IReadOnlyList<AchievementDefinitionDto> FallbackChineseSchema { get; init; } = [];
}

/// <summary>
/// Pure DRM-Free native save achievement rule engine (<c>NativeSaveAchievementProvider</c>).
/// Ships out-of-the-box with exact reverse-engineered rules for GOG <c>LIMBO</c> (<c>1724299977</c> &lt;-&gt; Steam <c>AppID 48000</c>),
/// supporting Regex Key-Value, Bitmask, JSONPath, and Binary Bit Offset extraction.
/// </summary>
public sealed class NativeSaveAchievementProvider : IAchievementSourceProvider
{
    public const string Id = "drmfree.native_save";
    public const string LimboGogGameId = "1724299977";
    public const int LimboSteamAppId = 48000;
    public const int VampireSurvivorsSteamAppId = 1794680;

    private readonly ConcurrentDictionary<string, NativeSaveAchievementRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderId => Id;
    public int Priority => 15;

    public NativeSaveAchievementProvider()
    {
        RegisterRule(CreateBuiltInLimboRule());
        RegisterRule(CreateBuiltInVampireSurvivorsRule());
    }

    public IReadOnlyCollection<NativeSaveAchievementRule> RegisteredRules => [.. _rules.Values];

    public void RegisterRule(NativeSaveAchievementRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules[rule.RuleId] = rule;
    }

    public NativeSaveAchievementRule? FindMatchingRule(GameRecord game, int? effectiveSteamAppId = null, string? effectiveGogGameId = null)
    {
        var matches = FindMatchingRules(game, effectiveSteamAppId, effectiveGogGameId);
        return matches.Count > 0 ? matches[0] : null;
    }

    public IReadOnlyList<NativeSaveAchievementRule> FindMatchingRules(
        GameRecord game,
        int? effectiveSteamAppId = null,
        string? effectiveGogGameId = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        string? gogId = !string.IsNullOrWhiteSpace(effectiveGogGameId) ? effectiveGogGameId : game.GogGameId;
        if (string.IsNullOrWhiteSpace(gogId))
        {
            (gogId, _) = GogAchievementProvider.TryParseGogGameInfo(game.InstallDir);
        }

        int? steamId = effectiveSteamAppId ?? game.SteamAppId;
        var matched = new List<NativeSaveAchievementRule>();

        foreach (var rule in _rules.Values)
        {
            if (!string.IsNullOrWhiteSpace(rule.GogGameId)
                && string.Equals(rule.GogGameId, gogId, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(rule);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.SignatureFileName)
                && !string.IsNullOrWhiteSpace(game.InstallDir)
                && Directory.Exists(game.InstallDir)
                && File.Exists(Path.Combine(game.InstallDir, rule.SignatureFileName)))
            {
                matched.Add(rule);
                continue;
            }

            if (rule.MappedSteamAppId.HasValue && steamId == rule.MappedSteamAppId.Value)
            {
                if (rule.AllowAlongsideSteamEmulator
                    || (!GogAchievementProvider.HasGalaxySdkDll(game.InstallDir) && !HasSteamApiDll(game.InstallDir)))
                {
                    matched.Add(rule);
                }
            }
        }

        return matched;
    }

    public IReadOnlyList<AchievementWatchTarget> DiscoverTargets(AchievementSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rules = FindMatchingRules(context.Game, context.EffectiveSteamAppId, context.EffectiveGogGameId);
        if (rules.Count == 0)
        {
            return [];
        }

        var targets = new List<AchievementWatchTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            string label = rule.AllowAlongsideSteamEmulator
                ? "游戏存档成就同步 · 就绪"
                : "GOG 原生存档监听 · 就绪";
            var mode = rule.AllowAlongsideSteamEmulator
                ? AchievementCaptureMode.SteamEmulator
                : AchievementCaptureMode.GogNativeSave;

            foreach (var tpl in rule.SavePathTemplates)
            {
                string? root = ResolveRoot(tpl.Root, context);
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                // In isolated unit tests where EnvironmentOverrides is null, avoid watching real %APPDATA% if Game.InstallDir is in Temp,
                // UNLESS EnvironmentOverrides explicitly redirected AppDataRoaming/LocalAppData!
                if (tpl.Root != NativeSaveRootKind.GameInstallDir && context.EnvironmentOverrides is null && IsUnderTempDirectory(context.Game.InstallDir))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(Path.Combine(root, tpl.RelativePath));
                if (!seen.Add(fullPath))
                {
                    continue;
                }

                targets.Add(new AchievementWatchTarget
                {
                    ProviderId = ProviderId,
                    Flavor = EmulatorFlavor.NativeSaveRule,
                    CaptureMode = mode,
                    ChannelLabel = label,
                    FilePath = fullPath,
                    RuleId = rule.RuleId,
                    IsPrimary = targets.Count == 0,
                });
            }
        }

        return targets;
    }

    public async ValueTask<IReadOnlyList<ParsedAchievementEntry>> ReadUnlocksAsync(
        AchievementWatchTarget target,
        AchievementSourceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(target.FilePath))
        {
            return [];
        }

        NativeSaveAchievementRule? rule = null;
        if (!string.IsNullOrWhiteSpace(target.RuleId))
        {
            _rules.TryGetValue(target.RuleId, out rule);
        }

        rule ??= FindMatchingRule(context.Game, context.EffectiveSteamAppId, context.EffectiveGogGameId);
        if (rule is null)
        {
            return [];
        }

        byte[]? rawBytes = await ResilientFileReader.ReadSharedBytesWithRetryAsync(target.FilePath, cancellationToken).ConfigureAwait(false);
        if (rawBytes is null || rawBytes.Length == 0)
        {
            return [];
        }

        var stamp = ResilientFileReader.GetMetadataStamp(target.FilePath);
        DateTimeOffset fallbackTime = stamp.Exists
            ? new DateTimeOffset(stamp.LastWriteTicksUtc, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;

        return EvaluateRule(rule, rawBytes, fallbackTime);
    }

    /// <summary>
    /// Evaluates a <see cref="NativeSaveAchievementRule"/> against raw save file bytes.
    /// Exposed publicly for deterministic unit testing across all 4 extraction engines.
    /// </summary>
    public static IReadOnlyList<ParsedAchievementEntry> EvaluateRule(
        NativeSaveAchievementRule rule,
        ReadOnlySpan<byte> rawBytes,
        DateTimeOffset? fileWriteTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rawBytes.IsEmpty)
        {
            return [];
        }

        DateTimeOffset effectiveUtc = fileWriteTimeUtc ?? DateTimeOffset.UtcNow;

        return rule.ExtractorKind switch
        {
            NativeSaveExtractorKind.BitmaskFromRegexInt => EvaluateBitmaskRule(rule, rawBytes, effectiveUtc),
            NativeSaveExtractorKind.RegexKeyValue => EvaluateRegexKeyValueRule(rule, rawBytes, effectiveUtc),
            NativeSaveExtractorKind.JsonPath => EvaluateJsonPathRule(rule, rawBytes, effectiveUtc),
            NativeSaveExtractorKind.BinaryBitOffset => EvaluateBinaryBitOffsetRule(rule, rawBytes, effectiveUtc),
            NativeSaveExtractorKind.JsonStringArray => EvaluateJsonStringArrayRule(rule, rawBytes, effectiveUtc),
            _ => [],
        };
    }

    public static NativeSaveAchievementRule CreateBuiltInVampireSurvivorsRule() =>
        new()
        {
            RuleId = "steam.vampire_survivors.1794680",
            TitleHint = "Vampire Survivors",
            MappedSteamAppId = VampireSurvivorsSteamAppId,
            SignatureFileName = "VampireSurvivors.exe",
            AllowAlongsideSteamEmulator = true,
            ExtractorKind = NativeSaveExtractorKind.JsonStringArray,
            JsonArrayPropertyPath = "Achievements",
            SavePathTemplates =
            [
                new(NativeSaveRootKind.PublicDocuments, Path.Combine("Steam", "RUNE", "1794680", "remote", "savedata")),
                new(NativeSaveRootKind.PublicDocuments, Path.Combine("Steam", "CODEX", "1794680", "remote", "savedata")),
                new(NativeSaveRootKind.AppDataRoaming, Path.Combine("GSE Saves", "1794680", "remote", "savedata")),
                new(NativeSaveRootKind.AppDataRoaming, Path.Combine("Goldberg SteamEmu Saves", "1794680", "remote", "savedata")),
                new(NativeSaveRootKind.AppDataLocalLow, Path.Combine("poncle", "Vampire Survivors", "SaveDataUnity.sav")),
                new(NativeSaveRootKind.AppDataRoaming, Path.Combine("Vampire_Survivors", "SaveData")),
            ],
        };

    public static NativeSaveAchievementRule CreateBuiltInLimboRule()
    {
        // Verified via static x86 disassembly of GOG LIMBO (limbo.exe @ 0x0042D240, 0x00442CC0, 0x0047BAC0):
        // - StorageEmulatorDevice writes `<GameInstallDir>/save_game/savegame.txt`
        // - `GrantAchievement(int id)` (1..13) sets bit `(id - 1)` (`1 << (id - 1)`) on integer key `achievements = "<mask>"`
        // - Mapped 1:1 to Steam AppID 48000 (`ACH_1` .. `ACH_13`)
        var bitMap = new Dictionary<int, string>(13);
        for (int i = 0; i < 13; i++)
        {
            bitMap[i] = $"ACH_{i + 1}";
        }

        IReadOnlyList<AchievementDefinitionDto> fallbackSchema =
        [
            new() { ApiName = "ACH_1",  DisplayName = "方向错误",     Description = "这是不对的。",           IsHidden = false, SortOrder = 0 },
            new() { ApiName = "ACH_2",  DisplayName = "态度决定高度", Description = "探索地面以上的地方",     IsHidden = false, SortOrder = 1 },
            new() { ApiName = "ACH_3",  DisplayName = "卡住了",       Description = "准备干着陆",             IsHidden = false, SortOrder = 2 },
            new() { ApiName = "ACH_4",  DisplayName = "都市大探险",   Description = "高高举起重物",           IsHidden = false, SortOrder = 3 },
            new() { ApiName = "ACH_5",  DisplayName = "黑暗中的孤寂", Description = "在节肢动物下",           IsHidden = false, SortOrder = 4 },
            new() { ApiName = "ACH_6",  DisplayName = "爬上齿轮",     Description = "别看到拉杆就想拉",       IsHidden = false, SortOrder = 5 },
            new() { ApiName = "ACH_7",  DisplayName = "返回原路",     Description = "爬上箱子就知道了",       IsHidden = false, SortOrder = 6 },
            new() { ApiName = "ACH_8",  DisplayName = "星星引路",     Description = "关键就是箱子",           IsHidden = false, SortOrder = 7 },
            new() { ApiName = "ACH_9",  DisplayName = "深入地心",     Description = "垂直通道",               IsHidden = false, SortOrder = 8 },
            new() { ApiName = "ACH_10", DisplayName = "向上爬升",     Description = "别输给地心引力",         IsHidden = false, SortOrder = 9 },
            new() { ApiName = "ACH_11", DisplayName = "最终完成任务", Description = "坚持下去就会成功",       IsHidden = false, SortOrder = 10 },
            new() { ApiName = "ACH_12", DisplayName = "没必要死",     Description = "死亡不超过 5 次就一口气过关", IsHidden = false, SortOrder = 11 },
            new()
            {
                ApiName = "ACH_13",
                DisplayName = "叮当声！",
                Description = "逃跑是没用的",
                HiddenSpoilerDescription = "逃跑是没用的（收集前 10 颗隐藏彩蛋开启地下隐藏关卡并抵达终点）",
                IsHidden = true,
                SortOrder = 12,
            },
        ];

        return new NativeSaveAchievementRule
        {
            RuleId = "gog.limbo.1724299977",
            TitleHint = "LIMBO",
            GogGameId = LimboGogGameId,
            MappedSteamAppId = LimboSteamAppId,
            SignatureFileName = "limbo_boot.pkg",
            ExtractorKind = NativeSaveExtractorKind.BitmaskFromRegexInt,
            UseLimboAesOrPlainDecoder = true,
            BitmaskRegexPattern = @"achievements\s*[:=]\s*[""']?(?<val>0x[0-9a-fA-F]+|\d+)[""']?",
            BitIndexToApiName = bitMap,
            SavePathTemplates =
            [
                new(NativeSaveRootKind.GameInstallDir, Path.Combine("save_game", "savegame.txt")),
                new(NativeSaveRootKind.GameInstallDir, "savegame.txt"),
                new(NativeSaveRootKind.AppDataRoaming, Path.Combine("Playdead", "Limbo", "savegame.txt")),
                new(NativeSaveRootKind.LocalAppData, Path.Combine("Playdead", "Limbo", "savegame.txt")),
            ],
            FallbackChineseSchema = fallbackSchema,
        };
    }

    private static List<ParsedAchievementEntry> EvaluateBitmaskRule(
        NativeSaveAchievementRule rule,
        ReadOnlySpan<byte> rawBytes,
        DateTimeOffset unlockTimeUtc)
    {
        string? text = rule.UseLimboAesOrPlainDecoder
            ? LimboSaveDecoder.DecodeToText(rawBytes)
            : Encoding.UTF8.GetString(rawBytes);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // Also support array/list representation e.g. `achievements = [1, 3, 5]` or `ACH_1 = 1` if written by custom tools
        var results = new List<ParsedAchievementEntry>(rule.BitIndexToApiName.Count);

        if (!string.IsNullOrWhiteSpace(rule.BitmaskRegexPattern))
        {
            var match = Regex.Match(text, rule.BitmaskRegexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                string rawVal = match.Groups["val"].Success ? match.Groups["val"].Value : match.Groups[1].Value;
                ulong mask = ParseUnsignedInt64(rawVal);

                foreach (var (bitIndex, apiName) in rule.BitIndexToApiName.OrderBy(kv => kv.Key))
                {
                    bool unlocked = bitIndex is >= 0 and < 64 && ((mask >> bitIndex) & 1UL) == 1UL;
                    results.Add(new ParsedAchievementEntry
                    {
                        ApiName = apiName,
                        IsUnlocked = unlocked,
                        UnlockedUtc = unlocked ? unlockTimeUtc : null,
                    });
                }

                return results;
            }
        }

        // Fallback: bracketed list of 1-based indices or API names e.g. `achievements = [1, 2, "ACH_3"]`
        var listMatch = Regex.Match(text, @"achievements\s*[:=]\s*\[(?<items>[^\]]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (listMatch.Success)
        {
            var unlockedApis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in listMatch.Groups["items"].Value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                string clean = token.Trim('"', '\'', ' ', '\t');
                if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx1Based)
                    && rule.BitIndexToApiName.TryGetValue(idx1Based - 1, out string? mappedApi))
                {
                    unlockedApis.Add(mappedApi);
                }
                else if (clean.StartsWith("ACH_", StringComparison.OrdinalIgnoreCase))
                {
                    unlockedApis.Add(clean);
                }
            }

            foreach (var (_, apiName) in rule.BitIndexToApiName.OrderBy(kv => kv.Key))
            {
                bool isUnl = unlockedApis.Contains(apiName);
                results.Add(new ParsedAchievementEntry
                {
                    ApiName = apiName,
                    IsUnlocked = isUnl,
                    UnlockedUtc = isUnl ? unlockTimeUtc : null,
                });
            }
        }

        return results;
    }

    private static List<ParsedAchievementEntry> EvaluateRegexKeyValueRule(
        NativeSaveAchievementRule rule,
        ReadOnlySpan<byte> rawBytes,
        DateTimeOffset unlockTimeUtc)
    {
        string text = rule.UseLimboAesOrPlainDecoder
            ? (LimboSaveDecoder.DecodeToText(rawBytes) ?? string.Empty)
            : Encoding.UTF8.GetString(rawBytes);

        var results = new List<ParsedAchievementEntry>(rule.Mappings.Count);
        foreach (var m in rule.Mappings)
        {
            if (string.IsNullOrWhiteSpace(m.RegexPattern))
            {
                continue;
            }

            var match = Regex.Match(text, m.RegexPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
            bool unlocked = false;
            if (match.Success)
            {
                string captured = match.Groups["val"].Success
                    ? match.Groups["val"].Value.Trim()
                    : (match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : match.Value.Trim());

                if (m.MinNumericThreshold.HasValue
                    && double.TryParse(captured, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                {
                    unlocked = num >= m.MinNumericThreshold.Value;
                }
                else if (!string.IsNullOrWhiteSpace(m.ExpectedValue))
                {
                    unlocked = string.Equals(captured, m.ExpectedValue, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    unlocked = SteamEmulatorFormatParsers.ParseBoolString(captured);
                }
            }

            results.Add(new ParsedAchievementEntry
            {
                ApiName = m.ApiName,
                IsUnlocked = unlocked,
                UnlockedUtc = unlocked ? unlockTimeUtc : null,
            });
        }

        return results;
    }

    private static List<ParsedAchievementEntry> EvaluateJsonPathRule(
        NativeSaveAchievementRule rule,
        ReadOnlySpan<byte> rawBytes,
        DateTimeOffset unlockTimeUtc)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBytes.ToArray());
            var results = new List<ParsedAchievementEntry>(rule.Mappings.Count);

            foreach (var m in rule.Mappings)
            {
                if (string.IsNullOrWhiteSpace(m.JsonPathExpression))
                {
                    continue;
                }

                bool unlocked = EvaluateSimpleJsonPath(doc.RootElement, m.JsonPathExpression, m.ExpectedValue, m.MinNumericThreshold);
                results.Add(new ParsedAchievementEntry
                {
                    ApiName = m.ApiName,
                    IsUnlocked = unlocked,
                    UnlockedUtc = unlocked ? unlockTimeUtc : null,
                });
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static List<ParsedAchievementEntry> EvaluateBinaryBitOffsetRule(
        NativeSaveAchievementRule rule,
        ReadOnlySpan<byte> rawBytes,
        DateTimeOffset unlockTimeUtc)
    {
        var results = new List<ParsedAchievementEntry>(rule.Mappings.Count);
        foreach (var m in rule.Mappings)
        {
            if (m.ByteOffset < 0 || m.ByteOffset >= rawBytes.Length)
            {
                results.Add(new ParsedAchievementEntry
                {
                    ApiName = m.ApiName,
                    IsUnlocked = false,
                    UnlockedUtc = null,
                });
                continue;
            }

            bool unlocked;
            if (m.MinNumericThreshold.HasValue && m.ByteOffset + 4 <= rawBytes.Length)
            {
                int val = BinaryPrimitives.ReadInt32LittleEndian(rawBytes.Slice(m.ByteOffset, 4));
                unlocked = val >= m.MinNumericThreshold.Value;
            }
            else
            {
                int bit = Math.Clamp(m.BitOffset, 0, 7);
                unlocked = ((rawBytes[m.ByteOffset] >> bit) & 1) == 1;
            }

            results.Add(new ParsedAchievementEntry
            {
                ApiName = m.ApiName,
                IsUnlocked = unlocked,
                UnlockedUtc = unlocked ? unlockTimeUtc : null,
            });
        }

        return results;
    }

    private static bool EvaluateSimpleJsonPath(
        JsonElement root,
        string expr,
        string? expectedValue,
        double? minThreshold)
    {
        string clean = expr.Trim();
        if (clean.StartsWith("$.", StringComparison.Ordinal))
        {
            clean = clean[2..];
        }
        else if (clean.StartsWith('$'))
        {
            clean = clean[1..];
        }

        JsonElement current = root;
        foreach (string rawSeg in clean.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            string seg = rawSeg;
            int? arrayIdx = null;
            bool wildcardArray = false;

            int bracket = seg.IndexOf('[');
            if (bracket >= 0 && seg.EndsWith(']'))
            {
                string inner = seg[(bracket + 1)..^1].Trim();
                seg = seg[..bracket];
                if (inner == "*")
                {
                    wildcardArray = true;
                }
                else if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIdx))
                {
                    arrayIdx = parsedIdx;
                }
            }

            if (seg.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !TryGetPropIgnoreCase(current, seg, out current))
                {
                    return false;
                }
            }

            if (wildcardArray && current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    if (MatchesJsonTerminal(item, expectedValue, minThreshold))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (arrayIdx.HasValue)
            {
                if (current.ValueKind != JsonValueKind.Array || arrayIdx.Value < 0 || arrayIdx.Value >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[arrayIdx.Value];
            }
        }

        return MatchesJsonTerminal(current, expectedValue, minThreshold);
    }

    private static bool MatchesJsonTerminal(JsonElement elem, string? expectedValue, double? minThreshold)
    {
        if (minThreshold.HasValue)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetDouble(out double d))
            {
                return d >= minThreshold.Value;
            }

            if (elem.ValueKind == JsonValueKind.String
                && double.TryParse(elem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd))
            {
                return sd >= minThreshold.Value;
            }

            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedValue))
        {
            return string.Equals(elem.ToString(), expectedValue, StringComparison.OrdinalIgnoreCase);
        }

        return elem.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => elem.TryGetDouble(out double n) && n != 0,
            JsonValueKind.String => SteamEmulatorFormatParsers.ParseBoolString(elem.GetString()),
            _ => false,
        };
    }

    private static bool TryGetPropIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static ulong ParseUnsignedInt64(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex) ? hex : 0;
        }

        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong dec) ? dec : 0;
    }

    private static List<ParsedAchievementEntry> EvaluateJsonStringArrayRule(
        NativeSaveAchievementRule rule,
        ReadOnlySpan<byte> rawBytes,
        DateTimeOffset unlockTimeUtc)
    {
        if (rawBytes.IsEmpty)
        {
            return [];
        }

        ReadOnlySpan<byte> span = rawBytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        int nullIdx = span.IndexOf((byte)0);
        if (nullIdx > 0)
        {
            span = span[..nullIdx];
        }

        try
        {
            using var doc = JsonDocument.Parse(span.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            string path = string.IsNullOrWhiteSpace(rule.JsonArrayPropertyPath) ? "Achievements" : rule.JsonArrayPropertyPath.Trim();
            if (path.StartsWith("$.", StringComparison.Ordinal))
            {
                path = path[2..];
            }
            else if (path.StartsWith('$'))
            {
                path = path[1..];
            }

            JsonElement cur = doc.RootElement;
            foreach (string seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cur.ValueKind != JsonValueKind.Object || !TryGetPropIgnoreCase(cur, seg, out cur))
                {
                    return [];
                }
            }

            if (cur.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<ParsedAchievementEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var elem in cur.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? apiName = elem.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && seen.Add(apiName))
                {
                    results.Add(new ParsedAchievementEntry
                    {
                        ApiName = apiName,
                        IsUnlocked = true,
                        UnlockedUtc = unlockTimeUtc,
                    });
                }
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static string? ResolveRoot(NativeSaveRootKind kind, AchievementSourceContext context) =>
        kind switch
        {
            NativeSaveRootKind.GameInstallDir => context.Game.InstallDir,
            NativeSaveRootKind.AppDataRoaming => context.GetAppDataRoaming(),
            NativeSaveRootKind.LocalAppData => context.GetLocalAppData(),
            NativeSaveRootKind.UserDocuments => context.GetUserDocuments(),
            NativeSaveRootKind.PublicDocuments => context.GetPublicDocuments(),
            NativeSaveRootKind.AppDataLocalLow => context.GetAppDataLocalLow(),
            _ => null,
        };

    private static bool HasSteamApiDll(string? installDir) =>
        !string.IsNullOrWhiteSpace(installDir)
        && Directory.Exists(installDir)
        && (SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "steam_api64.dll", maxDepth: 4).Any()
            || SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "steam_api.dll", maxDepth: 4).Any());

    private static bool IsUnderTempDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        try
        {
            string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            string candidate = Path.GetFullPath(dir);
            return candidate.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
