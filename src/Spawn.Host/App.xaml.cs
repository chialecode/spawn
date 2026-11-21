using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Spawn.Host.Views;
using Spawn.Modules.Achievement;
using Spawn.Modules.Launcher;

namespace Spawn.Host;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;
    private ServiceProvider? _serviceProvider;
    private TrayIconService? _trayService;
    private MainWindowViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
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

        paths.EnsureDirectoriesExist();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        services.AddSingleton(paths);
        services.AddSingleton(_singleInstance);
        services.AddSingleton<OverlayIpcClient>(sp => new OverlayIpcClient(sp.GetRequiredService<SpawnPaths>()));
        services.AddSingleton<OverlayHostProcessManager>();
        services.AddSingleton<IOverlayIpcClient, ResilientOverlayIpcClient>();

        services.AddSpawnCore(paths);
        services.AddSpawnLauncher();
        services.AddSpawnAchievement();
        services.TryAddSingleton<IGameDataManagementService, NullFallbackGameDataManagementService>();

        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        _serviceProvider = services.BuildServiceProvider();

        var db = _serviceProvider.GetRequiredService<SpawnDatabase>();
        await db.InitializeAsync().ConfigureAwait(true);

        var launchService = _serviceProvider.GetRequiredService<IGameLaunchService>();
        var overlayManager = _serviceProvider.GetRequiredService<OverlayHostProcessManager>();
        overlayManager.BindTrustedWindowCallback(launchService.RegisterTrustedOverlayWindow);

        var achievementService = _serviceProvider.GetRequiredService<IAchievementService>();
        await achievementService.StartMonitoringAsync().ConfigureAwait(true);

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
        _trayService.ExitApplicationRequested += () => _ = Dispatcher.InvokeAsync(ShutdownGracefullyAsync);
        _trayService.Initialize();

        _singleInstance.ActivateRequested += (_, _) =>
        {
            _ = Dispatcher.InvokeAsync(EnsureMainWindowShownAndActivated);
            return Task.CompletedTask;
        };

        _singleInstance.QuitForUpdateRequested += _ct =>
        {
            _ = Dispatcher.InvokeAsync(ShutdownGracefullyAsync);
            return Task.CompletedTask;
        };

        _mainViewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
        await _mainViewModel.InitializeAsync().ConfigureAwait(true);

        _ = Task.Run(() => overlayManager.EnsureOverlayRunningAsync());

        // When launched with --background (e.g. post-install tray startup), stay silent in system tray without popping up a window!
        if (!startup.Options.IsBackgroundTray)
        {
            EnsureMainWindowShownAndActivated();
        }
    }

    private void EnsureMainWindowShownAndActivated()
    {
        if (_serviceProvider is null || _mainViewModel is null || _trayService is null)
        {
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(
                _mainViewModel,
                _trayService,
                () => _ = Dispatcher.InvokeAsync(() => _ = ShutdownGracefullyAsync()));
            MainWindow = _mainWindow;
        }

        _mainWindow.BringToFrontAndActivate();
    }

    private async Task ShutdownGracefullyAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _trayService?.Dispose();

            if (_mainWindow is not null)
            {
                _mainWindow.PrepareForApplicationExit();
                _mainWindow.Close();
                _mainWindow = null;
            }

            if (_serviceProvider is not null)
            {
                var achievementService = _serviceProvider.GetService<IAchievementService>();
                if (achievementService is not null)
                {
                    await achievementService.StopMonitoringAsync().ConfigureAwait(true);
                }
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
