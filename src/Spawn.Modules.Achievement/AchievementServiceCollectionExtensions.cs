using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Dependency injection registration extensions for <c>Spawn.Modules.Achievement</c>.
/// </summary>
public static class AchievementServiceCollectionExtensions
{
    public static IServiceCollection AddSpawnAchievement(
        this IServiceCollection services,
        Action<AchievementModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AchievementModuleOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<GogCometLocalDaemon>();
        services.TryAddSingleton<NativeSaveAchievementProvider>();
        services.TryAddSingleton<SteamEmulatorAchievementProvider>();
        services.TryAddSingleton<SteamLocalCacheAchievementProvider>();
        services.TryAddSingleton<GogAchievementProvider>();

        services.AddSingleton<IAchievementSourceProvider>(sp => sp.GetRequiredService<SteamEmulatorAchievementProvider>());
        services.AddSingleton<IAchievementSourceProvider>(sp => sp.GetRequiredService<NativeSaveAchievementProvider>());
        services.AddSingleton<IAchievementSourceProvider>(sp => sp.GetRequiredService<GogAchievementProvider>());
        services.AddSingleton<IAchievementSourceProvider>(sp => sp.GetRequiredService<SteamLocalCacheAchievementProvider>());

        services.TryAddSingleton<AchievementIconProcessor>();
        services.TryAddSingleton<AchievementSchemaService>();
        services.TryAddSingleton<PinnedGbeForkReleaseSource>();
        services.TryAddSingleton<IGbeForkBinarySource>(sp => sp.GetRequiredService<PinnedGbeForkReleaseSource>());
        services.TryAddSingleton<GbeForkActivatorService>();

        services.TryAddSingleton<AchievementService>();
        services.TryAddSingleton<IAchievementService>(sp => sp.GetRequiredService<AchievementService>());

        return services;
    }
}
