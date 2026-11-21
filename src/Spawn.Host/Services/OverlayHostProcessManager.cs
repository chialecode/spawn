using System.Diagnostics;
using System.IO;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Host.Services;

/// <summary>
/// Supervises the standalone <c>Spawn.OverlayHost.exe</c> companion process so toast requests
/// automatically launch or revive the click-through overlay and register its HWND in the launcher's focus whitelist.
/// </summary>
public sealed class OverlayHostProcessManager : IAsyncDisposable
{
    private readonly SpawnPaths _paths;
    private readonly OverlayIpcClient _rawClient;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly bool _isHeadless;
    private Process? _spawnedProcess;
    private nint _registeredOverlayHwnd;
    private int _registeredOverlayPid;
    private Action<nint, int>? _onOverlayWindowReady;

    public int? ActiveOverlayProcessId => _registeredOverlayPid > 0 ? _registeredOverlayPid : null;

    public OverlayHostProcessManager(SpawnPaths paths, OverlayIpcClient rawClient)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(rawClient);
        _paths = paths;
        _rawClient = rawClient;
        _isHeadless = string.Equals(Environment.GetEnvironmentVariable("SPAWN_HEADLESS_TEST"), "1", StringComparison.OrdinalIgnoreCase);
    }

    public void BindTrustedWindowCallback(Action<nint, int> registerCallback)
    {
        _onOverlayWindowReady = registerCallback;
        if (_registeredOverlayHwnd != nint.Zero && _registeredOverlayPid > 0)
        {
            registerCallback(_registeredOverlayHwnd, _registeredOverlayPid);
        }
    }

    public async Task<OverlayHeartbeatReply?> EnsureOverlayRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_isHeadless)
        {
            return null;
        }

        var ping = await _rawClient.PingAsync(cancellationToken).ConfigureAwait(false);
        if (ping is not null)
        {
            RecordHeartbeat(ping);
            return ping;
        }

        await _launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ping = await _rawClient.PingAsync(cancellationToken).ConfigureAwait(false);
            if (ping is not null)
            {
                RecordHeartbeat(ping);
                return ping;
            }

            string? exePath = ResolveOverlayExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--pipe");
            psi.ArgumentList.Add(_paths.OverlayPipeName);
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            _spawnedProcess = Process.Start(psi);

            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                ping = await _rawClient.PingAsync(cancellationToken).ConfigureAwait(false);
                if (ping is not null)
                {
                    RecordHeartbeat(ping);
                    return ping;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    private void RecordHeartbeat(OverlayHeartbeatReply reply)
    {
        _registeredOverlayPid = reply.ProcessId;
        if (reply.WindowHandle != 0 && (nint)reply.WindowHandle != _registeredOverlayHwnd)
        {
            _registeredOverlayHwnd = (nint)reply.WindowHandle;
            _onOverlayWindowReady?.Invoke(_registeredOverlayHwnd, reply.ProcessId);
        }
    }

    private static string? ResolveOverlayExecutablePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string direct = Path.Combine(baseDir, "Spawn.OverlayHost.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        string installedSubdir = Path.Combine(baseDir, "Overlay", "Spawn.OverlayHost.exe");
        if (File.Exists(installedSubdir))
        {
            return installedSubdir;
        }

        // Development sibling project output fallback (e.g. dotnet run --project src/Spawn.Host)
        try
        {
            var dir = new DirectoryInfo(baseDir);
            string tfm = dir.Name;
            string config = dir.Parent?.Name ?? "Debug";
            var srcDir = dir.Parent?.Parent?.Parent?.Parent;
            if (srcDir is not null)
            {
                string sibling = Path.Combine(srcDir.FullName, "Spawn.OverlayHost", "bin", config, tfm, "Spawn.OverlayHost.exe");
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }
        }
        catch
        {
            // Ignore directory walk edge cases.
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            _ = await _rawClient.RequestShutdownAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignore IPC close race.
        }

        _spawnedProcess?.Dispose();
        _launchLock.Dispose();
    }
}

/// <summary>
/// Decorator over <see cref="OverlayIpcClient"/> ensuring <c>Spawn.OverlayHost.exe</c> is automatically started
/// before dispatching achievement unlock notifications.
/// </summary>
public sealed class ResilientOverlayIpcClient(
    OverlayIpcClient innerClient,
    OverlayHostProcessManager processManager) : IOverlayIpcClient
{
    public string PipeName => innerClient.PipeName;

    public async Task<bool> SendToastAsync(OverlayToastRequest request, CancellationToken cancellationToken = default)
    {
        _ = await processManager.EnsureOverlayRunningAsync(cancellationToken).ConfigureAwait(false);
        return await innerClient.SendToastAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> SendToastBatchAsync(IReadOnlyList<OverlayToastRequest> requests, CancellationToken cancellationToken = default)
    {
        _ = await processManager.EnsureOverlayRunningAsync(cancellationToken).ConfigureAwait(false);
        return await innerClient.SendToastBatchAsync(requests, cancellationToken).ConfigureAwait(false);
    }

    public Task<OverlayHeartbeatReply?> PingAsync(CancellationToken cancellationToken = default) =>
        innerClient.PingAsync(cancellationToken);

    public Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default) =>
        innerClient.RequestShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await processManager.DisposeAsync().ConfigureAwait(false);
        await innerClient.DisposeAsync().ConfigureAwait(false);
    }
}
