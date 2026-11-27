using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spawn.Contracts;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Dependency injection registration extensions for <c>Spawn.Modules.Launcher</c>.
/// </summary>
public static class LauncherServiceCollectionExtensions
{
    public static IServiceCollection AddSpawnLauncher(
        this IServiceCollection services,
        Action<GameScanOptions>? configureScanOptions = null,
        Action<GameLaunchOptions>? configureLaunchOptions = null,
        Action<GameDataManagementOptions>? configureGameDataOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var scanOptions = new GameScanOptions();
        configureScanOptions?.Invoke(scanOptions);
        services.TryAddSingleton(scanOptions);

        var launchOptions = new GameLaunchOptions();
        configureLaunchOptions?.Invoke(launchOptions);
        services.TryAddSingleton(launchOptions);

        var gameDataOptions = new GameDataManagementOptions();
        configureGameDataOptions?.Invoke(gameDataOptions);
        services.TryAddSingleton(gameDataOptions);

        services.AddHttpClient(nameof(Spawn.Modules.Launcher), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnLauncher/1.0 (Windows NT 10.0; Win64; x64)");
        });

        services.TryAddSingleton<GameArtworkExtractor>(sp =>
        {
            var factory = sp.GetService<IHttpClientFactory>();
            var httpClient = factory?.CreateClient(nameof(Spawn.Modules.Launcher));
            return new GameArtworkExtractor(
                sp.GetRequiredService<Spawn.Core.SpawnPaths>(),
                httpClient,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<GameArtworkExtractor>>());
        });

        services.TryAddSingleton<GameDirectoryScanner>(sp =>
        {
            var factory = sp.GetService<IHttpClientFactory>();
            var httpClient = factory?.CreateClient(nameof(Spawn.Modules.Launcher));
            return new GameDirectoryScanner(
                sp.GetRequiredService<IAntiCheatGuardService>(),
                sp.GetRequiredService<GameArtworkExtractor>(),
                httpClient,
                sp.GetRequiredService<GameScanOptions>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<GameDirectoryScanner>>());
        });

        services.TryAddSingleton<GameLibraryService>();
        services.TryAddSingleton<IGameLibraryService>(sp => sp.GetRequiredService<GameLibraryService>());

        services.TryAddSingleton<IUserActivityProbe, Win32UserActivityProbe>();
        services.TryAddSingleton<IGameProcessStarter, DefaultGameProcessStarter>();

        services.TryAddSingleton<GameLaunchService>();
        services.TryAddSingleton<IGameLaunchService>(sp => sp.GetRequiredService<GameLaunchService>());

        services.TryAddSingleton<GameDataManagementService>(sp =>
        {
            var factory = sp.GetService<IHttpClientFactory>();
            var httpClient = factory?.CreateClient(nameof(Spawn.Modules.Launcher));
            return new GameDataManagementService(
                sp.GetRequiredService<Spawn.Core.SpawnDatabase>(),
                sp.GetRequiredService<Spawn.Core.SpawnPaths>(),
                sp.GetRequiredService<IGameLaunchService>(),
                sp,
                httpClient,
                sp.GetRequiredService<GameDataManagementOptions>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<GameDataManagementService>>(),
                sp.GetService<ISteamClientLocator>());
        });
        services.TryAddSingleton<IGameDataManagementService>(sp => sp.GetRequiredService<GameDataManagementService>());

        return services;
    }
}
