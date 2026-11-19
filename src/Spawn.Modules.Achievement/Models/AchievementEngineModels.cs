using System.IO.Hashing;
using System.Text;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Enumeration of all supported offline Steam emulators, GOG channels, official caches, and native save rules.
/// </summary>
public enum EmulatorFlavor
{
    Unknown = 0,
    Goldberg = 1,
    GbeFork = 2,
    Codex = 3,
    Rune = 4,
    Plaza = 5,
    Empress = 6,
    Tenoke = 7,
    OnlineFix = 8,
    Ali213 = 9,
    ThreeDm = 10,
    Reloaded = 11,
    Skidrow = 12,
    Hoodlum = 13,
    SmartSteamEmu = 14,
    GreenLuma = 15,
    CreamApi = 16,
    SmokeApi = 17,
    SteamLocalBin = 18,
    GogCometLocal = 19,
    GogGalaxyDb = 20,
    NemirtingasGalaxyEmu = 21,
    NativeSaveRule = 22,
    FltNoOpStub = 23,
}

/// <summary>
/// Configuration options for the SPAWN Achievement module (supports deterministic overrides for isolated unit tests).
/// </summary>
public sealed class AchievementModuleOptions
{
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromMilliseconds(120);
    public TimeSpan ActiveGamePollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public int FileSystemWatcherBufferBytes { get; set; } = 65536;
    public int GogCometPort { get; set; } = 9977;
    public bool EnableGogCometAutoStart { get; set; } = true;
    public bool EnableCommunitySpoilerRequests { get; set; } = true;
    public TimeSpan CommunitySpoilerTimeout { get; set; } = TimeSpan.FromSeconds(2.5);
    public bool AllowNetworkDownloads { get; set; } = true;

    /// <summary>
    /// Optional root overrides for isolated unit tests so emulator discovery never touches real user profile directories.
    /// </summary>
    public AchievementEnvironmentOverrides? EnvironmentOverrides { get; set; }
}

/// <summary>
/// Redirects standard Windows special folders and Steam/GOG roots during isolated tests.
/// </summary>
public sealed record AchievementEnvironmentOverrides
{
    public string? AppDataRoaming { get; init; }
    public string? LocalAppData { get; init; }
    public string? LocalLow { get; init; }
    public string? PublicDocuments { get; init; }
    public string? UserDocuments { get; init; }
    public string? ProgramData { get; init; }
    public string? SteamInstallPath { get; init; }
    public string? RegistryRootKeyOverride { get; init; }
}

/// <summary>
/// Normalized single achievement state produced by any format parser or source provider.
/// </summary>
public sealed record ParsedAchievementEntry
{
    public required string ApiName { get; init; }
    public required bool IsUnlocked { get; init; }
    public DateTimeOffset? UnlockedUtc { get; init; }
    public double? CurrentProgress { get; init; }
    public double? MaxProgress { get; init; }
}

/// <summary>
/// Concrete file, database, network daemon, or registry endpoint watched for achievement changes.
/// </summary>
public sealed record AchievementWatchTarget
{
    public required string ProviderId { get; init; }
    public required EmulatorFlavor Flavor { get; init; }
    public required AchievementCaptureMode CaptureMode { get; init; }
    public required string ChannelLabel { get; init; }
    public string? FilePath { get; init; }
    public string? RegistryKeyPath { get; init; }
    public string? RuleId { get; init; }
    public bool IsPrimary { get; init; }
}

/// <summary>
/// Rich per-game resolution context passed to <see cref="IAchievementSourceProvider"/> instances.
/// </summary>
public sealed class AchievementSourceContext
{
    public required GameRecord Game { get; init; }
    public int? EffectiveSteamAppId { get; init; }
    public string? EffectiveGogGameId { get; init; }
    public string? EffectiveGogClientId { get; init; }
    public IReadOnlyList<AchievementDefinitionDto> SchemaDefinitions { get; init; } = [];
    public IReadOnlyDictionary<uint, string> Crc32ToApiName { get; init; } = new Dictionary<uint, string>();
    public AchievementEnvironmentOverrides? EnvironmentOverrides { get; init; }

    public string GetAppDataRoaming() =>
        !string.IsNullOrWhiteSpace(EnvironmentOverrides?.AppDataRoaming)
            ? EnvironmentOverrides.AppDataRoaming
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string GetLocalAppData() =>
        !string.IsNullOrWhiteSpace(EnvironmentOverrides?.LocalAppData)
            ? EnvironmentOverrides.LocalAppData
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string GetPublicDocuments()
    {
        if (!string.IsNullOrWhiteSpace(EnvironmentOverrides?.PublicDocuments))
        {
            return EnvironmentOverrides.PublicDocuments;
        }

        string commonDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (!string.IsNullOrWhiteSpace(commonDocs))
        {
            return commonDocs;
        }

        string? pub = Environment.GetEnvironmentVariable("PUBLIC");
        return !string.IsNullOrWhiteSpace(pub) ? Path.Combine(pub, "Documents") : string.Empty;
    }

    public string GetUserDocuments() =>
        !string.IsNullOrWhiteSpace(EnvironmentOverrides?.UserDocuments)
            ? EnvironmentOverrides.UserDocuments
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string GetProgramData() =>
        !string.IsNullOrWhiteSpace(EnvironmentOverrides?.ProgramData)
            ? EnvironmentOverrides.ProgramData
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public string GetAppDataLocalLow()
    {
        if (!string.IsNullOrWhiteSpace(EnvironmentOverrides?.LocalLow))
        {
            return EnvironmentOverrides.LocalLow;
        }

        if (EnvironmentOverrides is not null)
        {
            // Stay 100% isolated inside the test sandbox when EnvironmentOverrides is active!
            return !string.IsNullOrWhiteSpace(EnvironmentOverrides.LocalAppData)
                ? Path.GetFullPath(Path.Combine(EnvironmentOverrides.LocalAppData, "..", "LocalLow"))
                : string.Empty;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(userProfile)
            ? Path.Combine(userProfile, "AppData", "LocalLow")
            : string.Empty;
    }

    /// <summary>
    /// Precomputes IEEE 802.3 CRC32 reverse-lookup dictionary for SmartSteamEmu (<c>stats.bin</c>) O(1) resolution.
    /// </summary>
    public static IReadOnlyDictionary<uint, string> BuildCrc32Lookup(IEnumerable<string> apiNames)
    {
        var map = new Dictionary<uint, string>();
        foreach (string raw in apiNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string canonical = raw.Trim();
            uint exact = ComputeAsciiCrc32(canonical);
            uint lower = ComputeAsciiCrc32(canonical.ToLowerInvariant());
            uint upper = ComputeAsciiCrc32(canonical.ToUpperInvariant());

            map[exact] = canonical;
            map.TryAdd(lower, canonical);
            map.TryAdd(upper, canonical);
        }

        return map;
    }

    public static uint ComputeAsciiCrc32(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        return Crc32.HashToUInt32(bytes);
    }
}

/// <summary>
/// Pluggable achievement capture source provider contract covering Steam emulators, official Steam cache,
/// GOG Comet / Galaxy DB / Nemirtingas, and pure DRM-Free native save rules.
/// </summary>
public interface IAchievementSourceProvider
{
    string ProviderId { get; }
    int Priority { get; }

    IReadOnlyList<AchievementWatchTarget> DiscoverTargets(AchievementSourceContext context);

    ValueTask<IReadOnlyList<ParsedAchievementEntry>> ReadUnlocksAsync(
        AchievementWatchTarget target,
        AchievementSourceContext context,
        CancellationToken cancellationToken = default);
}
