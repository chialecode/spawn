using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Spawn.Contracts;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class NamedPipeIpcTests
{
    [Fact]
    public void NamedPipeSecurityFactory_CurrentUserAcl_GrantsUserSidAndExplicitlyDeniesNetworkSid()
    {
        PipeSecurity security = NamedPipeSecurityFactory.CreateCurrentUserPipeSecurity();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier currentUserSid = identity.User!;
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        Assert.Contains(rules, r =>
            r.IdentityReference.Equals(currentUserSid)
            && r.AccessControlType == AccessControlType.Allow);

        Assert.Contains(rules, r =>
            r.IdentityReference.Equals(networkSid)
            && r.AccessControlType == AccessControlType.Deny);

        Assert.DoesNotContain(rules, r =>
            r.IdentityReference.Equals(worldSid)
            && r.AccessControlType == AccessControlType.Allow);
    }

    [Fact]
    public async Task NamedPipeJsonRpc_MultiClientBidirectionalRpcAndServerBroadcast_DeliversCleanlyWithoutTcp()
    {
        string pipeName = $"spawn-unit-rpc-{Guid.NewGuid():N}";
        await using var server = NamedPipeJsonRpc.CreateServer(pipeName);

        server.RegisterHandler<int, int>("math.double", (val, _) => Task.FromResult(val * 2));
        await server.StartAsync();

        await using var clientA = NamedPipeJsonRpc.CreateClient(pipeName);
        await using var clientB = NamedPipeJsonRpc.CreateClient(pipeName);

        var broadcastReceivedA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcastReceivedB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        clientA.RegisterNotificationHandler<string>("host.event", (msg, _) =>
        {
            broadcastReceivedA.TrySetResult(msg ?? string.Empty);
            return Task.CompletedTask;
        });
        clientB.RegisterNotificationHandler<string>("host.event", (msg, _) =>
        {
            broadcastReceivedB.TrySetResult(msg ?? string.Empty);
            return Task.CompletedTask;
        });

        int resA = await clientA.InvokeAsync<int, int>("math.double", 21);
        int resB = await clientB.InvokeAsync<int, int>("math.double", 50);
        Assert.Equal(42, resA);
        Assert.Equal(100, resB);

        int broadcastCount = await server.BroadcastAsync("host.event", "ping-all-clients");
        Assert.Equal(2, broadcastCount);

        Assert.Equal("ping-all-clients", await broadcastReceivedA.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("ping-all-clients", await broadcastReceivedB.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OverlayIpcServerAndClient_ToastSingleBatchHeartbeatAndShutdown_RoundTripsSilently()
    {
        await using var sandbox = new TestSandbox();
        var receivedToasts = new List<OverlayToastRequest>();
        var shutdownCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var overlayServer = new OverlayIpcServer(sandbox.Paths)
        {
            HeartbeatProvider = () => new OverlayHeartbeatReply
            {
                ProcessId = Environment.ProcessId,
                WindowHandle = 0x12345,
                PendingToastCount = receivedToasts.Count,
            },
        };

        overlayServer.ToastReceived += (toast, _) =>
        {
            lock (receivedToasts)
            {
                receivedToasts.Add(toast);
            }

            return Task.CompletedTask;
        };

        overlayServer.ShutdownRequested += _ =>
        {
            shutdownCalled.TrySetResult(true);
            return Task.CompletedTask;
        };

        await overlayServer.StartAsync();

        await using var overlayClient = new OverlayIpcClient(sandbox.Paths);

        bool singleOk = await overlayClient.SendToastAsync(new OverlayToastRequest
        {
            GameId = "game-limbo",
            GameTitle = "LIMBO",
            AchievementApiName = "ACH_WRONG_WAY",
            DisplayName = "走错方向",
            Description = "那就太不对了",
            Theme = OverlayThemeKind.Steam,
            MuteSound = true,
            SoundVolume = 0.0,
        });
        Assert.True(singleOk);

        int batchCount = await overlayClient.SendToastBatchAsync(
        [
            new OverlayToastRequest
            {
                GameTitle = "LIMBO",
                AchievementApiName = "ACH_ALTITUDE",
                DisplayName = "恐高症",
                Theme = OverlayThemeKind.Ps5Trophy,
                MuteSound = true,
                SoundVolume = 0.0,
            },
            new OverlayToastRequest
            {
                GameTitle = "LIMBO",
                AchievementApiName = "ACH_DING",
                DisplayName = "叮！",
                IsRare = true,
                GlobalPercent = 3.8,
                IsPlatinumCompletion = true,
                Theme = OverlayThemeKind.XboxRare,
                MuteSound = true,
                SoundVolume = 0.0,
            },
        ]);
        Assert.Equal(2, batchCount);

        var heartbeat = await overlayClient.PingAsync();
        Assert.NotNull(heartbeat);
        Assert.Equal(3, heartbeat!.PendingToastCount);
        Assert.Equal(0x12345, heartbeat.WindowHandle);

        bool shutdownAck = await overlayClient.RequestShutdownAsync();
        Assert.True(shutdownAck);
        Assert.True(await shutdownCalled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
