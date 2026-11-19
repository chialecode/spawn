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
