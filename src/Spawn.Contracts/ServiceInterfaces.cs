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

    async Task<GameImportBatchResult> ImportGameDetailedAsync(
        string directoryOrExePath,
        CancellationToken cancellationToken = default)
    {
        var single = await ImportGameAsync(directoryOrExePath, cancellationToken).ConfigureAwait(false);
        return single is null
            ? new GameImportBatchResult { FailureReason = "未识别到可导入的游戏主程序。" }
            : new GameImportBatchResult { ImportedGames = [single], AddedCount = 1 };
    }

    async Task<LibraryScanReport> ScanLibraryDirectoryDetailedAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        bool accessible = !string.IsNullOrWhiteSpace(rootDirectory) && Directory.Exists(rootDirectory);
        if (!accessible)
        {
            return new LibraryScanReport
            {
                RootDirectory = rootDirectory ?? string.Empty,
                RootAccessible = false,
                InaccessibleDirectories = string.IsNullOrWhiteSpace(rootDirectory) ? [] : [rootDirectory],
            };
        }

        var list = await ScanLibraryDirectoryAsync(rootDirectory, cancellationToken).ConfigureAwait(false);
        return new LibraryScanReport
        {
            RootDirectory = rootDirectory,
            RootAccessible = true,
            AddedGames = list,
        };
    }

    Task<GameRecord?> RelocateGameDirectoryAsync(
        string gameId,
        string newInstallDir,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<GameRecord?>(null);

    IReadOnlyList<DiscoveredLibraryDirectory> DiscoverLocalPlatformLibraries() => [];
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

    async Task<GameStopResult> StopGameAsync(
        string gameId,
        bool forceKill,
        TimeSpan? gracefulTimeout = null,
        CancellationToken cancellationToken = default)
    {
        bool stopped = await StopGameAsync(gameId, cancellationToken).ConfigureAwait(false);
        return stopped
            ? (forceKill ? GameStopResult.ForceKilled() : GameStopResult.StoppedGracefully())
            : GameStopResult.NotRunning();
    }

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

    async Task<IReadOnlyList<GameDataUndoSnapshotDto>> ListUndoSnapshotsAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestUndoSnapshotAsync(gameId, cancellationToken).ConfigureAwait(false);
        return latest is null ? [] : [latest];
    }

    async Task<GameDataCleanResult> RestoreUndoSnapshotAsync(
        string gameId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestUndoSnapshotAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (latest is not null && string.Equals(latest.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
        {
            return await RestoreLastCleanupAsync(gameId, cancellationToken).ConfigureAwait(false);
        }

        return GameDataCleanResult.Fail("未找到指定的清理回收站快照。");
    }

    Task<GameDataTrashPurgeResult> PurgeTrashSnapshotsAsync(
        string gameId,
        string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(GameDataTrashPurgeResult.Fail("当前服务实现不支持清空回收站快照。"));

    /// <summary>
    /// Read-only enumeration of discovered save directories/files and <c>HKCU</c> registry keys for a game
    /// (consumed by <c>Spawn.Modules.Progress</c> pre-trainer snapshots without cross-module project coupling).
    /// </summary>
    async Task<IReadOnlyList<GameDataEntryDto>> EnumerateSaveLocationsAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default)
    {
        var all = await InspectGameDataAsync(gameId, refreshOnlineRules, cancellationToken).ConfigureAwait(false);
        return all
            .Where(e => e.Exists && e.IsSafeToClean && (e.Category == GameDataCategory.SaveData || e.Category == GameDataCategory.RegistryData))
            .ToArray();
    }

    /// <summary>
    /// Validates a candidate save restore target (filesystem path or <c>HKCU</c> registry key) against the safety fence.
    /// Allows restoring into known safe save locations even when the target directory/key has since been deleted (<c>Exists == false</c>).
    /// </summary>
    async Task<GameDataSafetyCheckResult> ValidateSaveRestoreTargetAsync(
        string gameId,
        string pathOrRegistryKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(pathOrRegistryKey))
        {
            return new GameDataSafetyCheckResult(false, "游戏标识或恢复目标路径为空。");
        }

        string trimmed = pathOrRegistryKey.Trim();
        string[] rawSegments = trimmed.Split(['\\', '/'], StringSplitOptions.None);
        if (rawSegments.Any(static s => s is ".." or "."))
        {
            return new GameDataSafetyCheckResult(false, $"安全围栏已拦截含相对跳转段的目标路径：{pathOrRegistryKey}");
        }

        bool looksLikeRegistry =
            trimmed.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKCU/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKLM/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKCC\\", StringComparison.OrdinalIgnoreCase);

        if (looksLikeRegistry)
        {
            string normalizedReg = trimmed.Replace('/', '\\').TrimEnd('\\');
            if (normalizedReg.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            {
                normalizedReg = "HKCU\\" + normalizedReg["HKEY_CURRENT_USER\\".Length..];
            }

            if (!normalizedReg.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
                || normalizedReg.Length <= "HKCU\\".Length)
            {
                return new GameDataSafetyCheckResult(false, $"安全围栏仅允许恢复 HKCU 下的游戏注册表项：{pathOrRegistryKey}");
            }

            var allEntries = await InspectGameDataAsync(gameId, refreshOnlineRules: false, cancellationToken).ConfigureAwait(false);
            foreach (var loc in allEntries)
            {
                if (!loc.IsSafeToClean || !loc.IsRegistryEntry || string.IsNullOrWhiteSpace(loc.PathOrRegistryKey))
                {
                    continue;
                }

                string locReg = loc.PathOrRegistryKey.Trim().Replace('/', '\\').TrimEnd('\\');
                if (locReg.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
                {
                    locReg = "HKCU\\" + locReg["HKEY_CURRENT_USER\\".Length..];
                }

                if (!locReg.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) || locReg.Length <= "HKCU\\".Length)
                {
                    continue;
                }

                if (string.Equals(normalizedReg, locReg, StringComparison.OrdinalIgnoreCase)
                    || normalizedReg.StartsWith(locReg + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return new GameDataSafetyCheckResult(true);
                }
            }

            return new GameDataSafetyCheckResult(false, $"注册表路径不在已验证的存档位置范围内：{pathOrRegistryKey}");
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return new GameDataSafetyCheckResult(false, $"恢复目标必须为绝对路径：{pathOrRegistryKey}");
        }

        string normalizedTarget;
        try
        {
            normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new GameDataSafetyCheckResult(false, $"非法的恢复目标路径：{pathOrRegistryKey}");
        }

        var discovered = await InspectGameDataAsync(gameId, refreshOnlineRules: false, cancellationToken).ConfigureAwait(false);
        foreach (var loc in discovered)
        {
            if (!loc.IsSafeToClean
                || loc.IsRegistryEntry
                || (loc.Category != GameDataCategory.SaveData && loc.Category != GameDataCategory.ConfigAndLogs)
                || string.IsNullOrWhiteSpace(loc.PathOrRegistryKey))
            {
                continue;
            }

            string normalizedLoc;
            try
            {
                string candidateLoc = loc.PathOrRegistryKey.Trim();
                if (!Path.IsPathFullyQualified(candidateLoc))
                {
                    continue;
                }

                normalizedLoc = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateLoc));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (string.Equals(normalizedTarget, normalizedLoc, StringComparison.OrdinalIgnoreCase)
                || normalizedTarget.StartsWith(normalizedLoc + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new GameDataSafetyCheckResult(true);
            }
        }

        return new GameDataSafetyCheckResult(false, $"路径不在已验证的存档位置范围内：{pathOrRegistryKey}");
    }
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
