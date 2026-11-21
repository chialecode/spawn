using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Registers core SPAWN infrastructure services into a DI container.
/// </summary>
public static class SpawnCoreServiceCollectionExtensions
{
    public static IServiceCollection AddSpawnCore(
        this IServiceCollection services,
        SpawnPaths? explicitPaths = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var paths = explicitPaths ?? SpawnPaths.Resolve();
        services.TryAddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISpawnEventBus, InMemorySpawnEventBus>();
        services.TryAddSingleton<DpapiSecretStore>();
        services.TryAddSingleton<ISteamApiKeyProvider, SteamApiKeyProvider>();
        services.TryAddSingleton<SpawnDatabase>();
        services.TryAddSingleton(sp => new AntiCheatGuardService(
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            persistedStateFilePath: paths.AntiCheatGuardStateFilePath));
        services.TryAddSingleton<IAntiCheatGuardService>(sp => sp.GetRequiredService<AntiCheatGuardService>());
        services.TryAddSingleton<IOverlayIpcClient, OverlayIpcClient>();
        services.TryAddSingleton<SingleInstanceCoordinator>();

        return services;
    }
}
