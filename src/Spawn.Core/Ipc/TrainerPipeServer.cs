using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core.Ipc;

/// <summary>
/// Configuration for a single-session, single-instance inverted <see cref="TrainerPipeServer"/> hosted by Medium IL <c>Spawn.Host</c>.
/// </summary>
public sealed class TrainerPipeServerOptions
{
    public required string PipeName { get; init; }
    public SingleUseLaunchNonce? LaunchNonce { get; init; }
    public IReadOnlyList<string>? ExpectedWorkerExecutablePaths { get; init; }
    public ITrainerPipePeerInspector? PeerInspector { get; init; }
}

/// <summary>
/// Authenticated remote worker metadata established after first-frame <c>trainer.handshake</c>.
/// </summary>
public sealed record AuthenticatedTrainerWorkerInfo(
    int WorkerPid,
    string WorkerExecutablePath,
    int WorkerIntegrityLevel,
    int ProtocolVersion);

/// <summary>
/// Inverted cross-IL Named Pipe JSON-RPC server hosted by Medium IL <c>Spawn.Host</c> for a single <c>Spawn.TrainerWorker</c> session.
/// Enforces <c>maxNumberOfServerInstances = 1</c> + <c>PipeOptions.FirstPipeInstance</c>, current-user Allow + NetworkSid Deny ACL,
/// <c>GetNamedPipeClientProcessId</c> + <c>0x1000</c> path check, and single-use constant-time 256-bit <c>LaunchNonce</c> verification.
/// </summary>
public sealed class TrainerPipeServer : IAsyncDisposable
{
    private readonly ITrainerPipePeerInspector _peerInspector;
    private readonly IReadOnlyList<string> _allowedWorkerPaths;
    private readonly SingleUseLaunchNonce _nonce;
    private readonly bool _ownsNonce;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NamedPipeRpcMessage>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task>> _notificationHandlers = new(StringComparer.OrdinalIgnoreCase);

    private NamedPipeServerStream? _serverStream;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private int _disposed;
    private volatile bool _isAuthenticated;

    public string PipeName { get; }
    public string LaunchNonce => _nonce.EncodedValue;
    public bool IsListening => _serverStream is not null && Volatile.Read(ref _disposed) == 0;
    public bool IsAuthenticated => _isAuthenticated && _serverStream is { IsConnected: true };
    public AuthenticatedTrainerWorkerInfo? AuthenticatedWorker { get; private set; }

    public event Action<string, JsonElement?>? NotificationReceived;
    public event Action? Disconnected;

    public TrainerPipeServer(TrainerPipeServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);

        PipeName = NamedPipeJsonRpcServer.NormalizePipeName(options.PipeName);
        _peerInspector = options.PeerInspector ?? Win32TrainerPipePeerInspector.Instance;
        _allowedWorkerPaths = options.ExpectedWorkerExecutablePaths is { Count: > 0 }
            ? options.ExpectedWorkerExecutablePaths
            : TrainerPipePathResolver.ResolveDefaultWorkerExecutablePaths();

        if (options.LaunchNonce is not null)
        {
            _nonce = options.LaunchNonce;
            _ownsNonce = false;
        }
        else
        {
            _nonce = SingleUseLaunchNonce.Generate();
            _ownsNonce = true;
        }
    }

    public void RegisterNotificationHandler<TParams>(
        string method,
        Func<TParams?, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);

        _notificationHandlers[method] = (raw, ct) =>
        {
            TParams? typed = raw.HasValue
                ? raw.Value.Deserialize<TParams>(NamedPipeJsonRpc.SerializerOptions)
                : default;
            return handler(typed, ct);
        };
    }

    /// <summary>
    /// Synchronously binds the single-instance Named Pipe in the Windows kernel.
    /// Must be invoked BEFORE spawning <c>Spawn.TrainerWorker.exe</c> to prevent pipe squatting.
    /// </summary>
    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_serverStream is not null)
        {
            return;
        }

        _serverStream = NamedPipeSecurityFactory.CreateSingleInstanceSecureServerStream(PipeName);
    }

    /// <summary>
    /// Waits for the expected worker to connect and verifies client PID, <c>0x1000</c> executable path,
    /// and first-frame single-use 256-bit <c>LaunchNonce</c>. Immediately disconnects on any violation.
    /// </summary>
    public async Task<AuthenticatedTrainerWorkerInfo> WaitForAuthenticatedClientAsync(
        int expectedWorkerPid,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (expectedWorkerPid <= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedWorkerPid), "Expected worker PID must be a valid user-mode process ID.");
        }

        if (_serverStream is null)
        {
            StartListening();
        }

        NamedPipeServerStream stream = _serverStream!;
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            await stream.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);

            // 1. Verify kernel-reported client PID matches the exact Worker PID spawned by Host.
            if (!_peerInspector.TryGetClientProcessId(stream.SafePipeHandle, out int actualClientPid))
            {
                DisconnectStreamImmediately();
                throw new TrainerPipeAuthenticationException(
                    "CLIENT_PID_UNAVAILABLE",
                    "Unable to query GetNamedPipeClientProcessId for connecting worker.");
            }

            if (actualClientPid != expectedWorkerPid)
            {
                DisconnectStreamImmediately();
                throw new TrainerPipeAuthenticationException(
                    "CLIENT_PID_MISMATCH",
                    $"Connecting client PID {actualClientPid} does not match expected worker PID {expectedWorkerPid}.");
            }

            // 2. Verify 0x1000 image path matches the expected Spawn.TrainerWorker.exe path.
            string? actualClientPath = _peerInspector.TryGetProcessExecutablePath(actualClientPid);
            if (!TrainerPipePathResolver.MatchesAnyPath(actualClientPath, _allowedWorkerPaths))
            {
                DisconnectStreamImmediately();
                throw new TrainerPipeAuthenticationException(
                    "CLIENT_PATH_MISMATCH",
                    $"Connecting client executable path '{actualClientPath ?? "<unknown>"}' does not match allowed Spawn.TrainerWorker paths.");
            }

            // 3. First frame MUST be trainer.handshake carrying the single-use 256-bit LaunchNonce.
            NamedPipeRpcMessage? firstFrame = await NamedPipeJsonRpc.ReadFrameAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            if (firstFrame is null
                || string.IsNullOrWhiteSpace(firstFrame.Id)
                || !string.Equals(firstFrame.Method, TrainerIpcMethods.Handshake, StringComparison.Ordinal)
                || !firstFrame.Params.HasValue)
            {
                _ = _nonce.VerifyAndBurn(null);
                DisconnectStreamImmediately();
                throw new TrainerPipeAuthenticationException(
                    "INVALID_FIRST_FRAME",
                    $"First frame must be '{TrainerIpcMethods.Handshake}' RPC request.");
            }

            TrainerHandshakeRequest? handshake = firstFrame.Params.Value.Deserialize<TrainerHandshakeRequest>(NamedPipeJsonRpc.SerializerOptions);
            if (handshake is null)
            {
                _ = _nonce.VerifyAndBurn(null);
                await TrySendHandshakeRejectAndDisconnectAsync(stream, firstFrame.Id, "Malformed handshake payload.", timeoutCts.Token).ConfigureAwait(false);
                throw new TrainerPipeAuthenticationException("MALFORMED_HANDSHAKE", "Handshake payload was null.");
            }

            bool nonceValid = _nonce.VerifyAndBurn(handshake.LaunchNonce);
            if (!nonceValid)
            {
                await TrySendHandshakeRejectAndDisconnectAsync(stream, firstFrame.Id, "Invalid or replayed launch nonce.", timeoutCts.Token).ConfigureAwait(false);
                throw new TrainerPipeAuthenticationException("INVALID_NONCE", "Worker presented an invalid or already consumed LaunchNonce.");
            }

            if (handshake.ProtocolVersion != TrainerIpcMethods.ProtocolVersion)
            {
                await TrySendHandshakeRejectAndDisconnectAsync(
                    stream,
                    firstFrame.Id,
                    $"Unsupported protocol version {handshake.ProtocolVersion}.",
                    timeoutCts.Token).ConfigureAwait(false);
                throw new TrainerPipeAuthenticationException(
                    "PROTOCOL_VERSION_MISMATCH",
                    $"Worker protocol version {handshake.ProtocolVersion} does not match expected {TrainerIpcMethods.ProtocolVersion}.");
            }

            if (handshake.WorkerPid != actualClientPid)
            {
                await TrySendHandshakeRejectAndDisconnectAsync(
                    stream,
                    firstFrame.Id,
                    "Handshake PID claim does not match kernel pipe client PID.",
                    timeoutCts.Token).ConfigureAwait(false);
                throw new TrainerPipeAuthenticationException(
                    "HANDSHAKE_PID_SPOOF",
                    $"Handshake WorkerPid {handshake.WorkerPid} disagreed with kernel pipe PID {actualClientPid}.");
            }

            var reply = new NamedPipeRpcMessage
            {
                Id = firstFrame.Id,
                Result = JsonSerializer.SerializeToElement(
                    new TrainerHandshakeResponse
                    {
                        Accepted = true,
                        HostPid = Environment.ProcessId,
                        ProtocolVersion = TrainerIpcMethods.ProtocolVersion,
                    },
                    NamedPipeJsonRpc.SerializerOptions),
            };

            await NamedPipeJsonRpc.WriteFrameAsync(stream, reply, _writeGate, timeoutCts.Token).ConfigureAwait(false);

            var info = new AuthenticatedTrainerWorkerInfo(
                actualClientPid,
                actualClientPath!,
                handshake.WorkerIntegrityLevel,
                handshake.ProtocolVersion);

            AuthenticatedWorker = info;
            _isAuthenticated = true;

            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpLoopAsync(stream, _pumpCts.Token), CancellationToken.None);
            return info;
        }
        catch (TrainerPipeAuthenticationException)
        {
            DisconnectStreamImmediately();
            throw;
        }
        catch (Exception)
        {
            _ = _nonce.VerifyAndBurn(null);
            DisconnectStreamImmediately();
            throw;
        }
    }

    /// <summary>
    /// Sends an authenticated Host → Worker JSON-RPC request and awaits the typed response.
    /// </summary>
    public async Task<TResult?> InvokeAsync<TParams, TResult>(
        string method,
        TParams parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (!IsAuthenticated || _serverStream is not { IsConnected: true } stream)
        {
            throw new IOException("TrainerPipeServer is not connected to an authenticated worker.");
        }

        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<NamedPipeRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        using var linkedCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (linkedCts is not null && timeout.HasValue)
        {
            linkedCts.CancelAfter(timeout.Value);
        }

        CancellationToken effectiveCt = linkedCts?.Token ?? cancellationToken;

        try
        {
            var req = new NamedPipeRpcMessage
            {
                Id = id,
                Method = method,
                Params = JsonSerializer.SerializeToElement(parameters, NamedPipeJsonRpc.SerializerOptions),
            };

            await NamedPipeJsonRpc.WriteFrameAsync(stream, req, _writeGate, effectiveCt).ConfigureAwait(false);

            await using var reg = effectiveCt.Register(
                static state => ((TaskCompletionSource<NamedPipeRpcMessage>)state!).TrySetCanceled(),
                tcs);

            NamedPipeRpcMessage reply = await tcs.Task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reply.Error))
            {
                throw new InvalidOperationException($"Trainer IPC error on '{method}': {reply.Error}");
            }

            return reply.Result.HasValue
                ? reply.Result.Value.Deserialize<TResult>(NamedPipeJsonRpc.SerializerOptions)
                : default;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private async Task PumpLoopAsync(NamedPipeServerStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && stream.IsConnected)
            {
                NamedPipeRpcMessage? frame = await NamedPipeJsonRpc.ReadFrameAsync(stream, token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(frame.Id) && _pendingRequests.TryRemove(frame.Id, out var waiter))
                {
                    waiter.TrySetResult(frame);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(frame.Method))
                {
                    if (string.Equals(frame.Method, TrainerIpcMethods.Handshake, StringComparison.OrdinalIgnoreCase))
                    {
                        // Duplicate handshake after authentication is a protocol violation; drop immediately.
                        break;
                    }

                    NotificationReceived?.Invoke(frame.Method, frame.Params);
                    if (_notificationHandlers.TryGetValue(frame.Method, out var handler))
                    {
                        try
                        {
                            await handler(frame.Params, token).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Keep pipe alive if a subscriber handler throws.
                        }
                    }
                }
            }
        }
        catch
        {
            // Pipe closed, cancelled, or broken.
        }
        finally
        {
            _isAuthenticated = false;
            foreach (var (_, waiter) in _pendingRequests.ToArray())
            {
                waiter.TrySetException(new IOException("Trainer worker pipe disconnected before RPC response arrived."));
            }

            _pendingRequests.Clear();
            DisconnectStreamImmediately();
            Disconnected?.Invoke();
        }
    }

    private async Task TrySendHandshakeRejectAndDisconnectAsync(
        NamedPipeServerStream stream,
        string requestId,
        string rejectReason,
        CancellationToken ct)
    {
        try
        {
            if (stream.IsConnected)
            {
                var rejectReply = new NamedPipeRpcMessage
                {
                    Id = requestId,
                    Result = JsonSerializer.SerializeToElement(
                        new TrainerHandshakeResponse
                        {
                            Accepted = false,
                            HostPid = Environment.ProcessId,
                            ProtocolVersion = TrainerIpcMethods.ProtocolVersion,
                            RejectReason = rejectReason,
                        },
                        NamedPipeJsonRpc.SerializerOptions),
                };

                await NamedPipeJsonRpc.WriteFrameAsync(stream, rejectReply, _writeGate, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore write errors during immediate rejection.
        }
        finally
        {
            DisconnectStreamImmediately();
        }
    }

    private void DisconnectStreamImmediately()
    {
        var stream = Interlocked.Exchange(ref _serverStream, null);
        if (stream is null)
        {
            return;
        }

        try
        {
            if (stream.IsConnected)
            {
                stream.Disconnect();
            }
        }
        catch
        {
            // Ignore disconnect races.
        }

        try
        {
            stream.Dispose();
        }
        catch
        {
            // Ignore dispose races.
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
            // Ignore cancellation race.
        }

        DisconnectStreamImmediately();

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
        if (_ownsNonce)
        {
            _nonce.Dispose();
        }

        _writeGate.Dispose();
    }
}
