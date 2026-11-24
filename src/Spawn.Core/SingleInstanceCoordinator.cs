using System.Runtime.InteropServices;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Parsed startup arguments relevant to single-instance coordination, background tray mode, and installer handshakes.
/// </summary>
public sealed record SpawnCommandLineOptions(
    bool IsQuitForUpdate,
    bool IsBackgroundTray,
    bool IsActivate,
    string? ExplicitIdentity,
    IReadOnlyList<string> RawArgs,
    string? RestartReason = null)
{
    public static SpawnCommandLineOptions Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new SpawnCommandLineOptions(false, false, false, null, [], null);
        }

        bool quitForUpdate = false;
        bool background = false;
        bool activate = false;
        string? identity = null;
        string? restartReason = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i]?.Trim() ?? string.Empty;
            if (string.Equals(arg, "--quit-for-update", StringComparison.OrdinalIgnoreCase))
            {
                quitForUpdate = true;
            }
            else if (string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase))
            {
                background = true;
            }
            else if (string.Equals(arg, "--activate", StringComparison.OrdinalIgnoreCase))
            {
                activate = true;
            }
            else if (string.Equals(arg, "--identity", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                identity = args[++i]?.Trim();
            }
            else if (arg.StartsWith("--identity=", StringComparison.OrdinalIgnoreCase))
            {
                identity = arg["--identity=".Length..].Trim();
            }
            else if (string.Equals(arg, "--restart-reason", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                restartReason = args[++i]?.Trim();
            }
            else if (arg.StartsWith("--restart-reason=", StringComparison.OrdinalIgnoreCase))
            {
                restartReason = arg["--restart-reason=".Length..].Trim();
            }
        }

        return new SpawnCommandLineOptions(quitForUpdate, background, activate, identity, args, restartReason);
    }
}

/// <summary>
/// Action that <c>Spawn.Host</c> should take after running <see cref="SingleInstanceCoordinator.InitializeAsync"/>.
/// </summary>
public enum SingleInstanceStartupAction
{
    /// <summary>This process owns the primary mutex and control pipe; continue normal startup.</summary>
    RunAsPrimary = 0,

    /// <summary>A primary instance is already running and received our activation signal (or background no-op); exit cleanly with code 0.</summary>
    ExitSecondaryInstance = 1,

    /// <summary><c>--quit-for-update</c> handshake completed; exit cleanly with code 0.</summary>
    ExitAfterQuitForUpdate = 2,
}

/// <summary>
/// Outcome of <see cref="SingleInstanceCoordinator.InitializeAsync"/>.
/// </summary>
public sealed record SingleInstanceStartupResult(
    SingleInstanceStartupAction Action,
    bool WasPrimaryRunning,
    bool HandshakeDelivered,
    SpawnCommandLineOptions Options);

/// <summary>
/// Coordinates per-identity single-instance ownership via a session-scoped Windows Mutex (<c>&lt;Identity&gt;-instance</c>)
/// and an SDDL-protected Named Pipe JSON-RPC control server (<c>&lt;Identity&gt;-control</c>).
/// Supports secondary window activation (<c>--activate</c>) and installer graceful shutdown (<c>--quit-for-update</c>)
/// without force-killing processes or disturbing running games.
/// </summary>
public sealed partial class SingleInstanceCoordinator : IAsyncDisposable, IDisposable
{
    public const string MethodActivate = "instance.activate";
    public const string MethodQuitForUpdate = "instance.quitForUpdate";
    public const string MethodPing = "instance.ping";
    private const uint AsfwAny = 0xFFFFFFFF;

    private readonly ManualResetEventSlim _releaseMutexSignal = new(false);
    private readonly TaskCompletionSource _mutexReleasedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeJsonRpcServer? _controlServer;
    private Thread? _mutexOwnerThread;
    private volatile bool _ownsPrimaryMutex;
    private volatile string _lastQuitForUpdateReason = UpdateRestartReason.DevInstall;
    private int _disposed;

    public string Identity { get; }
    public string MutexName => $"{Identity}-instance";
    public string ControlPipeName => $"{Identity}-control";
    public bool IsPrimaryInstance => _ownsPrimaryMutex;
    public string LastQuitForUpdateReason => _lastQuitForUpdateReason;

    public event Func<IReadOnlyList<string>, CancellationToken, Task>? ActivateRequested;
    public event Func<CancellationToken, Task>? QuitForUpdateRequested;
    public event Func<string, CancellationToken, Task>? QuitForUpdateWithReasonRequested;

    public SingleInstanceCoordinator(SpawnPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).Identity)
    {
    }

    public SingleInstanceCoordinator(string? identity = null)
    {
        string resolved = string.IsNullOrWhiteSpace(identity)
            ? SpawnBuildInfo.Identity
            : identity.Trim();
        Identity = SanitizeIdentityToken(resolved);
    }

    /// <summary>
    /// Evaluates startup arguments, performs <c>--quit-for-update</c> or secondary activation forwarding if needed,
    /// or acquires the primary <c>&lt;Identity&gt;-instance</c> mutex and starts the <c>&lt;Identity&gt;-control</c> pipe server.
    /// </summary>
    public async Task<SingleInstanceStartupResult> InitializeAsync(
        IReadOnlyList<string>? args = null,
        TimeSpan? quitWaitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var options = SpawnCommandLineOptions.Parse(args);

        if (options.IsQuitForUpdate)
        {
            var (wasRunning, exitedCleanly) = await SignalQuitForUpdateAsync(
                Identity,
                quitWaitTimeout ?? TimeSpan.FromSeconds(25),
                options.RestartReason,
                cancellationToken).ConfigureAwait(false);

            return new SingleInstanceStartupResult(
                SingleInstanceStartupAction.ExitAfterQuitForUpdate,
                WasPrimaryRunning: wasRunning,
                HandshakeDelivered: !wasRunning || exitedCleanly,
                Options: options);
        }

        bool acquired = await TryAcquireMutexOnDedicatedThreadAsync(cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            bool forwarded = true;
            if (!options.IsBackgroundTray || options.IsActivate)
            {
                forwarded = await ForwardActivateToPrimaryAsync(ControlPipeName, options.RawArgs, cancellationToken).ConfigureAwait(false);
            }

            return new SingleInstanceStartupResult(
                SingleInstanceStartupAction.ExitSecondaryInstance,
                WasPrimaryRunning: true,
                HandshakeDelivered: forwarded,
                Options: options);
        }

        await StartControlServerAsync(cancellationToken).ConfigureAwait(false);
        return new SingleInstanceStartupResult(
            SingleInstanceStartupAction.RunAsPrimary,
            WasPrimaryRunning: false,
            HandshakeDelivered: true,
            Options: options);
    }

    /// <summary>
    /// Sends a graceful <c>--quit-for-update</c> request to a running instance of <paramref name="identity"/>
    /// and waits until its <c>&lt;Identity&gt;-instance</c> mutex is cleanly released (never force-killing).
    /// </summary>
    public static Task<(bool WasRunning, bool ExitedCleanly)> SignalQuitForUpdateAsync(
        string? identity,
        TimeSpan? waitTimeout,
        CancellationToken cancellationToken) =>
        SignalQuitForUpdateAsync(identity, waitTimeout, restartReason: null, cancellationToken);

    /// <summary>
    /// Sends a graceful <c>--quit-for-update</c> request with an explicit restart reason (defaulting to
    /// <see cref="UpdateRestartReason.DevInstall"/>) and waits until the primary mutex is cleanly released.
    /// </summary>
    public static async Task<(bool WasRunning, bool ExitedCleanly)> SignalQuitForUpdateAsync(
        string? identity = null,
        TimeSpan? waitTimeout = null,
        string? restartReason = null,
        CancellationToken cancellationToken = default)
    {
        string safeIdentity = SanitizeIdentityToken(string.IsNullOrWhiteSpace(identity) ? SpawnBuildInfo.Identity : identity.Trim());
        string mutexName = $"{safeIdentity}-instance";
        string pipeName = $"{safeIdentity}-control";
        string normalizedReason = UpdateRestartReason.Normalize(restartReason);

        if (!IsMutexCurrentlyHeld(mutexName))
        {
            return (WasRunning: false, ExitedCleanly: true);
        }

        bool rpcSent = false;
        try
        {
            await using var client = new NamedPipeJsonRpcClient(pipeName);
            rpcSent = await client.InvokeAsync<string?, bool>(
                MethodQuitForUpdate,
                normalizedReason,
                connectTimeout: TimeSpan.FromSeconds(2),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Instance may already be mid-shutdown; proceed to mutex release poll.
        }

        TimeSpan effectiveWait = waitTimeout ?? TimeSpan.FromSeconds(25);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < effectiveWait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsMutexCurrentlyHeld(mutexName))
            {
                return (WasRunning: true, ExitedCleanly: true);
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        return (WasRunning: true, ExitedCleanly: rpcSent && !IsMutexCurrentlyHeld(mutexName));
    }

    public static async Task<bool> ForwardActivateToPrimaryAsync(
        string controlPipeName,
        IReadOnlyList<string>? forwardedArgs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Allow the already-running primary instance to take foreground focus when launched from Start Menu / Desktop.
            try
            {
                _ = AllowSetForegroundWindow(AsfwAny);
            }
            catch
            {
                // Best-effort focus handoff on Windows.
            }

            await using var client = new NamedPipeJsonRpcClient(controlPipeName);
            return await client.InvokeAsync<IReadOnlyList<string>, bool>(
                MethodActivate,
                forwardedArgs ?? [],
                connectTimeout: TimeSpan.FromSeconds(1.5),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInstanceRunning(string? identity = null)
    {
        string safeIdentity = SanitizeIdentityToken(string.IsNullOrWhiteSpace(identity) ? SpawnBuildInfo.Identity : identity.Trim());
        return IsMutexCurrentlyHeld($"{safeIdentity}-instance");
    }

    private async Task StartControlServerAsync(CancellationToken cancellationToken)
    {
        var server = new NamedPipeJsonRpcServer(ControlPipeName);

        server.RegisterHandler<List<string>, bool>(MethodActivate, async (incomingArgs, ct) =>
        {
            var handler = ActivateRequested;
            if (handler is not null)
            {
                await handler(incomingArgs ?? [], ct).ConfigureAwait(false);
            }

            return true;
        });

        server.RegisterHandler<JsonElement?, bool>(MethodQuitForUpdate, (incomingPayload, _ct) =>
        {
            string? parsedReason = null;
            if (incomingPayload.HasValue)
            {
                if (incomingPayload.Value.ValueKind == JsonValueKind.String)
                {
                    parsedReason = incomingPayload.Value.GetString();
                }
                else if (incomingPayload.Value.ValueKind == JsonValueKind.Object
                         && incomingPayload.Value.TryGetProperty("reason", out JsonElement reasonProp)
                         && reasonProp.ValueKind == JsonValueKind.String)
                {
                    parsedReason = reasonProp.GetString();
                }
            }

            string normalizedReason = UpdateRestartReason.Normalize(parsedReason);
            _lastQuitForUpdateReason = normalizedReason;

            var reasonHandler = QuitForUpdateWithReasonRequested;
            var legacyHandler = QuitForUpdateRequested;
            if (reasonHandler is not null || legacyHandler is not null)
            {
                // Run asynchronously after sending RPC ACK so client receives immediate confirmation before pipe closes.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (reasonHandler is not null)
                        {
                            await reasonHandler(normalizedReason, CancellationToken.None).ConfigureAwait(false);
                        }

                        if (legacyHandler is not null)
                        {
                            await legacyHandler(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // Swallow shutdown callback exceptions to avoid crashing background thread.
                    }
                }, CancellationToken.None);
            }

            return Task.FromResult(true);
        });

        server.RegisterHandler<object?, int>(MethodPing, (_, _) => Task.FromResult(Environment.ProcessId));

        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        _controlServer = server;
    }

    private Task<bool> TryAcquireMutexOnDedicatedThreadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var acquireTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var ownerThread = new Thread(() =>
        {
            Mutex? mutex = null;
            bool hasHandle = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, name: MutexName, out _);
                try
                {
                    hasHandle = mutex.WaitOne(0, exitContext: false);
                }
                catch (AbandonedMutexException)
                {
                    // Previous crashed process abandoned the mutex; take clean ownership.
                    hasHandle = true;
                }

                if (!hasHandle)
                {
                    acquireTcs.TrySetResult(false);
                    return;
                }

                _ownsPrimaryMutex = true;
                acquireTcs.TrySetResult(true);

                // Hold mutex on this exact OS thread until Dispose signals release.
                _releaseMutexSignal.Wait();
            }
            catch (Exception ex)
            {
                acquireTcs.TrySetException(ex);
            }
            finally
            {
                if (hasHandle && mutex is not null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore release anomalies during process teardown.
                    }
                }

                _ownsPrimaryMutex = false;
                mutex?.Dispose();
                _mutexReleasedTcs.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = $"SpawnInstanceMutex:{Identity}",
        };

        _mutexOwnerThread = ownerThread;
        ownerThread.Start();
        return acquireTcs.Task;
    }

    private static bool IsMutexCurrentlyHeld(string mutexName)
    {
        if (!Mutex.TryOpenExisting(mutexName, out Mutex? existing) || existing is null)
        {
            return false;
        }

        using (existing)
        {
            bool acquiredProbe = false;
            try
            {
                acquiredProbe = existing.WaitOne(0, exitContext: false);
                return !acquiredProbe;
            }
            catch (AbandonedMutexException)
            {
                acquiredProbe = true;
                return false;
            }
            finally
            {
                if (acquiredProbe)
                {
                    try
                    {
                        existing.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore probe release errors.
                    }
                }
            }
        }
    }

    private static string SanitizeIdentityToken(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = string.Concat(raw.Select(c => Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '-' : c));
        return string.IsNullOrWhiteSpace(cleaned) ? "spawn-dev" : cleaned;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_controlServer is not null)
        {
            await _controlServer.DisposeAsync().ConfigureAwait(false);
            _controlServer = null;
        }

        _releaseMutexSignal.Set();
        if (_mutexOwnerThread is not null)
        {
            try
            {
                await _mutexReleasedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore teardown wait timeout.
            }
        }

        _releaseMutexSignal.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);
}
