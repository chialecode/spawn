using Spawn.Contracts;

namespace Spawn.Core.AntiCheat;

/// <summary>
/// Read-only OS probe abstraction used by <see cref="TrainerSafetyGate"/> for live anti-cheat detection and
/// the 2-second active-session emergency stop watchdog. Injectable for 100% deterministic offline tests.
/// </summary>
public interface IAntiCheatSystemProbe
{
    IReadOnlyList<SafeProcessEntry> EnumerateProcesses(bool resolveFullPaths);
    IReadOnlyList<string> EnumerateLoadedDriverBaseNames();
    IReadOnlyList<ActiveKernelDriverInfo> EnumerateActiveDriverServices();
}

/// <summary>
/// Default Clean-Host compliant system probe backed strictly by <see cref="SafeProcessInspector"/> (<c>0x1000</c>)
/// and read-only <see cref="HighRiskEnvironmentProbe"/> (<c>K32EnumDeviceDrivers</c> + SCM).
/// </summary>
public sealed class DefaultAntiCheatSystemProbe : IAntiCheatSystemProbe
{
    public static DefaultAntiCheatSystemProbe Instance { get; } = new();

    public IReadOnlyList<SafeProcessEntry> EnumerateProcesses(bool resolveFullPaths) =>
        SafeProcessInspector.EnumerateProcesses(resolveFullPaths);

    public IReadOnlyList<string> EnumerateLoadedDriverBaseNames() =>
        HighRiskEnvironmentProbe.EnumerateLoadedDriverBaseNames();

    public IReadOnlyList<ActiveKernelDriverInfo> EnumerateActiveDriverServices() =>
        HighRiskEnvironmentProbe.EnumerateActiveDriverServices();
}

/// <summary>
/// Evaluates live running processes and kernel drivers from an <see cref="IAntiCheatSystemProbe"/> into a
/// structured <see cref="LiveAntiCheatProbeReport"/>.
/// </summary>
public static class AntiCheatLiveEvaluator
{
    public static LiveAntiCheatProbeReport Evaluate(
        IAntiCheatSystemProbe probe,
        DateTimeOffset nowUtc,
        bool resolveFullPaths,
        out IReadOnlyList<SafeProcessEntry> processSnapshot)
    {
        ArgumentNullException.ThrowIfNull(probe);

        processSnapshot = probe.EnumerateProcesses(resolveFullPaths);
        var activeProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeSessionDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool vanguardClientActive = false;
        bool vanguardKernelLoaded = false;

        foreach (var proc in processSnapshot)
        {
            if (AntiCheatFeatureCatalog.IsVanguardClientProcessName(proc.ExeFileName))
            {
                vanguardClientActive = true;
                activeProcesses.Add(AntiCheatFeatureCatalog.VanguardClientProcessName);
            }
            else if (AntiCheatFeatureCatalog.IsResidualGuardProcessName(proc.ExeFileName))
            {
                activeProcesses.Add(Path.GetFileName(proc.ExeFileName));
            }
        }

        foreach (string driverBaseName in probe.EnumerateLoadedDriverBaseNames())
        {
            if (AntiCheatFeatureCatalog.IsVanguardKernelDriver(driverBaseName))
            {
                vanguardKernelLoaded = true;
            }
            else if (AntiCheatFeatureCatalog.TryMatchSessionAntiCheatDriver(driverBaseName, out string canonical))
            {
                activeSessionDrivers.Add(canonical);
            }
        }

        foreach (var svc in probe.EnumerateActiveDriverServices())
        {
            if (AntiCheatFeatureCatalog.IsVanguardKernelDriver(svc.ServiceName)
                || AntiCheatFeatureCatalog.IsVanguardKernelDriver(svc.DriverFileName)
                || AntiCheatFeatureCatalog.IsVanguardKernelDriver(svc.RawImagePath))
            {
                vanguardKernelLoaded = true;
            }

            if (AntiCheatFeatureCatalog.TryMatchSessionAntiCheatDriver(svc.DriverFileName, out string canonicalByFile))
            {
                activeSessionDrivers.Add(canonicalByFile);
            }
            else if (AntiCheatFeatureCatalog.TryMatchSessionAntiCheatDriver(svc.ServiceName, out string canonicalByService))
            {
                activeSessionDrivers.Add(canonicalByService);
            }
        }

        if (vanguardClientActive && vanguardKernelLoaded)
        {
            activeSessionDrivers.Add(AntiCheatFeatureCatalog.VanguardKernelDriverFileName);
        }

        return new LiveAntiCheatProbeReport
        {
            ActiveAntiCheatProcesses = activeProcesses.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            ActiveSessionAntiCheatDrivers = activeSessionDrivers.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            IsVanguardClientActive = vanguardClientActive,
            IsVanguardKernelIdleLoaded = vanguardKernelLoaded && !vanguardClientActive,
            ProbedAtUtc = nowUtc,
        };
    }
}
