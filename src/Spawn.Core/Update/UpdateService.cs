using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;

namespace Spawn.Core.Update;

/// <summary>
/// Signed in-app auto-updater for the <c>release</c> channel (<c>ADR-0007 §3.3</c>).
/// Enforces channel gating, strict HTTPS/host-allowlist/redirect/size caps, ECDSA P-256 DER manifest verification,
/// anti-downgrade checks, atomic staging &amp; cache pruning, idle+tray-only auto-install, TOCTOU-locked install
/// handoff, and bounded 20-entry <c>app_settings</c> persistence (zero schema upgrade).
/// </summary>
public sealed class UpdateService : IUpdateService
{
    public const string DevChannelStatusMessage = "开发版由本机打包自动重装";

    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _database;
    private readonly IUpdateIdleEvaluator _idleEvaluator;
    private readonly IUpdateQuitCoordinator _quitCoordinator;
    private readonly IUpdateInstallerLauncher _installerLauncher;
    private readonly TimeProvider _timeProvider;
    private readonly UpdateServiceOptions _options;
    private readonly HttpMessageHandler? _httpMessageHandler;
    private readonly ILogger<UpdateService> _logger;

    private readonly string _effectiveChannel;
    private readonly string _effectiveVersion;
    private readonly string _effectiveCommitSha;
    private readonly bool _isReleaseChannel;

    private static readonly AsyncLocal<(UpdateService Service, int LoopId)?> CurrentLoopContext = new();
    private const int SchedulerLoopId = 1;
    private const int IdleLoopId = 2;

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<CancellationTokenSource> _deferredDisposedCts = [];
    private int _activeQuitCallbackLoopId;

    private UpdateHttpFetcher? _fetcher;
    private CancellationTokenSource? _schedulerCts;
    private Task? _schedulerTask;
    private Task? _idleMonitorTask;

    private UpdateSettingsDto _settings = new();
    private DateTimeOffset? _lastCheckUtc;
    private DateTimeOffset? _nextCheckUtc;
    private UpdateCheckOutcome? _lastCheckOutcome;
    private string _statusMessage;
    private UpdateServiceState _state;
    private PreparedUpdateDto? _readyUpdate;
    private double? _downloadProgressPercent;
    private List<UpdateHistoryEntryDto> _history = [];
    private string? _lastNotifiedReadyVersion;
    private string? _lastNotifiedFailureVersion;
    private string? _lastAutoInstallFailedVersion;
    private UpdateStateSnapshot _snapshot;
    private int _disposed;

    public UpdateStateSnapshot CurrentSnapshot
    {
        get
        {
            lock (_stateLock)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<UpdateStateSnapshot>? StateChanged;
    public event EventHandler<UpdateUserNotificationEvent>? UserNotificationRequested;

    public UpdateService(
        SpawnPaths paths,
        SpawnDatabase database,
        IUpdateIdleEvaluator idleEvaluator,
        IUpdateQuitCoordinator quitCoordinator,
        IUpdateInstallerLauncher installerLauncher,
        TimeProvider timeProvider,
        UpdateServiceOptions? options = null,
        HttpMessageHandler? httpMessageHandler = null,
        ILogger<UpdateService>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _idleEvaluator = idleEvaluator ?? throw new ArgumentNullException(nameof(idleEvaluator));
        _quitCoordinator = quitCoordinator ?? throw new ArgumentNullException(nameof(quitCoordinator));
        _installerLauncher = installerLauncher ?? throw new ArgumentNullException(nameof(installerLauncher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? new UpdateServiceOptions();
        _httpMessageHandler = httpMessageHandler;
        _logger = logger ?? NullLogger<UpdateService>.Instance;

        _effectiveChannel = string.IsNullOrWhiteSpace(_options.ChannelOverride)
            ? SpawnBuildInfo.Channel
            : _options.ChannelOverride.Trim();
        _effectiveVersion = string.IsNullOrWhiteSpace(_options.CurrentVersionOverride)
            ? SpawnBuildInfo.Version
            : _options.CurrentVersionOverride.Trim();
        _effectiveCommitSha = string.IsNullOrWhiteSpace(_options.CommitShaOverride)
            ? SpawnBuildInfo.CommitSha
            : _options.CommitShaOverride.Trim();
        _isReleaseChannel = string.Equals(_effectiveChannel, "release", StringComparison.OrdinalIgnoreCase);

        if (_isReleaseChannel)
        {
            _state = UpdateServiceState.Idle;
            _statusMessage = "等待检查更新";
        }
        else
        {
            _state = UpdateServiceState.DisabledDevChannel;
            _statusMessage = DevChannelStatusMessage;
        }

        _snapshot = BuildSnapshotCore();
    }

    public async Task InitializeAsync(
        bool launchedAfterUpdate = false,
        bool consumedValidUpdateMarker = false,
        string? previousVersionFromMarker = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);

            _settings = await LoadSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
            _lastCheckUtc = await LoadNullableUtcSettingAsync(UpdateSettingKeys.LastCheckUtc, cancellationToken).ConfigureAwait(false);
            _nextCheckUtc = await LoadNullableUtcSettingAsync(UpdateSettingKeys.NextCheckUtc, cancellationToken).ConfigureAwait(false);

            string? rawOutcome = await _database.GetSettingAsync(UpdateSettingKeys.LastCheckOutcome, cancellationToken).ConfigureAwait(false);
            _lastCheckOutcome = Enum.TryParse<UpdateCheckOutcome>(rawOutcome, ignoreCase: true, out UpdateCheckOutcome parsedOutcome)
                ? parsedOutcome
                : null;

            string? savedMessage = await _database.GetSettingAsync(UpdateSettingKeys.LastCheckMessage, cancellationToken).ConfigureAwait(false);
            _lastNotifiedFailureVersion = await _database.GetSettingAsync(UpdateSettingKeys.LastNotifiedFailureVersion, cancellationToken).ConfigureAwait(false);

            List<UpdateHistoryEntryDto> loadedHistory = await _database.GetSettingJsonAsync<List<UpdateHistoryEntryDto>>(
                UpdateSettingKeys.HistoryJson,
                [],
                cancellationToken).ConfigureAwait(false);
            _history = TrimHistory(loadedHistory);

            if (!_isReleaseChannel)
            {
                _readyUpdate = null;
                _nextCheckUtc = null;
                _state = UpdateServiceState.DisabledDevChannel;
                _statusMessage = DevChannelStatusMessage;
                PublishSnapshot();
                return;
            }

            // Record completed in-app update transition if restarted by installer with --after-update / valid update marker.
            bool isPostUpdateTransition = (launchedAfterUpdate || consumedValidUpdateMarker)
                && !string.IsNullOrWhiteSpace(previousVersionFromMarker)
                && !string.Equals(previousVersionFromMarker.Trim(), _effectiveVersion, StringComparison.OrdinalIgnoreCase);

            if (isPostUpdateTransition)
            {
                string fromVer = previousVersionFromMarker!.Trim();
                await AppendHistoryCoreAsync(
                    new UpdateHistoryEntryDto
                    {
                        TimestampUtc = _timeProvider.GetUtcNow(),
                        FromVersion = fromVer,
                        ToVersion = _effectiveVersion,
                        Trigger = UpdateTriggerKind.Auto,
                        Result = UpdateHistoryResult.Completed,
                        Detail = $"已从 v{fromVer} 更新至 v{_effectiveVersion}",
                    },
                    cancellationToken).ConfigureAwait(false);

                await _database.SetSettingAsync(UpdateSettingKeys.PendingUpdateJson, null, cancellationToken).ConfigureAwait(false);
                await _database.SetSettingAsync(UpdateSettingKeys.LastNotifiedFailureVersion, null, cancellationToken).ConfigureAwait(false);
                _lastNotifiedFailureVersion = null;
                _readyUpdate = null;
                PruneUpdatesCacheDirectory(keepVersionDirectoryName: null);
                _state = UpdateServiceState.UpToDate;
                _statusMessage = $"已完成更新，当前版本 v{_effectiveVersion}";
                await _database.SetSettingAsync(UpdateSettingKeys.LastCheckMessage, _statusMessage, cancellationToken).ConfigureAwait(false);
                PublishSnapshot();
                return;
            }

            // Restore any previously staged verified update if it is still newer than current version and intact on disk.
            PreparedUpdateDto? staged = await _database.GetSettingJsonAsync<PreparedUpdateDto?>(
                UpdateSettingKeys.PendingUpdateJson,
                null,
                cancellationToken).ConfigureAwait(false);

            if (staged is not null)
            {
                bool stillNewer = UpdateSemanticVersion.TryParseStrict(staged.Version, out var stagedVer)
                    && UpdateSemanticVersion.TryParseStrict(_effectiveVersion, out var curVer)
                    && stagedVer > curVer;

                if (stillNewer
                    && IsPathStrictlyUnderUpdatesCache(staged.InstallerPath)
                    && await VerifyFileIntegrityAsync(staged.InstallerPath, staged.InstallerSize, staged.ExpectedSha256, cancellationToken).ConfigureAwait(false))
                {
                    _readyUpdate = staged;
                    _state = UpdateServiceState.ReadyToInstall;
                    _statusMessage = $"新版本 v{staged.Version} 已就绪，重启更新";
                    PublishSnapshot();
                    return;
                }

                // Staged artifact is obsolete or corrupted — clear and prune.
                await _database.SetSettingAsync(UpdateSettingKeys.PendingUpdateJson, null, cancellationToken).ConfigureAwait(false);
                PruneUpdatesCacheDirectory(keepVersionDirectoryName: null);
            }

            _readyUpdate = null;
            _state = _lastCheckOutcome switch
            {
                UpdateCheckOutcome.UpToDate => UpdateServiceState.UpToDate,
                UpdateCheckOutcome.DeferredOffline => UpdateServiceState.DeferredOffline,
                UpdateCheckOutcome.VerificationFailed or UpdateCheckOutcome.DownloadFailed or UpdateCheckOutcome.InstallFailed => UpdateServiceState.Error,
                _ => UpdateServiceState.Idle,
            };
            _statusMessage = string.IsNullOrWhiteSpace(savedMessage) ? "等待检查更新" : savedMessage;
            PublishSnapshot();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void StartBackgroundScheduler()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_isReleaseChannel)
        {
            // Dev / source builds MUST NEVER schedule background checks or touch network.
            return;
        }

        lock (_stateLock)
        {
            if (_schedulerCts is not null)
            {
                return;
            }

            _schedulerCts = new CancellationTokenSource();
            CancellationToken token = _schedulerCts.Token;

            // Arm initial timers synchronously so virtual TimeProvider clocks observe exact StartupDelay / IdleRetryInterval.
            Task initialCheckDelay = _options.StartupDelay > TimeSpan.Zero
                ? Task.Delay(_options.StartupDelay, _timeProvider, token)
                : Task.CompletedTask;
            TimeSpan idleInterval = _options.IdleRetryInterval > TimeSpan.Zero
                ? _options.IdleRetryInterval
                : TimeSpan.FromMinutes(2);
            Task initialIdleDelay = Task.Delay(idleInterval, _timeProvider, token);

            if (_settings.AutoCheckEnabled)
            {
                _nextCheckUtc = _timeProvider.GetUtcNow() + _options.StartupDelay;
                _snapshot = BuildSnapshotCore();
            }

            _schedulerTask = Task.Run(() => RunBackgroundCheckLoopAsync(initialCheckDelay, token), CancellationToken.None);
            _idleMonitorTask = Task.Run(() => RunIdleInstallLoopAsync(initialIdleDelay, idleInterval, token), CancellationToken.None);
        }

        RaiseStateChanged();
        if (_settings.AutoCheckEnabled && _nextCheckUtc.HasValue)
        {
            _ = PersistNextCheckUtcBestEffortAsync(_nextCheckUtc);
        }
    }

    public async Task StopBackgroundSchedulerAsync()
    {
        CancellationTokenSource? cts;
        Task? schedulerTask;
        Task? idleTask;

        lock (_stateLock)
        {
            cts = _schedulerCts;
            schedulerTask = _schedulerTask;
            idleTask = _idleMonitorTask;
            _schedulerCts = null;
            _schedulerTask = null;
            _idleMonitorTask = null;
        }

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation race.
            }

            (UpdateService Service, int LoopId)? activeLoop = CurrentLoopContext.Value;
            int quitLoopId = Volatile.Read(ref _activeQuitCallbackLoopId);
            bool isReenteringScheduler = (activeLoop.HasValue
                    && ReferenceEquals(activeLoop.Value.Service, this)
                    && activeLoop.Value.LoopId == SchedulerLoopId)
                || quitLoopId == SchedulerLoopId;
            bool isReenteringIdle = (activeLoop.HasValue
                    && ReferenceEquals(activeLoop.Value.Service, this)
                    && activeLoop.Value.LoopId == IdleLoopId)
                || quitLoopId == IdleLoopId;

            if (schedulerTask is not null && !isReenteringScheduler)
            {
                try
                {
                    await schedulerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Update scheduler loop exited with exception during stop.");
                }
            }

            if (idleTask is not null && !isReenteringIdle)
            {
                try
                {
                    await idleTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Update idle loop exited with exception during stop.");
                }
            }

            if (isReenteringScheduler || isReenteringIdle)
            {
                lock (_stateLock)
                {
                    _deferredDisposedCts.Add(cts);
                }
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    public async Task<UpdateSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await LoadSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(UpdateSettingsDto settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _database.SetSettingAsync(
                UpdateSettingKeys.AutoCheckEnabled,
                settings.AutoCheckEnabled ? "true" : "false",
                cancellationToken).ConfigureAwait(false);
            await _database.SetSettingAsync(
                UpdateSettingKeys.AutoInstallWhenIdle,
                settings.AutoInstallWhenIdle ? "true" : "false",
                cancellationToken).ConfigureAwait(false);

            _settings = settings;

            if (_isReleaseChannel)
            {
                _nextCheckUtc = settings.AutoCheckEnabled
                    ? (_nextCheckUtc ?? (_timeProvider.GetUtcNow() + _options.CheckInterval))
                    : null;
                await _database.SetSettingAsync(
                    UpdateSettingKeys.NextCheckUtc,
                    _nextCheckUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _nextCheckUtc = null;
            }

            PublishSnapshot();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateTriggerKind trigger,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Hard channel guard: dev and source builds MUST NEVER issue any HTTP request!
        if (!_isReleaseChannel)
        {
            lock (_stateLock)
            {
                _state = UpdateServiceState.DisabledDevChannel;
                _statusMessage = DevChannelStatusMessage;
                _snapshot = BuildSnapshotCore();
            }

            RaiseStateChanged();
            return new UpdateCheckResult
            {
                Outcome = UpdateCheckOutcome.DisabledDevChannel,
                Message = DevChannelStatusMessage,
                ReadyUpdate = null,
                AutoInstallTriggered = false,
                ShouldNotifyUser = false,
            };
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset checkTimeUtc = _timeProvider.GetUtcNow();
        try
        {
            return await CheckForUpdatesLockedAsync(trigger, checkTimeUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-requested abort: never leave the UI stuck on Checking/Downloading.
            RestoreSettledStateAfterCancellation();
            throw;
        }
        catch (Exception ex)
        {
            // Any other unexpected failure (I/O, database, internal cancellation leak, ...) is recorded
            // through the regular failure path so the state machine always settles.
            return await HandleUnexpectedCheckFailureAsync(checkTimeUtc, trigger, ex).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<UpdateCheckResult> CheckForUpdatesLockedAsync(
        UpdateTriggerKind trigger,
        DateTimeOffset checkTimeUtc,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _downloadProgressPercent = null;
        _state = UpdateServiceState.Checking;
        _statusMessage = "正在检查正式版更新…";
        PublishSnapshot();

        UpdateHttpFetcher fetcher = GetOrCreateFetcher();

        // 1. Download manifest (<= 64 KiB) and signature (<= 4 KiB).
        byte[] manifestBytes;
        byte[] signatureBytes;
        try
        {
            manifestBytes = await fetcher.DownloadBytesAsync(
                _options.ManifestUri,
                _options.MaxManifestBytes,
                cancellationToken).ConfigureAwait(false);
            signatureBytes = await fetcher.DownloadBytesAsync(
                _options.SignatureUri,
                _options.MaxSignatureBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateNetworkException ex)
        {
            return await HandleOfflineDeferralAsync(checkTimeUtc, trigger, ex.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UpdatePolicyViolationException ex)
        {
            return await HandleCheckFailureAsync(
                checkTimeUtc,
                trigger,
                UpdateCheckOutcome.VerificationFailed,
                targetVersion: null,
                ex.Message,
                cancellationToken).ConfigureAwait(false);
        }

        // 2. Cryptographic ECDSA P-256 DER verification + schema/channel/version/URL validation.
        ManifestVerificationResult verification = UpdateManifestVerifier.Verify(
            manifestBytes,
            signatureBytes,
            _options.PublicKeyPem,
            _effectiveVersion,
            _options);

        if (verification.Status == ManifestVerificationStatus.SameVersion)
        {
            _readyUpdate = null;
            await _database.SetSettingAsync(UpdateSettingKeys.PendingUpdateJson, null, cancellationToken).ConfigureAwait(false);
            PruneUpdatesCacheDirectory(keepVersionDirectoryName: null);

            DateTimeOffset? nextUtc = _settings.AutoCheckEnabled ? checkTimeUtc + _options.CheckInterval : null;
            await PersistCheckMetadataAsync(checkTimeUtc, nextUtc, UpdateCheckOutcome.UpToDate, verification.Message, cancellationToken)
                .ConfigureAwait(false);
            await AppendHistoryCoreAsync(
                new UpdateHistoryEntryDto
                {
                    TimestampUtc = checkTimeUtc,
                    FromVersion = _effectiveVersion,
                    ToVersion = _effectiveVersion,
                    Trigger = trigger,
                    Result = UpdateHistoryResult.UpToDate,
                    Detail = verification.Message,
                },
                cancellationToken).ConfigureAwait(false);

            _state = UpdateServiceState.UpToDate;
            _statusMessage = verification.Message;
            PublishSnapshot();

            return new UpdateCheckResult
            {
                Outcome = UpdateCheckOutcome.UpToDate,
                Message = verification.Message,
                ReadyUpdate = null,
                AutoInstallTriggered = false,
                ShouldNotifyUser = false,
            };
        }

        if (!verification.IsEligibleForDownload || verification.Manifest is null || verification.ValidatedInstallerUri is null)
        {
            return await HandleCheckFailureAsync(
                checkTimeUtc,
                trigger,
                UpdateCheckOutcome.VerificationFailed,
                targetVersion: verification.Manifest?.Version,
                verification.Message,
                cancellationToken).ConfigureAwait(false);
        }

        UpdateManifestDto manifest = verification.Manifest;
        string targetVersion = verification.ParsedVersion.ToString();
        string expectedSha256 = verification.NormalizedSha256;
        string versionDir = Path.Combine(_paths.UpdatesCacheDirectory, targetVersion);
        string finalPath = Path.Combine(versionDir, manifest.Installer.Name);
        string partialPath = finalPath + ".partial";

        // Defense-in-depth containment check against path traversal.
        if (!IsPathStrictlyUnderUpdatesCache(finalPath) || !IsPathStrictlyUnderUpdatesCache(partialPath))
        {
            return await HandleCheckFailureAsync(
                checkTimeUtc,
                trigger,
                UpdateCheckOutcome.VerificationFailed,
                targetVersion,
                "安装包缓存目标路径越界，已拒绝。",
                cancellationToken).ConfigureAwait(false);
        }

        // 3. Reuse already-verified installer in cache if intact; otherwise download to .partial, verify, atomically rename, prune old versions.
        bool reusedVerifiedFile = await VerifyFileIntegrityAsync(
            finalPath,
            manifest.Installer.Size,
            expectedSha256,
            cancellationToken).ConfigureAwait(false);

        if (!reusedVerifiedFile)
        {
            _state = UpdateServiceState.Downloading;
            _downloadProgressPercent = 0.0;
            _statusMessage = $"正在下载正式版 v{targetVersion}…";
            PublishSnapshot();

            var progress = new Progress<double>(pct =>
            {
                lock (_stateLock)
                {
                    // Progress<T> callbacks are posted asynchronously; ignore late reports once the
                    // download has settled (timeout, cancellation, failure) so they cannot resurrect it.
                    if (_state != UpdateServiceState.Downloading)
                    {
                        return;
                    }

                    _downloadProgressPercent = pct;
                    _statusMessage = string.Create(CultureInfo.InvariantCulture, $"正在下载正式版 v{targetVersion} ({pct:0}%)…");
                    _snapshot = BuildSnapshotCore();
                }

                RaiseStateChanged();
            });

            DownloadedFileResult downloaded;
            try
            {
                downloaded = await fetcher.DownloadFileAsync(
                    verification.ValidatedInstallerUri,
                    partialPath,
                    _options.MaxInstallerBytes,
                    manifest.Installer.Size,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (UpdateNetworkException ex)
            {
                return await HandleOfflineDeferralAsync(checkTimeUtc, trigger, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UpdatePolicyViolationException ex)
            {
                return await HandleCheckFailureAsync(
                    checkTimeUtc,
                    trigger,
                    UpdateCheckOutcome.DownloadFailed,
                    targetVersion,
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
            }

            // Exact byte size AND SHA-256 verification before atomic promotion!
            if (downloaded.BytesWritten != manifest.Installer.Size)
            {
                TryDeleteFileQuietly(partialPath);
                return await HandleCheckFailureAsync(
                    checkTimeUtc,
                    trigger,
                    UpdateCheckOutcome.DownloadFailed,
                    targetVersion,
                    $"安装包大小校验失败（期望 {manifest.Installer.Size} 字节，实际 {downloaded.BytesWritten} 字节）。",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(downloaded.Sha256LowerHex, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFileQuietly(partialPath);
                return await HandleCheckFailureAsync(
                    checkTimeUtc,
                    trigger,
                    UpdateCheckOutcome.DownloadFailed,
                    targetVersion,
                    "安装包 SHA-256 校验未通过，已丢弃临时文件。",
                    cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(versionDir);
            File.Move(partialPath, finalPath, overwrite: true);
            PruneUpdatesCacheDirectory(keepVersionDirectoryName: targetVersion);
        }
        else
        {
            PruneUpdatesCacheDirectory(keepVersionDirectoryName: targetVersion);
        }

        var prepared = new PreparedUpdateDto
        {
            Version = targetVersion,
            InstallerName = manifest.Installer.Name,
            InstallerPath = finalPath,
            InstallerSize = manifest.Installer.Size,
            ExpectedSha256 = expectedSha256,
            PublishedUtc = manifest.PublishedUtc,
            NotesUrl = manifest.NotesUrl,
            PreparedAtUtc = _timeProvider.GetUtcNow(),
        };

        _readyUpdate = prepared;
        _downloadProgressPercent = 100.0;
        _state = UpdateServiceState.ReadyToInstall;
        _statusMessage = $"新版本 v{targetVersion} 已就绪，重启更新";

        await _database.SetSettingJsonAsync(UpdateSettingKeys.PendingUpdateJson, prepared, cancellationToken).ConfigureAwait(false);

        DateTimeOffset? nextCheckUtc = _settings.AutoCheckEnabled ? checkTimeUtc + _options.CheckInterval : null;
        await PersistCheckMetadataAsync(checkTimeUtc, nextCheckUtc, UpdateCheckOutcome.UpdateReady, _statusMessage, cancellationToken)
            .ConfigureAwait(false);

        if (!reusedVerifiedFile)
        {
            await AppendHistoryCoreAsync(
                new UpdateHistoryEntryDto
                {
                    TimestampUtc = _timeProvider.GetUtcNow(),
                    FromVersion = _effectiveVersion,
                    ToVersion = targetVersion,
                    Trigger = trigger,
                    Result = UpdateHistoryResult.Downloaded,
                    Detail = $"已下载并完成签名与 SHA-256 校验（{manifest.Installer.Name}）",
                },
                cancellationToken).ConfigureAwait(false);
        }

        PublishSnapshot();

        // 4. Evaluate Q-002 idle & tray-only auto-install policy.
        UpdateIdleSnapshot idle = _idleEvaluator.Evaluate();
        bool eligibleForSilentAutoInstall = trigger == UpdateTriggerKind.Auto
            && _settings.AutoInstallWhenIdle
            && idle.CanAutoInstallSilently
            && !string.Equals(_lastAutoInstallFailedVersion, targetVersion, StringComparison.OrdinalIgnoreCase);

        if (eligibleForSilentAutoInstall)
        {
            UpdateInstallResult installResult = await InstallReadyUpdateLockedAsync(
                UpdateTriggerKind.Auto,
                cancellationToken).ConfigureAwait(false);

            return new UpdateCheckResult
            {
                Outcome = installResult.Succeeded ? UpdateCheckOutcome.InstallStarted : UpdateCheckOutcome.InstallFailed,
                Message = installResult.Message,
                ReadyUpdate = _readyUpdate,
                AutoInstallTriggered = installResult.Succeeded,
                ShouldNotifyUser = installResult.ShouldNotifyUser,
            };
        }

        // Window is open, busy, notify-only, or manual check -> surface ready banner & tray notification once without forcing restart.
        bool shouldNotifyReady = !string.Equals(_lastNotifiedReadyVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
        if (shouldNotifyReady)
        {
            _lastNotifiedReadyVersion = targetVersion;
            UserNotificationRequested?.Invoke(
                this,
                new UpdateUserNotificationEvent(
                    Title: "SPAWN 新版本已就绪",
                    Message: $"正式版 v{targetVersion} 已完成验签并就绪，点击重启更新。",
                    TargetVersion: targetVersion,
                    IsError: false,
                    IsUpdateReady: true));
        }

        return new UpdateCheckResult
        {
            Outcome = UpdateCheckOutcome.UpdateReady,
            Message = _statusMessage,
            ReadyUpdate = prepared,
            AutoInstallTriggered = false,
            ShouldNotifyUser = shouldNotifyReady,
        };
    }

    private void RestoreSettledStateAfterCancellation()
    {
        lock (_stateLock)
        {
            if (_state is UpdateServiceState.Checking or UpdateServiceState.Downloading)
            {
                _state = _readyUpdate is not null ? UpdateServiceState.ReadyToInstall : UpdateServiceState.Idle;
                _statusMessage = _readyUpdate is not null
                    ? $"新版本 v{_readyUpdate.Version} 已就绪，重启更新"
                    : "已取消本次更新检查";
            }

            _downloadProgressPercent = null;
        }

        PublishSnapshot();
    }

    private async Task<UpdateCheckResult> HandleUnexpectedCheckFailureAsync(
        DateTimeOffset checkTimeUtc,
        UpdateTriggerKind trigger,
        Exception exception)
    {
        _logger.LogWarning(exception, "Unexpected update check failure; settling update state.");

        UpdateCheckOutcome outcome = _state == UpdateServiceState.Downloading
            ? UpdateCheckOutcome.DownloadFailed
            : UpdateCheckOutcome.VerificationFailed;
        string detail = $"更新检查意外失败：{exception.Message}";

        try
        {
            return await HandleCheckFailureAsync(
                checkTimeUtc,
                trigger,
                outcome,
                targetVersion: null,
                detail,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception persistException)
        {
            // Persistence itself failed (e.g. database unavailable): settle in-memory state anyway.
            _logger.LogWarning(persistException, "Persisting unexpected update check failure failed; settling in memory only.");
            lock (_stateLock)
            {
                _downloadProgressPercent = null;
                _state = _readyUpdate is not null ? UpdateServiceState.ReadyToInstall : UpdateServiceState.Error;
                _statusMessage = detail;
            }

            PublishSnapshot();
            return new UpdateCheckResult
            {
                Outcome = outcome,
                Message = detail,
                ReadyUpdate = _readyUpdate,
                AutoInstallTriggered = false,
                ShouldNotifyUser = trigger == UpdateTriggerKind.Manual,
            };
        }
    }

    public async Task<UpdateInstallResult> TryAutoInstallIfEligibleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_isReleaseChannel)
        {
            return new UpdateInstallResult
            {
                Succeeded = false,
                Message = DevChannelStatusMessage,
                ShouldNotifyUser = false,
            };
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new UpdateInstallResult
            {
                Succeeded = false,
                Message = "更新操作正在进行中。",
                ShouldNotifyUser = false,
            };
        }

        try
        {
            if (_readyUpdate is null)
            {
                return new UpdateInstallResult
                {
                    Succeeded = false,
                    Message = "当前没有待安装的更新。",
                    ShouldNotifyUser = false,
                };
            }

            if (!_settings.AutoInstallWhenIdle)
            {
                return new UpdateInstallResult
                {
                    Succeeded = false,
                    Message = "当前已设置为仅提醒，不自动重启更新。",
                    ShouldNotifyUser = false,
                    TargetVersion = _readyUpdate.Version,
                };
            }

            if (string.Equals(_lastAutoInstallFailedVersion, _readyUpdate.Version, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateInstallResult
                {
                    Succeeded = false,
                    Message = $"版本 v{_readyUpdate.Version} 上次自动安装失败，已暂停自动重试。",
                    ShouldNotifyUser = false,
                    TargetVersion = _readyUpdate.Version,
                };
            }

            UpdateIdleSnapshot idle = _idleEvaluator.Evaluate();
            if (!idle.CanAutoInstallSilently)
            {
                return new UpdateInstallResult
                {
                    Succeeded = false,
                    Message = DescribeBusyOrWindowReason(idle),
                    ShouldNotifyUser = false,
                    TargetVersion = _readyUpdate.Version,
                };
            }

            return await InstallReadyUpdateLockedAsync(UpdateTriggerKind.Auto, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<UpdateInstallResult> InstallReadyUpdateAsync(
        UpdateTriggerKind trigger = UpdateTriggerKind.Manual,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_isReleaseChannel)
        {
            return new UpdateInstallResult
            {
                Succeeded = false,
                Message = DevChannelStatusMessage,
                ShouldNotifyUser = false,
            };
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallReadyUpdateLockedAsync(trigger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<UpdateInstallResult> InstallReadyUpdateLockedAsync(
        UpdateTriggerKind trigger,
        CancellationToken cancellationToken)
    {
        PreparedUpdateDto? ready = _readyUpdate;
        if (ready is null)
        {
            return new UpdateInstallResult
            {
                Succeeded = false,
                Message = "当前没有已就绪的更新安装包。",
                ShouldNotifyUser = false,
            };
        }

        if (trigger == UpdateTriggerKind.Auto)
        {
            UpdateIdleSnapshot idle = _idleEvaluator.Evaluate();
            if (!_settings.AutoInstallWhenIdle || !idle.CanAutoInstallSilently)
            {
                return new UpdateInstallResult
                {
                    Succeeded = false,
                    Message = DescribeBusyOrWindowReason(idle),
                    ShouldNotifyUser = false,
                    TargetVersion = ready.Version,
                };
            }
        }

        if (!IsPathStrictlyUnderUpdatesCache(ready.InstallerPath) || !File.Exists(ready.InstallerPath))
        {
            _readyUpdate = null;
            await _database.SetSettingAsync(UpdateSettingKeys.PendingUpdateJson, null, cancellationToken).ConfigureAwait(false);
            return await RecordInstallFailureAsync(
                ready.Version,
                trigger,
                "已就绪的安装包文件缺失或路径非法，请重新检查更新。",
                cancellationToken).ConfigureAwait(false);
        }

        // TOCTOU defense: open with FileShare.Read (denying concurrent writes/truncation/renames),
        // recompute SHA-256 directly from this exact handle, and KEEP THE HANDLE OPEN across
        // RequestQuitForUpdateAsync(UpdateRestartReason.Update) AND ShellExecute installer launch!
        FileStream? lockStream = null;
        bool quitPrepared = false;
        try
        {
            lockStream = new FileStream(
                ready.InstallerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            if (lockStream.Length != ready.InstallerSize)
            {
                await lockStream.DisposeAsync().ConfigureAwait(false);
                lockStream = null;
                TryDeleteFileQuietly(ready.InstallerPath);
                _readyUpdate = null;
                await _database.SetSettingAsync(UpdateSettingKeys.PendingUpdateJson, null, cancellationToken).ConfigureAwait(false);

                return await RecordInstallFailureAsync(
                    ready.Version,
                    trigger,
                    $"安装前 TOCTOU 大小复核未通过（期望 {ready.InstallerSize} 字节），已中止安装。",
                    cancellationToken).ConfigureAwait(false);
            }

            byte[] digestBytes = await SHA256.HashDataAsync(lockStream, cancellationToken).ConfigureAwait(false);
            string actualSha256 = Convert.ToHexStringLower(digestBytes);
            if (!string.Equals(actualSha256, ready.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                await lockStream.DisposeAsync().ConfigureAwait(false);
                lockStream = null;
                TryDeleteFileQuietly(ready.InstallerPath);
                _readyUpdate = null;
                await _database.SetSettingAsync(UpdateSettingKeys.PendingUpdateJson, null, cancellationToken).ConfigureAwait(false);

                return await RecordInstallFailureAsync(
                    ready.Version,
                    trigger,
                    "安装前 TOCTOU SHA-256 复核未通过（安装包可能已被篡改），已中止安装。",
                    cancellationToken).ConfigureAwait(false);
            }

            _state = UpdateServiceState.Installing;
            _statusMessage = $"正在重启并安装正式版 v{ready.Version}…";
            PublishSnapshot();

            // Step 1 (handle still open!): prepare graceful quit & write reason="update" restart marker.
            // Use CancellationToken.None past this commit point because RequestQuitForUpdateAsync stops
            // the background scheduler (cancelling its loop token) even when auto-install was initiated by that loop.
            (UpdateService Service, int LoopId)? callerLoop = CurrentLoopContext.Value;
            int loopIdForQuit = callerLoop.HasValue && ReferenceEquals(callerLoop.Value.Service, this)
                ? callerLoop.Value.LoopId
                : 0;
            Volatile.Write(ref _activeQuitCallbackLoopId, loopIdForQuit);
            try
            {
                await _quitCoordinator.RequestQuitForUpdateAsync(UpdateRestartReason.Update, CancellationToken.None)
                    .ConfigureAwait(false);
                quitPrepared = true;
            }
            finally
            {
                Volatile.Write(ref _activeQuitCallbackLoopId, 0);
            }

            // Step 2 (handle STILL open!): launch per-user silent installer with /RELAUNCH (zero UAC).
            bool launched = _installerLauncher.LaunchSilentInstaller(
                ready.InstallerPath,
                _options.SilentInstallerArguments,
                out string? launchError);

            if (!launched)
            {
                await lockStream.DisposeAsync().ConfigureAwait(false);
                lockStream = null;

                await _quitCoordinator.AbortDeferredShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                quitPrepared = false;

                string detail = string.IsNullOrWhiteSpace(launchError)
                    ? "启动静默更新安装器失败。"
                    : $"启动静默更新安装器失败：{launchError}";
                return await RecordInstallFailureAsync(ready.Version, trigger, detail, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Point of no return: the silent installer process is already running!
            // Persist metadata/history best-effort with CancellationToken.None so nothing can skip
            // releasing the lock handle or completing deferred host shutdown.
            try
            {
                await PersistCheckMetadataAsync(
                    _lastCheckUtc ?? _timeProvider.GetUtcNow(),
                    _nextCheckUtc,
                    UpdateCheckOutcome.InstallStarted,
                    $"已启动 v{ready.Version} 静默安装器，等待重启",
                    CancellationToken.None).ConfigureAwait(false);

                await AppendHistoryCoreAsync(
                    new UpdateHistoryEntryDto
                    {
                        TimestampUtc = _timeProvider.GetUtcNow(),
                        FromVersion = _effectiveVersion,
                        ToVersion = ready.Version,
                        Trigger = trigger,
                        Result = UpdateHistoryResult.InstallLaunched,
                        Detail = $"已持锁复核 SHA-256 并启动静默安装器（{ready.InstallerName}）",
                    },
                    CancellationToken.None).ConfigureAwait(false);

                await _database.SetSettingAsync(UpdateSettingKeys.LastNotifiedFailureVersion, null, CancellationToken.None)
                    .ConfigureAwait(false);
                _lastNotifiedFailureVersion = null;
                _lastAutoInstallFailedVersion = null;
                PublishSnapshot();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Best-effort post-launch update history persistence failed; continuing shutdown.");
            }
            finally
            {
                if (lockStream is not null)
                {
                    await lockStream.DisposeAsync().ConfigureAwait(false);
                    lockStream = null;
                }

                _quitCoordinator.CompleteDeferredShutdown();
            }

            return new UpdateInstallResult
            {
                Succeeded = true,
                Message = $"已启动 v{ready.Version} 静默更新安装器。",
                ShouldNotifyUser = false,
                TargetVersion = ready.Version,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (lockStream is not null)
            {
                await lockStream.DisposeAsync().ConfigureAwait(false);
                lockStream = null;
            }

            if (quitPrepared)
            {
                try
                {
                    await _quitCoordinator.AbortDeferredShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort abort.
                }
            }

            return await RecordInstallFailureAsync(
                ready.Version,
                trigger,
                $"执行静默更新失败：{ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (lockStream is not null)
            {
                await lockStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<UpdateCheckResult> HandleOfflineDeferralAsync(
        DateTimeOffset checkTimeUtc,
        UpdateTriggerKind trigger,
        string detail,
        CancellationToken cancellationToken)
    {
        const string message = "当前离线或更新源暂不可达，已静默顺延至下次检查";
        DateTimeOffset? nextUtc = _settings.AutoCheckEnabled ? checkTimeUtc + _options.CheckInterval : null;

        await PersistCheckMetadataAsync(checkTimeUtc, nextUtc, UpdateCheckOutcome.DeferredOffline, message, cancellationToken)
            .ConfigureAwait(false);
        await AppendHistoryCoreAsync(
            new UpdateHistoryEntryDto
            {
                TimestampUtc = checkTimeUtc,
                FromVersion = _effectiveVersion,
                ToVersion = null,
                Trigger = trigger,
                Result = UpdateHistoryResult.DeferredOffline,
                Detail = string.IsNullOrWhiteSpace(detail) ? message : detail,
            },
            cancellationToken).ConfigureAwait(false);

        _downloadProgressPercent = null;
        _state = _readyUpdate is not null ? UpdateServiceState.ReadyToInstall : UpdateServiceState.DeferredOffline;
        _statusMessage = _readyUpdate is not null
            ? $"新版本 v{_readyUpdate.Version} 已就绪，重启更新"
            : message;
        PublishSnapshot();

        return new UpdateCheckResult
        {
            Outcome = UpdateCheckOutcome.DeferredOffline,
            Message = message,
            ReadyUpdate = _readyUpdate,
            AutoInstallTriggered = false,
            ShouldNotifyUser = false,
        };
    }

    private async Task<UpdateCheckResult> HandleCheckFailureAsync(
        DateTimeOffset checkTimeUtc,
        UpdateTriggerKind trigger,
        UpdateCheckOutcome outcome,
        string? targetVersion,
        string detail,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? nextUtc = _settings.AutoCheckEnabled ? checkTimeUtc + _options.CheckInterval : null;
        await PersistCheckMetadataAsync(checkTimeUtc, nextUtc, outcome, detail, cancellationToken).ConfigureAwait(false);
        await AppendHistoryCoreAsync(
            new UpdateHistoryEntryDto
            {
                TimestampUtc = checkTimeUtc,
                FromVersion = _effectiveVersion,
                ToVersion = targetVersion,
                Trigger = trigger,
                Result = UpdateHistoryResult.Failed,
                Detail = detail,
            },
            cancellationToken).ConfigureAwait(false);

        _downloadProgressPercent = null;
        _state = _readyUpdate is not null ? UpdateServiceState.ReadyToInstall : UpdateServiceState.Error;
        _statusMessage = detail;
        PublishSnapshot();

        string failureKey = !string.IsNullOrWhiteSpace(targetVersion) ? targetVersion! : outcome.ToString();
        bool shouldNotify = trigger == UpdateTriggerKind.Manual
            || !string.Equals(_lastNotifiedFailureVersion, failureKey, StringComparison.OrdinalIgnoreCase);

        if (shouldNotify && trigger == UpdateTriggerKind.Auto)
        {
            _lastNotifiedFailureVersion = failureKey;
            await _database.SetSettingAsync(UpdateSettingKeys.LastNotifiedFailureVersion, failureKey, cancellationToken)
                .ConfigureAwait(false);
        }

        return new UpdateCheckResult
        {
            Outcome = outcome,
            Message = detail,
            ReadyUpdate = _readyUpdate,
            AutoInstallTriggered = false,
            ShouldNotifyUser = shouldNotify,
        };
    }

    private async Task<UpdateInstallResult> RecordInstallFailureAsync(
        string targetVersion,
        UpdateTriggerKind trigger,
        string detail,
        CancellationToken cancellationToken)
    {
        if (trigger == UpdateTriggerKind.Auto)
        {
            _lastAutoInstallFailedVersion = targetVersion;
        }

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        await PersistCheckMetadataAsync(
            _lastCheckUtc ?? nowUtc,
            _nextCheckUtc,
            UpdateCheckOutcome.InstallFailed,
            detail,
            cancellationToken).ConfigureAwait(false);

        await AppendHistoryCoreAsync(
            new UpdateHistoryEntryDto
            {
                TimestampUtc = nowUtc,
                FromVersion = _effectiveVersion,
                ToVersion = targetVersion,
                Trigger = trigger,
                Result = UpdateHistoryResult.Failed,
                Detail = detail,
            },
            cancellationToken).ConfigureAwait(false);

        _state = _readyUpdate is not null ? UpdateServiceState.ReadyToInstall : UpdateServiceState.Error;
        _statusMessage = detail;
        PublishSnapshot();

        // Notify once per target version (no repeat-spam on subsequent failures for the same version!).
        bool shouldNotify = !string.Equals(_lastNotifiedFailureVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
        if (shouldNotify)
        {
            _lastNotifiedFailureVersion = targetVersion;
            await _database.SetSettingAsync(UpdateSettingKeys.LastNotifiedFailureVersion, targetVersion, cancellationToken)
                .ConfigureAwait(false);

            UserNotificationRequested?.Invoke(
                this,
                new UpdateUserNotificationEvent(
                    Title: "SPAWN 更新未能完成",
                    Message: $"更新至 v{targetVersion} 失败：{detail}",
                    TargetVersion: targetVersion,
                    IsError: true,
                    IsUpdateReady: false));
        }

        return new UpdateInstallResult
        {
            Succeeded = false,
            Message = detail,
            ShouldNotifyUser = shouldNotify,
            TargetVersion = targetVersion,
        };
    }

    private async Task RunBackgroundCheckLoopAsync(Task initialDelay, CancellationToken cancellationToken)
    {
        CurrentLoopContext.Value = (this, SchedulerLoopId);
        try
        {
            await initialDelay.ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_settings.AutoCheckEnabled)
                {
                    try
                    {
                        UpdateCheckResult checkResult = await CheckForUpdatesAsync(UpdateTriggerKind.Auto, cancellationToken)
                            .ConfigureAwait(false);
                        if (checkResult.AutoInstallTriggered)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Scheduled update check deferred after unexpected error.");
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                TimeSpan waitInterval = _options.CheckInterval > TimeSpan.Zero
                    ? _options.CheckInterval
                    : TimeSpan.FromHours(24);

                // Arm the next cycle timer BEFORE publishing NextCheckUtc/StateChanged so callers observing
                // the updated snapshot are guaranteed the timer is already registered on TimeProvider.
                Task nextDelay = Task.Delay(waitInterval, _timeProvider, cancellationToken);

                if (_settings.AutoCheckEnabled)
                {
                    DateTimeOffset nextUtc = _timeProvider.GetUtcNow() + waitInterval;
                    lock (_stateLock)
                    {
                        _nextCheckUtc = nextUtc;
                        _snapshot = BuildSnapshotCore();
                    }

                    RaiseStateChanged();
                    await PersistNextCheckUtcBestEffortAsync(nextUtc).ConfigureAwait(false);
                }

                await nextDelay.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            CurrentLoopContext.Value = null;
        }
    }

    private async Task RunIdleInstallLoopAsync(
        Task initialDelay,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        CurrentLoopContext.Value = (this, IdleLoopId);
        try
        {
            Task currentDelay = initialDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                await currentDelay.ConfigureAwait(false);

                PreparedUpdateDto? ready;
                UpdateSettingsDto settings;
                lock (_stateLock)
                {
                    ready = _readyUpdate;
                    settings = _settings;
                }

                if (ready is not null
                    && settings.AutoInstallWhenIdle
                    && !string.Equals(_lastAutoInstallFailedVersion, ready.Version, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        UpdateInstallResult installResult = await TryAutoInstallIfEligibleAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (installResult.Succeeded)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Idle auto-install check failed.");
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                currentDelay = Task.Delay(interval, _timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            CurrentLoopContext.Value = null;
        }
    }

    private UpdateHttpFetcher GetOrCreateFetcher()
    {
        _fetcher ??= new UpdateHttpFetcher(_options, _effectiveVersion, _httpMessageHandler);
        return _fetcher;
    }

    private async Task<UpdateSettingsDto> LoadSettingsCoreAsync(CancellationToken cancellationToken)
    {
        string? rawAutoCheck = await _database.GetSettingAsync(UpdateSettingKeys.AutoCheckEnabled, cancellationToken).ConfigureAwait(false);
        string? rawAutoInstall = await _database.GetSettingAsync(UpdateSettingKeys.AutoInstallWhenIdle, cancellationToken).ConfigureAwait(false);

        // Defaults per Q-002: AutoCheckEnabled = true, AutoInstallWhenIdle = true.
        bool autoCheck = string.IsNullOrWhiteSpace(rawAutoCheck) || (!bool.TryParse(rawAutoCheck, out bool parsedCheck) || parsedCheck);
        bool autoInstall = string.IsNullOrWhiteSpace(rawAutoInstall) || (!bool.TryParse(rawAutoInstall, out bool parsedInstall) || parsedInstall);

        return new UpdateSettingsDto
        {
            AutoCheckEnabled = autoCheck,
            AutoInstallWhenIdle = autoInstall,
        };
    }

    private async Task<DateTimeOffset?> LoadNullableUtcSettingAsync(string key, CancellationToken cancellationToken)
    {
        string? raw = await _database.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private async Task PersistCheckMetadataAsync(
        DateTimeOffset lastCheckUtc,
        DateTimeOffset? nextCheckUtc,
        UpdateCheckOutcome outcome,
        string message,
        CancellationToken cancellationToken)
    {
        _lastCheckUtc = lastCheckUtc;
        _nextCheckUtc = nextCheckUtc;
        _lastCheckOutcome = outcome;

        await _database.SetSettingAsync(
            UpdateSettingKeys.LastCheckUtc,
            lastCheckUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await _database.SetSettingAsync(
            UpdateSettingKeys.NextCheckUtc,
            nextCheckUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await _database.SetSettingAsync(
            UpdateSettingKeys.LastCheckOutcome,
            outcome.ToString(),
            cancellationToken).ConfigureAwait(false);
        await _database.SetSettingAsync(
            UpdateSettingKeys.LastCheckMessage,
            message,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistNextCheckUtcBestEffortAsync(DateTimeOffset? nextCheckUtc)
    {
        try
        {
            await _database.SetSettingAsync(
                UpdateSettingKeys.NextCheckUtc,
                nextCheckUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal metadata update.
        }
    }

    private async Task AppendHistoryCoreAsync(UpdateHistoryEntryDto entry, CancellationToken cancellationToken)
    {
        int max = Math.Max(1, _options.MaxHistoryEntries);
        _history.Insert(0, entry);
        if (_history.Count > max)
        {
            _history.RemoveRange(max, _history.Count - max);
        }

        await _database.SetSettingJsonAsync(UpdateSettingKeys.HistoryJson, _history, cancellationToken)
            .ConfigureAwait(false);
    }

    private List<UpdateHistoryEntryDto> TrimHistory(List<UpdateHistoryEntryDto>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return [];
        }

        int max = Math.Max(1, _options.MaxHistoryEntries);
        return raw.OrderByDescending(e => e.TimestampUtc).Take(max).ToList();
    }

    private bool IsPathStrictlyUnderUpdatesCache(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.UpdatesCacheDirectory));
            string full = Path.GetFullPath(candidatePath);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void PruneUpdatesCacheDirectory(string? keepVersionDirectoryName)
    {
        try
        {
            if (!Directory.Exists(_paths.UpdatesCacheDirectory))
            {
                return;
            }

            foreach (string subDir in Directory.EnumerateDirectories(_paths.UpdatesCacheDirectory))
            {
                string dirName = Path.GetFileName(subDir);
                if (!string.IsNullOrWhiteSpace(keepVersionDirectoryName)
                    && string.Equals(dirName, keepVersionDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    // Clean stray .partial files inside the kept directory as well.
                    foreach (string partial in Directory.EnumerateFiles(subDir, "*.partial"))
                    {
                        TryDeleteFileQuietly(partial);
                    }

                    continue;
                }

                try
                {
                    Directory.Delete(subDir, recursive: true);
                }
                catch
                {
                    // Best-effort cache cleanup.
                }
            }

            foreach (string strayFile in Directory.EnumerateFiles(_paths.UpdatesCacheDirectory))
            {
                TryDeleteFileQuietly(strayFile);
            }
        }
        catch
        {
            // Best-effort cache cleanup.
        }
    }

    private static async Task<bool> VerifyFileIntegrityAsync(
        string filePath,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            if (fs.Length != expectedSize)
            {
                return false;
            }

            byte[] hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexStringLower(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeBusyOrWindowReason(UpdateIdleSnapshot idle)
    {
        if (idle.HasRunningTrackedGame)
        {
            return "有受追踪游戏正在运行，暂不自动重启更新。";
        }

        if (idle.HasActiveTrainerSession)
        {
            return "存在活跃修改器会话，暂不自动重启更新。";
        }

        if (idle.HasActiveSaveSnapshotOrRollback)
        {
            return "存档快照或回滚正在进行中，暂不自动重启更新。";
        }

        if (idle.HasActiveGameDataCleanup)
        {
            return "游戏数据清理或恢复正在进行中，暂不自动重启更新。";
        }

        if (idle.IsMainWindowOpen)
        {
            return "主窗口处于打开状态，已转为提示条等待您手动重启更新。";
        }

        return "当前状态不满足静默自动安装条件。";
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort file cleanup.
        }
    }

    private void PublishSnapshot()
    {
        UpdateStateSnapshot next;
        lock (_stateLock)
        {
            next = BuildSnapshotCore();
            _snapshot = next;
        }

        StateChanged?.Invoke(this, next);
    }

    private void RaiseStateChanged()
    {
        UpdateStateSnapshot current;
        lock (_stateLock)
        {
            current = _snapshot;
        }

        StateChanged?.Invoke(this, current);
    }

    private UpdateStateSnapshot BuildSnapshotCore() =>
        new()
        {
            IsUpdateSupported = _isReleaseChannel,
            CurrentVersion = _effectiveVersion,
            Channel = _effectiveChannel,
            CommitSha = _effectiveCommitSha,
            State = _state,
            Settings = _settings,
            LastCheckUtc = _lastCheckUtc,
            NextCheckUtc = _nextCheckUtc,
            LastCheckOutcome = _lastCheckOutcome,
            StatusMessage = _statusMessage,
            ReadyUpdate = _readyUpdate,
            DownloadProgressPercent = _downloadProgressPercent,
            History = _history.ToArray(),
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopBackgroundSchedulerAsync().ConfigureAwait(false);
        List<CancellationTokenSource> toDispose;
        lock (_stateLock)
        {
            toDispose = [.. _deferredDisposedCts];
            _deferredDisposedCts.Clear();
        }

        foreach (CancellationTokenSource deferred in toDispose)
        {
            try
            {
                deferred.Dispose();
            }
            catch
            {
                // Ignore deferred CTS disposal race.
            }
        }

        _fetcher?.Dispose();
        _fetcher = null;
        _operationLock.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
