using System.Globalization;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Zero-intrusion local Steam client <c>appcache/stats/UserGameStats_&lt;Id32&gt;_&lt;AppId&gt;.bin</c> Binary VDF provider.
/// Never calls <c>SteamAPI_Init</c> and never opens handles to the Steam or game processes.
/// </summary>
public sealed class SteamLocalCacheAchievementProvider : IAchievementSourceProvider
{
    public const string Id = "steam.local_cache";

    private readonly ISteamClientLocator? _steamLocator;

    public SteamLocalCacheAchievementProvider(ISteamClientLocator? steamLocator = null)
    {
        _steamLocator = steamLocator;
    }

    public string ProviderId => Id;
    public int Priority => 40;

    public IReadOnlyList<AchievementWatchTarget> DiscoverTargets(AchievementSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int? appId = context.EffectiveSteamAppId;
        if (appId is null or <= 0)
        {
            return [];
        }

        string? steamRoot = ResolveSteamInstallPath(
            context.EnvironmentOverrides,
            context.SteamInstallDirectoryOverride,
            _steamLocator);
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return [];
        }

        string statsDir = Path.Combine(steamRoot, "appcache", "stats");
        if (!Directory.Exists(statsDir))
        {
            return [];
        }

        string pattern = $"UserGameStats_*_{appId.Value.ToString(CultureInfo.InvariantCulture)}.bin";
        string[] files;
        try
        {
            files = Directory.GetFiles(statsDir, pattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }

        var targets = new List<AchievementWatchTarget>();
        foreach (string file in files.OrderByDescending(f => ResilientFileReader.GetMetadataStamp(f).LastWriteTicksUtc))
        {
            targets.Add(new AchievementWatchTarget
            {
                ProviderId = ProviderId,
                Flavor = EmulatorFlavor.SteamLocalBin,
                CaptureMode = AchievementCaptureMode.SteamLocalCache,
                ChannelLabel = "Steam 本地缓存监听 · 就绪",
                FilePath = Path.GetFullPath(file),
                IsPrimary = targets.Count == 0,
            });
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

        byte[]? rawStats = await ResilientFileReader.ReadSharedBytesWithRetryAsync(target.FilePath, cancellationToken).ConfigureAwait(false);
        if (rawStats is null || rawStats.Length == 0)
        {
            return [];
        }

        IReadOnlyList<SteamBinAchievementBitMapping> bitMappings = [];
        int appId = context.EffectiveSteamAppId ?? 0;

        string? statsDir = Path.GetDirectoryName(target.FilePath);
        if (!string.IsNullOrWhiteSpace(statsDir) && appId > 0)
        {
            string schemaPath = Path.Combine(statsDir, $"UserGameStatsSchema_{appId.ToString(CultureInfo.InvariantCulture)}.bin");
            byte[]? rawSchema = await ResilientFileReader.ReadSharedBytesWithRetryAsync(schemaPath, cancellationToken).ConfigureAwait(false);
            if (rawSchema is { Length: > 0 })
            {
                bitMappings = SteamBinaryVdfParser.ParseSchemaBin(rawSchema, appId);
            }
        }

        return SteamBinaryVdfParser.ParseUserStatsBin(rawStats, bitMappings);
    }

    public static string? ResolveSteamInstallPath(
        AchievementEnvironmentOverrides? overrides,
        string? globalSteamInstallDirOverride = null,
        ISteamClientLocator? steamLocator = null)
    {
        if (overrides is not null)
        {
            // In isolated test mode, strictly respect overrides (and explicit DB override when set) and never leak into host Steam directories.
            string? explicitOverride = !string.IsNullOrWhiteSpace(overrides.SteamInstallPath)
                ? overrides.SteamInstallPath
                : globalSteamInstallDirOverride;
            return TryNormalizeExistingSteamDirectory(explicitOverride);
        }

        if (!string.IsNullOrWhiteSpace(globalSteamInstallDirOverride))
        {
            return TryNormalizeExistingSteamDirectory(globalSteamInstallDirOverride);
        }

        ISteamClientLocator locator = steamLocator ?? SteamClientLocator.Default;
        if (locator is SteamClientLocator concreteLocator)
        {
            IReadOnlyList<(string Directory, SteamPathDiscoverySource Source)> candidates =
                concreteLocator.EnumerateCandidateInstallDirectories(explicitInstallDirOverride: null);
            return candidates.Count > 0 ? candidates[0].Directory : null;
        }

        SteamClientDiscoveryResult discovery = locator.Discover(explicitInstallDirOverride: null);
        return TryNormalizeExistingSteamDirectory(discovery.InstallDirectory);
    }

    private static string? TryNormalizeExistingSteamDirectory(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            string trimmed = rawPath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            string full = Path.GetFullPath(trimmed);
            if (string.Equals(Path.GetFileName(full), SteamClientLocator.SteamExecutableFileName, StringComparison.OrdinalIgnoreCase))
            {
                full = Path.GetDirectoryName(full) ?? full;
            }

            string normalized = Path.TrimEndingDirectorySeparator(full);
            return Directory.Exists(normalized) ? normalized : null;
        }
        catch
        {
            return null;
        }
    }
}
