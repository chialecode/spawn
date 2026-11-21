using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;
using Spawn.Core.Ipc;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Spawn.TrainerWorker;

/// <summary>
/// Parsed command-line arguments for <c>Spawn.TrainerWorker.exe</c>.
/// </summary>
internal sealed record TrainerWorkerCliOptions(
    int HostPid,
    string PipeName,
    string LaunchNonce,
    string? ExpectedHostExecutablePath = null,
    bool SkipLiveAntiCheatProbeForTest = false);

/// <summary>
/// Main isolated supervisor loop for <c>Spawn.TrainerWorker</c>:
/// <list type="number">
/// <item><description>Opens parent Host handle with <c>PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE</c> and watches for Host exit;</description></item>
/// <item><description>Connects via inverted <see cref="TrainerPipeClient"/> verifying <c>GetNamedPipeServerProcessId == --host-pid</c> + <c>0x1000</c> path + 256-bit <c>LaunchNonce</c>;</description></item>
/// <item><description>Serves whitelisted <c>trainer.*</c> JSON-RPC commands (<c>launch</c>, <c>triggerHotkey</c>, <c>setWindowVisibility</c>, <c>setAudioFallback</c>, <c>addDefenderExclusion</c>, <c>stop</c>, <c>emergencyShutdown</c>);</description></item>
/// <item><description>Self-destructs immediately on Host exit or pipe disconnection, letting kernel <c>KILL_ON_JOB_CLOSE</c> purge all child/grandchild trainer processes.</description></item>
/// </list>
/// </summary>
internal sealed class TrainerWorkerApplication
{
    private static readonly JsonSerializerOptions GuardStateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
    };

    private readonly TrainerWorkerCliOptions _cliOptions;
    private readonly TrainerHotkeyProxy _hotkeyProxy;
    private readonly ITrainerWindowInterop _windowInterop;
    private readonly IWorkerAudioFallbackExecutor _audioFallbackExecutor;
    private readonly IDefenderWmiExecutor _defenderWmiExecutor;
    private readonly ITrainerPipePeerInspector _peerInspector;
    private readonly Func<int> _integrityLevelProvider;
    private readonly IAntiCheatSystemProbe _antiCheatProbe;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _sessionGate = new();
    private TrainerSandboxJob? _activeJob;
    private string? _activeSessionId;
    private int _activeHoldMs = TrainerSettingsDto.DefaultHotkeyHoldMs;
    private int _sessionEndedSent;

    public TrainerWorkerApplication(
        TrainerWorkerCliOptions cliOptions,
        TrainerHotkeyProxy? hotkeyProxy = null,
        ITrainerWindowInterop? windowInterop = null,
        IWorkerAudioFallbackExecutor? audioFallbackExecutor = null,
        IDefenderWmiExecutor? defenderWmiExecutor = null,
        ITrainerPipePeerInspector? peerInspector = null,
        Func<int>? integrityLevelProvider = null,
        IAntiCheatSystemProbe? antiCheatProbe = null,
        TimeProvider? timeProvider = null)
    {
        _cliOptions = cliOptions ?? throw new ArgumentNullException(nameof(cliOptions));
        _hotkeyProxy = hotkeyProxy ?? new TrainerHotkeyProxy();
        _windowInterop = windowInterop ?? Win32TrainerWindowInterop.Instance;
        _audioFallbackExecutor = audioFallbackExecutor ?? WasapiWorkerAudioFallbackExecutor.Instance;
        _defenderWmiExecutor = defenderWmiExecutor ?? ComDefenderWmiExecutor.Instance;
        _peerInspector = peerInspector ?? Win32TrainerPipePeerInspector.Instance;
        _integrityLevelProvider = integrityLevelProvider ?? WorkerIntegrityHelper.GetCurrentProcessIntegrityLevel;
        _antiCheatProbe = antiCheatProbe ?? DefaultAntiCheatSystemProbe.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static bool TryParseArguments(string[] args, out TrainerWorkerCliOptions? options)
    {
        options = null;
        if (args is null || args.Length == 0)
        {
            return false;
        }

        int hostPid = 0;
        string? pipeName = null;
        string? nonce = null;
        string? expectedHostPath = null;
        bool skipLiveAntiCheatProbe = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--host-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _ = int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out hostPid);
            }
            else if (arg.Equals("--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pipeName = args[++i];
            }
            else if (arg.Equals("--nonce", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                nonce = args[++i];
            }
            else if (arg.Equals("--test-expected-host-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                expectedHostPath = args[++i];
            }
            else if (arg.Equals("--test-skip-live-ac-probe", StringComparison.OrdinalIgnoreCase))
            {
                skipLiveAntiCheatProbe = true;
            }
        }

        if (hostPid <= 4 || string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        string trimmedPipeName = pipeName.Trim();

        // Security invariant: --test-expected-host-path and --test-skip-live-ac-probe are strictly rejected in
        // elevated (High IL) processes, in packaged dev/release binaries (compile-time SpawnChannel metadata,
        // immune to SPAWN_CHANNEL env var), and for non-test pipes.
        bool isTestOverridePermitted = IsCompiledForSourceChannel()
            && !WorkerIntegrityHelper.IsCurrentProcessElevated()
            && trimmedPipeName.StartsWith("spawn-test-ipc-", StringComparison.OrdinalIgnoreCase);

        string? safeExpectedHostPath = !string.IsNullOrWhiteSpace(expectedHostPath) && isTestOverridePermitted
            ? expectedHostPath
            : null;
        bool safeSkipLiveAntiCheatProbe = skipLiveAntiCheatProbe && isTestOverridePermitted;

        options = new TrainerWorkerCliOptions(
            hostPid,
            trimmedPipeName,
            nonce.Trim(),
            safeExpectedHostPath,
            safeSkipLiveAntiCheatProbe);
        return true;
    }

    private static bool IsCompiledForSourceChannel()
    {
        foreach (AssemblyMetadataAttribute attr in typeof(TrainerWorkerApplication).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, "SpawnChannel", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(attr.Value, "source", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        // Open parent Host process handle strictly with PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE.
        nint hostHandle = SafeProcessInspector.TryOpenSynchronizeHandle(_cliOptions.HostPid);
        if (hostHandle == nint.Zero)
        {
            return 2;
        }

        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exitCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var hostWatchThread = new Thread(() =>
        {
            try
            {
                // Poll in short slices so the thread exits promptly on normal shutdown without leaking WaitForSingleObject.
                while (!shutdownCts.IsCancellationRequested)
                {
                    uint waitResult = (uint)PInvoke.WaitForSingleObject(new HANDLE(hostHandle), 200);
                    if (waitResult == 0) // WAIT_OBJECT_0 -> Parent Host exited!
                    {
                        TerminateActiveJobImmediate();
                        shutdownCts.Cancel();
                        exitCompletion.TrySetResult(3);
                        break;
                    }
                }
            }
            catch
            {
                TerminateActiveJobImmediate();
                exitCompletion.TrySetResult(3);
            }
        })
        {
            Name = "Spawn.TrainerWorker.HostWatch",
            IsBackground = true,
        };
        hostWatchThread.Start();

        int integrityLevel = _integrityLevelProvider();
        IReadOnlyList<string> allowedHostPaths = !string.IsNullOrWhiteSpace(_cliOptions.ExpectedHostExecutablePath)
            ? [_cliOptions.ExpectedHostExecutablePath!]
            : TrainerPipePathResolver.ResolveDefaultAllowedHostExecutablePaths();

        await using var client = new TrainerPipeClient(new TrainerPipeClientOptions
        {
            PipeName = _cliOptions.PipeName,
            ExpectedHostPid = _cliOptions.HostPid,
            LaunchNonce = _cliOptions.LaunchNonce,
            WorkerIntegrityLevel = integrityLevel,
            ExpectedHostExecutablePaths = allowedHostPaths,
            PeerInspector = _peerInspector,
        });

        RegisterCommandHandlers(client, shutdownCts, exitCompletion);

        client.Disconnected += () =>
        {
            TerminateActiveJobImmediate();
            try
            {
                shutdownCts.Cancel();
            }
            catch
            {
                // Ignore cancel race.
            }

            exitCompletion.TrySetResult(0);
        };

        try
        {
            await client.ConnectAndAuthenticateAsync(TimeSpan.FromSeconds(5), shutdownCts.Token).ConfigureAwait(false);
            using var reg = shutdownCts.Token.Register(() => exitCompletion.TrySetResult(0));
            return await exitCompletion.Task.ConfigureAwait(false);
        }
        catch (TrainerPipeAuthenticationException)
        {
            return 4;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch
        {
            return 5;
        }
        finally
        {
            TerminateActiveJobImmediate();
            try
            {
                shutdownCts.Cancel();
            }
            catch
            {
                // Ignore cancel race.
            }

            if (hostWatchThread.IsAlive)
            {
                _ = hostWatchThread.Join(TimeSpan.FromMilliseconds(500));
            }

            SafeProcessInspector.CloseSafeHandle(hostHandle);
        }
    }

    private void RegisterCommandHandlers(
        TrainerPipeClient client,
        CancellationTokenSource shutdownCts,
        TaskCompletionSource<int> exitCompletion)
    {
        client.RegisterHandler<TrainerLaunchRequest, TrainerLaunchResponse>(
            TrainerIpcMethods.Launch,
            (request, ct) => HandleLaunchAsync(client, request, ct));

        client.RegisterHandler<TrainerTriggerHotkeyRequest, TrainerTriggerHotkeyResponse>(
            TrainerIpcMethods.TriggerHotkey,
            (request, ct) => Task.FromResult(HandleTriggerHotkey(request, ct)));

        client.RegisterHandler<TrainerSetWindowVisibilityRequest, TrainerSetWindowVisibilityResponse>(
            TrainerIpcMethods.SetWindowVisibility,
            (request, _) => Task.FromResult(HandleSetWindowVisibility(request)));

        client.RegisterHandler<TrainerSetAudioFallbackRequest, TrainerSetAudioFallbackResponse>(
            TrainerIpcMethods.SetAudioFallback,
            (request, ct) => HandleSetAudioFallbackAsync(request, ct));

        client.RegisterHandler<TrainerAddDefenderExclusionRequest, TrainerAddDefenderExclusionResponse>(
            TrainerIpcMethods.AddDefenderExclusion,
            (request, ct) => HandleAddDefenderExclusionAsync(request, ct));

        client.RegisterHandler<TrainerStopRequest, TrainerStopResponse>(
            TrainerIpcMethods.Stop,
            (request, ct) => HandleStopAsync(
                client,
                request?.SessionId,
                request?.GracefulTimeoutMs ?? 1500,
                request?.Reason ?? TrainerShutdownReason.UserRequested,
                shutdownCts,
                exitCompletion,
                ct));

        client.RegisterHandler<TrainerEmergencyShutdownRequest, TrainerStopResponse>(
            TrainerIpcMethods.EmergencyShutdown,
            (request, ct) => HandleStopAsync(
                client,
                request?.SessionId,
                request?.GracefulTimeoutMs ?? 500,
                request?.Reason ?? TrainerShutdownReason.AntiCheatWatchdogEmergency,
                shutdownCts,
                exitCompletion,
                ct));
    }

    private Task<TrainerLaunchResponse> HandleLaunchAsync(
        TrainerPipeClient client,
        TrainerLaunchRequest? request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null)
        {
            return Task.FromResult(new TrainerLaunchResponse
            {
                Succeeded = false,
                ErrorCode = "INVALID_REQUEST",
                ErrorMessage = "Launch request payload was null.",
            });
        }

        ValidatedTrainerLaunchTarget validated = TrainerLaunchValidator.Validate(request);
        if (!validated.Succeeded)
        {
            return Task.FromResult(new TrainerLaunchResponse
            {
                Succeeded = false,
                ErrorCode = validated.ErrorCode,
                ErrorMessage = validated.ErrorMessage,
            });
        }

        // Hold a deny-write handle on the verified image until CreateProcessW has mapped it (TOCTOU guard).
        using FileStream? imageLock = TrainerLaunchValidator.OpenVerifiedImageLock(validated.ExecutableFullPath!, request.Sha256);
        if (imageLock is null)
        {
            return Task.FromResult(new TrainerLaunchResponse
            {
                Succeeded = false,
                ErrorCode = "SHA256_MISMATCH",
                ErrorMessage = "Trainer image changed or could not be locked after SHA-256 verification; launch refused.",
            });
        }

        // Independent Worker-side defense-in-depth safety gate (ipc-and-security-contracts.md §5.3(1)):
        // Never trust Host alone; re-verify persisted anti-cheat guard lock and live AC processes/drivers before spawning.
        TrainerLaunchResponse? safetyRejection = EvaluateWorkerSafetyGate(request);
        if (safetyRejection is not null)
        {
            return Task.FromResult(safetyRejection);
        }

        TrainerSandboxJob job;
        lock (_sessionGate)
        {
            if (_activeJob is not null && !_activeJob.IsDisposed)
            {
                return Task.FromResult(new TrainerLaunchResponse
                {
                    Succeeded = false,
                    ErrorCode = "ALREADY_RUNNING",
                    ErrorMessage = "Worker already hosts an active trainer sandbox job.",
                });
            }

            job = new TrainerSandboxJob();
            _activeJob = job;
            _activeSessionId = request.SessionId;
            _activeHoldMs = Math.Clamp(request.HotkeyHoldMs, TrainerSettingsDto.MinHotkeyHoldMs, TrainerSettingsDto.MaxHotkeyHoldMs);
            Interlocked.Exchange(ref _sessionEndedSent, 0);
        }

        string sessionId = request.SessionId;
        job.ProcessCreated += (pid, imagePath) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (client.IsConnected)
                    {
                        await client.NotifyAsync(
                            TrainerIpcMethods.ProcessCreated,
                            new TrainerProcessCreatedNotification
                            {
                                SessionId = sessionId,
                                ProcessId = pid,
                                ImagePath = imagePath,
                                OccurredUtc = DateTimeOffset.UtcNow,
                            },
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignore notification errors during teardown.
                }
            });
        };

        job.ActiveProcessZero += exitCode =>
        {
            if (Interlocked.Exchange(ref _sessionEndedSent, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (client.IsConnected)
                    {
                        await client.NotifyAsync(
                            TrainerIpcMethods.SessionEnded,
                            new TrainerSessionEndedNotification
                            {
                                SessionId = sessionId,
                                Reason = TrainerShutdownReason.TrainerExitedNaturally,
                                ExitCode = exitCode,
                                OccurredUtc = DateTimeOffset.UtcNow,
                            },
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignore notification errors during teardown.
                }
            });
        };

        SandboxLaunchResult launchResult = job.LaunchAtomically(
            validated.ExecutableFullPath!,
            validated.WorkingDirectory!,
            request.HideOriginalWindow);

        if (!launchResult.Succeeded)
        {
            lock (_sessionGate)
            {
                _activeJob = null;
                _activeSessionId = null;
            }

            job.Dispose();
            return Task.FromResult(new TrainerLaunchResponse
            {
                Succeeded = false,
                Win32ErrorCode = launchResult.Win32ErrorCode == 0 ? null : launchResult.Win32ErrorCode,
                ErrorCode = launchResult.Win32ErrorCode is 225 or 226
                    ? "ANTIVIRUS_QUARANTINED"
                    : launchResult.Win32ErrorCode == 4551
                        ? "SMART_APP_CONTROL_BLOCKED"
                        : "CREATE_PROCESS_FAILED",
                ErrorMessage = launchResult.ErrorMessage,
            });
        }

        return Task.FromResult(new TrainerLaunchResponse
        {
            Succeeded = true,
            TrainerPid = launchResult.RootProcessId,
        });
    }

    private TrainerTriggerHotkeyResponse HandleTriggerHotkey(
        TrainerTriggerHotkeyRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return new TrainerTriggerHotkeyResponse
            {
                Succeeded = false,
                EventsInjected = 0,
                ErrorMessage = "TriggerHotkey request payload was null.",
            };
        }

        int holdMs;
        lock (_sessionGate)
        {
            holdMs = _activeHoldMs;
        }

        return _hotkeyProxy.Trigger(request, holdMs, cancellationToken);
    }

    private TrainerSetWindowVisibilityResponse HandleSetWindowVisibility(
        TrainerSetWindowVisibilityRequest? request)
    {
        if (request is null)
        {
            return new TrainerSetWindowVisibilityResponse
            {
                Succeeded = false,
                ErrorMessage = "SetWindowVisibility request payload was null.",
            };
        }

        TrainerSandboxJob? job;
        lock (_sessionGate)
        {
            job = _activeJob;
        }

        if (job is null || job.IsDisposed)
        {
            return new TrainerSetWindowVisibilityResponse
            {
                Succeeded = false,
                ErrorMessage = "No active trainer session.",
            };
        }

        int affected = job.SetWindowVisibility(request.Visible, _windowInterop);
        return new TrainerSetWindowVisibilityResponse
        {
            Succeeded = true,
            AffectedWindowCount = affected,
        };
    }

    private Task<TrainerSetAudioFallbackResponse> HandleSetAudioFallbackAsync(
        TrainerSetAudioFallbackRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(new TrainerSetAudioFallbackResponse
            {
                Succeeded = false,
                ErrorMessage = "SetAudioFallback request payload was null.",
            });
        }

        return _audioFallbackExecutor.ApplyAsync(request, cancellationToken);
    }

    private Task<TrainerAddDefenderExclusionResponse> HandleAddDefenderExclusionAsync(
        TrainerAddDefenderExclusionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SandboxPath))
        {
            return Task.FromResult(new TrainerAddDefenderExclusionResponse
            {
                Succeeded = false,
                VerifiedByReadback = false,
                Status = DefenderExclusionStatus.Failed,
                ErrorMessage = "SandboxPath must not be empty.",
            });
        }

        return _defenderWmiExecutor.AddAndVerifyExclusionAsync(request.SandboxPath, cancellationToken);
    }

    private async Task<TrainerStopResponse> HandleStopAsync(
        TrainerPipeClient client,
        string? requestedSessionId,
        int gracefulTimeoutMs,
        TrainerShutdownReason reason,
        CancellationTokenSource shutdownCts,
        TaskCompletionSource<int> exitCompletion,
        CancellationToken cancellationToken)
    {
        TrainerSandboxJob? job;
        string? sessionId;
        lock (_sessionGate)
        {
            job = _activeJob;
            sessionId = _activeSessionId ?? requestedSessionId;
            _activeJob = null;
            _activeSessionId = null;
        }

        SandboxStopResult stopResult = new(Succeeded: true, TerminatedForcefully: false, TerminatedProcessCount: 0);
        if (job is not null)
        {
            Interlocked.Exchange(ref _sessionEndedSent, 1);
            stopResult = await job.StopAsync(Math.Max(0, gracefulTimeoutMs), _windowInterop, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(sessionId) && client.IsConnected)
        {
            try
            {
                await client.NotifyAsync(
                    TrainerIpcMethods.SessionEnded,
                    new TrainerSessionEndedNotification
                    {
                        SessionId = sessionId,
                        Reason = reason,
                        OccurredUtc = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore notification race during stop.
            }
        }

        // Schedule graceful worker exit shortly after the RPC reply frame flushes.
        _ = Task.Run(
            async () =>
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    shutdownCts.Cancel();
                }
                catch
                {
                    // Ignore cancel race.
                }

                exitCompletion.TrySetResult(0);
            },
            CancellationToken.None);

        return new TrainerStopResponse
        {
            Succeeded = stopResult.Succeeded,
            TerminatedForcefully = stopResult.TerminatedForcefully,
            TerminatedProcessCount = stopResult.TerminatedProcessCount,
        };
    }

    private TrainerLaunchResponse? EvaluateWorkerSafetyGate(TrainerLaunchRequest request)
    {
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();

        // 1. Persisted guard-lock file fail-closed check (always enforced, even when live probe test flag is set).
        if (!string.IsNullOrWhiteSpace(request.GuardStateFilePath) && File.Exists(request.GuardStateFilePath))
        {
            try
            {
                string json = File.ReadAllText(request.GuardStateFilePath);
                WorkerPersistedGuardState? persisted = JsonSerializer.Deserialize<WorkerPersistedGuardState>(json, GuardStateJsonOptions);
                if (persisted is null || !Enum.IsDefined(persisted.Phase))
                {
                    return new TrainerLaunchResponse
                    {
                        Succeeded = false,
                        ErrorCode = "GUARD_LOCKED",
                        ErrorMessage = "Worker 独立安全验票失败：反作弊守卫锁状态文件损坏，按安全闭锁原则拒绝启动。",
                    };
                }

                if (persisted.Phase is AntiCheatGuardPhase.ProtectedRunning or AntiCheatGuardPhase.ResidualGuardWatch)
                {
                    return new TrainerLaunchResponse
                    {
                        Succeeded = false,
                        ErrorCode = "GUARD_LOCKED",
                        ErrorMessage = $"Worker 独立安全验票拒绝启动：反作弊守卫当前处于 {persisted.Phase} 锁定状态。",
                    };
                }

                if (persisted.Phase == AntiCheatGuardPhase.CooldownLocked)
                {
                    if (!persisted.CooldownEndsAtUtc.HasValue || persisted.CooldownEndsAtUtc.Value > nowUtc)
                    {
                        double remaining = persisted.CooldownEndsAtUtc.HasValue
                            ? Math.Max(0.1, (persisted.CooldownEndsAtUtc.Value - nowUtc).TotalSeconds)
                            : 15.0;
                        return new TrainerLaunchResponse
                        {
                            Succeeded = false,
                            ErrorCode = "GUARD_LOCKED",
                            ErrorMessage = string.Create(
                                CultureInfo.InvariantCulture,
                                $"Worker 独立安全验票拒绝启动：反作弊 15 秒余温安全冷却尚未结束（剩余约 {remaining:0.0}s）。"),
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new TrainerLaunchResponse
                {
                    Succeeded = false,
                    ErrorCode = "GUARD_LOCKED",
                    ErrorMessage = $"Worker 独立安全验票失败：无法读取或解析反作弊守卫锁文件（{ex.Message}），已安全闭锁拒绝启动。",
                };
            }
        }

        // 2. Live OS anti-cheat processes, session kernel drivers, vgc.exe, and optional strict vgk.sys check.
        if (_cliOptions.SkipLiveAntiCheatProbeForTest)
        {
            return null;
        }

        LiveAntiCheatProbeReport liveReport = AntiCheatLiveEvaluator.Evaluate(
            _antiCheatProbe,
            nowUtc,
            resolveFullPaths: false,
            out _);

        if (!liveReport.IsClearForTrainer)
        {
            string[] indicators = liveReport.ActiveAntiCheatProcesses
                .Concat(liveReport.ActiveSessionAntiCheatDrivers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string detail = indicators.Length > 0 ? string.Join(", ", indicators) : "ActiveAntiCheat";
            return new TrainerLaunchResponse
            {
                Succeeded = false,
                ErrorCode = "ANTI_CHEAT_BLOCKED",
                ErrorMessage = $"Worker 独立反作弊硬验票拒绝启动：检测到活跃反作弊进程或内核驱动（{detail}）。",
            };
        }

        if (request.StrictVanguardMode && liveReport.IsVanguardKernelIdleLoaded)
        {
            return new TrainerLaunchResponse
            {
                Succeeded = false,
                ErrorCode = "ANTI_CHEAT_BLOCKED",
                ErrorMessage = "Worker 独立反作弊硬验票拒绝启动：严格 Vanguard 模式下检测到 vgk.sys 内核驱动处于加载状态。",
            };
        }

        return null;
    }

    private sealed class WorkerPersistedGuardState
    {
        public AntiCheatGuardPhase Phase { get; init; }
        public string? ActiveProtectedGameId { get; init; }
        public DateTimeOffset? CooldownEndsAtUtc { get; init; }
        public DateTimeOffset WrittenAtUtc { get; init; }
    }

    private void TerminateActiveJobImmediate()
    {
        TrainerSandboxJob? job;
        lock (_sessionGate)
        {
            job = _activeJob;
            _activeJob = null;
            _activeSessionId = null;
        }

        if (job is not null)
        {
            job.TerminateNow();
            job.Dispose();
        }
    }
}
