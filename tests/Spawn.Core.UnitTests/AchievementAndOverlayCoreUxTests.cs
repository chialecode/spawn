using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Achievement;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// Offline, zero-audio, isolated-sandbox tests covering:
/// - NamedPipe <c>overlay.clearQueue</c> IPC round-trip
/// - Achievement icon cache non-pollution when offline &amp; <c>forceRefresh</c> passthrough
/// - Zero fabricated <c>UtcNow</c> timestamps on <c>silentSeed: true</c> without source timestamps
/// - Honest IPC dispatch feedback &amp; conditional <c>MarkAchievementPopupNotifiedAsync</c>
/// - Bulk rescan anti-spam collapse &amp; honest non-Goldberg manual mark/reset message
/// - <c>gbe_fork</c> executable-adjacent DLL prioritization &amp; classified network/SHA-256/7z errors.
/// </summary>
public sealed class AchievementAndOverlayCoreUxTests
{
    [Fact]
    public async Task OverlayIpc_ClearQueueCommand_RoundTripsCleanlyOverNamedPipe()
    {
        await using var sandbox = new TestSandbox();
        var clearQueueCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new OverlayIpcServer(sandbox.Paths);
        server.ClearQueueRequested += _ =>
        {
            clearQueueCalled.TrySetResult(true);
            return Task.CompletedTask;
        };
        await server.StartAsync();

        await using var client = new OverlayIpcClient(sandbox.Paths);
        bool ack = await client.ClearQueueAsync();
        Assert.True(ack);
        Assert.True(await clearQueueCalled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AchievementIconProcessor_OfflineNeverPollutesFormalCache_AndForceRefreshReDownloadsIcons()
    {
        await using var sandbox = new TestSandbox();
        var processor = new AchievementIconProcessor(sandbox.Paths);
        const string schemaKey = "Steam_999001";
        string iconDir = sandbox.Paths.GetAchievementIconDirectory(schemaKey);

        byte[] redPng = BuildSolidColorPng(new SKColor(220, 40, 40, 255));
        byte[] bluePng = BuildSolidColorPng(new SKColor(40, 90, 230, 255));
        byte[] currentPayload = redPng;

        using var fakeHttp = new HttpClient(new StubHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(currentPayload) };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return resp;
        }));

        var inputDef = new AchievementDefinitionDto
        {
            ApiName = "ACH_CACHE_TEST",
            DisplayName = "图标缓存测试",
            IconPath = "https://cdn.fake-steam.test/ach_cache_test.png",
            IconGrayPath = "https://cdn.fake-steam.test/ach_cache_test.png",
        };

        string formalColorPath = Path.Combine(iconDir, "ACH_CACHE_TEST_color.png");
        string formalGrayPath = Path.Combine(iconDir, "ACH_CACHE_TEST_gray.png");

        // 1. Offline run must NOT write procedural fallback placeholders into formal `{safeApi}_color.png` / `_gray.png`!
        var offlineResult = await processor.CacheAndProcessIconsAsync(
            schemaKey, [inputDef], fakeHttp, allowNetworkDownloads: false);
        Assert.False(File.Exists(formalColorPath));
        Assert.False(File.Exists(formalGrayPath));
        Assert.True(AchievementIconProcessor.IsFallbackPlaceholderPath(offlineResult[0].IconPath));
        Assert.True(AchievementIconProcessor.IsFallbackPlaceholderPath(offlineResult[0].IconGrayPath));
        Assert.True(File.Exists(offlineResult[0].IconPath!));

        // 2. Subsequent online sync sees clean formal cache and downloads the real red icon without needing forceRefresh.
        var onlineResult = await processor.CacheAndProcessIconsAsync(
            schemaKey, [inputDef], fakeHttp, allowNetworkDownloads: true, forceRefresh: false);
        Assert.True(File.Exists(formalColorPath));
        Assert.True(File.Exists(formalGrayPath));
        Assert.Equal(formalColorPath, onlineResult[0].IconPath);
        Assert.Equal(formalGrayPath, onlineResult[0].IconGrayPath);
        AssertPixelColorApprox(formalColorPath, expectedRed: 220, expectedBlue: 40);

        // 3. Updating remote asset to blue: forceRefresh=false keeps cached red; forceRefresh=true re-downloads blue!
        currentPayload = bluePng;
        _ = await processor.CacheAndProcessIconsAsync(
            schemaKey, [inputDef], fakeHttp, allowNetworkDownloads: true, forceRefresh: false);
        AssertPixelColorApprox(formalColorPath, expectedRed: 220, expectedBlue: 40);

        _ = await processor.CacheAndProcessIconsAsync(
            schemaKey, [inputDef], fakeHttp, allowNetworkDownloads: true, forceRefresh: true);
        AssertPixelColorApprox(formalColorPath, expectedRed: 40, expectedBlue: 230);

        // 4. forceRefresh=true while offline preserves the existing real cached icon (does not wipe or downgrade it).
        var offlineRefresh = await processor.CacheAndProcessIconsAsync(
            schemaKey, [inputDef], fakeHttp, allowNetworkDownloads: false, forceRefresh: true);
        Assert.Equal(formalColorPath, offlineRefresh[0].IconPath);
        AssertPixelColorApprox(formalColorPath, expectedRed: 40, expectedBlue: 230);
    }

    [Fact]
    public async Task AchievementService_SilentSeedUntimestampedSave_DoesNotForgeUtcNowOrHijackRecentTimeline()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string limboDir = sandbox.CreateSubDirectory("games", "LIMBO_TimestampTest");
        await File.WriteAllBytesAsync(Path.Combine(limboDir, "LIMBO.exe"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(limboDir, "limbo_boot.pkg"), [0x01]);
        string saveDir = Directory.CreateDirectory(Path.Combine(limboDir, "save_game")).FullName;
        // Unlock ACH_1, ACH_2, ACH_3 (bitmask 0b111 = 7) with zero per-achievement timestamps
        await File.WriteAllBytesAsync(
            Path.Combine(saveDir, "savegame.txt"),
            LimboSaveDecoder.EncryptFromText("achievements = \"7\"\n"));

        var limboGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "LIMBO",
            InstallDir = limboDir,
            ExecutableRelPath = "LIMBO.exe",
            PlatformSource = PlatformSource.GogOffline,
            AntiCheatTier = AntiCheatTier.Tier0,
            GogGameId = "1724299977",
            SteamAppId = 48000,
            AchievementCaptureMode = AchievementCaptureMode.GogNativeSave,
        };
        await sandbox.Database.UpsertGameAsync(limboGame);

        // Also seed a Goldberg game with a real historical unlock timestamp from 2023
        string appDataRoaming = sandbox.CreateSubDirectory("env", "AppDataRoaming");
        string gseDir = sandbox.CreateSubDirectory("games", "GseTimedGame");
        await File.WriteAllBytesAsync(Path.Combine(gseDir, "game.exe"), new byte[1024]);
        string gseSaveDir = Directory.CreateDirectory(Path.Combine(appDataRoaming, "GSE Saves", "400001")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(gseSaveDir, "achievements.json"),
            """{"ACH_HISTORICAL": {"earned": true, "earned_time": 1700000000}}""",
            Encoding.UTF8);

        var gseGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Timed GSE Game",
            InstallDir = gseDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.Steamrip,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 400001,
            AchievementCaptureMode = AchievementCaptureMode.GbeForkActivated,
        };
        await sandbox.Database.UpsertGameAsync(gseGame);

        var fakeIpc = new StubOverlayIpcClient { SendShouldSucceed = true };
        await using var sp = BuildAchievementTestProvider(sandbox, fakeIpc, appDataRoaming);
        var achService = sp.GetRequiredService<AchievementService>();

        int limboSeeded = await achService.ScanLocalUnlocksAsync(limboGame.GameId, silentSeed: true);
        Assert.Equal(3, limboSeeded);

        int gseSeeded = await achService.ScanLocalUnlocksAsync(gseGame.GameId, silentSeed: true);
        Assert.Equal(1, gseSeeded);

        // LIMBO untimestamped entries must be unlocked with UnlockedUtc == null (never fabricated UtcNow!)
        var limboUnlocks = await sandbox.Database.GetAchievementUnlocksAsync(limboGame.GameId);
        var unlockedLimbo = limboUnlocks.Where(u => u.IsUnlocked).ToList();
        Assert.Equal(3, unlockedLimbo.Count);
        Assert.All(unlockedLimbo, u => Assert.Null(u.UnlockedUtc));

        // Recent timeline must contain ONLY achievements with real timestamps (never hijacked by untimestamped old saves)
        var recent = await achService.GetRecentUnlocksAsync(limit: 20);
        Assert.Single(recent);
        Assert.Equal("ACH_HISTORICAL", recent[0].ApiName);
        Assert.Equal(1700000000L, recent[0].UnlockedUtc!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task AchievementService_HonestIpcFeedback_TestToastFailsHonestly_AndNotifiedPopupTracksDelivery()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        await sandbox.Database.SaveOverlaySettingsAsync(new OverlaySettingsDto
        {
            Theme = OverlayThemeKind.Steam,
            MuteSound = true,
            SoundVolume = 0.0,
        });

        string appDataRoaming = sandbox.CreateSubDirectory("env", "AppDataRoaming");
        string gameDir = sandbox.CreateSubDirectory("games", "IpcFeedbackGame");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "game.exe"), new byte[1024]);
        string gseSaveDir = Directory.CreateDirectory(Path.Combine(appDataRoaming, "GSE Saves", "400002")).FullName;
        string gseAchFile = Path.Combine(gseSaveDir, "achievements.json");
        await File.WriteAllTextAsync(gseAchFile, "{}", Encoding.UTF8);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "IPC Feedback Game",
            InstallDir = gameDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.Steamrip,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 400002,
        };
        await sandbox.Database.UpsertGameAsync(game);
        await sandbox.Database.UpsertAchievementSchemaAsync(
            game.GameId,
            "Steam_400002",
            AchievementSchemaSource.LocalEmulatorSchema,
            "schinese",
            [
                new AchievementDefinitionDto { ApiName = "ACH_FAIL_IPC", DisplayName = "断链成就", SortOrder = 0 },
                new AchievementDefinitionDto { ApiName = "ACH_OK_IPC", DisplayName = "正常成就", SortOrder = 1 },
            ]);

        var fakeIpc = new StubOverlayIpcClient
        {
            SendShouldSucceed = false,
            LastLaunchError = "未找到 Spawn.OverlayHost.exe",
        };
        await using var sp = BuildAchievementTestProvider(sandbox, fakeIpc, appDataRoaming);
        var achService = sp.GetRequiredService<AchievementService>();

        // 1. Test toast failure is reported honestly by both TrySendTestToastAsync and SendTestToastAsync.
        var dispatchResult = await achService.TrySendTestToastAsync(game.GameId);
        Assert.False(dispatchResult.Succeeded);
        Assert.Contains("Spawn.OverlayHost.exe", dispatchResult.Message, StringComparison.Ordinal);
        Assert.Single(fakeIpc.SentToasts);
        Assert.True(fakeIpc.SentToasts[0].ReplaceActivePreview);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => achService.SendTestToastAsync(game.GameId));
        Assert.Contains("Spawn.OverlayHost.exe", ex.Message, StringComparison.Ordinal);

        // 2. Real unlock when IPC fails MUST keep NotifiedPopup == false; when IPC succeeds -> NotifiedPopup == true.
        await achService.StartMonitoringAsync();
        try
        {
            await File.WriteAllTextAsync(
                gseAchFile,
                """{"ACH_FAIL_IPC": {"earned": true, "earned_time": 1730001000}}""",
                Encoding.UTF8);
            _ = await achService.ScanLocalUnlocksAsync(game.GameId, silentSeed: false);

            var afterFail = Assert.Single(await sandbox.Database.GetAchievementUnlocksAsync(game.GameId), u => u.ApiName == "ACH_FAIL_IPC");
            Assert.True(afterFail.IsUnlocked);
            Assert.False(afterFail.NotifiedPopup);

            fakeIpc.SendShouldSucceed = true;
            await File.WriteAllTextAsync(
                gseAchFile,
                """{"ACH_FAIL_IPC": {"earned": true, "earned_time": 1730001000}, "ACH_OK_IPC": {"earned": true, "earned_time": 1730001050}}""",
                Encoding.UTF8);
            _ = await achService.ScanLocalUnlocksAsync(game.GameId, silentSeed: false);

            var afterOk = Assert.Single(await sandbox.Database.GetAchievementUnlocksAsync(game.GameId), u => u.ApiName == "ACH_OK_IPC");
            Assert.True(afterOk.IsUnlocked);
            Assert.True(afterOk.NotifiedPopup);
        }
        finally
        {
            await achService.StopMonitoringAsync();
        }
    }

    [Fact]
    public async Task AchievementService_ManualRescanAntiSpam_AndHonestNonGoldbergManualToggleMessage()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        await sandbox.Database.SaveOverlaySettingsAsync(new OverlaySettingsDto { MuteSound = true, SoundVolume = 0.0 });

        string appDataRoaming = sandbox.CreateSubDirectory("env", "AppDataRoaming");

        // Game A (non-Goldberg LIMBO) for non-monitoring rescan anti-spam + non-Goldberg manual toggle message
        string limboDir = sandbox.CreateSubDirectory("games", "LIMBO_RescanGuard");
        await File.WriteAllBytesAsync(Path.Combine(limboDir, "LIMBO.exe"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(limboDir, "limbo_boot.pkg"), [0x01]);
        string saveDir = Directory.CreateDirectory(Path.Combine(limboDir, "save_game")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(saveDir, "savegame.txt"), LimboSaveDecoder.EncryptFromText("achievements = \"15\"\n"));

        var limboGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "LIMBO",
            InstallDir = limboDir,
            ExecutableRelPath = "LIMBO.exe",
            PlatformSource = PlatformSource.GogOffline,
            AntiCheatTier = AntiCheatTier.Tier0,
            GogGameId = "1724299977",
            SteamAppId = 48000,
        };
        await sandbox.Database.UpsertGameAsync(limboGame);

        // Game B (Goldberg GSE) for bulk >= 8 summary collapse + Goldberg manual toggle sync
        string gseDir = sandbox.CreateSubDirectory("games", "BulkGseGame");
        await File.WriteAllBytesAsync(Path.Combine(gseDir, "game.exe"), new byte[1024]);
        string gseSaveDir = Directory.CreateDirectory(Path.Combine(appDataRoaming, "GSE Saves", "400003")).FullName;
        string gseAchFile = Path.Combine(gseSaveDir, "achievements.json");
        await File.WriteAllTextAsync(gseAchFile, "{}", Encoding.UTF8);

        var gseGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Bulk GSE Game",
            InstallDir = gseDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.Steamrip,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 400003,
        };
        await sandbox.Database.UpsertGameAsync(gseGame);

        var fakeIpc = new StubOverlayIpcClient { SendShouldSucceed = true };
        await using var sp = BuildAchievementTestProvider(sandbox, fakeIpc, appDataRoaming);
        var achService = sp.GetRequiredService<AchievementService>();

        // 1. Non-monitoring manual rescan (IsMonitoring == false) MUST NOT bombard overlay with popups!
        int foundOffline = await achService.ScanLocalUnlocksAsync(limboGame.GameId, silentSeed: false);
        Assert.Equal(4, foundOffline);
        Assert.Empty(fakeIpc.SentToasts);

        // 2. Non-Goldberg manual mark/reset honestly reports "仅更新 SPAWN 展示记录".
        var nonGoldbergToggle = await achService.SetAchievementUnlockedDetailedAsync(limboGame.GameId, "ACH_5", unlocked: true);
        Assert.True(nonGoldbergToggle.Succeeded);
        Assert.False(nonGoldbergToggle.SyncedToLocalEmulatorFile);
        Assert.Contains("仅更新 SPAWN 展示记录", nonGoldbergToggle.Message, StringComparison.Ordinal);

        // 3. Goldberg manual mark syncs to local achievements.json and reports local file sync.
        var goldbergToggle = await achService.SetAchievementUnlockedDetailedAsync(gseGame.GameId, "ACH_MANUAL_1", unlocked: true);
        Assert.True(goldbergToggle.Succeeded);
        Assert.True(goldbergToggle.SyncedToLocalEmulatorFile);
        string updatedGseJson = await File.ReadAllTextAsync(gseAchFile, Encoding.UTF8);
        Assert.Contains("ACH_MANUAL_1", updatedGseJson, StringComparison.Ordinal);

        // 4. Active monitoring + bulk >= 8 new unlocks collapses into ONE summary toast instead of 10 popups.
        await achService.StartMonitoringAsync();
        try
        {
            var bulkEntries = string.Join(
                ",",
                Enumerable.Range(1, 10).Select(i => $"\"BULK_{i}\": {{\"earned\": true, \"earned_time\": {1730002000 + i}}}"));
            await File.WriteAllTextAsync(gseAchFile, "{" + bulkEntries + "}", Encoding.UTF8);

            int bulkCount = await achService.ScanLocalUnlocksAsync(gseGame.GameId, silentSeed: false);
            Assert.Equal(10, bulkCount);
            Assert.Single(fakeIpc.SentToasts);
            Assert.Equal(AchievementService.BulkSummaryToastApiName, fakeIpc.SentToasts[0].AchievementApiName);
            Assert.Equal("已连续解锁 10 项成就", fakeIpc.SentToasts[0].DisplayName);
        }
        finally
        {
            await achService.StopMonitoringAsync();
        }
    }

    [Fact]
    public async Task GbeFork_FindPrimarySteamApiDll_PrefersExecutableDirectoryOverAlphabeticalAuxiliaryFolders()
    {
        await using var sandbox = new TestSandbox();

        // 1. 64-bit game in Binaries\Win64 alongside an alphabetically-earlier BonusLauncher folder.
        string gameRoot = sandbox.CreateSubDirectory("games", "UnrealNestedGame");
        string auxDir = Directory.CreateDirectory(Path.Combine(gameRoot, "AA_BonusLauncher")).FullName;
        string win64Dir = Directory.CreateDirectory(Path.Combine(gameRoot, "Binaries", "Win64")).FullName;

        string auxDll = Path.Combine(auxDir, "steam_api64.dll");
        string realDll = Path.Combine(win64Dir, "steam_api64.dll");
        await File.WriteAllBytesAsync(auxDll, BuildSyntheticPeBytes(is64Bit: true));
        await File.WriteAllBytesAsync(realDll, BuildSyntheticPeBytes(is64Bit: true));
        await File.WriteAllBytesAsync(Path.Combine(win64Dir, "MainGame.exe"), BuildSyntheticPeBytes(is64Bit: true));

        string? picked = GbeForkActivatorService.FindPrimarySteamApiDll(gameRoot, Path.Combine("Binaries", "Win64", "MainGame.exe"));
        Assert.Equal(Path.GetFullPath(realDll), picked);

        // 2. 32-bit game co-located with steam_api.dll while Tools\ contains a 64-bit steam_api64.dll.
        string x86Root = sandbox.CreateSubDirectory("games", "ClassicX86Game");
        string toolsDir = Directory.CreateDirectory(Path.Combine(x86Root, "Tools")).FullName;
        string x86Dll = Path.Combine(x86Root, "steam_api.dll");
        await File.WriteAllBytesAsync(x86Dll, BuildSyntheticPeBytes(is64Bit: false));
        await File.WriteAllBytesAsync(Path.Combine(x86Root, "ClassicGame.exe"), BuildSyntheticPeBytes(is64Bit: false));
        await File.WriteAllBytesAsync(Path.Combine(toolsDir, "steam_api64.dll"), BuildSyntheticPeBytes(is64Bit: true));

        string? pickedX86 = GbeForkActivatorService.FindPrimarySteamApiDll(x86Root, "ClassicGame.exe");
        Assert.Equal(Path.GetFullPath(x86Dll), pickedX86);
    }

    [Fact]
    public async Task GbeFork_PinnedReleaseSource_ClassifiesNetworkSha256AndMissing7zErrorsClearly()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        var options = new AchievementModuleOptions { AllowNetworkDownloads = true };

        // 1. Network download failure (HTTP 503)
        using (var http503 = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        using (var src = new PinnedGbeForkReleaseSource(sandbox.Paths, options, http503))
        {
            var res = await src.AcquireSteamApiDllAsync(is64Bit: true);
            Assert.False(res.Succeeded);
            Assert.Equal(GbeForkAcquisitionErrorKind.NetworkDownloadFailed, res.ErrorKind);
            Assert.Contains("网络下载", res.ErrorMessage!, StringComparison.Ordinal);
            Assert.Contains("503", res.ErrorMessage!, StringComparison.Ordinal);
        }

        // 2. SHA-256 verification failure (HTTP 200 with mismatched bytes)
        using (var httpTampered = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("tampered-bytes"u8.ToArray()) })))
        using (var src = new PinnedGbeForkReleaseSource(sandbox.Paths, options, httpTampered))
        {
            var res = await src.AcquireSteamApiDllAsync(is64Bit: true);
            Assert.False(res.Succeeded);
            Assert.Equal(GbeForkAcquisitionErrorKind.Sha256VerificationFailed, res.ErrorKind);
            Assert.Contains("SHA-256", res.ErrorMessage!, StringComparison.Ordinal);
        }

        // 3. Missing .7z extractor (Win10 tar without .7z + no 7-Zip) + propagated cleanly through GbeForkActivatorService
        using (var httpOk = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("fake-7z-archive"u8.ToArray()) })))
        using (var src = PinnedGbeForkReleaseSource.CreateForTesting(
            sandbox.Paths,
            options,
            httpOk,
            archiveDigestVerifier: static _ => true,
            sevenZipExtractor: static (_, _, _) => Task.FromResult(SevenZipExtractOutcome.MissingExtractor)))
        {
            var res = await src.AcquireSteamApiDllAsync(is64Bit: true);
            Assert.False(res.Succeeded);
            Assert.Equal(GbeForkAcquisitionErrorKind.MissingSevenZipExtractor, res.ErrorKind);
            Assert.Contains(".7z", res.ErrorMessage!, StringComparison.Ordinal);
            Assert.Contains("7-Zip", res.ErrorMessage!, StringComparison.Ordinal);
            Assert.Contains("Windows 10", res.ErrorMessage!, StringComparison.Ordinal);

            // Verify GbeForkActivatorService surfaces the exact 7-Zip error instead of a vague offline message.
            string gameDir = sandbox.CreateSubDirectory("games", "Win10ExtractFailGame");
            await File.WriteAllBytesAsync(Path.Combine(gameDir, "steam_api64.dll"), BuildSyntheticPeBytes(is64Bit: true));
            await File.WriteAllBytesAsync(Path.Combine(gameDir, "game.exe"), BuildSyntheticPeBytes(is64Bit: true));

            var game = new GameRecord
            {
                GameId = SpawnId.NewUlid(),
                Title = "Win10 7z Test",
                InstallDir = gameDir,
                ExecutableRelPath = "game.exe",
                PlatformSource = PlatformSource.Steamrip,
                AntiCheatTier = AntiCheatTier.Tier0,
                SteamAppId = 48000,
            };
            await sandbox.Database.UpsertGameAsync(game);

            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            using var activator = new GbeForkActivatorService(sandbox.Paths, sandbox.Database, guard, options, binarySource: src);

            var activationResult = await activator.ActivateAsync(game, 48000, []);
            Assert.False(activationResult.Succeeded);
            Assert.Contains("7-Zip", activationResult.Message, StringComparison.Ordinal);
        }
    }

    private static ServiceProvider BuildAchievementTestProvider(
        TestSandbox sandbox,
        IOverlayIpcClient overlayClient,
        string appDataRoaming)
    {
        var services = new ServiceCollection();
        services.AddSpawnCore(sandbox.Paths);
        services.AddSingleton(sandbox.Database);
        services.AddSingleton<IOverlayIpcClient>(overlayClient);
        services.AddSpawnAchievement(opt =>
        {
            opt.AllowNetworkDownloads = false;
            opt.EnableGogCometAutoStart = false;
            opt.EnableCommunitySpoilerRequests = false;
            opt.DebounceWindow = TimeSpan.FromMilliseconds(40);
            opt.ActiveGamePollInterval = TimeSpan.FromMilliseconds(80);
            opt.EnvironmentOverrides = new AchievementEnvironmentOverrides
            {
                AppDataRoaming = appDataRoaming,
                LocalAppData = sandbox.CreateSubDirectory("env", "LocalAppData"),
                PublicDocuments = sandbox.CreateSubDirectory("env", "PublicDocs"),
                UserDocuments = sandbox.CreateSubDirectory("env", "UserDocs"),
                ProgramData = sandbox.CreateSubDirectory("env", "ProgramData"),
                SteamInstallPath = sandbox.CreateSubDirectory("env", "Steam"),
            };
        });
        return services.BuildServiceProvider();
    }

    private static byte[] BuildSolidColorPng(SKColor color)
    {
        using var bmp = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(color);
        }

        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 95).ToArray();
    }

    private static void AssertPixelColorApprox(string pngPath, byte expectedRed, byte expectedBlue)
    {
        using var bmp = SKBitmap.Decode(File.ReadAllBytes(pngPath));
        Assert.NotNull(bmp);
        SKColor px = bmp!.GetPixel(16, 16);
        Assert.InRange(Math.Abs(px.Red - expectedRed), 0, 5);
        Assert.InRange(Math.Abs(px.Blue - expectedBlue), 0, 5);
    }

    private static byte[] BuildSyntheticPeBytes(bool is64Bit)
    {
        byte[] bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, 4), 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84, 2), is64Bit ? (ushort)0x8664 : (ushort)0x014C);
        return bytes;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubOverlayIpcClient : IOverlayIpcClient, IOverlayIpcDiagnostics
    {
        private readonly Lock _lock = new();
        public List<OverlayToastRequest> SentToasts { get; } = [];
        public bool SendShouldSucceed { get; set; } = true;
        public string? LastLaunchError { get; set; }
        public string PipeName => "stub-overlay-pipe";

        public Task<bool> SendToastAsync(OverlayToastRequest request, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                SentToasts.Add(request);
            }

            return Task.FromResult(SendShouldSucceed);
        }

        public Task<int> SendToastBatchAsync(IReadOnlyList<OverlayToastRequest> requests, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                SentToasts.AddRange(requests);
            }

            return Task.FromResult(SendShouldSucceed ? requests.Count : 0);
        }

        public Task<OverlayHeartbeatReply?> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<OverlayHeartbeatReply?>(null);

        public Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
