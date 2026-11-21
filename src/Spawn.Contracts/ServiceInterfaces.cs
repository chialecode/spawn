namespace Spawn.Contracts;

/// <summary>
/// Manages the local game catalog, directory scanning, offline artwork extraction, and metadata updates.
/// </summary>
public interface IGameLibraryService
{
    Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default);
    Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default);
    Task<GameRecord?> ImportGameAsync(string directoryOrExePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default);
    Task<GameRecord?> RescanGameAsync(string gameId, CancellationToken cancellationToken = default);
    Task<GameRecord?> UpdateGameMetadataAsync(string gameId, GameMetadataUpdate update, CancellationToken cancellationToken = default);
    Task<bool> RemoveGameAsync(string gameId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Launches games with anti-cheat isolation, non-killing JobObject + IOCP / shell-breaking tracking, and tri-state playtime accounting.
/// </summary>
public interface IGameLaunchService
{
    IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; }
    event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged;

    ActiveGameSession? GetActiveSession(string gameId);
    Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default);
    Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default);
    void RegisterTrustedOverlayWindow(nint windowHandle, int processId);
    void UnregisterTrustedOverlayWindow(nint windowHandle);
}

/// <summary>
/// Coordinates multi-channel offline/native achievement parsing, Chinese schema waterfall, real-time monitoring, and gbe_fork activation.
/// </summary>
public interface IAchievementService
{
    event EventHandler<AchievementUnlockedNotification>? AchievementUnlocked;

    Task<GameAchievementSummaryDto> GetGameAchievementsAsync(string gameId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AchievementUnlockDto>> GetRecentUnlocksAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task<GameAchievementSummaryDto> SyncGameSchemaAsync(string gameId, bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<int> ScanLocalUnlocksAsync(string gameId, bool silentSeed = false, CancellationToken cancellationToken = default);
    Task<bool> SetAchievementUnlockedAsync(string gameId, string apiName, bool unlocked, CancellationToken cancellationToken = default);
    Task<GbeForkActivationResult> ActivateGbeForkAsync(string gameId, CancellationToken cancellationToken = default);
    Task<GbeForkActivationResult> RestoreOriginalDllAsync(string gameId, CancellationToken cancellationToken = default);
    Task SendTestToastAsync(string? gameId = null, OverlayThemeKind? themeOverride = null, CancellationToken cancellationToken = default);
    Task ResetGameAchievementStateAsync(string gameId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task StartMonitoringAsync(CancellationToken cancellationToken = default);
    Task StopMonitoringAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovers, backs up, cleans, and restores per-game save data, offline emulator achievement files, configs/logs, and HKCU registry keys.
/// </summary>
public interface IGameDataManagementService
{
    Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default);

    Task<GameDataCleanResult> CleanGameDataAsync(
        GameDataCleanRequest request,
        CancellationToken cancellationToken = default);

    Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(
        string gameId,
        CancellationToken cancellationToken = default);

    Task<GameDataCleanResult> RestoreLastCleanupAsync(
        string gameId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Enforces four-tier anti-cheat classification, protected session interlocks, daemon exit verification, and the mandatory 15-second post-exit cooldown.
/// </summary>
public interface IAntiCheatGuardService
{
    AntiCheatGuardSnapshot CurrentSnapshot { get; }
    event EventHandler<AntiCheatGuardSnapshot>? StateChanged;

    AntiCheatInspectionResult InspectGameDirectory(string installDir);

    /// <summary>
    /// Sweeps the machine for high-risk tools (Cheat Engine, <c>dbk64</c> driver, SPAWN sensitive Workers) that must be
    /// absent before launching a <c>Tier 2/3</c> protected game.
    /// </summary>
    HighRiskEnvironmentReport InspectHighRiskEnvironment();
    void EnsureHighRiskOperationPermitted(AntiCheatTier targetGameTier, string operationName);
    void NotifyProtectedGameStarted(string gameId, AntiCheatTier tier);
    Task NotifyProtectedGameExitedAsync(string gameId, AntiCheatTier tier, CancellationToken cancellationToken = default);
}

/// <summary>
/// Named Pipe JSON-RPC client used by <c>Spawn.Host</c> to communicate with <c>Spawn.OverlayHost</c>.
/// </summary>
public interface IOverlayIpcClient : IAsyncDisposable
{
    string PipeName { get; }

    Task<bool> SendToastAsync(OverlayToastRequest request, CancellationToken cancellationToken = default);
    Task<int> SendToastBatchAsync(IReadOnlyList<OverlayToastRequest> requests, CancellationToken cancellationToken = default);
    Task<OverlayHeartbeatReply?> PingAsync(CancellationToken cancellationToken = default);
    Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides read-only / DPAPI-backed access to the Steam Web API key with automatic development-mode <c>.local/secrets/steam-web-api.json</c> fallback.
/// </summary>
public interface ISteamApiKeyProvider
{
    ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);
    ValueTask<SteamApiKeyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    ValueTask SaveCustomApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Named Pipe JSON-RPC server hosted inside <c>Spawn.OverlayHost</c> with current-user SDDL ACL and explicit NetworkSid deny.
/// </summary>
public interface IOverlayIpcServer : IAsyncDisposable
{
    string PipeName { get; }
    bool IsRunning { get; }
    Func<OverlayHeartbeatReply>? HeartbeatProvider { get; set; }

    event Func<OverlayToastRequest, CancellationToken, Task>? ToastReceived;
    event Func<CancellationToken, Task>? ShutdownRequested;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
