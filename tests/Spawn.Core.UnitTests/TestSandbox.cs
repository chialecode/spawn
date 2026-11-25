using Microsoft.Data.Sqlite;
using Spawn.Core;

namespace Spawn.Core.UnitTests;

/// <summary>
/// Per-test isolated temporary directory harness (<c>%TEMP%\spawn-tests-&lt;guid&gt;</c>).
/// Guarantees 100% physical isolation from <c>%LOCALAPPDATA%\spawn</c> and <c>%LOCALAPPDATA%\spawn-dev</c>,
/// and cleans up SQLite WAL pools and temporary files upon disposal.
/// </summary>
public sealed class TestSandbox : IDisposable, IAsyncDisposable
{
    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string GamesSandboxDirectory { get; }
    public SpawnPaths Paths { get; }
    public SpawnDatabase Database { get; }

    public TestSandbox(string? workspaceRootHint = null)
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), $"spawn-tests-{Guid.NewGuid():N}");
        DataDirectory = Path.Combine(RootDirectory, "data");
        GamesSandboxDirectory = Path.Combine(RootDirectory, "games");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(GamesSandboxDirectory);

        string effectiveHint = workspaceRootHint ?? TryLocateRepositoryRoot() ?? DataDirectory;
        Paths = SpawnPaths.CreateIsolated(DataDirectory, workspaceRootHint: effectiveHint);
        Paths.EnsureDirectoriesExist();
        Database = new SpawnDatabase(Paths);
    }

    public string CreateSubDirectory(params string[] segments)
    {
        string combined = Path.Combine([RootDirectory, .. segments]);
        Directory.CreateDirectory(combined);
        return combined;
    }

    public static string? TryLocateRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Spawn.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Database.Dispose();
        ReleaseSqliteHandles();

        // spawn.db / -wal / -shm can stay briefly locked by pooled SQLite handles that are returned to the pool
        // only after background services finish their last statement. Retry with backoff (~6s budget) and
        // never fail the test itself because of temp-folder teardown.
        Exception? lastError = null;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                ReleaseSqliteHandles();
                Thread.Sleep(Math.Min(50 * (attempt + 1), 400));
            }
        }

        // Last resort: leave the isolated %TEMP% folder for the OS temp cleaner, but record why.
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), $"spawn-tests-leftover-{Path.GetFileName(RootDirectory)}.txt"),
                lastError?.ToString() ?? "unknown");
        }
        catch
        {
            // Ignore diagnostics failure.
        }
    }

    private static void ReleaseSqliteHandles()
    {
        // ClearAllPools only closes idle pooled connections (in-use ones are closed when returned),
        // so it is safe even while other tests run in parallel against their own sandboxes.
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

/// <summary>
/// Deterministic virtual clock implementing <see cref="TimeProvider"/> without external test packages.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ScheduledEntry> _timers = [];
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset? initialUtc = null)
    {
        _utcNow = initialUtc ?? new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <summary>
    /// Timers that are set and have not fired yet. A test moves the clock only while the code under test waits on one, so a step never lands
    /// between two of its waits and gets lost (#31).
    /// </summary>
    public int PendingTimerCount
    {
        get
        {
            lock (_gate)
            {
                int pending = 0;
                foreach (var entry in _timers)
                {
                    if (!entry.Disposed && entry.DueAtUtc != DateTimeOffset.MaxValue)
                    {
                        pending++;
                    }
                }

                return pending;
            }
        }
    }

    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        List<ScheduledEntry> due = [];

        lock (_gate)
        {
            _utcNow += delta;
            foreach (var entry in _timers.ToArray())
            {
                if (!entry.Disposed && entry.DueAtUtc <= _utcNow)
                {
                    due.Add(entry);
                    if (entry.Period > TimeSpan.Zero && entry.Period != Timeout.InfiniteTimeSpan)
                    {
                        entry.DueAtUtc = _utcNow + entry.Period;
                    }
                    else
                    {
                        entry.DueAtUtc = DateTimeOffset.MaxValue;
                    }
                }
            }
        }

        foreach (var item in due)
        {
            item.Invoke();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var entry = new ScheduledEntry(this, callback, state, dueTime, period);
        if (dueTime == TimeSpan.Zero)
        {
            entry.Invoke();
        }

        return entry;
    }

    private void RemoveTimer(ScheduledEntry entry)
    {
        lock (_gate)
        {
            _timers.Remove(entry);
        }
    }

    private sealed class ScheduledEntry : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public DateTimeOffset DueAtUtc { get; set; }
        public TimeSpan Period { get; set; }
        public bool Disposed { get; private set; }

        public ScheduledEntry(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            Period = period;

            lock (_owner._gate)
            {
                DueAtUtc = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : _owner._utcNow + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                _owner._timers.Add(this);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                if (Disposed)
                {
                    return false;
                }

                Period = period;
                DueAtUtc = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : _owner._utcNow + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                return true;
            }
        }

        public void Invoke()
        {
            if (!Disposed)
            {
                _callback(_state);
            }
        }

        public void Dispose()
        {
            Disposed = true;
            _owner.RemoveTimer(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
