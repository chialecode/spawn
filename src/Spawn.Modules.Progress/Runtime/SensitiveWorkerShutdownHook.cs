using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Runtime;

/// <summary>
/// Read-only sandbox directory orphan process scanner &amp; targeted terminator for LOOP-06 pre-launch purge.
/// Strictly limited to SPAWN's own <c>TrainerSandboxDirectory</c>; NEVER kills user-launched third-party tools outside the sandbox.
/// </summary>
public interface ISandboxProcessScanner
{
    IReadOnlyList<SafeProcessEntry> FindProcessesUnderSandbox(string sandboxRoot);
    bool TryTerminateProcess(int processId);
}

/// <summary>
/// Clean-Host compliant implementation using <see cref="SafeProcessInspector.FindProcessesUnderDirectory"/> (<c>0x1000</c>).
/// </summary>
public sealed class SafeSandboxProcessScanner : ISandboxProcessScanner
{
    public static SafeSandboxProcessScanner Instance { get; } = new();

    public IReadOnlyList<SafeProcessEntry> FindProcessesUnderSandbox(string sandboxRoot) =>
        SafeProcessInspector.FindProcessesUnderDirectory(sandboxRoot, excludeAuxiliary: false);

    public bool TryTerminateProcess(int processId)
    {
        if (processId <= 4)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(processId);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                return proc.WaitForExit(500);
            }

            return true;
        }
        catch (ArgumentException)
        {
            // Already exited.
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// LOOP-06 pre-flight shutdown hook (<see cref="ISensitiveWorkerShutdownHook"/>):
/// invoked by <c>GameLaunchService</c> before launching any <c>Tier 2/3</c> protected game.
/// Stops all active SPAWN trainer sessions within <paramref name="budget"/>, sweeps SPAWN's own trainer sandbox
/// for surviving orphan processes, and confirms clean exit before returning.
/// </summary>
public sealed class SensitiveWorkerShutdownHook : ISensitiveWorkerShutdownHook
{
    private readonly ITrainerRuntimeService _runtimeService;
    private readonly ITrainerBackend _backend;
    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _database;
    private readonly ISandboxProcessScanner _sandboxScanner;
    private readonly ILogger<SensitiveWorkerShutdownHook> _logger;

    public SensitiveWorkerShutdownHook(
        ITrainerRuntimeService runtimeService,
        ITrainerBackend backend,
        SpawnPaths paths,
        SpawnDatabase database,
        ISandboxProcessScanner? sandboxScanner = null,
        ILogger<SensitiveWorkerShutdownHook>? logger = null)
    {
        _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _sandboxScanner = sandboxScanner ?? SafeSandboxProcessScanner.Instance;
        _logger = logger ?? NullLogger<SensitiveWorkerShutdownHook>.Instance;
    }

    public async Task<SensitiveWorkerShutdownReport> ShutdownAllForProtectedLaunchAsync(
        TimeSpan budget,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan effectiveBudget = budget < TimeSpan.FromMilliseconds(150)
            ? TimeSpan.FromMilliseconds(150)
            : budget;

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(effectiveBudget);

        int workersBefore = CountActiveWorkers();

        // 1. Stop all active managed trainer sessions & workers immediately.
        try
        {
            await _runtimeService.StopAllAsync(TrainerShutdownReason.ProtectedGamePreFlightPurge, budgetCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Runtime StopAllAsync hit timeout/error during LOOP-06 purge; falling back to backend emergency shutdown.");
        }

        try
        {
            await _backend.ShutdownAllImmediatelyAsync(TrainerShutdownReason.ProtectedGamePreFlightPurge, budgetCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Backend ShutdownAllImmediatelyAsync encountered an error during LOOP-06 purge.");
        }

        int workersAfter = CountActiveWorkers();
        int terminatedWorkers = Math.Max(0, workersBefore - workersAfter);
        if (workersBefore > 0 && terminatedWorkers == 0 && workersAfter == 0)
        {
            terminatedWorkers = workersBefore;
        }

        // 2. Sweep SPAWN's own trainer sandbox root for orphan processes and confirm full cleanup.
        string sandboxRoot = await ResolveActiveSandboxRootAsync(ct).ConfigureAwait(false);
        int terminatedOrphans = 0;
        IReadOnlyList<SafeProcessEntry> orphans = _sandboxScanner.FindProcessesUnderSandbox(sandboxRoot);

        if (orphans.Count > 0)
        {
            foreach (SafeProcessEntry proc in orphans)
            {
                if (budgetCts.IsCancellationRequested)
                {
                    break;
                }

                if (_sandboxScanner.TryTerminateProcess(proc.ProcessId))
                {
                    terminatedOrphans++;
                }
            }

            // Re-verify clean state within remaining budget (poll briefly for kernel teardown).
            while (!budgetCts.IsCancellationRequested)
            {
                orphans = _sandboxScanner.FindProcessesUnderSandbox(sandboxRoot);
                if (orphans.Count == 0)
                {
                    break;
                }

                TimeSpan remaining = effectiveBudget - sw.Elapsed;
                if (remaining <= TimeSpan.FromMilliseconds(30))
                {
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)), budgetCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        IReadOnlyList<SafeProcessEntry> finalRemaining = _sandboxScanner.FindProcessesUnderSandbox(sandboxRoot);
        string[] remainingNames = finalRemaining
            .Select(static p => string.IsNullOrWhiteSpace(p.FullExecutablePath) ? $"{p.ExeFileName} (PID {p.ProcessId})" : $"{p.ExeFileName} (PID {p.ProcessId})")
            .ToArray();

        bool succeeded = remainingNames.Length == 0 && CountActiveWorkers() == 0;
        return new SensitiveWorkerShutdownReport
        {
            Succeeded = succeeded,
            TerminatedWorkerCount = terminatedWorkers,
            TerminatedOrphanCount = terminatedOrphans,
            RemainingSandboxProcesses = remainingNames,
            Elapsed = sw.Elapsed,
            Message = succeeded
                ? "全部 SPAWN 修改器 Worker 与沙箱进程已确认清零。"
                : $"清场超时或仍有残留沙箱进程：{string.Join("、", remainingNames)}",
        };
    }

    private int CountActiveWorkers()
    {
        if (_backend is ITrainerBackendSessionEvents events)
        {
            return Math.Max(events.ActiveWorkerCount, _runtimeService.ActiveSession is not null ? 1 : 0);
        }

        return _runtimeService.ActiveSession is not null ? 1 : 0;
    }

    private async Task<string> ResolveActiveSandboxRootAsync(CancellationToken ct)
    {
        try
        {
            TrainerSettingsDto settings = await _database.GetTrainerSettingsAsync(ct).ConfigureAwait(false);
            return _paths.ResolveTrainerSandboxDirectory(settings.CustomSandboxRoot, requirePureAscii: false);
        }
        catch
        {
            return _paths.TrainerSandboxDirectory;
        }
    }
}
