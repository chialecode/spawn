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
    public string? SteamBuildId { get; init; }
    public DateTimeOffset? SteamLastUpdatedUtc { get; init; }
    public bool VerifiedOfflineBypass { get; init; }
    public DateTimeOffset? VerifiedOfflineBypassAtUtc { get; init; }

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
    public string? SteamBuildId { get; init; }
    public DateTimeOffset? SteamLastUpdatedUtc { get; init; }
    public bool? VerifiedOfflineBypass { get; init; }
    public DateTimeOffset? VerifiedOfflineBypassAtUtc { get; init; }
    public bool? PinTitle { get; init; }
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

/// <summary>
/// Outcome status of requesting a tracked game session to stop.
/// </summary>
public enum GameStopOutcomeKind
{
    NotRunning = 0,
    StoppedGracefully = 1,
    ForceKilled = 2,
    GracefulTimeoutStillRunning = 3,
    Failed = 4,
}

/// <summary>
/// Detailed result returned by <see cref="IGameLaunchService.StopGameAsync(string, bool, TimeSpan?, CancellationToken)"/>.
/// </summary>
public sealed record GameStopResult
{
    public required bool Succeeded { get; init; }
    public required GameStopOutcomeKind Outcome { get; init; }
    public bool StillRunning { get; init; }
    public required string Message { get; init; }

    public static GameStopResult NotRunning() =>
        new()
        {
            Succeeded = false,
            Outcome = GameStopOutcomeKind.NotRunning,
            StillRunning = false,
            Message = "该游戏当前未在运行中。",
        };

    public static GameStopResult StoppedGracefully(string? message = null) =>
        new()
        {
            Succeeded = true,
            Outcome = GameStopOutcomeKind.StoppedGracefully,
            StillRunning = false,
            Message = message ?? "游戏已优雅退出。",
        };

    public static GameStopResult ForceKilled(string? message = null) =>
        new()
        {
            Succeeded = true,
            Outcome = GameStopOutcomeKind.ForceKilled,
            StillRunning = false,
            Message = message ?? "已强制结束游戏进程树。",
        };

    public static GameStopResult GracefulTimeout(string? message = null) =>
        new()
        {
            Succeeded = false,
            Outcome = GameStopOutcomeKind.GracefulTimeoutStillRunning,
            StillRunning = true,
            Message = message ?? "游戏未在优雅关闭等待时间内退出（可能存在未保存进度或关闭确认弹窗），未强杀进程；如需强制结束请显式选择强杀。",
        };

    public static GameStopResult Failed(string message, bool stillRunning = false) =>
        new()
        {
            Succeeded = false,
            Outcome = GameStopOutcomeKind.Failed,
            StillRunning = stillRunning,
            Message = message,
        };
}

/// <summary>
/// Read-only auto-discovered local game library root directory (Steam <c>steamapps\common</c> or GOG installation root).
/// </summary>
public sealed record DiscoveredLibraryDirectory(
    string Path,
    PlatformSource PlatformSource,
    string DisplayLabel,
    int DetectedGameHintCount = 0);

/// <summary>
/// Detailed result of importing a single game folder, executable, <c>.lnk</c> shortcut, or multi-game anthology container.
/// </summary>
public sealed record GameImportBatchResult
{
    public IReadOnlyList<GameRecord> ImportedGames { get; init; } = [];
    public int AddedCount { get; init; }
    public int UpdatedCount { get; init; }
    public bool ExpandedAnthology { get; init; }
    public bool ResolvedFromShortcut { get; init; }
    public string? FailureReason { get; init; }

    [JsonIgnore]
    public int TotalCount => ImportedGames.Count;

    [JsonIgnore]
    public GameRecord? PrimaryGame => ImportedGames.Count > 0 ? ImportedGames[0] : null;

    [JsonIgnore]
    public bool Succeeded => ImportedGames.Count > 0;
}

/// <summary>
/// Detailed report of scanning one library root directory, distinguishing genuinely new imports,
/// updated existing entries, and inaccessible or failed directories.
/// </summary>
public sealed record LibraryScanReport
{
    public required string RootDirectory { get; init; }
    public bool RootAccessible { get; init; } = true;
    public IReadOnlyList<GameRecord> AddedGames { get; init; } = [];
    public IReadOnlyList<GameRecord> UpdatedGames { get; init; } = [];
    public IReadOnlyList<string> InaccessibleDirectories { get; init; } = [];
    public IReadOnlyList<string> FailedDirectories { get; init; } = [];

    [JsonIgnore]
    public int AddedCount => AddedGames.Count;

    [JsonIgnore]
    public int UpdatedCount => UpdatedGames.Count;

    [JsonIgnore]
    public int InaccessibleCount => InaccessibleDirectories.Count;

    [JsonIgnore]
    public IReadOnlyList<GameRecord> AllGames => [.. AddedGames, .. UpdatedGames];
}
