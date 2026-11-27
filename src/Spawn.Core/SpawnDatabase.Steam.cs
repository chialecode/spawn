using System.Globalization;
using Spawn.Contracts;

namespace Spawn.Core;

public sealed partial class SpawnDatabase
{
    // -------------------------------------------------------------------------
    // Global & Per-Game Steam Launch Settings Persistence (app_settings)
    // -------------------------------------------------------------------------

    public async Task<string?> GetSteamInstallDirectoryOverrideAsync(CancellationToken cancellationToken = default)
    {
        string? raw = await GetSettingAsync(AppSettingKeys.SteamInstallDirectoryOverride, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    public Task SetSteamInstallDirectoryOverrideAsync(string? directoryPath, CancellationToken cancellationToken = default) =>
        SetSettingAsync(
            AppSettingKeys.SteamInstallDirectoryOverride,
            string.IsNullOrWhiteSpace(directoryPath) ? null : directoryPath.Trim(),
            cancellationToken);

    public async Task<uint?> GetSteamTargetAccountIdAsync(CancellationToken cancellationToken = default)
    {
        string? raw = await GetSettingAsync(AppSettingKeys.SteamTargetAccountId, cancellationToken).ConfigureAwait(false);
        return uint.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed) && parsed > 0
            ? parsed
            : null;
    }

    public Task SetSteamTargetAccountIdAsync(uint? accountId, CancellationToken cancellationToken = default) =>
        SetSettingAsync(
            AppSettingKeys.SteamTargetAccountId,
            accountId is > 0 ? accountId.Value.ToString(CultureInfo.InvariantCulture) : null,
            cancellationToken);

    /// <summary>
    /// Defaults to <see langword="true"/> so official <c>SteamLocal</c> games launched through Steam do not double-pop
    /// both Steam's native achievement toast and SPAWN's OverlayHost toast simultaneously.
    /// </summary>
    public async Task<bool> GetSteamSuppressDuplicateAchievementToastAsync(CancellationToken cancellationToken = default)
    {
        string? raw = await GetSettingAsync(AppSettingKeys.SteamSuppressDuplicateAchievementToast, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out bool parsed) || parsed;
    }

    public Task SetSteamSuppressDuplicateAchievementToastAsync(bool suppress, CancellationToken cancellationToken = default) =>
        SetSettingAsync(AppSettingKeys.SteamSuppressDuplicateAchievementToast, suppress ? "true" : "false", cancellationToken);

    public async Task<GameSteamLaunchSettings> GetGameSteamLaunchSettingsAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        string? rawMode = await GetSettingAsync(AppSettingKeys.GetGameLaunchModeKey(gameId), cancellationToken).ConfigureAwait(false);
        string? rawUseAppId = await GetSettingAsync(AppSettingKeys.GetGameSteamUseAppIdNameKey(gameId), cancellationToken).ConfigureAwait(false);
        string? rawShortcutId = await GetSettingAsync(AppSettingKeys.GetGameSteamShortcutAppIdKey(gameId), cancellationToken).ConfigureAwait(false);

        GameLaunchMode mode = ParseEnumOrDefault(rawMode, GameLaunchMode.Direct);
        bool useAppIdName = bool.TryParse(rawUseAppId, out bool parsedBool) && parsedBool;
        int? shortcutAppId = int.TryParse(rawShortcutId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId) && parsedId != 0
            ? parsedId
            : null;

        return new GameSteamLaunchSettings
        {
            LaunchMode = mode,
            UseSteamAppIdAsShortcutName = useAppIdName,
            RegisteredShortcutAppId = shortcutAppId,
        };
    }

    public async Task SaveGameSteamLaunchSettingsAsync(
        string gameId,
        GameSteamLaunchSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(settings);

        await SetSettingAsync(
            AppSettingKeys.GetGameLaunchModeKey(gameId),
            settings.LaunchMode == GameLaunchMode.Direct ? null : settings.LaunchMode.ToString(),
            cancellationToken).ConfigureAwait(false);

        await SetSettingAsync(
            AppSettingKeys.GetGameSteamUseAppIdNameKey(gameId),
            settings.UseSteamAppIdAsShortcutName ? "true" : null,
            cancellationToken).ConfigureAwait(false);

        await SetSettingAsync(
            AppSettingKeys.GetGameSteamShortcutAppIdKey(gameId),
            settings.RegisteredShortcutAppId is { } id && id != 0
                ? id.ToString(CultureInfo.InvariantCulture)
                : null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateGameRegisteredShortcutAppIdAsync(
        string gameId,
        int? shortcutAppId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return SetSettingAsync(
            AppSettingKeys.GetGameSteamShortcutAppIdKey(gameId),
            shortcutAppId is { } id && id != 0
                ? id.ToString(CultureInfo.InvariantCulture)
                : null,
            cancellationToken);
    }
}
