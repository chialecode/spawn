using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Ipc;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class TrainerPipeAuthTests
{
    [Fact]
    public void SingleInstanceServerStream_SecondInstanceOnSamePipeName_FailsToPreventSquatting()
    {
        using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();

        Assert.StartsWith("spawn-test-ipc-", pipeName, StringComparison.Ordinal);
        string suffix = pipeName[sandbox.Paths.TrainerPipeNamePrefix.Length..];
        Assert.Equal(32, suffix.Length); // 128-bit hex suffix

        using NamedPipeServerStream first = NamedPipeSecurityFactory.CreateSingleInstanceSecureServerStream(pipeName);
        Assert.NotNull(first);

        Exception ex = Assert.ThrowsAny<Exception>(
            () => NamedPipeSecurityFactory.CreateSingleInstanceSecureServerStream(pipeName));
        Assert.True(
            ex is UnauthorizedAccessException or IOException,
            $"Expected UnauthorizedAccessException or IOException for duplicate single-instance pipe, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void SingleInstanceServerStream_AclGrantsCurrentUserAndDeniesNetworkSid()
    {
        using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();

        using NamedPipeServerStream serverStream = NamedPipeSecurityFactory.CreateSingleInstanceSecureServerStream(pipeName);
        PipeSecurity acl = serverStream.GetAccessControl();
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier currentUserSid = identity.User!;
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        Assert.Contains(rules, r => r.IdentityReference.Equals(currentUserSid) && r.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, r => r.IdentityReference.Equals(networkSid) && r.AccessControlType == AccessControlType.Deny);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(worldSid) && r.AccessControlType == AccessControlType.Allow);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(authUsersSid) && r.AccessControlType == AccessControlType.Allow);
    }

    [Fact]
    public void SingleUseLaunchNonce_Is256BitAndBurnsImmediatelyAfterFirstVerification()
    {
        using var nonce = SingleUseLaunchNonce.Generate();
        Assert.False(nonce.IsConsumed);
        Assert.Equal(43, nonce.EncodedValue.Length); // 32 bytes Base64Url without padding

        // Valid verification succeeds once and immediately burns the nonce.
        Assert.True(nonce.VerifyAndBurn(nonce.EncodedValue));
        Assert.True(nonce.IsConsumed);

        // Replay of the exact same nonce MUST fail.
        Assert.False(nonce.VerifyAndBurn(nonce.EncodedValue));

        // A wrong nonce also burns on first attempt so brute force cannot retry.
        using var nonce2 = SingleUseLaunchNonce.Generate();
        using var different = SingleUseLaunchNonce.Generate();
        Assert.False(nonce2.VerifyAndBurn(different.EncodedValue));
        Assert.True(nonce2.IsConsumed);
        Assert.False(nonce2.VerifyAndBurn(nonce2.EncodedValue));
    }

    [Fact]
    public async Task TrainerPipe_MutualAuthWithRealKernelPeerInspection_SucceedsAndExchangesBidirectionalRpc()
    {
        await using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [currentExe],
        });

        var createdTcs = new TaskCompletionSource<TrainerProcessCreatedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterNotificationHandler<TrainerProcessCreatedNotification>(
            TrainerIpcMethods.ProcessCreated,
            (payload, _) =>
            {
                if (payload is not null)
                {
                    createdTcs.TrySetResult(payload);
                }

                return Task.CompletedTask;
            });

        server.StartListening();

        await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
        {
            PipeName = pipeName,
            ExpectedHostPid = currentPid,
            LaunchNonce = server.LaunchNonce,
            WorkerIntegrityLevel = 0x2000,
            ExpectedHostExecutablePaths = [currentExe],
        });

        client.RegisterHandler<TrainerTriggerHotkeyRequest, TrainerTriggerHotkeyResponse>(
            TrainerIpcMethods.TriggerHotkey,
            (req, _) => Task.FromResult(new TrainerTriggerHotkeyResponse
            {
                Succeeded = true,
                EventsInjected = req is null ? 0 : 2,
            }));

        Task<AuthenticatedTrainerWorkerInfo> acceptTask = server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));
        Task connectTask = client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(5));

        await Task.WhenAll(acceptTask, connectTask);
        AuthenticatedTrainerWorkerInfo workerInfo = await acceptTask;

        Assert.Equal(currentPid, workerInfo.WorkerPid);
        Assert.Equal(0x2000, workerInfo.WorkerIntegrityLevel);
        Assert.True(TrainerPipePathResolver.PathsEqual(currentExe, workerInfo.WorkerExecutablePath));
        Assert.Equal(currentPid, client.AuthenticatedHostPid);

        // Host -> Worker RPC
        TrainerTriggerHotkeyResponse? hotkeyReply = await server.InvokeAsync<TrainerTriggerHotkeyRequest, TrainerTriggerHotkeyResponse>(
            TrainerIpcMethods.TriggerHotkey,
            new TrainerTriggerHotkeyRequest
            {
                SessionId = "sess-1",
                EntryId = "entry-1",
                VirtualKey = 0x61,
            });
        Assert.NotNull(hotkeyReply);
        Assert.True(hotkeyReply!.Succeeded);
        Assert.Equal(2, hotkeyReply.EventsInjected);

        // Worker -> Host notification
        await client.NotifyAsync(
            TrainerIpcMethods.ProcessCreated,
            new TrainerProcessCreatedNotification
            {
                SessionId = "sess-1",
                ProcessId = 4242,
                ImagePath = @"C:\Sandbox\trainer.exe",
            });

        TrainerProcessCreatedNotification received = await createdTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4242, received.ProcessId);
        Assert.Equal("sess-1", received.SessionId);
    }

    [Fact]
    public async Task TrainerPipe_ServerRejectsSpoofedNonce_AndDisconnectsClient()
    {
        await using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [currentExe],
        });
        server.StartListening();

        using var wrongNonce = SingleUseLaunchNonce.Generate();
        await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
        {
            PipeName = pipeName,
            ExpectedHostPid = currentPid,
            LaunchNonce = wrongNonce.EncodedValue,
            ExpectedHostExecutablePaths = [currentExe],
        });

        Task<AuthenticatedTrainerWorkerInfo> acceptTask = server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));
        Task connectTask = client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(5));

        TrainerPipeAuthenticationException serverEx = await Assert.ThrowsAsync<TrainerPipeAuthenticationException>(() => acceptTask);
        Assert.Equal("INVALID_NONCE", serverEx.ReasonCode);

        await Assert.ThrowsAsync<TrainerPipeAuthenticationException>(() => connectTask);
        Assert.False(server.IsAuthenticated);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task TrainerPipe_ServerRejectsClientPidMismatch_AndDisconnectsImmediately()
    {
        await using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        var spoofedInspector = new FakePeerInspector(
            clientPid: 99999,
            serverPid: currentPid,
            pathResolver: pid => pid == currentPid ? currentExe : @"C:\Fake\Spawn.TrainerWorker.exe");

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [currentExe],
            PeerInspector = spoofedInspector,
        });
        server.StartListening();

        await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
        {
            PipeName = pipeName,
            ExpectedHostPid = currentPid,
            LaunchNonce = server.LaunchNonce,
            ExpectedHostExecutablePaths = [currentExe],
            PeerInspector = spoofedInspector,
        });

        Task<AuthenticatedTrainerWorkerInfo> acceptTask = server.WaitForAuthenticatedClientAsync(expectedWorkerPid: 12345, TimeSpan.FromSeconds(5));
        Task connectTask = client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(5));

        TrainerPipeAuthenticationException serverEx = await Assert.ThrowsAsync<TrainerPipeAuthenticationException>(() => acceptTask);
        Assert.Equal("CLIENT_PID_MISMATCH", serverEx.ReasonCode);
        await Assert.ThrowsAnyAsync<Exception>(() => connectTask);
        Assert.False(server.IsAuthenticated);
    }

    [Fact]
    public async Task TrainerPipe_ServerRejectsNonWorkerClientPath_AndDisconnectsImmediately()
    {
        await using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        var roguePathInspector = new FakePeerInspector(
            clientPid: currentPid,
            serverPid: currentPid,
            pathResolver: _ => Path.Combine(sandbox.Paths.DataRoot, "rogue", "evil-client.exe"));

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [@"C:\Program Files\SPAWN\Workers\Trainer\Spawn.TrainerWorker.exe"],
            PeerInspector = roguePathInspector,
        });
        server.StartListening();

        await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
        {
            PipeName = pipeName,
            ExpectedHostPid = currentPid,
            LaunchNonce = server.LaunchNonce,
            ExpectedHostExecutablePaths = [currentExe],
        });

        Task<AuthenticatedTrainerWorkerInfo> acceptTask = server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));
        Task connectTask = client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(5));

        TrainerPipeAuthenticationException serverEx = await Assert.ThrowsAsync<TrainerPipeAuthenticationException>(() => acceptTask);
        Assert.Equal("CLIENT_PATH_MISMATCH", serverEx.ReasonCode);
        await Assert.ThrowsAnyAsync<Exception>(() => connectTask);
    }

    [Fact]
    public async Task TrainerPipe_ServerRejectsNonHandshakeFirstFrame()
    {
        await using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();
        string currentExe = Environment.ProcessPath!;
        int currentPid = Environment.ProcessId;

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [currentExe],
        });
        server.StartListening();

        Task<AuthenticatedTrainerWorkerInfo> acceptTask = server.WaitForAuthenticatedClientAsync(currentPid, TimeSpan.FromSeconds(5));

        await using var rawClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await rawClient.ConnectAsync(3000);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new NamedPipeRpcMessage
            {
                Id = "1",
                Method = TrainerIpcMethods.Launch,
                Params = JsonSerializer.SerializeToElement(new { sessionId = "bypass" }, NamedPipeJsonRpc.SerializerOptions),
            },
            NamedPipeJsonRpc.SerializerOptions);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await rawClient.WriteAsync(header);
        await rawClient.WriteAsync(payload);
        await rawClient.FlushAsync();

        TrainerPipeAuthenticationException serverEx = await Assert.ThrowsAsync<TrainerPipeAuthenticationException>(() => acceptTask);
        Assert.Equal("INVALID_FIRST_FRAME", serverEx.ReasonCode);
        Assert.False(server.IsAuthenticated);
    }

    [Fact]
    public async Task TrainerPipe_ClientRejectsWrongServerPid_WithoutTransmittingNonce()
    {
        await using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();
        string fakeHostPath = @"C:\App\Spawn.Host.exe";
        const int expectedHostPid = 22222;
        const int rogueServerPid = 33333;

        var spoofedServerInspector = new FakePeerInspector(
            clientPid: Environment.ProcessId,
            serverPid: rogueServerPid,
            pathResolver: pid => pid == expectedHostPid ? fakeHostPath : @"C:\Rogue\FakeHost.exe");

        await using var server = new TrainerPipeServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedWorkerExecutablePaths = [Environment.ProcessPath!],
        });
        server.StartListening();

        await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
        {
            PipeName = pipeName,
            ExpectedHostPid = expectedHostPid,
            LaunchNonce = server.LaunchNonce,
            ExpectedHostExecutablePaths = [fakeHostPath],
            PeerInspector = spoofedServerInspector,
        });

        Task<AuthenticatedTrainerWorkerInfo> acceptTask = server.WaitForAuthenticatedClientAsync(Environment.ProcessId, TimeSpan.FromSeconds(2));

        TrainerPipeAuthenticationException clientEx = await Assert.ThrowsAsync<TrainerPipeAuthenticationException>(
            () => client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("SERVER_PID_MISMATCH", clientEx.ReasonCode);
        Assert.False(client.IsConnected);

        // Server never received a handshake frame and disconnects cleanly.
        await Assert.ThrowsAnyAsync<Exception>(() => acceptTask);
    }

    [Fact]
    public async Task TrainerPipe_ClientRejectsWrongHostPathPreConnect()
    {
        await using var sandbox = new TestSandbox();
        string pipeName = sandbox.Paths.CreateTrainerSessionPipeName();

        var wrongHostInspector = new FakePeerInspector(
            clientPid: Environment.ProcessId,
            serverPid: Environment.ProcessId,
            pathResolver: _ => @"C:\Temp\NotSpawnHost.exe");

        await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
        {
            PipeName = pipeName,
            ExpectedHostPid = Environment.ProcessId,
            LaunchNonce = SingleUseLaunchNonce.Generate().EncodedValue,
            ExpectedHostExecutablePaths = [@"C:\Install\Spawn.Host.exe", @"C:\Install\spawn.exe"],
            PeerInspector = wrongHostInspector,
        });

        TrainerPipeAuthenticationException ex = await Assert.ThrowsAsync<TrainerPipeAuthenticationException>(
            () => client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("HOST_PRECONNECT_PATH_MISMATCH", ex.ReasonCode);
    }

    [Fact]
    public void TrainerPipePathResolver_PackagedWorkersLayout_ResolvesExactParentHostBinaries()
    {
        string installRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? @"C:\", "SpawnInstall", "spawn-dev");
        string workerExe = Path.Combine(installRoot, "Workers", "Trainer", "Spawn.TrainerWorker.exe");
        IReadOnlyList<string> allowedHosts = TrainerPipePathResolver.ResolveDefaultAllowedHostExecutablePaths(workerExe);

        Assert.Contains(Path.Combine(installRoot, "Spawn.Host.exe"), allowedHosts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(installRoot, "spawn.exe"), allowedHosts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(installRoot, "spawn-dev.exe"), allowedHosts, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakePeerInspector(
        int clientPid,
        int serverPid,
        Func<int, string?> pathResolver) : ITrainerPipePeerInspector
    {
        public bool TryGetClientProcessId(SafeHandle pipeHandle, out int processId)
        {
            processId = clientPid;
            return clientPid > 4;
        }

        public bool TryGetServerProcessId(SafeHandle pipeHandle, out int processId)
        {
            processId = serverPid;
            return serverPid > 4;
        }

        public string? TryGetProcessExecutablePath(int processId) => pathResolver(processId);
    }
}
