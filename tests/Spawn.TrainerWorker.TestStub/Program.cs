using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Spawn.TrainerWorker.TestStub;

/// <summary>
/// Harmless, windowless synthetic multi-process stub executable used exclusively by
/// <c>Spawn.TrainerWorker.UnitTests</c> to verify atomic <c>KILL_ON_JOB_CLOSE</c> JobObject process-tree
/// cleanup, IOCP <c>JOB_OBJECT_MSG_NEW_PROCESS</c> / <c>ACTIVE_PROCESS_ZERO</c>, and Host-disconnect self-destruct.
/// Produces zero visible windows, zero audio, and enforces a strict safety lifetime cap.
/// </summary>
internal static class Program
{
    private const int DefaultMaxLifetimeMs = 20_000;
    private const int HardMaxLifetimeMs = 45_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static int Main(string[] args)
    {
        try
        {
            TryLowerProcessPriority();

            StubRunSpec spec = ResolveSpec(args);
            RecordSelfPid(spec);

            if (spec.Depth > 0 && spec.ChildrenPerNode > 0)
            {
                SpawnChildren(spec);
            }

            int effectiveLifetimeMs = Math.Clamp(spec.EffectiveLifetimeMs, 0, HardMaxLifetimeMs);
            if (effectiveLifetimeMs > 0)
            {
                WaitForLifetimeOrStopSignal(spec, effectiveLifetimeMs);
            }

            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static StubRunSpec ResolveSpec(string[] args)
    {
        if (args.Length > 0)
        {
            return ParseCommandLine(args);
        }

        // When launched via trainer.launch (which enforces zero command-line arguments),
        // read optional teststub.config.json from the working directory or executable directory.
        string[] candidateConfigPaths =
        [
            Path.Combine(Environment.CurrentDirectory, "teststub.config.json"),
            Path.Combine(AppContext.BaseDirectory, "teststub.config.json"),
        ];

        foreach (string candidate in candidateConfigPaths)
        {
            if (File.Exists(candidate))
            {
                string json = File.ReadAllText(candidate, Encoding.UTF8);
                StubConfigFile? cfg = JsonSerializer.Deserialize<StubConfigFile>(json, JsonOptions);
                if (cfg is not null)
                {
                    int maxLifetime = Math.Clamp(cfg.MaxLifetimeMs ?? DefaultMaxLifetimeMs, 0, HardMaxLifetimeMs);
                    int rootLifetime = cfg.RootExitAfterMs.HasValue
                        ? Math.Clamp(cfg.RootExitAfterMs.Value, 0, maxLifetime)
                        : maxLifetime;
                    int childLifetime = cfg.ChildExitAfterMs.HasValue
                        ? Math.Clamp(cfg.ChildExitAfterMs.Value, 0, maxLifetime)
                        : maxLifetime;

                    return new StubRunSpec
                    {
                        Depth = Math.Clamp(cfg.Depth, 0, 3),
                        ChildrenPerNode = Math.Clamp(cfg.ChildrenPerNode, 0, 3),
                        PidDirectory = cfg.PidDirectory,
                        StopSignalFile = cfg.StopSignalFile,
                        EffectiveLifetimeMs = rootLifetime,
                        ChildLifetimeMs = childLifetime,
                        IsRoot = true,
                    };
                }
            }
        }

        // Default no-arg behavior: exit immediately and cleanly.
        return new StubRunSpec
        {
            Depth = 0,
            ChildrenPerNode = 0,
            EffectiveLifetimeMs = 0,
            ChildLifetimeMs = 0,
            IsRoot = true,
        };
    }

    private static StubRunSpec ParseCommandLine(string[] args)
    {
        int depth = 0;
        int children = 0;
        int lifetimeMs = DefaultMaxLifetimeMs;
        int? childLifetimeMs = null;
        string? pidDir = null;
        string? stopSignalFile = null;
        bool isRoot = true;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--depth", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _ = int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out depth);
            }
            else if (arg.Equals("--children", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _ = int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out children);
            }
            else if (arg.Equals("--lifetime-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _ = int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out lifetimeMs);
            }
            else if (arg.Equals("--child-lifetime-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedChild))
                {
                    childLifetimeMs = parsedChild;
                }
            }
            else if (arg.Equals("--pid-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pidDir = args[++i];
            }
            else if (arg.Equals("--stop-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                stopSignalFile = args[++i];
            }
            else if (arg.Equals("--child", StringComparison.OrdinalIgnoreCase))
            {
                isRoot = false;
            }
        }

        int clampedLifetime = Math.Clamp(lifetimeMs, 0, HardMaxLifetimeMs);
        int clampedChildLifetime = Math.Clamp(childLifetimeMs ?? clampedLifetime, 0, HardMaxLifetimeMs);

        return new StubRunSpec
        {
            Depth = Math.Clamp(depth, 0, 3),
            ChildrenPerNode = Math.Clamp(children, 0, 3),
            PidDirectory = pidDir,
            StopSignalFile = stopSignalFile,
            EffectiveLifetimeMs = clampedLifetime,
            ChildLifetimeMs = clampedChildLifetime,
            IsRoot = isRoot,
        };
    }

    private static void SpawnChildren(StubRunSpec spec)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return;
        }

        int nextDepth = spec.Depth - 1;
        for (int i = 0; i < spec.ChildrenPerNode; i++)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            psi.ArgumentList.Add("--child");
            psi.ArgumentList.Add("--depth");
            psi.ArgumentList.Add(nextDepth.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--children");
            psi.ArgumentList.Add(spec.ChildrenPerNode.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--lifetime-ms");
            psi.ArgumentList.Add(spec.ChildLifetimeMs.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--child-lifetime-ms");
            psi.ArgumentList.Add(spec.ChildLifetimeMs.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrWhiteSpace(spec.PidDirectory))
            {
                psi.ArgumentList.Add("--pid-dir");
                psi.ArgumentList.Add(spec.PidDirectory);
            }

            if (!string.IsNullOrWhiteSpace(spec.StopSignalFile))
            {
                psi.ArgumentList.Add("--stop-file");
                psi.ArgumentList.Add(spec.StopSignalFile);
            }

            using Process? child = Process.Start(psi);
            _ = child;
        }
    }

    private static void RecordSelfPid(StubRunSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.PidDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(spec.PidDirectory);
            int pid = Environment.ProcessId;
            string payload = JsonSerializer.Serialize(
                new StubPidEntry(pid, spec.IsRoot, spec.Depth, DateTimeOffset.UtcNow),
                JsonOptions);

            string tmpPath = Path.Combine(spec.PidDirectory, $"{pid}.pid.tmp");
            string finalPath = Path.Combine(spec.PidDirectory, $"{pid}.pid");
            File.WriteAllText(tmpPath, payload, Encoding.UTF8);
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch
        {
            // Ignore transient filesystem errors during shutdown.
        }
    }

    private static void WaitForLifetimeOrStopSignal(StubRunSpec spec, int lifetimeMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < lifetimeMs)
        {
            if (!string.IsNullOrWhiteSpace(spec.StopSignalFile) && File.Exists(spec.StopSignalFile))
            {
                break;
            }

            int remaining = lifetimeMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            Thread.Sleep(Math.Min(50, remaining));
        }
    }

    private static void TryLowerProcessPriority()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Ignore priority adjustments if restricted.
        }
    }

    private sealed class StubConfigFile
    {
        public int Depth { get; set; }
        public int ChildrenPerNode { get; set; }
        public string? PidDirectory { get; set; }
        public string? StopSignalFile { get; set; }
        public int? MaxLifetimeMs { get; set; }
        public int? RootExitAfterMs { get; set; }
        public int? ChildExitAfterMs { get; set; }
    }

    private sealed class StubRunSpec
    {
        public int Depth { get; init; }
        public int ChildrenPerNode { get; init; }
        public string? PidDirectory { get; init; }
        public string? StopSignalFile { get; init; }
        public int EffectiveLifetimeMs { get; init; }
        public int ChildLifetimeMs { get; init; }
        public bool IsRoot { get; init; }
    }

    private sealed record StubPidEntry(int Pid, bool IsRoot, int RemainingDepth, DateTimeOffset RecordedAtUtc);
}
