using System.Diagnostics;
using System.IO;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Host.Services;

/// <summary>
/// Supervises the standalone <c>Spawn.OverlayHost.exe</c> companion process so toast requests
/// automatically launch or revive the click-through overlay and register its HWND in the launcher's focus whitelist.
/// Eliminates redundant double-ping delays on cold start and preserves actionable <see cref="LastLaunchError"/> diagnostics.
/// </summary>
public sealed class OverlayHostProcessManager : IAsyncDisposable
{
    private readonly SpawnPaths _paths;
    private readonly IOverlayIpcClient _rawClient;
    private readonly Func<string?> _overlayExeResolver;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly int _maxStartupPingAttempts;
    private readonly int _startupPollIntervalMs;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly bool _isHeadless;

    private Process? _spawnedProcess;
    private nint _registeredOverlayHwnd;
    private int _registeredOverlayPid;
    private Action<nint, int>? _onOverlayWindowReady;
    private int _launchGeneration;
    private int _disposed;

    public int? ActiveOverlayProcessId => _registeredOverlayPid > 0 ? _registeredOverlayPid : null;

    /// <summary>
    /// Diagnostic reason for the most recent overlay startup failure, or <see langword="null"/> when healthy.
    /// </summary>
    public string? LastLaunchError { get; private set; }

    public OverlayHostProcessManager(SpawnPaths paths, OverlayIpcClient rawClient)
        : this(
            paths,
            (IOverlayIpcClient)rawClient,
            forceHeadlessOverride: null,
            overlayExeResolver: null,
            processStarter: null,
            maxStartupPingAttempts: 12,
            startupPollIntervalMs: 100)
    {
    }

    public static OverlayHostProcessManager CreateForTesting(
        SpawnPaths paths,
        IOverlayIpcClient rawClient,
        bool? forceHeadlessOverride = null,
        Func<string?>? overlayExeResolver = null,
        Func<ProcessStartInfo, Process?>? processStarter = null,
        int maxStartupPingAttempts = 12,
        int startupPollIntervalMs = 100) =>
        new(paths, rawClient, forceHeadlessOverride, overlayExeResolver, processStarter, maxStartupPingAttempts, startupPollIntervalMs);

    private OverlayHostProcessManager(
        SpawnPaths paths,
        IOverlayIpcClient rawClient,
        bool? forceHeadlessOverride,
        Func<string?>? overlayExeResolver,
        Func<ProcessStartInfo, Process?>? processStarter,
        int maxStartupPingAttempts,
        int startupPollIntervalMs)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(rawClient);

        _paths = paths;
        _rawClient = rawClient;
        _isHeadless = forceHeadlessOverride
                      ?? string.Equals(Environment.GetEnvironmentVariable("SPAWN_HEADLESS_TEST"), "1", StringComparison.OrdinalIgnoreCase);
        _overlayExeResolver = overlayExeResolver ?? ResolveOverlayExecutablePath;
        _processStarter = processStarter ?? Process.Start;
        _maxStartupPingAttempts = Math.Clamp(maxStartupPingAttempts, 1, 50);
        _startupPollIntervalMs = Math.Clamp(startupPollIntervalMs, 10, 1000);
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
            LastLaunchError = "当前处于无头测试模式（SPAWN_HEADLESS_TEST=1），已跳过拉起 OverlayHost。";
            return null;
        }

        int generationBeforePing = Volatile.Read(ref _launchGeneration);
        OverlayHeartbeatReply? ping = await _rawClient.PingAsync(cancellationToken).ConfigureAwait(false);
        if (ping is not null)
        {
            RecordHeartbeat(ping);
            return ping;
        }

        await _launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only re-ping inside the lock if another caller finished a launch attempt while we were waiting.
            // This eliminates the redundant back-to-back 500ms ping timeout on single-caller cold start.
            if (Volatile.Read(ref _launchGeneration) != generationBeforePing)
            {
                ping = await _rawClient.PingAsync(cancellationToken).ConfigureAwait(false);
                if (ping is not null)
                {
                    RecordHeartbeat(ping);
                    return ping;
                }
            }

            string? exePath = _overlayExeResolver();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                LastLaunchError = $"未找到 Spawn.OverlayHost.exe（已查找运行目录 {AppContext.BaseDirectory} 及 Overlay 子目录）。";
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

            _spawnedProcess?.Dispose();
            _spawnedProcess = _processStarter(psi);
            if (_spawnedProcess is null)
            {
                LastLaunchError = $"启动 {Path.GetFileName(exePath)} 失败：操作系统未返回有效进程句柄。";
                return null;
            }

            for (int attempt = 0; attempt < _maxStartupPingAttempts; attempt++)
            {
                await Task.Delay(_startupPollIntervalMs, cancellationToken).ConfigureAwait(false);

                if (TryCheckProcessExited(_spawnedProcess, out int exitCode))
                {
                    LastLaunchError = $"Spawn.OverlayHost.exe 启动后提前退出（退出码 {exitCode}）。";
                    return null;
                }

                ping = await _rawClient.PingAsync(cancellationToken).ConfigureAwait(false);
                if (ping is not null)
                {
                    RecordHeartbeat(ping);
                    return ping;
                }
            }

            LastLaunchError = $"已启动 {Path.GetFileName(exePath)}，但在等待期内未收到命名管道心跳响应。";
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastLaunchError = $"启动 OverlayHost 失败：{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            Interlocked.Increment(ref _launchGeneration);
            _launchLock.Release();
        }
    }

    private void RecordHeartbeat(OverlayHeartbeatReply reply)
    {
        LastLaunchError = null;
        _registeredOverlayPid = reply.ProcessId;
        if (reply.WindowHandle != 0 && (nint)reply.WindowHandle != _registeredOverlayHwnd)
        {
            _registeredOverlayHwnd = (nint)reply.WindowHandle;
            _onOverlayWindowReady?.Invoke(_registeredOverlayHwnd, reply.ProcessId);
        }
    }

    private static bool TryCheckProcessExited(Process proc, out int exitCode)
    {
        exitCode = 0;
        try
        {
            if (proc.HasExited)
            {
                exitCode = proc.ExitCode;
                return true;
            }
        }
        catch
        {
            // Process handle might not support exit status inspection.
        }

        return false;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
/// before dispatching achievement unlock notifications and exposing <see cref="IOverlayIpcDiagnostics"/>.
/// </summary>
public sealed class ResilientOverlayIpcClient(
    OverlayIpcClient innerClient,
    OverlayHostProcessManager processManager) : IOverlayIpcClient, IOverlayIpcDiagnostics
{
    public string PipeName => innerClient.PipeName;

    public string? LastLaunchError => processManager.LastLaunchError;

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

    public Task<bool> ClearQueueAsync(CancellationToken cancellationToken = default) =>
        innerClient.ClearQueueAsync(cancellationToken);

    public Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default) =>
        innerClient.RequestShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await processManager.DisposeAsync().ConfigureAwait(false);
        await innerClient.DisposeAsync().ConfigureAwait(false);
    }
}
