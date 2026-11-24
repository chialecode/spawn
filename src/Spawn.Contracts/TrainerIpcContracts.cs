namespace Spawn.Contracts;

/// <summary>
/// Frozen JSON-RPC method and notification names for the inverted cross-IL <c>Spawn.Host</c> ↔ <c>Spawn.TrainerWorker</c> Named Pipe protocol.
/// </summary>
public static class TrainerIpcMethods
{
    public const int ProtocolVersion = 1;

    /// <summary>Worker → Host: first-frame mutual authentication carrying 256-bit single-use LaunchNonce.</summary>
    public const string Handshake = "trainer.handshake";

    /// <summary>Host → Worker: verify sandbox path + pinned SHA-256 and atomically spawn inside KILL_ON_JOB_CLOSE job.</summary>
    public const string Launch = "trainer.launch";

    /// <summary>Host → Worker: inject staggered MAPVK_VK_TO_VSC_EX hotkey sequence via SendInput.</summary>
    public const string TriggerHotkey = "trainer.triggerHotkey";

    /// <summary>Host → Worker: show or hide the external trainer's top-level window via ShowWindow.</summary>
    public const string SetWindowVisibility = "trainer.setWindowVisibility";

    /// <summary>Host → Worker: WASAPI mute/volume fallback when Host Medium IL receives E_ACCESSDENIED on High IL sessions.</summary>
    public const string SetAudioFallback = "trainer.setAudioFallback";

    /// <summary>Host → High IL Worker: add single-folder Defender sandbox exclusion via WMI MSFT_MpPreference.Add and verify by readback.</summary>
    public const string AddDefenderExclusion = "trainer.addDefenderExclusion";

    /// <summary>Host → Worker: graceful WM_CLOSE (1500ms) followed by TerminateJobObject.</summary>
    public const string Stop = "trainer.stop";

    /// <summary>Host → Worker: emergency WM_CLOSE (500ms) followed by immediate TerminateJobObject on anti-cheat trip.</summary>
    public const string EmergencyShutdown = "trainer.emergencyShutdown";

    /// <summary>Worker → Host (notification): IOCP JOB_OBJECT_MSG_NEW_PROCESS child PID report for WASAPI muting.</summary>
    public const string ProcessCreated = "trainer.processCreated";

    /// <summary>Worker → Host (notification): IOCP JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO or shutdown completion.</summary>
    public const string SessionEnded = "trainer.sessionEnded";
}

/// <summary>
/// Worker → Host first-frame authentication payload (<see cref="TrainerIpcMethods.Handshake"/>).
/// </summary>
public sealed record TrainerHandshakeRequest
{
    public required string LaunchNonce { get; init; }
    public int WorkerPid { get; init; }
    public int WorkerIntegrityLevel { get; init; }
    public int ProtocolVersion { get; init; } = TrainerIpcMethods.ProtocolVersion;
    public string? WorkerExecutablePath { get; init; }
}

/// <summary>
/// Host → Worker handshake reply (<see cref="TrainerIpcMethods.Handshake"/>).
/// </summary>
public sealed record TrainerHandshakeResponse
{
    public bool Accepted { get; init; }
    public int HostPid { get; init; }
    public int ProtocolVersion { get; init; } = TrainerIpcMethods.ProtocolVersion;
    public string? RejectReason { get; init; }
}

/// <summary>
/// Host → Worker request to verify and launch a sandboxed trainer inside <c>TrainerSandboxJob</c> (<see cref="TrainerIpcMethods.Launch"/>).
/// </summary>
public sealed record TrainerLaunchRequest
{
    public required string SessionId { get; init; }
    public required string SandboxRoot { get; init; }
    public required string SandboxRelativePath { get; init; }
    public required string Sha256 { get; init; }
    public string? WorkingDirectory { get; init; }
    public int HotkeyHoldMs { get; init; } = TrainerSettingsDto.DefaultHotkeyHoldMs;
    public string? ExpectedArchitecture { get; init; }
    public bool HideOriginalWindow { get; init; }
    public bool ScanCodeMode { get; init; }
    public bool StrictVanguardMode { get; init; }
    public string? GuardStateFilePath { get; init; }
}

/// <summary>
/// Worker → Host reply to <see cref="TrainerIpcMethods.Launch"/>.
/// </summary>
public sealed record TrainerLaunchResponse
{
    public bool Succeeded { get; init; }
    public int TrainerPid { get; init; }
    public int? Win32ErrorCode { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Host → Worker request to synthesize a hotkey chord via <c>SendInput</c> (<see cref="TrainerIpcMethods.TriggerHotkey"/>).
/// </summary>
public sealed record TrainerTriggerHotkeyRequest
{
    public required string SessionId { get; init; }
    public required string EntryId { get; init; }
    public ushort VirtualKey { get; init; }
    public IReadOnlyList<ushort> Modifiers { get; init; } = [];
    public bool IsExtendedKeyOverride { get; init; }
    public int? HoldMsOverride { get; init; }
    public bool ScanCodeMode { get; init; }
}

/// <summary>
/// Worker → Host reply to <see cref="TrainerIpcMethods.TriggerHotkey"/>.
/// </summary>
public sealed record TrainerTriggerHotkeyResponse
{
    public bool Succeeded { get; init; }
    public int EventsInjected { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Host → Worker request to show or hide the external trainer's original window (<see cref="TrainerIpcMethods.SetWindowVisibility"/>).
/// </summary>
public sealed record TrainerSetWindowVisibilityRequest
{
    public required string SessionId { get; init; }
    public bool Visible { get; init; }
}

/// <summary>
/// Worker → Host reply to <see cref="TrainerIpcMethods.SetWindowVisibility"/>.
/// </summary>
public sealed record TrainerSetWindowVisibilityResponse
{
    public bool Succeeded { get; init; }
    public int AffectedWindowCount { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Host → Worker fallback request to mute/adjust WASAPI audio sessions from the Worker's integrity level (<see cref="TrainerIpcMethods.SetAudioFallback"/>).
/// </summary>
public sealed record TrainerSetAudioFallbackRequest
{
    public string? SessionId { get; init; }
    public IReadOnlyList<int> Pids { get; init; } = [];
    public bool Mute { get; init; } = true;
    public double Volume { get; init; }
}

/// <summary>
/// Worker → Host reply to <see cref="TrainerIpcMethods.SetAudioFallback"/>.
/// </summary>
public sealed record TrainerSetAudioFallbackResponse
{
    public bool Succeeded { get; init; }
    public int AffectedSessionCount { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Host → High IL Worker request to add a single-folder Windows Defender sandbox exclusion via WMI (<see cref="TrainerIpcMethods.AddDefenderExclusion"/>).
/// </summary>
public sealed record TrainerAddDefenderExclusionRequest
{
    public required string SandboxPath { get; init; }
}

/// <summary>
/// High IL Worker → Host reply to <see cref="TrainerIpcMethods.AddDefenderExclusion"/>.
/// </summary>
public sealed record TrainerAddDefenderExclusionResponse
{
    public bool Succeeded { get; init; }
    public bool VerifiedByReadback { get; init; }
    public DefenderExclusionStatus Status { get; init; }
    public int? WmiReturnValue { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Host → Worker graceful stop request (<see cref="TrainerIpcMethods.Stop"/>).
/// </summary>
public sealed record TrainerStopRequest
{
    public required string SessionId { get; init; }
    public int GracefulTimeoutMs { get; init; } = 1500;
    public TrainerShutdownReason Reason { get; init; } = TrainerShutdownReason.UserRequested;
}

/// <summary>
/// Host → Worker emergency shutdown request triggered by anti-cheat guard or watchdog (<see cref="TrainerIpcMethods.EmergencyShutdown"/>).
/// </summary>
public sealed record TrainerEmergencyShutdownRequest
{
    public string? SessionId { get; init; }
    public int GracefulTimeoutMs { get; init; } = 500;
    public TrainerShutdownReason Reason { get; init; } = TrainerShutdownReason.AntiCheatWatchdogEmergency;
    public string? Detail { get; init; }
}

/// <summary>
/// Worker → Host reply to <see cref="TrainerIpcMethods.Stop"/> or <see cref="TrainerIpcMethods.EmergencyShutdown"/>.
/// </summary>
public sealed record TrainerStopResponse
{
    public bool Succeeded { get; init; }
    public bool TerminatedForcefully { get; init; }
    public int TerminatedProcessCount { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Worker → Host notification pushed whenever IOCP observes <c>JOB_OBJECT_MSG_NEW_PROCESS</c> (<see cref="TrainerIpcMethods.ProcessCreated"/>).
/// </summary>
public sealed record TrainerProcessCreatedNotification
{
    public required string SessionId { get; init; }
    public int ProcessId { get; init; }
    public string? ImagePath { get; init; }
    public DateTimeOffset OccurredUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Worker → Host notification pushed when <c>JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO</c> signals session termination (<see cref="TrainerIpcMethods.SessionEnded"/>).
/// </summary>
public sealed record TrainerSessionEndedNotification
{
    public required string SessionId { get; init; }
    public TrainerShutdownReason Reason { get; init; }
    public int? ExitCode { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset OccurredUtc { get; init; } = DateTimeOffset.UtcNow;
}
