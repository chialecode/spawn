using System.Windows;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.OverlayHost.Audio;
using Spawn.OverlayHost.Services;
using Spawn.OverlayHost.Views;

namespace Spawn.OverlayHost;

/// <summary>
/// Standalone zero-injection, zero-focus-stealing overlay host process listening on SPAWN's ACL-protected Named Pipe.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private SynthesizedUnlockSoundPlayer? _soundPlayer;
    private OverlayToastWindow? _toastWindow;
    private OverlayToastQueueCoordinator? _queueCoordinator;
    private OverlayIpcServer? _ipcServer;
    private CancellationTokenSource? _parentWatchCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = ParseCommandLine(e.Args);
        var paths = SpawnPaths.Resolve();
        string pipeName = !string.IsNullOrWhiteSpace(options.PipeName)
            ? options.PipeName!
            : paths.OverlayPipeName;

        string mutexName = $@"Local\spawn-overlay-instance-{SanitizeMutexSegment(pipeName)}";
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        bool isHeadless = options.Headless
                          || string.Equals(Environment.GetEnvironmentVariable("SPAWN_HEADLESS_TEST"), "1", StringComparison.OrdinalIgnoreCase);

        _soundPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: isHeadless);

        if (!isHeadless)
        {
            _toastWindow = new OverlayToastWindow(_soundPlayer);
            _toastWindow.EnsureHandleCreatedHidden();
        }

        _queueCoordinator = new OverlayToastQueueCoordinator(async (toast, ct) =>
        {
            if (_toastWindow is not null)
            {
                await _toastWindow.PlayToastAsync(toast, ct).ConfigureAwait(false);
            }
        });

        _ipcServer = new OverlayIpcServer(pipeName)
        {
            HeartbeatProvider = () => new OverlayHeartbeatReply
            {
                ProcessId = Environment.ProcessId,
                WindowHandle = _toastWindow?.WindowHandle ?? 0,
                ServerTimeUtc = DateTimeOffset.UtcNow,
                PendingToastCount = _queueCoordinator.PendingToastCount,
            },
        };

        _ipcServer.ToastReceived += (toast, _) =>
        {
            _queueCoordinator.Enqueue(toast);
            return Task.CompletedTask;
        };

        _ipcServer.ToastBatchReceived += (batch, _) =>
        {
            _queueCoordinator.EnqueueBatch(batch);
            return Task.CompletedTask;
        };

        _ipcServer.ClearQueueRequested += _ =>
        {
            _queueCoordinator.ClearQueue();
            return Task.CompletedTask;
        };

        _ipcServer.ShutdownRequested += _ct =>
        {
            _ = Dispatcher.InvokeAsync(() => Shutdown(0));
            return Task.CompletedTask;
        };

        try
        {
            await _ipcServer.StartAsync().ConfigureAwait(true);
        }
        catch
        {
            Shutdown(1);
            return;
        }

        if (options.ParentProcessId is > 4)
        {
            int parentPid = options.ParentProcessId.Value;
            _parentWatchCts = new CancellationTokenSource();
            _ = MonitorParentProcessAsync(parentPid, _parentWatchCts.Token);
        }
    }

    private async Task MonitorParentProcessAsync(int parentPid, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!SafeProcessInspector.IsProcessAlive(parentPid))
                {
                    await Dispatcher.InvokeAsync(() => Shutdown(0));
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit.
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_parentWatchCts is not null)
            {
                await _parentWatchCts.CancelAsync().ConfigureAwait(false);
                _parentWatchCts.Dispose();
            }

            if (_ipcServer is not null)
            {
                await _ipcServer.DisposeAsync().ConfigureAwait(false);
            }

            if (_queueCoordinator is not null)
            {
                await _queueCoordinator.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore shutdown races.
        }

        _soundPlayer?.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // Ignore mutex ownership edge cases.
        }

        base.OnExit(e);
    }

    private static OverlayLaunchOptions ParseCommandLine(string[] args)
    {
        string? pipe = null;
        int? parentPid = null;
        bool headless = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pipe = args[++i];
            }
            else if (string.Equals(arg, "--parent-pid", StringComparison.OrdinalIgnoreCase)
                     && i + 1 < args.Length
                     && int.TryParse(args[++i], out int parsedPid))
            {
                parentPid = parsedPid;
            }
            else if (string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase))
            {
                headless = true;
            }
        }

        return new OverlayLaunchOptions(pipe, parentPid, headless);
    }

    private static string SanitizeMutexSegment(string raw)
    {
        char[] chars = raw.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private sealed record OverlayLaunchOptions(
        string? PipeName,
        int? ParentProcessId,
        bool Headless);
}
