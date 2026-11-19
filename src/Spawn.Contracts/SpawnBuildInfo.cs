using System.Reflection;

namespace Spawn.Contracts;

/// <summary>
/// Immutable snapshot of build, channel, and identity metadata embedded at compile/package time.
/// </summary>
public sealed record SpawnBuildMetadataSnapshot(
    string Channel,
    string Identity,
    string Version,
    string CommitSha,
    string BuildTimeUtc,
    string InstallDirectory)
{
    public bool IsReleaseChannel => string.Equals(Channel, "release", StringComparison.OrdinalIgnoreCase);
    public bool IsDevOrSourceChannel => !IsReleaseChannel;
    public string DisplayProductName => IsReleaseChannel ? "SPAWN" : "SPAWN Dev";
}

/// <summary>
/// Reads compile-time <see cref="AssemblyMetadataAttribute"/> values injected by <c>Directory.Build.props</c>
/// and <c>scripts/package.py</c>, with optional environment overrides for isolated test harnesses.
/// </summary>
public static class SpawnBuildInfo
{
    public const string ChannelEnvVar = "SPAWN_CHANNEL";
    public const string IdentityEnvVar = "SPAWN_IDENTITY";

    private static readonly Lazy<Dictionary<string, string>> MetadataMap = new(LoadAssemblyMetadata);

    public static string Channel
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable(ChannelEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.Trim();
            }

            return GetMetadataValue("SpawnChannel", "source");
        }
    }

    public static string Identity
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable(IdentityEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.Trim();
            }

            string? envChannel = Environment.GetEnvironmentVariable(ChannelEnvVar);
            if (string.Equals(envChannel, "release", StringComparison.OrdinalIgnoreCase))
            {
                return "spawn";
            }

            return GetMetadataValue("SpawnIdentity", IsReleaseChannel ? "spawn" : "spawn-dev");
        }
    }

    public static string Version => GetMetadataValue("SpawnVersion", "0.1.0-source");

    public static string CommitSha => GetMetadataValue("SpawnCommitSha", "local");

    public static string BuildTimeUtc => GetMetadataValue("SpawnBuildTimeUtc", "dev");

    public static bool IsReleaseChannel => string.Equals(Channel, "release", StringComparison.OrdinalIgnoreCase);

    public static bool IsDevOrSourceChannel => !IsReleaseChannel;

    public static string DisplayProductName => IsReleaseChannel ? "SPAWN" : "SPAWN Dev";

    public static string InstallDirectory =>
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static SpawnBuildMetadataSnapshot GetSnapshot(string? explicitIdentityOverride = null) =>
        new(
            Channel: Channel,
            Identity: string.IsNullOrWhiteSpace(explicitIdentityOverride) ? Identity : explicitIdentityOverride.Trim(),
            Version: Version,
            CommitSha: CommitSha,
            BuildTimeUtc: BuildTimeUtc,
            InstallDirectory: InstallDirectory);

    private static string GetMetadataValue(string key, string fallback) =>
        MetadataMap.Value.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static Dictionary<string, string> LoadAssemblyMetadata()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Read fallback assembly first, then let EntryAssembly win if it defines Spawn* metadata.
        PopulateFromAssembly(typeof(SpawnBuildInfo).Assembly, dict);
        var entry = Assembly.GetEntryAssembly();
        if (entry is not null && entry != typeof(SpawnBuildInfo).Assembly)
        {
            PopulateFromAssembly(entry, dict);
        }

        return dict;
    }

    private static void PopulateFromAssembly(Assembly assembly, Dictionary<string, string> sink)
    {
        try
        {
            foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!string.IsNullOrWhiteSpace(attr.Key) && !string.IsNullOrWhiteSpace(attr.Value))
                {
                    sink[attr.Key] = attr.Value;
                }
            }
        }
        catch
        {
            // Ignore reflection anomalies in dynamic test hosts.
        }
    }
}
