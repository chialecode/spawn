using System.Diagnostics;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress.Saves.Rules;
using Spawn.Modules.Progress.UnitTests.Snapshots;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Saves;

public sealed class LudusaviRuleResolverTests
{
    [Fact]
    public void Expander_All16WindowsPlaceholders_ExpandDeterministically()
    {
        using var sandbox = new SnapshotTestSandbox();
        var (folders, steamRoot, steamInstallDir) = CreateSyntheticEnvironment(sandbox);

        // Create real files across all placeholder targets inside the temp sandbox.
        string baseFile = WriteTempFile(Path.Combine(steamInstallDir, "Saves", "portable.sav"), "base");
        string homeFile = WriteTempFile(Path.Combine(folders.Home, "Saved Games", "TestGame", "home.sav"), "home");
        string appDataFile = WriteTempFile(Path.Combine(folders.WinAppData, "TestGame", "roam.sav"), "roam");
        string localFile = WriteTempFile(Path.Combine(folders.WinLocalAppData, "TestGame", "local.sav"), "local");
        string localLowFile = WriteTempFile(Path.Combine(folders.WinLocalAppDataLow, "Studio", "TestGame", "low.dat"), "low");
        string docsFile = WriteTempFile(Path.Combine(folders.WinDocuments, "My Games", "TestGame", "doc.sav"), "doc");
        string publicFile = WriteTempFile(Path.Combine(folders.WinPublic, "Documents", "Steam", "CODEX", "12345", "pub.sav"), "pub");
        string progDataFile = WriteTempFile(Path.Combine(folders.WinProgramData, "Steam", "RLD!", "12345", "pd.sav"), "pd");
        string winDirFile = WriteTempFile(Path.Combine(folders.WinDir, "legacy_test.ini"), "win");
        string osUserFile = WriteTempFile(Path.Combine(folders.WinDocuments, "Profiles", "TestPilot", "slot.sav"), "user");
        string steamUserFile = WriteTempFile(Path.Combine(steamRoot, "userdata", "100042", "12345", "remote", "cloud.sav"), "steam");
        string xdgFallbackFile = WriteTempFile(Path.Combine(folders.WinLocalAppData, "xdg_game", "save.bin"), "xdg");
        string globMatchFile = WriteTempFile(Path.Combine(steamInstallDir, "Nested", "Act1", "slot_01.sav"), "glob");

        var steamGame = CreateGameRecord("g-steam", "Test Game", steamInstallDir, steamAppId: 12345);
        var standaloneInstallDir = sandbox.CreateSubDirectory("portable", "StandaloneGame");
        string standaloneFile = WriteTempFile(Path.Combine(standaloneInstallDir, "Saves", "s1.sav"), "standalone");
        var standaloneGame = CreateGameRecord("g-standalone", "Standalone Game", standaloneInstallDir);

        var expander = new LudusaviPlaceholderExpander(folders);

        // 1. <base>
        Assert.Contains(baseFile, expander.ExpandTemplate("<base>/Saves/portable.sav", steamGame));
        // 2 & 3. <root> & <game> (Steam library & standalone <root>/<game> -> <base>)
        Assert.Contains(baseFile, expander.ExpandTemplate("<root>/<game>/Saves/portable.sav", steamGame));
        Assert.Contains(baseFile, expander.ExpandTemplate("<root>/steamapps/common/<game>/Saves/portable.sav", steamGame));
        Assert.Contains(standaloneFile, expander.ExpandTemplate("<root>/<game>/Saves/s1.sav", standaloneGame));
        // 4. <home>
        Assert.Contains(homeFile, expander.ExpandTemplate("<home>/Saved Games/TestGame/home.sav", steamGame));
        // 5. <winAppData>
        Assert.Contains(appDataFile, expander.ExpandTemplate("<winAppData>/TestGame/roam.sav", steamGame));
        // 6. <winLocalAppData>
        Assert.Contains(localFile, expander.ExpandTemplate("<winLocalAppData>/TestGame/local.sav", steamGame));
        // 7. <winLocalAppDataLow>
        Assert.Contains(localLowFile, expander.ExpandTemplate("<winLocalAppDataLow>/Studio/TestGame/low.dat", steamGame));
        // 8. <winDocuments>
        Assert.Contains(docsFile, expander.ExpandTemplate("<winDocuments>/My Games/TestGame/doc.sav", steamGame));
        // 9. <winPublic>
        Assert.Contains(publicFile, expander.ExpandTemplate("<winPublic>/Documents/Steam/CODEX/12345/pub.sav", steamGame));
        // 10. <winProgramData>
        Assert.Contains(progDataFile, expander.ExpandTemplate("<winProgramData>/Steam/RLD!/12345/pd.sav", steamGame));
        // 11. <winDir>
        Assert.Contains(winDirFile, expander.ExpandTemplate("<winDir>/legacy_test.ini", steamGame));
        // 12. <osUserName>
        Assert.Contains(osUserFile, expander.ExpandTemplate("<winDocuments>/Profiles/<osUserName>/slot.sav", steamGame));
        // 13. <storeUserId>
        Assert.Contains(steamUserFile, expander.ExpandTemplate("<root>/userdata/<storeUserId>/12345/remote/cloud.sav", steamGame));
        // 14 & 15. <xdgData> / <xdgConfig>: ignored when Windows alternative exists; falls back to %LOCALAPPDATA% otherwise
        Assert.Empty(expander.ExpandTemplate("<xdgData>/xdg_game/save.bin", steamGame, hasWindowsNativeAlternative: true));
        Assert.Empty(expander.ExpandTemplate("<xdgConfig>/xdg_game/save.bin", steamGame, hasWindowsNativeAlternative: true));
        Assert.Contains(xdgFallbackFile, expander.ExpandTemplate("<xdgData>/xdg_game/save.bin", steamGame, hasWindowsNativeAlternative: false));
        Assert.Contains(xdgFallbackFile, expander.ExpandTemplate("<xdgConfig>/xdg_game/save.bin", steamGame, hasWindowsNativeAlternative: false));
        // 16. * / ** glob
        Assert.Contains(globMatchFile, expander.ExpandTemplate("<base>/Nested/**/slot_*.sav", steamGame));
    }

    [Fact]
    public void Expander_StoreUserId_EnumeratesSteamGoldbergAndGbeForkSortedByRecentActivity()
    {
        using var sandbox = new SnapshotTestSandbox();
        var (folders, steamRoot, steamInstallDir) = CreateSyntheticEnvironment(sandbox);

        // Steam userdata accounts (two different users with different timestamps).
        string olderSteamSave = WriteTempFile(Path.Combine(steamRoot, "userdata", "10001", "582010", "remote", "SAVEDATA1000"), "old");
        string newerSteamSave = WriteTempFile(Path.Combine(steamRoot, "userdata", "20002", "582010", "remote", "SAVEDATA1000"), "new");
        DateTime oldUtc = DateTime.UtcNow.AddDays(-5);
        DateTime newUtc = DateTime.UtcNow.AddMinutes(-2);
        File.SetLastWriteTimeUtc(olderSteamSave, oldUtc);
        Directory.SetLastWriteTimeUtc(Path.Combine(steamRoot, "userdata", "10001", "582010", "remote"), oldUtc);
        Directory.SetLastWriteTimeUtc(Path.Combine(steamRoot, "userdata", "10001", "582010"), oldUtc);
        Directory.SetLastWriteTimeUtc(Path.Combine(steamRoot, "userdata", "10001"), oldUtc);
        File.SetLastWriteTimeUtc(newerSteamSave, newUtc);
        Directory.SetLastWriteTimeUtc(Path.Combine(steamRoot, "userdata", "20002", "582010", "remote"), newUtc);
        Directory.SetLastWriteTimeUtc(Path.Combine(steamRoot, "userdata", "20002", "582010"), newUtc);
        Directory.SetLastWriteTimeUtc(Path.Combine(steamRoot, "userdata", "20002"), newUtc);

        // Goldberg + gbe_fork custom IDs under %APPDATA%.
        WriteTempFile(
            Path.Combine(folders.WinAppData, "Goldberg SteamEmu Saves", "settings", "user_steam_id.txt"),
            "76561198000011111");
        WriteTempFile(
            Path.Combine(folders.WinAppData, "GSE Saves", "settings", "configs.user.ini"),
            "[user::general]\naccount_name=Pilot\naccount_steamid=76561198000022222\n");

        // Multi-user Elden Ring folders under %APPDATA%\EldenRing\<uid>\ER0000.sl2.
        string erOld = WriteTempFile(Path.Combine(folders.WinAppData, "EldenRing", "76561198000011111", "ER0000.sl2"), "er-old");
        string erActive = WriteTempFile(Path.Combine(folders.WinAppData, "EldenRing", "76561198000022222", "ER0000.sl2"), "er-active");
        string erGoldberg = WriteTempFile(Path.Combine(folders.WinAppData, "EldenRing", WindowsKnownFolderResolver.DefaultGoldbergUserId, "ER0000.sl2"), "er-gb");
        File.SetLastWriteTimeUtc(erOld, oldUtc);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(erOld)!, oldUtc);
        File.SetLastWriteTimeUtc(erGoldberg, oldUtc.AddDays(-1));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(erGoldberg)!, oldUtc.AddDays(-1));
        File.SetLastWriteTimeUtc(erActive, newUtc);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(erActive)!, newUtc);

        var game = CreateGameRecord("g-er", "Elden Ring", steamInstallDir, steamAppId: 1245620);
        var expander = new LudusaviPlaceholderExpander(folders);

        IReadOnlyList<string> erExpanded = expander.ExpandTemplate("<winAppData>/EldenRing/<storeUserId>/*.sl2", game);
        Assert.Equal(3, erExpanded.Count);
        Assert.Equal(erActive, erExpanded[0]); // Most recently modified active branch first.
        Assert.Contains(erOld, erExpanded);
        Assert.Contains(erGoldberg, erExpanded);

        IReadOnlyList<string> steamDiscovered = WindowsKnownFolderResolver.DiscoverStoreUserIds([steamRoot], folders.WinAppData);
        Assert.Contains("10001", steamDiscovered);
        Assert.Contains("20002", steamDiscovered);
        Assert.Contains("76561198000011111", steamDiscovered);
        Assert.Contains("76561198000022222", steamDiscovered);
        Assert.Contains(WindowsKnownFolderResolver.DefaultGoldbergUserId, steamDiscovered);

        var mhwGame = CreateGameRecord("g-mhw", "Monster Hunter: World", steamInstallDir, steamAppId: 582010);
        IReadOnlyList<string> steamExpanded = expander.ExpandTemplate("<root>/userdata/<storeUserId>/582010/remote/SAVEDATA1000", mhwGame);
        Assert.Contains(newerSteamSave, steamExpanded);
        Assert.Contains(olderSteamSave, steamExpanded);
        Assert.Equal(newerSteamSave, steamExpanded[0]);
    }

    [Fact]
    public void Expander_GlobAndDirectoryMeasurement_SkipsShaderCacheCrashDumpsLogsReparsePointsAndBacktracking()
    {
        using var sandbox = new SnapshotTestSandbox();
        var (folders, _, steamInstallDir) = CreateSyntheticEnvironment(sandbox);

        string savesRoot = Path.Combine(steamInstallDir, "Saves");
        string validSave1 = WriteTempFile(Path.Combine(savesRoot, "Slot1", "hero.sav"), "valid-1");
        string validSave2 = WriteTempFile(Path.Combine(savesRoot, "Slot2", "Deep", "boss.sav"), "valid-2");
        string shaderCacheFile = WriteTempFile(Path.Combine(savesRoot, "ShaderCache", "pso.sav"), "cache-junk");
        string crashDumpFile = WriteTempFile(Path.Combine(savesRoot, "CrashDumps", "crash.sav"), "crash-junk");
        string logsFile = WriteTempFile(Path.Combine(savesRoot, "Logs", "game.sav"), "log-junk");

        // External target directory linked via NTFS Directory Junction inside Saves\LinkedTrap.
        string externalDir = sandbox.CreateSubDirectory("external-trap");
        string externalSecret = WriteTempFile(Path.Combine(externalDir, "escaped.sav"), "must-not-follow");
        string junctionPath = Path.Combine(savesRoot, "LinkedTrap");
        bool createdJunction = SnapshotTestSandbox.TryCreateDirectoryJunction(junctionPath, externalDir);
        Assert.True(createdJunction, "Expected mklink /J to create an NTFS directory junction in temp sandbox.");

        var game = CreateGameRecord("g-glob", "Glob Game", steamInstallDir);
        var expander = new LudusaviPlaceholderExpander(folders);

        IReadOnlyList<string> matched = expander.ExpandTemplate("<base>/Saves/**/*.sav", game);
        Assert.Equal(2, matched.Count);
        Assert.Contains(validSave1, matched);
        Assert.Contains(validSave2, matched);
        Assert.DoesNotContain(shaderCacheFile, matched);
        Assert.DoesNotContain(crashDumpFile, matched);
        Assert.DoesNotContain(logsFile, matched);
        Assert.DoesNotContain(matched, p => p.Contains("LinkedTrap", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(externalSecret, matched);

        // MeasurePathSkippingIgnored must also skip ShaderCache, CrashDumps, Logs, and ReparsePoints.
        var (exists, fileCount, totalBytes) = LudusaviPlaceholderExpander.MeasurePathSkippingIgnored(savesRoot);
        Assert.True(exists);
        Assert.Equal(2, fileCount);
        Assert.Equal("valid-1".Length + "valid-2".Length, totalBytes);

        // Zero-backtracking linear wildcard stress test (would catastrophically backtrack in naive regex).
        string adversarialText = new string('a', 1000) + "c";
        string adversarialPattern = "*a*a*a*a*a*a*a*a*a*a*a*a*b";
        var sw = Stopwatch.StartNew();
        bool matchResult = LudusaviPlaceholderExpander.WildcardMatchLinear(adversarialPattern, adversarialText);
        sw.Stop();
        Assert.False(matchResult);
        Assert.True(sw.ElapsedMilliseconds < 200, $"Linear wildcard matcher took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void CanonicalizeAndExpand_RoundTripsAllRootsAndRegistryKeysBidirectionally()
    {
        using var sandbox = new SnapshotTestSandbox();
        var (folders, _, steamInstallDir) = CreateSyntheticEnvironment(sandbox);
        var game = CreateGameRecord("g-rt", "RoundTrip Game", steamInstallDir);
        var expander = new LudusaviPlaceholderExpander(folders);

        (string FullPath, string ExpectedLogical)[] cases =
        [
            (Path.Combine(steamInstallDir, "Saves", "slot0.sav"), "<base>/Saves/slot0.sav"),
            (Path.Combine(folders.WinAppData, "EldenRing", "76561198000000001", "ER0000.sl2"), "<winAppData>/EldenRing/76561198000000001/ER0000.sl2"),
            (Path.Combine(folders.WinLocalAppData, "Pal", "Saved", "SaveGames", "Level.sav"), "<winLocalAppData>/Pal/Saved/SaveGames/Level.sav"),
            (Path.Combine(folders.WinLocalAppDataLow, "Team Cherry", "Hollow Knight", "user1.dat"), "<winLocalAppDataLow>/Team Cherry/Hollow Knight/user1.dat"),
            (Path.Combine(folders.WinDocuments, "The Witcher 3", "gamesaves", "AutoSave.sav"), "<winDocuments>/The Witcher 3/gamesaves/AutoSave.sav"),
            (Path.Combine(folders.WinPublic, "Documents", "Steam", "RUNE", "42", "save.bin"), "<winPublic>/Documents/Steam/RUNE/42/save.bin"),
            (Path.Combine(folders.WinProgramData, "Steam", "RLD!", "42", "save.bin"), "<winProgramData>/Steam/RLD!/42/save.bin"),
            (Path.Combine(folders.WinDir, "game.ini"), "<winDir>/game.ini"),
            (Path.Combine(folders.Home, "Saved Games", "Cyberpunk 2077", "sav.dat"), "<home>/Saved Games/Cyberpunk 2077/sav.dat"),
        ];

        foreach (var (fullPath, expectedLogical) in cases)
        {
            string logical = expander.CanonicalizeToLogicalPath(fullPath, game);
            Assert.Equal(expectedLogical, logical);

            // Idempotent when called again on an already-canonical logical path.
            Assert.Equal(expectedLogical, expander.CanonicalizeToLogicalPath(logical, game));

            // Round-trip expansion restores the exact normalized Windows path.
            string expanded = expander.ExpandLogicalPath(logical, game);
            Assert.Equal(Path.GetFullPath(fullPath), expanded, ignoreCase: true);
        }

        // Registry round-trip: HKCU\... and HKEY_CURRENT_USER\... -> registry://HKCU/... -> HKCU\...
        string regLogical1 = expander.CanonicalizeToLogicalPath(@"HKCU\Software\Team Cherry\Hollow Knight", game);
        string regLogical2 = expander.CanonicalizeToLogicalPath(@"HKEY_CURRENT_USER\Software\Team Cherry\Hollow Knight", game);
        Assert.Equal("registry://HKCU/Software/Team Cherry/Hollow Knight", regLogical1);
        Assert.Equal(regLogical1, regLogical2);
        Assert.Equal(@"HKCU\Software\Team Cherry\Hollow Knight", expander.ExpandLogicalPath(regLogical1, game));
    }

    [Fact]
    public async Task Resolver_BuiltInSeedAndDualFormatJsonImport_PersistsAndFiltersCleanly()
    {
        using var sandbox = new SnapshotTestSandbox();
        var (folders, _, _) = CreateSyntheticEnvironment(sandbox);
        var fakeDataService = CreatePermissiveSandboxGameDataService(sandbox.RootDirectory, _ => []);
        var resolver = new LudusaviRuleResolver(sandbox.Database, fakeDataService, folders);

        // 1. Built-in seed is rich (>= 35 games) and idempotent.
        int seeded = await resolver.EnsureBuiltInRulesSeededAsync();
        Assert.True(seeded >= 35, $"Expected at least 35 built-in rules, got {seeded}.");
        Assert.Equal(seeded, await resolver.GetRuleCountAsync());

        int secondCall = await resolver.EnsureBuiltInRulesSeededAsync();
        Assert.Equal(seeded, secondCall);
        Assert.Equal(seeded, await resolver.GetRuleCountAsync());

        // 2. Import SPAWN rule JSON array (custom game + GOG ID).
        const string spawnArrayJson = """
            [
              {
                "ruleName": "Synthetic Chronicles",
                "steamAppId": 990001,
                "gogId": "880001",
                "aliases": ["Synth Chron"],
                "savePaths": ["<winAppData>/SyntheticChronicles/Saves/**"],
                "configPaths": ["<winAppData>/SyntheticChronicles/settings.ini"],
                "registryRules": ["HKEY_CURRENT_USER\\Software\\SynthStudio\\Chronicles", "HKLM\\Software\\Blocked\\Key"]
              }
            ]
            """;

        int arrayImported = await resolver.ImportRulesJsonAsync(spawnArrayJson);
        Assert.Equal(1, arrayImported);
        Assert.Equal(seeded + 1, await resolver.GetRuleCountAsync());

        LudusaviRuleRecord? byGog = await sandbox.Database.FindLudusaviRuleAsync(gogId: "880001");
        Assert.NotNull(byGog);
        Assert.Equal("Synthetic Chronicles", byGog.RuleName);
        Assert.Single(byGog.SavePaths);
        Assert.Single(byGog.ConfigPaths);
        // HKLM must be stripped, HKCU normalized.
        Assert.Equal([@"HKCU\Software\SynthStudio\Chronicles"], byGog.RegistryRules);

        // 3. Import Ludusavi-style manifest JSON object with when/tags/alias filtering.
        const string ludusaviManifestJson = """
            {
              "Arcane Odyssey": {
                "steam": { "id": 990002 },
                "gog": { "id": 880002 },
                "files": {
                  "<winLocalAppDataLow>/ArcaneDev/ArcaneOdyssey/slot*.sav": {
                    "tags": ["save"],
                    "when": [{ "os": "windows" }]
                  },
                  "<winLocalAppDataLow>/ArcaneDev/ArcaneOdyssey/graphics.cfg": {
                    "tags": ["config"],
                    "when": [{ "os": "windows" }]
                  },
                  "<xdgData>/ArcaneOdyssey/linux_only.sav": {
                    "tags": ["save"],
                    "when": [{ "os": "linux" }]
                  },
                  "<home>/Library/Application Support/ArcaneOdyssey/mac.sav": {
                    "tags": ["save"],
                    "when": [{ "os": "macos" }]
                  },
                  "<base>/portable.sav": {
                    "tags": ["save"]
                  }
                },
                "registry": {
                  "HKEY_CURRENT_USER/Software/ArcaneDev/ArcaneOdyssey": {
                    "tags": ["save"],
                    "when": [{ "os": "windows" }]
                  },
                  "HKEY_CURRENT_USER/Software/ArcaneDev/ArcaneOdysseyConfigOnly": {
                    "tags": ["config"],
                    "when": [{ "os": "windows" }]
                  },
                  "HKEY_LOCAL_MACHINE/Software/ArcaneDev/Forbidden": {
                    "tags": ["save"],
                    "when": [{ "os": "windows" }]
                  }
                }
              },
              "Arcane Odyssey Deluxe": {
                "alias": "Arcane Odyssey"
              }
            }
            """;

        int manifestImported = await resolver.ImportRulesJsonAsync(ludusaviManifestJson);
        Assert.Equal(1, manifestImported);

        LudusaviRuleRecord? byAlias = await sandbox.Database.FindLudusaviRuleAsync(title: "Arcane Odyssey Deluxe");
        Assert.NotNull(byAlias);
        Assert.Equal("Arcane Odyssey", byAlias.RuleName);
        Assert.Equal(990002, byAlias.SteamAppId);
        Assert.Equal("880002", byAlias.GogId);
        Assert.Equal(2, byAlias.SavePaths.Count);
        Assert.Contains("<winLocalAppDataLow>/ArcaneDev/ArcaneOdyssey/slot*.sav", byAlias.SavePaths);
        Assert.Contains("<base>/portable.sav", byAlias.SavePaths);
        Assert.DoesNotContain(byAlias.SavePaths, p => p.Contains("linux_only", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(byAlias.SavePaths, p => p.Contains("mac.sav", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["<winLocalAppDataLow>/ArcaneDev/ArcaneOdyssey/graphics.cfg"], byAlias.ConfigPaths);
        Assert.Equal([@"HKCU\Software\ArcaneDev\ArcaneOdyssey"], byAlias.RegistryRules);

        // Re-running EnsureBuiltInRulesSeededAsync must NOT clobber custom imported rules.
        int afterReseed = await resolver.EnsureBuiltInRulesSeededAsync();
        Assert.Equal(seeded + 2, afterReseed);
        Assert.NotNull(await sandbox.Database.FindLudusaviRuleAsync(steamAppId: 990002));
    }

    [Fact]
    public async Task Resolver_ResolveGameSaveLayout_SeparatesSaveConfigRegistryAndMergesWithoutDuplicates()
    {
        using var sandbox = new SnapshotTestSandbox();
        var (folders, _, steamInstallDir) = CreateSyntheticEnvironment(sandbox);

        // Prepare Hollow Knight game in DB (matched via dirty bilingual title + Steam AppID).
        string hkInstallDir = Path.Combine(Path.GetDirectoryName(steamInstallDir)!, "Hollow Knight");
        Directory.CreateDirectory(hkInstallDir);
        var hkGame = CreateGameRecord("g-hk", "空洞骑士 / Hollow Knight [v1.5.78 SteamRIP]", hkInstallDir, steamAppId: 367520);
        await sandbox.Database.UpsertGameAsync(hkGame);

        // Create save, config, and junk files in LocalAppDataLow.
        string hkDir = Path.Combine(folders.WinLocalAppDataLow, "Team Cherry", "Hollow Knight");
        string saveFile1 = WriteTempFile(Path.Combine(hkDir, "user1.dat"), "knight-save-slot-1");
        string saveBak = WriteTempFile(Path.Combine(hkDir, "user1.dat.bak1"), "knight-backup");
        string configFile = WriteTempFile(Path.Combine(hkDir, "Config.ini"), "vsync=1");

        // Additional PCGamingWiki + SteamEmulator + Achievement + Unsafe entries from fake IGameDataManagementService.
        string emuSaveDir = Path.Combine(folders.WinAppData, "GSE Saves", "367520", "remote");
        string emuSaveFile = WriteTempFile(Path.Combine(emuSaveDir, "user2.dat"), "emu-save");
        string achFile = WriteTempFile(Path.Combine(folders.WinAppData, "GSE Saves", "367520", "achievements.json"), "{}");
        string unsafeExe = WriteTempFile(Path.Combine(hkInstallDir, "hollow_knight.exe"), "MZ");

        // Junction inside emulator dir that must be skipped if reported.
        string junctionTarget = sandbox.CreateSubDirectory("outside-target");
        WriteTempFile(Path.Combine(junctionTarget, "trap.dat"), "trap");
        string junctionEntry = Path.Combine(folders.WinAppData, "HollowKnightJunction");
        Assert.True(SnapshotTestSandbox.TryCreateDirectoryJunction(junctionEntry, junctionTarget));

        var fakeEntries = new List<GameDataEntryDto>
        {
            // Duplicate of Ludusavi save file -> must NOT produce duplicate entry!
            new()
            {
                EntryId = "e-dup-save",
                GameId = hkGame.GameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.PCGamingWiki,
                PathOrRegistryKey = saveFile1,
                Exists = true,
                IsSafeToClean = true,
                FileCount = 1,
                TotalBytes = 18,
            },
            // Duplicate of Ludusavi registry key (written in HKEY_CURRENT_USER form) -> must deduplicate!
            new()
            {
                EntryId = "e-dup-reg",
                GameId = hkGame.GameId,
                Category = GameDataCategory.RegistryData,
                SourceRule = GameDataSourceRule.UnityEngine,
                PathOrRegistryKey = @"HKEY_CURRENT_USER\Software\Team Cherry\Hollow Knight",
                Exists = true,
                IsSafeToClean = true,
            },
            // Distinct SteamEmulator save directory -> must be merged into SaveLocations!
            new()
            {
                EntryId = "e-emu",
                GameId = hkGame.GameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.SteamEmulator,
                SourceLabel = "离线模拟器",
                PathOrRegistryKey = emuSaveDir,
                Exists = true,
                IsSafeToClean = true,
                FileCount = 1,
                TotalBytes = 8,
            },
            // Achievement data -> must be excluded from save layout!
            new()
            {
                EntryId = "e-ach",
                GameId = hkGame.GameId,
                Category = GameDataCategory.AchievementData,
                SourceRule = GameDataSourceRule.SteamEmulator,
                PathOrRegistryKey = achFile,
                Exists = true,
                IsSafeToClean = true,
            },
            // ReparsePoint entry -> must be skipped!
            new()
            {
                EntryId = "e-junc",
                GameId = hkGame.GameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = junctionEntry,
                Exists = true,
                IsSafeToClean = true,
            },
            // Unsafe .exe path -> must be rejected by ValidateSaveRestoreTargetAsync!
            new()
            {
                EntryId = "e-unsafe-exe",
                GameId = hkGame.GameId,
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = unsafeExe,
                Exists = true,
                IsSafeToClean = true,
            },
            // Unsafe HKLM registry -> must be rejected!
            new()
            {
                EntryId = "e-hklm",
                GameId = hkGame.GameId,
                Category = GameDataCategory.RegistryData,
                SourceRule = GameDataSourceRule.PCGamingWiki,
                PathOrRegistryKey = @"HKLM\Software\Team Cherry\Hollow Knight",
                Exists = true,
                IsSafeToClean = true,
            },
        };

        var fakeDataService = CreatePermissiveSandboxGameDataService(sandbox.RootDirectory, _ => fakeEntries);
        var resolver = new LudusaviRuleResolver(
            sandbox.Database,
            fakeDataService,
            folders,
            timeProvider: null,
            registryKeyExistsProbe: subKey => string.Equals(subKey, @"Software\Team Cherry\Hollow Knight", StringComparison.OrdinalIgnoreCase));

        ResolvedSaveLayout layout = await resolver.ResolveGameSaveLayoutAsync(hkGame.GameId);

        Assert.Equal("Hollow Knight", layout.MatchedRuleName);

        // SaveLocations: user1.dat (deduped!), user1.dat.bak1, and emuSaveDir (3 unique locations).
        Assert.Equal(3, layout.SaveLocations.Count);
        Assert.Single(layout.SaveLocations, l => string.Equals(l.ResolvedFullPath, saveFile1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(layout.SaveLocations, l => string.Equals(l.ResolvedFullPath, saveBak, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(layout.SaveLocations, l => string.Equals(l.ResolvedFullPath, emuSaveDir, StringComparison.OrdinalIgnoreCase) && l.SourceRule == GameDataSourceRule.SteamEmulator);
        Assert.All(layout.SaveLocations, l => Assert.Equal(SaveFileCategory.Save, l.Category));

        // ConfigLocations: Config.ini strictly separated from SaveLocations.
        Assert.Single(layout.ConfigLocations);
        Assert.Equal(configFile, layout.ConfigLocations[0].ResolvedFullPath, ignoreCase: true);
        Assert.Equal("<winLocalAppDataLow>/Team Cherry/Hollow Knight/Config.ini", layout.ConfigLocations[0].LogicalTemplatePath);
        Assert.Equal(SaveFileCategory.Config, layout.ConfigLocations[0].Category);

        // RegistryKeys: HKCU deduplicated to 1 entry, HKLM rejected.
        Assert.Single(layout.RegistryKeys);
        Assert.Equal(@"HKCU\Software\Team Cherry\Hollow Knight", layout.RegistryKeys[0].ResolvedFullPath);
        Assert.Equal("registry://HKCU/Software/Team Cherry/Hollow Knight", layout.RegistryKeys[0].LogicalTemplatePath);
        Assert.Equal(SaveFileCategory.RegistryExport, layout.RegistryKeys[0].Category);
        Assert.True(layout.RegistryKeys[0].Exists);

        // Excluded items must never appear anywhere in layout.Locations.
        Assert.DoesNotContain(layout.Locations, l => string.Equals(l.ResolvedFullPath, achFile, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(layout.Locations, l => string.Equals(l.ResolvedFullPath, junctionEntry, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(layout.Locations, l => string.Equals(l.ResolvedFullPath, unsafeExe, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(layout.Locations, l => l.ResolvedFullPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase));

        // Warnings captured for skipped junction, unsafe .exe, and HKLM.
        Assert.True(layout.Warnings.Count >= 3);
        Assert.Equal(layout.SaveLocations.Count + layout.ConfigLocations.Count + layout.RegistryKeys.Count, layout.Locations.Count);

        // Verify emuSaveFile was counted properly in emuSaveDir.
        ResolvedSaveLocation emuLoc = layout.SaveLocations.Single(l => string.Equals(l.ResolvedFullPath, emuSaveDir, StringComparison.OrdinalIgnoreCase));
        Assert.True(emuLoc.Exists);
        Assert.Equal(1, emuLoc.FileCount);
        Assert.Equal("emu-save".Length, emuLoc.TotalBytes);
        Assert.True(File.Exists(emuSaveFile));
    }

    [Fact]
    public async Task Resolver_MatchesByCleanedTitleWhenSteamAndGogIdsAreAbsent()
    {
        using var sandbox = new SnapshotTestSandbox();
        var (folders, _, steamInstallDir) = CreateSyntheticEnvironment(sandbox);

        string installDir = Path.Combine(Path.GetDirectoryName(steamInstallDir)!, "Stardew_Valley_v1.6.8_Portable");
        Directory.CreateDirectory(installDir);
        string saveFile = WriteTempFile(Path.Combine(folders.WinAppData, "StardewValley", "Saves", "Farm_123", "SaveGameInfo"), "<xml/>");
        string configFile = WriteTempFile(Path.Combine(folders.WinAppData, "StardewValley", "startup_preferences"), "<prefs/>");

        // No SteamAppId and no GogGameId — must match built-in rule via cleaned title / alias!
        var game = CreateGameRecord("g-sdv", "[Repack] 星露谷物语 / Stardew.Valley.v1.6.8-RUNE", installDir);
        await sandbox.Database.UpsertGameAsync(game);

        var fakeDataService = CreatePermissiveSandboxGameDataService(sandbox.RootDirectory, _ => []);
        var resolver = new LudusaviRuleResolver(sandbox.Database, fakeDataService, folders);

        ResolvedSaveLayout layout = await resolver.ResolveGameSaveLayoutAsync(game.GameId);

        Assert.Equal("Stardew Valley", layout.MatchedRuleName);
        Assert.Single(layout.SaveLocations);
        Assert.Equal(Path.Combine(folders.WinAppData, "StardewValley", "Saves"), layout.SaveLocations[0].ResolvedFullPath, ignoreCase: true);
        Assert.Equal("<winAppData>/StardewValley/Saves", layout.SaveLocations[0].LogicalTemplatePath);
        Assert.True(layout.SaveLocations[0].Exists);
        Assert.Equal(1, layout.SaveLocations[0].FileCount);

        Assert.Single(layout.ConfigLocations);
        Assert.Equal(configFile, layout.ConfigLocations[0].ResolvedFullPath, ignoreCase: true);
        Assert.True(File.Exists(saveFile));
    }

    private static (StaticKnownFolderProvider Folders, string SteamRoot, string SteamInstallDir) CreateSyntheticEnvironment(
        SnapshotTestSandbox sandbox)
    {
        string home = sandbox.CreateSubDirectory("env", "Users", "TestPilot");
        string appData = sandbox.CreateSubDirectory("env", "Users", "TestPilot", "AppData", "Roaming");
        string localAppData = sandbox.CreateSubDirectory("env", "Users", "TestPilot", "AppData", "Local");
        string localAppDataLow = sandbox.CreateSubDirectory("env", "Users", "TestPilot", "AppData", "LocalLow");
        string documents = sandbox.CreateSubDirectory("env", "OneDrive", "Documents");
        string pub = sandbox.CreateSubDirectory("env", "Users", "Public");
        string programData = sandbox.CreateSubDirectory("env", "ProgramData");
        string winDir = sandbox.CreateSubDirectory("env", "Windows");
        string steamRoot = sandbox.CreateSubDirectory("env", "Steam");
        string steamInstallDir = sandbox.CreateSubDirectory("env", "Steam", "steamapps", "common", "TestGame");

        var folders = new StaticKnownFolderProvider
        {
            Home = home,
            WinAppData = appData,
            WinLocalAppData = localAppData,
            WinLocalAppDataLow = localAppDataLow,
            WinDocuments = documents,
            WinPublic = pub,
            WinProgramData = programData,
            WinDir = winDir,
            OsUserName = "TestPilot",
            SteamRootDirectories = [steamRoot],
        };

        return (folders, steamRoot, steamInstallDir);
    }

    private static GameRecord CreateGameRecord(
        string gameId,
        string title,
        string installDir,
        int? steamAppId = null,
        string? gogGameId = null) =>
        new()
        {
            GameId = gameId,
            Title = title,
            InstallDir = installDir,
            ExecutableRelPath = "game.exe",
            SteamAppId = steamAppId,
            GogGameId = gogGameId,
        };

    private static string WriteTempFile(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    private static StubSnapshotGameDataService CreatePermissiveSandboxGameDataService(
        string sandboxRoot,
        Func<string, IReadOnlyList<GameDataEntryDto>> entriesProvider)
    {
        string normSandbox = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot));
        return new StubSnapshotGameDataService(
            entriesProvider,
            customValidator: (_, pathOrKey) =>
            {
                if (pathOrKey.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
                {
                    return new GameDataSafetyCheckResult(true);
                }

                if (pathOrKey.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
                    || pathOrKey.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
                {
                    return new GameDataSafetyCheckResult(false, "安全围栏禁止 HKLM 注册表路径。");
                }

                string ext = Path.GetExtension(pathOrKey);
                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    return new GameDataSafetyCheckResult(false, $"安全围栏禁止可执行文件目标：{pathOrKey}");
                }

                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathOrKey));
                bool insideSandbox = full.StartsWith(normSandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                return insideSandbox
                    ? new GameDataSafetyCheckResult(true)
                    : new GameDataSafetyCheckResult(false, $"路径越界：{pathOrKey}");
            });
    }
}
