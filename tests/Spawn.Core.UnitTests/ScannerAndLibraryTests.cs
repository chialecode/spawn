using System.IO.Compression;
using System.Text;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class ScannerAndLibraryTests
{
    [Fact]
    public async Task Scanner_GogOfflinePackageWithWebcacheZip_ExtractsGameIdPrimaryTaskAndOfflineArtwork()
    {
        await using var sandbox = new TestSandbox();
        string gogDir = sandbox.CreateSubDirectory("games", "GogLimboSynthetic");

        // Write goggame-1724299977.info with game + tool + URL tasks
        const string gogInfoJson = """
            {
              "clientId": "49494844895097688",
              "gameId": "1724299977",
              "name": "LIMBO",
              "playTasks": [
                { "category": "tool", "isPrimary": false, "name": "Language Setup", "path": "language_setup.exe", "type": "FileTask" },
                { "category": "game", "isPrimary": true, "name": "LIMBO", "path": "LIMBO.exe", "type": "FileTask" },
                { "category": "document", "link": "http://www.gog.com/support/limbo", "name": "Support", "type": "URLTask" }
              ]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(gogDir, "goggame-1724299977.info"), gogInfoJson, Encoding.UTF8);
        await File.WriteAllBytesAsync(Path.Combine(gogDir, "LIMBO.exe"), new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(gogDir, "language_setup.exe"), new byte[8192]);
        await File.WriteAllBytesAsync(Path.Combine(gogDir, "unins000.exe"), new byte[8192]);

        // Build authentic webcache.zip with resources.json + PNG/JPG image assets
        byte[] samplePng = CreateSolidPngBytes(32, 32, new SKColor(20, 30, 40));
        using (var zipFs = File.Create(Path.Combine(gogDir, "webcache.zip")))
        using (var zip = new ZipArchive(zipFs, ZipArchiveMode.Create))
        {
            var resEntry = zip.CreateEntry("resources.json");
            await using (var sw = new StreamWriter(resEntry.Open(), Encoding.UTF8))
            {
                await sw.WriteAsync("""
                    {
                      "images\\icon": "icon_hash.png",
                      "images\\logo2x": "logo2x_hash.png",
                      "images\\background": "bg_hash.png"
                    }
                    """);
            }

            foreach (string assetName in new[] { "icon_hash.png", "logo2x_hash.png", "bg_hash.png" })
            {
                var imgEntry = zip.CreateEntry(assetName);
                await using var s = imgEntry.Open();
                await s.WriteAsync(samplePng);
            }
        }

        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });

        var scanned = await scanner.ScanGameDirectoryAsync(gogDir, "test-gog-limbo");
        Assert.NotNull(scanned);
        Assert.Equal("LIMBO", scanned!.Title);
        Assert.Equal("1724299977", scanned.GogGameId);
        Assert.Equal("49494844895097688", scanned.GogClientId);
        Assert.Equal(48000, scanned.SteamAppId);
        Assert.Equal(PlatformSource.GogOffline, scanned.PlatformSource);
        Assert.Equal(AchievementCaptureMode.GogNativeSave, scanned.AchievementCaptureMode);
        Assert.Equal("LIMBO.exe", scanned.ExecutableRelPath, ignoreCase: true);

        Assert.False(string.IsNullOrWhiteSpace(scanned.IconPath));
        Assert.False(string.IsNullOrWhiteSpace(scanned.CoverImagePath));
        Assert.False(string.IsNullOrWhiteSpace(scanned.HeroImagePath));
        Assert.True(File.Exists(scanned.IconPath!));
        Assert.True(File.Exists(scanned.CoverImagePath!));
        Assert.StartsWith(sandbox.Paths.ArtworkDirectory, scanned.IconPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scanner_SteamripOnlineFixAndHeuristicExe_PrefersRealAppIdOver480AndFiltersCrashHandlers()
    {
        await using var sandbox = new TestSandbox();
        string steamripDir = sandbox.CreateSubDirectory("games", "[官中] Hollow.Knight.v1.5.78.11833-SteamRIP");

        // Write Spacewar 480 in steam_appid.txt, plus RealAppId=367520 in OnlineFix.ini
        await File.WriteAllTextAsync(Path.Combine(steamripDir, "steam_appid.txt"), "480\n", Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(steamripDir, "OnlineFix.ini"),
            """
            [Main]
            FakeAppId=480
            RealAppId=367520
            """,
            Encoding.UTF8);
        await File.WriteAllBytesAsync(Path.Combine(steamripDir, "steam_api64.dll"), [0x4D, 0x5A]);

        // Auxiliary/penalized executables vs main game executable
        await File.WriteAllBytesAsync(Path.Combine(steamripDir, "UnityCrashHandler64.exe"), new byte[16384]);
        await File.WriteAllBytesAsync(Path.Combine(steamripDir, "vc_redist.x64.exe"), new byte[32768]);
        await File.WriteAllBytesAsync(Path.Combine(steamripDir, "Launcher.exe"), new byte[8192]);
        await File.WriteAllBytesAsync(Path.Combine(steamripDir, "Hollow Knight.exe"), new byte[12288]);

        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });

        var scanned = await scanner.ScanGameDirectoryAsync(steamripDir, "test-steamrip-hk");
        Assert.NotNull(scanned);
        Assert.Equal(367520, scanned!.SteamAppId);
        Assert.Equal("Hollow Knight", scanned.Title);
        Assert.Equal("Hollow Knight.exe", scanned.ExecutableRelPath);
        Assert.Equal(PlatformSource.Steamrip, scanned.PlatformSource);
        Assert.Equal(AchievementCaptureMode.SteamEmulator, scanned.AchievementCaptureMode);
    }

    [Fact]
    public async Task Library_ImportRescanUpdateAndRemove_DeletesCachedArtworkOnlyNeverTouchesUserGameFiles()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string rootLibraryDir = sandbox.CreateSubDirectory("library-root");
        string gameFolder = Directory.CreateDirectory(Path.Combine(rootLibraryDir, "Celeste.v1.4.0.0-SteamRIP")).FullName;
        string gameExePath = Path.Combine(gameFolder, "Celeste.exe");
        await File.WriteAllBytesAsync(gameExePath, new byte[4096]);
        await File.WriteAllTextAsync(Path.Combine(gameFolder, "steam_appid.txt"), "504230", Encoding.UTF8);
        await File.WriteAllBytesAsync(Path.Combine(gameFolder, "cover.png"), CreateSolidPngBytes(24, 36, SKColors.CornflowerBlue));

        var importedEvents = new List<GameImportedOrRescannedEvent>();
        var removedEvents = new List<GameRemovedEvent>();
        var eventBus = new InMemorySpawnEventBus();
        using var subImport = eventBus.Subscribe<GameImportedOrRescannedEvent>(e => importedEvents.Add(e));
        using var subRemove = eventBus.Subscribe<GameRemovedEvent>(e => removedEvents.Add(e));

        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(guard, extractor, options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, eventBus);

        var discovered = await library.ScanLibraryDirectoryAsync(rootLibraryDir);
        Assert.Single(discovered);
        Assert.Single(importedEvents);
        Assert.True(importedEvents[0].IsNewImport);

        var imported = discovered[0];
        Assert.Equal("Celeste", imported.Title);
        Assert.Equal(504230, imported.SteamAppId);
        Assert.NotNull(imported.CoverImagePath);
        Assert.True(File.Exists(imported.CoverImagePath!));

        // Update metadata
        var updated = await library.UpdateGameMetadataAsync(imported.GameId, new GameMetadataUpdate
        {
            Title = "蔚蓝 Celeste",
            LaunchArguments = "--windowed",
        });
        Assert.NotNull(updated);
        Assert.Equal("蔚蓝 Celeste", updated!.Title);
        Assert.Equal("--windowed", updated.LaunchArguments);

        // Remove game -> cached artwork directory deleted, user's game folder & Celeste.exe 100% untouched!
        string cachedArtworkDir = sandbox.Paths.GetGameArtworkDirectory(imported.GameId);
        Assert.True(Directory.Exists(cachedArtworkDir));

        bool removed = await library.RemoveGameAsync(imported.GameId);
        Assert.True(removed);
        Assert.Single(removedEvents);
        Assert.Null(await library.GetGameByIdAsync(imported.GameId));
        Assert.False(Directory.Exists(cachedArtworkDir));
        Assert.True(File.Exists(gameExePath));
    }

    [Fact]
    public void GameTitleCleaner_DirtySceneReleaseFolderNames_StripsTagsAndComputesJaroWinkler()
    {
        Assert.Equal("Hollow Knight", GameTitleCleaner.CleanTitle("[官中] Hollow.Knight.v1.5.78.11833-SteamRIP"));
        Assert.Equal("Cyberpunk 2077", GameTitleCleaner.CleanTitle("Cyberpunk_2077_Ultimate_Edition_v2.12_FLT"));
        Assert.Equal("Witcher 3", GameTitleCleaner.BuildSortTitle("The Witcher 3"));
        Assert.Equal("RJ01123456", GameTitleCleaner.TryExtractDlsiteCode("[RJ01123456] Sample Doujin Work"));
        Assert.True(GameTitleCleaner.ComputeJaroWinklerSimilarity("LIMBO", "Limbo") == 1.0);
        Assert.True(GameTitleCleaner.ComputeJaroWinklerSimilarity("Hollow Knight", "Hollow Knight Silksong") > 0.80);
    }

    [Fact]
    public async Task Scanner_RealLocalGamesWorkspaceAllThreeTitles_ReadOnlyExtractsAccurateMetadataIconsAndCovers()
    {
        string? repoRoot = TestSandbox.TryLocateRepositoryRoot();
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return;
        }

        string localGamesRoot = Path.Combine(repoRoot, ".local", "games");
        if (!Directory.Exists(localGamesRoot))
        {
            return;
        }

        string limboDir = Path.Combine(localGamesRoot, "LIMBO");
        string stanleyDir = Path.Combine(localGamesRoot, "The Stanley Parable Ultra Deluxe");
        string vsDir = Path.Combine(localGamesRoot, "Vampire Survivors");
        if (!Directory.Exists(limboDir) || !Directory.Exists(stanleyDir) || !Directory.Exists(vsDir))
        {
            return;
        }

        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // Snapshot top-level entry counts inside each real game directory to prove 100% READ-ONLY behavior
        int limboEntriesBefore = Directory.GetFileSystemEntries(limboDir).Length;
        int stanleyEntriesBefore = Directory.GetFileSystemEntries(stanleyDir).Length;
        int vsEntriesBefore = Directory.GetFileSystemEntries(vsDir).Length;

        using var guard = new AntiCheatGuardService();
        var extractor = new GameArtworkExtractor(sandbox.Paths);
        var scanner = new GameDirectoryScanner(
            guard,
            extractor,
            options: new GameScanOptions
            {
                AllowOnlineMetadataLookup = true,
                OnlineLookupTimeout = TimeSpan.FromSeconds(2.5),
            });

        // 1. DiscoverCandidateGameDirectories must discover all 3 real games while skipping _OST / StreamingAssets
        var candidates = scanner.DiscoverCandidateGameDirectories(localGamesRoot);
        Assert.Contains(candidates, c => string.Equals(Path.GetFileName(c), "LIMBO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => string.Equals(Path.GetFileName(c), "The Stanley Parable Ultra Deluxe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => string.Equals(Path.GetFileName(c), "Vampire Survivors", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, c => string.Equals(Path.GetFileName(c), "_OST", StringComparison.OrdinalIgnoreCase));

        // 2. LIMBO (GOG Offline 1724299977 <-> Steam 48000, GogNativeSave, webcache.zip cover & icon)
        var limbo = await scanner.ScanGameDirectoryAsync(limboDir, "ro-scan-limbo");
        Assert.NotNull(limbo);
        Assert.Equal("LIMBO", limbo!.Title, ignoreCase: true);
        Assert.Equal("1724299977", limbo.GogGameId);
        Assert.Equal(48000, limbo.SteamAppId);
        Assert.Equal(PlatformSource.GogOffline, limbo.PlatformSource);
        Assert.Equal(AntiCheatTier.Tier0, limbo.AntiCheatTier);
        Assert.Equal(AchievementCaptureMode.GogNativeSave, limbo.AchievementCaptureMode);
        Assert.Equal("LIMBO.exe", limbo.ExecutableRelPath, ignoreCase: true);
        Assert.True(File.Exists(limbo.IconPath!));
        Assert.True(File.Exists(limbo.CoverImagePath!));
        Assert.StartsWith(sandbox.Paths.ArtworkDirectory, limbo.CoverImagePath!, StringComparison.OrdinalIgnoreCase);

        // 3. The Stanley Parable: Ultra Deluxe (Unity Mono + nested Plugins\x86_64\steam_api64.cdx + steam_emu.ini -> CODEX 1703340)
        var stanley = await scanner.ScanGameDirectoryAsync(stanleyDir, "ro-scan-stanley");
        Assert.NotNull(stanley);
        Assert.Contains("Stanley Parable", stanley!.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1703340, stanley.SteamAppId);
        Assert.Equal(PlatformSource.Steamrip, stanley.PlatformSource);
        Assert.Equal(AntiCheatTier.Tier0, stanley.AntiCheatTier);
        Assert.Equal(AchievementCaptureMode.SteamEmulator, stanley.AchievementCaptureMode);
        Assert.Contains("CODEX", stanley.AchievementChannelLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The Stanley Parable Ultra Deluxe.exe", stanley.ExecutableRelPath, ignoreCase: true);
        Assert.True(File.Exists(stanley.IconPath!));
        Assert.StartsWith(sandbox.Paths.ArtworkDirectory, stanley.IconPath!, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(stanley.CoverImagePath))
        {
            Assert.True(File.Exists(stanley.CoverImagePath));
            Assert.StartsWith(sandbox.Paths.ArtworkDirectory, stanley.CoverImagePath, StringComparison.OrdinalIgnoreCase);
        }

        // 4. Vampire Survivors (Unity IL2CPP + steam_appid.txt + nested Plugins\x86_64\steam_api64.rne -> RUNE 1794680, skips replication-server.exe & _OST)
        var vs = await scanner.ScanGameDirectoryAsync(vsDir, "ro-scan-vs");
        Assert.NotNull(vs);
        Assert.Contains("Vampire Survivors", vs!.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1794680, vs.SteamAppId);
        Assert.Equal(PlatformSource.Steamrip, vs.PlatformSource);
        Assert.Equal(AntiCheatTier.Tier0, vs.AntiCheatTier);
        Assert.Equal(AchievementCaptureMode.SteamEmulator, vs.AchievementCaptureMode);
        Assert.Contains("RUNE", vs.AchievementChannelLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("VampireSurvivors.exe", vs.ExecutableRelPath, ignoreCase: true);
        Assert.DoesNotContain("replication-server", vs.ExecutableRelPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(vs.IconPath!));
        Assert.StartsWith(sandbox.Paths.ArtworkDirectory, vs.IconPath!, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(vs.CoverImagePath))
        {
            Assert.True(File.Exists(vs.CoverImagePath));
            Assert.StartsWith(sandbox.Paths.ArtworkDirectory, vs.CoverImagePath, StringComparison.OrdinalIgnoreCase);
        }

        // Verify zero files were created or removed inside `.local/games`
        Assert.Equal(limboEntriesBefore, Directory.GetFileSystemEntries(limboDir).Length);
        Assert.Equal(stanleyEntriesBefore, Directory.GetFileSystemEntries(stanleyDir).Length);
        Assert.Equal(vsEntriesBefore, Directory.GetFileSystemEntries(vsDir).Length);
    }

    private static byte[] CreateSolidPngBytes(int width, int height, SKColor color)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(color);
        }

        using var img = SKImage.FromBitmap(bmp);
        using var encoded = img.Encode(SKEncodedImageFormat.Png, 90);
        return encoded.ToArray();
    }
}
