using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Update;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Spawn.Host.ViewModels.Saves;
using Spawn.Host.Views;
using Spawn.Modules.Achievement;
using Spawn.Modules.Launcher;
using Spawn.Modules.Progress;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Host;

public partial class App : Application
{
    private static readonly TimeSpan GracefulStopServiceTimeout = TimeSpan.FromSeconds(20);

    private SpawnPaths? _paths;
    private SingleInstanceCoordinator? _singleInstance;
    private ServiceProvider? _serviceProvider;
    private TrayIconService? _trayService;
    private MainWindowViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private StartupWindowDecision? _startupDecision;
    private int _shutdownStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var cliOptions = SpawnCommandLineOptions.Parse(e.Args);
        var paths = new SpawnPaths(explicitIdentity: cliOptions.ExplicitIdentity);

        _singleInstance = new SingleInstanceCoordinator(paths);
        SingleInstanceStartupResult startup = await _singleInstance.InitializeAsync(e.Args).ConfigureAwait(true);
        if (startup.Action != SingleInstanceStartupAction.RunAsPrimary)
        {
            await _singleInstance.DisposeAsync().ConfigureAwait(true);
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        _paths = paths;
        paths.EnsureDirectoriesExist();

        bool launchedAfterUpdate = startup.Options.RawArgs.Any(
            a => string.Equals(a?.Trim(), "--after-update", StringComparison.OrdinalIgnoreCase));

        _startupDecision = UpdateRestartStateStore.ConsumeAndDecide(
            paths.UpdateRestartStateFilePath,
            startup.Options,
            DateTimeOffset.UtcNow);

        SessionEnding += (_, _) => _ = Dispatcher.InvokeAsync(() => ShutdownGracefullyAsync());

        var services = new ServiceCollection();
        RegisterHostCoreAndModuleServices(services, paths, _singleInstance);

        services.Replace(ServiceDescriptor.Singleton<IUpdateIdleEvaluator>(sp =>
            new HostUpdateIdleEvaluator(
                sp.GetRequiredService<IGameLaunchService>(),
                sp.GetRequiredService<ITrainerRuntimeService>(),
                sp.GetRequiredService<UpdateActivityTracker>(),
                () => IsMainWindowCurrentlyOpen())));
        services.Replace(ServiceDescriptor.Singleton<IUpdateQuitCoordinator>(_ =>
            new HostUpdateQuitCoordinator(this)));

        services.AddSingleton<TrayIconService>();

        _serviceProvider = services.BuildServiceProvider();

        var db = _serviceProvider.GetRequiredService<SpawnDatabase>();
        await db.InitializeAsync().ConfigureAwait(true);

        var launchService = _serviceProvider.GetRequiredService<IGameLaunchService>();
        var overlayManager = _serviceProvider.GetRequiredService<OverlayHostProcessManager>();
        overlayManager.BindTrustedWindowCallback(launchService.RegisterTrustedOverlayWindow);

        var achievementService = _serviceProvider.GetRequiredService<IAchievementService>();
        await achievementService.StartMonitoringAsync().ConfigureAwait(true);

        var saveWatcher = _serviceProvider.GetService<ISaveWatcherService>();
        if (saveWatcher is not null)
        {
            await saveWatcher.StartAsync().ConfigureAwait(true);
        }

        var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
        bool consumedValidUpdateMarker = _startupDecision is { ConsumedValidRestartMarker: true }
            && string.Equals(_startupDecision.ConsumedRestartReason, UpdateRestartReason.Update, StringComparison.OrdinalIgnoreCase);
        await updateService.InitializeAsync(
            launchedAfterUpdate: launchedAfterUpdate,
            consumedValidUpdateMarker: consumedValidUpdateMarker,
            previousVersionFromMarker: _startupDecision?.PreviousVersion).ConfigureAwait(true);

        _trayService = _serviceProvider.GetRequiredService<TrayIconService>();
        _trayService.OpenMainWindowRequested += () => _ = Dispatcher.InvokeAsync(EnsureMainWindowShownAndActivated);
        _trayService.TestToastRequested += () =>
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (_mainViewModel is not null)
                {
                    await _mainViewModel.Settings.TriggerTestToastCommand.ExecuteAsync(null).ConfigureAwait(true);
                }
            });
        };
        _trayService.RestartForUpdateRequested += () =>
            _ = Task.Run(() => updateService.InstallReadyUpdateAsync(UpdateTriggerKind.Manual));
        _trayService.OpenUpdateSettingsRequested += () => _ = Dispatcher.InvokeAsync(OpenUpdateSettingsWindow);
        _trayService.ExitApplicationRequested += () => _ = Dispatcher.InvokeAsync(() => ShutdownGracefullyAsync());
        _trayService.Initialize();
        _trayService.SetReadyUpdateVersion(updateService.CurrentSnapshot.ReadyUpdate?.Version);

        updateService.StateChanged += (_, snap) =>
            _ = Dispatcher.InvokeAsync(() => _trayService?.SetReadyUpdateVersion(snap.ReadyUpdate?.Version));
        updateService.UserNotificationRequested += (_, ev) =>
            _ = Dispatcher.InvokeAsync(() => _trayService?.ShowNotification(ev.Title, ev.Message, ev.IsError));

        // Re-evaluate silent idle auto-install whenever tracked games, trainers, or save/cleanup operations become idle.
        var trainerRuntime = _serviceProvider.GetRequiredService<ITrainerRuntimeService>();
        var trainerSafetyGate = _serviceProvider.GetRequiredService<ITrainerSafetyGate>();
        var activityTracker = _serviceProvider.GetRequiredService<UpdateActivityTracker>();
        long lastEmergencyTrayNotifyTicks = 0;

        void NotifySafetyEmergencySilent(string title, string body)
        {
            long nowTicks = Environment.TickCount64;
            long prevTicks = Interlocked.Read(ref lastEmergencyTrayNotifyTicks);
            if (nowTicks - prevTicks < 1500)
            {
                return;
            }

            Interlocked.Exchange(ref lastEmergencyTrayNotifyTicks, nowTicks);
            _ = Dispatcher.InvokeAsync(() => _trayService?.ShowNotification(title, body, isError: true));
        }

        launchService.SessionChanged += (_, _) => TriggerIdleAutoInstallCheck(updateService);
        trainerRuntime.StateChanged += (_, ev) =>
        {
            TriggerIdleAutoInstallCheck(updateService);
            if (ev.CurrentState == TrainerRuntimeState.EmergencyStopped)
            {
                string detail = string.IsNullOrWhiteSpace(ev.Message)
                    ? "已自动紧急停止修改器与隔离工作进程，确保受反作弊保护的游戏运行安全。"
                    : ev.Message!;
                NotifySafetyEmergencySilent("SPAWN 安全守卫急停", detail);
            }
        };
        trainerSafetyGate.EmergencyStopTriggered += (_, ev) =>
        {
            string detail = string.IsNullOrWhiteSpace(ev.Reason)
                ? "反作弊安全看门狗检测到受保护游戏或反作弊进程，已立即中止修改器。"
                : ev.Reason;
            NotifySafetyEmergencySilent("SPAWN 反作弊看门狗急停", detail);
        };
        activityTracker.ActivityChanged += () => TriggerIdleAutoInstallCheck(updateService);

        _singleInstance.ActivateRequested += (incomingArgs, _ct) =>
        {
            var incoming = SpawnCommandLineOptions.Parse(incomingArgs);
            SecondInstanceAction action = StartupWindowPolicy.DecideSecondInstanceAction(
                incoming,
                isQuitting: Volatile.Read(ref _shutdownStarted) != 0);

            if (action == SecondInstanceAction.QuitForUpdate)
            {
                string reason = UpdateRestartReason.Normalize(incoming.RestartReason);
                _ = Dispatcher.InvokeAsync(() => RequestQuitForUpdateAsync(reason));
            }
            else if (action == SecondInstanceAction.ActivateWindow)
            {
                _ = Dispatcher.InvokeAsync(EnsureMainWindowShownAndActivated);
            }

            return Task.CompletedTask;
        };

        _singleInstance.QuitForUpdateRequested += _ct =>
        {
            string reason = _singleInstance?.LastQuitForUpdateReason ?? UpdateRestartReason.DevInstall;
            _ = Dispatcher.InvokeAsync(() => RequestQuitForUpdateAsync(reason));
            return Task.CompletedTask;
        };

        _mainViewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
        await _mainViewModel.InitializeAsync().ConfigureAwait(true);

        _ = Task.Run(() => overlayManager.EnsureOverlayRunningAsync());

        // Apply startup window decision (respecting --background, --after-update, 5-minute update restart marker,
        // and graceful fallback to showing the window if tray creation failed in an interactive session).
        EffectiveStartupWindowResult effectiveWindow = StartupWindowPolicy.ResolveEffectiveOpenWindow(
            _startupDecision ?? new StartupWindowDecision(!startup.Options.IsBackgroundTray, false, null, null, null),
            trayActive: _trayService.IsActive,
            isHeadlessTest: StartupWindowPolicy.IsHeadlessTestEnvironment(SpawnBuildInfo.IsReleaseChannel));
        if (effectiveWindow.OpenWindow)
        {
            EnsureMainWindowShownAndActivated();
        }

        // Release-only background scheduler (strictly no-op on dev/source channels).
        updateService.StartBackgroundScheduler();
    }

    internal Task RequestQuitForUpdateAsync(string reason = UpdateRestartReason.DevInstall) =>
        ShutdownGracefullyAsync(recordRestartReason: reason);

    private bool IsMainWindowCurrentlyOpen()
    {
        if (Dispatcher.CheckAccess())
        {
            return _mainWindow is not null && _mainWindow.IsVisible;
        }

        try
        {
            return Dispatcher.Invoke(() => _mainWindow is not null && _mainWindow.IsVisible);
        }
        catch
        {
            return true;
        }
    }

    private void TriggerIdleAutoInstallCheck(IUpdateService updateService)
    {
        if (Volatile.Read(ref _shutdownStarted) != 0
            || updateService.CurrentSnapshot.ReadyUpdate is null
            || !updateService.CurrentSnapshot.Settings.AutoInstallWhenIdle)
        {
            return;
        }

        _ = Task.Run(() => updateService.TryAutoInstallIfEligibleAsync());
    }

    private void OpenUpdateSettingsWindow()
    {
        EnsureMainWindowShownAndActivated();
        if (_mainViewModel is not null)
        {
            _mainViewModel.ActiveSection = PrimaryNavSection.Settings;
            _mainViewModel.Settings.SelectedCategory = SettingsCategory.Updates;
        }
    }

    private void EnsureMainWindowShownAndActivated()
    {
        if (Volatile.Read(ref _shutdownStarted) != 0
            || _serviceProvider is null
            || _mainViewModel is null
            || _trayService is null)
        {
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(
                _mainViewModel,
                _trayService,
                () => _ = Dispatcher.InvokeAsync(() => _ = ShutdownGracefullyAsync()));
            _mainWindow.HiddenToTray += () =>
            {
                var updateSvc = _serviceProvider?.GetService<IUpdateService>();
                if (updateSvc is not null)
                {
                    TriggerIdleAutoInstallCheck(updateSvc);
                }
            };
            MainWindow = _mainWindow;
        }

        _mainWindow.BringToFrontAndActivate();
    }

    private async Task PrepareGracefulShutdownForUpdateAsync(string? recordRestartReason)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        bool wasWindowOpen = _mainWindow is not null && _mainWindow.IsVisible;

        if (!string.IsNullOrWhiteSpace(recordRestartReason) && _paths is not null)
        {
            try
            {
                var marker = new UpdateRestartMarker(
                    Reason: UpdateRestartReason.Normalize(recordRestartReason),
                    WindowOpen: wasWindowOpen,
                    FromVersion: SpawnBuildInfo.Version,
                    AtUtc: DateTimeOffset.UtcNow);
                UpdateRestartStateStore.SaveMarker(_paths.UpdateRestartStateFilePath, marker);
            }
            catch
            {
                // Best-effort marker write must never block graceful update exit.
            }
        }

        if (_serviceProvider is not null)
        {
            var updateService = _serviceProvider.GetService<IUpdateService>();
            if (updateService is not null)
            {
                try
                {
                    await updateService.StopBackgroundSchedulerAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(true);
                }
                catch
                {
                    // Ignore scheduler stop timeout.
                }
            }

            var trainerRuntime = _serviceProvider.GetService<ITrainerRuntimeService>();
            if (trainerRuntime is not null)
            {
                try
                {
                    await trainerRuntime.StopAllAsync(TrainerShutdownReason.HostShutdownOrUpdate)
                        .WaitAsync(GracefulStopServiceTimeout)
                        .ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    // Bounded exit: never hang --quit-for-update forever.
                }
            }

            var saveWatcher = _serviceProvider.GetService<ISaveWatcherService>();
            if (saveWatcher is not null)
            {
                try
                {
                    await saveWatcher.StopAsync()
                        .WaitAsync(GracefulStopServiceTimeout)
                        .ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    // Bounded exit: never hang --quit-for-update forever.
                }
            }

            var achievementService = _serviceProvider.GetService<IAchievementService>();
            if (achievementService is not null)
            {
                try
                {
                    await achievementService.StopMonitoringAsync()
                        .WaitAsync(GracefulStopServiceTimeout)
                        .ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    // Bounded exit: never hang --quit-for-update forever.
                }
            }

            var overlayClient = _serviceProvider.GetService<OverlayIpcClient>();
            if (overlayClient is not null)
            {
                try
                {
                    await overlayClient.RequestShutdownAsync()
                        .WaitAsync(TimeSpan.FromSeconds(3))
                        .ConfigureAwait(true);
                }
                catch
                {
                    // Overlay may not be running.
                }
            }
        }

        // Keep _singleInstance mutex and control pipe alive until CompleteDeferredShutdownCoreAsync()
        // so that AbortDeferredShutdownCoreAsync() leaves single-instance coordination intact if installer
        // launch fails, while _shutdownStarted == 1 already guards against duplicate quit handshakes.
    }

    private async Task CompleteDeferredShutdownCoreAsync()
    {
        try
        {
            _trayService?.Dispose();

            if (_mainWindow is not null)
            {
                _mainWindow.PrepareForApplicationExit();
                _mainWindow.Close();
                _mainWindow = null;
            }

            _mainViewModel?.Dispose();
            _mainViewModel = null;

            if (_serviceProvider is not null)
            {
                await _serviceProvider.DisposeAsync().ConfigureAwait(true);
                _serviceProvider = null;
            }

            if (_singleInstance is not null)
            {
                await _singleInstance.DisposeAsync().ConfigureAwait(true);
                _singleInstance = null;
            }
        }
        catch
        {
            // Ignore shutdown races.
        }
        finally
        {
            Shutdown(0);
        }
    }

    private async Task AbortDeferredShutdownCoreAsync()
    {
        try
        {
            if (_paths is not null)
            {
                _ = UpdateRestartStateStore.TryReadAndDeleteMarker(_paths.UpdateRestartStateFilePath);
            }

            Volatile.Write(ref _shutdownStarted, 0);

            if (_serviceProvider is not null)
            {
                var achievementService = _serviceProvider.GetService<IAchievementService>();
                if (achievementService is not null)
                {
                    await achievementService.StartMonitoringAsync().ConfigureAwait(true);
                }

                var saveWatcher = _serviceProvider.GetService<ISaveWatcherService>();
                if (saveWatcher is not null)
                {
                    await saveWatcher.StartAsync().ConfigureAwait(true);
                }

                var updateService = _serviceProvider.GetService<IUpdateService>();
                updateService?.StartBackgroundScheduler();
            }
        }
        catch
        {
            // Best-effort recovery after failed installer launch.
        }
    }

    private async Task ShutdownGracefullyAsync(string? recordRestartReason = null)
    {
        await PrepareGracefulShutdownForUpdateAsync(recordRestartReason).ConfigureAwait(true);
        await CompleteDeferredShutdownCoreAsync().ConfigureAwait(true);
    }

    private sealed class HostUpdateIdleEvaluator(
        IGameLaunchService launchService,
        ITrainerRuntimeService trainerRuntime,
        UpdateActivityTracker activityTracker,
        Func<bool> isMainWindowOpen) : IUpdateIdleEvaluator
    {
        public UpdateIdleSnapshot Evaluate()
        {
            bool hasRunningGame = launchService.ActiveSessions.Count > 0;
            bool hasActiveTrainer = trainerRuntime.ActiveSession is not null
                || trainerRuntime.State is TrainerRuntimeState.Launching
                    or TrainerRuntimeState.Running
                    or TrainerRuntimeState.Stopping;

            return new UpdateIdleSnapshot
            {
                HasRunningTrackedGame = hasRunningGame,
                HasActiveTrainerSession = hasActiveTrainer,
                HasActiveSaveSnapshotOrRollback = activityTracker.IsSaveSnapshotOrRollbackInProgress,
                HasActiveGameDataCleanup = activityTracker.IsGameDataCleanupInProgress,
                IsMainWindowOpen = isMainWindowOpen(),
            };
        }
    }

    private sealed class HostUpdateQuitCoordinator(App app) : IUpdateQuitCoordinator
    {
        public Task RequestQuitForUpdateAsync(string reason, CancellationToken cancellationToken = default) =>
            app.Dispatcher.InvokeAsync(() => app.PrepareGracefulShutdownForUpdateAsync(reason)).Task.Unwrap();

        public void CompleteDeferredShutdown() =>
            _ = app.Dispatcher.InvokeAsync(() => app.CompleteDeferredShutdownCoreAsync());

        public Task AbortDeferredShutdownAsync(CancellationToken cancellationToken = default) =>
            app.Dispatcher.InvokeAsync(() => app.AbortDeferredShutdownCoreAsync()).Task.Unwrap();
    }

    /// <summary>
    /// Registers core, module, activity-tracking decorator, and ViewModel services shared by the live
    /// WPF <see cref="App"/> host and headless DI composition tests.
    /// </summary>
    public static void RegisterHostCoreAndModuleServices(
        IServiceCollection services,
        SpawnPaths paths,
        SingleInstanceCoordinator? singleInstance = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        services.AddSingleton(paths);
        if (singleInstance is not null)
        {
            services.AddSingleton(singleInstance);
        }
        else
        {
            services.AddSingleton(sp => new SingleInstanceCoordinator(sp.GetRequiredService<SpawnPaths>()));
        }

        services.AddSingleton<OverlayIpcClient>(sp => new OverlayIpcClient(sp.GetRequiredService<SpawnPaths>()));
        services.AddSingleton<OverlayHostProcessManager>();
        services.AddSingleton<IOverlayIpcClient, ResilientOverlayIpcClient>();

        services.AddSpawnCore(paths);
        services.AddSpawnLauncher();
        services.AddSpawnAchievement();
        services.AddSpawnProgress();
        services.TryAddSingleton<IGameDataManagementService, NullFallbackGameDataManagementService>();

        // Decorate game-data cleanup and pre-trainer snapshot services so UpdateIdleEvaluator sees live busy activity.
        services.Replace(ServiceDescriptor.Singleton<IGameDataManagementService>(sp =>
            new ActivityTrackingGameDataManagementService(
                sp.GetRequiredService<GameDataManagementService>(),
                sp.GetRequiredService<UpdateActivityTracker>())));
        services.Replace(ServiceDescriptor.Singleton<IPreTrainerSaveSnapshotService>(sp =>
            new ActivityTrackingPreTrainerSnapshotService(
                sp.GetRequiredService<PreTrainerSaveSnapshotService>(),
                sp.GetRequiredService<UpdateActivityTracker>())));

        DecorateSingletonIfRegistered<ISaveTimeMachineService>(
            services,
            (inner, sp) => new ActivityTrackingSaveTimeMachineService(inner, sp.GetRequiredService<UpdateActivityTracker>()));
        DecorateSingletonIfRegistered<ICloudSaveSyncService>(
            services,
            (inner, sp) => new ActivityTrackingCloudSaveSyncService(inner, sp.GetRequiredService<UpdateActivityTracker>()));

        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TrainerWorkbenchViewModel>();
        services.AddSingleton<SaveWorkbenchViewModel>();
        services.AddSingleton<MainWindowViewModel>();
    }

    private static void DecorateSingletonIfRegistered<TService>(
        IServiceCollection services,
        Func<TService, IServiceProvider, TService> decoratorFactory)
        where TService : class
    {
        ServiceDescriptor? existing = services.LastOrDefault(d => d.ServiceType == typeof(TService));
        if (existing is null)
        {
            return;
        }

        services.Remove(existing);
        services.AddSingleton<TService>(sp =>
        {
            TService inner = existing.ImplementationInstance is TService inst
                ? inst
                : existing.ImplementationFactory is not null
                    ? (TService)existing.ImplementationFactory(sp)
                    : (TService)ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!);
            return decoratorFactory(inner, sp);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _trayService?.Dispose();
            _singleInstance?.Dispose();
        }
        catch
        {
            // Ignore final cleanup race.
        }

        base.OnExit(e);
    }
}
