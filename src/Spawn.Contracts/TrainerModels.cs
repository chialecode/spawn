using System.Text.Json.Serialization;

namespace Spawn.Contracts;

/// <summary>
/// Zero-injection audio mute policy for external trainer processes.
/// </summary>
public sealed record TrainerAudioPolicy
{
    public TrainerAudioMode Mode { get; init; } = TrainerAudioMode.FullMute;
    public double CueVolume { get; init; } = 0.35;
    public bool EnableA0 { get; init; } = true;
}

/// <summary>
/// Global user preferences for the M3 Single-Player Trainer Manager &amp; Security Sandbox, persisted in <c>app_settings</c>.
/// </summary>
public sealed record TrainerSettingsDto
{
    public const int MinHotkeyHoldMs = 50;
    public const int MaxHotkeyHoldMs = 120;
    public const int DefaultHotkeyHoldMs = 75;
    public const int DefaultPreSnapshotRetentionCount = 5;
    public const int DefaultPreSnapshotMaxSizeMb = 256;

    public string? CustomSandboxRoot { get; init; }
    public TrainerAudioMode AudioMode { get; init; } = TrainerAudioMode.FullMute;
    public double CueVolume { get; init; } = 0.35;
    public bool EnableA0PreMute { get; init; } = true;
    public int HotkeyHoldMs { get; init; } = DefaultHotkeyHoldMs;
    public bool CreatePreTrainerSnapshot { get; init; } = true;
    public int PreTrainerSnapshotRetentionCount { get; init; } = DefaultPreSnapshotRetentionCount;
    public int PreTrainerSnapshotMaxSizeMb { get; init; } = DefaultPreSnapshotMaxSizeMb;
    public bool StopWithGameExit { get; init; } = true;
    public bool HideOriginalWindowOnLaunch { get; init; }
    public bool StrictVanguardMode { get; init; }
    public bool IncludeFlingArchiveInSearch { get; init; }
    public bool DefaultScanCodeMode { get; init; }

    public TrainerAudioPolicy ToAudioPolicy() =>
        new()
        {
            Mode = AudioMode,
            CueVolume = Math.Clamp(CueVolume, 0.0, 1.0),
            EnableA0 = EnableA0PreMute,
        };
}

/// <summary>
/// Persisted catalog record for an imported external trainer (<c>trainer_catalog</c>).
/// </summary>
public sealed record TrainerCatalogRecord
{
    public required string TrainerId { get; init; }
    public required string GameId { get; init; }
    public TrainerProviderKind Provider { get; init; } = TrainerProviderKind.LocalImport;
    public string? DisplayName { get; init; }
    public string? VersionLabel { get; init; }
    public string? SourcePageUrl { get; init; }
    public string? SourceUpdatedAt { get; init; }
    public required string SandboxRelativePath { get; init; }
    public required string FileSha256 { get; init; }
    public long? FileSize { get; init; }
    public string? PeMachine { get; init; }
    public bool RequiresAdmin { get; init; }
    public TrainerAuthenticodeStatus AuthenticodeStatus { get; init; } = TrainerAuthenticodeStatus.Unknown;
    public TrainerParseMode ParseMode { get; init; } = TrainerParseMode.FreeHosted;
    public string? BoundBuildId { get; init; }
    public long? BoundExeSize { get; init; }
    public DateTimeOffset? BoundExeLastWriteUtc { get; init; }
    public DateTimeOffset ImportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLaunchedAtUtc { get; init; }
}

/// <summary>
/// Cached canonical English title and user search override for a game (<c>trainer_title_map</c>).
/// </summary>
public sealed record TrainerTitleMapRecord
{
    public required string GameId { get; init; }
    public required string EnglishTitle { get; init; }
    public TrainerTitleSource TitleSource { get; init; } = TrainerTitleSource.Cleaner;
    public string? UserSearchOverride { get; init; }
    public string? FlingPageUrl { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Single structured or manually added hotkey binding row (<c>trainer_hotkey_bindings</c>).
/// </summary>
public sealed record TrainerHotkeyBindingRecord
{
    public required string TrainerId { get; init; }
    public required string EntryId { get; init; }
    public required string Label { get; init; }
    public string? Tooltip { get; init; }
    public required string OriginalChord { get; init; }
    public string? MappedChord { get; init; }
    public TrainerHotkeyEntryKind EntryKind { get; init; } = TrainerHotkeyEntryKind.Toggle;
    public bool ProxyState { get; init; }
    public int SortOrder { get; init; }

    [JsonIgnore]
    public string EffectiveChord => string.IsNullOrWhiteSpace(MappedChord) ? OriginalChord : MappedChord!;
}

/// <summary>
/// Lightweight pre-trainer or pre-restore save snapshot ledger row (<c>pre_trainer_snapshots</c>).
/// </summary>
public sealed record PreTrainerSnapshotRecord
{
    public required string SnapshotId { get; init; }
    public required string GameId { get; init; }
    public string? TrainerId { get; init; }
    public PreTrainerSnapshotTriggerKind TriggerKind { get; init; } = PreTrainerSnapshotTriggerKind.PreTrainer;
    public PreTrainerSnapshotStatus Status { get; init; } = PreTrainerSnapshotStatus.Completed;
    public string? SkipReason { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public string? ManifestRelativePath { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RestoredAtUtc { get; init; }
}

/// <summary>
/// Evaluation state of a hybrid single-player game's controlled offline bypass (<c>verified_offline_bypass</c>).
/// Never mutates the game's static <see cref="AntiCheatTier"/>.
/// </summary>
public sealed record VerifiedOfflineBypass
{
    public required string GameId { get; init; }
    public bool IsConfirmed { get; init; }
    public DateTimeOffset? ConfirmedAtUtc { get; init; }
    public bool IsEligible { get; init; }
    public bool WasAutoInvalidated { get; init; }
    public string? InvalidationReason { get; init; }
    public AntiCheatTier StaticAntiCheatTier { get; init; }
    public bool HasSinglePlayerMode { get; init; }
    public bool IsLiveAntiCheatClear { get; init; }
    public IReadOnlyList<string> ActiveAntiCheatIndicators { get; init; } = [];
}

/// <summary>
/// Live read-only probe result for running anti-cheat daemons and session kernel anti-cheat drivers.
/// </summary>
public sealed record LiveAntiCheatProbeReport
{
    public IReadOnlyList<string> ActiveAntiCheatProcesses { get; init; } = [];
    public IReadOnlyList<string> ActiveSessionAntiCheatDrivers { get; init; } = [];
    public bool IsVanguardClientActive { get; init; }
    public bool IsVanguardKernelIdleLoaded { get; init; }
    public DateTimeOffset ProbedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsClearForTrainer =>
        ActiveAntiCheatProcesses.Count == 0
        && ActiveSessionAntiCheatDrivers.Count == 0
        && !IsVanguardClientActive;
}

/// <summary>
/// Result of the four-layer anti-cheat classification pipeline (directory signatures depth 6, AreWeAntiCheatYet offline index,
/// Steam appdetails categories, and conservative offline fallback).
/// </summary>
public sealed record FourLayerAntiCheatReport
{
    public AntiCheatTier EffectiveTier { get; init; }
    public string? PrimarySignature { get; init; }
    public IReadOnlyList<string> MatchedIndicators { get; init; } = [];
    public IReadOnlyList<string> AreWeAntiCheatYetMatches { get; init; } = [];
    public bool? HasSinglePlayerCategory { get; init; }
    public bool HasVacCategory { get; init; }
    public bool HasMmoCategory { get; init; }
    public bool IsMultiplayerOnly { get; init; }
    public bool UsedOnlineSteamCategories { get; init; }
    public bool OfflineConservativeFallbackApplied { get; init; }
}

/// <summary>
/// Authoritative decision from <see cref="ITrainerSafetyGate"/> governing whether a trainer may be discovered, downloaded, or launched.
/// </summary>
public sealed record TrainerAllowanceDecision
{
    public required TrainerAllowanceOutcome Outcome { get; init; }
    public required bool IsAllowed { get; init; }
    public required string Message { get; init; }
    public AntiCheatTier StaticGameTier { get; init; }
    public AntiCheatGuardPhase GuardPhase { get; init; }
    public double RemainingCooldownSeconds { get; init; }
    public bool CanConfirmOfflineBypass { get; init; }
    public bool IsVanguardIdleWarning { get; init; }
    public bool UsedVerifiedOfflineBypass { get; init; }
    public IReadOnlyList<string> DetectedAntiCheatProcesses { get; init; } = [];
    public IReadOnlyList<string> DetectedAntiCheatDrivers { get; init; } = [];
}

/// <summary>
/// High-priority emergency stop event raised by the 2-second anti-cheat watchdog during an active trainer session.
/// </summary>
public sealed record TrainerEmergencyStopEvent
{
    public required string Reason { get; init; }
    public IReadOnlyList<string> DetectedProcesses { get; init; } = [];
    public IReadOnlyList<string> DetectedDrivers { get; init; } = [];
    public string? TriggeringGameId { get; init; }
    public DateTimeOffset OccurredUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Outcome of pre-launch purge for sensitive SPAWN workers prior to launching a Tier 2/3 protected game (LOOP-06).
/// </summary>
public sealed record SensitiveWorkerShutdownReport
{
    public required bool Succeeded { get; init; }
    public int TerminatedWorkerCount { get; init; }
    public int TerminatedOrphanCount { get; init; }
    public IReadOnlyList<string> RemainingSandboxProcesses { get; init; } = [];
    public TimeSpan Elapsed { get; init; }
    public string? Message { get; init; }

    public static SensitiveWorkerShutdownReport Clean { get; } = new() { Succeeded = true };
}

/// <summary>
/// Single downloadable trainer attachment row extracted from a candidate source page.
/// </summary>
public sealed record TrainerAttachmentCandidate
{
    public required string AttachmentId { get; init; }
    public required string FileName { get; init; }
    public required string DownloadUrl { get; init; }
    public string? VersionLabel { get; init; }
    public string? UpdatedDateText { get; init; }
    public long? ApproximateSizeBytes { get; init; }
    public TrainerVersionConfidence VersionConfidence { get; init; } = TrainerVersionConfidence.CheckByDate;
    public string? ConfidenceReason { get; init; }
}

/// <summary>
/// Discovered candidate trainer page returned by clean-room search.
/// </summary>
public sealed record TrainerCandidate
{
    public required string CandidateId { get; init; }
    public required TrainerProviderKind Provider { get; init; }
    public required string Title { get; init; }
    public required string PageUrl { get; init; }
    public string? VersionLabel { get; init; }
    public string? UpdatedDateText { get; init; }
    public double TitleMatchScore { get; init; }
    public TrainerVersionConfidence VersionConfidence { get; init; } = TrainerVersionConfidence.CheckByDate;
    public string? ConfidenceReason { get; init; }
    public IReadOnlyList<TrainerAttachmentCandidate> Attachments { get; init; } = [];
    public TrainerAttachmentCandidate? SelectedAttachment { get; init; }
    public IReadOnlyList<TrainerHotkeyBindingRecord> PreviewHotkeys { get; init; } = [];
}

/// <summary>
/// Result of matching a game against clean-room FLiNG discovery sources.
/// </summary>
public sealed record TrainerMatchResult
{
    public required string GameId { get; init; }
    public required string EffectiveQuery { get; init; }
    public TrainerTitleSource QuerySource { get; init; }
    public bool Succeeded { get; init; } = true;
    public bool FallbackToBrowserRecommended { get; init; }
    public string? FallbackBrowserUrl { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<TrainerCandidate> Candidates { get; init; } = [];
}

/// <summary>
/// Streaming progress tick reported while downloading a user-selected trainer file.
/// </summary>
public sealed record TrainerDownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public string StageLabel { get; init; } = string.Empty;
}

/// <summary>
/// Candidate executable discovered inside a multi-EXE archive requiring explicit user selection.
/// </summary>
public sealed record TrainerExecutableChoice
{
    public required string RelativePath { get; init; }
    public long FileSize { get; init; }
    public string? PeMachine { get; init; }
    public bool RequiresAdmin { get; init; }
}

/// <summary>
/// Outcome of downloading or locally importing a trainer through the staged verification pipeline.
/// </summary>
public sealed record TrainerImportResult
{
    public required bool Succeeded { get; init; }
    public TrainerImportFailureKind FailureKind { get; init; } = TrainerImportFailureKind.None;
    public string? Message { get; init; }
    public string? FallbackBrowserUrl { get; init; }
    public TrainerCatalogRecord? Trainer { get; init; }
    public IReadOnlyList<TrainerHotkeyBindingRecord> Hotkeys { get; init; } = [];
    public IReadOnlyList<TrainerExecutableChoice> CandidateExecutables { get; init; } = [];
    public int? Win32ErrorCode { get; init; }
}

/// <summary>
/// Pre-launch TOCTOU SHA-256 verification and game version fingerprint drift report.
/// </summary>
public sealed record TrainerIntegrityReport
{
    public required string TrainerId { get; init; }
    public required bool IsValid { get; init; }
    public bool FileExists { get; init; }
    public bool HashMatches { get; init; }
    public bool IsInsideSandbox { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? ActualSha256 { get; init; }
    public string? ResolvedExecutablePath { get; init; }
    public bool GameFilesChangedSinceBinding { get; init; }
    public string? GameUpdateNotice { get; init; }
    public int? Win32ErrorCode { get; init; }
    public string? FailureMessage { get; init; }
}

/// <summary>
/// Per-launch runtime options supplied when starting a managed trainer.
/// </summary>
public sealed record TrainerLaunchOptions
{
    public int HotkeyHoldMs { get; init; } = TrainerSettingsDto.DefaultHotkeyHoldMs;
    public bool HideOriginalWindow { get; init; }
    public bool StopWithGameExit { get; init; } = true;
    public bool CreatePreTrainerSnapshot { get; init; } = true;
    public bool ScanCodeMode { get; init; }
    public TrainerAudioPolicy? AudioPolicyOverride { get; init; }
    public bool StrictVanguardMode { get; init; }
}

/// <summary>
/// Fully validated launch specification passed from <see cref="ITrainerRuntimeService"/> into <see cref="ITrainerBackend"/>.
/// </summary>
public sealed record TrainerLaunchSpec
{
    public required string SessionId { get; init; }
    public required string GameId { get; init; }
    public required string TrainerId { get; init; }
    public required string SandboxRoot { get; init; }
    public required string SandboxRelativePath { get; init; }
    public required string ExpectedSha256 { get; init; }
    public string? ExpectedArchitecture { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool TargetGameIsElevated { get; init; }
    public required TrainerLaunchOptions Options { get; init; }
}

/// <summary>
/// Active trainer session metadata returned upon successful worker handshake and atomic job enrollment.
/// </summary>
public sealed record TrainerSessionHandle
{
    public required string SessionId { get; init; }
    public required string GameId { get; init; }
    public required string TrainerId { get; init; }
    public required string BackendKind { get; init; }
    public TrainerElevationLevel ElevationLevel { get; init; }
    public int WorkerProcessId { get; init; }
    public int TrainerProcessId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Complete outcome of <see cref="ITrainerRuntimeService.LaunchAsync"/>.
/// </summary>
public sealed record TrainerLaunchOutcome
{
    public required bool Succeeded { get; init; }
    public TrainerLaunchFailureKind FailureKind { get; init; } = TrainerLaunchFailureKind.None;
    public string? Message { get; init; }
    public TrainerSessionHandle? Session { get; init; }
    public TrainerAllowanceDecision? AllowanceDecision { get; init; }
    public PreTrainerSnapshotResult? PreSnapshot { get; init; }
    public TrainerIntegrityReport? IntegrityReport { get; init; }
    public int? Win32ErrorCode { get; init; }
}

/// <summary>
/// State transition notification emitted by <see cref="ITrainerRuntimeService"/>.
/// </summary>
public sealed record TrainerRuntimeStateChanged
{
    public required string GameId { get; init; }
    public string? TrainerId { get; init; }
    public string? SessionId { get; init; }
    public TrainerRuntimeState PreviousState { get; init; }
    public TrainerRuntimeState CurrentState { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset OccurredUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of creating a lightweight pre-trainer save snapshot (non-blocking: Skipped/Failed never blocks launch).
/// </summary>
public sealed record PreTrainerSnapshotResult
{
    public required bool Succeeded { get; init; }
    public required PreTrainerSnapshotStatus Status { get; init; }
    public PreTrainerSnapshotSkipReason SkipReason { get; init; } = PreTrainerSnapshotSkipReason.None;
    public string? Message { get; init; }
    public PreTrainerSnapshotRecord? Snapshot { get; init; }
}

/// <summary>
/// Result of restoring a pre-trainer save snapshot (after creating an automatic <c>PreRestoreSafety</c> backup).
/// </summary>
public sealed record PreTrainerRestoreResult
{
    public required bool Succeeded { get; init; }
    public string? Message { get; init; }
    public PreTrainerSnapshotRecord? SafetySnapshot { get; init; }
    public int RestoredFileCount { get; init; }
    public int RestoredRegistryKeyCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Inspection or opt-in addition report for Windows Defender single-folder sandbox exclusion and Smart App Control guidance.
/// </summary>
public sealed record DefenderExclusionStatusReport
{
    public required DefenderExclusionStatus Status { get; init; }
    public required string SandboxPath { get; init; }
    public bool IsExcluded { get; init; }
    public bool IsTamperProtectionEnabled { get; init; }
    public bool IsDefenderPassiveMode { get; init; }
    public bool IsSmartAppControlEnforced { get; init; }
    public required string Message { get; init; }
    public string? GuidanceUrl { get; init; }
}
