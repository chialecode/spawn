using System.Globalization;
using Microsoft.Win32;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Zero-intrusion local Steam client <c>appcache/stats/UserGameStats_&lt;Id32&gt;_&lt;AppId&gt;.bin</c> Binary VDF provider.
/// Never calls <c>SteamAPI_Init</c> and never opens handles to the Steam or game processes.
/// </summary>
public sealed class SteamLocalCacheAchievementProvider : IAchievementSourceProvider
{
    public const string Id = "steam.local_cache";

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

        string? steamRoot = ResolveSteamInstallPath(context.EnvironmentOverrides);
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

    public static string? ResolveSteamInstallPath(AchievementEnvironmentOverrides? overrides)
    {
        if (overrides is not null)
        {
            // In isolated test mode, strictly respect overrides and never leak into host Steam directories.
            return !string.IsNullOrWhiteSpace(overrides.SteamInstallPath) && Directory.Exists(overrides.SteamInstallPath)
                ? Path.GetFullPath(overrides.SteamInstallPath)
                : null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: false);
            string? regPath = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(regPath))
            {
                string normalized = regPath.Replace('/', Path.DirectorySeparatorChar);
                if (Directory.Exists(normalized))
                {
                    return Path.GetFullPath(normalized);
                }
            }
        }
        catch
        {
            // Ignore registry lookup errors.
        }

        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
        {
            string candidate = Path.Combine(pf86, "Steam");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
