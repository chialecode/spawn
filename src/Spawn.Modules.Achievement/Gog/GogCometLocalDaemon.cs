using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Core;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Event payload emitted immediately when a GOG offline game unlocks an achievement via local Comet (<c>127.0.0.1:9977</c>).
/// </summary>
public sealed record GogCometAchievementCapturedEventArgs(
    string? ClientId,
    string? GameId,
    ParsedAchievementEntry Entry,
    string PersistedStateFilePath);

/// <summary>
/// Built-in local <c>127.0.0.1:9977</c> GOG Galaxy Communication Service (Comet-compatible) loopback daemon.
/// Captures <c>Galaxy.dll</c> / <c>Galaxy64.dll</c> Protobuf handshake and achievement unlock messages with zero file modification,
/// and yields gracefully when official GOG Galaxy already occupies port 9977.
/// </summary>
public sealed class GogCometLocalDaemon : IAsyncDisposable, IDisposable
{
    private readonly SpawnPaths _paths;
    private readonly ILogger<GogCometLocalDaemon> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _clientIdToGameId = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private int _disposed;

    public int ConfiguredPort { get; private set; } = 9977;
    public int BoundPort { get; private set; }
    public bool IsListening { get; private set; }
    public bool IsYieldedToOfficialGalaxy { get; private set; }

    public event Func<GogCometAchievementCapturedEventArgs, CancellationToken, Task>? AchievementCaptured;

    public GogCometLocalDaemon(SpawnPaths paths, ILogger<GogCometLocalDaemon>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _logger = logger ?? NullLogger<GogCometLocalDaemon>.Instance;
    }

    public void RegisterGameSession(string? gogGameId, string? gogClientId)
    {
        if (!string.IsNullOrWhiteSpace(gogClientId) && !string.IsNullOrWhiteSpace(gogGameId))
        {
            _clientIdToGameId[gogClientId.Trim()] = gogGameId.Trim();
        }
    }

    public string GetPersistedStateFilePath(string gogIdOrClientId)
    {
        string safe = string.Concat(gogIdOrClientId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string dir = Path.Combine(_paths.CacheDirectory, "gog_comet");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{safe}.json");
    }

    /// <summary>
    /// Starts the local loopback Comet listener on <paramref name="port"/> (defaults to <c>9977</c>; pass <c>0</c> for an ephemeral test port).
    /// Returns <c>true</c> if bound and listening, or <c>false</c> if yielded smoothly because the port is already occupied.
    /// </summary>
    public async Task<bool> StartAsync(int port = 9977, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsListening)
            {
                return true;
            }

            ConfiguredPort = port;
            IsYieldedToOfficialGalaxy = false;

            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                listener.Stop();
                IsListening = false;
                BoundPort = 0;
                IsYieldedToOfficialGalaxy = true;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Local port {Port} is already occupied (official GOG Galaxy active); yielding smoothly to gameplay.db read-only mode.",
                        port);
                }
                return false;
            }

            _listener = listener;
            BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            IsListening = true;
            _cts = new CancellationTokenSource();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);

            return true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loopToWait = null;
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                _listener?.Stop();
            }
            catch
            {
                // Ignore listener shutdown errors.
            }

            _listener = null;
            IsListening = false;
            BoundPort = 0;
            loopToWait = _acceptLoopTask;
            _acceptLoopTask = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (loopToWait is not null)
        {
            try
            {
                await loopToWait.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation during shutdown.
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Builds a standard GOG Galaxy Communication Service Protobuf frame (<c>uint16 BE headerLen + Protobuf Header + Protobuf Payload</c>).
    /// Exposed publicly so unit tests can construct authentic wire frames without external tools.
    /// </summary>
    public static byte[] EncodeCometProtobufFrame(uint sort, uint messageType, uint oseq, ReadOnlySpan<byte> payload)
    {
        byte[] headerBytes;
        using (var headerMs = new MemoryStream())
        {
            using var cos = new CodedOutputStream(headerMs, leaveOpen: true);
            cos.WriteTag(1, WireFormat.WireType.Varint);
            cos.WriteUInt32(sort);
            cos.WriteTag(2, WireFormat.WireType.Varint);
            cos.WriteUInt32(messageType);
            cos.WriteTag(3, WireFormat.WireType.Varint);
            cos.WriteUInt32((uint)payload.Length);
            if (oseq != 0)
            {
                cos.WriteTag(4, WireFormat.WireType.Varint);
                cos.WriteUInt32(oseq);
            }

            cos.Flush();
            headerBytes = headerMs.ToArray();
        }

        byte[] frame = new byte[2 + headerBytes.Length + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)headerBytes.Length);
        Buffer.BlockCopy(headerBytes, 0, frame, 2, headerBytes.Length);
        payload.CopyTo(frame.AsSpan(2 + headerBytes.Length));
        return frame;
    }

    public static byte[] EncodeAuthInfoRequestPayload(string clientId, string? clientSecret = null)
    {
        using var ms = new MemoryStream();
        using var cos = new CodedOutputStream(ms, leaveOpen: true);
        cos.WriteTag(1, WireFormat.WireType.LengthDelimited);
        cos.WriteString(clientId);
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            cos.WriteTag(2, WireFormat.WireType.LengthDelimited);
            cos.WriteString(clientSecret);
        }

        cos.Flush();
        return ms.ToArray();
    }

    public static byte[] EncodeUnlockAchievementPayload(string apiName, bool unlocked = true, ulong unlockTimeUnixSec = 0)
    {
        using var ms = new MemoryStream();
        using var cos = new CodedOutputStream(ms, leaveOpen: true);
        // Field 1: achievement key/name, Field 2: unlocked bool, Field 3: unlock_time uint64
        cos.WriteTag(1, WireFormat.WireType.LengthDelimited);
        cos.WriteString(apiName);
        cos.WriteTag(2, WireFormat.WireType.Varint);
        cos.WriteBool(unlocked);
        if (unlockTimeUnixSec > 0)
        {
            cos.WriteTag(3, WireFormat.WireType.Varint);
            cos.WriteUInt64(unlockTimeUnixSec);
        }

        cos.Flush();
        return ms.ToArray();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientConnectionAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            string? sessionClientId = null;
            string? sessionGameId = null;

            byte[] peekBuf = new byte[2];
            while (!ct.IsCancellationRequested)
            {
                int firstRead = await ReadUpToAsync(stream, peekBuf.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (firstRead == 0)
                {
                    break;
                }

                // Support lightweight JSON-line Comet webhook/test packets starting with '{'
                if (peekBuf[0] == (byte)'{')
                {
                    string? jsonLine = await ReadRemainingLineAsync(stream, (byte)'{', ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(jsonLine))
                    {
                        break;
                    }

                    (sessionClientId, sessionGameId) = await ProcessJsonMessageAsync(
                        stream, jsonLine, sessionClientId, sessionGameId, ct).ConfigureAwait(false);
                    continue;
                }

                int secondRead = await ReadExactOrEofAsync(stream, peekBuf.AsMemory(1, 1), ct).ConfigureAwait(false);
                if (secondRead == 0)
                {
                    break;
                }

                ushort headerLen = BinaryPrimitives.ReadUInt16BigEndian(peekBuf);
                if (headerLen == 0 || headerLen > 4096)
                {
                    break;
                }

                byte[] headerBuf = new byte[headerLen];
                if (await ReadExactOrEofAsync(stream, headerBuf, ct).ConfigureAwait(false) < headerLen)
                {
                    break;
                }

                var (sort, msgType, payloadSize, oseq) = ParseCometHeader(headerBuf);
                if (payloadSize > 1024 * 1024)
                {
                    break;
                }

                byte[] payloadBuf = payloadSize > 0 ? new byte[payloadSize] : [];
                if (payloadSize > 0 && await ReadExactOrEofAsync(stream, payloadBuf, ct).ConfigureAwait(false) < payloadSize)
                {
                    break;
                }

                (sessionClientId, sessionGameId) = await ProcessProtobufFrameAsync(
                    stream, sort, msgType, oseq, payloadBuf, sessionClientId, sessionGameId, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<(string? ClientId, string? GameId)> ProcessProtobufFrameAsync(
        NetworkStream stream,
        uint sort,
        uint msgType,
        uint oseq,
        byte[] payload,
        string? sessionClientId,
        string? sessionGameId,
        CancellationToken ct)
    {
        var (strings, numbers) = ExtractProtobufFields(payload);

        // Handshake / AuthInfoRequest (sort=1, msgType=1, or payload carrying numeric client_id string)
        if ((sort == 1 && msgType == 1)
            || (sessionClientId is null && strings.TryGetValue(1, out string? maybeClientId) && maybeClientId.All(char.IsDigit) && maybeClientId.Length >= 8))
        {
            if (strings.TryGetValue(1, out string? cid) && !string.IsNullOrWhiteSpace(cid))
            {
                sessionClientId = cid.Trim();
                if (_clientIdToGameId.TryGetValue(sessionClientId, out string? mappedGameId))
                {
                    sessionGameId = mappedGameId;
                }
            }

            byte[] authRespPayload = BuildAuthInfoResponsePayload();
            byte[] respFrame = EncodeCometProtobufFrame(sort == 0 ? 1u : sort, msgType + 1, oseq, authRespPayload);
            await stream.WriteAsync(respFrame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return (sessionClientId, sessionGameId);
        }

        // Achievement unlock message detection across Galaxy SDK versions:
        // Looks for achievement key string in field 1/2/3 that is NOT purely numeric client_id.
        string? apiName = TryExtractAchievementKeyFromFields(strings);
        if (!string.IsNullOrWhiteSpace(apiName))
        {
            bool unlocked = !numbers.TryGetValue(2, out ulong unlockedFlag) || unlockedFlag != 0;
            ulong tsSec = numbers.TryGetValue(3, out ulong rawTs) && rawTs > 100_000_000UL
                ? rawTs
                : (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await RecordAndDispatchCaptureAsync(sessionClientId, sessionGameId, apiName, unlocked, (long)tsSec, ct).ConfigureAwait(false);

            byte[] ackFrame = EncodeCometProtobufFrame(sort == 0 ? 2u : sort, msgType + 1, oseq, []);
            await stream.WriteAsync(ackFrame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return (sessionClientId, sessionGameId);
        }

        // Generic ACK response for GetUserStats / SubscribeTopic / Heartbeat
        byte[] genericAck = EncodeCometProtobufFrame(sort == 0 ? 1u : sort, msgType + 1, oseq, []);
        await stream.WriteAsync(genericAck, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return (sessionClientId, sessionGameId);
    }

    private async Task<(string? ClientId, string? GameId)> ProcessJsonMessageAsync(
        NetworkStream stream,
        string jsonLine,
        string? sessionClientId,
        string? sessionGameId,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (sessionClientId, sessionGameId);
            }

            if (root.TryGetProperty("clientId", out var cidProp) && cidProp.ValueKind == JsonValueKind.String)
            {
                sessionClientId = cidProp.GetString();
            }

            if (root.TryGetProperty("gameId", out var gidProp) && gidProp.ValueKind == JsonValueKind.String)
            {
                sessionGameId = gidProp.GetString();
            }
            else if (!string.IsNullOrWhiteSpace(sessionClientId)
                     && _clientIdToGameId.TryGetValue(sessionClientId, out string? mapped))
            {
                sessionGameId = mapped;
            }

            string? type = root.TryGetProperty("type", out var tProp) && tProp.ValueKind == JsonValueKind.String
                ? tProp.GetString()
                : null;

            string? apiName = null;
            foreach (string keyField in new[] { "achievement", "apiName", "key", "achievementId" })
            {
                if (root.TryGetProperty(keyField, out var ap) && ap.ValueKind == JsonValueKind.String)
                {
                    apiName = ap.GetString();
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(apiName))
            {
                bool unlocked = !root.TryGetProperty("unlocked", out var uProp)
                                || uProp.ValueKind != JsonValueKind.False;
                long ts = root.TryGetProperty("unlockTime", out var tsProp) && tsProp.TryGetInt64(out long parsedTs)
                    ? parsedTs
                    : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                await RecordAndDispatchCaptureAsync(sessionClientId, sessionGameId, apiName, unlocked, ts, ct).ConfigureAwait(false);
            }

            string ack = string.Equals(type, "handshake", StringComparison.OrdinalIgnoreCase)
                ? "{\"ok\":true,\"status\":\"authenticated\",\"userName\":\"SPAWN\"}\n"
                : "{\"ok\":true}\n";
            byte[] ackBytes = Encoding.UTF8.GetBytes(ack);
            await stream.WriteAsync(ackBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore malformed client frame.
        }

        return (sessionClientId, sessionGameId);
    }

    private async Task RecordAndDispatchCaptureAsync(
        string? clientId,
        string? gameId,
        string apiName,
        bool unlocked,
        long unlockUnixSec,
        CancellationToken ct)
    {
        string bucketKey = !string.IsNullOrWhiteSpace(gameId)
            ? gameId!
            : (!string.IsNullOrWhiteSpace(clientId) ? clientId! : "default");

        string persistedPath = GetPersistedStateFilePath(bucketKey);
        await SteamEmulatorAchievementProvider.TrySyncLocalGoldbergSaveAsync(
            persistedPath, apiName, unlocked, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(gameId)
            && !string.Equals(clientId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            string clientPath = GetPersistedStateFilePath(clientId);
            await SteamEmulatorAchievementProvider.TrySyncLocalGoldbergSaveAsync(
                clientPath, apiName, unlocked, ct).ConfigureAwait(false);
        }

        var entry = new ParsedAchievementEntry
        {
            ApiName = apiName.Trim(),
            IsUnlocked = unlocked,
            UnlockedUtc = unlocked ? SteamEmulatorFormatParsers.UnixTimestampToUtc(unlockUnixSec) ?? DateTimeOffset.UtcNow : null,
        };

        var handlers = AchievementCaptured;
        if (handlers is not null)
        {
            var args = new GogCometAchievementCapturedEventArgs(clientId, gameId, entry, persistedPath);
            foreach (var d in handlers.GetInvocationList())
            {
                if (d is Func<GogCometAchievementCapturedEventArgs, CancellationToken, Task> fn)
                {
                    try
                    {
                        await fn(args, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in GogCometLocalDaemon AchievementCaptured subscriber.");
                    }
                }
            }
        }
    }

    private static (uint Sort, uint Type, int PayloadSize, uint Oseq) ParseCometHeader(byte[] headerBytes)
    {
        uint sort = 0;
        uint type = 0;
        uint size = 0;
        uint oseq = 0;

        try
        {
            using var cis = new CodedInputStream(headerBytes);
            uint tag;
            while ((tag = cis.ReadTag()) != 0)
            {
                int fieldNumber = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                if (wireType == WireFormat.WireType.Varint)
                {
                    uint val = cis.ReadUInt32();
                    switch (fieldNumber)
                    {
                        case 1: sort = val; break;
                        case 2: type = val; break;
                        case 3: size = val; break;
                        case 4: oseq = val; break;
                    }
                }
                else
                {
                    cis.SkipLastField();
                }
            }
        }
        catch
        {
            // Ignore malformed header fields.
        }

        return (sort, type, (int)Math.Min(size, 1024 * 1024), oseq);
    }

    private static (Dictionary<int, string> Strings, Dictionary<int, ulong> Numbers) ExtractProtobufFields(byte[] payload)
    {
        var strings = new Dictionary<int, string>();
        var numbers = new Dictionary<int, ulong>();
        if (payload.Length == 0)
        {
            return (strings, numbers);
        }

        try
        {
            using var cis = new CodedInputStream(payload);
            uint tag;
            while ((tag = cis.ReadTag()) != 0)
            {
                int fieldNum = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                switch (wireType)
                {
                    case WireFormat.WireType.Varint:
                        numbers[fieldNum] = cis.ReadUInt64();
                        break;

                    case WireFormat.WireType.LengthDelimited:
                        ByteString bs = cis.ReadBytes();
                        byte[] raw = bs.ToByteArray();
                        if (IsPrintableUtf8(raw))
                        {
                            strings[fieldNum] = Encoding.UTF8.GetString(raw);
                        }
                        else if (raw.Length > 0)
                        {
                            // Recurse 1 level for nested AchievementEntry messages inside StoreStats/UpdateUserAchievement
                            var (nestedStr, nestedNum) = ExtractNestedSingleLevel(raw);
                            foreach (var (nk, nv) in nestedStr)
                            {
                                strings.TryAdd(nk, nv);
                            }

                            foreach (var (nk, nv) in nestedNum)
                            {
                                numbers.TryAdd(nk, nv);
                            }
                        }
                        break;

                    case WireFormat.WireType.Fixed32:
                        numbers[fieldNum] = cis.ReadFixed32();
                        break;

                    case WireFormat.WireType.Fixed64:
                        numbers[fieldNum] = cis.ReadFixed64();
                        break;

                    default:
                        cis.SkipLastField();
                        break;
                }
            }
        }
        catch
        {
            // Ignore truncated unknown payload tail.
        }

        return (strings, numbers);
    }

    private static (Dictionary<int, string> Strings, Dictionary<int, ulong> Numbers) ExtractNestedSingleLevel(byte[] raw)
    {
        var strings = new Dictionary<int, string>();
        var numbers = new Dictionary<int, ulong>();
        try
        {
            using var cis = new CodedInputStream(raw);
            uint tag;
            while ((tag = cis.ReadTag()) != 0)
            {
                int f = WireFormat.GetTagFieldNumber(tag);
                var wt = WireFormat.GetTagWireType(tag);
                if (wt == WireFormat.WireType.Varint)
                {
                    numbers[f] = cis.ReadUInt64();
                }
                else if (wt == WireFormat.WireType.LengthDelimited)
                {
                    byte[] b = cis.ReadBytes().ToByteArray();
                    if (IsPrintableUtf8(b))
                    {
                        strings[f] = Encoding.UTF8.GetString(b);
                    }
                }
                else
                {
                    cis.SkipLastField();
                }
            }
        }
        catch
        {
            // Ignore.
        }

        return (strings, numbers);
    }

    private static string? TryExtractAchievementKeyFromFields(Dictionary<int, string> strings)
    {
        foreach (int field in new[] { 1, 2, 3, 4 })
        {
            if (strings.TryGetValue(field, out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                string trimmed = s.Trim();
                // Skip pure numeric GOG clientIds/userIds or bearer tokens
                if (trimmed.All(char.IsDigit) && trimmed.Length >= 8)
                {
                    continue;
                }

                if (trimmed.Length is >= 1 and <= 128 && !trimmed.Contains(' '))
                {
                    return trimmed;
                }
            }
        }

        return null;
    }

    private static byte[] BuildAuthInfoResponsePayload()
    {
        using var ms = new MemoryStream();
        using var cos = new CodedOutputStream(ms, leaveOpen: true);
        // Galaxy AuthInfoResponse: 1 = refresh_token, 2 = access_token_expires_in, 3 = user_id (46687792954896523), 4 = user_name
        cos.WriteTag(1, WireFormat.WireType.LengthDelimited);
        cos.WriteString("spawn-local-offline-token");
        cos.WriteTag(2, WireFormat.WireType.Varint);
        cos.WriteUInt32(86400);
        cos.WriteTag(3, WireFormat.WireType.Varint);
        cos.WriteUInt64(46687792954896523UL);
        cos.WriteTag(4, WireFormat.WireType.LengthDelimited);
        cos.WriteString("SPAWN_Player");
        cos.Flush();
        return ms.ToArray();
    }

    private static bool IsPrintableUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 256)
        {
            return false;
        }

        foreach (byte b in bytes)
        {
            if (b < 0x20 || b == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<int> ReadUpToAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            return await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static async Task<int> ReadExactOrEofAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await ReadUpToAsync(stream, buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<string?> ReadRemainingLineAsync(NetworkStream stream, byte firstByte, CancellationToken ct)
    {
        using var ms = new MemoryStream(256);
        ms.WriteByte(firstByte);
        byte[] single = new byte[1];
        while (ms.Length < 65536)
        {
            int r = await ReadUpToAsync(stream, single, ct).ConfigureAwait(false);
            if (r == 0 || single[0] == (byte)'\n')
            {
                break;
            }

            if (single[0] != (byte)'\r')
            {
                ms.WriteByte(single[0]);
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // Ignore.
        }

        _cts?.Dispose();
        _lifecycleLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }
}
