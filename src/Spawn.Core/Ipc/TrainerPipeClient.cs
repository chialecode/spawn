using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core.Ipc;

/// <summary>
/// Configuration for the worker-side inverted <see cref="TrainerPipeClient"/>.
/// </summary>
public sealed class TrainerPipeClientOptions
{
    public required string PipeName { get; init; }
    public required int ExpectedHostPid { get; init; }
    public required string LaunchNonce { get; init; }
    public int WorkerIntegrityLevel { get; init; } = 0x2000;
    public IReadOnlyList<string>? ExpectedHostExecutablePaths { get; init; }
    public ITrainerPipePeerInspector? PeerInspector { get; init; }
}

/// <summary>
/// Worker-side inverted cross-IL Named Pipe JSON-RPC client.
/// Verifies parent Host PID &amp; <c>0x1000</c> path BEFORE and AFTER connecting via <c>GetNamedPipeServerProcessId</c>,
/// performs first-frame <c>trainer.handshake</c>, serves incoming Host → Worker RPC requests, and pushes Worker → Host notifications.
/// </summary>
public sealed class TrainerPipeClient : IAsyncDisposable
{
    private readonly TrainerPipeClientOptions _options;
    private readonly ITrainerPipePeerInspector _peerInspector;
    private readonly IReadOnlyList<string> _allowedHostPaths;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement?>>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    private NamedPipeClientStream? _stream;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private int _disposed;
    private volatile bool _isAuthenticated;

    public string PipeName { get; }
    public bool IsConnected => _isAuthenticated && _stream is { IsConnected: true };
    public int? AuthenticatedHostPid { get; private set; }
    public string? AuthenticatedHostPath { get; private set; }

    public event Action? Disconnected;

    public TrainerPipeClient(TrainerPipeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LaunchNonce);

        _options = options;
        PipeName = NamedPipeJsonRpcServer.NormalizePipeName(options.PipeName);
        _peerInspector = options.PeerInspector ?? Win32TrainerPipePeerInspector.Instance;
        _allowedHostPaths = options.ExpectedHostExecutablePaths is { Count: > 0 }
            ? options.ExpectedHostExecutablePaths
            : TrainerPipePathResolver.ResolveDefaultAllowedHostExecutablePaths();
    }

    public void RegisterHandler<TParams, TResult>(
        string method,
        Func<TParams?, CancellationToken, Task<TResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[method] = async (rawParams, ct) =>
        {
            TParams? typed = rawParams.HasValue
                ? rawParams.Value.Deserialize<TParams>(NamedPipeJsonRpc.SerializerOptions)
                : default;
            TResult result = await handler(typed, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, NamedPipeJsonRpc.SerializerOptions);
        };
    }

    /// <summary>
    /// Validates the expected Host process, connects to the Named Pipe server, re-verifies the kernel pipe server PID and path,
    /// completes <c>trainer.handshake</c>, and begins serving incoming Host commands.
    /// </summary>
    public async Task ConnectAndAuthenticateAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_options.ExpectedHostPid <= 4)
        {
            throw new TrainerPipeAuthenticationException("INVALID_HOST_PID", "Expected Host PID must be a valid user-mode process ID.");
        }

        // 1. Pre-connect verification: expected Host PID must exist and resolve to an allowed Spawn.Host executable path.
        string? preConnectHostPath = _peerInspector.TryGetProcessExecutablePath(_options.ExpectedHostPid);
        if (!TrainerPipePathResolver.MatchesAnyPath(preConnectHostPath, _allowedHostPaths))
        {
            throw new TrainerPipeAuthenticationException(
                "HOST_PRECONNECT_PATH_MISMATCH",
                $"Parent Host PID {_options.ExpectedHostPid} path '{preConnectHostPath ?? "<unknown>"}' does not match allowed Spawn.Host paths.");
        }

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync((int)Math.Max(100, effectiveTimeout.TotalMilliseconds), timeoutCts.Token).ConfigureAwait(false);

            // 2. Post-connect kernel verification: server PID MUST equal --host-pid and path MUST match allowed Spawn.Host.
            if (!_peerInspector.TryGetServerProcessId(client.SafePipeHandle, out int actualServerPid))
            {
                client.Dispose();
                throw new TrainerPipeAuthenticationException(
                    "SERVER_PID_UNAVAILABLE",
                    "Unable to query GetNamedPipeServerProcessId for connected Host pipe.");
            }

            if (actualServerPid != _options.ExpectedHostPid)
            {
                client.Dispose();
                throw new TrainerPipeAuthenticationException(
                    "SERVER_PID_MISMATCH",
                    $"Pipe server PID {actualServerPid} does not match expected parent Host PID {_options.ExpectedHostPid}.");
            }

            string? actualServerPath = _peerInspector.TryGetProcessExecutablePath(actualServerPid);
            if (!TrainerPipePathResolver.MatchesAnyPath(actualServerPath, _allowedHostPaths))
            {
                client.Dispose();
                throw new TrainerPipeAuthenticationException(
                    "SERVER_PATH_MISMATCH",
                    $"Pipe server executable path '{actualServerPath ?? "<unknown>"}' does not match allowed Spawn.Host paths.");
            }

            // 3. First-frame mutual authentication handshake carrying the 256-bit single-use LaunchNonce.
            string handshakeId = Guid.NewGuid().ToString("N");
            var handshakeRequest = new NamedPipeRpcMessage
            {
                Id = handshakeId,
                Method = TrainerIpcMethods.Handshake,
                Params = JsonSerializer.SerializeToElement(
                    new TrainerHandshakeRequest
                    {
                        LaunchNonce = _options.LaunchNonce,
                        WorkerPid = Environment.ProcessId,
                        WorkerIntegrityLevel = _options.WorkerIntegrityLevel,
                        ProtocolVersion = TrainerIpcMethods.ProtocolVersion,
                        WorkerExecutablePath = Environment.ProcessPath,
                    },
                    NamedPipeJsonRpc.SerializerOptions),
            };

            await NamedPipeJsonRpc.WriteFrameAsync(client, handshakeRequest, _writeGate, timeoutCts.Token).ConfigureAwait(false);
            NamedPipeRpcMessage? replyFrame = await NamedPipeJsonRpc.ReadFrameAsync(client, timeoutCts.Token).ConfigureAwait(false);
            if (replyFrame is null || !string.Equals(replyFrame.Id, handshakeId, StringComparison.Ordinal))
            {
                client.Dispose();
                throw new TrainerPipeAuthenticationException("HANDSHAKE_NO_REPLY", "Host closed the pipe before acknowledging handshake.");
            }

            if (!string.IsNullOrWhiteSpace(replyFrame.Error))
            {
                client.Dispose();
                throw new TrainerPipeAuthenticationException("HANDSHAKE_RPC_ERROR", $"Host rejected handshake: {replyFrame.Error}");
            }

            TrainerHandshakeResponse? handshakeReply = replyFrame.Result.HasValue
                ? replyFrame.Result.Value.Deserialize<TrainerHandshakeResponse>(NamedPipeJsonRpc.SerializerOptions)
                : null;

            if (handshakeReply is null || !handshakeReply.Accepted || handshakeReply.HostPid != actualServerPid)
            {
                client.Dispose();
                throw new TrainerPipeAuthenticationException(
                    "HANDSHAKE_REJECTED",
                    $"Host rejected worker handshake: {handshakeReply?.RejectReason ?? "unknown reason"}.");
            }

            _stream = client;
            AuthenticatedHostPid = actualServerPid;
            AuthenticatedHostPath = actualServerPath;
            _isAuthenticated = true;

            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpLoopAsync(client, _pumpCts.Token), CancellationToken.None);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pushes a Worker → Host JSON-RPC notification (such as <c>trainer.processCreated</c> or <c>trainer.sessionEnded</c>).
    /// </summary>
    public async Task NotifyAsync<TParams>(
        string method,
        TParams payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        NamedPipeClientStream? stream = _stream;
        if (!_isAuthenticated || stream is not { IsConnected: true })
        {
            throw new IOException("TrainerPipeClient is not connected.");
        }

        var msg = new NamedPipeRpcMessage
        {
            Method = method,
            Params = JsonSerializer.SerializeToElement(payload, NamedPipeJsonRpc.SerializerOptions),
        };

        await NamedPipeJsonRpc.WriteFrameAsync(stream, msg, _writeGate, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpLoopAsync(NamedPipeClientStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && stream.IsConnected)
            {
                NamedPipeRpcMessage? incoming = await NamedPipeJsonRpc.ReadFrameAsync(stream, token).ConfigureAwait(false);
                if (incoming is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(incoming.Method))
                {
                    continue;
                }

                // Dispatch each Host -> Worker RPC concurrently so emergencyShutdown is never blocked behind a slow hotkey or launch.
                _ = Task.Run(() => HandleIncomingRequestAsync(stream, incoming, token), CancellationToken.None);
            }
        }
        catch
        {
            // Pipe closed or cancelled.
        }
        finally
        {
            _isAuthenticated = false;
            CloseStreamImmediately();
            Disconnected?.Invoke();
        }
    }

    private async Task HandleIncomingRequestAsync(
        NamedPipeClientStream stream,
        NamedPipeRpcMessage incoming,
        CancellationToken token)
    {
        NamedPipeRpcMessage? response = null;
        if (_handlers.TryGetValue(incoming.Method!, out var handler))
        {
            try
            {
                JsonElement? res = await handler(incoming.Params, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(incoming.Id))
                {
                    response = new NamedPipeRpcMessage
                    {
                        Id = incoming.Id,
                        Result = res,
                    };
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(incoming.Id))
                {
                    response = new NamedPipeRpcMessage
                    {
                        Id = incoming.Id,
                        Error = ex.Message,
                    };
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(incoming.Id))
        {
            response = new NamedPipeRpcMessage
            {
                Id = incoming.Id,
                Error = $"Unknown trainer worker RPC method '{incoming.Method}'.",
            };
        }

        if (response is not null && stream.IsConnected)
        {
            try
            {
                await NamedPipeJsonRpc.WriteFrameAsync(stream, response, _writeGate, token).ConfigureAwait(false);
            }
            catch
            {
                // Ignore write race if pipe closed while replying.
            }
        }
    }

    private void CloseStreamImmediately()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Dispose();
        }
        catch
        {
            // Ignore close race.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _isAuthenticated = false;

        try
        {
            if (_pumpCts is not null)
            {
                await _pumpCts.CancelAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore cancel race.
        }

        CloseStreamImmediately();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore pump teardown timeout.
            }
        }

        _pumpCts?.Dispose();
        _pumpCts = null;
        _writeGate.Dispose();
    }
}
