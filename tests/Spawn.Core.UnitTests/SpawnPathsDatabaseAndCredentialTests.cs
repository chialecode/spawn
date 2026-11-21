using Microsoft.Data.Sqlite;
using Spawn.Contracts;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class SpawnPathsDatabaseAndCredentialTests
{
    [Fact]
    public void SpawnPaths_CreateIsolated_StaysInsideTempAndNeverTouchesRealAppData()
    {
        string tempRootSnapshot;
        using (var sandbox = new TestSandbox())
        {
            tempRootSnapshot = sandbox.RootDirectory;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string prodRoot = Path.GetFullPath(Path.Combine(localAppData, "spawn"));
            string devRoot = Path.GetFullPath(Path.Combine(localAppData, "spawn-dev"));
            string tempBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

            Assert.True(sandbox.Paths.IsIsolatedOverride);
            Assert.Equal("spawn-test", sandbox.Paths.ChannelName);
            Assert.StartsWith(tempBase, sandbox.Paths.DataRoot, StringComparison.OrdinalIgnoreCase);
            Assert.False(sandbox.Paths.DataRoot.StartsWith(prodRoot, StringComparison.OrdinalIgnoreCase));
            Assert.False(sandbox.Paths.DataRoot.StartsWith(devRoot, StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("spawn-test-ipc-", sandbox.Paths.OverlayPipeName, StringComparison.Ordinal);

            string artDir = sandbox.Paths.GetGameArtworkDirectory("gog:1724299977");
            string iconDir = sandbox.Paths.GetAchievementIconDirectory("steam:48000");
            string trashDir = sandbox.Paths.GetGameTrashDirectory("gog:1724299977");
            Assert.True(Directory.Exists(artDir));
            Assert.True(Directory.Exists(iconDir));
            Assert.True(Directory.Exists(trashDir));
        }

        Assert.False(Directory.Exists(tempRootSnapshot));
    }

    [Fact]
    public async Task SpawnPaths_DevWorkspaceJsonBridge_ResolvesLocalGamesAndSecretsFromExternalInstallDir()
    {
        await using var sandbox = new TestSandbox();
        string fakeRepoRoot = sandbox.CreateSubDirectory("simulated-repo");
        string localGamesDir = Directory.CreateDirectory(Path.Combine(fakeRepoRoot, ".local", "games")).FullName;
        string localSecretsDir = Directory.CreateDirectory(Path.Combine(fakeRepoRoot, ".local", "secrets")).FullName;

        string externalDataDir = sandbox.CreateSubDirectory("installed-dev-data");
        string bridgeFile = Path.Combine(externalDataDir, SpawnPaths.DevWorkspaceFileName);
        string escapedRepo = fakeRepoRoot.Replace("\\", "\\\\");
        await File.WriteAllTextAsync(bridgeFile, $$"""{"workspaceRoot": "{{escapedRepo}}"}""");

        Assert.Equal(Path.GetFullPath(fakeRepoRoot), SpawnPaths.TryReadDevWorkspaceFile(bridgeFile));

        var bridgedPaths = new SpawnPaths(
            explicitDataDir: externalDataDir,
            isProduction: false,
            workspaceRootHint: null,
            explicitIdentity: "spawn-dev-test");

        Assert.Equal("spawn-dev-test", bridgedPaths.Identity);
        Assert.Equal(Path.GetFullPath(fakeRepoRoot), bridgedPaths.LocalWorkspaceRoot);
        Assert.Equal(Path.GetFullPath(localGamesDir), bridgedPaths.LocalGamesDirectory);
        Assert.Equal(Path.GetFullPath(localSecretsDir), bridgedPaths.LocalSecretsDirectory);
        Assert.Contains(Path.GetFullPath(localGamesDir), bridgedPaths.GetDefaultScanDirectories());
    }

    [Fact]
    public async Task Database_WalModeAndSchemaMigrations_CrudRoundTripForGamesSessionsAchievementsAndSettings()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        Assert.True(File.Exists(sandbox.Database.DatabaseFilePath));

        // Verify SQLite WAL pragma is active on the database file.
        await using (var rawConn = new SqliteConnection($"Data Source={sandbox.Database.DatabaseFilePath};Pooling=False"))
        {
            await rawConn.OpenAsync();
            await using var cmd = rawConn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            string? mode = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("wal", mode, ignoreCase: true);
        }

        string fakeInstallDir = sandbox.CreateSubDirectory("games", "SampleTitle");
        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "LIMBO Sandbox Copy",
            InstallDir = fakeInstallDir,
            ExecutableRelPath = "limbo.exe",
            PlatformSource = PlatformSource.GogOffline,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 48000,
            GogGameId = "1724299977",
            GogClientId = "49494844895097688",
            AchievementCaptureMode = AchievementCaptureMode.GogNativeSave,
        };

        await sandbox.Database.UpsertGameAsync(game);
        var loaded = await sandbox.Database.GetGameByIdAsync(game.GameId);
        Assert.NotNull(loaded);
        Assert.Equal("1724299977", loaded!.GogGameId);
        Assert.Equal(48000, loaded.SteamAppId);

        var byDir = await sandbox.Database.GetGameByInstallDirAsync(fakeInstallDir + Path.DirectorySeparatorChar);
        Assert.NotNull(byDir);
        Assert.Equal(game.GameId, byDir!.GameId);

        // Play session completion & atomic accumulation
        var session = new PlaySessionRecord
        {
            SessionId = SpawnId.NewUlid(),
            GameId = game.GameId,
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-15),
            EndedUtc = DateTimeOffset.UtcNow,
            ActiveSec = 600,
            BackgroundSec = 180,
            AfkSec = 120,
            ExitCode = 0,
        };
        await sandbox.Database.CompletePlaySessionAndAccumulateAsync(session);

        var updatedGame = await sandbox.Database.GetGameByIdAsync(game.GameId);
        Assert.Equal(600, updatedGame!.TotalActiveSec);
        Assert.Equal(180, updatedGame.TotalBackgroundSec);
        Assert.Equal(120, updatedGame.TotalAfkSec);
        Assert.Equal(900, updatedGame.TotalPlaytimeSec);

        // Achievement schema + initial silent seed vs live transition
        var defs = new[]
        {
            new AchievementDefinitionDto
            {
                ApiName = "ACH_WRONG_WAY",
                DisplayName = "走错方向",
                Description = "那就太不对了",
                IsHidden = false,
                GlobalPercent = 42.5,
                SortOrder = 0,
            },
            new AchievementDefinitionDto
            {
                ApiName = "ACH_DING",
                DisplayName = "叮！",
                Description = string.Empty,
                HiddenSpoilerDescription = "不改重力连续奔跑跳跃",
                IsHidden = true,
                GlobalPercent = 4.2,
                SortOrder = 1,
            },
        };
        await sandbox.Database.UpsertAchievementSchemaAsync(
            game.GameId,
            "steam:48000",
            AchievementSchemaSource.SteamWebApi,
            "schinese",
            defs);

        // Seed 1st achievement unlocked
        var seedTransition = await sandbox.Database.UpsertAchievementUnlocksBatchAsync(
            game.GameId,
            [
                new AchievementUnlockDto
                {
                    GameId = game.GameId,
                    ApiName = "ACH_WRONG_WAY",
                    IsUnlocked = true,
                    SourceProvider = "LimboNative",
                    NotifiedPopup = true,
                },
            ]);
        Assert.Single(seedTransition);

        // Re-upserting already-unlocked achievement must NOT trigger a duplicate transition!
        var duplicateCheck = await sandbox.Database.UpsertAchievementUnlocksBatchAsync(
            game.GameId,
            [
                new AchievementUnlockDto
                {
                    GameId = game.GameId,
                    ApiName = "ACH_WRONG_WAY",
                    IsUnlocked = true,
                    SourceProvider = "LimboNative",
                    NotifiedPopup = false,
                },
                new AchievementUnlockDto
                {
                    GameId = game.GameId,
                    ApiName = "ACH_DING",
                    IsUnlocked = true,
                    SourceProvider = "LimboNative",
                    NotifiedPopup = false,
                },
            ]);
        Assert.Single(duplicateCheck);
        Assert.Equal("ACH_DING", duplicateCheck[0].ApiName);

        var summary = await sandbox.Database.GetGameAchievementSummaryAsync(game.GameId);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(2, summary.UnlockedCount);
        Assert.Equal(100.0, summary.CompletionPercent);
        Assert.True(summary.Items[1].Definition.IsRare);

        // Overlay settings persistence (defaults smoothly to SteamAuthentic + AutoSmart null/default, then round-trips CustomWav + device ID)
        var initialDefaultPrefs = await sandbox.Database.GetOverlaySettingsAsync();
        Assert.Equal(OverlaySoundPreset.SteamAuthentic, initialDefaultPrefs.SoundPreset);
        Assert.True(string.IsNullOrEmpty(initialDefaultPrefs.AudioOutputDeviceId)
                    || initialDefaultPrefs.AudioOutputDeviceId == OverlayAudioOutputModes.AutoSmart);

        string customWavSample = Path.Combine(sandbox.Paths.ConfigDirectory, "custom_toast.wav");
        var overlayPrefs = new OverlaySettingsDto
        {
            Theme = OverlayThemeKind.Ps5Trophy,
            Position = OverlayPositionKind.TopRight,
            SoundPreset = OverlaySoundPreset.CustomWav,
            CustomSoundPath = customWavSample,
            AudioOutputDeviceId = OverlayAudioOutputModes.SystemDefault,
            SoundVolume = 0.45,
            MuteSound = true,
        };
        await sandbox.Database.SaveOverlaySettingsAsync(overlayPrefs);
        var loadedPrefs = await sandbox.Database.GetOverlaySettingsAsync();
        Assert.Equal(OverlayThemeKind.Ps5Trophy, loadedPrefs.Theme);
        Assert.Equal(OverlayPositionKind.TopRight, loadedPrefs.Position);
        Assert.Equal(OverlaySoundPreset.CustomWav, loadedPrefs.SoundPreset);
        Assert.Equal(customWavSample, loadedPrefs.CustomSoundPath);
        Assert.Equal(OverlayAudioOutputModes.SystemDefault, loadedPrefs.AudioOutputDeviceId);
        Assert.Equal(0.45, loadedPrefs.SoundVolume, precision: 3);
        Assert.True(loadedPrefs.MuteSound);

        // Cascade delete check
        Assert.True(await sandbox.Database.DeleteGameAsync(game.GameId));
        Assert.Null(await sandbox.Database.GetGameByIdAsync(game.GameId));
    }

    [Fact]
    public async Task Database_ConcurrentSingleWriterQueue_SerializesBatchWritesWithoutLockFailure()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        var tasks = Enumerable.Range(0, 16).Select(async idx =>
        {
            string dir = sandbox.CreateSubDirectory("games", $"Concurrent_{idx}");
            await sandbox.Database.UpsertGameAsync(new GameRecord
            {
                GameId = SpawnId.NewUlid(),
                Title = $"Game #{idx}",
                InstallDir = dir,
                ExecutableRelPath = "game.exe",
            });
            await sandbox.Database.SetSettingAsync($"test.key.{idx}", $"val-{idx}");
        });

        await Task.WhenAll(tasks);
        var allGames = await sandbox.Database.GetAllGamesAsync();
        Assert.Equal(16, allGames.Count);
    }

    [Fact]
    public async Task Credential_DpapiSecretStoreAndSteamApiKeyProvider_RoundTripsMasksAndReadsLocalFallback()
    {
        // 1. Isolated workspace without .local fallback -> tests pure DPAPI lifecycle
        string emptyHintDir;
        await using (var isolatedSandbox = new TestSandbox())
        {
            emptyHintDir = isolatedSandbox.CreateSubDirectory("empty-workspace");
            Directory.CreateDirectory(Path.Combine(emptyHintDir, ".git"));

            var isolatedPaths = SpawnPaths.CreateIsolated(
                isolatedSandbox.CreateSubDirectory("dpapi-only"),
                workspaceRootHint: emptyHintDir);

            var provider = new SteamApiKeyProvider(isolatedPaths, allowLocalDevFallback: false);
            var initialStatus = await provider.GetStatusAsync();
            Assert.False(initialStatus.IsConfigured);
            Assert.Null(await provider.GetApiKeyAsync());

            const string sampleKey = "A1B2C3D4E5F60718293A4B5C6D7E8F90";
            await provider.SaveCustomApiKeyAsync(sampleKey);

            string? readBack = await provider.GetApiKeyAsync();
            Assert.Equal(sampleKey, readBack);

            var configuredStatus = await provider.GetStatusAsync();
            Assert.True(configuredStatus.IsConfigured);
            Assert.NotNull(configuredStatus.MaskedPreview);
            Assert.StartsWith("A1B2", configuredStatus.MaskedPreview!, StringComparison.Ordinal);
            Assert.Contains("************************", configuredStatus.MaskedPreview!, StringComparison.Ordinal);
            Assert.DoesNotContain(sampleKey, configuredStatus.MaskedPreview!, StringComparison.Ordinal);

            // Verify raw .sec file on disk does NOT contain plaintext bytes
            string secFile = Path.Combine(isolatedPaths.SecretsDirectory, $"{AppSettingKeys.SteamWebApiCredentialId}.sec");
            Assert.True(File.Exists(secFile));
            string rawDiskAscii = System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(secFile));
            Assert.DoesNotContain(sampleKey, rawDiskAscii, StringComparison.Ordinal);

            // Delete custom key
            await provider.SaveCustomApiKeyAsync(null);
            Assert.Null(await provider.GetApiKeyAsync());
        }

        // 2. Synthetic .local/secrets/steam-web-api.json read-only fallback verification in temp sandbox
        await using (var fallbackSandbox = new TestSandbox())
        {
            string fakeWorkspace = fallbackSandbox.CreateSubDirectory("dev-workspace");
            string localSecretsDir = Path.Combine(fakeWorkspace, ".local", "secrets");
            Directory.CreateDirectory(localSecretsDir);
            const string syntheticLocalKey = "99887766554433221100AABBCCDDEEFF";
            await File.WriteAllTextAsync(
                Path.Combine(localSecretsDir, "steam-web-api.json"),
                $$"""{"apiKey": "{{syntheticLocalKey}}", "domain": "localhost"}""");

            var devPaths = SpawnPaths.CreateIsolated(
                fallbackSandbox.CreateSubDirectory("dev-data"),
                workspaceRootHint: fakeWorkspace);
            var devProvider = new SteamApiKeyProvider(devPaths, allowLocalDevFallback: true);

            string? resolved = await devProvider.GetApiKeyAsync();
            Assert.Equal(syntheticLocalKey, resolved);

            var status = await devProvider.GetStatusAsync();
            Assert.True(status.IsConfigured);
            Assert.Contains(".local", status.SourceLabel, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("9988", status.MaskedPreview!, StringComparison.Ordinal);
            Assert.EndsWith("EEFF (来自 .local)", status.MaskedPreview!, StringComparison.Ordinal);
            Assert.DoesNotContain(syntheticLocalKey, status.MaskedPreview!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SpawnPaths_SteamClientSoundLocator_PrefersDesktopAchievementToastOverGamepadUiInSandbox()
    {
        await using var sandbox = new TestSandbox();
        string fakeSteamRoot = sandbox.CreateSubDirectory("fake-steam");

        // 1. Directory exists without sound files -> graceful fallback message
        var emptyProbe = SteamClientSoundLocator.Probe(fakeSteamRoot);
        Assert.True(emptyProbe.IsSteamInstalled);
        Assert.False(emptyProbe.HasAuthenticAchievementSound);
        Assert.Null(emptyProbe.EffectiveSoundPath);

        // 2. Only the Big Picture / Deck gamepad UI deck_ui_achievement_toast.wav present -> used as fallback
        string soundsDir = Directory.CreateDirectory(Path.Combine(fakeSteamRoot, "steamui", "sounds")).FullName;
        string fallbackWav = Path.Combine(soundsDir, "deck_ui_achievement_toast.wav");
        await File.WriteAllBytesAsync(fallbackWav, [0x52, 0x49, 0x46, 0x46]);

        var fallbackProbe = SteamClientSoundLocator.Probe(fakeSteamRoot);
        Assert.False(fallbackProbe.HasAuthenticAchievementSound);
        Assert.Equal(Path.GetFullPath(fallbackWav), fallbackProbe.GamepadUiAchievementToastWavPath);
        Assert.Equal(Path.GetFullPath(fallbackWav), fallbackProbe.EffectiveSoundPath);

        // 3. Desktop desktop_toast_default.wav (what desktop Steam plays for ToastAchievement) present -> takes precedence!
        string primaryWav = Path.Combine(soundsDir, "desktop_toast_default.wav");
        await File.WriteAllBytesAsync(primaryWav, [0x52, 0x49, 0x46, 0x46]);

        var primaryProbe = SteamClientSoundLocator.Probe(fakeSteamRoot);
        Assert.True(primaryProbe.HasAuthenticAchievementSound);
        Assert.Equal(Path.GetFullPath(primaryWav), primaryProbe.AchievementToastWavPath);
        Assert.Equal(Path.GetFullPath(primaryWav), primaryProbe.EffectiveSoundPath);
        Assert.Contains("Steam", primaryProbe.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
