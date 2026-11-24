using System.Net;
using System.Text;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Achievement;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class TrilogyAndUnrealRecognitionTests
{
    [Fact]
    public async Task AnthologyContainer_WithMenuLauncherAndThreeUnrealSubGames_ExpandsIntoStandaloneGamesAndSkipsRedirectorStubs()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string libraryRoot = sandbox.CreateSubDirectory("library-root");
        string anthologyDir = await CreateSyntheticUnrealTrilogyAsync(libraryRoot);

        var importedEvents = new List<GameImportedOrRescannedEvent>();
        var eventBus = new InMemorySpawnEventBus();
        using var subImport = eventBus.Subscribe<GameImportedOrRescannedEvent>(e => importedEvents.Add(e));

        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, eventBus);

        // 1. Library root scan expands anthology container into 3 standalone child games
        var discovered = await library.ScanLibraryDirectoryAsync(libraryRoot);
        Assert.Equal(3, discovered.Count);
        Assert.Equal(3, importedEvents.Count);
        Assert.DoesNotContain(
            discovered,
            g => string.Equals(
                Path.TrimEndingDirectorySeparator(g.InstallDir),
                Path.TrimEndingDirectorySeparator(anthologyDir),
                StringComparison.OrdinalIgnoreCase));

        var byAppId = discovered.ToDictionary(g => g.SteamAppId!.Value);
        Assert.True(byAppId.ContainsKey(901001));
        Assert.True(byAppId.ContainsKey(901002));
        Assert.True(byAppId.ContainsKey(901003));

        Assert.Equal(
            Path.Combine("Gameface", "Binaries", "Win64", "CityThree.exe"),
            byAppId[901001].ExecutableRelPath);
        Assert.Equal(
            Path.Combine("Gameface", "Binaries", "Win64", "ViceEpisode.exe"),
            byAppId[901002].ExecutableRelPath);
        Assert.Equal(
            Path.Combine("Gameface", "Binaries", "Win64", "SanEpisode.exe"),
            byAppId[901003].ExecutableRelPath);

        foreach (var game in discovered)
        {
            Assert.Equal(PlatformSource.Steamrip, game.PlatformSource);
            Assert.Equal(AntiCheatTier.Tier0, game.AntiCheatTier);
            Assert.Equal(AchievementCaptureMode.SteamEmulator, game.AchievementCaptureMode);
            Assert.Contains("CODEX", game.AchievementChannelLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PlayEpisode", game.ExecutableRelPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Launcher", game.ExecutableRelPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Redistributables", game.ExecutableRelPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CrashReportClient", game.ExecutableRelPath, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Direct directory ImportGameAsync on the anthology container also expands into the 3 child games
        await using var sandboxDirect = new TestSandbox();
        await sandboxDirect.Database.InitializeAsync();
        var scannerDirect = new GameDirectoryScanner(
            guard,
            new GameArtworkExtractor(sandboxDirect.Paths),
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var libraryDirect = new GameLibraryService(
            sandboxDirect.Database,
            sandboxDirect.Paths,
            scannerDirect,
            new InMemorySpawnEventBus());

        var firstReturned = await libraryDirect.ImportGameAsync(anthologyDir);
        Assert.NotNull(firstReturned);
        var allPersisted = await libraryDirect.GetAllGamesAsync();
        Assert.Equal(3, allPersisted.Count);

        // 3. Explicit .exe file ImportGameAsync on Launcher.exe honors user intent and pins Launcher.exe
        string explicitLauncherPath = Path.Combine(anthologyDir, "Launcher.exe");
        var explicitImported = await libraryDirect.ImportGameAsync(explicitLauncherPath);
        Assert.NotNull(explicitImported);
        Assert.Equal("Launcher.exe", explicitImported!.ExecutableRelPath);
    }

    [Fact]
    public async Task StaleMergedParentContainer_ReconcilesAndRetiresOnRescanOrLibraryScan_WhilePreservingUnlockedHistory()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string libraryRoot = sandbox.CreateSubDirectory("library-root");
        string anthologyDir = await CreateSyntheticUnrealTrilogyAsync(libraryRoot);

        var removedEvents = new List<GameRemovedEvent>();
        var eventBus = new InMemorySpawnEventBus();
        using var subRemove = eventBus.Subscribe<GameRemovedEvent>(e => removedEvents.Add(e));

        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, eventBus);

        // Pre-seed a broken merged parent record (0 unlocked achievements, unpinned wrong sub-game stub .exe)
        string staleParentId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = staleParentId,
            Title = "Synthetic Episode III – The Definitive Edition",
            SortTitle = "Synthetic Episode III – The Definitive Edition",
            InstallDir = anthologyDir,
            ExecutableRelPath = Path.Combine("GTA Vice Synthetic - The Definitive Edition", "PlayEpisodeVC.exe"),
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 901001,
        });

        // Case A: Single-game RescanGameAsync on the stale merged parent splits into 3 child games and retires the stale parent
        var rescanResult = await library.RescanGameAsync(staleParentId);
        Assert.NotNull(rescanResult);
        Assert.Null(await library.GetGameByIdAsync(staleParentId));
        Assert.Single(removedEvents, e => e.GameId == staleParentId);
        Assert.True(File.Exists(Path.Combine(anthologyDir, "Launcher.exe")));

        var afterRescan = await library.GetAllGamesAsync();
        Assert.Equal(3, afterRescan.Count);

        // Case B: Re-seed another unpinned stale parent and run ScanLibraryDirectoryAsync -> also retired cleanly
        string secondStaleId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = secondStaleId,
            Title = "Merged Trilogy Container",
            SortTitle = "Merged Trilogy Container",
            InstallDir = anthologyDir,
            ExecutableRelPath = "Launcher.exe",
            PlatformSource = PlatformSource.Standalone,
        });

        await library.ScanLibraryDirectoryAsync(libraryRoot);
        Assert.Null(await library.GetGameByIdAsync(secondStaleId));
        Assert.Equal(3, (await library.GetAllGamesAsync()).Count);

        // Case C: Safety guard — if a parent row has unlocked achievement history, reconciliation MUST NOT delete it
        string unlockedParentId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = unlockedParentId,
            Title = "Container With Unlocked History",
            SortTitle = "Container With Unlocked History",
            InstallDir = anthologyDir,
            ExecutableRelPath = "Launcher.exe",
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 901001,
        });
        await sandbox.Database.UpsertAchievementUnlockAsync(new AchievementUnlockDto
        {
            GameId = unlockedParentId,
            ApiName = "ACH_KEEP_HISTORY",
            IsUnlocked = true,
            UnlockedUtc = DateTimeOffset.UtcNow,
            SourceProvider = "Test",
        });

        await library.ScanLibraryDirectoryAsync(libraryRoot);
        Assert.NotNull(await library.GetGameByIdAsync(unlockedParentId));
    }

    [Fact]
    public async Task RescanGame_RefreshesUnpinnedStaleAutoPickedExe_PreservesUserPinnedExe_AndInfersUnrealRootPastContent()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "GTA III - The Definitive Edition");
        Directory.CreateDirectory(Path.Combine(gameDir, "Engine", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(gameDir, "Gameface", "Content", "Paks"));
        string win64Dir = Directory.CreateDirectory(Path.Combine(gameDir, "Gameface", "Binaries", "Win64")).FullName;

        string stubExe = Path.Combine(gameDir, "PlayGTA3.exe");
        string realExe = Path.Combine(win64Dir, "LibertyCity.exe");
        string modLoaderExe = Path.Combine(gameDir, "CustomModLoader.exe");

        await File.WriteAllBytesAsync(stubExe, new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "title.rgl"), [0x52, 0x47, 0x4C, 0x4D]);
        await File.WriteAllBytesAsync(realExe, new byte[131072]);
        await File.WriteAllBytesAsync(modLoaderExe, new byte[16384]);

        // Verify ResolveImportTarget on <GameRoot>\Gameface\Binaries\Win64\LibertyCity.exe steps past Gameface\Content to <GameRoot>
        var resolvedTarget = GameDirectoryScanner.ResolveImportTarget(realExe);
        Assert.NotNull(resolvedTarget);
        Assert.Equal(Path.TrimEndingDirectorySeparator(gameDir), resolvedTarget!.Value.InstallDir, ignoreCase: true);
        Assert.Equal(Path.Combine("Gameface", "Binaries", "Win64", "LibertyCity.exe"), resolvedTarget.Value.ExplicitExeRelPath);

        using var guard = new AntiCheatGuardService();
        var scanner = new GameDirectoryScanner(
            guard,
            new GameArtworkExtractor(sandbox.Paths),
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, new InMemorySpawnEventBus());

        // Seed stale unpinned record pointing to broken PlayGTA3.exe redirector stub
        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "Grand Theft Auto III - The Definitive Edition",
            SortTitle = "Grand Theft Auto III - The Definitive Edition",
            InstallDir = gameDir,
            ExecutableRelPath = "PlayGTA3.exe",
            PlatformSource = PlatformSource.Standalone,
        });

        // 1. Unpinned rescan replaces broken stub with real Unreal binary
        var rescanned = await library.RescanGameAsync(gameId);
        Assert.NotNull(rescanned);
        Assert.Equal(Path.Combine("Gameface", "Binaries", "Win64", "LibertyCity.exe"), rescanned!.ExecutableRelPath);

        // 2. Explicit user metadata override pins CustomModLoader.exe across subsequent rescans & directory re-imports
        var pinned = await library.UpdateGameMetadataAsync(gameId, new GameMetadataUpdate
        {
            ExecutableRelPath = "CustomModLoader.exe",
        });
        Assert.NotNull(pinned);
        Assert.Equal("CustomModLoader.exe", pinned!.ExecutableRelPath);

        var rescannedAfterPin = await library.RescanGameAsync(gameId);
        Assert.NotNull(rescannedAfterPin);
        Assert.Equal("CustomModLoader.exe", rescannedAfterPin!.ExecutableRelPath);

        var reimportedDirAfterPin = await library.ImportGameAsync(gameDir);
        Assert.NotNull(reimportedDirAfterPin);
        Assert.Equal("CustomModLoader.exe", reimportedDirAfterPin!.ExecutableRelPath);
    }

    [Fact]
    public void TitleCleaner_AndPeRoleClassifier_EliminatesDanglingTheExpandsGtaAndRejectsRedirectorMetadata()
    {
        // 1. No dangling "- The" when stripping Definitive Edition / - DE
        Assert.Equal("GTA III", GameTitleCleaner.CleanTitle("GTA III - The Definitive Edition"));
        Assert.Equal("Grand Theft Auto III", GameTitleCleaner.CleanTitle("Grand Theft Auto III - The Definitive Edition"));
        Assert.Equal("Grand Theft Auto III", GameTitleCleaner.CleanTitle("Grand Theft Auto III – The Definitive Edition"));
        Assert.Equal("GTA - The Trilogy", GameTitleCleaner.CleanTitle("GTA - The Trilogy - DE"));

        // 2. Edition-preserving mode keeps remaster distinction and expands shorthand "- DE"
        Assert.Equal(
            "GTA III - The Definitive Edition",
            GameTitleCleaner.CleanTitle("[SteamRIP] GTA III - The Definitive Edition v1.0.112", preserveRemasterEdition: true));
        Assert.Equal(
            "GTA - The Trilogy - Definitive Edition",
            GameTitleCleaner.CleanTitle("GTA - The Trilogy - DE", preserveRemasterEdition: true));

        // 3. Acronym expansion & search candidates
        Assert.Equal(
            "Grand Theft Auto III - The Definitive Edition",
            GameTitleCleaner.ExpandSeriesAbbreviations("GTA III - The Definitive Edition"));
        Assert.Equal("GTFO", GameTitleCleaner.ExpandSeriesAbbreviations("GTFO"));

        var candidates = GameTitleCleaner.BuildSearchCandidates(
            @"C:\SyntheticGames\GTA III - The Definitive Edition",
            mainExeFullPath: null);
        Assert.Equal("Grand Theft Auto III - The Definitive Edition", candidates[0]);
        Assert.Contains("GTA III - The Definitive Edition", candidates);
        Assert.Contains("Grand Theft Auto III", candidates);

        // 4. Sequel & anthology conflict detection
        Assert.True(GameTitleCleaner.HasConflictingSequelOrAnthologyTokens("GTA - The Trilogy - Definitive Edition", "Grand Theft Auto V"));
        Assert.True(GameTitleCleaner.HasConflictingSequelOrAnthologyTokens("Grand Theft Auto III", "Grand Theft Auto V"));
        Assert.True(GameTitleCleaner.HasConflictingSequelOrAnthologyTokens("Grand Theft Auto III", "Grand Theft Auto IV"));
        Assert.False(GameTitleCleaner.HasConflictingSequelOrAnthologyTokens(
            "Grand Theft Auto III - The Definitive Edition",
            "Grand Theft Auto III – The Definitive Edition"));

        // 5. PE metadata role classification for RockstarRedirector, BootstrapPackagedGame, CompactAutoRunner, and real UE binary
        Assert.Equal(
            PeExecutableRole.RedirectorOrPrerequisiteStub,
            GameTitleCleaner.ClassifyPeVersionMetadata(
                originalFilename: "RockstarRedirector.exe",
                internalName: "RockstarRedirector",
                productName: "Rockstar Games Launcher Redirector",
                fileDescription: "Rockstar Games Launcher Redirector"));

        Assert.Equal(
            PeExecutableRole.RedirectorOrPrerequisiteStub,
            GameTitleCleaner.ClassifyPeVersionMetadata(
                originalFilename: "BootstrapPackagedGame.exe",
                internalName: "BootstrapPackagedGame",
                productName: "BootstrapPackagedGame",
                fileDescription: "UE4 Prerequisites"));

        Assert.Equal(
            PeExecutableRole.LauncherOrMenuWrapper,
            GameTitleCleaner.ClassifyPeVersionMetadata(
                originalFilename: "CompactAutoRunner.exe",
                internalName: "CompactAutoRunner",
                productName: "CompactAutoRunner",
                fileDescription: "Compact Customizable CD Menu"));

        Assert.Equal(
            PeExecutableRole.GameBinary,
            GameTitleCleaner.ClassifyPeVersionMetadata(
                originalFilename: "LibertyCity-Win64-Shipping.exe",
                internalName: "LibertyCity",
                productName: "Grand Theft Auto III - The Definitive Edition",
                fileDescription: "Grand Theft Auto III - The Definitive Edition"));
    }

    [Fact]
    public async Task AchievementSchemaAndScannerStoreSearch_RejectSequelHijackAndDisambiguateDefinitiveEdition()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // Stub Steam storesearch returning GTA V (271590, Tier 2 BattlEye in AWACY index) as items[0],
        // 2001 GTA III (12100) as items[1], and GTA III Definitive Edition (1546970) as items[2].
        const string storeSearchJson = """
            {
              "total": 3,
              "items": [
                { "id": 271590, "name": "Grand Theft Auto V" },
                { "id": 12100, "name": "Grand Theft Auto III" },
                { "id": 1546970, "name": "Grand Theft Auto III – The Definitive Edition" }
              ]
            }
            """;

        using var handler = new StubHttpHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/api/storesearch/", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(storeSearchJson, Encoding.UTF8, "application/json"),
                };
            }

            if (url.Contains("appids=1546970", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"1546970":{"success":true,"data":{"name":"Grand Theft Auto III – The Definitive Edition"}}}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);

        // Part 1: AchievementSchemaService MUST NOT blindly return items[0] (271590 GTA V) for an unmatched trilogy/standalone title
        using var schemaService = new AchievementSchemaService(
            new NullSteamApiKeyProvider(),
            new NativeSaveAchievementProvider(),
            new AchievementIconProcessor(sandbox.Paths),
            new AchievementModuleOptions
            {
                AllowNetworkDownloads = true,
                EnableCommunitySpoilerRequests = false,
            },
            httpClient);

        string standalonePackDir = sandbox.CreateSubDirectory("games", "GTA - The Trilogy - DE");
        await File.WriteAllBytesAsync(Path.Combine(standalonePackDir, "Launcher.exe"), new byte[8192]);

        var trilogyRecord = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "GTA - The Trilogy - DE",
            SortTitle = "GTA - The Trilogy - DE",
            InstallDir = standalonePackDir,
            ExecutableRelPath = "Launcher.exe",
            PlatformSource = PlatformSource.Standalone,
            SteamAppId = null,
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await sandbox.Database.UpsertGameAsync(trilogyRecord);

        var resolvedSchema = await schemaService.FetchAndBuildSchemaAsync(trilogyRecord);
        Assert.Null(resolvedSchema.ResolvedSteamAppId);
        Assert.Equal($"Game_{trilogyRecord.GameId}", resolvedSchema.SchemaKey);

        // Part 2: GameDirectoryScanner online storesearch on "GTA III - The Definitive Edition" (without local AppId)
        // expands "GTA" -> "Grand Theft Auto", rejects 271590 (sequel V) and 12100 (non-Definitive), and selects 1546970 (Tier 0)!
        string deGameDir = sandbox.CreateSubDirectory("games", "GTA III - The Definitive Edition");
        Directory.CreateDirectory(Path.Combine(deGameDir, "Engine"));
        string deWin64 = Directory.CreateDirectory(Path.Combine(deGameDir, "Gameface", "Binaries", "Win64")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(deWin64, "LibertyCity.exe"), new byte[65536]);

        using var guard = new AntiCheatGuardService();
        var scanner = new GameDirectoryScanner(
            guard,
            new GameArtworkExtractor(sandbox.Paths, httpClient),
            httpClient: httpClient,
            options: new GameScanOptions { AllowOnlineMetadataLookup = true });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, new InMemorySpawnEventBus());

        var importedDe = await library.ImportGameAsync(deGameDir);
        Assert.NotNull(importedDe);
        Assert.Equal(1546970, importedDe!.SteamAppId);
        Assert.Equal(AntiCheatTier.Tier0, importedDe.AntiCheatTier);
        Assert.Equal("Grand Theft Auto III – The Definitive Edition", importedDe.Title);
    }

    private static async Task<string> CreateSyntheticUnrealTrilogyAsync(string libraryRoot)
    {
        string anthologyDir = Directory.CreateDirectory(Path.Combine(libraryRoot, "GTA - The Trilogy - DE")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(anthologyDir, "Launcher.exe"), new byte[16384]);
        await File.WriteAllTextAsync(Path.Combine(anthologyDir, "launch_ep3.bat"), "@echo off\n", Encoding.UTF8);

        string redistDir = Directory.CreateDirectory(Path.Combine(anthologyDir, "Redistributables")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(redistDir, "Rockstar-Games-Launcher.exe"), new byte[32768]);
        await File.WriteAllBytesAsync(Path.Combine(redistDir, "Social-Club-Setup.exe"), new byte[32768]);

        var specs = new (string Folder, string StubExe, string RealExe, int AppId)[]
        {
            ("GTA Synthetic III - The Definitive Edition", "PlayEpisode3.exe", "CityThree.exe", 901001),
            ("GTA Vice Synthetic - The Definitive Edition", "PlayEpisodeVC.exe", "ViceEpisode.exe", 901002),
            ("GTA San Synthetic - The Definitive Edition", "PlayEpisodeSA.exe", "SanEpisode.exe", 901003),
        };

        foreach (var spec in specs)
        {
            string subRoot = Directory.CreateDirectory(Path.Combine(anthologyDir, spec.Folder)).FullName;

            // Broken root redirector stub + VDF referencing missing Redistributables\Rockstar-Games-Launcher.exe
            await File.WriteAllBytesAsync(Path.Combine(subRoot, spec.StubExe), new byte[4096]);
            await File.WriteAllBytesAsync(Path.Combine(subRoot, "title.rgl"), [0x52, 0x47, 0x4C, 0x4D]);
            await File.WriteAllTextAsync(
                Path.Combine(subRoot, "installscript_sdk.vdf"),
                """
                "InstallScript"
                {
                    "Run Process"
                    {
                        "RGL" "%INSTALLDIR%\\Redistributables\\Rockstar-Games-Launcher.exe"
                    }
                }
                """,
                Encoding.UTF8);

            // Unreal Engine tree with depth-6 Steamworks emulator files + CrashReportClient helper
            string engineWin64 = Directory.CreateDirectory(Path.Combine(subRoot, "Engine", "Binaries", "Win64")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(engineWin64, "CrashReportClient.exe"), new byte[24576]);

            string steamworksWin64 = Directory.CreateDirectory(
                Path.Combine(subRoot, "Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv147", "Win64")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(steamworksWin64, "steam_api64.dll"), [0x4D, 0x5A]);
            await File.WriteAllBytesAsync(Path.Combine(steamworksWin64, "steam_api64.cdx"), [0x4D, 0x5A]);
            await File.WriteAllTextAsync(
                Path.Combine(steamworksWin64, "steam_emu.ini"),
                $"[Settings]\nAppId={spec.AppId}\n",
                Encoding.UTF8);

            // Real non-Shipping Unreal binary at depth 3 from sub-game root (depth 4 from anthology root)
            Directory.CreateDirectory(Path.Combine(subRoot, "Gameface", "Content", "Paks"));
            string gameWin64 = Directory.CreateDirectory(Path.Combine(subRoot, "Gameface", "Binaries", "Win64")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(gameWin64, spec.RealExe), new byte[131072]);
        }

        return anthologyDir;
    }

    private sealed class NullSteamApiKeyProvider : ISteamApiKeyProvider
    {
        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<SteamApiKeyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SteamApiKeyStatusDto(false, "None", null));

        public ValueTask SaveCustomApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
