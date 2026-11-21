using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Ipc;

namespace Spawn.Modules.Progress.Runtime;

/// <summary>
/// Reusable Level 2 single-UAC bridge for M3-W08's <see cref="IDefenderExclusionAssistant"/>:
/// spawns a one-shot High IL <c>Spawn.TrainerWorker</c> over an inverted single-instance Named Pipe
/// to execute WMI <c>MSFT_MpPreference.Add(ExclusionPath=[sandboxPath])</c> + readback verification without <c>powershell.exe</c>.
/// </summary>
public interface IElevatedDefenderExecutor
{
    Task<TrainerAddDefenderExclusionResponse> AddSandboxExclusionAsync(
        string sandboxPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory abstraction for constructing <see cref="TrainerPipeServer"/> instances (injectable for unit tests).
/// </summary>
public interface ITrainerPipeServerFactory
{
    TrainerPipeServer CreateServer(TrainerPipeServerOptions options);
}

public sealed class DefaultTrainerPipeServerFactory : ITrainerPipeServerFactory
{
    public static DefaultTrainerPipeServerFactory Instance { get; } = new();

    public TrainerPipeServer CreateServer(TrainerPipeServerOptions options) => new(options);
}

/// <summary>
/// Production implementation of <see cref="IElevatedDefenderExecutor"/> reusing the Level 2 <c>Spawn.TrainerWorker</c>
/// inverted-pipe handshake channel.
/// </summary>
public sealed class WorkerElevatedDefenderExecutor : IElevatedDefenderExecutor
{
    private readonly SpawnPaths _paths;
    private readonly ITrainerWorkerLauncher _workerLauncher;
    private readonly ITrainerPipeServerFactory _pipeServerFactory;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _rpcTimeout;

    public WorkerElevatedDefenderExecutor(
        SpawnPaths paths,
        ITrainerWorkerLauncher? workerLauncher = null,
        ITrainerPipeServerFactory? pipeServerFactory = null,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? rpcTimeout = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _workerLauncher = workerLauncher ?? new Win32TrainerWorkerLauncher();
        _pipeServerFactory = pipeServerFactory ?? DefaultTrainerPipeServerFactory.Instance;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _rpcTimeout = rpcTimeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<TrainerAddDefenderExclusionResponse> AddSandboxExclusionAsync(
        string sandboxPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sandboxPath))
        {
            return new TrainerAddDefenderExclusionResponse
            {
                Succeeded = false,
                VerifiedByReadback = false,
                Status = DefenderExclusionStatus.Failed,
                ErrorMessage = "沙箱排除路径不能为空。",
            };
        }

        string pipeName = _paths.CreateTrainerSessionPipeName();
        using var nonce = SingleUseLaunchNonce.Generate();
        string? expectedWorkerPath = _workerLauncher.ResolveWorkerExecutablePath();

        await using TrainerPipeServer server = _pipeServerFactory.CreateServer(new TrainerPipeServerOptions
        {
            PipeName = pipeName,
            LaunchNonce = nonce,
            ExpectedWorkerExecutablePaths = string.IsNullOrWhiteSpace(expectedWorkerPath) ? null : [expectedWorkerPath],
        });

        server.StartListening();

        WorkerProcessLaunchResult launch = await _workerLauncher.LaunchWorkerAsync(
            TrainerElevationLevel.Level2HighIlUac,
            pipeName,
            nonce.EncodedValue,
            cancellationToken).ConfigureAwait(false);

        if (!launch.Succeeded)
        {
            return new TrainerAddDefenderExclusionResponse
            {
                Succeeded = false,
                VerifiedByReadback = false,
                Status = launch.WasCancelledByUser
                    ? DefenderExclusionStatus.UacCancelledByUser
                    : DefenderExclusionStatus.Failed,
                ErrorMessage = launch.ErrorMessage ?? "无法启动高权限 Worker 执行 Defender 排除配置。",
            };
        }

        try
        {
            _ = await server.WaitForAuthenticatedClientAsync(
                launch.WorkerProcessId,
                _handshakeTimeout,
                cancellationToken).ConfigureAwait(false);

            TrainerAddDefenderExclusionResponse? response = await server.InvokeAsync<TrainerAddDefenderExclusionRequest, TrainerAddDefenderExclusionResponse>(
                TrainerIpcMethods.AddDefenderExclusion,
                new TrainerAddDefenderExclusionRequest { SandboxPath = sandboxPath.Trim() },
                _rpcTimeout,
                cancellationToken).ConfigureAwait(false);

            try
            {
                _ = await server.InvokeAsync<TrainerStopRequest, TrainerStopResponse>(
                    TrainerIpcMethods.Stop,
                    new TrainerStopRequest
                    {
                        SessionId = "defender-exclusion-oneshot",
                        GracefulTimeoutMs = 200,
                        Reason = TrainerShutdownReason.UserRequested,
                    },
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Pipe disposal below also triggers immediate worker exit.
            }

            return response ?? new TrainerAddDefenderExclusionResponse
            {
                Succeeded = false,
                VerifiedByReadback = false,
                Status = DefenderExclusionStatus.Failed,
                ErrorMessage = "高权限 Worker 未返回有效的 Defender 排除结果。",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _workerLauncher.TryTerminateWorkerProcess(launch.WorkerProcessId);
            return new TrainerAddDefenderExclusionResponse
            {
                Succeeded = false,
                VerifiedByReadback = false,
                Status = DefenderExclusionStatus.Failed,
                ErrorMessage = ex.Message,
            };
        }
    }
}
