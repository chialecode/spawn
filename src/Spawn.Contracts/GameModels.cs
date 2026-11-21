using System.Text.Json.Serialization;

namespace Spawn.Contracts;

/// <summary>
/// Canonical game library entity persisted in SQLite and shared across SPAWN modules.
/// </summary>
public sealed record GameRecord
{
    public required string GameId { get; init; }
    public required string Title { get; init; }
    public string SortTitle { get; init; } = string.Empty;
    public required string InstallDir { get; init; }
    public required string ExecutableRelPath { get; init; }
    public PlatformSource PlatformSource { get; init; } = PlatformSource.Standalone;
    public AntiCheatTier AntiCheatTier { get; init; } = AntiCheatTier.Tier0;
    public string? AntiCheatSignature { get; init; }
    public int? SteamAppId { get; init; }
    public string? GogGameId { get; init; }
    public string? GogClientId { get; init; }
    public AchievementCaptureMode AchievementCaptureMode { get; init; } = AchievementCaptureMode.None;
    public string? AchievementChannelLabel { get; init; }
    public string? CoverImagePath { get; init; }
    public string? IconPath { get; init; }
    public string? HeroImagePath { get; init; }
    public string? LaunchArguments { get; init; }
    public long TotalActiveSec { get; init; }
    public long TotalBackgroundSec { get; init; }
    public long TotalAfkSec { get; init; }
    public DateTimeOffset? LastPlayedUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string EffectiveSortTitle => string.IsNullOrWhiteSpace(SortTitle) ? Title : SortTitle;

    [JsonIgnore]
    public string ExecutableFullPath => Path.GetFullPath(Path.Combine(InstallDir, ExecutableRelPath));

    [JsonIgnore]
    public long TotalPlaytimeSec => TotalActiveSec + TotalBackgroundSec + TotalAfkSec;
}

/// <summary>
/// Partial update payload for user-edited or rescanned game metadata.
/// </summary>
public sealed record GameMetadataUpdate
{
    public string? Title { get; init; }
    public string? SortTitle { get; init; }
    public string? ExecutableRelPath { get; init; }
    public string? LaunchArguments { get; init; }
    public PlatformSource? PlatformSource { get; init; }
    public AntiCheatTier? AntiCheatTier { get; init; }
    public string? AntiCheatSignature { get; init; }
    public int? SteamAppId { get; init; }
    public string? GogGameId { get; init; }
    public string? GogClientId { get; init; }
    public AchievementCaptureMode? AchievementCaptureMode { get; init; }
    public string? AchievementChannelLabel { get; init; }
    public string? CoverImagePath { get; init; }
    public string? IconPath { get; init; }
    public string? HeroImagePath { get; init; }
}

/// <summary>
/// Persisted play session ledger row with tri-state playtime counters.
/// </summary>
public sealed record PlaySessionRecord
{
    public required string SessionId { get; init; }
    public required string GameId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public long ActiveSec { get; init; }
    public long BackgroundSec { get; init; }
    public long AfkSec { get; init; }
    public int? ExitCode { get; init; }
}

/// <summary>
/// Live runtime snapshot of a tracked game session.
/// </summary>
public sealed record ActiveGameSession
{
    public required string SessionId { get; init; }
    public required string GameId { get; init; }
    public required string GameTitle { get; init; }
    public required string InstallDir { get; init; }
    public AntiCheatTier AntiCheatTier { get; init; }
    public PlaySessionActivityState State { get; init; } = PlaySessionActivityState.Launching;
    public int RootProcessId { get; init; }
    public IReadOnlyList<int> TrackedProcessIds { get; init; } = [];
    public required DateTimeOffset StartedUtc { get; init; }
    public long SessionActiveSec { get; init; }
    public long SessionBackgroundSec { get; init; }
    public long SessionAfkSec { get; init; }
}

/// <summary>
/// Outcome of launching a game via <see cref="IGameLaunchService"/>.
/// </summary>
public sealed record GameLaunchResult
{
    public required bool Succeeded { get; init; }
    public ActiveGameSession? Session { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static GameLaunchResult Success(ActiveGameSession session) =>
        new() { Succeeded = true, Session = session };

    public static GameLaunchResult Failure(string errorCode, string errorMessage) =>
        new() { Succeeded = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of statically inspecting a game installation folder for anti-cheat indicators.
/// </summary>
public sealed record AntiCheatInspectionResult(
    AntiCheatTier Tier,
    string? Signature,
    IReadOnlyList<string> MatchedIndicators);

/// <summary>
/// Result of the pre-launch high-risk environment sweep required before a <c>Tier 2/3</c> protected game starts
/// (Cheat Engine processes, the <c>dbk64.sys</c>/<c>dbk32.sys</c> kernel driver, SPAWN sensitive Workers).
/// </summary>
public sealed record HighRiskEnvironmentReport(
    IReadOnlyList<string> BlockingProcesses,
    IReadOnlyList<string> BlockingDrivers)
{
    public static HighRiskEnvironmentReport Clean { get; } = new([], []);

    public bool IsClean => BlockingProcesses.Count == 0 && BlockingDrivers.Count == 0;
}

/// <summary>
/// Snapshot of the global Anti-Cheat Guard lock and 15-second residual cooldown state.
/// </summary>
public sealed record AntiCheatGuardSnapshot(
    AntiCheatGuardPhase Phase,
    bool IsHighRiskLocked,
    double RemainingCooldownSeconds,
    string? ActiveProtectedGameId,
    IReadOnlyList<string> DetectedResidualProcesses);
