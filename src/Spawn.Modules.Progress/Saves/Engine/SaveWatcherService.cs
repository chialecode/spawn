using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Saves.Engine;

/// <summary>
/// Configurable timing parameters for <see cref="SaveWatcherService"/> (defaults match <c>A-03</c> <c>2500ms</c>
/// quiescent debounce and <c>500ms</c> post-exit file-handle settle window, overridable for fast deterministic tests).
/// </summary>
public sealed class SaveWatcherOptions
{
    public TimeSpan? QuiescentDebounceOverride { get; set; }
    public TimeSpan PostExitSettleDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Anti-tear quiescent file watcher &amp; post-game-exit automatic snapshot coordinator implementing
/// <see cref="ISaveWatcherService"/> (<c>REQ-PRG-02</c>, <c>A-03</c>).
/// Subscribes to <see cref="ISpawnEventBus"/> <see cref="ActiveGameSessionChangedEvent"/> with zero coupling to
/// <c>Spawn.Modules.Launcher</c>.
/// </summary>
public sealed class SaveWatcherService : ISaveWatcherService
{
    private readonly ISaveTimeMachineService _timeMachine;
    private readonly ILudusaviRuleResolver _ruleResolver;
    private readonly ISpawnEventBus _eventBus;
    private readonly SpawnDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly SaveWatcherOptions _options;
    private readonly ILogger<SaveWatcherService> _logger;

    private readonly ConcurrentDictionary<string, GameWatchContext> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SaveWatcherStatusDto> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lifecycleLock = new();

    private IDisposable? _sessionSubscription;
    private bool _started;
    private int _disposed;

    public event EventHandler<SaveWatcherStatusDto>? WatcherStatusChanged;

    public SaveWatcherService(
        ISaveTimeMachineService timeMachine,
        ILudusaviRuleResolver ruleResolver,
        ISpawnEventBus eventBus,
        SpawnDatabase database,
        TimeProvider? timeProvider = null,
        SaveWatcherOptions? options = null,
        ILogger<SaveWatcherService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(timeMachine);
        ArgumentNullException.ThrowIfNull(ruleResolver);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(database);

        _timeMachine = timeMachine;
        _ruleResolver = ruleResolver;
        _eventBus = eventBus;
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new SaveWatcherOptions();
        _logger = logger ?? NullLogger<SaveWatcherService>.Instance;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_lifecycleLock)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _sessionSubscription = _eventBus.Subscribe<ActiveGameSessionChangedEvent>(OnGameSessionChangedAsync);
            _started = true;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            _sessionSubscription?.Dispose();
            _sessionSubscription = null;
            _started = false;
        }

        foreach (string gameId in _watchers.Keys.ToArray())
        {
            StopWatchingGameInternal(gameId);
        }

        return Task.CompletedTask;
    }

    public async Task StartWatchingGameAsync(string gameId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        // Stop any existing watcher before rebuilding with freshly resolved paths.
        StopWatchingGameInternal(gameId);

        ResolvedSaveLayout layout = await _ruleResolver
            .ResolveGameSaveLayoutAsync(gameId, refreshOnlineRules: false, ct)
            .ConfigureAwait(false);

        SaveSettingsDto settings = await _database.GetSaveSettingsAsync(ct).ConfigureAwait(false);
        TimeSpan debounceWindow = _options.QuiescentDebounceOverride
            ?? TimeSpan.FromMilliseconds(Math.Clamp(
                settings.QuiescentDebounceMs,
                SaveSettingsDto.MinQuiescentDebounceMs,
                SaveSettingsDto.MaxQuiescentDebounceMs));

        var watchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedSaveLocation loc in layout.SaveLocations)
        {
            if (!loc.IsSafe || string.IsNullOrWhiteSpace(loc.ResolvedFullPath))
            {
                continue;
            }

            string? dirToWatch = null;
            if (Directory.Exists(loc.ResolvedFullPath))
            {
                dirToWatch = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc.ResolvedFullPath));
            }
            else if (File.Exists(loc.ResolvedFullPath))
            {
                string? parent = Path.GetDirectoryName(Path.GetFullPath(loc.ResolvedFullPath));
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    dirToWatch = Path.TrimEndingDirectorySeparator(parent);
                }
            }

            if (!string.IsNullOrWhiteSpace(dirToWatch) && !AntiTearFileCapture.IsReparsePoint(dirToWatch))
            {
                watchedDirs.Add(dirToWatch);
            }
        }

        if (watchedDirs.Count == 0)
        {
            UpdateStatus(new SaveWatcherStatusDto
            {
                GameId = gameId,
                Status = SaveWatcherStatusKind.Stopped,
                WatchedDirectories = [],
                LastEventUtc = _timeProvider.GetUtcNow(),
                LastMessage = "未检测到可监听的现存存档目录。",
            });
            return;
        }

        var ctx = new GameWatchContext(gameId, debounceWindow);
        var fsWatchers = new List<FileSystemWatcher>(watchedDirs.Count);

        foreach (string dir in watchedDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fsw = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 32 * 1024,
                };

                fsw.Changed += (_, e) => OnFileSystemEvent(ctx, e.FullPath);
                fsw.Created += (_, e) => OnFileSystemEvent(ctx, e.FullPath);
                fsw.Renamed += (_, e) => OnFileSystemEvent(ctx, e.FullPath);
                fsw.Deleted += (_, e) => OnFileSystemEvent(ctx, e.FullPath);
                fsw.EnableRaisingEvents = true;
                fsWatchers.Add(fsw);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex, "Could not attach save FileSystemWatcher to {Dir}.", dir);
            }
        }

        if (fsWatchers.Count == 0)
        {
            ctx.Dispose();
            return;
        }

        ctx.SetWatchers(fsWatchers, watchedDirs.ToArray());
        _watchers[gameId] = ctx;

        UpdateStatus(new SaveWatcherStatusDto
        {
            GameId = gameId,
            Status = SaveWatcherStatusKind.Watching,
            WatchedDirectories = ctx.WatchedDirectories,
            LastEventUtc = _timeProvider.GetUtcNow(),
            LastMessage = $"正在防撕裂监听 {ctx.WatchedDirectories.Count} 个存档目录（静默窗口 {(int)debounceWindow.TotalMilliseconds}ms）。",
        });
    }

    public Task StopWatchingGameAsync(string gameId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            StopWatchingGameInternal(gameId);
        }

        return Task.CompletedTask;
    }

    public SaveWatcherStatusDto GetStatus(string gameId) =>
        !string.IsNullOrWhiteSpace(gameId) && _statuses.TryGetValue(gameId, out SaveWatcherStatusDto? s)
            ? s
            : new SaveWatcherStatusDto { GameId = gameId ?? string.Empty, Status = SaveWatcherStatusKind.Stopped };

    public IReadOnlyList<SaveWatcherStatusDto> GetAllStatuses() =>
        _statuses.Values.OrderBy(s => s.GameId, StringComparer.OrdinalIgnoreCase).ToArray();

    private async Task OnGameSessionChangedAsync(ActiveGameSessionChangedEvent ev, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(ev.GameId))
        {
            return;
        }

        try
        {
            SaveGameStateRecord state = await _database
                .GetOrCreateSaveGameStateAsync(ev.GameId, _timeProvider.GetUtcNow(), ct)
                .ConfigureAwait(false);

            if (ev.CurrentState is PlaySessionActivityState.Launching
                or PlaySessionActivityState.Active
                or PlaySessionActivityState.ShellBreakingWatch)
            {
                if (state.AutoWatchEnabled && !_watchers.ContainsKey(ev.GameId))
                {
                    await StartWatchingGameAsync(ev.GameId, ct).ConfigureAwait(false);
                }
            }
            else if (ev.CurrentState == PlaySessionActivityState.Stopped)
            {
                StopWatchingGameInternal(ev.GameId);

                if (state.PostExitSnapshotEnabled)
                {
                    if (_options.PostExitSettleDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.PostExitSettleDelay, _timeProvider, ct).ConfigureAwait(false);
                    }

                    CreateSaveSnapshotResult res = await _timeMachine.CreateSnapshotAsync(
                        new CreateSaveSnapshotRequest
                        {
                            GameId = ev.GameId,
                            TriggerKind = SaveSnapshotTriggerKind.PostExit,
                            Note = "退出游戏自动备份",
                            SkipIfUnchangedFromHead = true,
                        },
                        ct).ConfigureAwait(false);

                    UpdateStatus(new SaveWatcherStatusDto
                    {
                        GameId = ev.GameId,
                        Status = SaveWatcherStatusKind.Stopped,
                        WatchedDirectories = [],
                        LastEventUtc = _timeProvider.GetUtcNow(),
                        LastSnapshotId = res.Snapshot?.SnapshotId,
                        LastMessage = res.Message,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveWatcherService session handler failed for game {GameId}.", ev.GameId);
        }
    }

    private void OnFileSystemEvent(GameWatchContext ctx, string? fullPath)
    {
        if (Volatile.Read(ref _disposed) != 0 || ctx.IsDisposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(fullPath)
            && (fullPath.EndsWith(AtomicSaveRestorer.RestoreStageSuffix, StringComparison.OrdinalIgnoreCase)
                || fullPath.EndsWith(".spawn_tmp", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        CancellationTokenSource nextCts = ctx.ResetDebounceTimer();

        UpdateStatus(new SaveWatcherStatusDto
        {
            GameId = ctx.GameId,
            Status = SaveWatcherStatusKind.Debouncing,
            WatchedDirectories = ctx.WatchedDirectories,
            LastEventUtc = _timeProvider.GetUtcNow(),
            LastMessage = "检测到存档变动，等待写入静默窗口…",
        });

        _ = Task.Run(() => RunQuiescentCaptureAfterDelayAsync(ctx, nextCts.Token), CancellationToken.None);
    }

    private async Task RunQuiescentCaptureAfterDelayAsync(GameWatchContext ctx, CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(ctx.DebounceWindow, _timeProvider, debounceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer file change arrived or watcher stopped; timer was superseded.
            return;
        }

        if (Volatile.Read(ref _disposed) != 0 || ctx.IsDisposed || debounceToken.IsCancellationRequested)
        {
            return;
        }

        UpdateStatus(new SaveWatcherStatusDto
        {
            GameId = ctx.GameId,
            Status = SaveWatcherStatusKind.Capturing,
            WatchedDirectories = ctx.WatchedDirectories,
            LastEventUtc = _timeProvider.GetUtcNow(),
            LastMessage = "静默窗口已就绪，正在捕获防撕裂热备快照…",
        });

        try
        {
            CreateSaveSnapshotResult res = await _timeMachine.CreateSnapshotAsync(
                new CreateSaveSnapshotRequest
                {
                    GameId = ctx.GameId,
                    TriggerKind = SaveSnapshotTriggerKind.AutoQuiescent,
                    Note = "防撕裂静默热备",
                    SkipIfUnchangedFromHead = true,
                },
                CancellationToken.None).ConfigureAwait(false);

            if (!ctx.IsDisposed)
            {
                UpdateStatus(new SaveWatcherStatusDto
                {
                    GameId = ctx.GameId,
                    Status = SaveWatcherStatusKind.Watching,
                    WatchedDirectories = ctx.WatchedDirectories,
                    LastEventUtc = _timeProvider.GetUtcNow(),
                    LastSnapshotId = res.Snapshot?.SnapshotId ?? GetStatus(ctx.GameId).LastSnapshotId,
                    LastMessage = res.Message,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quiescent auto-snapshot failed for game {GameId}.", ctx.GameId);
            if (!ctx.IsDisposed)
            {
                UpdateStatus(new SaveWatcherStatusDto
                {
                    GameId = ctx.GameId,
                    Status = SaveWatcherStatusKind.Error,
                    WatchedDirectories = ctx.WatchedDirectories,
                    LastEventUtc = _timeProvider.GetUtcNow(),
                    LastMessage = $"自动热备异常：{ex.Message}",
                });
            }
        }
    }

    private void StopWatchingGameInternal(string gameId)
    {
        if (_watchers.TryRemove(gameId, out GameWatchContext? ctx))
        {
            ctx.Dispose();
            UpdateStatus(new SaveWatcherStatusDto
            {
                GameId = gameId,
                Status = SaveWatcherStatusKind.Stopped,
                WatchedDirectories = [],
                LastEventUtc = _timeProvider.GetUtcNow(),
                LastSnapshotId = GetStatus(gameId).LastSnapshotId,
                LastMessage = "已停止存档目录监听。",
            });
        }
    }

    private void UpdateStatus(SaveWatcherStatusDto status)
    {
        _statuses[status.GameId] = status;
        try
        {
            WatcherStatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WatcherStatusChanged subscriber threw for game {GameId}.", status.GameId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private sealed class GameWatchContext(string gameId, TimeSpan debounceWindow) : IDisposable
    {
        private readonly Lock _lock = new();
        private CancellationTokenSource? _debounceCts;
        private List<FileSystemWatcher> _fsWatchers = [];
        private volatile bool _disposed;

        public string GameId { get; } = gameId;
        public TimeSpan DebounceWindow { get; } = debounceWindow;
        public IReadOnlyList<string> WatchedDirectories { get; private set; } = [];
        public bool IsDisposed => _disposed;

        public void SetWatchers(List<FileSystemWatcher> watchers, IReadOnlyList<string> dirs)
        {
            _fsWatchers = watchers;
            WatchedDirectories = dirs;
        }

        public CancellationTokenSource ResetDebounceTimer()
        {
            lock (_lock)
            {
                if (_debounceCts is not null)
                {
                    try
                    {
                        _debounceCts.Cancel();
                        _debounceCts.Dispose();
                    }
                    catch
                    {
                        // Ignore cancel race.
                    }
                }

                _debounceCts = new CancellationTokenSource();
                return _debounceCts;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_lock)
            {
                if (_debounceCts is not null)
                {
                    try
                    {
                        _debounceCts.Cancel();
                        _debounceCts.Dispose();
                    }
                    catch
                    {
                        // Ignore cancel race.
                    }

                    _debounceCts = null;
                }
            }

            foreach (FileSystemWatcher fsw in _fsWatchers)
            {
                try
                {
                    fsw.EnableRaisingEvents = false;
                    fsw.Dispose();
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            _fsWatchers.Clear();
        }
    }
}
