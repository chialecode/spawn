using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Spawn.Contracts;
using Spawn.Core.AntiCheat;
using Spawn.Core.Storage;
using Spawn.Core.Update;

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
        services.TryAddSingleton<CasChunkStore>(sp => new CasChunkStore(
            paths: sp.GetRequiredService<SpawnPaths>(),
            database: sp.GetRequiredService<SpawnDatabase>(),
            timeProvider: sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton(AreWeAntiCheatYetIndex.Default);
        services.TryAddSingleton<IAntiCheatSystemProbe>(DefaultAntiCheatSystemProbe.Instance);
        services.TryAddSingleton(sp => new SteamAppCategoryClassifier(
            database: sp.GetRequiredService<SpawnDatabase>(),
            timeProvider: sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton(sp => new AntiCheatGuardService(
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            persistedStateFilePath: paths.AntiCheatGuardStateFilePath,
            trainerSandboxRootsProvider: () => ResolveTrainerSandboxRoots(paths, sp.GetRequiredService<SpawnDatabase>())));
        services.TryAddSingleton<IAntiCheatGuardService>(sp => sp.GetRequiredService<AntiCheatGuardService>());

        services.TryAddSingleton(sp => new TrainerSafetyGate(
            antiCheatGuard: sp.GetRequiredService<IAntiCheatGuardService>(),
            database: sp.GetRequiredService<SpawnDatabase>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            systemProbe: sp.GetRequiredService<IAntiCheatSystemProbe>(),
            steamCategoryClassifier: sp.GetRequiredService<SteamAppCategoryClassifier>(),
            awacyIndex: sp.GetRequiredService<AreWeAntiCheatYetIndex>(),
            logger: sp.GetService<ILogger<TrainerSafetyGate>>()));
        services.TryAddSingleton<ITrainerSafetyGate>(sp => sp.GetRequiredService<TrainerSafetyGate>());

        services.TryAddSingleton<IOverlayIpcClient, OverlayIpcClient>();
        services.TryAddSingleton<SingleInstanceCoordinator>();

        services.TryAddSingleton<UpdateActivityTracker>();
        services.TryAddSingleton<IUpdateInstallerLauncher, ShellExecuteUpdateInstallerLauncher>();
        services.TryAddSingleton<IUpdateIdleEvaluator>(ConservativeFallbackUpdateIdleEvaluator.Instance);
        services.TryAddSingleton<IUpdateQuitCoordinator>(NoOpUpdateQuitCoordinator.Instance);
        services.TryAddSingleton(_ => new UpdateServiceOptions());
        services.TryAddSingleton<IUpdateService>(sp => new UpdateService(
            paths: sp.GetRequiredService<SpawnPaths>(),
            database: sp.GetRequiredService<SpawnDatabase>(),
            idleEvaluator: sp.GetRequiredService<IUpdateIdleEvaluator>(),
            quitCoordinator: sp.GetRequiredService<IUpdateQuitCoordinator>(),
            installerLauncher: sp.GetRequiredService<IUpdateInstallerLauncher>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            options: sp.GetRequiredService<UpdateServiceOptions>(),
            httpMessageHandler: null,
            logger: sp.GetService<ILogger<UpdateService>>()));

        return services;
    }

    /// <summary>
    /// Default sandbox root plus the user-configured custom root (if any), so the anti-cheat guard's
    /// pre-launch high-risk probe covers the same directory the trainer runtime and LOOP-06 purge use.
    /// </summary>
    public static IReadOnlyList<string> ResolveTrainerSandboxRoots(SpawnPaths paths, SpawnDatabase database)
    {
        var roots = new List<string> { paths.TrainerSandboxDirectory };
        try
        {
            TrainerSettingsDto settings = database.GetTrainerSettingsAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            string active = paths.ResolveTrainerSandboxDirectory(settings.CustomSandboxRoot, requirePureAscii: false);
            if (!roots.Contains(active, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(active);
            }
        }
        catch (Exception)
        {
            // Settings unavailable: the default root is still probed.
        }

        return roots;
    }
}
