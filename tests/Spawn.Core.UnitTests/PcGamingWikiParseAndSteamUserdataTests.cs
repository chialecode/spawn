using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// M3-A-13 automated verification: PCGamingWiki <c>action=parse&amp;redirects=1&amp;prop=wikitext</c> primary/fallback parsing,
/// <c>action=cargoquery</c> <c>permissiondenied</c> fallback, <c>action=query&amp;list=search</c> missing-title fallback,
/// SQLite rule caching, Steam <c>userdata\&lt;uid&gt;\&lt;AppId&gt;\remote</c> offline discovery, and
/// <see cref="IGameDataManagementService"/> save enumeration + restore safety-fence validation.
/// 100% offline with zero real network calls and zero real user-profile/Steam/registry scanning.
/// </summary>
public sealed class PcGamingWikiParseAndSteamUserdataTests
{
    private const string SampleWikitext = """
        {{Infobox game
        |steam appid = 1703340
        }}
        ===Save game data location===
        {{Game data
        |{{ Game data/saves | Windows | {{p|appdata}}\StanleyStudio\The Stanley Parable\Saves<ref name="save">Cloud backed</ref> }}
        |{{Game data/saves|Windows|{{p|localappdata}}\StanleyStudio\The Stanley Parable\LocalSaves{{cn}}}}
        |{{Game data/saves|Windows|{{p|userprofile}}\Saved Games\The Stanley Parable\Slots}}
        |{{Game data/saves|Windows|{{p|game}}\save_game\}}
        |{{Game data/saves|Steam|{{p|steam}}\userdata\{{p|uid}}\1703340\remote\}}
        |{{Game data/saves|Windows|{{p|hkcu}}\Software\Crows Crows Crows\The Stanley Parable}}
        |{{Game data/saves|Linux|{{p|xdgdata}}/StanleyParable}}
        }}
        ===Configuration file(s) location===
        {{Game data
        |{{Game data/config|Windows|{{p|localappdata}}\StanleyStudio\The Stanley Parable\Config.ini|note=Video settings}}
        }}
        """;

    [Fact]
    public async Task PcGamingWikiParseAndSteamUserdata_ActionParsePrimary_ParsesAllPlaceholdersAndCachesInSqlite()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string regTestRoot = $@"Software\SPAWN-Test\{Guid.NewGuid():N}";
        string installDir = sandbox.CreateSubDirectory("games", "The Stanley Parable");
        await File.WriteAllBytesAsync(Path.Combine(installDir, "Stanley.exe"), [0x4D, 0x5A, 0x90, 0x00]);

        var recordedRequests = new List<Uri>();
        using var mockHandler = new RecordingMockHttpMessageHandler(req =>
        {
            recordedRequests.Add(req.RequestUri!);
            string query = req.RequestUri!.Query;

            if (query.Contains("action=cargoquery", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"error":{"code":"permissiondenied","info":"You do not have permission to use cargoquery."}}""");
            }

            if (query.Contains("action=parse", StringComparison.OrdinalIgnoreCase)
                && query.Contains("redirects=1", StringComparison.OrdinalIgnoreCase)
                && query.Contains("prop=wikitext", StringComparison.OrdinalIgnoreCase))
            {
                string payload = JsonSerializer.Serialize(new
                {
                    parse = new
                    {
                        title = "The Stanley Parable",
                        pageid = 4242,
                        wikitext = new Dictionary<string, string> { ["*"] = SampleWikitext },
                    },
                });
                return JsonResponse(payload);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var options = CreateIsolatedOptions(sandbox, regTestRoot, mockHandler, allowOnlineLookup: true);
        string steamRemoteDir = Path.Combine(options.SteamInstallDirOverride!, "userdata", "1048576", "1703340", "remote");
        Directory.CreateDirectory(steamRemoteDir);
        await File.WriteAllTextAsync(Path.Combine(steamRemoteDir, "slot0.sav"), "STEAM_REMOTE_SAVE", Encoding.UTF8);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "The Stanley Parable",
            InstallDir = installDir,
            ExecutableRelPath = "Stanley.exe",
            PlatformSource = PlatformSource.SteamLocal,
            SteamAppId = 1703340,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var service = new GameDataManagementService(
            sandbox.Database,
            sandbox.Paths,
            new StubLaunchService(),
            options: options);

        // 1. First inspect triggers action=parse&redirects=1&prop=wikitext and persists rules into SQLite cache.
        var entries = await service.InspectGameDataAsync(game.GameId, refreshOnlineRules: true);

        Assert.Single(recordedRequests);
        Assert.Contains("action=parse", recordedRequests[0].Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redirects=1", recordedRequests[0].Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prop=wikitext", recordedRequests[0].Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recordedRequests, u => u.Query.Contains("action=cargoquery", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(entries, e =>
            e.Category == GameDataCategory.SaveData
            && string.Equals(e.PathOrRegistryKey, Path.Combine(options.AppDataRoamingOverride!, "StanleyStudio", "The Stanley Parable", "Saves"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.Category == GameDataCategory.SaveData
            && string.Equals(e.PathOrRegistryKey, Path.Combine(options.LocalAppDataOverride!, "StanleyStudio", "The Stanley Parable", "LocalSaves"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.Category == GameDataCategory.SaveData
            && string.Equals(e.PathOrRegistryKey, Path.Combine(options.UserProfileOverride!, "Saved Games", "The Stanley Parable", "Slots"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.Category == GameDataCategory.SaveData
            && string.Equals(e.PathOrRegistryKey, Path.Combine(installDir, "save_game"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.Category == GameDataCategory.SaveData
            && string.Equals(e.PathOrRegistryKey, steamRemoteDir, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.Category == GameDataCategory.RegistryData
            && string.Equals(e.PathOrRegistryKey, $@"HKCU\{regTestRoot}\Crows Crows Crows\The Stanley Parable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.PathOrRegistryKey.Contains("xdgdata", StringComparison.OrdinalIgnoreCase));

        // 2. Rules must be persisted in SQLite game_data_rules cache.
        var cachedRow = await sandbox.Database.GetGameDataRuleCacheByGameAsync(game.GameId, game.SteamAppId, null);
        Assert.NotNull(cachedRow);
        Assert.Equal("PCGamingWiki", cachedRow!.Provider);
        Assert.Contains("StanleyStudio", cachedRow.RulesJson, StringComparison.Ordinal);

        // 3. Subsequent inspect with refreshOnlineRules: false must consume SQLite cache with ZERO HTTP calls.
        int countBeforeCachedRead = recordedRequests.Count;
        var cachedEntries = await service.InspectGameDataAsync(game.GameId, refreshOnlineRules: false);
        Assert.Equal(countBeforeCachedRead, recordedRequests.Count);
        Assert.Contains(cachedEntries, e => string.Equals(e.PathOrRegistryKey, steamRemoteDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PcGamingWikiParseAndSteamUserdata_CargoQueryPermissionDenied_FallsBackAutomaticallyToActionParse()
    {
        var recordedQueries = new List<string>();
        using var mockHandler = new RecordingMockHttpMessageHandler(req =>
        {
            string query = req.RequestUri!.Query;
            recordedQueries.Add(query);

            if (query.Contains("action=cargoquery", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"error":{"code":"permissiondenied","info":"You do not have permission to execute action cargoquery."}}""");
            }

            if (query.Contains("action=parse", StringComparison.OrdinalIgnoreCase)
                && query.Contains("page=Elden%20Ring", StringComparison.OrdinalIgnoreCase)
                && query.Contains("redirects=1", StringComparison.OrdinalIgnoreCase)
                && query.Contains("prop=wikitext", StringComparison.OrdinalIgnoreCase))
            {
                string payload = JsonSerializer.Serialize(new
                {
                    parse = new
                    {
                        title = "Elden Ring",
                        wikitext = new Dictionary<string, string>
                        {
                            ["*"] = """
                                    {{Game data
                                    |{{Game data/saves|Windows|{{p|appdata}}\EldenRing\{{p|uid}}\}}
                                    }}
                                    """,
                        },
                    },
                });
                return JsonResponse(payload);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var rules = await PcGamingWikiRuleParser.FetchOnlineRulesAsync(
            mockHandler,
            steamAppId: 1245620,
            gogGameId: "1999999999",
            gameTitle: "Elden Ring",
            timeout: TimeSpan.FromSeconds(3),
            tryCargoQueryFirst: true);

        Assert.NotNull(rules);
        Assert.Single(rules!);
        Assert.Equal(GameDataCategory.SaveData, rules[0].Category);
        Assert.Equal(@"{{p|appdata}}\EldenRing\{{p|uid}}\", rules[0].RawTemplatePath);

        // Cargo query must short-circuit after the first permissiondenied (never retrying GOG ID), then fall back to action=parse.
        Assert.Equal(1, recordedQueries.Count(q => q.Contains("action=cargoquery", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, recordedQueries.Count(q => q.Contains("action=parse", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task PcGamingWikiParseAndSteamUserdata_MissingTitleAndUnknownPageName_FallsBackToListSearchThenParses()
    {
        // Case 1: Page title returns missingtitle -> list=search skips Talk: namespace and resolves canonical title -> action=parse succeeds.
        var case1Queries = new List<string>();
        using var missingTitleHandler = new RecordingMockHttpMessageHandler(req =>
        {
            string query = req.RequestUri!.Query;
            case1Queries.Add(query);

            if (query.Contains("action=parse", StringComparison.OrdinalIgnoreCase)
                && query.Contains("page=Stanley%20Deluxe", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"error":{"code":"missingtitle","info":"The page you specified doesn't exist."}}""");
            }

            if (query.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && query.Contains("list=search", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "query": {
                        "search": [
                          { "title": "Talk:The Stanley Parable: Ultra Deluxe", "pageid": 1001 },
                          { "title": "The Stanley Parable: Ultra Deluxe", "pageid": 7788 }
                        ]
                      }
                    }
                    """);
            }

            if (query.Contains("action=parse", StringComparison.OrdinalIgnoreCase)
                && query.Contains("The%20Stanley%20Parable", StringComparison.OrdinalIgnoreCase)
                && query.Contains("redirects=1", StringComparison.OrdinalIgnoreCase))
            {
                // Also verify formatversion=2 plain string wikitext shape.
                string payload = JsonSerializer.Serialize(new
                {
                    parse = new
                    {
                        title = "The Stanley Parable: Ultra Deluxe",
                        wikitext = """{{Game data/saves|Windows|{{p|locallow}}\Crows Crows Crows\The Stanley Parable_ Ultra Deluxe\}}""",
                    },
                });
                return JsonResponse(payload);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var resolvedFromMissingTitle = await PcGamingWikiRuleParser.FetchOnlineRulesAsync(
            missingTitleHandler,
            steamAppId: 1703340,
            gogGameId: null,
            gameTitle: "Stanley Deluxe",
            timeout: TimeSpan.FromSeconds(3));

        Assert.NotNull(resolvedFromMissingTitle);
        Assert.Single(resolvedFromMissingTitle!);
        Assert.Equal(@"{{p|locallow}}\Crows Crows Crows\The Stanley Parable_ Ultra Deluxe\", resolvedFromMissingTitle[0].RawTemplatePath);
        Assert.Contains(case1Queries, q => q.Contains("list=search", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(case1Queries, q => q.Contains("Talk%3A", StringComparison.OrdinalIgnoreCase));

        // Case 2: Page title completely unknown (empty string), only Steam AppId provided -> list=search by AppId -> action=parse.
        var case2Queries = new List<string>();
        using var unknownTitleHandler = new RecordingMockHttpMessageHandler(req =>
        {
            string query = req.RequestUri!.Query;
            case2Queries.Add(query);

            if (query.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && query.Contains("list=search", StringComparison.OrdinalIgnoreCase)
                && query.Contains("srsearch=1794680", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"query":{"search":[{"title":"Vampire Survivors","pageid":9001}]}}""");
            }

            if (query.Contains("action=parse", StringComparison.OrdinalIgnoreCase)
                && query.Contains("page=Vampire%20Survivors", StringComparison.OrdinalIgnoreCase))
            {
                string payload = JsonSerializer.Serialize(new
                {
                    parse = new
                    {
                        title = "Vampire Survivors",
                        wikitext = new Dictionary<string, string>
                        {
                            ["*"] = """{{Game data/saves|Windows|{{p|appdata}}\Vampire_Survivors\saves\}}""",
                        },
                    },
                });
                return JsonResponse(payload);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var resolvedByAppIdSearch = await PcGamingWikiRuleParser.FetchOnlineRulesAsync(
            unknownTitleHandler,
            steamAppId: 1794680,
            gogGameId: null,
            gameTitle: string.Empty,
            timeout: TimeSpan.FromSeconds(3));

        Assert.NotNull(resolvedByAppIdSearch);
        Assert.Single(resolvedByAppIdSearch!);
        Assert.Equal(@"{{p|appdata}}\Vampire_Survivors\saves\", resolvedByAppIdSearch[0].RawTemplatePath);
        Assert.Contains(case2Queries, q => q.Contains("srsearch=1794680", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PcGamingWikiParseAndSteamUserdata_OfflineSteamUserdataRemoteRule_DiscoversMatchingUidRemotesOnly()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string regTestRoot = $@"Software\SPAWN-Test\{Guid.NewGuid():N}";
        var options = CreateIsolatedOptions(sandbox, regTestRoot, mockHandler: null, allowOnlineLookup: false);

        string installDir = sandbox.CreateSubDirectory("games", "Hollow Knight");
        await File.WriteAllBytesAsync(Path.Combine(installDir, "hollow_knight.exe"), [0x4D, 0x5A]);

        // Seed synthetic Steam userdata with two real account UIDs for AppId 367520, one unrelated AppId, and 'anonymous'.
        string uid1Remote = Path.Combine(options.SteamInstallDirOverride!, "userdata", "10000123", "367520", "remote");
        string uid2Remote = Path.Combine(options.SteamInstallDirOverride!, "userdata", "20000456", "367520", "remote");
        string otherAppRemote = Path.Combine(options.SteamInstallDirOverride!, "userdata", "10000123", "999999", "remote");
        string anonRemote = Path.Combine(options.SteamInstallDirOverride!, "userdata", "anonymous", "367520", "remote");

        Directory.CreateDirectory(uid1Remote);
        Directory.CreateDirectory(uid2Remote);
        Directory.CreateDirectory(otherAppRemote);
        Directory.CreateDirectory(anonRemote);

        await File.WriteAllTextAsync(Path.Combine(uid1Remote, "user1.dat"), "HK_SAVE_UID1", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(uid2Remote, "user1.dat"), "HK_SAVE_UID2", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(otherAppRemote, "other.dat"), "OTHER_GAME", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(anonRemote, "anon.dat"), "ANON", Encoding.UTF8);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Hollow Knight",
            InstallDir = installDir,
            ExecutableRelPath = "hollow_knight.exe",
            PlatformSource = PlatformSource.SteamLocal,
            SteamAppId = 367520,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var service = new GameDataManagementService(
            sandbox.Database,
            sandbox.Paths,
            new StubLaunchService(),
            options: options);

        var saveLocations = await service.EnumerateSaveLocationsAsync(game.GameId, refreshOnlineRules: false);

        Assert.Contains(saveLocations, e =>
            e.Category == GameDataCategory.SaveData
            && e.Exists
            && e.IsSafeToClean
            && string.Equals(e.PathOrRegistryKey, uid1Remote, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(saveLocations, e =>
            e.Category == GameDataCategory.SaveData
            && e.Exists
            && e.IsSafeToClean
            && string.Equals(e.PathOrRegistryKey, uid2Remote, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(saveLocations, e => string.Equals(e.PathOrRegistryKey, otherAppRemote, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(saveLocations, e => string.Equals(e.PathOrRegistryKey, anonRemote, StringComparison.OrdinalIgnoreCase));

        // Ensure AllowSystemSteamDiscovery = false with no override never probes the host machine's Steam installation.
        var noSteamOptions = new GameDataManagementOptions
        {
            AllowSystemSteamDiscovery = false,
            SteamInstallDirOverride = null,
        };
        Assert.Null(noSteamOptions.ResolveSteamInstallDir());
    }

    [Fact]
    public async Task PcGamingWikiParseAndSteamUserdata_EnumerateAndValidateRestoreTarget_EnforcesSafetyFenceAndAllowsDeletedSafeTargets()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string regTestRoot = $@"Software\SPAWN-Test\{Guid.NewGuid():N}";
        var options = CreateIsolatedOptions(sandbox, regTestRoot, mockHandler: null, allowOnlineLookup: false);

        try
        {
            string installDir = sandbox.CreateSubDirectory("games", "Celeste");
            await File.WriteAllBytesAsync(Path.Combine(installDir, "Celeste.exe"), [0x4D, 0x5A]);
            await File.WriteAllBytesAsync(Path.Combine(installDir, "UnityPlayer.dll"), [0x4D, 0x5A]);
            string unityDataDir = Directory.CreateDirectory(Path.Combine(installDir, "Celeste_Data")).FullName;
            await File.WriteAllTextAsync(Path.Combine(unityDataDir, "app.info"), "MaddyMakesGames\nCeleste\n", Encoding.UTF8);

            // Seed existing saves + config + HKCU test key.
            string inGameSaves = Directory.CreateDirectory(Path.Combine(installDir, "saves")).FullName;
            await File.WriteAllTextAsync(Path.Combine(inGameSaves, "0.celeste"), "SAVE_0", Encoding.UTF8);

            string steamRemote = Path.Combine(options.SteamInstallDirOverride!, "userdata", "777888", "504230", "remote");
            Directory.CreateDirectory(steamRemote);
            await File.WriteAllTextAsync(Path.Combine(steamRemote, "0.celeste"), "STEAM_SAVE_0", Encoding.UTF8);

            string unityLocalLow = Path.Combine(options.LocalLowOverride!, "MaddyMakesGames", "Celeste");
            Directory.CreateDirectory(unityLocalLow);
            await File.WriteAllTextAsync(Path.Combine(unityLocalLow, "prefs.dat"), "PREFS", Encoding.UTF8);

            string hkcuSubKey = $@"{regTestRoot}\MaddyMakesGames\Celeste";
            string hkcuFullKey = $@"HKCU\{hkcuSubKey}";
            using (var key = Registry.CurrentUser.CreateSubKey(hkcuSubKey, writable: true))
            {
                key!.SetValue("Chapter", 7, RegistryValueKind.DWord);
            }

            // Seed a cached PCGamingWiki Config rule to ensure EnumerateSaveLocationsAsync excludes ConfigAndLogs.
            var game = new GameRecord
            {
                GameId = SpawnId.NewUlid(),
                Title = "Celeste",
                InstallDir = installDir,
                ExecutableRelPath = "Celeste.exe",
                PlatformSource = PlatformSource.SteamLocal,
                SteamAppId = 504230,
            };
            await sandbox.Database.UpsertGameAsync(game);

            string configFile = Path.Combine(options.LocalAppDataOverride!, "Celeste", "settings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            await File.WriteAllTextAsync(configFile, "vsync=1", Encoding.UTF8);
            await sandbox.Database.UpsertGameDataRuleCacheAsync(new GameDataRuleCacheRecord
            {
                CacheKey = "pcgw:steam:504230",
                GameId = game.GameId,
                SteamAppId = 504230,
                Provider = "PCGamingWiki",
                RulesJson = JsonSerializer.Serialize(new[]
                {
                    new PcGamingWikiRawRule(GameDataCategory.ConfigAndLogs, @"{{p|localappdata}}\Celeste\settings.ini"),
                }),
                FetchedUtc = DateTimeOffset.UtcNow,
            });

            var service = new GameDataManagementService(
                sandbox.Database,
                sandbox.Paths,
                new StubLaunchService(),
                options: options);

            // 1. EnumerateSaveLocationsAsync returns ONLY existing safe SaveData & RegistryData (never ConfigAndLogs).
            var enumerated = await service.EnumerateSaveLocationsAsync(game.GameId);
            Assert.Contains(enumerated, e => string.Equals(e.PathOrRegistryKey, inGameSaves, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(enumerated, e => string.Equals(e.PathOrRegistryKey, steamRemote, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(enumerated, e => string.Equals(e.PathOrRegistryKey, unityLocalLow, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(enumerated, e => string.Equals(e.PathOrRegistryKey, hkcuFullKey, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(enumerated, e => e.Category == GameDataCategory.ConfigAndLogs);
            Assert.All(enumerated, e =>
            {
                Assert.True(e.Exists);
                Assert.True(e.IsSafeToClean);
            });

            // 2. ValidateSaveRestoreTargetAsync MUST block game root, EXE/DLL, *_Data, HKLM, path traversal, SPAWN data root, and foreign paths.
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, installDir)).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(installDir, "Celeste.exe"))).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(installDir, "UnityPlayer.dll"))).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(inGameSaves, "injected.exe"))).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(inGameSaves, "injected.dll"))).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, unityDataDir)).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(unityDataDir, "level0"))).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, @"HKLM\Software\MaddyMakesGames\Celeste")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, $@"HKCU\{regTestRoot}\Microsoft\Windows")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, $@"HKCU\{regTestRoot}\OtherStudio\UnrelatedGame")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, inGameSaves + @"\..\Celeste.exe")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, steamRemote.Replace('\\', '/') + "/../../999999/remote/save.dat")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, hkcuFullKey + @"\..\OtherGame")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, @"relative\saves\0.celeste")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, @"\\localhost\c$\temp\0.celeste")).IsSafe);
            Assert.False((await service.ValidateSaveRestoreTargetAsync(game.GameId, sandbox.Paths.DatabaseFilePath)).IsSafe);

            // 3. Delete save folders & HKCU key to prove ValidateSaveRestoreTargetAsync STILL allows restoring deleted valid save locations.
            Directory.Delete(inGameSaves, recursive: true);
            Directory.Delete(steamRemote, recursive: true);
            Registry.CurrentUser.DeleteSubKeyTree(hkcuSubKey, throwOnMissingSubKey: false);

            Assert.True((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(inGameSaves, "0.celeste"))).IsSafe);
            Assert.True((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(steamRemote, "0.celeste"))).IsSafe);
            Assert.True((await service.ValidateSaveRestoreTargetAsync(game.GameId, Path.Combine(unityLocalLow, "prefs.dat"))).IsSafe);
            Assert.True((await service.ValidateSaveRestoreTargetAsync(game.GameId, hkcuFullKey)).IsSafe);
            Assert.True((await service.ValidateSaveRestoreTargetAsync(game.GameId, $@"HKEY_CURRENT_USER\{hkcuSubKey}\Profile1")).IsSafe);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(regTestRoot, throwOnMissingSubKey: false);
            TryDeleteEmptySpawnTestRoot();
        }
    }

    private static GameDataManagementOptions CreateIsolatedOptions(
        TestSandbox sandbox,
        string hkcuPrefix,
        HttpMessageHandler? mockHandler,
        bool allowOnlineLookup) =>
        new()
        {
            AllowOnlinePcGamingWikiLookup = allowOnlineLookup,
            AllowSystemSteamDiscovery = false,
            HttpMessageHandlerOverride = mockHandler,
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

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static void TryDeleteEmptySpawnTestRoot()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKey(@"Software\SPAWN-Test", throwOnMissingSubKey: false);
        }
        catch
        {
            // Sibling parallel test may still hold a subkey under Software\SPAWN-Test.
        }
    }

    private sealed class RecordingMockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubLaunchService : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();

        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
        {
            add { }
            remove { }
        }

        public ActiveGameSession? GetActiveSession(string gameId) => null;

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
