using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spawn.Contracts;

namespace Spawn.Core.AntiCheat;

/// <summary>
/// Single projected record from the MIT-licensed AreWeAntiCheatYet dataset.
/// </summary>
public sealed record AreWeAntiCheatYetEntry
{
    [JsonPropertyName("steamAppId")]
    public int? SteamAppId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];

    [JsonPropertyName("antiCheats")]
    public IReadOnlyList<string> AntiCheats { get; init; } = [];

    [JsonPropertyName("minTier")]
    public int MinTier { get; init; }

    [JsonPropertyName("hasSinglePlayer")]
    public bool? HasSinglePlayer { get; init; }
}

/// <summary>
/// Result of querying <see cref="AreWeAntiCheatYetIndex"/> for a game.
/// </summary>
public sealed record AreWeAntiCheatYetMatch(
    AreWeAntiCheatYetEntry Entry,
    AntiCheatTier Tier,
    string PrimarySignature,
    IReadOnlyList<string> AntiCheats,
    bool? HasSinglePlayerHint,
    string MatchedBy);

/// <summary>
/// Read-only embedded offline projection of AreWeAntiCheatYet (MIT) mapping Steam AppIds and
/// normalized canonical game titles/aliases to anti-cheat systems and minimum static tiers (Layer 2).
/// </summary>
public sealed class AreWeAntiCheatYetIndex
{
    public const string EmbeddedResourceName = "Spawn.Core.AntiCheat.areweanticheatyet-index.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<AreWeAntiCheatYetIndex> LazyDefault = new(LoadEmbeddedDefault);

    private readonly Dictionary<int, AreWeAntiCheatYetEntry> _bySteamAppId;
    private readonly Dictionary<string, AreWeAntiCheatYetEntry> _byNormalizedName;

    public static AreWeAntiCheatYetIndex Default => LazyDefault.Value;

    public string License { get; }
    public string Source { get; }
    public IReadOnlyList<AreWeAntiCheatYetEntry> Entries { get; }

    private AreWeAntiCheatYetIndex(
        string source,
        string license,
        IReadOnlyList<AreWeAntiCheatYetEntry> entries)
    {
        Source = source;
        License = license;
        Entries = entries;

        _bySteamAppId = new Dictionary<int, AreWeAntiCheatYetEntry>();
        _byNormalizedName = new Dictionary<string, AreWeAntiCheatYetEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.SteamAppId is > 0 && !_bySteamAppId.ContainsKey(entry.SteamAppId.Value))
            {
                _bySteamAppId[entry.SteamAppId.Value] = entry;
            }

            RegisterNormalizedName(entry.Name, entry);
            foreach (string alias in entry.Aliases)
            {
                RegisterNormalizedName(alias, entry);
            }
        }
    }

    public static AreWeAntiCheatYetIndex LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var doc = JsonSerializer.Deserialize<AreWeAntiCheatYetDocument>(json, JsonOptions)
                  ?? throw new InvalidDataException("Failed to deserialize AreWeAntiCheatYet index JSON.");

        return new AreWeAntiCheatYetIndex(
            source: doc.Source ?? "AreWeAntiCheatYet",
            license: doc.License ?? "MIT",
            entries: doc.Entries ?? []);
    }

    public AreWeAntiCheatYetMatch? Inspect(GameRecord game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Inspect(game.SteamAppId, game.Title, game.InstallDir, game.ExecutableRelPath);
    }

    public AreWeAntiCheatYetMatch? Inspect(
        int? steamAppId,
        string? title,
        string? installDir = null,
        string? executableRelPath = null)
    {
        // 1. Primary lookup: exact Steam AppId match.
        if (steamAppId is > 0 && _bySteamAppId.TryGetValue(steamAppId.Value, out var appIdEntry))
        {
            return BuildMatch(appIdEntry, $"SteamAppId:{steamAppId.Value}");
        }

        // 2. Secondary lookup: exact normalized title / folder / executable stem match (for non-Steam or standalone imports).
        if (TryMatchExactNormalized(title, out var titleEntry))
        {
            return BuildMatch(titleEntry!, "Title");
        }

        if (!string.IsNullOrWhiteSpace(installDir))
        {
            string trimmedDir = Path.TrimEndingDirectorySeparator(installDir.Trim());
            string leaf = Path.GetFileName(trimmedDir);
            if (TryMatchExactNormalized(leaf, out var dirEntry))
            {
                return BuildMatch(dirEntry!, "InstallDir");
            }
        }

        if (!string.IsNullOrWhiteSpace(executableRelPath))
        {
            string exeStem = Path.GetFileNameWithoutExtension(executableRelPath.Trim());
            if (TryMatchExactNormalized(exeStem, out var exeEntry))
            {
                return BuildMatch(exeEntry!, "Executable");
            }
        }

        return null;
    }

    public static string NormalizeLookupKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        bool prevWasSpace = true;

        foreach (char ch in raw)
        {
            if (ch is '™' or '®' or '©')
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevWasSpace = false;
            }
            else if (!prevWasSpace)
            {
                sb.Append(' ');
                prevWasSpace = true;
            }
        }

        int len = sb.Length;
        while (len > 0 && sb[len - 1] == ' ')
        {
            len--;
        }

        return len == 0 ? string.Empty : sb.ToString(0, len);
    }

    private void RegisterNormalizedName(string? candidate, AreWeAntiCheatYetEntry entry)
    {
        string normalized = NormalizeLookupKey(candidate);
        if (!string.IsNullOrEmpty(normalized) && !_byNormalizedName.ContainsKey(normalized))
        {
            _byNormalizedName[normalized] = entry;
        }
    }

    private bool TryMatchExactNormalized(string? candidate, out AreWeAntiCheatYetEntry? entry)
    {
        entry = null;
        string normalized = NormalizeLookupKey(candidate);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        return _byNormalizedName.TryGetValue(normalized, out entry);
    }

    private static AreWeAntiCheatYetMatch BuildMatch(AreWeAntiCheatYetEntry entry, string matchedBy)
    {
        var tier = (AntiCheatTier)Math.Clamp(entry.MinTier, 0, 3);
        foreach (string ac in entry.AntiCheats)
        {
            var derived = AntiCheatFeatureCatalog.ClassifyAntiCheatSystemName(ac);
            if (derived > tier)
            {
                tier = derived;
            }
        }

        string primaryAc = entry.AntiCheats.Count > 0 ? entry.AntiCheats[0] : "AntiCheat";
        string primarySignature = $"AWACY:{primaryAc}";
        return new AreWeAntiCheatYetMatch(
            Entry: entry,
            Tier: tier,
            PrimarySignature: primarySignature,
            AntiCheats: entry.AntiCheats,
            HasSinglePlayerHint: entry.HasSinglePlayer,
            MatchedBy: matchedBy);
    }

    private static AreWeAntiCheatYetIndex LoadEmbeddedDefault()
    {
        Assembly asm = typeof(AreWeAntiCheatYetIndex).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            throw new MissingManifestResourceException(
                $"Embedded resource '{EmbeddedResourceName}' was not found in assembly '{asm.FullName}'.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return LoadFromJson(reader.ReadToEnd());
    }

    private sealed class AreWeAntiCheatYetDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("license")]
        public string? License { get; init; }

        [JsonPropertyName("entries")]
        public List<AreWeAntiCheatYetEntry>? Entries { get; init; }
    }
}
