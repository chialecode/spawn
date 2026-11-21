using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Wire envelope for SPAWN's zero-TCP local Named Pipe JSON-RPC bus.
/// </summary>
public sealed record NamedPipeRpcMessage
{
    public string Jsonrpc { get; init; } = "2.0";
    public string? Id { get; init; }
    public string? Method { get; init; }
    public JsonElement? Params { get; init; }
    public JsonElement? Result { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Builds hardened Windows <see cref="PipeSecurity"/> ACLs granting read/write strictly to the current
/// interactive user SID while explicitly denying <see cref="WellKnownSidType.NetworkSid"/>.
/// </summary>
public static class NamedPipeSecurityFactory
{
    public static PipeSecurity CreateCurrentUserPipeSecurity()
    {
        var pipeSecurity = new PipeSecurity();

        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier userSid = identity.User
            ?? throw new InvalidOperationException("Unable to resolve current WindowsIdentity user SID for Named Pipe ACL.");

        pipeSecurity.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            networkSid,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        return pipeSecurity;
    }

    public static NamedPipeServerStream CreateSecureServerStream(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeSecurity security = CreateCurrentUserPipeSecurity();

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 65536,
            outBufferSize: 65536,
            pipeSecurity: security);
    }

    /// <summary>
    /// Creates an ACL-hardened, strictly single-instance Named Pipe server stream
    /// (<c>maxNumberOfServerInstances = 1</c> + <see cref="PipeOptions.FirstPipeInstance"/>)
    /// to prevent same-user pipe squatting for inverted cross-IL worker sessions.
    /// </summary>
    public static NamedPipeServerStream CreateSingleInstanceSecureServerStream(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeSecurity security = CreateCurrentUserPipeSecurity();
        string normalizedName = NamedPipeJsonRpcServer.NormalizePipeName(pipeName);

        return NamedPipeServerStreamAcl.Create(
            normalizedName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 65536,
            outBufferSize: 65536,
            pipeSecurity: security);
    }
}

/// <summary>
/// Entrypoint helpers and constants for SPAWN's zero-TCP Named Pipe JSON-RPC transport.
/// </summary>
public static class NamedPipeJsonRpc
{
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static NamedPipeJsonRpcServer CreateServer(string pipeName) => new(pipeName);

    public static NamedPipeJsonRpcClient CreateClient(string pipeName) => new(pipeName);

    internal static async Task WriteFrameAsync(
        Stream stream,
        NamedPipeRpcMessage message,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (utf8.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"IPC frame size {utf8.Length} exceeds limit {MaxFrameBytes}.");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, utf8.Length);

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(utf8, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    internal static async Task<NamedPipeRpcMessage?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        int headerRead = await ReadUpToAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < 4)
        {
            throw new EndOfStreamException("Truncated 4-byte Named Pipe RPC frame header.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid Named Pipe RPC frame length: {length}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NamedPipeRpcMessage>(payload, SerializerOptions);
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return total;
            }

            total += n;
        }

        return total;
    }
}

/// <summary>
/// Bidirectional multi-client JSON-RPC server over ACL-hardened Windows Named Pipes.
/// </summary>
public sealed class NamedPipeJsonRpcServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Func<JsonElement?, Guid, CancellationToken, Task<JsonElement?>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private volatile bool _isRunning;

    public string PipeName { get; }
    public bool IsRunning => _isRunning;
    public int ConnectedClientCount => _sessions.Count;

    public NamedPipeJsonRpcServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeName = NormalizePipeName(pipeName);
    }

    public void RegisterHandler<TParams, TResult>(
        string method,
        Func<TParams?, CancellationToken, Task<TResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[method] = async (rawParams, _, ct) =>
        {
            TParams? typed = rawParams.HasValue
                ? rawParams.Value.Deserialize<TParams>(NamedPipeJsonRpc.SerializerOptions)
                : default;
            TResult result = await handler(typed, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, NamedPipeJsonRpc.SerializerOptions);
        };
    }

    public void RegisterNotificationHandler<TParams>(
        string method,
        Func<TParams?, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[method] = async (rawParams, _, ct) =>
        {
            TParams? typed = rawParams.HasValue
                ? rawParams.Value.Deserialize<TParams>(NamedPipeJsonRpc.SerializerOptions)
                : default;
            await handler(typed, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(true, NamedPipeJsonRpc.SerializerOptions);
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _isRunning = true;
        _acceptLoopTask = Task.Run(() => AcceptConnectionsLoopAsync(readyTcs, _cts.Token), CancellationToken.None);
        return readyTcs.Task;
    }

    /// <summary>
    /// Broadcasts a server-originated JSON-RPC notification to all currently connected clients.
    /// </summary>
    public async Task<int> BroadcastAsync<TPayload>(
        string method,
        TPayload payload,
        Guid? excludeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var msg = new NamedPipeRpcMessage
        {
            Method = method,
            Params = JsonSerializer.SerializeToElement(payload, NamedPipeJsonRpc.SerializerOptions),
        };

        int delivered = 0;
        foreach (var (id, session) in _sessions.ToArray())
        {
            if (excludeSessionId.HasValue && id == excludeSessionId.Value)
            {
                continue;
            }

            if (!session.Stream.IsConnected)
            {
                _sessions.TryRemove(id, out _);
                continue;
            }

            try
            {
                await NamedPipeJsonRpc.WriteFrameAsync(session.Stream, msg, session.WriteGate, cancellationToken).ConfigureAwait(false);
                delivered++;
            }
            catch
            {
                _sessions.TryRemove(id, out _);
            }
        }

        return delivered;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (var (_, session) in _sessions.ToArray())
        {
            session.Dispose();
        }

        _sessions.Clear();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore graceful shutdown timeout.
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptConnectionsLoopAsync(TaskCompletionSource firstStreamReady, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                serverStream = NamedPipeSecurityFactory.CreateSecureServerStream(PipeName);
                firstStreamReady.TrySetResult();

                await serverStream.WaitForConnectionAsync(token).ConfigureAwait(false);
                var session = new ClientSession(Guid.NewGuid(), serverStream);
                _sessions[session.Id] = session;
                _ = Task.Run(() => PumpClientSessionAsync(session, token), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                serverStream?.Dispose();
                firstStreamReady.TrySetResult();
                break;
            }
            catch (Exception ex)
            {
                serverStream?.Dispose();
                if (!firstStreamReady.Task.IsCompleted)
                {
                    firstStreamReady.TrySetException(ex);
                    break;
                }

                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task PumpClientSessionAsync(ClientSession session, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && session.Stream.IsConnected)
            {
                var incoming = await NamedPipeJsonRpc.ReadFrameAsync(session.Stream, token).ConfigureAwait(false);
                if (incoming is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(incoming.Method))
                {
                    continue;
                }

                NamedPipeRpcMessage? response = null;
                if (_handlers.TryGetValue(incoming.Method, out var handler))
                {
                    try
                    {
                        JsonElement? res = await handler(incoming.Params, session.Id, token).ConfigureAwait(false);
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
                        Error = $"Unknown RPC method '{incoming.Method}'.",
                    };
                }

                if (response is not null && session.Stream.IsConnected)
                {
                    await NamedPipeJsonRpc.WriteFrameAsync(session.Stream, response, session.WriteGate, token).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Disconnected cleanly or cancelled.
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            session.Dispose();
        }
    }

    internal static string NormalizePipeName(string raw)
    {
        const string prefix = @"\\.\pipe\";
        return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? raw[prefix.Length..]
            : raw.Trim();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class ClientSession(Guid id, NamedPipeServerStream stream) : IDisposable
    {
        public Guid Id { get; } = id;
        public NamedPipeServerStream Stream { get; } = stream;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        public void Dispose()
        {
            try
            {
                Stream.Dispose();
            }
            catch
            {
                // Ignore stream close race.
            }

            WriteGate.Dispose();
        }
    }
}

/// <summary>
/// Bidirectional JSON-RPC client over Windows Named Pipes supporting concurrent requests and server push notifications.
/// </summary>
public sealed class NamedPipeJsonRpcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NamedPipeRpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task>> _notificationHandlers = new(StringComparer.OrdinalIgnoreCase);

    private NamedPipeClientStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoopTask;

    public string PipeName { get; }
    public bool IsConnected => _stream is { IsConnected: true };

    public event Action<string, JsonElement?>? NotificationReceived;

    public NamedPipeJsonRpcClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeName = NamedPipeJsonRpcServer.NormalizePipeName(pipeName);
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

    public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            CleanupConnectionNoLock();

            var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(2);
            await client.ConnectAsync((int)Math.Max(50, effectiveTimeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);

            _stream = client;
            _readCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(client, _readCts.Token), CancellationToken.None);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<TResult?> InvokeAsync<TParams, TResult>(
        string method,
        TParams parameters,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        await ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);

        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<NamedPipeRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var req = new NamedPipeRpcMessage
            {
                Id = id,
                Method = method,
                Params = JsonSerializer.SerializeToElement(parameters, NamedPipeJsonRpc.SerializerOptions),
            };

            var activeStream = _stream ?? throw new IOException("Named Pipe client stream is not connected.");
            await NamedPipeJsonRpc.WriteFrameAsync(activeStream, req, _writeGate, cancellationToken).ConfigureAwait(false);

            await using var reg = cancellationToken.Register(
                static state => ((TaskCompletionSource<NamedPipeRpcMessage>)state!).TrySetCanceled(),
                tcs);

            var reply = await tcs.Task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reply.Error))
            {
                throw new InvalidOperationException($"Named Pipe RPC error on '{method}': {reply.Error}");
            }

            return reply.Result.HasValue
                ? reply.Result.Value.Deserialize<TResult>(NamedPipeJsonRpc.SerializerOptions)
                : default;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task NotifyAsync<TParams>(
        string method,
        TParams parameters,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        await ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);

        var msg = new NamedPipeRpcMessage
        {
            Method = method,
            Params = JsonSerializer.SerializeToElement(parameters, NamedPipeJsonRpc.SerializerOptions),
        };

        var activeStream = _stream ?? throw new IOException("Named Pipe client stream is not connected.");
        await NamedPipeJsonRpc.WriteFrameAsync(activeStream, msg, _writeGate, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(NamedPipeClientStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && stream.IsConnected)
            {
                var frame = await NamedPipeJsonRpc.ReadFrameAsync(stream, token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(frame.Id) && _pending.TryRemove(frame.Id, out var waiter))
                {
                    waiter.TrySetResult(frame);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(frame.Method))
                {
                    NotificationReceived?.Invoke(frame.Method, frame.Params);
                    if (_notificationHandlers.TryGetValue(frame.Method, out var handler))
                    {
                        await handler(frame.Params, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch
        {
            // Pipe closed or cancelled.
        }
        finally
        {
            foreach (var (_, waiter) in _pending.ToArray())
            {
                waiter.TrySetException(new IOException("Named Pipe connection closed before RPC reply arrived."));
            }

            _pending.Clear();
        }
    }

    private void CleanupConnectionNoLock()
    {
        try
        {
            _readCts?.Cancel();
            _readCts?.Dispose();
        }
        catch
        {
            // Ignore cancel race.
        }

        _readCts = null;

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Ignore stream close race.
        }

        _stream = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CleanupConnectionNoLock();
            if (_readLoopTask is not null)
            {
                try
                {
                    await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore shutdown wait timeout.
                }
            }
        }
        finally
        {
            _connectLock.Release();
            _connectLock.Dispose();
            _writeGate.Dispose();
        }
    }
}

/// <summary>
/// Ready-to-use <see cref="IOverlayIpcServer"/> hosted inside <c>Spawn.OverlayHost</c>.
/// </summary>
public sealed class OverlayIpcServer : IOverlayIpcServer
{
    public const string MethodToast = "overlay.toast";
    public const string MethodToastBatch = "overlay.toastBatch";
    public const string MethodPing = "overlay.ping";
    public const string MethodShutdown = "overlay.shutdown";

    private readonly NamedPipeJsonRpcServer _server;

    public string PipeName => _server.PipeName;
    public bool IsRunning => _server.IsRunning;
    public Func<OverlayHeartbeatReply>? HeartbeatProvider { get; set; }

    public event Func<OverlayToastRequest, CancellationToken, Task>? ToastReceived;
    public event Func<CancellationToken, Task>? ShutdownRequested;

    public OverlayIpcServer(SpawnPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).OverlayPipeName)
    {
    }

    public OverlayIpcServer(string pipeName)
    {
        _server = new NamedPipeJsonRpcServer(pipeName);

        _server.RegisterHandler<OverlayToastRequest, bool>(MethodToast, async (toast, ct) =>
        {
            if (toast is null)
            {
                return false;
            }

            var handler = ToastReceived;
            if (handler is not null)
            {
                await handler(toast, ct).ConfigureAwait(false);
            }

            return true;
        });

        _server.RegisterHandler<List<OverlayToastRequest>, int>(MethodToastBatch, async (batch, ct) =>
        {
            if (batch is null || batch.Count == 0)
            {
                return 0;
            }

            var handler = ToastReceived;
            int count = 0;
            foreach (var item in batch)
            {
                if (handler is not null)
                {
                    await handler(item, ct).ConfigureAwait(false);
                }

                count++;
            }

            return count;
        });

        _server.RegisterHandler<object?, OverlayHeartbeatReply>(MethodPing, (_, _) =>
        {
            var reply = HeartbeatProvider?.Invoke() ?? new OverlayHeartbeatReply
            {
                ProcessId = Environment.ProcessId,
                ServerTimeUtc = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(reply);
        });

        _server.RegisterHandler<object?, bool>(MethodShutdown, async (_, ct) =>
        {
            var handler = ShutdownRequested;
            if (handler is not null)
            {
                await handler(ct).ConfigureAwait(false);
            }

            return true;
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => _server.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _server.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// Ready-to-use <see cref="IOverlayIpcClient"/> used by <c>Spawn.Host</c> and <c>Spawn.Modules.Achievement</c>.
/// </summary>
public sealed class OverlayIpcClient : IOverlayIpcClient
{
    private readonly NamedPipeJsonRpcClient _client;

    public string PipeName => _client.PipeName;

    public OverlayIpcClient(SpawnPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).OverlayPipeName)
    {
    }

    public OverlayIpcClient(string pipeName)
    {
        _client = new NamedPipeJsonRpcClient(pipeName);
    }

    public async Task<bool> SendToastAsync(OverlayToastRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await _client.InvokeAsync<OverlayToastRequest, bool>(
                OverlayIpcServer.MethodToast,
                request,
                connectTimeout: TimeSpan.FromMilliseconds(800),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> SendToastBatchAsync(IReadOnlyList<OverlayToastRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return 0;
        }

        try
        {
            return await _client.InvokeAsync<IReadOnlyList<OverlayToastRequest>, int>(
                OverlayIpcServer.MethodToastBatch,
                requests,
                connectTimeout: TimeSpan.FromMilliseconds(800),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<OverlayHeartbeatReply?> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client.InvokeAsync<object?, OverlayHeartbeatReply>(
                OverlayIpcServer.MethodPing,
                null,
                connectTimeout: TimeSpan.FromMilliseconds(500),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client.InvokeAsync<object?, bool>(
                OverlayIpcServer.MethodShutdown,
                null,
                connectTimeout: TimeSpan.FromMilliseconds(500),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
