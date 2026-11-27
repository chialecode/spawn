namespace Spawn.Contracts;

/// <summary>
/// Unified, Clean-Host compliant local Steam client, executable, running-state, and account locator.
/// </summary>
public interface ISteamClientLocator
{
    /// <summary>
    /// Discovers the local Steam installation, executable path, running state, and Steam accounts.
    /// Read-only: never launches Steam or modifies any registry key or file.
    /// </summary>
    SteamClientDiscoveryResult Discover(
        string? explicitInstallDirOverride = null,
        uint? preferredAccountId = null);

    /// <summary>
    /// Validates and normalizes a user-supplied Steam installation directory or <c>steam.exe</c> path.
    /// </summary>
    bool TryNormalizeInstallDirectory(
        string? rawPathOrExe,
        out string? normalizedInstallDir,
        out string? steamExePath,
        out string? errorMessage);
}

/// <summary>
/// Prepares official Steam and non-Steam (<c>shortcuts.vdf</c>) Steam Input launches,
/// including lossless Binary VDF synchronization and user-confirmed graceful Steam restarts.
/// </summary>
public interface ISteamShortcutService
{
    /// <summary>
    /// Evaluates whether <paramref name="game"/> can launch via Steam, resolves its <c>steam://rungameid/...</c> URI,
    /// inspects emulator/elevation warnings, and indicates whether <c>shortcuts.vdf</c> requires writing or a Steam restart.
    /// Read-only: never modifies disk state or restarts Steam.
    /// </summary>
    Task<SteamLaunchPreparationResult> PrepareLaunchAsync(
        GameRecord game,
        GameSteamLaunchSettings settings,
        string? steamInstallDirOverride = null,
        uint? preferredAccountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes/updates the non-Steam shortcut in <c>shortcuts.vdf</c> atomically when needed.
    /// When Steam is running, only performs a graceful shutdown (<c>steam.exe -shutdown</c>), write, and relaunch
    /// if <paramref name="allowGracefulSteamRestart"/> is <see langword="true"/>.
    /// </summary>
    Task<SteamShortcutSyncResult> ApplyShortcutAndRestartIfNeededAsync(
        GameRecord game,
        GameSteamLaunchSettings settings,
        bool allowGracefulSteamRestart,
        string? steamInstallDirOverride = null,
        uint? preferredAccountId = null,
        CancellationToken cancellationToken = default);
}
