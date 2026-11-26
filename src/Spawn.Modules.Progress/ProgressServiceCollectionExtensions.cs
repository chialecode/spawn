using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Audio;
using Spawn.Modules.Progress.Catalog;
using Spawn.Modules.Progress.Runtime;
using Spawn.Modules.Progress.Saves.Cloud;
using Spawn.Modules.Progress.Saves.Engine;
using Spawn.Modules.Progress.Saves.Rules;
using Spawn.Modules.Progress.Security;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress;

/// <summary>
/// Dependency injection registration extensions for <c>Spawn.Modules.Progress</c>
/// (Clean Host side: trainer catalog, clean-room discovery/download/import, A0+A audio mute,
/// pre-trainer save snapshots, Save Time Machine &amp; branching tree, Ludusavi rule resolver,
/// quiescent save watcher, 32MB SPPK cloud sync, runtime orchestration, LOOP-06 shutdown hook,
/// and Defender exclusion assistant).
/// </summary>
public static class ProgressServiceCollectionExtensions
{
    public const string CatalogHttpClientName = "Spawn.Modules.Progress.Catalog";
    public const string CloudSyncHttpClientName = "SpawnCloudSync";

    public static IServiceCollection AddSpawnProgress(
        this IServiceCollection services,
        Action<TrainerCatalogOptions>? configureCatalogOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var catalogOptions = new TrainerCatalogOptions();
        configureCatalogOptions?.Invoke(catalogOptions);
        services.TryAddSingleton(catalogOptions);

        // Honest, non-spoofed User-Agent for robots.txt-compliant discovery & Steam appdetails title resolution.
        services.AddHttpClient(CatalogHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnProgress/1.0 (+https://github.com/chialecode/spawn)");
        });

        // 1. Catalog: canonical title waterfall, clean-room FLiNG discovery, manual-redirect downloader, archive extractor
        services.TryAddSingleton<CanonicalTitleResolver>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new CanonicalTitleResolver(
                database: sp.GetRequiredService<SpawnDatabase>(),
                httpClient: factory.CreateClient(CatalogHttpClientName),
                timeProvider: sp.GetService<TimeProvider>(),
                logger: sp.GetService<ILogger<CanonicalTitleResolver>>());
        });

        services.TryAddSingleton<FlingCleanRoomDiscoveryClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new FlingCleanRoomDiscoveryClient(
                paths: sp.GetRequiredService<SpawnPaths>(),
                httpClient: factory.CreateClient(CatalogHttpClientName),
                timeProvider: sp.GetService<TimeProvider>(),
                logger: sp.GetService<ILogger<FlingCleanRoomDiscoveryClient>>());
        });

        services.TryAddSingleton<TrainerSafeDownloader>(sp =>
        {
            var opts = sp.GetRequiredService<TrainerCatalogOptions>();
            return new TrainerSafeDownloader(
                httpClient: TrainerSafeDownloader.CreateNonRedirectingHttpClient(opts.ConnectTimeout),
                options: opts,
                logger: sp.GetService<ILogger<TrainerSafeDownloader>>());
        });

        services.TryAddSingleton<IExternalSevenZipRunner>(_ => new ProcessExternalSevenZipRunner());
        services.TryAddSingleton<TrainerArchiveExtractor>(sp => new TrainerArchiveExtractor(
            options: sp.GetRequiredService<TrainerCatalogOptions>(),
            sevenZipRunner: sp.GetService<IExternalSevenZipRunner>(),
            logger: sp.GetService<ILogger<TrainerArchiveExtractor>>()));

        services.TryAddSingleton<TrainerCatalogService>();
        services.TryAddSingleton<ITrainerCatalogService>(sp => sp.GetRequiredService<TrainerCatalogService>());

        // 2. Audio: A0 pre-write + dedicated MTA WASAPI session controller
        services.TryAddSingleton<TrainerAudioController>();
        services.TryAddSingleton<ITrainerAudioController>(sp => sp.GetRequiredService<TrainerAudioController>());

        // 3. Snapshots: pre-trainer lightweight save snapshots & one-click rollback
        services.TryAddSingleton<PreTrainerSaveSnapshotService>(sp => new PreTrainerSaveSnapshotService(
            database: sp.GetRequiredService<SpawnDatabase>(),
            paths: sp.GetRequiredService<SpawnPaths>(),
            gameDataService: sp.GetRequiredService<IGameDataManagementService>(),
            gameLaunchService: sp.GetRequiredService<IGameLaunchService>(),
            timeProvider: sp.GetService<TimeProvider>(),
            logger: sp.GetService<ILogger<PreTrainerSaveSnapshotService>>()));
        services.TryAddSingleton<IPreTrainerSaveSnapshotService>(sp => sp.GetRequiredService<PreTrainerSaveSnapshotService>());

        // 4. Runtime & Worker isolation bridge (Clean Host never references Spawn.TrainerWorker)
        services.TryAddSingleton<ITrainerElevationAdvisor, TrainerElevationAdvisor>();
        services.TryAddSingleton<ITrainerWorkerLauncher>(_ => new Win32TrainerWorkerLauncher());
        services.TryAddSingleton<ITrainerPipeServerFactory>(DefaultTrainerPipeServerFactory.Instance);
        services.TryAddSingleton<ISandboxProcessScanner>(SafeSandboxProcessScanner.Instance);

        services.TryAddSingleton<ExternalTrainerBackend>(sp => new ExternalTrainerBackend(
            paths: sp.GetRequiredService<SpawnPaths>(),
            workerLauncher: sp.GetRequiredService<ITrainerWorkerLauncher>(),
            elevationAdvisor: sp.GetRequiredService<ITrainerElevationAdvisor>(),
            pipeServerFactory: sp.GetRequiredService<ITrainerPipeServerFactory>(),
            logger: sp.GetService<ILogger<ExternalTrainerBackend>>()));
        services.TryAddSingleton<ITrainerBackend>(sp => sp.GetRequiredService<ExternalTrainerBackend>());

        services.TryAddSingleton<TrainerRuntimeService>(sp => new TrainerRuntimeService(
            database: sp.GetRequiredService<SpawnDatabase>(),
            paths: sp.GetRequiredService<SpawnPaths>(),
            safetyGate: sp.GetRequiredService<ITrainerSafetyGate>(),
            catalogService: sp.GetRequiredService<ITrainerCatalogService>(),
            snapshotService: sp.GetRequiredService<IPreTrainerSaveSnapshotService>(),
            audioController: sp.GetRequiredService<ITrainerAudioController>(),
            backend: sp.GetRequiredService<ITrainerBackend>(),
            gameLaunchService: sp.GetRequiredService<IGameLaunchService>(),
            elevationAdvisor: sp.GetRequiredService<ITrainerElevationAdvisor>(),
            logger: sp.GetService<ILogger<TrainerRuntimeService>>(),
            saveTimeMachineService: sp.GetService<ISaveTimeMachineService>()));
        services.TryAddSingleton<ITrainerRuntimeService>(sp => sp.GetRequiredService<TrainerRuntimeService>());

        services.TryAddSingleton<SensitiveWorkerShutdownHook>(sp => new SensitiveWorkerShutdownHook(
            runtimeService: sp.GetRequiredService<ITrainerRuntimeService>(),
            backend: sp.GetRequiredService<ITrainerBackend>(),
            paths: sp.GetRequiredService<SpawnPaths>(),
            database: sp.GetRequiredService<SpawnDatabase>(),
            sandboxScanner: sp.GetRequiredService<ISandboxProcessScanner>(),
            logger: sp.GetService<ILogger<SensitiveWorkerShutdownHook>>()));

        // Register a deferred proxy for ISensitiveWorkerShutdownHook so GameLaunchService receives LOOP-06
        // without creating a constructor cycle (GameLaunchService -> ISensitiveWorkerShutdownHook -> TrainerRuntimeService -> IGameLaunchService).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISensitiveWorkerShutdownHook, DeferredSensitiveWorkerShutdownHook>());

        // 5. Security: Windows Defender single-folder sandbox exclusion assistant & Smart App Control diagnostics
        services.TryAddSingleton<IDefenderEnvironmentProbe>(WindowsDefenderEnvironmentProbe.Instance);
        services.TryAddSingleton<IElevatedDefenderExecutor>(sp => new WorkerElevatedDefenderExecutor(
            paths: sp.GetRequiredService<SpawnPaths>(),
            workerLauncher: sp.GetRequiredService<ITrainerWorkerLauncher>(),
            pipeServerFactory: sp.GetRequiredService<ITrainerPipeServerFactory>()));
        services.TryAddSingleton<DefenderExclusionAssistant>();
        services.TryAddSingleton<IDefenderExclusionAssistant>(sp => sp.GetRequiredService<DefenderExclusionAssistant>());

        // 6. Save Time Machine, Branching Save Tree, Ludusavi Path Resolver, Quiescent Watcher & 32MB SPPK Cloud Sync
        services.AddHttpClient(CloudSyncHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnCloudSync/1.0 (+https://github.com/chialecode/spawn)");
        });

        services.TryAddSingleton<LudusaviRuleResolver>(sp => new LudusaviRuleResolver(
            database: sp.GetRequiredService<SpawnDatabase>(),
            gameDataService: sp.GetRequiredService<IGameDataManagementService>(),
            knownFolderProvider: sp.GetService<IKnownFolderProvider>(),
            timeProvider: sp.GetService<TimeProvider>(),
            logger: sp.GetService<ILogger<LudusaviRuleResolver>>()));
        services.TryAddSingleton<ILudusaviRuleResolver>(sp => sp.GetRequiredService<LudusaviRuleResolver>());

        services.TryAddSingleton(_ => new SaveWatcherOptions());

        services.TryAddSingleton<SaveTimeMachineService>(sp => new SaveTimeMachineService(
            database: sp.GetRequiredService<SpawnDatabase>(),
            paths: sp.GetRequiredService<SpawnPaths>(),
            casStore: sp.GetRequiredService<CasChunkStore>(),
            ruleResolver: sp.GetRequiredService<ILudusaviRuleResolver>(),
            gameDataService: sp.GetRequiredService<IGameDataManagementService>(),
            gameLaunchService: sp.GetRequiredService<IGameLaunchService>(),
            eventBus: sp.GetRequiredService<ISpawnEventBus>(),
            timeProvider: sp.GetService<TimeProvider>(),
            logger: sp.GetService<ILogger<SaveTimeMachineService>>(),
            isWatchingProbe: gameId =>
            {
                ISaveWatcherService? watcher = sp.GetService<ISaveWatcherService>();
                return watcher is not null
                       && watcher.GetStatus(gameId).Status is SaveWatcherStatusKind.Watching
                           or SaveWatcherStatusKind.Debouncing
                           or SaveWatcherStatusKind.Capturing;
            }));
        services.TryAddSingleton<ISaveTimeMachineService>(sp => sp.GetRequiredService<SaveTimeMachineService>());

        services.TryAddSingleton<SaveWatcherService>(sp => new SaveWatcherService(
            timeMachine: sp.GetRequiredService<ISaveTimeMachineService>(),
            ruleResolver: sp.GetRequiredService<ILudusaviRuleResolver>(),
            eventBus: sp.GetRequiredService<ISpawnEventBus>(),
            database: sp.GetRequiredService<SpawnDatabase>(),
            timeProvider: sp.GetService<TimeProvider>(),
            options: sp.GetRequiredService<SaveWatcherOptions>(),
            logger: sp.GetService<ILogger<SaveWatcherService>>()));
        services.TryAddSingleton<ISaveWatcherService>(sp => sp.GetRequiredService<SaveWatcherService>());

        services.TryAddSingleton<CloudSaveSyncService>(sp => new CloudSaveSyncService(
            database: sp.GetRequiredService<SpawnDatabase>(),
            casChunkStore: sp.GetRequiredService<CasChunkStore>(),
            secretStore: sp.GetRequiredService<DpapiSecretStore>(),
            timeMachineService: sp.GetRequiredService<ISaveTimeMachineService>(),
            eventBus: sp.GetRequiredService<ISpawnEventBus>(),
            httpClientFactory: sp.GetService<IHttpClientFactory>(),
            timeProvider: sp.GetService<TimeProvider>(),
            logger: sp.GetService<ILogger<CloudSaveSyncService>>()));
        services.TryAddSingleton<ICloudSaveSyncService>(sp => sp.GetRequiredService<CloudSaveSyncService>());

        return services;
    }

    private sealed class DeferredSensitiveWorkerShutdownHook(IServiceProvider serviceProvider) : ISensitiveWorkerShutdownHook
    {
        public Task<SensitiveWorkerShutdownReport> ShutdownAllForProtectedLaunchAsync(
            TimeSpan budget,
            CancellationToken ct = default)
        {
            var hook = serviceProvider.GetRequiredService<SensitiveWorkerShutdownHook>();
            return hook.ShutdownAllForProtectedLaunchAsync(budget, ct);
        }
    }
}
