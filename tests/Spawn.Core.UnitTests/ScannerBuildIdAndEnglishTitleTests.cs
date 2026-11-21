using System.Net;
using System.Text;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// M3-A-06 (Launcher side) verification: Steam ACF <c>buildid</c> &amp; <c>LastUpdated</c> extraction,
/// canonical English title waterfall with permanent <c>trainer_title_map</c> caching while preserving
/// localized Chinese display titles, and Unity/Unreal PE engine version noise filtering.
/// Zero real network requests are performed.
/// </summary>
public sealed class ScannerBuildIdAndEnglishTitleTests
{
    [Fact]
    public async Task Scanner_SteamAppManifestAcf_ParsesBuildIdLastUpdatedAndEnglishTitle()
    {
        await using var sandbox = new TestSandbox();
        string steamAppsDir = sandbox.CreateSubDirectory("SteamLibrary", "steamapps");
        string gameDir = sandbox.CreateSubDirectory("SteamLibrary", "steamapps", "common", "ELDEN RING");

        await File.WriteAllBytesAsync(Path.Combine(gameDir, "eldenring.exe"), new byte[8192]);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "steam_api64.dll"), [0x4D, 0x5A]);

        const long unixUpdated = 1744761600; // 2025-04-16T00:00:00Z
        string acfContent = $$"""
            "AppState"
            {
                "appid"         "1245620"
                "Universe"      "1"
                "name"          "ELDEN RING"
                "StateFlags"    "4"
                "installdir"    "ELDEN RING"
                "LastUpdated"   "{{unixUpdated}}"
                "buildid"       "17991234"
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(steamAppsDir, "appmanifest_1245620.acf"), acfContent, Encoding.UTF8);

        // 1. Direct ACF parser assertions
        SteamAppManifestMetadata? meta = GameDirectoryScanner.TryParseAppManifestText(acfContent);
        Assert.NotNull(meta);
        Assert.Equal(1245620, meta!.AppId);
        Assert.Equal("ELDEN RING", meta.Name);
        Assert.Equal("ELDEN RING", meta.InstallDir);
        Assert.Equal("17991234", meta.BuildId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(unixUpdated), meta.LastUpdatedUtc);

        // 2. Full directory scan populates ScannedGameDescriptor & applies to GameRecord
        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });

        ScannedGameDescriptor? scanned = await scanner.ScanGameDirectoryAsync(gameDir, "game-er-1");
        Assert.NotNull(scanned);
        Assert.Equal(1245620, scanned!.SteamAppId);
        Assert.Equal("17991234", scanned.SteamBuildId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(unixUpdated), scanned.SteamLastUpdatedUtc);
        Assert.Equal("ELDEN RING", scanned.EnglishTitle);
        Assert.Equal(TrainerTitleSource.AcfName, scanned.EnglishTitleSource);

        var baseRecord = new GameRecord
        {
            GameId = "game-er-1",
            Title = "艾尔登法环",
            InstallDir = gameDir,
            ExecutableRelPath = scanned.ExecutableRelPath,
            PlatformSource = scanned.PlatformSource,
            SteamAppId = scanned.SteamAppId,
        };
        GameRecord updatedRecord = GameDirectoryScanner.ApplySteamBuildFingerprint(baseRecord, scanned);
        Assert.Equal("艾尔登法环", updatedRecord.Title);
        Assert.Equal("17991234", updatedRecord.SteamBuildId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(unixUpdated), updatedRecord.SteamLastUpdatedUtc);
    }

    [Fact]
    public async Task Scanner_LocalizedChineseGame_LazilyQueriesSteamEnglishAppDetailsAndCachesToTitleMapWithoutBreakingChineseDisplay()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "艾尔登法环");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "eldenring.exe"), new byte[4096]);
        await File.WriteAllTextAsync(Path.Combine(gameDir, "steam_appid.txt"), "1245620", Encoding.UTF8);

        string gameId = SpawnId.NewUlid();
        var game = new GameRecord
        {
            GameId = gameId,
            Title = "艾尔登法环",
            SortTitle = "艾尔登法环",
            InstallDir = gameDir,
            ExecutableRelPath = "eldenring.exe",
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 1245620,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var requestedUrls = new List<string>();
        using var handler = new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            requestedUrls.Add(url);

            if (url.Contains("appids=1245620", StringComparison.Ordinal) && url.Contains("l=english", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"1245620":{"success":true,"data":{"name":"ELDEN RING"}}}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            httpClient: httpClient,
            options: new GameScanOptions { AllowOnlineMetadataLookup = true });

        // First call performs a single lazy appdetails?appids=1245620&l=english&filters=basic request and caches to trainer_title_map.
        TrainerTitleMapRecord first = await scanner.ResolveAndCacheEnglishTitleAsync(game, sandbox.Database);
        Assert.Equal("ELDEN RING", first.EnglishTitle);
        Assert.Equal(TrainerTitleSource.SteamAppDetails, first.TitleSource);
        Assert.Single(requestedUrls);
        Assert.Contains("l=english", requestedUrls[0], StringComparison.Ordinal);
        Assert.Contains("filters=basic", requestedUrls[0], StringComparison.Ordinal);

        // Second call hits permanent trainer_title_map cache (0 additional HTTP requests).
        TrainerTitleMapRecord second = await scanner.ResolveAndCacheEnglishTitleAsync(game, sandbox.Database);
        Assert.Equal("ELDEN RING", second.EnglishTitle);
        Assert.Equal(TrainerTitleSource.SteamAppDetails, second.TitleSource);
        Assert.Single(requestedUrls);

        // Localized Chinese display title in games table must remain 100% intact.
        GameRecord? persistedGame = await sandbox.Database.GetGameByIdAsync(gameId);
        Assert.NotNull(persistedGame);
        Assert.Equal("艾尔登法环", persistedGame!.Title);

        // Explicit user override takes top precedence and persists.
        TrainerTitleMapRecord overridden = await scanner.ResolveAndCacheEnglishTitleAsync(
            game,
            sandbox.Database,
            userSearchOverride: "Elden Ring Shadow of the Erdtree");
        Assert.Equal("Elden Ring Shadow of the Erdtree", overridden.UserSearchOverride);
        Assert.Equal("ELDEN RING", overridden.EnglishTitle);
        Assert.Single(requestedUrls);
    }

    [Fact]
    public async Task Scanner_EnglishTitleWaterfall_SupportsGogInfoAndGameTitleCleanerFallback()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // 1. GOG goggame-*.info "name"
        string gogDir = sandbox.CreateSubDirectory("games", "GogWitcher");
        await File.WriteAllBytesAsync(Path.Combine(gogDir, "witcher3.exe"), new byte[4096]);
        await File.WriteAllTextAsync(
            Path.Combine(gogDir, "goggame-1207664643.info"),
            """{"gameId":"1207664643","name":"The Witcher 3: Wild Hunt"}""",
            Encoding.UTF8);

        string gogGameId = SpawnId.NewUlid();
        var gogRecord = new GameRecord
        {
            GameId = gogGameId,
            Title = "巫师3：狂猎",
            InstallDir = gogDir,
            ExecutableRelPath = "witcher3.exe",
            PlatformSource = PlatformSource.GogOffline,
            GogGameId = "1207664643",
        };
        await sandbox.Database.UpsertGameAsync(gogRecord);

        // 2. Standalone dirty folder -> GameTitleCleaner fallback
        string cleanDir = sandbox.CreateSubDirectory("games", "[官中] Cyberpunk.2077.v2.12-SteamRIP");
        await File.WriteAllBytesAsync(Path.Combine(cleanDir, "Cyberpunk2077.exe"), new byte[4096]);
        string cleanGameId = SpawnId.NewUlid();
        var cleanRecord = new GameRecord
        {
            GameId = cleanGameId,
            Title = "赛博朋克 2077",
            InstallDir = cleanDir,
            ExecutableRelPath = "Cyberpunk2077.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        await sandbox.Database.UpsertGameAsync(cleanRecord);

        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });

        var gogResolved = await scanner.ResolveAndCacheEnglishTitleAsync(gogRecord, sandbox.Database);
        Assert.Equal("The Witcher 3: Wild Hunt", gogResolved.EnglishTitle);
        Assert.Equal(TrainerTitleSource.GogInfo, gogResolved.TitleSource);

        var cleanerResolved = await scanner.ResolveAndCacheEnglishTitleAsync(cleanRecord, sandbox.Database);
        Assert.Equal("Cyberpunk 2077", cleanerResolved.EnglishTitle);
        Assert.Equal(TrainerTitleSource.Cleaner, cleanerResolved.TitleSource);
    }

    [Fact]
    public void GameTitleCleaner_EnglishExtractionAndEngineVersionNoiseFilter_BehavesDeterministically()
    {
        // Bilingual / Chinese-wrapped titles
        Assert.Equal("Black Myth: Wukong", GameTitleCleaner.CleanEnglishTitle("黑神话：悟空 / Black Myth: Wukong"));
        Assert.Equal("Celeste", GameTitleCleaner.CleanEnglishTitle("蔚蓝 Celeste"));
        Assert.Equal("Hollow Knight", GameTitleCleaner.CleanEnglishTitle("[官中] Hollow.Knight.v1.5.78.11833-SteamRIP"));
        Assert.Null(GameTitleCleaner.CleanEnglishTitle("纯中文标题无英文"));
        Assert.True(GameTitleCleaner.IsLikelyEnglishTitle("ELDEN RING"));
        Assert.False(GameTitleCleaner.IsLikelyEnglishTitle("艾尔登法环"));

        // Unity (20xx.x.*, 6000.x.*) & Unreal (4.27.*, 5.x.*) PE version noise filtered
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("2020.3.48f1"));
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("2021.3.35.12345"));
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("2022.3.20.0"));
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("6000.0.12.0"));
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("4.27.2.0"));
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("5.1.1.0"));
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("5.4.4.0"));
        Assert.True(GameTitleCleaner.IsEngineVersionNoise("0.0.0.0"));

        // Real game versions preserved
        Assert.False(GameTitleCleaner.IsEngineVersionNoise("1.16.1"));
        Assert.False(GameTitleCleaner.IsEngineVersionNoise("1.0.2.0"));
        Assert.False(GameTitleCleaner.IsEngineVersionNoise("2.12.0"));
        Assert.False(GameTitleCleaner.IsEngineVersionNoise("3.0.4.1"));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
