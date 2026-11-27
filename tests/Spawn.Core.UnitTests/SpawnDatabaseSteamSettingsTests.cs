using Spawn.Contracts;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class SpawnDatabaseSteamSettingsTests
{
    [Fact]
    public async Task GlobalSteamSettings_DefaultsRoundTripClearAndPersistAcrossReopen()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // Defaults on fresh database
        Assert.Null(await sandbox.Database.GetSteamInstallDirectoryOverrideAsync());
        Assert.Null(await sandbox.Database.GetSteamTargetAccountIdAsync());
        Assert.True(await sandbox.Database.GetSteamSuppressDuplicateAchievementToastAsync());

        // Set global values (verifying whitespace trimming on path)
        await sandbox.Database.SetSteamInstallDirectoryOverrideAsync("  D:\\Portable\\Steam  ");
        await sandbox.Database.SetSteamTargetAccountIdAsync(123456789u);
        await sandbox.Database.SetSteamSuppressDuplicateAchievementToastAsync(false);

        Assert.Equal(@"D:\Portable\Steam", await sandbox.Database.GetSteamInstallDirectoryOverrideAsync());
        Assert.Equal(123456789u, await sandbox.Database.GetSteamTargetAccountIdAsync());
        Assert.False(await sandbox.Database.GetSteamSuppressDuplicateAchievementToastAsync());

        // Verify persistence when reopening a fresh SpawnDatabase instance on the same temp SQLite file
        using (var reopened = new SpawnDatabase(sandbox.Paths))
        {
            await reopened.InitializeAsync();
            Assert.Equal(@"D:\Portable\Steam", await reopened.GetSteamInstallDirectoryOverrideAsync());
            Assert.Equal(123456789u, await reopened.GetSteamTargetAccountIdAsync());
            Assert.False(await reopened.GetSteamSuppressDuplicateAchievementToastAsync());

            // Clear override & account ID, re-enable duplicate toast suppression
            await reopened.SetSteamInstallDirectoryOverrideAsync("   ");
            await reopened.SetSteamTargetAccountIdAsync(0u);
            await reopened.SetSteamSuppressDuplicateAchievementToastAsync(true);
        }

        Assert.Null(await sandbox.Database.GetSteamInstallDirectoryOverrideAsync());
        Assert.Null(await sandbox.Database.GetSteamTargetAccountIdAsync());
        Assert.True(await sandbox.Database.GetSteamSuppressDuplicateAchievementToastAsync());

        // Malformed raw app_settings rows fail safe
        await sandbox.Database.SetSettingAsync(AppSettingKeys.SteamTargetAccountId, "not-a-uint");
        await sandbox.Database.SetSettingAsync(AppSettingKeys.SteamSuppressDuplicateAchievementToast, "not-a-bool");
        Assert.Null(await sandbox.Database.GetSteamTargetAccountIdAsync());
        Assert.True(await sandbox.Database.GetSteamSuppressDuplicateAchievementToastAsync());
    }

    [Fact]
    public async Task PerGameSteamLaunchSettings_RoundTripsIsolatesGamesAndUpdatesRegisteredShortcutAppId()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameA = SpawnId.NewUlid();
        string gameB = SpawnId.NewUlid();

        // Defaults for unconfigured game
        GameSteamLaunchSettings defaultA = await sandbox.Database.GetGameSteamLaunchSettingsAsync(gameA);
        Assert.Equal(GameLaunchMode.Direct, defaultA.LaunchMode);
        Assert.False(defaultA.UseSteamAppIdAsShortcutName);
        Assert.Null(defaultA.RegisteredShortcutAppId);

        // Save Steam launch settings including signed negative int32 shortcut AppID (high bit 0x80000000 set)
        int signedShortcutAppId = unchecked((int)0x81234567u);
        var savedA = new GameSteamLaunchSettings
        {
            LaunchMode = GameLaunchMode.Steam,
            UseSteamAppIdAsShortcutName = true,
            RegisteredShortcutAppId = signedShortcutAppId,
        };
        await sandbox.Database.SaveGameSteamLaunchSettingsAsync(gameA, savedA);

        GameSteamLaunchSettings loadedA = await sandbox.Database.GetGameSteamLaunchSettingsAsync(gameA);
        Assert.Equal(GameLaunchMode.Steam, loadedA.LaunchMode);
        Assert.True(loadedA.UseSteamAppIdAsShortcutName);
        Assert.Equal(signedShortcutAppId, loadedA.RegisteredShortcutAppId);

        // Game B remains unaffected
        GameSteamLaunchSettings loadedB = await sandbox.Database.GetGameSteamLaunchSettingsAsync(gameB);
        Assert.Equal(GameLaunchMode.Direct, loadedB.LaunchMode);
        Assert.False(loadedB.UseSteamAppIdAsShortcutName);
        Assert.Null(loadedB.RegisteredShortcutAppId);

        // UpdateGameRegisteredShortcutAppIdAsync updates ONLY the shortcut AppID without clobbering other fields
        int updatedAppId = unchecked((int)0x89ABCDEFu);
        await sandbox.Database.UpdateGameRegisteredShortcutAppIdAsync(gameA, updatedAppId);
        GameSteamLaunchSettings afterUpdate = await sandbox.Database.GetGameSteamLaunchSettingsAsync(gameA);
        Assert.Equal(GameLaunchMode.Steam, afterUpdate.LaunchMode);
        Assert.True(afterUpdate.UseSteamAppIdAsShortcutName);
        Assert.Equal(updatedAppId, afterUpdate.RegisteredShortcutAppId);

        // Passing 0 or null clears RegisteredShortcutAppId cleanly
        await sandbox.Database.UpdateGameRegisteredShortcutAppIdAsync(gameA, 0);
        Assert.Null((await sandbox.Database.GetGameSteamLaunchSettingsAsync(gameA)).RegisteredShortcutAppId);

        // Resetting back to Direct defaults cleans underlying app_settings entries
        await sandbox.Database.SaveGameSteamLaunchSettingsAsync(gameA, new GameSteamLaunchSettings());
        GameSteamLaunchSettings resetA = await sandbox.Database.GetGameSteamLaunchSettingsAsync(gameA);
        Assert.Equal(GameLaunchMode.Direct, resetA.LaunchMode);
        Assert.False(resetA.UseSteamAppIdAsShortcutName);
        Assert.Null(resetA.RegisteredShortcutAppId);
        Assert.Null(await sandbox.Database.GetSettingAsync(AppSettingKeys.GetGameLaunchModeKey(gameA)));
        Assert.Null(await sandbox.Database.GetSettingAsync(AppSettingKeys.GetGameSteamUseAppIdNameKey(gameA)));
        Assert.Null(await sandbox.Database.GetSettingAsync(AppSettingKeys.GetGameSteamShortcutAppIdKey(gameA)));
    }
}
