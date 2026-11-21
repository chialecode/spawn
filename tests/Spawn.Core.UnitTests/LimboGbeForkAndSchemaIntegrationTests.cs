using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Achievement;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class LimboGbeForkAndSchemaIntegrationTests
{
    [Fact]
    public async Task LimboRealWorkspaceAndSandboxEndToEnd_ReadOnlyScanChineseSchemaAndSubSecondRealtimeToast()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // Mute sound unconditionally in test overlay settings
        await sandbox.Database.SaveOverlaySettingsAsync(new OverlaySettingsDto
        {
            Theme = OverlayThemeKind.Ps5Trophy,
            Position = OverlayPositionKind.BottomRight,
            SoundVolume = 0.0,
            MuteSound = true,
        });

        string? repoRoot = TestSandbox.TryLocateRepositoryRoot();

        // Part A: If real `.local/games/LIMBO` exists on this workstation, perform 100% READ-ONLY scan verification
        // (NEVER launch limbo.exe, NEVER write any file into `.local/games/LIMBO`!).
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            string localLimboDir = Path.Combine(repoRoot, ".local", "games", "LIMBO");
            if (Directory.Exists(localLimboDir))
            {
                using var roGuard = new AntiCheatGuardService();
                var roExtractor = new GameArtworkExtractor(sandbox.Paths);
                var roScanner = new GameDirectoryScanner(
                    roGuard,
                    roExtractor,
                    options: new GameScanOptions { AllowOnlineMetadataLookup = false });

                var realLimboScan = await roScanner.ScanGameDirectoryAsync(localLimboDir, "readonly-limbo-probe");
                Assert.NotNull(realLimboScan);
                Assert.Equal("LIMBO", realLimboScan!.Title);
                Assert.Equal("1724299977", realLimboScan.GogGameId);
                Assert.Equal("49494844895097688", realLimboScan.GogClientId);
                Assert.Equal(48000, realLimboScan.SteamAppId);
                Assert.Equal(PlatformSource.GogOffline, realLimboScan.PlatformSource);
                Assert.Equal(AchievementCaptureMode.GogNativeSave, realLimboScan.AchievementCaptureMode);
                Assert.Equal("LIMBO.exe", realLimboScan.ExecutableRelPath, ignoreCase: true);
                Assert.False(string.IsNullOrWhiteSpace(realLimboScan.CoverImagePath));
                Assert.False(string.IsNullOrWhiteSpace(realLimboScan.IconPath));
                Assert.True(File.Exists(realLimboScan.CoverImagePath!));
                Assert.True(File.Exists(realLimboScan.IconPath!));
                Assert.StartsWith(sandbox.Paths.ArtworkDirectory, realLimboScan.CoverImagePath!, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Part B: Full End-to-End Isolated Sandbox Integration (Schema Sync + Initial Silent Seed + <1s Realtime Save Watcher -> Overlay IPC Toast)
        string sandboxLimboDir = sandbox.CreateSubDirectory("games", "LIMBO_Sandbox");
        await File.WriteAllTextAsync(
            Path.Combine(sandboxLimboDir, "goggame-1724299977.info"),
            """
            {
              "clientId": "49494844895097688",
              "gameId": "1724299977",
              "name": "LIMBO",
              "playTasks": [
                { "category": "game", "isPrimary": true, "name": "LIMBO", "path": "LIMBO.exe", "type": "FileTask" }
              ]
            }
            """,
            Encoding.UTF8);
        await File.WriteAllBytesAsync(Path.Combine(sandboxLimboDir, "LIMBO.exe"), new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(sandboxLimboDir, "limbo_boot.pkg"), [0x01, 0x02]);

        // Copy webcache.zip into sandbox if available so offline artwork is tested in the full pipeline too
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            string realWebcache = Path.Combine(repoRoot, ".local", "games", "LIMBO", "webcache.zip");
            if (File.Exists(realWebcache))
            {
                File.Copy(realWebcache, Path.Combine(sandboxLimboDir, "webcache.zip"), overwrite: true);
            }
        }

        // Pre-seed save_game/savegame.txt with ACH_1 already unlocked (mask = 1) BEFORE import to test Silent Initial Seed!
        string saveGameDir = Directory.CreateDirectory(Path.Combine(sandboxLimboDir, "save_game")).FullName;
        string saveFilePath = Path.Combine(saveGameDir, "savegame.txt");
        await File.WriteAllBytesAsync(saveFilePath, LimboSaveDecoder.EncryptFromText("achievements = \"1\"\n"));

        // Start in-process isolated Named Pipe OverlayIpcServer to capture real-time OverlayToastRequests
        var receivedToasts = new List<OverlayToastRequest>();
        var firstRealtimeToastTcs = new TaskCompletionSource<OverlayToastRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var platinumToastTcs = new TaskCompletionSource<OverlayToastRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var overlayServer = new OverlayIpcServer(sandbox.Paths);
        overlayServer.ToastReceived += (toast, _) =>
        {
            lock (receivedToasts)
            {
                receivedToasts.Add(toast);
            }

            if (toast.AchievementApiName == "ACH_3")
            {
                firstRealtimeToastTcs.TrySetResult(toast);
            }

            if (toast.IsPlatinumCompletion)
            {
                platinumToastTcs.TrySetResult(toast);
            }

            return Task.CompletedTask;
        };
        await overlayServer.StartAsync();

        var envOverrides = new AchievementEnvironmentOverrides
        {
            AppDataRoaming = sandbox.CreateSubDirectory("env", "AppDataRoaming"),
            LocalAppData = sandbox.CreateSubDirectory("env", "LocalAppData"),
            PublicDocuments = sandbox.CreateSubDirectory("env", "PublicDocs"),
            UserDocuments = sandbox.CreateSubDirectory("env", "UserDocs"),
            ProgramData = sandbox.CreateSubDirectory("env", "ProgramData"),
            SteamInstallPath = sandbox.CreateSubDirectory("env", "Steam"),
        };

        var services = new ServiceCollection();
        services.AddSpawnCore(sandbox.Paths);
        services.AddSingleton(sandbox.Database);
        services.AddSpawnLauncher(
            scan => scan.AllowOnlineMetadataLookup = false,
            launch => launch.ForceCreateNoWindow = true);
        services.AddSpawnAchievement(opt =>
        {
            opt.DebounceWindow = TimeSpan.FromMilliseconds(45);
            opt.ActiveGamePollInterval = TimeSpan.FromMilliseconds(90);
            opt.EnableGogCometAutoStart = false;
            // Tests never touch the real network: LIMBO resolves through the built-in offline Chinese schema.
            opt.AllowNetworkDownloads = false;
            opt.EnableCommunitySpoilerRequests = false;
            opt.EnvironmentOverrides = envOverrides;
        });

        await using var provider = services.BuildServiceProvider();
        var library = provider.GetRequiredService<IGameLibraryService>();
        var achService = provider.GetRequiredService<IAchievementService>();

        // 1. Import sandbox LIMBO -> triggers Chinese Schema sync (13 achievements, AppID 48000) + Silent Seed of ACH_1
        var importedGame = await library.ImportGameAsync(sandboxLimboDir);
        Assert.NotNull(importedGame);
        Assert.Equal("1724299977", importedGame!.GogGameId);
        Assert.Equal(48000, importedGame.SteamAppId);

        var summary = await achService.GetGameAchievementsAsync(importedGame.GameId);
        Assert.Equal(13, summary.TotalCount);
        Assert.Equal(1, summary.UnlockedCount); // ACH_1 seeded!

        // Verify Chinese titles, hidden spoiler description on ACH_13, and cached color + grayscale icons
        var ach13 = Assert.Single(summary.Items, i => i.Definition.ApiName == "ACH_13");
        Assert.True(ach13.Definition.IsHidden);
        Assert.False(string.IsNullOrWhiteSpace(ach13.Definition.HiddenSpoilerDescription));
        Assert.Equal("隐藏成就 · 点击展开查看条件", ach13.GetEffectiveDescription(revealHiddenSpoilers: false));
        Assert.False(string.IsNullOrWhiteSpace(ach13.GetEffectiveDescription(revealHiddenSpoilers: true)));
        Assert.True(File.Exists(ach13.Definition.IconPath!));
        Assert.True(File.Exists(ach13.Definition.IconGrayPath!));

        // Silent seed MUST NOT fire any popup toast for pre-existing ACH_1!
        lock (receivedToasts)
        {
            Assert.Empty(receivedToasts);
        }

        // 2. Start real-time file monitoring, simulate unlocking ACH_3 (bit 2 -> mask 1 | 4 = 5) in savegame.txt,
        //    and assert toast arrives in < 1.0s!
        await achService.StartMonitoringAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            await File.WriteAllBytesAsync(saveFilePath, LimboSaveDecoder.EncryptFromText("achievements = \"5\"\n"));

            var liveToast = await firstRealtimeToastTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(980));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000, $"Expected <1000ms capture latency, actual: {sw.ElapsedMilliseconds}ms");
            Assert.Equal("LIMBO", liveToast.GameTitle);
            Assert.Equal("ACH_3", liveToast.AchievementApiName);
            Assert.True(liveToast.MuteSound);

            // Ensure ACH_1 was NOT re-toasted
            lock (receivedToasts)
            {
                Assert.DoesNotContain(receivedToasts, t => t.AchievementApiName == "ACH_1");
            }

            // 3. Simulate unlocking all 13 achievements (mask = (1 << 13) - 1 = 8191) -> triggers 100% Platinum Completion flag!
            await File.WriteAllBytesAsync(saveFilePath, LimboSaveDecoder.EncryptFromText("achievements = \"8191\"\n"));
            var platToast = await platinumToastTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(platToast.IsPlatinumCompletion);

            var finalSummary = await achService.GetGameAchievementsAsync(importedGame.GameId);
            Assert.Equal(13, finalSummary.UnlockedCount);
            Assert.Equal(100.0, finalSummary.CompletionPercent);
        }
        finally
        {
            await achService.StopMonitoringAsync();
        }
    }

    [Fact]
    public async Task GbeForkAndSchema_Tier0BackupAndByteExactRestore_EnforcesAntiCheatAndSteamAppsGuards()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // 1. Prepare a Tier 0 Steamrip game with an original 64-bit steam_api64.dll
        string tier0Dir = sandbox.CreateSubDirectory("games", "FltStubGame");
        string dllPath = Path.Combine(tier0Dir, "steam_api64.dll");
        byte[] originalDllBytes = BuildSyntheticPe64DllBytes();
        string expectedOriginalSha256 = Convert.ToHexStringLower(SHA256.HashData(originalDllBytes));

        await File.WriteAllBytesAsync(dllPath, originalDllBytes);
        await File.WriteAllBytesAsync(Path.Combine(tier0Dir, "game.exe"), new byte[2048]);
        await File.WriteAllTextAsync(Path.Combine(tier0Dir, "steam_appid.txt"), "48000", Encoding.UTF8);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "FLT Offline Title",
            InstallDir = tier0Dir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.Steamrip,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 48000,
            AchievementCaptureMode = AchievementCaptureMode.PendingGbeForkActivation,
        };
        await sandbox.Database.UpsertGameAsync(game);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
        var activator = new GbeForkActivatorService(
            sandbox.Paths,
            sandbox.Database,
            guard,
            new AchievementModuleOptions { AllowNetworkDownloads = false },
            binarySource: new InMemoryGbeForkBinarySource());

        Assert.True(GbeForkActivatorService.IsCandidateForActivation(game));

        var sampleDefs = NativeSaveAchievementProvider.CreateBuiltInLimboRule().FallbackChineseSchema;
        var activateRes = await activator.ActivateAsync(game, 48000, sampleDefs);
        Assert.True(activateRes.Succeeded, activateRes.Message);
        Assert.Equal(expectedOriginalSha256, activateRes.OriginalDllSha256);
        Assert.True(File.Exists(dllPath + GbeForkActivatorService.BackupSuffix));
        Assert.True(File.Exists(Path.Combine(tier0Dir, "steam_settings", "achievements.json")));
        Assert.True(File.Exists(Path.Combine(tier0Dir, "steam_settings", "configs.user.ini")));

        // Verify active DLL was replaced by shim
        string activeShaAfterActivate = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dllPath)));
        Assert.NotEqual(expectedOriginalSha256, activeShaAfterActivate);

        // Restore original DLL -> SHA-256 must match 100% byte-for-byte, and generated files cleaned up!
        var restoreRes = await activator.RestoreOriginalDllAsync(game);
        Assert.True(restoreRes.Succeeded, restoreRes.Message);

        string restoredSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dllPath)));
        Assert.Equal(expectedOriginalSha256, restoredSha);
        Assert.False(File.Exists(dllPath + GbeForkActivatorService.BackupSuffix));
        Assert.False(Directory.Exists(Path.Combine(tier0Dir, "steam_settings")));

        // 2. Security Gate: Tier 2 game MUST be blocked
        var tier2Game = game with { GameId = SpawnId.NewUlid(), AntiCheatTier = AntiCheatTier.Tier2 };
        var tier2Res = await activator.ActivateAsync(tier2Game, 48000, sampleDefs);
        Assert.False(tier2Res.Succeeded);

        // 3. Security Gate: Official steamapps\common path MUST be blocked
        string steamAppsCommonDir = sandbox.CreateSubDirectory("steamlib", "steamapps", "common", "OfficialGame");
        await File.WriteAllBytesAsync(Path.Combine(steamAppsCommonDir, "steam_api64.dll"), originalDllBytes);
        var officialSteamGame = game with { GameId = SpawnId.NewUlid(), InstallDir = steamAppsCommonDir };
        var steamAppsRes = await activator.ActivateAsync(officialSteamGame, 48000, sampleDefs);
        Assert.False(steamAppsRes.Succeeded);
    }

    [Fact]
    public void SchemaAndGrayscaleIconSynthesis_Rec709ColorMatrix_ProducesGrayscalePngAndBackfillsSpoilers()
    {
        // 1. Synthesize Rec.709 grayscale from saturated red-orange icon
        byte[] vividColorPng;
        using (var bmp = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(new SKColor(240, 60, 20, 255));
            }

            using var img = SKImage.FromBitmap(bmp);
            vividColorPng = img.Encode(SKEncodedImageFormat.Png, 95).ToArray();
        }

        byte[]? grayPng = AchievementIconProcessor.SynthesizeRec709GrayscalePng(vividColorPng);
        Assert.NotNull(grayPng);

        using var decodedGray = SKBitmap.Decode(grayPng);
        Assert.NotNull(decodedGray);
        SKColor center = decodedGray.GetPixel(16, 16);
        Assert.InRange(Math.Abs(center.Red - center.Green), 0, 2);
        Assert.InRange(Math.Abs(center.Green - center.Blue), 0, 2);

        // 2. Waterfall merge backfills stripped hidden=1 description & rarity percentage
        var primaryFromWebApi = new[]
        {
            new AchievementDefinitionDto
            {
                ApiName = "ACH_HIDDEN_BOSS",
                DisplayName = "Secret Victor",
                Description = string.Empty, // Stripped by official Steam GetSchemaForGame!
                IsHidden = true,
            },
        };
        var communityLayer = new[]
        {
            new AchievementDefinitionDto
            {
                ApiName = "ACH_HIDDEN_BOSS",
                DisplayName = "隐藏霸主",
                Description = "无伤击败深渊隐藏首领",
                HiddenSpoilerDescription = "无伤击败深渊隐藏首领",
                IsHidden = true,
            },
        };
        var rarity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["ACH_HIDDEN_BOSS"] = 2.45,
        };

        var merged = AchievementSchemaService.MergeWaterfallLayers(
            primaryFromWebApi,
            localSteamBin: [],
            localEmu: [],
            community: communityLayer,
            builtInRule: [],
            globalPercents: rarity);

        Assert.Single(merged);
        Assert.Equal("隐藏霸主", merged[0].DisplayName);
        Assert.Equal("无伤击败深渊隐藏首领", merged[0].HiddenSpoilerDescription);
        Assert.Equal(2.45, merged[0].GlobalPercent);
        Assert.True(merged[0].IsRare);
    }

    [Fact]
    public async Task GbeFork_NestedUnityPluginsX86_64Dll_ActivatesRestoresByteExactAndSkipsSceneCdxRneGames()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // 1. Unity game with nested <Game>_Data\Plugins\x86_64\steam_api64.dll (unconfigured vanilla/FLT stub)
        string unityFltDir = sandbox.CreateSubDirectory("games", "UnityFltNested");
        await File.WriteAllBytesAsync(Path.Combine(unityFltDir, "UnityFlt.exe"), new byte[2048]);
        string pluginsDir = Directory.CreateDirectory(
            Path.Combine(unityFltDir, "UnityFlt_Data", "Plugins", "x86_64")).FullName;
        string nestedDllPath = Path.Combine(pluginsDir, "steam_api64.dll");

        byte[] originalDllBytes = BuildSyntheticPe64DllBytes();
        string expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(originalDllBytes));
        await File.WriteAllBytesAsync(nestedDllPath, originalDllBytes);
        await File.WriteAllTextAsync(Path.Combine(unityFltDir, "steam_appid.txt"), "48000", Encoding.UTF8);

        var nestedGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Unity Nested Stub",
            InstallDir = unityFltDir,
            ExecutableRelPath = "UnityFlt.exe",
            PlatformSource = PlatformSource.Steamrip,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 48000,
        };
        await sandbox.Database.UpsertGameAsync(nestedGame);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
        var activator = new GbeForkActivatorService(
            sandbox.Paths,
            sandbox.Database,
            guard,
            new AchievementModuleOptions { AllowNetworkDownloads = false },
            binarySource: new InMemoryGbeForkBinarySource());

        Assert.True(GbeForkActivatorService.IsCandidateForActivation(nestedGame));

        var defs = NativeSaveAchievementProvider.CreateBuiltInLimboRule().FallbackChineseSchema;
        var actRes = await activator.ActivateAsync(nestedGame, 48000, defs);
        Assert.True(actRes.Succeeded, actRes.Message);
        Assert.True(File.Exists(nestedDllPath + GbeForkActivatorService.BackupSuffix));
        Assert.True(File.Exists(Path.Combine(pluginsDir, "steam_settings", "achievements.json")));

        var resRes = await activator.RestoreOriginalDllAsync(nestedGame);
        Assert.True(resRes.Succeeded, resRes.Message);
        string restoredSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(nestedDllPath)));
        Assert.Equal(expectedSha256, restoredSha);
        Assert.False(Directory.Exists(Path.Combine(pluginsDir, "steam_settings")));

        // 2. Unity game that ALREADY ships CODEX (.cdx + steam_emu.ini) or RUNE (.rne + steam_emu.ini):
        //    - IsCandidateForActivation stays false (not flagged as broken unconfigured FLT),
        //    - Yet user-triggered ActivateAsync upgrades nested steam_api64.dll to gbe_fork with full schema & restores 100% byte-exact!
        await File.WriteAllBytesAsync(Path.Combine(pluginsDir, "steam_api64.cdx"), [0x4D, 0x5A]);
        await File.WriteAllTextAsync(Path.Combine(pluginsDir, "steam_emu.ini"), "[Settings]\nAppId=1703340\nUserName=CODEX\n", Encoding.UTF8);
        Assert.False(GbeForkActivatorService.IsCandidateForActivation(nestedGame));

        var codexUpgradeRes = await activator.ActivateAsync(nestedGame, 1703340, defs);
        Assert.True(codexUpgradeRes.Succeeded, codexUpgradeRes.Message);
        Assert.True(File.Exists(Path.Combine(pluginsDir, "steam_settings", "achievements.json")));

        var codexRestoreRes = await activator.RestoreOriginalDllAsync(nestedGame);
        Assert.True(codexRestoreRes.Succeeded, codexRestoreRes.Message);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(nestedDllPath))));
        Assert.True(File.Exists(Path.Combine(pluginsDir, "steam_api64.cdx")));
    }

    [Fact]
    public async Task Schema_OfflineFakeSteamWebApi_SyncsChineseSchemasFor48000And1703340And220Plus1794680()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string limboDir = sandbox.CreateSubDirectory("games", "LIMBO");
        string stanleyDir = sandbox.CreateSubDirectory("games", "The Stanley Parable Ultra Deluxe");
        string vsDir = sandbox.CreateSubDirectory("games", "Vampire Survivors");

        var nativeProvider = new NativeSaveAchievementProvider();
        var iconProcessor = new AchievementIconProcessor(sandbox.Paths);
        using var offlineHandler = new OfflineSteamApiFakeHandler(new Dictionary<int, int>
        {
            [1703340] = 24,
            [1794680] = 236,
        });
        using var httpClient = new HttpClient(offlineHandler) { Timeout = TimeSpan.FromSeconds(10) };

        using var schemaService = new AchievementSchemaService(
            new FixedSteamApiKeyProvider("OFFLINE-TEST-KEY"),
            nativeProvider,
            iconProcessor,
            new AchievementModuleOptions
            {
                AllowNetworkDownloads = true,
                EnableCommunitySpoilerRequests = false,
            },
            httpClient);

        // 1. LIMBO (GOG 1724299977 <-> Steam 48000): fake GOG/Steam endpoints return 404 -> built-in 13 Chinese achievements + ACH_13 spoiler truth
        var limboSchema = await schemaService.FetchAndBuildSchemaAsync(new GameRecord
        {
            GameId = "offline-schema-limbo",
            Title = "LIMBO",
            InstallDir = limboDir,
            ExecutableRelPath = "LIMBO.exe",
            PlatformSource = PlatformSource.GogOffline,
            GogGameId = "1724299977",
            SteamAppId = 48000,
        });
        Assert.Equal(13, limboSchema.Definitions.Count);
        Assert.Contains(limboSchema.Definitions, d => d.ApiName == "ACH_13" && !string.IsNullOrWhiteSpace(d.HiddenSpoilerDescription));

        // 2. The Stanley Parable: Ultra Deluxe (CODEX 1703340 -> Chinese SteamWebApi schema served by the fake handler)
        var stanleySchema = await schemaService.FetchAndBuildSchemaAsync(new GameRecord
        {
            GameId = "offline-schema-stanley",
            Title = "The Stanley Parable: Ultra Deluxe",
            InstallDir = stanleyDir,
            ExecutableRelPath = "The Stanley Parable Ultra Deluxe.exe",
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 1703340,
        });
        Assert.Equal(AchievementSchemaSource.SteamWebApi, stanleySchema.Source);
        Assert.Equal(24, stanleySchema.Definitions.Count);
        Assert.Contains(stanleySchema.Definitions, d => d.DisplayName.Contains("成就", StringComparison.Ordinal));
        Assert.All(stanleySchema.Definitions, d =>
        {
            Assert.True(File.Exists(d.IconPath!));
            Assert.True(File.Exists(d.IconGrayPath!));
        });

        // 3. Vampire Survivors (RUNE 1794680 -> 220+ Chinese SteamWebApi achievements + cached icons)
        var vsSchema = await schemaService.FetchAndBuildSchemaAsync(new GameRecord
        {
            GameId = "offline-schema-vs",
            Title = "Vampire Survivors",
            InstallDir = vsDir,
            ExecutableRelPath = "VampireSurvivors.exe",
            PlatformSource = PlatformSource.Steamrip,
            SteamAppId = 1794680,
        });
        Assert.Equal(AchievementSchemaSource.SteamWebApi, vsSchema.Source);
        Assert.True(vsSchema.Definitions.Count >= 220, $"Expected >=220 achievements for Vampire Survivors (1794680), got {vsSchema.Definitions.Count}");
        Assert.True(File.Exists(vsSchema.Definitions[0].IconPath!));
        Assert.True(File.Exists(vsSchema.Definitions[0].IconGrayPath!));

        // Every request was answered in-process; nothing ever reached a real socket.
        Assert.Contains(offlineHandler.RequestedUris, u => u.Contains("GetSchemaForGame", StringComparison.Ordinal) && u.Contains("appid=1794680", StringComparison.Ordinal));
        Assert.Empty(offlineHandler.UnexpectedUris);
    }

    [Fact]
    public async Task Achievement_PostCleanupNonExistentCodexAndRuneDirectories_NewlyCreatedAchievementsIniFiresSubSecondRealtimeToasts()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        await sandbox.Database.SaveOverlaySettingsAsync(new OverlaySettingsDto
        {
            Theme = OverlayThemeKind.Steam,
            Position = OverlayPositionKind.BottomRight,
            SoundVolume = 0.0,
            MuteSound = true,
        });

        string hkcuPrefix = $@"Software\SpawnTests_{Guid.NewGuid():N}";
        string appDataRoaming = sandbox.CreateSubDirectory("env", "AppDataRoaming");
        string localAppData = sandbox.CreateSubDirectory("env", "LocalAppData");
        string localLow = sandbox.CreateSubDirectory("env", "LocalLow");
        string publicDocs = sandbox.CreateSubDirectory("env", "PublicDocs");
        string userDocs = sandbox.CreateSubDirectory("env", "UserDocs");
        string programData = sandbox.CreateSubDirectory("env", "ProgramData");
        string steamInstall = sandbox.CreateSubDirectory("env", "Steam");

        try
        {
            // 1. Prepare synthetic CODEX (1703340) & RUNE (1794680) games matching real nested Plugins\x86_64 layout
            string stanleyDir = sandbox.CreateSubDirectory("games", "StanleyCodexLive");
            await File.WriteAllBytesAsync(Path.Combine(stanleyDir, "The Stanley Parable Ultra Deluxe.exe"), new byte[2048]);
            string stanleyPlugins = Directory.CreateDirectory(
                Path.Combine(stanleyDir, "The Stanley Parable Ultra Deluxe_Data", "Plugins", "x86_64")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(stanleyPlugins, "steam_api64.cdx"), [0x4D, 0x5A]);
            await File.WriteAllTextAsync(
                Path.Combine(stanleyPlugins, "steam_emu.ini"),
                "[Settings]\nAppId=1703340\nUserName=CODEX\n",
                Encoding.UTF8);

            string vsDir = sandbox.CreateSubDirectory("games", "VampireRuneLive");
            await File.WriteAllBytesAsync(Path.Combine(vsDir, "VampireSurvivors.exe"), new byte[2048]);
            string vsPlugins = Directory.CreateDirectory(
                Path.Combine(vsDir, "VampireSurvivors_Data", "Plugins", "x86_64")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(vsPlugins, "steam_api64.rne"), [0x4D, 0x5A]);
            await File.WriteAllTextAsync(
                Path.Combine(vsPlugins, "steam_emu.ini"),
                "[Settings]\nAppId=1794680\nUserName=RUNE\n",
                Encoding.UTF8);

            // Pre-seed old achievements.ini files before cleanup
            string codexGameDir = Directory.CreateDirectory(Path.Combine(publicDocs, "Steam", "CODEX", "1703340")).FullName;
            string codexAchPath = Path.Combine(codexGameDir, "achievements.ini");
            await File.WriteAllTextAsync(codexAchPath, "[ACH_STANLEY_FIRST]\nAchieved=1\nUnlockTime=1730000100\n", Encoding.UTF8);

            string runeGameDir = Directory.CreateDirectory(Path.Combine(publicDocs, "Steam", "RUNE", "1794680")).FullName;
            string runeAchPath = Path.Combine(runeGameDir, "achievements.ini");
            await File.WriteAllTextAsync(runeAchPath, "[ACH_VS_FIRST]\nAchieved=1\nUnlockTime=1730000200\n", Encoding.UTF8);

            var codexToastTcs = new TaskCompletionSource<OverlayToastRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runeToastTcs = new TaskCompletionSource<OverlayToastRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var overlayServer = new OverlayIpcServer(sandbox.Paths);
            overlayServer.ToastReceived += (toast, _) =>
            {
                if (toast.AchievementApiName == "ACH_STANLEY_FIRST")
                {
                    codexToastTcs.TrySetResult(toast);
                }
                else if (toast.AchievementApiName == "ACH_VS_FIRST")
                {
                    runeToastTcs.TrySetResult(toast);
                }

                return Task.CompletedTask;
            };
            await overlayServer.StartAsync();

            var services = new ServiceCollection();
            services.AddSpawnCore(sandbox.Paths);
            services.AddSingleton(sandbox.Database);
            services.AddSpawnLauncher(
                scan => scan.AllowOnlineMetadataLookup = false,
                launch => launch.ForceCreateNoWindow = true,
                gd =>
                {
                    gd.AllowOnlinePcGamingWikiLookup = false;
                    gd.AppDataRoamingOverride = appDataRoaming;
                    gd.LocalAppDataOverride = localAppData;
                    gd.LocalLowOverride = localLow;
                    gd.PublicDocumentsOverride = publicDocs;
                    gd.UserDocumentsOverride = userDocs;
                    gd.ProgramDataOverride = programData;
                    gd.SteamInstallDirOverride = steamInstall;
                    gd.HkcuSoftwarePrefix = hkcuPrefix;
                });
            services.AddSpawnAchievement(ach =>
            {
                ach.DebounceWindow = TimeSpan.FromMilliseconds(45);
                ach.ActiveGamePollInterval = TimeSpan.FromMilliseconds(85);
                ach.AllowNetworkDownloads = false;
                ach.EnableGogCometAutoStart = false;
                ach.EnableCommunitySpoilerRequests = false;
                ach.EnvironmentOverrides = new AchievementEnvironmentOverrides
                {
                    AppDataRoaming = appDataRoaming,
                    LocalAppData = localAppData,
                    PublicDocuments = publicDocs,
                    UserDocuments = userDocs,
                    ProgramData = programData,
                    SteamInstallPath = steamInstall,
                };
            });

            await using var provider = services.BuildServiceProvider();
            var library = provider.GetRequiredService<IGameLibraryService>();
            var achService = provider.GetRequiredService<IAchievementService>();
            var dataService = provider.GetRequiredService<IGameDataManagementService>();

            var stanley = await library.ImportGameAsync(stanleyDir);
            var vs = await library.ImportGameAsync(vsDir);
            Assert.NotNull(stanley);
            Assert.NotNull(vs);

            await sandbox.Database.UpsertAchievementSchemaAsync(
                stanley!.GameId,
                "Steam_1703340",
                AchievementSchemaSource.LocalEmulatorSchema,
                "schinese",
                [new AchievementDefinitionDto { ApiName = "ACH_STANLEY_FIRST", DisplayName = "门后抉择", Description = "推开第一扇门", SortOrder = 0 }]);
            await sandbox.Database.UpsertAchievementSchemaAsync(
                vs!.GameId,
                "Steam_1794680",
                AchievementSchemaSource.LocalEmulatorSchema,
                "schinese",
                [new AchievementDefinitionDto { ApiName = "ACH_VS_FIRST", DisplayName = "夜幕幸存者", Description = "击败首个首领", SortOrder = 0 }]);

            await achService.ScanLocalUnlocksAsync(stanley.GameId, silentSeed: true);
            await achService.ScanLocalUnlocksAsync(vs.GameId, silentSeed: true);

            await achService.StartMonitoringAsync();
            try
            {
                // 2. Clean BOTH games while monitoring is active so CODEX\1703340 & RUNE\1794680 directories are completely removed!
                var stanleyEntries = await dataService.InspectGameDataAsync(stanley.GameId);
                var stanleyClean = await dataService.CleanGameDataAsync(new GameDataCleanRequest
                {
                    GameId = stanley.GameId,
                    EntryIds = [.. stanleyEntries.Where(e => e.Exists && e.IsSafeToClean).Select(e => e.EntryId)],
                    ResetSpawnAchievementsAndPlaytime = true,
                    CreateUndoBackup = true,
                });
                Assert.True(stanleyClean.Succeeded, stanleyClean.Message);

                var vsEntries = await dataService.InspectGameDataAsync(vs.GameId);
                var vsClean = await dataService.CleanGameDataAsync(new GameDataCleanRequest
                {
                    GameId = vs.GameId,
                    EntryIds = [.. vsEntries.Where(e => e.Exists && e.IsSafeToClean).Select(e => e.EntryId)],
                    ResetSpawnAchievementsAndPlaytime = true,
                    CreateUndoBackup = true,
                });
                Assert.True(vsClean.Succeeded, vsClean.Message);

                // Even remove PublicDocs\Steam itself to prove multi-level upward parent FSW + self-healing works from zero folders!
                string steamPubRoot = Path.Combine(publicDocs, "Steam");
                if (Directory.Exists(steamPubRoot))
                {
                    Directory.Delete(steamPubRoot, recursive: true);
                }

                Assert.False(Directory.Exists(codexGameDir));
                Assert.False(Directory.Exists(runeGameDir));

                // 3. Simulate CODEX game launching fresh, creating PublicDocs\Steam\CODEX\1703340\achievements.ini & unlocking ACH_STANLEY_FIRST
                var swCodex = Stopwatch.StartNew();
                Directory.CreateDirectory(codexGameDir);
                await File.WriteAllTextAsync(
                    codexAchPath,
                    "[SteamAchievements]\nCount=1\n00000=ACH_STANLEY_FIRST\n\n[ACH_STANLEY_FIRST]\nAchieved=1\nUnlockTime=1732000500\n",
                    Encoding.UTF8);

                var codexToast = await codexToastTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(980));
                swCodex.Stop();
                Assert.True(swCodex.ElapsedMilliseconds < 1000, $"Expected CODEX post-clean toast <1000ms, got {swCodex.ElapsedMilliseconds}ms");
                Assert.Equal("ACH_STANLEY_FIRST", codexToast.AchievementApiName);
                Assert.True(codexToast.MuteSound);

                // 4. Simulate RUNE game launching fresh, creating PublicDocs\Steam\RUNE\1794680\achievements.ini & unlocking ACH_VS_FIRST
                var swRune = Stopwatch.StartNew();
                Directory.CreateDirectory(runeGameDir);
                await File.WriteAllTextAsync(
                    runeAchPath,
                    "[SteamAchievements]\nCount=1\n00000=ACH_VS_FIRST\n\n[ACH_VS_FIRST]\nAchieved=1\nUnlockTime=1732000600\n",
                    Encoding.UTF8);

                var runeToast = await runeToastTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(980));
                swRune.Stop();
                Assert.True(swRune.ElapsedMilliseconds < 1000, $"Expected RUNE post-clean toast <1000ms, got {swRune.ElapsedMilliseconds}ms");
                Assert.Equal("ACH_VS_FIRST", runeToast.AchievementApiName);
                Assert.True(runeToast.MuteSound);
            }
            finally
            {
                await achService.StopMonitoringAsync();
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(hkcuPrefix, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Achievement_VampireSurvivorsRuneRemoteSavedataJson_StartupSyncAndSubSecondIncrementalRealtimeToast()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        await sandbox.Database.SaveOverlaySettingsAsync(new OverlaySettingsDto
        {
            Theme = OverlayThemeKind.Steam,
            Position = OverlayPositionKind.BottomRight,
            SoundVolume = 0.0,
            MuteSound = true,
        });

        string appDataRoaming = sandbox.CreateSubDirectory("env", "AppDataRoaming");
        string localAppData = sandbox.CreateSubDirectory("env", "LocalAppData");
        string localLow = sandbox.CreateSubDirectory("env", "LocalLow");
        string publicDocs = sandbox.CreateSubDirectory("env", "Public", "Documents");
        string userDocs = sandbox.CreateSubDirectory("env", "UserDocs");
        string programData = sandbox.CreateSubDirectory("env", "ProgramData");
        string steamInstall = sandbox.CreateSubDirectory("env", "Steam");

        // 1. Reproduce exact Vampire Survivors (1794680) RUNE layout where achievements.ini stays Count=0
        //    while actual unlocked achievement API names are persisted in RUNE\1794680\remote\savedata JSON!
        string vsDir = sandbox.CreateSubDirectory("games", "Vampire Survivors");
        await File.WriteAllBytesAsync(Path.Combine(vsDir, "VampireSurvivors.exe"), new byte[2048]);
        await File.WriteAllTextAsync(Path.Combine(vsDir, "steam_appid.txt"), "1794680\n", Encoding.UTF8);
        string vsPlugins = Directory.CreateDirectory(
            Path.Combine(vsDir, "VampireSurvivors_Data", "Plugins", "x86_64")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(vsPlugins, "steam_api64.dll"), BuildSyntheticPe64DllBytes());
        await File.WriteAllBytesAsync(Path.Combine(vsPlugins, "steam_api64.rne"), [0x4D, 0x5A]);
        await File.WriteAllTextAsync(
            Path.Combine(vsPlugins, "steam_emu.ini"),
            "[Settings]\nAppId=1794680\nUserName=RUNE\n",
            Encoding.UTF8);

        string runeAppDir = Directory.CreateDirectory(Path.Combine(publicDocs, "Steam", "RUNE", "1794680")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(runeAppDir, "achievements.ini"),
            "[SteamAchievements]\nCount=0\n",
            Encoding.UTF8);

        string runeRemoteDir = Directory.CreateDirectory(Path.Combine(runeAppDir, "remote")).FullName;
        string savedataPath = Path.Combine(runeRemoteDir, "savedata");
        await File.WriteAllTextAsync(
            savedataPath,
            """
            {
              "Coins": 120,
              "SelectedCharacter": "ANTONIO",
              "Achievements": [
                "ReachLV5",
                "Survive1Minute",
                "GetHolyWaterToLevel4"
              ]
            }
            """,
            Encoding.UTF8);

        var liveToastTcs = new TaskCompletionSource<OverlayToastRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedToasts = new List<OverlayToastRequest>();

        await using var overlayServer = new OverlayIpcServer(sandbox.Paths);
        overlayServer.ToastReceived += (toast, _) =>
        {
            lock (receivedToasts)
            {
                receivedToasts.Add(toast);
            }

            if (toast.AchievementApiName == "Kill3000Enemies")
            {
                liveToastTcs.TrySetResult(toast);
            }

            return Task.CompletedTask;
        };
        await overlayServer.StartAsync();

        var services = new ServiceCollection();
        services.AddSpawnCore(sandbox.Paths);
        services.AddSingleton(sandbox.Database);
        services.AddSpawnLauncher(
            scan => scan.AllowOnlineMetadataLookup = false,
            launch => launch.ForceCreateNoWindow = true);
        services.AddSpawnAchievement(ach =>
        {
            ach.DebounceWindow = TimeSpan.FromMilliseconds(45);
            ach.ActiveGamePollInterval = TimeSpan.FromMilliseconds(85);
            ach.AllowNetworkDownloads = false;
            ach.EnableGogCometAutoStart = false;
            ach.EnableCommunitySpoilerRequests = false;
            ach.EnvironmentOverrides = new AchievementEnvironmentOverrides
            {
                AppDataRoaming = appDataRoaming,
                LocalAppData = localAppData,
                LocalLow = localLow,
                PublicDocuments = publicDocs,
                UserDocuments = userDocs,
                ProgramData = programData,
                SteamInstallPath = steamInstall,
            };
        });

        await using var provider = services.BuildServiceProvider();
        var library = provider.GetRequiredService<IGameLibraryService>();
        var achService = provider.GetRequiredService<IAchievementService>();

        var vs = await library.ImportGameAsync(vsDir);
        Assert.NotNull(vs);
        Assert.Equal(1794680, vs!.SteamAppId);

        await sandbox.Database.UpsertAchievementSchemaAsync(
            vs.GameId,
            "Steam_1794680",
            AchievementSchemaSource.SteamWebApi,
            "schinese",
            [
                new AchievementDefinitionDto { ApiName = "ReachLV5", DisplayName = "达到 5 级", Description = "任意角色升至 5 级", SortOrder = 0 },
                new AchievementDefinitionDto { ApiName = "Survive1Minute", DisplayName = "存活 1 分钟", Description = "单局存活满 1 分钟", SortOrder = 1 },
                new AchievementDefinitionDto { ApiName = "GetHolyWaterToLevel4", DisplayName = "圣水 4 级", Description = "将圣水升至 4 级", SortOrder = 2 },
                new AchievementDefinitionDto { ApiName = "Kill3000Enemies", DisplayName = "讨伐三千敌", Description = "累计击败 3000 名敌人", SortOrder = 3 },
            ]);

        await achService.StartMonitoringAsync();
        try
        {
            // 2. Startup monitoring syncs all 3 pre-existing unlocks from RUNE\1794680\remote\savedata JSON!
            var summaryAfterStartup = await achService.GetGameAchievementsAsync(vs.GameId);
            Assert.Equal(3, summaryAfterStartup.UnlockedCount);
            Assert.Contains(summaryAfterStartup.Items, i => i.Definition.ApiName == "ReachLV5" && i.IsUnlocked);
            Assert.Contains(summaryAfterStartup.Items, i => i.Definition.ApiName == "Survive1Minute" && i.IsUnlocked);
            Assert.Contains(summaryAfterStartup.Items, i => i.Definition.ApiName == "GetHolyWaterToLevel4" && i.IsUnlocked);

            // 3. Simulate unlocking a 4th achievement in-game -> appends "Kill3000Enemies" to remote\savedata -> <1s toast!
            var sw = Stopwatch.StartNew();
            await File.WriteAllTextAsync(
                savedataPath,
                """
                {
                  "Coins": 580,
                  "SelectedCharacter": "ANTONIO",
                  "Achievements": [
                    "ReachLV5",
                    "Survive1Minute",
                    "GetHolyWaterToLevel4",
                    "Kill3000Enemies"
                  ]
                }
                """,
                Encoding.UTF8);

            var toast = await liveToastTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(980));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000, $"Expected Vampire Survivors savedata JSON toast <1000ms, got {sw.ElapsedMilliseconds}ms");
            Assert.Equal("Kill3000Enemies", toast.AchievementApiName);
            Assert.True(toast.MuteSound);

            var finalSummary = await achService.GetGameAchievementsAsync(vs.GameId);
            Assert.Equal(4, finalSummary.UnlockedCount);
        }
        finally
        {
            await achService.StopMonitoringAsync();
        }
    }

    private static byte[] BuildSyntheticPe64DllBytes()
    {
        byte[] bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, 4), 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84, 2), 0x8664);
        byte[] vendorSig = "ORIGINAL_VENDOR_STEAM_API64_STUB_V1"u8.ToArray();
        Buffer.BlockCopy(vendorSig, 0, bytes, 0x120, vendorSig.Length);
        return bytes;
    }

    [Fact]
    public async Task GbeFork_PinnedReleaseSource_OfflineWithoutVerifiedCacheReturnsNullAndRejectsForeignDigest()
    {
        await using var sandbox = new TestSandbox();
        using var source = new PinnedGbeForkReleaseSource(sandbox.Paths, new AchievementModuleOptions { AllowNetworkDownloads = false });

        // Fixed upstream version + digest (never "latest").
        Assert.Equal("release-2026_09_27", PinnedGbeForkReleaseSource.PinnedReleaseTag);
        Assert.Matches("^[0-9a-f]{64}$", PinnedGbeForkReleaseSource.PinnedAssetSha256);
        Assert.Contains(PinnedGbeForkReleaseSource.PinnedReleaseTag, PinnedGbeForkReleaseSource.PinnedAssetUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", PinnedGbeForkReleaseSource.PinnedAssetUrl, StringComparison.OrdinalIgnoreCase);
        Assert.False(PinnedGbeForkReleaseSource.IsPinnedArchiveDigest("tampered-archive"u8));

        // Offline + empty cache -> null (no fake PE fallback in production code).
        Assert.Null(await source.GetSteamApiDllAsync(is64Bit: true));

        // A DLL dropped into the tag cache WITHOUT a matching SHA-256 manifest is never trusted.
        Directory.CreateDirectory(source.PinnedCacheDirectory);
        await File.WriteAllBytesAsync(Path.Combine(source.PinnedCacheDirectory, "steam_api64.dll"), BuildSyntheticPe64DllBytes());
        Assert.Null(await source.GetSteamApiDllAsync(is64Bit: true));
    }

    private sealed class FixedSteamApiKeyProvider(string key) : ISteamApiKeyProvider
    {
        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(key);

        public ValueTask<SteamApiKeyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SteamApiKeyStatusDto(true, "offline-test", "OFF***KEY"));

        public ValueTask SaveCustomApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Test-injected deterministic gbe_fork DLL source (replaces the former production-side isolated-mode PE shim).
    /// </summary>
    private sealed class InMemoryGbeForkBinarySource : IGbeForkBinarySource
    {
        public Task<byte[]?> GetSteamApiDllAsync(bool is64Bit, CancellationToken cancellationToken = default)
        {
            byte[] buf = new byte[512];
            buf[0] = (byte)'M';
            buf[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x3C, 4), 0x80);
            buf[0x80] = (byte)'P';
            buf[0x81] = (byte)'E';
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x84, 2), is64Bit ? (ushort)0x8664 : (ushort)0x014C);
            byte[] marker = "GSE Saves\0steam_settings\0SPAWN_TEST_INJECTED_GBE\0"u8.ToArray();
            Buffer.BlockCopy(marker, 0, buf, 0x100, marker.Length);
            return Task.FromResult<byte[]?>(buf);
        }
    }

    /// <summary>
    /// 100% in-process HTTP stub (no inner socket handler): serves synthetic Chinese <c>GetSchemaForGame</c> payloads for the
    /// configured AppIDs, a tiny procedural PNG for every icon URL, and HTTP 404 for every other known endpoint.
    /// Any request to an unknown host is recorded in <see cref="UnexpectedUris"/> so the test can assert zero real-network intent.
    /// </summary>
    private sealed class OfflineSteamApiFakeHandler(IReadOnlyDictionary<int, int> schemaCountsByAppId) : HttpMessageHandler
    {
        private static readonly byte[] TinyColorPngBytes = AchievementIconProcessor.GenerateProceduralFallbackBadgePng(grayscale: false, isRare: false);

        private static readonly HashSet<string> KnownHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "api.steampowered.com",
            "store.steampowered.com",
            "gameplay.gog.com",
            "steamhunters.com",
            "cdn.fake-steam.test",
        };

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requested = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _unexpected = new();

        public IReadOnlyCollection<string> RequestedUris => _requested;

        public IReadOnlyCollection<string> UnexpectedUris => _unexpected;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            _requested.Enqueue(uri.ToString());

            if (!KnownHosts.Contains(uri.Host))
            {
                _unexpected.Enqueue(uri.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            string path = uri.AbsolutePath;
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                var img = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyColorPngBytes) };
                img.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return Task.FromResult(img);
            }

            if (path.Contains("GetSchemaForGame", StringComparison.OrdinalIgnoreCase))
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (int.TryParse(query["appid"], out int appId) && schemaCountsByAppId.TryGetValue(appId, out int count))
                {
                    var achievements = Enumerable.Range(0, count).Select(i => new Dictionary<string, object>
                    {
                        ["name"] = $"ACH_{i}",
                        ["displayName"] = $"成就 {i}",
                        ["description"] = $"离线夹具描述 {i}",
                        ["hidden"] = i % 7 == 0 ? 1 : 0,
                        ["icon"] = $"https://cdn.fake-steam.test/{appId}/{i}.jpg",
                        ["icongray"] = $"https://cdn.fake-steam.test/{appId}/{i}_gray.jpg",
                    });
                    string json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["game"] = new Dictionary<string, object>
                        {
                            ["gameName"] = "Offline Fixture",
                            ["availableGameStats"] = new Dictionary<string, object> { ["achievements"] = achievements.ToList() },
                        },
                    });
                    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                    return Task.FromResult(ok);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
