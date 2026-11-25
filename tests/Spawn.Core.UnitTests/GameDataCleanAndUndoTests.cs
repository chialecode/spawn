using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Achievement;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class GameDataCleanAndUndoTests
{
    [Fact]
    public async Task GameData_PcGamingWikiRuleParserAndCache_ExpandsAllPlaceholdersAndPersistsSqliteRuleCache()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string installDir = sandbox.CreateSubDirectory("games", "StanleySynthetic");
        string testRegSubRoot = $"SpawnTests_{Guid.NewGuid():N}";
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\{testRegSubRoot}");

        // Create a UID subfolder under Steam\userdata\<uid>\1703340\remote to test {{p|uid}} wildcard expansion
        string steamUserRemote = Path.Combine(options.SteamInstallDirOverride!, "userdata", "76561198000012345", "1703340", "remote");
        Directory.CreateDirectory(steamUserRemote);

        const string sampleWikitext = """
            ===Save game data location===
            {{Game data
            |{{Game data/saves|Windows|{{p|locallow}}\Crows Crows Crows\The Stanley Parable_ Ultra Deluxe\}}
            |{{Game data/saves|Windows|{{p|appdata}}\Vampire_Survivors\SaveData.sav}}
            |{{Game data/saves|Windows|{{p|public}}\Documents\Steam\CODEX\1703340\remote\}}
            |{{Game data/saves|Windows|{{p|steam}}\userdata\{{p|uid}}\1703340\remote\}}
            |{{Game data/saves|Windows|{{p|game}}\save_game\}}
            |{{Game data/saves|Linux|{{p|xdgdata}}/StanleyParable}}
            }}
            ===Configuration file(s) location===
            {{Game data
            |{{Game data/config|Windows|{{p|hkcu}}\Software\Crows Crows Crows\The Stanley Parable: Ultra Deluxe}}
            |{{Game data/config|OS X|~/Library/Preferences/com.crowscrowscrows.stanley.plist}}
            }}
            """;

        var parsedRules = PcGamingWikiRuleParser.ParseWikitext(sampleWikitext);
        // Linux and OS X rows must be ignored; 5 Windows save rows + 1 Windows HKCU config row = 6 rules.
        Assert.Equal(6, parsedRules.Count);

        var expanded = parsedRules
            .SelectMany(r => PcGamingWikiRuleParser.ExpandTemplateRule(r, installDir, options))
            .ToList();

        Assert.Contains(
            expanded,
            x => x.EffectiveCategory == GameDataCategory.SaveData
                 && string.Equals(
                     x.ResolvedPathOrKey,
                     Path.Combine(options.LocalLowOverride!, "Crows Crows Crows", "The Stanley Parable_ Ultra Deluxe"),
                     StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            expanded,
            x => x.EffectiveCategory == GameDataCategory.SaveData
                 && string.Equals(
                     x.ResolvedPathOrKey,
                     Path.Combine(options.AppDataRoamingOverride!, "Vampire_Survivors", "SaveData.sav"),
                     StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            expanded,
            x => x.EffectiveCategory == GameDataCategory.SaveData
                 && string.Equals(
                     x.ResolvedPathOrKey,
                     Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX", "1703340", "remote"),
                     StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            expanded,
            x => x.EffectiveCategory == GameDataCategory.SaveData
                 && string.Equals(x.ResolvedPathOrKey, steamUserRemote, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            expanded,
            x => x.EffectiveCategory == GameDataCategory.SaveData
                 && string.Equals(x.ResolvedPathOrKey, Path.Combine(installDir, "save_game"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            expanded,
            x => x.EffectiveCategory == GameDataCategory.RegistryData
                 && string.Equals(
                     x.ResolvedPathOrKey,
                     $@"HKCU\Software\{testRegSubRoot}\Crows Crows Crows\The Stanley Parable: Ultra Deluxe",
                     StringComparison.OrdinalIgnoreCase));

        // Persist rule cache row in SQLite and verify offline InspectGameDataAsync consumes it without network calls
        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "The Stanley Parable: Ultra Deluxe",
            InstallDir = installDir,
            ExecutableRelPath = "Stanley.exe",
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 1703340,
        };
        await File.WriteAllBytesAsync(Path.Combine(installDir, "Stanley.exe"), new byte[1024]);
        await sandbox.Database.UpsertGameAsync(game);

        await sandbox.Database.UpsertGameDataRuleCacheAsync(new GameDataRuleCacheRecord
        {
            CacheKey = "pcgw:steam:1703340",
            GameId = game.GameId,
            SteamAppId = 1703340,
            Provider = "PCGamingWiki",
            RulesJson = JsonSerializer.Serialize(parsedRules),
            FetchedUtc = DateTimeOffset.UtcNow,
        });

        var cachedLookup = await sandbox.Database.GetGameDataRuleCacheByGameAsync(game.GameId, 1703340, null);
        Assert.NotNull(cachedLookup);

        var launchStub = new ControllableLaunchServiceStub();
        var service = new GameDataManagementService(
            sandbox.Database,
            sandbox.Paths,
            launchStub,
            options: options);

        var inspected = await service.InspectGameDataAsync(game.GameId, refreshOnlineRules: false);
        Assert.Contains(inspected, e => string.Equals(e.PathOrRegistryKey, steamUserRemote, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GameData_UnityAndSceneEmulatorDiscovery_DetectsColonToUnderscoreLocalLowIsolatedHkcuAndCodexRunePaths()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string testRegSubRoot = $"SpawnTests_{Guid.NewGuid():N}";
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\{testRegSubRoot}");

        // 1. Unity Mono + nested Plugins\x86_64\steam_api64.cdx + steam_emu.ini (CODEX 1703340)
        string stanleyDir = sandbox.CreateSubDirectory("games", "The Stanley Parable Ultra Deluxe");
        await File.WriteAllBytesAsync(Path.Combine(stanleyDir, "The Stanley Parable Ultra Deluxe.exe"), new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(stanleyDir, "UnityPlayer.dll"), new byte[1024]);
        string stanleyDataDir = Directory.CreateDirectory(Path.Combine(stanleyDir, "The Stanley Parable Ultra Deluxe_Data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(stanleyDataDir, "app.info"),
            "Crows Crows Crows\nThe Stanley Parable: Ultra Deluxe\n",
            Encoding.UTF8);
        string stanleyPluginsDir = Directory.CreateDirectory(Path.Combine(stanleyDataDir, "Plugins", "x86_64")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(stanleyPluginsDir, "steam_api64.dll"), [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(Path.Combine(stanleyPluginsDir, "steam_api64.cdx"), [0x4D, 0x5A]);
        await File.WriteAllTextAsync(
            Path.Combine(stanleyPluginsDir, "steam_emu.ini"),
            "[Settings]\nAppId=1703340\nUserName=CODEX\n",
            Encoding.UTF8);

        var stanleyRecord = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "The Stanley Parable: Ultra Deluxe",
            InstallDir = stanleyDir,
            ExecutableRelPath = "The Stanley Parable Ultra Deluxe.exe",
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 1703340,
            AchievementCaptureMode = AchievementCaptureMode.SteamEmulator,
        };
        await sandbox.Database.UpsertGameAsync(stanleyRecord);

        // 2. Unity IL2CPP + nested Plugins\x86_64\steam_api64.rne + steam_emu.ini (RUNE 1794680)
        string vsDir = sandbox.CreateSubDirectory("games", "Vampire Survivors");
        await File.WriteAllBytesAsync(Path.Combine(vsDir, "VampireSurvivors.exe"), new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(vsDir, "UnityPlayer.dll"), new byte[1024]);
        string vsDataDir = Directory.CreateDirectory(Path.Combine(vsDir, "VampireSurvivors_Data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(vsDataDir, "app.info"),
            "poncle\nVampire Survivors\n",
            Encoding.UTF8);
        string vsPluginsDir = Directory.CreateDirectory(Path.Combine(vsDataDir, "Plugins", "x86_64")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(vsPluginsDir, "steam_api64.dll"), [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(Path.Combine(vsPluginsDir, "steam_api64.rne"), [0x4D, 0x5A]);
        await File.WriteAllTextAsync(
            Path.Combine(vsPluginsDir, "steam_emu.ini"),
            "[Settings]\nAppId=1794680\nUserName=RUNE\n",
            Encoding.UTF8);

        var vsRecord = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Vampire Survivors",
            InstallDir = vsDir,
            ExecutableRelPath = "VampireSurvivors.exe",
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 1794680,
            AchievementCaptureMode = AchievementCaptureMode.SteamEmulator,
        };
        await sandbox.Database.UpsertGameAsync(vsRecord);

        var launchStub = new ControllableLaunchServiceStub();
        var service = new GameDataManagementService(sandbox.Database, sandbox.Paths, launchStub, options: options);

        var stanleyEntries = await service.InspectGameDataAsync(stanleyRecord.GameId);
        string expectedStanleyLocalLow = Path.Combine(
            options.LocalLowOverride!,
            "Crows Crows Crows",
            "The Stanley Parable_ Ultra Deluxe");
        string expectedStanleyReg = $@"HKCU\Software\{testRegSubRoot}\Crows Crows Crows\The Stanley Parable: Ultra Deluxe";
        string expectedCodexRemote = Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX", "1703340", "remote");
        string expectedCodexAch = Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX", "1703340", "achievements.ini");

        Assert.Contains(stanleyEntries, e => string.Equals(e.PathOrRegistryKey, expectedStanleyLocalLow, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stanleyEntries, e => string.Equals(e.PathOrRegistryKey, expectedStanleyReg, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stanleyEntries, e => string.Equals(e.PathOrRegistryKey, expectedCodexRemote, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stanleyEntries, e => string.Equals(e.PathOrRegistryKey, expectedCodexAch, StringComparison.OrdinalIgnoreCase));

        var vsEntries = await service.InspectGameDataAsync(vsRecord.GameId);
        string expectedVsRoaming = Path.Combine(options.AppDataRoamingOverride!, "Vampire_Survivors");
        string expectedRuneRemote = Path.Combine(options.PublicDocumentsOverride!, "Steam", "RUNE", "1794680", "remote");
        string expectedRuneAch = Path.Combine(options.PublicDocumentsOverride!, "Steam", "RUNE", "1794680", "achievements.ini");

        Assert.Contains(vsEntries, e => string.Equals(e.PathOrRegistryKey, expectedVsRoaming, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vsEntries, e => string.Equals(e.PathOrRegistryKey, expectedRuneRemote, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vsEntries, e => string.Equals(e.PathOrRegistryKey, expectedRuneAch, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GameData_SafetyFenceAndRunningGameGuard_BlocksProtectedTargetsInstallBinariesSharedRootsAndActiveSessions()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string testRegSubRoot = $"SpawnTests_{Guid.NewGuid():N}";
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\{testRegSubRoot}");
        string installDir = sandbox.CreateSubDirectory("games", "ProtectedGame");
        await File.WriteAllBytesAsync(Path.Combine(installDir, "Game.exe"), new byte[1024]);
        var identity = new GameDataTargetIdentity
        {
            Title = "Protected Game",
            SteamAppId = 1703340,
            ProductNames = ["Protected Game"],
            ExactNames = ["Game", "ProtectedGame"],
        };

        // 1. Filesystem safety fence assertions
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(installDir, installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "Game.exe"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "UnityPlayer.dll"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "Game_Data", "Plugins", "x86_64", "steam_api64.cdx"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "Game_Data", "Plugins", "x86_64", "steam_api64.rne"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "Game_Data"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "Engine", "Binaries"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "MonoBleedingEdge"), installDir, identity, sandbox.Paths, options).IsSafe);

        // Shared emulator roots without AppID subfolder must be blocked so sibling games are never wiped
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(options.PublicDocumentsOverride!, "Steam", "RUNE"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(options.AppDataRoamingOverride!, "GSE Saves"), installDir, identity, sandbox.Paths, options).IsSafe);

        // SPAWN's own data root & database must be blocked
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(sandbox.Paths.DataRoot, installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateFilesystemTarget(sandbox.Paths.DatabaseFilePath, installDir, identity, sandbox.Paths, options).IsSafe);

        // Allowed targets inside & outside installDir
        Assert.True(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(installDir, "save_game"), installDir, identity, sandbox.Paths, options).IsSafe);
        Assert.True(GameDataSafetyFence.ValidateFilesystemTarget(Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX", "1703340", "remote"), installDir, identity, sandbox.Paths, options).IsSafe);

        // 2. Registry safety fence assertions
        Assert.False(GameDataSafetyFence.ValidateRegistryTarget(@"HKLM\Software\Vendor\Game", options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateRegistryTarget(@"HKCU\Software", options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateRegistryTarget($@"HKCU\Software\{testRegSubRoot}\Microsoft\Windows", options).IsSafe);
        Assert.False(GameDataSafetyFence.ValidateRegistryTarget(@"HKCU\Software\UnisolatedRealVendor\Game", options).IsSafe);
        Assert.True(GameDataSafetyFence.ValidateRegistryTarget($@"HKCU\Software\{testRegSubRoot}\Crows Crows Crows\The Stanley Parable: Ultra Deluxe", options).IsSafe);

        // 3. Running-game guard blocks CleanGameDataAsync and RestoreLastCleanupAsync
        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Protected Active Game",
            InstallDir = installDir,
            ExecutableRelPath = "Game.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var launchStub = new ControllableLaunchServiceStub();
        launchStub.SetRunningSession(new ActiveGameSession
        {
            SessionId = SpawnId.NewUlid(),
            GameId = game.GameId,
            GameTitle = game.Title,
            InstallDir = game.InstallDir,
            AntiCheatTier = AntiCheatTier.Tier0,
            State = PlaySessionActivityState.Active,
            RootProcessId = Environment.ProcessId,
            StartedUtc = DateTimeOffset.UtcNow,
        });

        var service = new GameDataManagementService(sandbox.Database, sandbox.Paths, launchStub, options: options);
        var cleanRes = await service.CleanGameDataAsync(new GameDataCleanRequest
        {
            GameId = game.GameId,
            EntryIds = [],
            ResetSpawnAchievementsAndPlaytime = true,
            CreateUndoBackup = true,
        });
        Assert.False(cleanRes.Succeeded);

        var restoreRes = await service.RestoreLastCleanupAsync(game.GameId);
        Assert.False(restoreRes.Succeeded);
    }

    [Fact]
    public async Task GameData_CleanResetAndOneClickUndo_BacksUpToTrashClearsFilesRegistryAndLedgerThenRestores100Percent()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string testRegSubRoot = $"SpawnTests_{Guid.NewGuid():N}";
        string hkcuPrefix = $@"Software\{testRegSubRoot}";
        var options = CreateSandboxGameDataOptions(sandbox, hkcuPrefix);

        try
        {
            string stanleyDir = sandbox.CreateSubDirectory("games", "StanleyCleanUndo");
            await File.WriteAllBytesAsync(Path.Combine(stanleyDir, "The Stanley Parable Ultra Deluxe.exe"), new byte[2048]);
            await File.WriteAllBytesAsync(Path.Combine(stanleyDir, "UnityPlayer.dll"), new byte[1024]);
            string dataDir = Directory.CreateDirectory(Path.Combine(stanleyDir, "The Stanley Parable Ultra Deluxe_Data")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(dataDir, "app.info"),
                "Crows Crows Crows\nThe Stanley Parable: Ultra Deluxe\n",
                Encoding.UTF8);
            string pluginsDir = Directory.CreateDirectory(Path.Combine(dataDir, "Plugins", "x86_64")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(pluginsDir, "steam_api64.cdx"), [0x4D, 0x5A]);
            await File.WriteAllTextAsync(
                Path.Combine(pluginsDir, "steam_emu.ini"),
                "[Settings]\nAppId=1703340\nUserName=CODEX\n",
                Encoding.UTF8);

            // Seed real files in sandbox LocalLow, in-game portable `saves` folder, & CODEX 1703340
            string inGamePortableSaveDir = Directory.CreateDirectory(Path.Combine(stanleyDir, "saves")).FullName;
            string inGameQuickSaveFile = Path.Combine(inGamePortableSaveDir, "quick.sav");
            await File.WriteAllTextAsync(inGameQuickSaveFile, "PORTABLE_QUICKSAVE_PAYLOAD", Encoding.UTF8);

            string localLowSaveDir = Directory.CreateDirectory(
                Path.Combine(options.LocalLowOverride!, "Crows Crows Crows", "The Stanley Parable_ Ultra Deluxe")).FullName;
            string slot0File = Path.Combine(localLowSaveDir, "slot0.sav");
            string nestedPrefsFile = Path.Combine(Directory.CreateDirectory(Path.Combine(localLowSaveDir, "profile")).FullName, "prefs.json");
            byte[] slot0Bytes = "STANLEY_BUCKET_ENDING_SAVE_V1"u8.ToArray();
            await File.WriteAllBytesAsync(slot0File, slot0Bytes);
            await File.WriteAllTextAsync(nestedPrefsFile, """{"subtitles":true,"fov":90}""", Encoding.UTF8);

            string codexGameDir = Directory.CreateDirectory(
                Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX", "1703340")).FullName;
            string codexRemoteFile = Path.Combine(Directory.CreateDirectory(Path.Combine(codexGameDir, "remote")).FullName, "cloud.dat");
            string codexAchFile = Path.Combine(codexGameDir, "achievements.ini");
            await File.WriteAllTextAsync(codexRemoteFile, "CODEX_CLOUD_DATA", Encoding.UTF8);
            await File.WriteAllTextAsync(
                codexAchFile,
                """
                [SteamAchievements]
                Count=2
                00000=ACH_888
                00001=ACH_GO_OUTSIDE

                [ACH_888]
                Achieved=1
                UnlockTime=1731000100

                [ACH_GO_OUTSIDE]
                Achieved=1
                UnlockTime=1731000200
                """,
                Encoding.UTF8);

            // Seed isolated temporary HKCU registry values & child subkey
            string gameRegSubKey = $@"{hkcuPrefix}\Crows Crows Crows\The Stanley Parable: Ultra Deluxe";
            byte[] binaryBlob = [0xDE, 0xAD, 0xBE, 0xEF, 0x42];
            using (RegistryKey? regKey = Registry.CurrentUser.CreateSubKey(gameRegSubKey, writable: true))
            {
                Assert.NotNull(regKey);
                regKey!.SetValue("Screenmanager Fullscreen mode_h3630240806", 1, RegistryValueKind.DWord);
                regKey.SetValue("LastPlayedBranch", "ultra_deluxe", RegistryValueKind.String);
                regKey.SetValue("PlayerChecksum", binaryBlob, RegistryValueKind.Binary);

                using RegistryKey? childKey = regKey.CreateSubKey("Graphics", writable: true);
                Assert.NotNull(childKey);
                childKey!.SetValue("QualityPreset", 3, RegistryValueKind.DWord);
            }

            // Build DI container with Launcher + Achievement services wired to sandbox overrides
            var services = new ServiceCollection();
            services.AddSpawnCore(sandbox.Paths);
            services.AddSingleton(sandbox.Database);
            services.AddSpawnLauncher(
                scan => scan.AllowOnlineMetadataLookup = false,
                launch => launch.ForceCreateNoWindow = true,
                gd =>
                {
                    gd.AllowOnlinePcGamingWikiLookup = false;
                    gd.AppDataRoamingOverride = options.AppDataRoamingOverride;
                    gd.LocalAppDataOverride = options.LocalAppDataOverride;
                    gd.LocalLowOverride = options.LocalLowOverride;
                    gd.PublicDocumentsOverride = options.PublicDocumentsOverride;
                    gd.UserDocumentsOverride = options.UserDocumentsOverride;
                    gd.UserProfileOverride = options.UserProfileOverride;
                    gd.SavedGamesOverride = options.SavedGamesOverride;
                    gd.ProgramDataOverride = options.ProgramDataOverride;
                    gd.SteamInstallDirOverride = options.SteamInstallDirOverride;
                    gd.HkcuSoftwarePrefix = hkcuPrefix;
                });
            services.AddSpawnAchievement(ach =>
            {
                ach.AllowNetworkDownloads = false;
                ach.EnableGogCometAutoStart = false;
                ach.EnableCommunitySpoilerRequests = false;
                ach.EnvironmentOverrides = new AchievementEnvironmentOverrides
                {
                    AppDataRoaming = options.AppDataRoamingOverride,
                    LocalAppData = options.LocalAppDataOverride,
                    PublicDocuments = options.PublicDocumentsOverride,
                    UserDocuments = options.UserDocumentsOverride,
                    ProgramData = options.ProgramDataOverride,
                    SteamInstallPath = options.SteamInstallDirOverride,
                };
            });

            await using var provider = services.BuildServiceProvider();
            var library = provider.GetRequiredService<IGameLibraryService>();
            var achService = provider.GetRequiredService<IAchievementService>();
            var dataService = provider.GetRequiredService<IGameDataManagementService>();

            var imported = await library.ImportGameAsync(stanleyDir);
            Assert.NotNull(imported);

            // Seed schema + playtime ledger (3600s active, 120s background, 30s AFK)
            await sandbox.Database.UpsertAchievementSchemaAsync(
                imported!.GameId,
                "Steam_1703340",
                AchievementSchemaSource.LocalEmulatorSchema,
                "schinese",
                [
                    new AchievementDefinitionDto { ApiName = "ACH_888", DisplayName = "88888888", Description = "按够八次八", SortOrder = 0 },
                    new AchievementDefinitionDto { ApiName = "ACH_GO_OUTSIDE", DisplayName = "出门走走", Description = "十年不启动游戏", SortOrder = 1 },
                ]);
            await achService.ScanLocalUnlocksAsync(imported.GameId, silentSeed: true);

            var sessionStart = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
            await sandbox.Database.CompletePlaySessionAndAccumulateAsync(new PlaySessionRecord
            {
                SessionId = SpawnId.NewUlid(),
                GameId = imported.GameId,
                StartedUtc = sessionStart,
                EndedUtc = sessionStart.AddHours(1),
                ActiveSec = 3600,
                BackgroundSec = 120,
                AfkSec = 30,
                ExitCode = 0,
            });

            var gameBeforeClean = await sandbox.Database.GetGameByIdAsync(imported.GameId);
            var achBeforeClean = await achService.GetGameAchievementsAsync(imported.GameId);
            Assert.Equal(3600, gameBeforeClean!.TotalActiveSec);
            Assert.Equal(2, achBeforeClean.UnlockedCount);

            // 1. Inspect discovered entries -> select all existing safe entries
            var inspected = await dataService.InspectGameDataAsync(imported.GameId);
            var selectedIds = inspected.Where(e => e.Exists && e.IsSafeToClean).Select(e => e.EntryId).ToList();
            Assert.True(selectedIds.Count >= 4, $"Expected at least 4 existing entries (LocalLow, CODEX remote, CODEX ach, HKCU), got {selectedIds.Count}");

            // 2. Execute reversible Clean + Reset
            var cleanResult = await dataService.CleanGameDataAsync(new GameDataCleanRequest
            {
                GameId = imported.GameId,
                EntryIds = selectedIds,
                ResetSpawnAchievementsAndPlaytime = true,
                CreateUndoBackup = true,
            });

            Assert.True(cleanResult.Succeeded, cleanResult.Message);
            Assert.True(cleanResult.SpawnStateReset);
            Assert.NotNull(cleanResult.UndoSnapshot);
            Assert.True(Directory.Exists(cleanResult.UndoSnapshot!.BackupDirectory));
            Assert.StartsWith(sandbox.Paths.TrashDir, cleanResult.UndoSnapshot.BackupDirectory, StringComparison.OrdinalIgnoreCase);

            // Verify files, in-game portable `saves` dir, empty CODEX\<AppId> parent directory, registry key, playtime, and unlocks are ALL wiped
            Assert.False(Directory.Exists(inGamePortableSaveDir));
            Assert.False(Directory.Exists(localLowSaveDir));
            Assert.False(File.Exists(codexAchFile));
            Assert.False(Directory.Exists(codexGameDir));
            using (RegistryKey? deletedCheck = Registry.CurrentUser.OpenSubKey(gameRegSubKey, writable: false))
            {
                Assert.Null(deletedCheck);
            }

            var gameAfterClean = await sandbox.Database.GetGameByIdAsync(imported.GameId);
            var achAfterClean = await achService.GetGameAchievementsAsync(imported.GameId);
            var sessionsAfterClean = await sandbox.Database.GetPlaySessionsByGameAsync(imported.GameId);
            Assert.Equal(0, gameAfterClean!.TotalActiveSec);
            Assert.Equal(0, gameAfterClean.TotalPlaytimeSec);
            Assert.Null(gameAfterClean.LastPlayedUtc);
            Assert.Equal(0, achAfterClean.UnlockedCount);
            Assert.Empty(sessionsAfterClean);

            var latestUndo = await dataService.GetLatestUndoSnapshotAsync(imported.GameId);
            Assert.NotNull(latestUndo);
            Assert.Equal(cleanResult.UndoSnapshot.SnapshotId, latestUndo!.SnapshotId);

            // 3. Execute One-Click RestoreLastCleanupAsync -> 100% of files, registry keys, playtime, and unlocks return!
            var restoreResult = await dataService.RestoreLastCleanupAsync(imported.GameId);
            Assert.True(restoreResult.Succeeded, restoreResult.Message);

            Assert.True(File.Exists(inGameQuickSaveFile));
            Assert.True(File.Exists(slot0File));
            Assert.Equal(slot0Bytes, await File.ReadAllBytesAsync(slot0File));
            Assert.True(File.Exists(nestedPrefsFile));
            Assert.True(File.Exists(codexRemoteFile));
            Assert.True(File.Exists(codexAchFile));

            using (RegistryKey? restoredKey = Registry.CurrentUser.OpenSubKey(gameRegSubKey, writable: false))
            {
                Assert.NotNull(restoredKey);
                Assert.Equal(1, restoredKey!.GetValue("Screenmanager Fullscreen mode_h3630240806"));
                Assert.Equal("ultra_deluxe", restoredKey.GetValue("LastPlayedBranch"));
                Assert.Equal(binaryBlob, (byte[])restoredKey.GetValue("PlayerChecksum")!);

                using RegistryKey? restoredGraphics = restoredKey.OpenSubKey("Graphics", writable: false);
                Assert.NotNull(restoredGraphics);
                Assert.Equal(3, restoredGraphics!.GetValue("QualityPreset"));
            }

            var gameAfterRestore = await sandbox.Database.GetGameByIdAsync(imported.GameId);
            var achAfterRestore = await achService.GetGameAchievementsAsync(imported.GameId);
            var sessionsAfterRestore = await sandbox.Database.GetPlaySessionsByGameAsync(imported.GameId);
            Assert.Equal(3600, gameAfterRestore!.TotalActiveSec);
            Assert.Equal(120, gameAfterRestore.TotalBackgroundSec);
            Assert.Equal(30, gameAfterRestore.TotalAfkSec);
            Assert.Equal(2, achAfterRestore.UnlockedCount);
            Assert.Single(sessionsAfterRestore);
            Assert.Null(await dataService.GetLatestUndoSnapshotAsync(imported.GameId));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(hkcuPrefix, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task GameData_SafetyFenceOutsideInstallDir_AllowListRequiresGameSpecificSegmentAndBlocksSharedParents()
    {
        await using var sandbox = new TestSandbox();
        string testRegSubRoot = $"SpawnTests_{Guid.NewGuid():N}";
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\{testRegSubRoot}");
        string installDir = sandbox.CreateSubDirectory("games", "The Stanley Parable Ultra Deluxe");
        var identity = new GameDataTargetIdentity
        {
            Title = "The Stanley Parable: Ultra Deluxe",
            SteamAppId = 1703340,
            ProductNames = ["The Stanley Parable: Ultra Deluxe"],
            ExactNames = ["The Stanley Parable Ultra Deluxe"],
        };

        bool Safe(string path) => GameDataSafetyFence.ValidateFilesystemTarget(path, installDir, identity, sandbox.Paths, options).IsSafe;

        string myGames = Path.Combine(options.UserDocumentsOverride!, "My Games");
        string roaming = options.AppDataRoamingOverride!;
        string localLow = options.LocalLowOverride!;

        // Shared parents are never cleanable: My Games, AppData roots, LocalLow\<Vendor>, Saved Games, Steam\userdata.
        Assert.False(Safe(myGames));
        Assert.False(Safe(Path.Combine(myGames, "SomeOtherGame")));
        Assert.False(Safe(roaming));
        Assert.False(Safe(Path.Combine(roaming, "Microsoft")));
        Assert.False(Safe(Path.Combine(roaming, "Steam")));
        Assert.False(Safe(options.LocalAppDataOverride!));
        Assert.False(Safe(localLow));
        Assert.False(Safe(Path.Combine(localLow, "Crows Crows Crows")));
        Assert.False(Safe(Path.Combine(localLow, "Unity")));
        Assert.False(Safe(Path.Combine(options.SavedGamesOverride!, "Saves")));
        Assert.False(Safe(Path.Combine(options.SteamInstallDirOverride!, "userdata", "76561198000012345")));

        // Paths outside every known data root (other drives/folders) and wildcard targets are blocked.
        Assert.False(Safe(sandbox.CreateSubDirectory("elsewhere", "The Stanley Parable Ultra Deluxe")));
        Assert.False(Safe(Path.Combine(roaming, "*")));

        // Game-specific targets: Vendor\Game, title folder under My Games, AppID-scoped emulator / Steam userdata folders.
        Assert.True(Safe(Path.Combine(localLow, "Crows Crows Crows", "The Stanley Parable_ Ultra Deluxe")));
        Assert.True(Safe(Path.Combine(myGames, "The Stanley Parable Ultra Deluxe", "Saves")));
        Assert.True(Safe(Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX", "1703340", "remote")));
        Assert.True(Safe(Path.Combine(options.SteamInstallDirOverride!, "userdata", "76561198000012345", "1703340", "remote")));

        // A different AppID never matches.
        Assert.False(Safe(Path.Combine(options.PublicDocumentsOverride!, "Steam", "CODEX", "1794680")));

        // PCGamingWiki wildcards can never widen into a shared parent.
        Directory.CreateDirectory(Path.Combine(roaming, "Microsoft"));
        Directory.CreateDirectory(Path.Combine(roaming, "The Stanley Parable Ultra Deluxe"));
        var wildcardRules = PcGamingWikiRuleParser.ParseWikitext("""
            ===Save game data location===
            {{Game data
            |{{Game data/saves|Windows|{{p|documents}}\My Games\*}}
            |{{Game data/saves|Windows|{{p|appdata}}\{{p|uid}}}}
            |{{Game data/saves|Windows|{{p|appdata}}\*\Saves\}}
            |{{Game data/saves|Windows|{{p|locallow}}\Crows Crows Crows\*.sav}}
            |{{Game data/saves|Windows|{{p|documents}}\My Games\The Stanley Parable Ultra Deluxe\*.sav}}
            }}
            """);
        var expanded = wildcardRules
            .SelectMany(r => PcGamingWikiRuleParser.ExpandTemplateRule(r, installDir, options, identity))
            .Select(x => x.ResolvedPathOrKey)
            .ToList();

        Assert.DoesNotContain(expanded, p => string.Equals(p, myGames, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(expanded, p => string.Equals(p, Path.Combine(roaming, "Microsoft"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(expanded, p => string.Equals(p, Path.Combine(localLow, "Crows Crows Crows"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(expanded, p => p.Contains('*', StringComparison.Ordinal));
        Assert.Contains(expanded, p => string.Equals(p, Path.Combine(roaming, "The Stanley Parable Ultra Deluxe"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(expanded, p => string.Equals(p, Path.Combine(myGames, "The Stanley Parable Ultra Deluxe"), StringComparison.OrdinalIgnoreCase));
        Assert.All(expanded, p => Assert.True(Safe(p), $"Expanded wildcard path must pass the fence: {p}"));
    }

    [Fact]
    public async Task GameData_CleanInterruptedMidway_PendingJournalTracksEveryItemAndUndoRestoresRemovedOnes()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\SpawnTests_{Guid.NewGuid():N}");

        string installDir = sandbox.CreateSubDirectory("games", "JournalGame");
        await File.WriteAllBytesAsync(Path.Combine(installDir, "JournalGame.exe"), new byte[1024]);
        string saveGameDir = Directory.CreateDirectory(Path.Combine(installDir, "save_game")).FullName;
        string savesDir = Directory.CreateDirectory(Path.Combine(installDir, "saves")).FullName;
        string saveDataDir = Directory.CreateDirectory(Path.Combine(installDir, "savedata")).FullName;
        await File.WriteAllTextAsync(Path.Combine(saveGameDir, "slot1.sav"), "slot-1");
        await File.WriteAllTextAsync(Path.Combine(savesDir, "slot2.sav"), "slot-2");
        await File.WriteAllTextAsync(Path.Combine(saveDataDir, "slot3.sav"), "slot-3");

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Journal Game",
            InstallDir = installDir,
            ExecutableRelPath = "JournalGame.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var service = new GameDataManagementService(sandbox.Database, sandbox.Paths, new ControllableLaunchServiceStub(), options: options);
        var entries = await service.InspectGameDataAsync(game.GameId);
        Assert.Equal(3, entries.Count(e => e.IsSafeToClean && e.Exists));

        // "saves" stays readable (backup succeeds) but cannot be deleted -> simulates an interruption in the middle of the run.
        GameDataCleanResult cleanRes;
        using (new FileStream(Path.Combine(savesDir, "slot2.sav"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            cleanRes = await service.CleanGameDataAsync(new GameDataCleanRequest
            {
                GameId = game.GameId,
                EntryIds = entries.Select(e => e.EntryId).ToList(),
                CreateUndoBackup = true,
            });
        }

        Assert.True(cleanRes.Succeeded, cleanRes.Message);
        Assert.NotEmpty(cleanRes.Warnings);
        Assert.False(Directory.Exists(saveGameDir));
        Assert.False(Directory.Exists(saveDataDir));
        Assert.True(File.Exists(Path.Combine(savesDir, "slot2.sav")));

        // The journal records every item with its own durable state.
        string manifestPath = Path.Combine(cleanRes.UndoSnapshot!.BackupDirectory, "manifest.json");
        Assert.False(File.Exists(manifestPath + ".tmp"));
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
            var states = doc.RootElement.GetProperty("items").EnumerateArray()
                .ToDictionary(
                    i => Path.GetFileName(i.GetProperty("originalPathOrKey").GetString()!),
                    i => i.GetProperty("state").GetString(),
                    StringComparer.OrdinalIgnoreCase);
            Assert.Equal("removed", states["save_game"]);
            Assert.Equal("removed", states["savedata"]);
            Assert.Equal("backed-up", states["saves"]);
        }

        // Crash simulation: flip the journal back to "pending" (as left by a power loss) - undo must still restore everything.
        string pendingJson = (await File.ReadAllTextAsync(manifestPath)).Replace("\"completed\"", "\"pending\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(manifestPath, pendingJson);

        var restoreRes = await service.RestoreLastCleanupAsync(game.GameId);
        Assert.True(restoreRes.Succeeded, restoreRes.Message);
        Assert.Equal("slot-1", await File.ReadAllTextAsync(Path.Combine(saveGameDir, "slot1.sav")));
        Assert.Equal("slot-2", await File.ReadAllTextAsync(Path.Combine(savesDir, "slot2.sav")));
        Assert.Equal("slot-3", await File.ReadAllTextAsync(Path.Combine(saveDataDir, "slot3.sav")));
    }

    [Fact]
    public async Task GameData_SafetyFenceTokenEquivalenceAndAdversarialMatrix_EnforcesInitialismSequelSubtitleEditionAndSharedContainerInvariants()
    {
        await using var sandbox = new TestSandbox();
        string testRegSubRoot = $"SpawnTests_{Guid.NewGuid():N}";
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\{testRegSubRoot}");

        string docs = options.UserDocumentsOverride!;
        string localAppData = options.LocalAppDataOverride!;
        string roaming = options.AppDataRoamingOverride!;
        string localLow = options.LocalLowOverride!;

        string gta3DeInstall = sandbox.CreateSubDirectory("games", "GTA III - The Definitive Edition");
        var gta3DeIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Grand Theft Auto III – The Definitive Edition",
            InstallDir = gta3DeInstall,
            ExecutableRelPath = Path.Combine("Gameface", "Binaries", "Win64", "LibertyCity.exe"),
            SteamAppId = 1546970,
        });

        string gtaVcDeInstall = sandbox.CreateSubDirectory("games", "GTA Vice City - The Definitive Edition");
        var gtaVcDeIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Grand Theft Auto: Vice City – The Definitive Edition",
            InstallDir = gtaVcDeInstall,
            ExecutableRelPath = Path.Combine("Gameface", "Binaries", "Win64", "ViceCity.exe"),
            SteamAppId = 1546990,
        });

        string gtaSaDeInstall = sandbox.CreateSubDirectory("games", "GTA San Andreas - The Definitive Edition");
        var gtaSaDeIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Grand Theft Auto: San Andreas – The Definitive Edition",
            InstallDir = gtaSaDeInstall,
            ExecutableRelPath = Path.Combine("Gameface", "Binaries", "Win64", "SanAndreas.exe"),
            SteamAppId = 1547000,
        });

        bool SafeFor(GameDataTargetIdentity id, string install, string candidate) =>
            GameDataSafetyFence.ValidateFilesystemTarget(candidate, install, id, sandbox.Paths, options).IsSafe;

        // 1. Positive invariants: Rockstar abbreviated Documents & LocalAppData paths for all 3 GTA Definitive Edition titles.
        Assert.True(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition")));
        Assert.True(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition", "Profiles", "0F74F4C4")));
        Assert.True(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition", "Config", "WindowsNoEditor")));
        Assert.True(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition", "Adjustable.cfg")));
        Assert.True(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(localAppData, "Rockstar Games", "GTA III Definitive Edition", "Saved")));
        Assert.True(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(localAppData, "Rockstar Games", "GTA III Definitive Edition", "Saved", "Config", "WindowsNoEditor")));
        Assert.True(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "Grand Theft Auto 3 Definitive Edition", "Profiles", "0F74F4C4")));

        Assert.True(SafeFor(gtaVcDeIdentity, gtaVcDeInstall, Path.Combine(docs, "Rockstar Games", "GTA Vice City Definitive Edition")));
        Assert.True(SafeFor(gtaVcDeIdentity, gtaVcDeInstall, Path.Combine(docs, "Rockstar Games", "GTA Vice City Definitive Edition", "Profiles", "0F74F4C4")));
        Assert.True(SafeFor(gtaVcDeIdentity, gtaVcDeInstall, Path.Combine(localAppData, "Rockstar Games", "GTA Vice City Definitive Edition", "Saved")));

        Assert.True(SafeFor(gtaSaDeIdentity, gtaSaDeInstall, Path.Combine(docs, "Rockstar Games", "GTA San Andreas Definitive Edition")));
        Assert.True(SafeFor(gtaSaDeIdentity, gtaSaDeInstall, Path.Combine(docs, "Rockstar Games", "GTA San Andreas Definitive Edition", "Profiles", "0F74F4C4")));
        Assert.True(SafeFor(gtaSaDeIdentity, gtaSaDeInstall, Path.Combine(localAppData, "Rockstar Games", "GTA San Andreas Definitive Edition", "Saved")));

        // Localized CJK display title still matches via multi-token English install folder promotion.
        var cjkGta3DeIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "侠盗猎车手III：决定版",
            InstallDir = gta3DeInstall,
            ExecutableRelPath = Path.Combine("Gameface", "Binaries", "Win64", "LibertyCity.exe"),
        });
        Assert.True(SafeFor(cjkGta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition", "Profiles", "0F74F4C4")));

        // GTA V and Red Dead Redemption 2 positive initialism equivalence.
        string gta5Install = sandbox.CreateSubDirectory("games", "Grand Theft Auto V");
        var gta5Identity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Grand Theft Auto V",
            InstallDir = gta5Install,
            ExecutableRelPath = "GTA5.exe",
        });
        Assert.True(SafeFor(gta5Identity, gta5Install, Path.Combine(docs, "Rockstar Games", "GTA V", "Profiles", "DFE3B7FD")));

        string rdr2Install = sandbox.CreateSubDirectory("games", "Red Dead Redemption 2");
        var rdr2Identity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Red Dead Redemption 2",
            InstallDir = rdr2Install,
            ExecutableRelPath = "RDR2.exe",
        });
        Assert.True(SafeFor(rdr2Identity, rdr2Install, Path.Combine(docs, "Rockstar Games", "Red Dead Redemption 2", "Profiles", "ABCD1234")));
        Assert.True(SafeFor(rdr2Identity, rdr2Install, Path.Combine(docs, "Rockstar Games", "RDR 2", "Profiles", "ABCD1234")));

        // 2. Adversarial non-match invariants: publisher container roots & generic leaf folders -> ALL MUST BE FALSE!
        string[] publisherContainers =
        [
            "Rockstar Games",
            "Capcom",
            "Ubisoft",
            "Square Enix",
            "EA Games",
            "Electronic Arts",
            "FromSoftware",
            "Bandai Namco",
            "My Games",
        ];
        foreach (string pub in publisherContainers)
        {
            Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, pub)), $"Publisher root must be blocked: {pub}");
            Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(localAppData, pub)), $"LocalAppData publisher root must be blocked: {pub}");
        }

        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(roaming, "Goldberg SocialClub Emu Saves")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(localAppData, "Gameface", "Saved", "SaveGames")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(localAppData, "UE4Game", "Saved", "SaveGames")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Saved")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Profiles")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Config")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Config", "WindowsNoEditor")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "Profiles")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(localAppData, "Rockstar Games", "Saved")));

        // 3. Adversarial non-match invariants: sibling sequels & subtitles -> ALL MUST BE FALSE!
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA Vice City Definitive Edition")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA San Andreas Definitive Edition")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA V")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA IV")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "GTA 2")));
        Assert.False(SafeFor(gtaVcDeIdentity, gtaVcDeInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition")));
        Assert.False(SafeFor(gtaVcDeIdentity, gtaVcDeInstall, Path.Combine(docs, "Rockstar Games", "GTA San Andreas Definitive Edition")));

        // 4. Adversarial non-match invariants: 2001/2004 originals vs Definitive / Enhanced editions (both directions!).
        string gta3ClassicInstall = sandbox.CreateSubDirectory("games", "Grand Theft Auto III");
        var gta3ClassicIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Grand Theft Auto III",
            InstallDir = gta3ClassicInstall,
            ExecutableRelPath = "gta3.exe",
            SteamAppId = 12100,
        });
        var gta3ClassicAbbrevIdentity = new GameDataTargetIdentity
        {
            Title = "GTA III",
            ProductNames = ["GTA III", "Grand Theft Auto III"],
            ExactNames = ["gta3"],
        };

        Assert.False(SafeFor(gta3ClassicIdentity, gta3ClassicInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition")));
        Assert.False(SafeFor(gta3ClassicAbbrevIdentity, gta3ClassicInstall, Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "Grand Theft Auto III")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "GTA3 User Files")));
        Assert.False(SafeFor(gtaSaDeIdentity, gtaSaDeInstall, Path.Combine(docs, "GTA San Andreas User Files")));
        Assert.False(SafeFor(gtaSaDeIdentity, gtaSaDeInstall, Path.Combine(docs, "Rockstar Games", "San Andreas")));
        Assert.False(SafeFor(gtaVcDeIdentity, gtaVcDeInstall, Path.Combine(docs, "Rockstar Games", "Vice City")));
        Assert.False(SafeFor(gtaVcDeIdentity, gtaVcDeInstall, Path.Combine(docs, "My Games", "Vice City")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(docs, "Rockstar Games", "Liberty City")));
        Assert.False(SafeFor(gta5Identity, gta5Install, Path.Combine(docs, "Rockstar Games", "GTA V Enhanced")));

        // Remaster executable stems (Crysis.exe, Mafia2.exe) MUST NOT bypass RemasterMarkers veto via ExactNames!
        string crysisRemasteredInstall = sandbox.CreateSubDirectory("games", "Crysis Remastered");
        var crysisRemasteredIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Crysis Remastered",
            InstallDir = crysisRemasteredInstall,
            ExecutableRelPath = Path.Combine("Bin64", "Crysis.exe"),
        });
        Assert.False(SafeFor(crysisRemasteredIdentity, crysisRemasteredInstall, Path.Combine(docs, "My Games", "Crysis")));
        Assert.True(SafeFor(crysisRemasteredIdentity, crysisRemasteredInstall, Path.Combine(docs, "My Games", "Crysis Remastered")));

        string mafia2DeInstall = sandbox.CreateSubDirectory("games", "Mafia II Definitive Edition");
        var mafia2DeIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Mafia II: Definitive Edition",
            InstallDir = mafia2DeInstall,
            ExecutableRelPath = Path.Combine("pc", "Mafia2.exe"),
        });
        Assert.False(SafeFor(mafia2DeIdentity, mafia2DeInstall, Path.Combine(docs, "My Games", "Mafia 2")));
        Assert.False(SafeFor(mafia2DeIdentity, mafia2DeInstall, Path.Combine(docs, "My Games", "Mafia2")));
        Assert.True(SafeFor(mafia2DeIdentity, mafia2DeInstall, Path.Combine(docs, "My Games", "Mafia II Definitive Edition")));

        // Engine runtime / wrapper executable stems (renpy.exe, godot.exe, love.exe, PlayGTA3.exe) & shared engine containers -> ALL FALSE!
        string renpyGameInstall = sandbox.CreateSubDirectory("games", "Doki Synthetic Club");
        var renpyIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Doki Synthetic Club",
            InstallDir = renpyGameInstall,
            ExecutableRelPath = "renpy.exe",
        });
        Assert.False(SafeFor(renpyIdentity, renpyGameInstall, Path.Combine(roaming, "RenPy")));
        Assert.True(SafeFor(renpyIdentity, renpyGameInstall, Path.Combine(roaming, "RenPy", "Doki Synthetic Club")));

        string godotGameInstall = sandbox.CreateSubDirectory("games", "Brotato Synthetic");
        var godotIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Brotato Synthetic",
            InstallDir = godotGameInstall,
            ExecutableRelPath = "godot.exe",
        });
        Assert.False(SafeFor(godotIdentity, godotGameInstall, Path.Combine(roaming, "Godot")));
        Assert.False(SafeFor(godotIdentity, godotGameInstall, Path.Combine(roaming, "Godot", "app_userdata")));
        Assert.True(SafeFor(godotIdentity, godotGameInstall, Path.Combine(roaming, "Godot", "app_userdata", "Brotato Synthetic")));

        string loveGameInstall = sandbox.CreateSubDirectory("games", "Balatro Synthetic");
        var loveIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Balatro Synthetic",
            InstallDir = loveGameInstall,
            ExecutableRelPath = "love.exe",
        });
        Assert.False(SafeFor(loveIdentity, loveGameInstall, Path.Combine(roaming, "LOVE")));

        // Unrelated two-word parent folder name on an English-titled game must never pollute ExactNames or ProductNames.
        string unrelatedParentInstall = sandbox.CreateSubDirectory("games", "Epic Games");
        var celesteInUnrelatedDirIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Celeste",
            InstallDir = unrelatedParentInstall,
            ExecutableRelPath = "Celeste.exe",
        });
        Assert.False(SafeFor(celesteInUnrelatedDirIdentity, unrelatedParentInstall, Path.Combine(localAppData, "Epic Games")));
        Assert.False(SafeFor(celesteInUnrelatedDirIdentity, unrelatedParentInstall, Path.Combine(localAppData, "EpicGamesLauncher")));

        string customTwoWordInstall = sandbox.CreateSubDirectory("games", "Custom Bundle Folder");
        var celesteInCustomTwoWordIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Celeste",
            InstallDir = customTwoWordInstall,
            ExecutableRelPath = "Celeste.exe",
        });
        Assert.False(SafeFor(celesteInCustomTwoWordIdentity, customTwoWordInstall, Path.Combine(docs, "My Games", "Custom Bundle Folder")));

        // Symmetric RDR -> Red Dead Redemption abbreviation expansion.
        Assert.Equal("Red Dead Redemption 2", GameTitleCleaner.ExpandSeriesAbbreviations("RDR 2"));
        Assert.Equal("Red Dead Redemption 2", GameTitleCleaner.ExpandSeriesAbbreviations("RDR2"));
        Assert.Equal("Red Dead Redemption II", GameTitleCleaner.ExpandSeriesAbbreviations("RDR II"));

        // Subtitle extension protection (e.g. Vice City Stories != Vice City, FF VII Rebirth != FF VII, Stanley Parable 2013 != Ultra Deluxe).
        string ff7RebirthInstall = sandbox.CreateSubDirectory("games", "Final Fantasy VII Rebirth");
        var ff7RebirthIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Final Fantasy VII Rebirth",
            InstallDir = ff7RebirthInstall,
            ExecutableRelPath = "ff7rebirth.exe",
        });
        Assert.False(SafeFor(ff7RebirthIdentity, ff7RebirthInstall, Path.Combine(docs, "My Games", "Final Fantasy VII")));

        string stanley2013Install = sandbox.CreateSubDirectory("games", "The Stanley Parable");
        var stanley2013Identity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "The Stanley Parable",
            InstallDir = stanley2013Install,
            ExecutableRelPath = "stanley.exe",
        });
        Assert.False(SafeFor(stanley2013Identity, stanley2013Install, Path.Combine(localLow, "Crows Crows Crows", "The Stanley Parable_ Ultra Deluxe")));

        // 5. Adversarial non-match invariants: shared multi-game Social Club Emu container GTA_DEF -> MUST BE FALSE!
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(roaming, "Goldberg SocialClub Emu Saves", "GTA_DEF")));
        Assert.False(SafeFor(gta3DeIdentity, gta3DeInstall, Path.Combine(roaming, "Goldberg SocialClub Emu Saves", "GTA_DEF", "0F74F4C4")));

        // 6. Adversarial non-match invariants: short 1-token acronyms, article-stripped single words, and ambiguous 2-letter acronyms.
        string gta1997Install = sandbox.CreateSubDirectory("games", "Grand Theft Auto");
        var gta1997Identity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Grand Theft Auto",
            InstallDir = gta1997Install,
            ExecutableRelPath = "gta.exe",
        });
        Assert.False(SafeFor(gta1997Identity, gta1997Install, Path.Combine(docs, "GTA")));

        string shortTitleInstall = sandbox.CreateSubDirectory("games", "YS");
        var shortTitleIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "YS",
            InstallDir = shortTitleInstall,
            ExecutableRelPath = "ys.exe",
        });
        Assert.False(SafeFor(shortTitleIdentity, shortTitleInstall, Path.Combine(roaming, "YS")));

        string theCrewInstall = sandbox.CreateSubDirectory("games", "The Crew");
        var theCrewIdentity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "The Crew",
            InstallDir = theCrewInstall,
            ExecutableRelPath = "TheCrew.exe",
        });
        Assert.False(SafeFor(theCrewIdentity, theCrewInstall, Path.Combine(roaming, "Crew")));

        string darkSouls2Install = sandbox.CreateSubDirectory("games", "Dark Souls II");
        var darkSouls2Identity = GameDataTargetIdentity.FromGame(new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Dark Souls II",
            InstallDir = darkSouls2Install,
            ExecutableRelPath = "DarkSoulsII.exe",
        });
        Assert.False(SafeFor(darkSouls2Identity, darkSouls2Install, Path.Combine(roaming, "DS2")));

        // 7. PCGamingWiki {{p|uid}} wildcard expansion passes Rockstar game-specific folder and blocks shared GTA_DEF.
        string gta3ProfileUid = Directory.CreateDirectory(
            Path.Combine(docs, "Rockstar Games", "GTA III Definitive Edition", "Profiles", "0F74F4C4")).FullName;
        Directory.CreateDirectory(Path.Combine(roaming, "Goldberg SocialClub Emu Saves", "GTA_DEF", "0F74F4C4"));

        var pcgwRules = PcGamingWikiRuleParser.ParseWikitext("""
            {{Game data
            |{{Game data/saves|Windows|{{p|documents}}\Rockstar Games\GTA III Definitive Edition\Profiles\{{p|uid}}\}}
            |{{Game data/saves|Windows|{{p|appdata}}\Goldberg SocialClub Emu Saves\GTA_DEF\{{p|uid}}\}}
            }}
            """);
        var expanded = pcgwRules
            .SelectMany(r => PcGamingWikiRuleParser.ExpandTemplateRule(r, gta3DeInstall, options, gta3DeIdentity))
            .Select(x => x.ResolvedPathOrKey)
            .ToList();

        Assert.Single(expanded);
        Assert.Equal(gta3ProfileUid, expanded[0], ignoreCase: true);
    }

    [Fact]
    public async Task GameData_RockstarUnrealCustomPublisherDiscoveryCleanAndUndo_SeparatesSavesFromConfigsProtectsSharedGtaDefAndSiblingGames()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\SpawnTests_{Guid.NewGuid():N}");

        // Build synthetic Unreal Engine Gameface directory for GTA III Definitive Edition
        string installDir = sandbox.CreateSubDirectory("games", "GTA III - The Definitive Edition");
        Directory.CreateDirectory(Path.Combine(installDir, "Engine", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(installDir, "Gameface", "Content", "Paks"));
        string win64Dir = Directory.CreateDirectory(Path.Combine(installDir, "Gameface", "Binaries", "Win64")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(installDir, "title.rgl"), [0x52, 0x47, 0x4C, 0x4D]);
        await File.WriteAllBytesAsync(Path.Combine(win64Dir, "LibertyCity.exe"), new byte[8192]);
        await File.WriteAllBytesAsync(Path.Combine(win64Dir, "socialclub.dll"), [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(Path.Combine(win64Dir, "Deftones.dll"), [0x4D, 0x5A]);

        // Seed Rockstar custom Documents & LocalAppData trees for GTA III DE
        string docGameDir = Directory.CreateDirectory(
            Path.Combine(options.UserDocumentsOverride!, "Rockstar Games", "GTA III Definitive Edition")).FullName;
        string profileUidDir = Directory.CreateDirectory(Path.Combine(docGameDir, "Profiles", "0F74F4C4")).FullName;
        string saveFile = Path.Combine(profileUidDir, "GTASAsf1.sav");
        byte[] savePayload = "GTA3_DE_SLOT1_MISSION_LUIGI"u8.ToArray();
        await File.WriteAllBytesAsync(saveFile, savePayload);

        string docConfigWinNoEditor = Directory.CreateDirectory(Path.Combine(docGameDir, "Config", "WindowsNoEditor")).FullName;
        string docUserSettingsIni = Path.Combine(docConfigWinNoEditor, "GameUserSettings.ini");
        await File.WriteAllTextAsync(docUserSettingsIni, "[ScalabilityGroups]\nsg.ResolutionQuality=100\n", Encoding.UTF8);

        string adjustableCfg = Path.Combine(docGameDir, "Adjustable.cfg");
        await File.WriteAllTextAsync(adjustableCfg, "Contrast=0.5\nBrightness=0.5\n", Encoding.UTF8);

        string docLogsDir = Directory.CreateDirectory(Path.Combine(docGameDir, "Logs", "NGX")).FullName;
        string ngxLogFile = Path.Combine(docLogsDir, "ngx_init.log");
        await File.WriteAllTextAsync(ngxLogFile, "NGX initialized", Encoding.UTF8);

        string localSavedDir = Directory.CreateDirectory(
            Path.Combine(options.LocalAppDataOverride!, "Rockstar Games", "GTA III Definitive Edition", "Saved")).FullName;
        string localSavedConfigDir = Directory.CreateDirectory(Path.Combine(localSavedDir, "Config", "WindowsNoEditor")).FullName;
        string localEngineIni = Path.Combine(localSavedConfigDir, "Engine.ini");
        await File.WriteAllTextAsync(localEngineIni, "[Core.System]\nPaths=../../../Gameface/Content\n", Encoding.UTF8);
        string localLogsDir = Directory.CreateDirectory(Path.Combine(localSavedDir, "Logs")).FullName;
        string localLogFile = Path.Combine(localLogsDir, "Gameface.log");
        await File.WriteAllTextAsync(localLogFile, "LogInit: Display: Running engine", Encoding.UTF8);

        // Seed sibling game's save (GTA Vice City Definitive Edition) & shared multi-game Social Club Emu container (GTA_DEF)
        string siblingVcSaveFile = Path.Combine(
            Directory.CreateDirectory(Path.Combine(options.UserDocumentsOverride!, "Rockstar Games", "GTA Vice City Definitive Edition", "Profiles", "0F74F4C4")).FullName,
            "VC_sf1.sav");
        await File.WriteAllTextAsync(siblingVcSaveFile, "VICE_CITY_MUST_NEVER_BE_TOUCHED", Encoding.UTF8);

        string sharedGtaDefDir = Directory.CreateDirectory(
            Path.Combine(options.AppDataRoamingOverride!, "Goldberg SocialClub Emu Saves", "GTA_DEF")).FullName;
        string sharedGtaDefFile = Path.Combine(
            Directory.CreateDirectory(Path.Combine(sharedGtaDefDir, "0F74F4C4")).FullName,
            "achievements.dat");
        await File.WriteAllTextAsync(sharedGtaDefFile, "SHARED_TRILOGY_SOCIALCLUB_STATE", Encoding.UTF8);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Grand Theft Auto III – The Definitive Edition",
            SortTitle = "Grand Theft Auto III – The Definitive Edition",
            InstallDir = installDir,
            ExecutableRelPath = Path.Combine("Gameface", "Binaries", "Win64", "LibertyCity.exe"),
            PlatformSource = PlatformSource.Standalone,
            SteamAppId = 1546970,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var service = new GameDataManagementService(
            sandbox.Database,
            sandbox.Paths,
            new ControllableLaunchServiceStub(),
            options: options);

        // 1. InspectGameDataAsync discovers Rockstar custom Save vs Config/Logs, skips phantom Gameface, and marks GTA_DEF IsSafeToClean = false.
        var entries = await service.InspectGameDataAsync(game.GameId);
        Assert.DoesNotContain(entries, e => e.PathOrRegistryKey.Contains(Path.Combine("LocalAppData", "Gameface"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.PathOrRegistryKey.Contains("Vice City", StringComparison.OrdinalIgnoreCase));

        var saveEntry = Assert.Single(entries, e => string.Equals(e.PathOrRegistryKey, profileUidDir, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GameDataCategory.SaveData, saveEntry.Category);
        Assert.True(saveEntry.IsSafeToClean);
        Assert.True(saveEntry.DefaultSelected);
        Assert.True(saveEntry.Exists);

        var docConfigEntry = Assert.Single(entries, e => string.Equals(e.PathOrRegistryKey, docConfigWinNoEditor, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GameDataCategory.ConfigAndLogs, docConfigEntry.Category);
        Assert.True(docConfigEntry.IsSafeToClean);
        Assert.False(docConfigEntry.DefaultSelected);

        var cfgFileEntry = Assert.Single(entries, e => string.Equals(e.PathOrRegistryKey, adjustableCfg, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GameDataCategory.ConfigAndLogs, cfgFileEntry.Category);
        Assert.True(cfgFileEntry.IsSafeToClean);
        Assert.False(cfgFileEntry.DefaultSelected);

        var localConfigEntry = Assert.Single(entries, e => string.Equals(e.PathOrRegistryKey, Path.Combine(localSavedDir, "Config"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GameDataCategory.ConfigAndLogs, localConfigEntry.Category);
        Assert.True(localConfigEntry.IsSafeToClean);
        Assert.False(localConfigEntry.DefaultSelected);

        var sharedGtaDefEntry = Assert.Single(entries, e => string.Equals(e.PathOrRegistryKey, sharedGtaDefDir, StringComparison.OrdinalIgnoreCase));
        Assert.False(sharedGtaDefEntry.IsSafeToClean);
        Assert.False(sharedGtaDefEntry.DefaultSelected);
        Assert.NotNull(sharedGtaDefEntry.SafetyNote);

        // 2. EnumerateSaveLocationsAsync returns ONLY safe SaveData (excludes ConfigAndLogs, Logs, and shared GTA_DEF).
        var saveLocations = await service.EnumerateSaveLocationsAsync(game.GameId);
        Assert.Single(saveLocations);
        Assert.Equal(profileUidDir, saveLocations[0].PathOrRegistryKey, ignoreCase: true);

        // 3. CleanGameDataAsync (even if caller passes ALL entry IDs including protected GTA_DEF) cleans GTA III data,
        //    preserves Rockstar Games container, sibling Vice City saves, and shared GTA_DEF, and supports 100% Undo restore!
        var cleanRes = await service.CleanGameDataAsync(new GameDataCleanRequest
        {
            GameId = game.GameId,
            EntryIds = entries.Select(e => e.EntryId).ToList(),
            ResetSpawnAchievementsAndPlaytime = false,
            CreateUndoBackup = true,
        });

        Assert.True(cleanRes.Succeeded, cleanRes.Message);
        Assert.NotEmpty(cleanRes.Warnings);
        Assert.False(File.Exists(saveFile));
        Assert.False(File.Exists(docUserSettingsIni));
        Assert.False(File.Exists(adjustableCfg));
        Assert.False(File.Exists(ngxLogFile));
        Assert.False(File.Exists(localEngineIni));
        Assert.False(Directory.Exists(docGameDir));
        Assert.True(Directory.Exists(Path.Combine(options.UserDocumentsOverride!, "Rockstar Games")));

        // Sibling game and shared Social Club container MUST be 100% untouched!
        Assert.True(File.Exists(siblingVcSaveFile));
        Assert.Equal("VICE_CITY_MUST_NEVER_BE_TOUCHED", await File.ReadAllTextAsync(siblingVcSaveFile));
        Assert.True(File.Exists(sharedGtaDefFile));
        Assert.Equal("SHARED_TRILOGY_SOCIALCLUB_STATE", await File.ReadAllTextAsync(sharedGtaDefFile));

        // ValidateSaveRestoreTargetAsync still permits restoring into the deleted Rockstar save path while blocking GTA_DEF & Vice City.
        Assert.True((await service.ValidateSaveRestoreTargetAsync(game.GameId, saveFile)).IsSafe);
        Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, siblingVcSaveFile)).IsSafe);
        Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, sharedGtaDefFile)).IsSafe);

        // 4. One-click RestoreLastCleanupAsync restores 100% of saves, configs, and logs!
        var restoreRes = await service.RestoreLastCleanupAsync(game.GameId);
        Assert.True(restoreRes.Succeeded, restoreRes.Message);
        Assert.True(File.Exists(saveFile));
        Assert.Equal(savePayload, await File.ReadAllBytesAsync(saveFile));
        Assert.True(File.Exists(docUserSettingsIni));
        Assert.True(File.Exists(adjustableCfg));
        Assert.True(File.Exists(ngxLogFile));
        Assert.True(File.Exists(localEngineIni));
        Assert.True(File.Exists(localLogFile));

        // 5. Verify InspectGameDataAsync on Crysis Remastered (Bin64\Crysis.exe) never discovers classic Documents\My Games\Crysis,
        //    and pre-first-launch abbreviated "RDR 2" resolves Documents\Rockstar Games\Red Dead Redemption 2\Profiles!
        string classicCrysisDir = Directory.CreateDirectory(Path.Combine(options.UserDocumentsOverride!, "My Games", "Crysis")).FullName;
        await File.WriteAllTextAsync(Path.Combine(classicCrysisDir, "classic.sav"), "2007_CRYSIS_SAVE", Encoding.UTF8);

        string crysisRemDir = sandbox.CreateSubDirectory("games", "Crysis Remastered");
        Directory.CreateDirectory(Path.Combine(crysisRemDir, "Bin64"));
        await File.WriteAllBytesAsync(Path.Combine(crysisRemDir, "Bin64", "Crysis.exe"), new byte[4096]);
        var crysisRemGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Crysis Remastered",
            InstallDir = crysisRemDir,
            ExecutableRelPath = Path.Combine("Bin64", "Crysis.exe"),
            PlatformSource = PlatformSource.Standalone,
        };
        await sandbox.Database.UpsertGameAsync(crysisRemGame);

        var crysisEntries = await service.InspectGameDataAsync(crysisRemGame.GameId);
        Assert.DoesNotContain(crysisEntries, e => string.Equals(e.PathOrRegistryKey, classicCrysisDir, StringComparison.OrdinalIgnoreCase));

        string rdr2AbbrevDir = sandbox.CreateSubDirectory("games", "RDR 2");
        await File.WriteAllBytesAsync(Path.Combine(rdr2AbbrevDir, "RDR2.exe"), new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(rdr2AbbrevDir, "title.rgl"), [0x52, 0x47, 0x4C, 0x4D]);
        var rdr2AbbrevGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "RDR 2",
            InstallDir = rdr2AbbrevDir,
            ExecutableRelPath = "RDR2.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        await sandbox.Database.UpsertGameAsync(rdr2AbbrevGame);

        var rdr2Entries = await service.InspectGameDataAsync(rdr2AbbrevGame.GameId);
        Assert.Contains(
            rdr2Entries,
            e => string.Equals(
                e.PathOrRegistryKey,
                Path.Combine(options.UserDocumentsOverride!, "Rockstar Games", "Red Dead Redemption 2", "Profiles"),
                StringComparison.OrdinalIgnoreCase) && e.IsSafeToClean);
    }

    [Fact]
    public async Task GameData_NtfsDirectoryJunctionProtection_BlocksRootNestedAndToctouJunctionsAndNeverDeletesVaultSlotTarget()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        var options = CreateSandboxGameDataOptions(sandbox, $@"Software\SpawnTests_{Guid.NewGuid():N}");

        string vaultSlotDir = sandbox.CreateSubDirectory("vault_slot", "Slot_MainProfile");
        string preciousSaveFile = Path.Combine(vaultSlotDir, "precious_slot.sav");
        await File.WriteAllTextAsync(preciousSaveFile, "PRECIOUS_VAULT_SLOT_MUST_SURVIVE", Encoding.UTF8);

        try
        {
            string installDir = sandbox.CreateSubDirectory("games", "GTA III - The Definitive Edition");
            await File.WriteAllBytesAsync(Path.Combine(installDir, "title.rgl"), [0x52, 0x47, 0x4C, 0x4D]);
            string win64Dir = Directory.CreateDirectory(Path.Combine(installDir, "Gameface", "Binaries", "Win64")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(win64Dir, "LibertyCity.exe"), new byte[4096]);

            var game = new GameRecord
            {
                GameId = SpawnId.NewUlid(),
                Title = "Grand Theft Auto III – The Definitive Edition",
                InstallDir = installDir,
                ExecutableRelPath = Path.Combine("Gameface", "Binaries", "Win64", "LibertyCity.exe"),
                PlatformSource = PlatformSource.Standalone,
                SteamAppId = 1546970,
            };
            await sandbox.Database.UpsertGameAsync(game);

            string docGameDir = Directory.CreateDirectory(
                Path.Combine(options.UserDocumentsOverride!, "Rockstar Games", "GTA III Definitive Edition")).FullName;
            string profilesJunction = Path.Combine(docGameDir, "Profiles");

            // Case 1: Profiles itself is an NTFS Directory Junction pointing to vaultSlotDir -> never marked cleanable!
            Assert.True(CreateDirectoryJunction(profilesJunction, vaultSlotDir));

            // Also seed a PCGamingWiki rule pointing directly to Profiles so CandidateListBuilder.TryAdd sees the junction path too.
            await sandbox.Database.UpsertGameDataRuleCacheAsync(new GameDataRuleCacheRecord
            {
                CacheKey = "pcgw:steam:1546970",
                GameId = game.GameId,
                SteamAppId = 1546970,
                Provider = "PCGamingWiki",
                RulesJson = JsonSerializer.Serialize(new[]
                {
                    new PcGamingWikiRawRule(GameDataCategory.SaveData, @"{{p|documents}}\Rockstar Games\GTA III Definitive Edition\Profiles"),
                }),
                FetchedUtc = DateTimeOffset.UtcNow,
            });

            var service = new GameDataManagementService(
                sandbox.Database,
                sandbox.Paths,
                new ControllableLaunchServiceStub(),
                options: options);

            var inspectedRootJunction = await service.InspectGameDataAsync(game.GameId);
            var rootJunctionEntry = Assert.Single(
                inspectedRootJunction,
                e => string.Equals(e.PathOrRegistryKey, profilesJunction, StringComparison.OrdinalIgnoreCase));
            Assert.False(rootJunctionEntry.IsSafeToClean);
            Assert.False(rootJunctionEntry.DefaultSelected);

            var cleanRootJunction = await service.CleanGameDataAsync(new GameDataCleanRequest
            {
                GameId = game.GameId,
                EntryIds = [rootJunctionEntry.EntryId],
                ResetSpawnAchievementsAndPlaytime = false,
                CreateUndoBackup = true,
            });
            Assert.False(cleanRootJunction.Succeeded);
            Assert.True(File.Exists(preciousSaveFile));

            // Unlink Profiles junction strictly via non-recursive Directory.Delete(recursive: false)!
            Directory.Delete(profilesJunction, recursive: false);
            Assert.True(File.Exists(preciousSaveFile));

            // Case 2: Regular Profiles\0F74F4C4 directory containing a nested NTFS Directory Junction -> blocked!
            string uidDir = Directory.CreateDirectory(Path.Combine(profilesJunction, "0F74F4C4")).FullName;
            await File.WriteAllTextAsync(Path.Combine(uidDir, "local.sav"), "LOCAL_SAVE", Encoding.UTF8);
            string nestedJunction = Path.Combine(uidDir, "MountedSlot");
            Assert.True(CreateDirectoryJunction(nestedJunction, vaultSlotDir));

            var inspectedNested = await service.InspectGameDataAsync(game.GameId);
            Assert.All(
                inspectedNested.Where(e => e.PathOrRegistryKey.StartsWith(profilesJunction, StringComparison.OrdinalIgnoreCase)),
                e => Assert.False(e.IsSafeToClean));

            // Unlink nested junction cleanly, inspect while clean (IsSafeToClean == true), then plant TOCTOU junction before CleanGameDataAsync!
            Directory.Delete(nestedJunction, recursive: false);
            var cleanInspection = await service.InspectGameDataAsync(game.GameId);
            var cleanUidEntry = Assert.Single(
                cleanInspection,
                e => string.Equals(e.PathOrRegistryKey, uidDir, StringComparison.OrdinalIgnoreCase));
            Assert.True(cleanUidEntry.IsSafeToClean);

            Assert.True(CreateDirectoryJunction(nestedJunction, vaultSlotDir));
            var toctouClean = await service.CleanGameDataAsync(new GameDataCleanRequest
            {
                GameId = game.GameId,
                EntryIds = [cleanUidEntry.EntryId],
                ResetSpawnAchievementsAndPlaytime = false,
                CreateUndoBackup = true,
            });
            Assert.False(toctouClean.Succeeded);
            Assert.True(File.Exists(preciousSaveFile));
            Assert.Equal("PRECIOUS_VAULT_SLOT_MUST_SURVIVE", await File.ReadAllTextAsync(preciousSaveFile));
        }
        finally
        {
            // Detach any test junctions non-recursively BEFORE TestSandbox.Dispose cleans temp tree!
            string docGameDir = Path.Combine(options.UserDocumentsOverride!, "Rockstar Games", "GTA III Definitive Edition");
            string nested = Path.Combine(docGameDir, "Profiles", "0F74F4C4", "MountedSlot");
            if (Directory.Exists(nested) && (File.GetAttributes(nested) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(nested, recursive: false);
            }

            string prof = Path.Combine(docGameDir, "Profiles");
            if (Directory.Exists(prof) && (File.GetAttributes(prof) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(prof, recursive: false);
            }
        }
    }

    private static bool CreateDirectoryJunction(string linkPath, string targetPath)
    {
        string? parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null)
        {
            return false;
        }

        proc.WaitForExit(5000);
        return proc.ExitCode == 0
               && Directory.Exists(linkPath)
               && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
    }

    private static GameDataManagementOptions CreateSandboxGameDataOptions(TestSandbox sandbox, string hkcuPrefix) =>
        new()
        {
            AllowOnlinePcGamingWikiLookup = false,
            AppDataRoamingOverride = sandbox.CreateSubDirectory("env", "AppDataRoaming"),
            LocalAppDataOverride = sandbox.CreateSubDirectory("env", "LocalAppData"),
            LocalLowOverride = sandbox.CreateSubDirectory("env", "LocalLow"),
            PublicDocumentsOverride = sandbox.CreateSubDirectory("env", "Public", "Documents"),
            UserDocumentsOverride = sandbox.CreateSubDirectory("env", "UserDocs"),
            UserProfileOverride = sandbox.CreateSubDirectory("env", "UserProfile"),
            SavedGamesOverride = sandbox.CreateSubDirectory("env", "SavedGames"),
            ProgramDataOverride = sandbox.CreateSubDirectory("env", "ProgramData"),
            SteamInstallDirOverride = sandbox.CreateSubDirectory("env", "Steam"),
            HkcuSoftwarePrefix = hkcuPrefix,
        };

    private sealed class ControllableLaunchServiceStub : IGameLaunchService
    {
        private readonly Dictionary<string, ActiveGameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions => _sessions;

        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
        {
            add { }
            remove { }
        }

        public void SetRunningSession(ActiveGameSession session) => _sessions[session.GameId] = session;

        public ActiveGameSession? GetActiveSession(string gameId) =>
            _sessions.TryGetValue(gameId, out var s) ? s : null;

        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameLaunchResult.Failure("STUB", "Not used in unit test."));

        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId)
        {
        }

        public void UnregisterTrustedOverlayWindow(nint windowHandle)
        {
        }
    }
}
