using System.Text.Json;
using Spawn.Contracts;
using Spawn.Core.AntiCheat;

namespace Spawn.Core;

/// <summary>
/// Raised when an elevated/companion operation is blocked by <see cref="AntiCheatGuardService"/>.
/// </summary>
public sealed class AntiCheatLockdownException(
    string message,
    AntiCheatGuardPhase phase,
    double remainingCooldownSeconds = 0) : InvalidOperationException(message)
{
    public AntiCheatGuardPhase Phase { get; } = phase;
    public double RemainingCooldownSeconds { get; } = remainingCooldownSeconds;
}

/// <summary>
/// Implements depth-6 four-tier anti-cheat directory signature inspection, protected game lockdown,
/// residual companion daemon &amp; session kernel anti-cheat driver exit monitoring (<c>REQ-SEC-05</c>),
/// and the mandatory 15-second post-exit cooldown window.
/// </summary>
public sealed class AntiCheatGuardService : IAntiCheatGuardService, IDisposable
{
    public static readonly TimeSpan DefaultMandatoryCooldown = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cooldownDuration;
    private readonly TimeSpan _pollInterval;
    private readonly Func<IReadOnlyList<string>> _residualProcessProbe;
    private readonly Func<IReadOnlyList<string>> _sessionDriverProbe;
    private readonly Func<HighRiskEnvironmentReport> _highRiskEnvironmentProbe;
    private readonly string? _persistedStateFilePath;
    private readonly Lock _stateLock = new();

    private AntiCheatGuardPhase _phase = AntiCheatGuardPhase.IdleSafe;
    private string? _activeProtectedGameId;
    private DateTimeOffset? _cooldownEndsAtUtc;
    private IReadOnlyList<string> _detectedResidualProcesses = [];
    private CancellationTokenSource? _cooldownCts;

    public event EventHandler<AntiCheatGuardSnapshot>? StateChanged;

    public AntiCheatGuardService()
        : this(timeProvider: null, cooldownDuration: null, residualProcessProbe: null, pollInterval: null)
    {
    }

    /// <param name="timeProvider">Clock used for cooldown accounting (virtual clock in tests).</param>
    /// <param name="cooldownDuration">Mandatory post-exit cooldown (defaults to 15s).</param>
    /// <param name="residualProcessProbe">Residual anti-cheat daemon detector (defaults to a Toolhelp name scan).</param>
    /// <param name="pollInterval">Residual daemon &amp; session kernel driver polling interval.</param>
    /// <param name="highRiskEnvironmentProbe">Pre-launch CE / <c>dbk64</c> / sensitive Worker detector.</param>
    /// <param name="persistedStateFilePath">
    /// Optional JSON file that keeps the lock across SPAWN restarts (protected session or unexpired cooldown).
    /// </param>
    /// <param name="sessionDriverProbe">
    /// Optional session kernel anti-cheat driver detector (defaults to read-only <c>K32EnumDeviceDrivers</c> + SCM
    /// when <paramref name="residualProcessProbe"/> is not mocked).
    /// </param>
    /// <param name="trainerSandboxRootsProvider">
    /// Optional provider returning trainer sandbox roots checked for orphan processes during high-risk sweeps.
    /// </param>
    public AntiCheatGuardService(
        TimeProvider? timeProvider = null,
        TimeSpan? cooldownDuration = null,
        Func<IReadOnlyList<string>>? residualProcessProbe = null,
        TimeSpan? pollInterval = null,
        Func<HighRiskEnvironmentReport>? highRiskEnvironmentProbe = null,
        string? persistedStateFilePath = null,
        Func<IReadOnlyList<string>>? sessionDriverProbe = null,
        Func<IEnumerable<string>>? trainerSandboxRootsProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cooldownDuration = cooldownDuration ?? DefaultMandatoryCooldown;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _residualProcessProbe = residualProcessProbe ?? DefaultDetectResidualGuardProcesses;
        _sessionDriverProbe = sessionDriverProbe
            ?? (residualProcessProbe is null ? DefaultDetectSessionAntiCheatDrivers : static () => []);
        _highRiskEnvironmentProbe = highRiskEnvironmentProbe
            ?? (trainerSandboxRootsProvider is not null
                ? () => new HighRiskEnvironmentReport(
                    HighRiskEnvironmentProbe.DetectHighRiskProcesses(trainerSandboxRootsProvider()),
                    HighRiskEnvironmentProbe.DetectHighRiskDrivers())
                : DefaultInspectHighRiskEnvironment);
        _persistedStateFilePath = string.IsNullOrWhiteSpace(persistedStateFilePath) ? null : persistedStateFilePath;

        // Guard start-up: (1) resume a lock persisted by a previous SPAWN instance, (2) probe live residual daemons/drivers.
        RestorePersistedLockOnStartup();
        ProbeResidualGuardsAndLockIfNeeded();
    }

    public AntiCheatGuardSnapshot CurrentSnapshot
    {
        get
        {
            lock (_stateLock)
            {
                RefreshCooldownExpirationNoLock();
                return BuildSnapshotNoLock();
            }
        }
    }

    public AntiCheatInspectionResult InspectGameDirectory(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return new AntiCheatInspectionResult(AntiCheatTier.Tier0, null, []);
        }

        var matchedIndicators = new List<string>();
        var highestTier = AntiCheatTier.Tier0;
        string? primarySignature = null;

        ScanDirectoryDepthBounded(
            new DirectoryInfo(installDir),
            currentDepth: 0,
            maxDepth: AntiCheatFeatureCatalog.DefaultScanMaxDepth,
            ref highestTier,
            ref primarySignature,
            matchedIndicators);

        return new AntiCheatInspectionResult(highestTier, primarySignature, matchedIndicators);
    }

    public HighRiskEnvironmentReport InspectHighRiskEnvironment()
    {
        try
        {
            return _highRiskEnvironmentProbe();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed: if the sweep itself cannot run, a protected launch must not proceed silently.
            return new HighRiskEnvironmentReport([$"高危环境检测失败：{ex.GetType().Name}"], []);
        }
    }

    public void EnsureHighRiskOperationPermitted(AntiCheatTier targetGameTier, string operationName)
    {
        if (targetGameTier != AntiCheatTier.Tier0)
        {
            throw new AntiCheatLockdownException(
                $"高敏操作 [{operationName}] 仅允许在 Tier 0 纯离线单机下执行（目标游戏安全等级为 {targetGameTier}）。",
                CurrentSnapshot.Phase,
                CurrentSnapshot.RemainingCooldownSeconds);
        }

        // Live re-check on every request: a residual daemon or session kernel driver left by a game started outside SPAWN
        // immediately (re-)enters the residual-watch + full 15s cooldown flow.
        ProbeResidualGuardsAndLockIfNeeded();

        var snapshot = CurrentSnapshot;
        if (snapshot.IsHighRiskLocked)
        {
            throw new AntiCheatLockdownException(
                $"反作弊安全守卫处于锁定状态（{FormatPhaseLabel(snapshot.Phase)}，剩余冷却 {snapshot.RemainingCooldownSeconds:0.0}s），已熔断操作 [{operationName}]。",
                snapshot.Phase,
                snapshot.RemainingCooldownSeconds);
        }
    }

    /// <summary>
    /// Performs an immediate live residual-process &amp; session-driver re-check without ever shortening
    /// the mandatory 15-second cooldown window or bypassing <see cref="AntiCheatGuardPhase.ProtectedRunning"/>.
    /// </summary>
    public AntiCheatGuardSnapshot RecheckLiveResiduals()
    {
        var residuals = SafeProbeResiduals();
        bool changed = false;
        CancellationToken? newWatchToken = null;

        lock (_stateLock)
        {
            RefreshCooldownExpirationNoLock();

            if (_phase == AntiCheatGuardPhase.ProtectedRunning)
            {
                return BuildSnapshotNoLock();
            }

            if (residuals.Count > 0)
            {
                if (_phase == AntiCheatGuardPhase.ResidualGuardWatch && _cooldownCts is not null)
                {
                    if (!_detectedResidualProcesses.SequenceEqual(residuals, StringComparer.OrdinalIgnoreCase))
                    {
                        _detectedResidualProcesses = residuals;
                        changed = true;
                    }
                }
                else
                {
                    CancelCooldownCtsNoLock();
                    _cooldownCts = new CancellationTokenSource();
                    newWatchToken = _cooldownCts.Token;
                    _phase = AntiCheatGuardPhase.ResidualGuardWatch;
                    _cooldownEndsAtUtc = null;
                    _detectedResidualProcesses = residuals;
                    PersistStateNoLock();
                    changed = true;
                }
            }
            else if (_phase == AntiCheatGuardPhase.ResidualGuardWatch && _detectedResidualProcesses.Count > 0)
            {
                // Update diagnostic residual list; the watch loop still enforces the full mandatory 15s cooldown.
                _detectedResidualProcesses = [];
                changed = true;
            }
        }

        if (changed)
        {
            PublishCurrentSnapshot();
        }

        if (newWatchToken.HasValue)
        {
            StartBackgroundWatch(resumeCooldownEndUtc: null, newWatchToken.Value);
        }

        return CurrentSnapshot;
    }

    private static string FormatPhaseLabel(AntiCheatGuardPhase phase) => phase switch
    {
        AntiCheatGuardPhase.IdleSafe => "安全空闲",
        AntiCheatGuardPhase.ProtectedRunning => "受保护游戏运行中",
        AntiCheatGuardPhase.ResidualGuardWatch => "等待反作弊残影退出",
        AntiCheatGuardPhase.CooldownLocked => "15 秒余温冷却锁定",
        _ => "安全锁定",
    };

    public void NotifyProtectedGameStarted(string gameId, AntiCheatTier tier)
    {
        if (tier < AntiCheatTier.Tier2)
        {
            return;
        }

        AntiCheatGuardSnapshot snapshot;
        lock (_stateLock)
        {
            CancelCooldownCtsNoLock();
            _phase = AntiCheatGuardPhase.ProtectedRunning;
            _activeProtectedGameId = gameId;
            _cooldownEndsAtUtc = null;
            _detectedResidualProcesses = [];
            PersistStateNoLock();
            snapshot = BuildSnapshotNoLock();
        }

        StateChanged?.Invoke(this, snapshot);
    }

    public async Task NotifyProtectedGameExitedAsync(
        string gameId,
        AntiCheatTier tier,
        CancellationToken cancellationToken = default)
    {
        if (tier < AntiCheatTier.Tier2)
        {
            return;
        }

        CancellationTokenSource linkedCts;
        lock (_stateLock)
        {
            CancelCooldownCtsNoLock();
            _cooldownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts = _cooldownCts;
            _phase = AntiCheatGuardPhase.ResidualGuardWatch;
            _cooldownEndsAtUtc = null;
            _detectedResidualProcesses = SafeProbeResiduals();
            PersistStateNoLock();
        }

        PublishCurrentSnapshot();

        try
        {
            await RunResidualWatchAndCooldownAsync(resumeCooldownEndUtc: null, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer protected session transition or a fresh residual detection.
        }
    }

    /// <summary>
    /// Immediately places the guard into <see cref="AntiCheatGuardPhase.ResidualGuardWatch"/> (followed by the full
    /// 15-second cooldown once residual processes and session kernel drivers clear) when the 2-second watchdog detects
    /// an external anti-cheat process/driver or protected game launch.
    /// </summary>
    public void NotifyExternalAntiCheatDetected(IReadOnlyList<string> detectedIndicators, string? triggeringGameId = null)
    {
        var indicators = detectedIndicators is { Count: > 0 }
            ? detectedIndicators.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : ["ExternalAntiCheatDetected"];

        CancellationToken token;
        lock (_stateLock)
        {
            if (_phase == AntiCheatGuardPhase.ProtectedRunning)
            {
                return;
            }

            CancelCooldownCtsNoLock();
            _cooldownCts = new CancellationTokenSource();
            token = _cooldownCts.Token;
            _phase = AntiCheatGuardPhase.ResidualGuardWatch;
            _activeProtectedGameId = triggeringGameId ?? _activeProtectedGameId;
            _cooldownEndsAtUtc = null;
            _detectedResidualProcesses = indicators;
            PersistStateNoLock();
        }

        PublishCurrentSnapshot();
        StartBackgroundWatch(resumeCooldownEndUtc: null, token);
    }

    public static IReadOnlyList<string> DefaultDetectResidualGuardProcesses()
    {
        var entries = SafeProcessInspector.EnumerateProcesses(resolveFullPaths: false);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (AntiCheatFeatureCatalog.IsResidualGuardProcessName(entry.ExeFileName)
                && SafeProcessInspector.IsProcessAlive(entry.ProcessId))
            {
                found.Add(entry.ExeFileName);
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Read-only detection of active session kernel anti-cheat drivers (e.g., <c>EasyAntiCheat_EOS.sys</c>,
    /// <c>BEDaisy.sys</c>, <c>ACE-BASE.sys</c>, <c>NeacSafe64.sys</c>) via <c>K32EnumDeviceDrivers</c> and SCM.
    /// Excludes boot-time persistent <c>vgk.sys</c> so post-exit cooldown can complete on Vanguard-installed systems.
    /// </summary>
    public static IReadOnlyList<string> DefaultDetectSessionAntiCheatDrivers()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string driverBaseName in HighRiskEnvironmentProbe.EnumerateLoadedDriverBaseNames())
        {
            if (AntiCheatFeatureCatalog.TryMatchSessionAntiCheatDriver(driverBaseName, out string canonical))
            {
                found.Add(canonical);
            }
        }

        foreach (var svc in HighRiskEnvironmentProbe.EnumerateActiveDriverServices())
        {
            if (AntiCheatFeatureCatalog.TryMatchSessionAntiCheatDriver(svc.DriverFileName, out string canonicalByFile))
            {
                found.Add(canonicalByFile);
            }
            else if (AntiCheatFeatureCatalog.TryMatchSessionAntiCheatDriver(svc.ServiceName, out string canonicalByService))
            {
                found.Add(canonicalByService);
            }
        }

        return [.. found];
    }

    public static HighRiskEnvironmentReport DefaultInspectHighRiskEnvironment() =>
        new(HighRiskEnvironmentProbe.DetectHighRiskProcesses(), HighRiskEnvironmentProbe.DetectHighRiskDrivers());

    /// <summary>
    /// Shared residual-watch + cooldown state machine: wait until every residual daemon AND session kernel AC driver
    /// is gone, then enforce the full cooldown; any resurgence during cooldown restarts the whole flow.
    /// <paramref name="resumeCooldownEndUtc"/> resumes an unexpired cooldown restored after a SPAWN restart.
    /// </summary>
    private async Task RunResidualWatchAndCooldownAsync(DateTimeOffset? resumeCooldownEndUtc, CancellationToken token)
    {
        DateTimeOffset? pendingCooldownEnd = resumeCooldownEndUtc;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (pendingCooldownEnd is null)
            {
                // Step 1: Wait until all residual anti-cheat guard daemons AND session kernel AC drivers have unloaded.
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var activeResiduals = SafeProbeResiduals();
                    if (activeResiduals.Count == 0)
                    {
                        break;
                    }

                    lock (_stateLock)
                    {
                        if (_phase == AntiCheatGuardPhase.ProtectedRunning)
                        {
                            return;
                        }

                        _phase = AntiCheatGuardPhase.ResidualGuardWatch;
                        _detectedResidualProcesses = activeResiduals;
                        _cooldownEndsAtUtc = null;
                        PersistStateNoLock();
                    }

                    PublishCurrentSnapshot();
                    await DelayWithTimeProviderAsync(_pollInterval, token).ConfigureAwait(false);
                }

                pendingCooldownEnd = _timeProvider.GetUtcNow() + _cooldownDuration;
            }

            // Step 2: Enforce mandatory 15-second residual heat cooldown window.
            DateTimeOffset cooldownEnd = pendingCooldownEnd.Value;
            pendingCooldownEnd = null;
            lock (_stateLock)
            {
                token.ThrowIfCancellationRequested();
                if (_phase == AntiCheatGuardPhase.ProtectedRunning)
                {
                    return;
                }

                _phase = AntiCheatGuardPhase.CooldownLocked;
                _detectedResidualProcesses = [];
                _cooldownEndsAtUtc = cooldownEnd;
                PersistStateNoLock();
            }

            PublishCurrentSnapshot();

            bool residualResurged = false;
            while (_timeProvider.GetUtcNow() < cooldownEnd)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan remaining = cooldownEnd - _timeProvider.GetUtcNow();
                TimeSpan step = remaining < _pollInterval ? remaining : _pollInterval;
                if (step > TimeSpan.Zero)
                {
                    await DelayWithTimeProviderAsync(step, token).ConfigureAwait(false);
                }

                if (SafeProbeResiduals().Count > 0)
                {
                    residualResurged = true;
                    break;
                }
            }

            if (residualResurged)
            {
                continue;
            }

            // Step 3: Cooldown elapsed cleanly with zero residual daemon or session kernel driver activity.
            lock (_stateLock)
            {
                token.ThrowIfCancellationRequested();
                if (_phase != AntiCheatGuardPhase.ProtectedRunning)
                {
                    _phase = AntiCheatGuardPhase.IdleSafe;
                    _activeProtectedGameId = null;
                    _cooldownEndsAtUtc = null;
                    _detectedResidualProcesses = [];
                    PersistStateNoLock();
                }
            }

            PublishCurrentSnapshot();
            return;
        }
    }

    /// <summary>
    /// Live residual daemon &amp; session kernel driver probe used at guard start-up and before every high-risk operation.
    /// Any detection outside a protected session (re-)starts the residual-watch + full cooldown flow.
    /// </summary>
    private void ProbeResidualGuardsAndLockIfNeeded()
    {
        var residuals = SafeProbeResiduals();
        if (residuals.Count == 0)
        {
            return;
        }

        CancellationToken token;
        lock (_stateLock)
        {
            if (_phase == AntiCheatGuardPhase.ProtectedRunning)
            {
                return;
            }

            if (_phase == AntiCheatGuardPhase.ResidualGuardWatch && _cooldownCts is not null)
            {
                // A watch loop is already waiting for these daemons/drivers to exit.
                _detectedResidualProcesses = residuals;
                return;
            }

            // IdleSafe or CooldownLocked: (re-)enter residual watch so the full cooldown restarts after unload.
            CancelCooldownCtsNoLock();
            _cooldownCts = new CancellationTokenSource();
            token = _cooldownCts.Token;
            _phase = AntiCheatGuardPhase.ResidualGuardWatch;
            _cooldownEndsAtUtc = null;
            _detectedResidualProcesses = residuals;
            PersistStateNoLock();
        }

        PublishCurrentSnapshot();
        StartBackgroundWatch(resumeCooldownEndUtc: null, token);
    }

    private void RestorePersistedLockOnStartup()
    {
        var persisted = TryReadPersistedState();
        if (persisted is null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset? resumeCooldownEnd = null;
        AntiCheatGuardPhase restoredPhase;

        if (persisted.Phase == AntiCheatGuardPhase.CooldownLocked
            && persisted.CooldownEndsAtUtc is { } end
            && end > now)
        {
            // Never trust an end time further away than one full cooldown (clock tampering / corrupted file).
            resumeCooldownEnd = end - now > _cooldownDuration ? now + _cooldownDuration : end;
            restoredPhase = AntiCheatGuardPhase.CooldownLocked;
        }
        else if (persisted.Phase is AntiCheatGuardPhase.ProtectedRunning or AntiCheatGuardPhase.ResidualGuardWatch)
        {
            // SPAWN stopped mid-session: we cannot know when the protected game ended, so start a fresh watch + full cooldown.
            restoredPhase = AntiCheatGuardPhase.ResidualGuardWatch;
        }
        else
        {
            lock (_stateLock)
            {
                PersistStateNoLock();
            }

            return;
        }

        CancellationToken token;
        lock (_stateLock)
        {
            _cooldownCts = new CancellationTokenSource();
            token = _cooldownCts.Token;
            _phase = restoredPhase;
            _activeProtectedGameId = persisted.ActiveProtectedGameId;
            _cooldownEndsAtUtc = resumeCooldownEnd;
            _detectedResidualProcesses = [];
            PersistStateNoLock();
        }

        StartBackgroundWatch(resumeCooldownEnd, token);
    }

    private void StartBackgroundWatch(DateTimeOffset? resumeCooldownEndUtc, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunResidualWatchAndCooldownAsync(resumeCooldownEndUtc, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer transition.
            }
        }, CancellationToken.None);
    }

    private IReadOnlyList<string> SafeProbeResiduals()
    {
        var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string proc in _residualProcessProbe())
            {
                if (!string.IsNullOrWhiteSpace(proc))
                {
                    combined.Add(proc);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ignore transient process probe error.
        }

        try
        {
            foreach (string drv in _sessionDriverProbe())
            {
                if (!string.IsNullOrWhiteSpace(drv))
                {
                    combined.Add(drv);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ignore transient driver probe error.
        }

        return combined.Count == 0 ? [] : [.. combined];
    }

    private async Task DelayWithTimeProviderAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (ReferenceEquals(_timeProvider, TimeProvider.System))
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var timer = _timeProvider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            tcs,
            delay,
            Timeout.InfiniteTimeSpan);
        await using var reg = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            tcs);
        await tcs.Task.ConfigureAwait(false);
    }

    private void RefreshCooldownExpirationNoLock()
    {
        if (_phase == AntiCheatGuardPhase.CooldownLocked
            && _cooldownEndsAtUtc.HasValue
            && _timeProvider.GetUtcNow() >= _cooldownEndsAtUtc.Value)
        {
            _phase = AntiCheatGuardPhase.IdleSafe;
            _activeProtectedGameId = null;
            _cooldownEndsAtUtc = null;
            _detectedResidualProcesses = [];
            PersistStateNoLock();
        }
    }

    private void PersistStateNoLock()
    {
        if (_persistedStateFilePath is null)
        {
            return;
        }

        try
        {
            if (_phase == AntiCheatGuardPhase.IdleSafe)
            {
                if (File.Exists(_persistedStateFilePath))
                {
                    File.Delete(_persistedStateFilePath);
                }

                return;
            }

            string? dir = Path.GetDirectoryName(_persistedStateFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var state = new PersistedGuardState
            {
                Phase = _phase,
                ActiveProtectedGameId = _activeProtectedGameId,
                CooldownEndsAtUtc = _cooldownEndsAtUtc,
                WrittenAtUtc = _timeProvider.GetUtcNow(),
            };
            string tmp = _persistedStateFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, _persistedStateFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is defense-in-depth; the in-memory lock stays authoritative for this process.
        }
    }

    private PersistedGuardState? TryReadPersistedState()
    {
        if (_persistedStateFilePath is null || !File.Exists(_persistedStateFilePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PersistedGuardState>(File.ReadAllText(_persistedStateFilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable/corrupted state: fail closed by treating it as an interrupted protected session.
            return new PersistedGuardState { Phase = AntiCheatGuardPhase.ResidualGuardWatch };
        }
    }

    private sealed class PersistedGuardState
    {
        public AntiCheatGuardPhase Phase { get; init; }
        public string? ActiveProtectedGameId { get; init; }
        public DateTimeOffset? CooldownEndsAtUtc { get; init; }
        public DateTimeOffset WrittenAtUtc { get; init; }
    }

    private AntiCheatGuardSnapshot BuildSnapshotNoLock()
    {
        double remaining = 0.0;
        if (_phase == AntiCheatGuardPhase.CooldownLocked && _cooldownEndsAtUtc.HasValue)
        {
            remaining = Math.Max(0.0, (_cooldownEndsAtUtc.Value - _timeProvider.GetUtcNow()).TotalSeconds);
        }

        bool isLocked = _phase != AntiCheatGuardPhase.IdleSafe;
        return new AntiCheatGuardSnapshot(
            Phase: _phase,
            IsHighRiskLocked: isLocked,
            RemainingCooldownSeconds: remaining,
            ActiveProtectedGameId: _activeProtectedGameId,
            DetectedResidualProcesses: _detectedResidualProcesses);
    }

    private void PublishCurrentSnapshot()
    {
        var snapshot = CurrentSnapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private void CancelCooldownCtsNoLock()
    {
        if (_cooldownCts is not null)
        {
            try
            {
                _cooldownCts.Cancel();
                _cooldownCts.Dispose();
            }
            catch
            {
                // Ignore disposal race.
            }

            _cooldownCts = null;
        }
    }

    private static void ScanDirectoryDepthBounded(
        DirectoryInfo directory,
        int currentDepth,
        int maxDepth,
        ref AntiCheatTier highestTier,
        ref string? primarySignature,
        List<string> matchedIndicators)
    {
        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (var file in directory.EnumerateFiles("*", enumOptions))
            {
                MatchEntry(file.Name, isDirectory: false, ref highestTier, ref primarySignature, matchedIndicators);
            }

            foreach (var subDir in directory.EnumerateDirectories("*", enumOptions))
            {
                MatchEntry(subDir.Name, isDirectory: true, ref highestTier, ref primarySignature, matchedIndicators);
                if (currentDepth + 1 <= maxDepth)
                {
                    ScanDirectoryDepthBounded(subDir, currentDepth + 1, maxDepth, ref highestTier, ref primarySignature, matchedIndicators);
                }
            }
        }
        catch
        {
            // Ignore transient IO permission denials during heuristic scan.
        }
    }

    private static void MatchEntry(
        string entryName,
        bool isDirectory,
        ref AntiCheatTier highestTier,
        ref string? primarySignature,
        List<string> matchedIndicators)
    {
        foreach (var rule in AntiCheatFeatureCatalog.DirectorySignatureRules)
        {
            if (!rule.Matches(entryName, isDirectory))
            {
                continue;
            }

            if (!matchedIndicators.Contains(rule.Signature, StringComparer.OrdinalIgnoreCase))
            {
                matchedIndicators.Add(rule.Signature);
            }

            if (rule.Tier > highestTier)
            {
                highestTier = rule.Tier;
                primarySignature = rule.Signature;
            }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            CancelCooldownCtsNoLock();
        }
    }
}
