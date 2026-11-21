using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// Deterministic, zero-network unit tests covering M3-W03 acceptance cases:
/// M3-A-03 (Four-layer Tier 0 determination), M3-A-04 (VerifiedOfflineBypass, vgk.sys policy,
/// 2s emergency watchdog, session kernel driver cooldown), and M3-A-05 (LOOP-06 pre-launch purge,
/// secondary sandbox orphan re-check, renamed CE driver FileVersionInfo &amp; QueryDosDeviceW CEDRIVER*).
/// </summary>
public sealed class AntiCheatTierAndWatchdogTests
{
    // =========================================================================
    // M3-A-03: Four-layer Tier 0 determination
    // =========================================================================

    [Fact]
    public void M3A03_DirectoryInspection_Depth6AndExpandedSignatures_DetectsNewMarkersAndEnforcesDepthBound()
    {
        using var sandbox = new TestSandbox();
        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));

        (string FileName, AntiCheatTier ExpectedTier, string ExpectedIndicatorPrefix)[] cases =
        [
            ("start_protected_game.exe", AntiCheatTier.Tier2, "EasyAntiCheat:start_protected_game"),
            ("EasyAntiCheat_EOS.sys", AntiCheatTier.Tier2, "EasyAntiCheat:EOS.sys"),
            ("BEDaisy.sys", AntiCheatTier.Tier2, "BattlEye:BEDaisy.sys"),
            ("RainbowSix_BE.exe", AntiCheatTier.Tier2, "BattlEye:BE_Launcher"),
            ("EAAntiCheat.GameServiceLauncher.exe", AntiCheatTier.Tier3, "EAAC:EAAntiCheat"),
            ("vgc.exe", AntiCheatTier.Tier3, "Vanguard:vgc.exe"),
            ("NeacSafe64.sys", AntiCheatTier.Tier3, "NetEase:NeacSafe64.sys"),
            ("GameMon64.des", AntiCheatTier.Tier2, "nProtect:GameMon64"),
            ("xhunter1.sys", AntiCheatTier.Tier2, "XIGNCODE3:xhunter1.sys"),
            ("BlackCipher64.aes", AntiCheatTier.Tier2, "Nexon:BlackCipher64"),
        ];

        for (int i = 0; i < cases.Length; i++)
        {
            var (fileName, expectedTier, expectedIndicator) = cases[i];
            string dir = sandbox.CreateSubDirectory("m3a03-sig", $"case-{i}");
            File.WriteAllBytes(Path.Combine(dir, fileName), [0x4D, 0x5A]);

            var result = guard.InspectGameDirectory(dir);
            Assert.Equal(expectedTier, result.Tier);
            Assert.Contains(result.MatchedIndicators, s => s.Equals(expectedIndicator, StringComparison.OrdinalIgnoreCase));
        }

        // Depth 6 must be scanned: root -> d1 -> d2 -> d3 -> d4 -> d5 -> d6 -> BEDaisy.sys
        string depth6Root = sandbox.CreateSubDirectory("m3a03-depth6");
        string depth6Leaf = Directory.CreateDirectory(Path.Combine(depth6Root, "d1", "d2", "d3", "d4", "d5", "d6")).FullName;
        File.WriteAllBytes(Path.Combine(depth6Leaf, "BEDaisy.sys"), [0x00]);
        var depth6Result = guard.InspectGameDirectory(depth6Root);
        Assert.Equal(AntiCheatTier.Tier2, depth6Result.Tier);
        Assert.Contains("BattlEye:BEDaisy.sys", depth6Result.MatchedIndicators);

        // Depth 7 must NOT be scanned (depth bound = 6)
        string depth7Root = sandbox.CreateSubDirectory("m3a03-depth7");
        string depth7Leaf = Directory.CreateDirectory(Path.Combine(depth7Root, "d1", "d2", "d3", "d4", "d5", "d6", "d7")).FullName;
        File.WriteAllBytes(Path.Combine(depth7Leaf, "BEDaisy.sys"), [0x00]);
        var depth7Result = guard.InspectGameDirectory(depth7Root);
        Assert.Equal(AntiCheatTier.Tier0, depth7Result.Tier);
        Assert.Empty(depth7Result.MatchedIndicators);
    }

    [Fact]
    public async Task M3A03_AreWeAntiCheatYetEmbeddedIndex_ClassifiesCs2ValorantAndMainstreamGamesWithoutLocalFiles()
    {
        await using var sandbox = new TestSandbox();
        var index = AreWeAntiCheatYetIndex.Default;

        Assert.Equal("MIT", index.License);
        Assert.True(index.Entries.Count >= 40);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
        var mockHttp = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var gate = new TrainerSafetyGate(
            guard,
            sandbox.Database,
            new ManualTimeProvider(),
            new FakeAntiCheatSystemProbe(),
            steamHttpMessageHandler: mockHttp);

        // CS2 (SteamAppId 730) with ZERO local anti-cheat files -> must NEVER be Tier 0!
        string cs2Dir = sandbox.CreateSubDirectory("games", "CS2Empty");
        await File.WriteAllBytesAsync(Path.Combine(cs2Dir, "cs2.exe"), [0x4D, 0x5A]);
        var cs2 = new GameRecord
        {
            GameId = "game-cs2",
            Title = "Counter-Strike 2",
            InstallDir = cs2Dir,
            ExecutableRelPath = "cs2.exe",
            SteamAppId = 730,
        };

        var cs2Report = await gate.InspectGameFourLayerAsync(cs2, allowOnlineLookup: false);
        Assert.True(cs2Report.EffectiveTier >= AntiCheatTier.Tier2);
        Assert.Contains("VAC", cs2Report.AreWeAntiCheatYetMatches);
        Assert.False(cs2Report.HasSinglePlayerCategory);
        Assert.True(cs2Report.IsMultiplayerOnly);

        // Valorant (NO SteamAppId, ZERO local anti-cheat files, matched by name) -> Tier 3!
        string valDir = sandbox.CreateSubDirectory("games", "ValorantEmpty");
        await File.WriteAllBytesAsync(Path.Combine(valDir, "VALORANT-Win64-Shipping.exe"), [0x4D, 0x5A]);
        var valorant = new GameRecord
        {
            GameId = "game-val",
            Title = "Valorant",
            InstallDir = valDir,
            ExecutableRelPath = "VALORANT-Win64-Shipping.exe",
            SteamAppId = null,
        };

        var valReport = await gate.InspectGameFourLayerAsync(valorant, allowOnlineLookup: false);
        Assert.Equal(AntiCheatTier.Tier3, valReport.EffectiveTier);
        Assert.Contains("Vanguard", valReport.AreWeAntiCheatYetMatches);

        // Verify all required mainstream titles are covered in the bundled MIT projection.
        int[] requiredSteamIds = [730, 1245620, 1888160, 1172470, 578080, 359550, 1085660, 976730, 271590, 1599340, 1203220, 252490, 381210];
        foreach (int appId in requiredSteamIds)
        {
            var match = index.Inspect(appId, title: null);
            Assert.NotNull(match);
            Assert.True(match!.Tier >= AntiCheatTier.Tier2, $"Expected AppId {appId} to classify as >= Tier2.");
            Assert.NotEmpty(match.AntiCheats);
        }

        Assert.NotNull(index.Inspect(steamAppId: null, title: "Fortnite"));
        Assert.NotNull(index.Inspect(steamAppId: null, title: "VALORANT"));

        // Verify zero false positives on pure single-player titles and substring lookalikes.
        Assert.Null(index.Inspect(48000, "LIMBO"));
        Assert.Null(index.Inspect(367520, "Hollow Knight"));
        Assert.Null(index.Inspect(504230, "Celeste"));
        Assert.Null(index.Inspect(steamAppId: null, title: "Trust"));
        Assert.Null(index.Inspect(steamAppId: null, title: "CS2000 Simulator"));
    }

    [Fact]
    public async Task M3A03_SteamAppDetailsCategories_VacMmoNoSinglePlayerTtlCacheAndOfflineFallback()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));

        var responses = new Dictionary<int, string>
        {
            // VAC (id=8) + Single-player (id=2) -> >= Tier 2
            [900001] = BuildSteamCategoriesJson(900001, (2, "Single-player"), (8, "Valve Anti-Cheat enabled")),
            // MMO (id=20) + Single-player (id=2) -> >= Tier 2
            [900002] = BuildSteamCategoriesJson(900002, (2, "Single-player"), (20, "MMO")),
            // Multiplayer-only (no Single-player id=2) -> >= Tier 2
            [900003] = BuildSteamCategoriesJson(900003, (1, "Multi-player"), (36, "Online PvP")),
            // Pure Single-player (id=2) -> Tier 0
            [900004] = BuildSteamCategoriesJson(900004, (2, "Single-player"), (22, "Steam Achievements")),
        };

        bool simulateOffline = false;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (simulateOffline)
            {
                throw new HttpRequestException("Simulated offline network failure");
            }

            string query = req.RequestUri?.Query ?? string.Empty;
            foreach (var (id, payload) in responses)
            {
                if (query.Contains($"appids={id}", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        string cleanDir = sandbox.CreateSubDirectory("games", "CleanSteam");
        await File.WriteAllBytesAsync(Path.Combine(cleanDir, "game.exe"), [0x4D, 0x5A]);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
        using var classifier = new SteamAppCategoryClassifier(
            database: sandbox.Database,
            httpMessageHandler: handler,
            timeProvider: clock,
            cacheTtl: TimeSpan.FromHours(1));
        using var gate = new TrainerSafetyGate(
            guard,
            sandbox.Database,
            clock,
            new FakeAntiCheatSystemProbe(),
            steamCategoryClassifier: classifier);

        GameRecord MakeGame(int appId, AntiCheatTier existingTier = AntiCheatTier.Tier0) => new()
        {
            GameId = $"steam-{appId}",
            Title = $"Synthetic Game {appId}",
            InstallDir = cleanDir,
            ExecutableRelPath = "game.exe",
            SteamAppId = appId,
            AntiCheatTier = existingTier,
        };

        // 1. VAC (id=8)
        var vacReport = await gate.InspectGameFourLayerAsync(MakeGame(900001));
        Assert.Equal(AntiCheatTier.Tier2, vacReport.EffectiveTier);
        Assert.True(vacReport.HasVacCategory);
        Assert.True(vacReport.HasSinglePlayerCategory);
        Assert.True(vacReport.UsedOnlineSteamCategories);
        Assert.False(vacReport.OfflineConservativeFallbackApplied);

        // 2. MMO (id=20)
        var mmoReport = await gate.InspectGameFourLayerAsync(MakeGame(900002));
        Assert.Equal(AntiCheatTier.Tier2, mmoReport.EffectiveTier);
        Assert.True(mmoReport.HasMmoCategory);
        Assert.True(mmoReport.UsedOnlineSteamCategories);

        // 3. No Single-player (id=2 missing)
        var mpOnlyReport = await gate.InspectGameFourLayerAsync(MakeGame(900003));
        Assert.Equal(AntiCheatTier.Tier2, mpOnlyReport.EffectiveTier);
        Assert.False(mpOnlyReport.HasSinglePlayerCategory);
        Assert.True(mpOnlyReport.IsMultiplayerOnly);

        // 4. Pure Single-player (id=2 present, no VAC/MMO)
        var spReport = await gate.InspectGameFourLayerAsync(MakeGame(900004));
        Assert.Equal(AntiCheatTier.Tier0, spReport.EffectiveTier);
        Assert.True(spReport.HasSinglePlayerCategory);
        Assert.False(spReport.IsMultiplayerOnly);
        Assert.Equal(4, handler.RequestCount);

        // 5. DB TTL cache hit: repeated query within 1 hour triggers 0 HTTP calls!
        clock.Advance(TimeSpan.FromMinutes(30));
        var cachedVacReport = await gate.InspectGameFourLayerAsync(MakeGame(900001));
        Assert.Equal(AntiCheatTier.Tier2, cachedVacReport.EffectiveTier);
        Assert.True(cachedVacReport.HasVacCategory);
        Assert.Equal(4, handler.RequestCount);

        // 6. TTL expiry: advancing beyond 1 hour triggers fresh lookup.
        clock.Advance(TimeSpan.FromMinutes(45));
        _ = await gate.InspectGameFourLayerAsync(MakeGame(900001));
        Assert.Equal(5, handler.RequestCount);

        // 7. Offline / network exception conservative fallback: never throws, never downgrades existing Tier 2!
        simulateOffline = true;
        var offlinePrevTier2 = await gate.InspectGameFourLayerAsync(MakeGame(999888, existingTier: AntiCheatTier.Tier2));
        Assert.Equal(AntiCheatTier.Tier2, offlinePrevTier2.EffectiveTier);
        Assert.True(offlinePrevTier2.OfflineConservativeFallbackApplied);
        Assert.False(offlinePrevTier2.UsedOnlineSteamCategories);
        Assert.Null(offlinePrevTier2.HasSinglePlayerCategory);
    }

    // =========================================================================
    // M3-A-04: VerifiedOfflineBypass, vgk.sys policy, 2s Watchdog, Driver Cooldown
    // =========================================================================

    [Fact]
    public async Task M3A04_VerifiedOfflineBypass_LiveHardTicket_AndVanguardPolicy_WorkDeterministically()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        var clock = new ManualTimeProvider();
        var probe = new FakeAntiCheatSystemProbe();
        IReadOnlyList<string> residuals = [];

        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: () => Volatile.Read(ref residuals),
            pollInterval: TimeSpan.FromMilliseconds(250));

        var offlineHttp = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var gate = new TrainerSafetyGate(
            guard,
            sandbox.Database,
            clock,
            probe,
            steamHttpMessageHandler: offlineHttp);

        // Tier 0 clean single-player game
        string spDir = sandbox.CreateSubDirectory("games", "LimboClean");
        await File.WriteAllBytesAsync(Path.Combine(spDir, "limbo.exe"), [0x4D, 0x5A]);
        var tier0Game = new GameRecord
        {
            GameId = "g-tier0",
            Title = "LIMBO",
            InstallDir = spDir,
            ExecutableRelPath = "limbo.exe",
            SteamAppId = 48000,
            AntiCheatTier = AntiCheatTier.Tier0,
        };

        // Tier 2 hybrid single-player game: Elden Ring (1245620, hasSinglePlayer = true in AWACY)
        string erDir = sandbox.CreateSubDirectory("games", "EldenRing");
        await File.WriteAllBytesAsync(Path.Combine(erDir, "start_protected_game.exe"), [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(Path.Combine(erDir, "eldenring.exe"), [0x4D, 0x5A]);
        var eldenRing = new GameRecord
        {
            GameId = "g-er",
            Title = "ELDEN RING",
            InstallDir = erDir,
            ExecutableRelPath = "eldenring.exe",
            SteamAppId = 1245620,
            AntiCheatTier = AntiCheatTier.Tier2,
        };
        await sandbox.Database.UpsertGameAsync(eldenRing);

        // Non-SP Tier 2 game (CS2), Tier 3 game (Valorant), Tier 1 game (HoYo)
        string cs2Dir = sandbox.CreateSubDirectory("games", "CS2");
        var cs2 = new GameRecord { GameId = "g-cs2", Title = "Counter-Strike 2", InstallDir = cs2Dir, ExecutableRelPath = "cs2.exe", SteamAppId = 730, AntiCheatTier = AntiCheatTier.Tier2 };
        string valDir = sandbox.CreateSubDirectory("games", "Val");
        var valorant = new GameRecord { GameId = "g-val", Title = "VALORANT", InstallDir = valDir, ExecutableRelPath = "VALORANT.exe", AntiCheatTier = AntiCheatTier.Tier3 };
        string hoyoDir = sandbox.CreateSubDirectory("games", "HoYo");
        await File.WriteAllBytesAsync(Path.Combine(hoyoDir, "HoYoKProtect.sys"), [0x00]);
        var hoyo = new GameRecord { GameId = "g-hoyo", Title = "OfflineGacha", InstallDir = hoyoDir, ExecutableRelPath = "gacha.exe", AntiCheatTier = AntiCheatTier.Tier1 };

        // 1. Tier 1, Tier 3, and non-SP Tier 2 are permanently blocked.
        var cs2Dec = await gate.EvaluateTrainerAllowanceAsync(cs2);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByTier, cs2Dec.Outcome);
        Assert.False(cs2Dec.IsAllowed);
        Assert.False(cs2Dec.CanConfirmOfflineBypass);

        var valDec = await gate.EvaluateTrainerAllowanceAsync(valorant);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByTier, valDec.Outcome);
        Assert.False(valDec.IsAllowed);

        var hoyoDec = await gate.EvaluateTrainerAllowanceAsync(hoyo);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByTier, hoyoDec.Outcome);
        Assert.False(hoyoDec.IsAllowed);

        // 2. Elden Ring before user confirmation -> RequiresOfflineConfirmation.
        var erBefore = await gate.EvaluateTrainerAllowanceAsync(eldenRing);
        Assert.Equal(TrainerAllowanceOutcome.RequiresOfflineConfirmation, erBefore.Outcome);
        Assert.False(erBefore.IsAllowed);
        Assert.True(erBefore.CanConfirmOfflineBypass);
        Assert.Equal(AntiCheatTier.Tier2, erBefore.StaticGameTier);

        // Enable VerifiedOfflineBypass in DB -> static anti_cheat_tier MUST stay Tier2!
        await sandbox.Database.SetGameVerifiedOfflineBypassAsync(eldenRing.GameId, enabled: true, clock.GetUtcNow());
        var reloadedEr = (await sandbox.Database.GetGameByIdAsync(eldenRing.GameId))!;
        Assert.Equal(AntiCheatTier.Tier2, reloadedEr.AntiCheatTier);
        Assert.True(reloadedEr.VerifiedOfflineBypass);

        // 3. Confirmed Elden Ring with live AC process or session kernel driver -> hard blocked!
        probe.SetProcesses(new SafeProcessEntry(5001, 4, "EasyAntiCheat_EOS.exe", @"C:\EAC\EasyAntiCheat_EOS.exe"));
        var blockedByProc = await gate.EvaluateTrainerAllowanceAsync(reloadedEr);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByLiveAntiCheat, blockedByProc.Outcome);
        Assert.False(blockedByProc.IsAllowed);
        Assert.Contains("EasyAntiCheat_EOS.exe", blockedByProc.DetectedAntiCheatProcesses);

        probe.ClearAll();
        probe.SetLoadedDrivers("EasyAntiCheat_EOS.sys");
        var blockedByDriver = await gate.EvaluateTrainerAllowanceAsync(reloadedEr);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByLiveAntiCheat, blockedByDriver.Outcome);
        Assert.False(blockedByDriver.IsAllowed);
        Assert.Contains("EasyAntiCheat_EOS.sys", blockedByDriver.DetectedAntiCheatDrivers);

        // Clear live AC -> Allowed with UsedVerifiedOfflineBypass = true!
        probe.ClearAll();
        var allowedEr = await gate.EvaluateTrainerAllowanceAsync(reloadedEr);
        Assert.Equal(TrainerAllowanceOutcome.Allowed, allowedEr.Outcome);
        Assert.True(allowedEr.IsAllowed);
        Assert.True(allowedEr.UsedVerifiedOfflineBypass);
        Assert.Equal(AntiCheatTier.Tier2, allowedEr.StaticGameTier);

        // 4. Vanguard vgk.sys 3-way policy:
        // (a) vgk.sys + active vgc.exe -> hard blocked!
        probe.SetProcesses(new SafeProcessEntry(6001, 4, "vgc.exe", @"C:\Program Files\Riot Vanguard\vgc.exe"));
        probe.SetLoadedDrivers("vgk.sys");
        var vgActive = await gate.EvaluateTrainerAllowanceAsync(tier0Game, strictVanguardMode: false);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByLiveAntiCheat, vgActive.Outcome);
        Assert.False(vgActive.IsAllowed);
        Assert.Contains("vgc.exe", vgActive.DetectedAntiCheatProcesses);

        // (b) vgk.sys idle resident only (no vgc.exe), strict=false -> VanguardIdleWarning (Allowed with warning)
        probe.SetProcesses();
        probe.SetLoadedDrivers("vgk.sys");
        var vgIdleWarn = await gate.EvaluateTrainerAllowanceAsync(tier0Game, strictVanguardMode: false);
        Assert.Equal(TrainerAllowanceOutcome.VanguardIdleWarning, vgIdleWarn.Outcome);
        Assert.True(vgIdleWarn.IsAllowed);
        Assert.True(vgIdleWarn.IsVanguardIdleWarning);

        // (c) vgk.sys idle resident only, strict=true -> BlockedByVanguardStrictMode
        var vgStrict = await gate.EvaluateTrainerAllowanceAsync(tier0Game, strictVanguardMode: true);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByVanguardStrictMode, vgStrict.Outcome);
        Assert.False(vgStrict.IsAllowed);
        Assert.True(vgStrict.IsVanguardIdleWarning);

        // 5. Guard locked (ProtectedRunning) blocks Tier 0 even when live probe is clear.
        probe.ClearAll();
        guard.NotifyProtectedGameStarted("game-ace", AntiCheatTier.Tier3);
        var guardLocked = await gate.EvaluateTrainerAllowanceAsync(tier0Game);
        Assert.Equal(TrainerAllowanceOutcome.BlockedByGuardPhase, guardLocked.Outcome);
        Assert.False(guardLocked.IsAllowed);
        Assert.Equal(AntiCheatGuardPhase.ProtectedRunning, guardLocked.GuardPhase);

        // 6. Direct EvaluateOfflineBypassAsync auto-invalidates if a previously confirmed record upgrades to Tier 3.
        var upgradedEr = reloadedEr with { AntiCheatTier = AntiCheatTier.Tier3 };
        var invalidatedBypass = await gate.EvaluateOfflineBypassAsync(upgradedEr);
        Assert.False(invalidatedBypass.IsEligible);
        Assert.False(invalidatedBypass.IsConfirmed);
        Assert.True(invalidatedBypass.WasAutoInvalidated);
        Assert.False(string.IsNullOrWhiteSpace(invalidatedBypass.InvalidationReason));
        Assert.False((await sandbox.Database.GetGameByIdAsync(eldenRing.GameId))!.VerifiedOfflineBypass);
    }

    [Fact]
    public async Task M3A04_GameLibraryRescan_PreservesVerifiedOfflineBypassAndAutoInvalidatesOnConditionLoss()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

        // Synthetic Steam AppId 910123: initially Single-player (2) + Multi-player (1)
        string currentSteamPayload = BuildSteamCategoriesJson(910123, (2, "Single-player"), (1, "Multi-player"));
        var httpHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Volatile.Read(ref currentSteamPayload), Encoding.UTF8, "application/json"),
        });

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
        using var classifier = new SteamAppCategoryClassifier(
            database: sandbox.Database,
            httpMessageHandler: httpHandler,
            timeProvider: clock,
            cacheTtl: TimeSpan.FromMinutes(10));
        using var gate = new TrainerSafetyGate(
            guard,
            sandbox.Database,
            clock,
            new FakeAntiCheatSystemProbe(),
            steamCategoryClassifier: classifier);

        var scanOptions = new GameScanOptions { AllowOnlineMetadataLookup = true };
        var scanner = new GameDirectoryScanner(
            guard,
            new GameArtworkExtractor(sandbox.Paths),
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(
            sandbox.Database,
            sandbox.Paths,
            scanner,
            new InMemorySpawnEventBus(),
            clock,
            trainerSafetyGate: gate,
            scanOptions: scanOptions);

        string gameDir = sandbox.CreateSubDirectory("games", "HybridActionGame");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "hybrid.exe"), new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "start_protected_game.exe"), [0x4D, 0x5A]);
        await File.WriteAllTextAsync(Path.Combine(gameDir, "steam_appid.txt"), "910123", Encoding.UTF8);

        var imported = await library.ImportGameAsync(gameDir);
        Assert.NotNull(imported);
        Assert.Equal(AntiCheatTier.Tier2, imported!.AntiCheatTier);
        Assert.False(imported.VerifiedOfflineBypass);

        // User confirms VerifiedOfflineBypass -> static Tier stays Tier2!
        DateTimeOffset confirmedAt = clock.GetUtcNow();
        var confirmed = await library.UpdateGameMetadataAsync(imported.GameId, new GameMetadataUpdate
        {
            VerifiedOfflineBypass = true,
            VerifiedOfflineBypassAtUtc = confirmedAt,
        });
        Assert.NotNull(confirmed);
        Assert.Equal(AntiCheatTier.Tier2, confirmed!.AntiCheatTier);
        Assert.True(confirmed.VerifiedOfflineBypass);
        Assert.Equal(confirmedAt, confirmed.VerifiedOfflineBypassAtUtc);

        // 1. Rescan while still eligible Tier 2 with Single-player -> user confirmation MUST be preserved!
        var rescannedKeep = await library.RescanGameAsync(imported.GameId);
        Assert.NotNull(rescannedKeep);
        Assert.Equal(AntiCheatTier.Tier2, rescannedKeep!.AntiCheatTier);
        Assert.True(rescannedKeep.VerifiedOfflineBypass);
        Assert.Equal(confirmedAt, rescannedKeep.VerifiedOfflineBypassAtUtc);

        // 2. Rescan after game loses Single-player category (only Multi-player id=1 remains) -> auto-invalidates!
        Volatile.Write(ref currentSteamPayload, BuildSteamCategoriesJson(910123, (1, "Multi-player"), (36, "Online PvP")));
        clock.Advance(TimeSpan.FromMinutes(15)); // expire TTL cache
        var rescannedLostSp = await library.RescanGameAsync(imported.GameId);
        Assert.NotNull(rescannedLostSp);
        Assert.Equal(AntiCheatTier.Tier2, rescannedLostSp!.AntiCheatTier);
        Assert.False(rescannedLostSp.VerifiedOfflineBypass);
        Assert.Null(rescannedLostSp.VerifiedOfflineBypassAtUtc);

        // 3. Re-confirm with Single-player restored, then upgrade directory to Tier 3 (SGuard64.exe) -> auto-invalidates!
        Volatile.Write(ref currentSteamPayload, BuildSteamCategoriesJson(910123, (2, "Single-player"), (1, "Multi-player")));
        clock.Advance(TimeSpan.FromMinutes(15));
        _ = await library.RescanGameAsync(imported.GameId);
        _ = await library.UpdateGameMetadataAsync(imported.GameId, new GameMetadataUpdate { VerifiedOfflineBypass = true });
        Assert.True((await library.GetGameByIdAsync(imported.GameId))!.VerifiedOfflineBypass);

        await File.WriteAllBytesAsync(Path.Combine(gameDir, "SGuard64.exe"), [0x4D, 0x5A]);
        var rescannedTier3 = await library.RescanGameAsync(imported.GameId);
        Assert.NotNull(rescannedTier3);
        Assert.Equal(AntiCheatTier.Tier3, rescannedTier3!.AntiCheatTier);
        Assert.False(rescannedTier3.VerifiedOfflineBypass);
        Assert.Null(rescannedTier3.VerifiedOfflineBypassAtUtc);

        // 4. Verify Tier 1 also auto-invalidates if static Tier transitions to Tier 1.
        File.Delete(Path.Combine(gameDir, "SGuard64.exe"));
        _ = await library.RescanGameAsync(imported.GameId);
        _ = await library.UpdateGameMetadataAsync(imported.GameId, new GameMetadataUpdate { VerifiedOfflineBypass = true });
        Assert.True((await library.GetGameByIdAsync(imported.GameId))!.VerifiedOfflineBypass);

        File.Delete(Path.Combine(gameDir, "start_protected_game.exe"));
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "HoYoKProtect.sys"), [0x00]);
        var rescannedTier1 = await library.RescanGameAsync(imported.GameId);
        Assert.NotNull(rescannedTier1);
        Assert.Equal(AntiCheatTier.Tier1, rescannedTier1!.AntiCheatTier);
        Assert.False(rescannedTier1.VerifiedOfflineBypass);
    }

    [Fact]
    public void M3A04_Watchdog_ZeroPollingWhenIdle_TriggersOnAcProcessDriverOrProtectedGameDuringActiveSession()
    {
        var clock = new ManualTimeProvider();
        var probe = new FakeAntiCheatSystemProbe();
        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: static () => [],
            pollInterval: TimeSpan.FromMilliseconds(250));

        string erDir = Path.Combine(Path.GetTempPath(), "spawn-wd-er");
        string r6Dir = Path.Combine(Path.GetTempPath(), "spawn-wd-r6");
        var libraryGames = new List<GameRecord>
        {
            new()
            {
                GameId = "er-offline",
                Title = "ELDEN RING",
                InstallDir = erDir,
                ExecutableRelPath = "eldenring.exe",
                AntiCheatTier = AntiCheatTier.Tier2,
                VerifiedOfflineBypass = true,
            },
            new()
            {
                GameId = "r6-online",
                Title = "Rainbow Six Siege",
                InstallDir = r6Dir,
                ExecutableRelPath = "RainbowSix.exe",
                AntiCheatTier = AntiCheatTier.Tier2,
                VerifiedOfflineBypass = false,
            },
        };

        using var gate = new TrainerSafetyGate(
            guard,
            database: null,
            timeProvider: clock,
            systemProbe: probe,
            libraryGamesProvider: () => libraryGames,
            watchdogInterval: TimeSpan.FromSeconds(2));

        var emergencyEvents = new List<TrainerEmergencyStopEvent>();
        gate.EmergencyStopTriggered += (_, evt) => emergencyEvents.Add(evt);

        // 1. No active trainer sessions -> strictly 0 polling even after 60 seconds!
        Assert.False(gate.IsWatchdogActive);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, probe.ProcessEnumerationCount);
        Assert.Equal(0, probe.DriverEnumerationCount);
        Assert.Empty(emergencyEvents);

        // 2. Start two concurrent trainer sessions -> ending only one keeps watchdog active until ALL sessions end!
        gate.NotifyTrainerSessionStarted("sess-er", "er-offline");
        gate.NotifyTrainerSessionStarted("sess-er-2", "er-offline");
        Assert.True(gate.IsWatchdogActive);

        // Running eldenring.exe (non-AC exe of the active verified-offline-bypassed session) + idle vgk.sys -> NO emergency stop!
        probe.SetProcesses(new SafeProcessEntry(4010, 4, "eldenring.exe", Path.Combine(erDir, "eldenring.exe")));
        probe.SetLoadedDrivers("vgk.sys");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, probe.ProcessEnumerationCount);
        Assert.Empty(emergencyEvents);

        // Ending first session while second is still active keeps watchdog running.
        gate.NotifyTrainerSessionEnded("sess-er");
        Assert.True(gate.IsWatchdogActive);

        // 3. Session kernel AC driver (BEDaisy.sys) loads -> immediate emergency stop & guard lockdown!
        probe.SetLoadedDrivers("BEDaisy.sys");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(emergencyEvents);
        Assert.Contains("BEDaisy.sys", emergencyEvents[0].DetectedDrivers);
        Assert.True(guard.CurrentSnapshot.IsHighRiskLocked);

        // End remaining session -> watchdog stops completely -> 0 further polls after 60 seconds!
        gate.NotifyTrainerSessionEnded("sess-er-2");
        Assert.False(gate.IsWatchdogActive);
        int pollsAtEnd = probe.ProcessEnumerationCount;
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(pollsAtEnd, probe.ProcessEnumerationCount);

        // 4. Separate clean gate: external protected game (cs2.exe) triggers emergency stop immediately.
        using var guard2 = new AntiCheatGuardService(timeProvider: clock, residualProcessProbe: static () => []);
        var probe2 = new FakeAntiCheatSystemProbe();
        using var gate2 = new TrainerSafetyGate(
            guard2,
            timeProvider: clock,
            systemProbe: probe2,
            libraryGamesProvider: () => libraryGames,
            watchdogInterval: TimeSpan.FromSeconds(2));
        var events2 = new List<TrainerEmergencyStopEvent>();
        gate2.EmergencyStopTriggered += (_, evt) => events2.Add(evt);

        gate2.NotifyTrainerSessionStarted("sess-2", "tier0-game");
        probe2.SetProcesses(new SafeProcessEntry(8801, 4, "cs2.exe", @"C:\Steam\cs2.exe"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(events2);
        Assert.Contains("cs2.exe", events2[0].DetectedProcesses);
        Assert.True(guard2.CurrentSnapshot.IsHighRiskLocked);
        gate2.NotifyTrainerSessionEnded("sess-2");
    }

    [Fact]
    public async Task M3A04_CooldownStateMachine_WaitsForSessionKernelAntiCheatDriverUnloadBefore15sCountdown()
    {
        var clock = new ManualTimeProvider();
        IReadOnlyList<string> residualProcesses = [];
        IReadOnlyList<string> sessionDrivers = [];

        using var guard = new AntiCheatGuardService(
            timeProvider: clock,
            cooldownDuration: TimeSpan.FromSeconds(15),
            residualProcessProbe: () => Volatile.Read(ref residualProcesses),
            pollInterval: TimeSpan.FromMilliseconds(250),
            sessionDriverProbe: () => Volatile.Read(ref sessionDrivers));

        guard.NotifyProtectedGameStarted("game-ace-drv", AntiCheatTier.Tier3);
        // Daemon process has already exited, but kernel driver ACE-BASE.sys is still loaded!
        Volatile.Write(ref residualProcesses, []);
        Volatile.Write(ref sessionDrivers, ["ACE-BASE.sys"]);

        Task exitTask = guard.NotifyProtectedGameExitedAsync("game-ace-drv", AntiCheatTier.Tier3);

        // Advance 5 seconds while ACE-BASE.sys remains loaded -> MUST stay in ResidualGuardWatch (cooldown NOT started!).
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }

        Assert.Equal(AntiCheatGuardPhase.ResidualGuardWatch, guard.CurrentSnapshot.Phase);
        Assert.Contains("ACE-BASE.sys", guard.CurrentSnapshot.DetectedResidualProcesses);
        Assert.False(exitTask.IsCompleted);

        // Unload ACE-BASE.sys -> enters full 15s CooldownLocked!
        Volatile.Write(ref sessionDrivers, []);
        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked);
        Assert.True(guard.CurrentSnapshot.RemainingCooldownSeconds > 14.0);

        // Mid-cooldown resurgence of EasyAntiCheat_EOS.sys -> returns to ResidualGuardWatch!
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }

        Volatile.Write(ref sessionDrivers, ["EasyAntiCheat_EOS.sys"]);
        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.ResidualGuardWatch);
        Assert.Contains("EasyAntiCheat_EOS.sys", guard.CurrentSnapshot.DetectedResidualProcesses);

        // Unload EasyAntiCheat_EOS.sys -> fresh full 15s cooldown -> IdleSafe!
        Volatile.Write(ref sessionDrivers, []);
        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.CooldownLocked);
        Assert.True(guard.CurrentSnapshot.RemainingCooldownSeconds > 14.0);

        await AdvanceUntilAsync(clock, () => guard.CurrentSnapshot.Phase == AntiCheatGuardPhase.IdleSafe);
        await exitTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AntiCheatGuardPhase.IdleSafe, guard.CurrentSnapshot.Phase);
        Assert.False(guard.CurrentSnapshot.IsHighRiskLocked);
    }

    // =========================================================================
    // M3-A-05: LOOP-06 automatic purge, sandbox orphans, renamed CE driver & CEDRIVER*
    // =========================================================================

    [Fact]
    public async Task M3A05_Loop06_ProtectedLaunchPurgesWorkersReChecksSandboxOrphansAndNeverKillsUserTools()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string tier2Dir = sandbox.CreateSubDirectory("games", "Loop06Shooter");
        await File.WriteAllBytesAsync(Path.Combine(tier2Dir, "shooter.exe"), [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(Path.Combine(tier2Dir, "start_protected_game.exe"), [0x4D, 0x5A]);

        var protectedGame = new GameRecord
        {
            GameId = "game-loop06",
            Title = "Loop06 Shooter",
            InstallDir = tier2Dir,
            ExecutableRelPath = "shooter.exe",
            AntiCheatTier = AntiCheatTier.Tier2,
        };
        await sandbox.Database.UpsertGameAsync(protectedGame);

        string tier0Dir = sandbox.CreateSubDirectory("games", "Loop06Offline");
        await File.WriteAllBytesAsync(Path.Combine(tier0Dir, "offline.exe"), [0x4D, 0x5A]);
        var tier0Game = new GameRecord
        {
            GameId = "game-tier0",
            Title = "Offline Indie",
            InstallDir = tier0Dir,
            ExecutableRelPath = "offline.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await sandbox.Database.UpsertGameAsync(tier0Game);

        // Case 1: Hook runs within 2s budget and clears Spawn.TrainerWorker -> secondary re-check passes -> launch succeeds!
        HighRiskEnvironmentReport currentEnv = new(["Spawn.TrainerWorker.exe"], []);
        IReadOnlyList<string> sandboxOrphans = [];

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: () => Volatile.Read(ref currentEnv));

        var hook = new FakeShutdownHook(budget =>
        {
            Volatile.Write(ref currentEnv, HighRiskEnvironmentReport.Clean);
            return new SensitiveWorkerShutdownReport
            {
                Succeeded = true,
                TerminatedWorkerCount = 1,
                Elapsed = TimeSpan.FromMilliseconds(40),
            };
        });

        var starter = new StubGameProcessStarter();
        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            processStarter: starter,
            options: new GameLaunchOptions
            {
                ForceCreateNoWindow = true,
                ShellBreakObservationWindow = TimeSpan.Zero,
            },
            sensitiveWorkerShutdownHooks: [hook],
            paths: sandbox.Paths,
            sandboxOrphanProbe: _ => Volatile.Read(ref sandboxOrphans));

        var okResult = await launchService.LaunchGameAsync(protectedGame.GameId);
        Assert.True(okResult.Succeeded);
        Assert.Equal(1, hook.CallCount);
        Assert.InRange(hook.LastBudget, TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(2));
        Assert.Equal(1, starter.ExplorerLaunchCount);
        await launchService.WaitForGameExitAsync(protectedGame.GameId);

        // Wait briefly for guard exit transition if needed, then reset with fresh guard for blocked scenarios.
        using var guard2 = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: () => Volatile.Read(ref currentEnv));
        var hook2 = new FakeShutdownHook(_ => SensitiveWorkerShutdownReport.Clean);
        var starter2 = new StubGameProcessStarter();
        await using var launchService2 = new GameLaunchService(
            sandbox.Database,
            guard2,
            new InMemorySpawnEventBus(),
            processStarter: starter2,
            options: new GameLaunchOptions { ForceCreateNoWindow = true, ShellBreakObservationWindow = TimeSpan.Zero },
            sensitiveWorkerShutdownHooks: [hook2],
            paths: sandbox.Paths,
            sandboxOrphanProbe: _ => Volatile.Read(ref sandboxOrphans));

        // Case 2: Sandbox path prefix orphan STILL alive after hook -> secondary re-check blocks launch!
        Volatile.Write(ref currentEnv, HighRiskEnvironmentReport.Clean);
        Volatile.Write(ref sandboxOrphans, ["orphan_trainer.exe (沙箱孤儿进程 PID 9012)"]);
        var blockedByOrphan = await launchService2.LaunchGameAsync(protectedGame.GameId);
        Assert.False(blockedByOrphan.Succeeded);
        Assert.Equal("HIGH_RISK_ENVIRONMENT_BLOCKED", blockedByOrphan.ErrorCode);
        Assert.Contains("orphan_trainer.exe", blockedByOrphan.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, hook2.CallCount);
        Assert.Equal(0, starter2.ExplorerLaunchCount);

        // Case 3: User's own Cheat Engine running -> blocked, NEVER killed!
        Volatile.Write(ref sandboxOrphans, []);
        Volatile.Write(ref currentEnv, new HighRiskEnvironmentReport(["cheatengine-x86_64.exe"], ["stealth_ce.sys (改名 CE 驱动: OriginalFilename=dbk64.sys)"]));
        var blockedByUserCe = await launchService2.LaunchGameAsync(protectedGame.GameId);
        Assert.False(blockedByUserCe.Succeeded);
        Assert.Equal("HIGH_RISK_ENVIRONMENT_BLOCKED", blockedByUserCe.ErrorCode);
        Assert.Contains("cheatengine-x86_64.exe", blockedByUserCe.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("stealth_ce.sys", blockedByUserCe.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, starter2.ExplorerLaunchCount);

        // Case 4: Tier 0 launch must NOT invoke sensitive worker shutdown hooks.
        int callsBeforeTier0 = hook2.CallCount;
        Volatile.Write(ref currentEnv, HighRiskEnvironmentReport.Clean);
        var tier0Launch = await launchService2.LaunchGameAsync(tier0Game.GameId);
        Assert.Equal(callsBeforeTier0, hook2.CallCount);
        if (tier0Launch.Succeeded)
        {
            await launchService2.StopGameAsync(tier0Game.GameId);
        }

        // Case 5: CS2 stored as stale Tier 0 in DB (zero local AC files) is caught by 4-layer inspection during launch!
        string cs2Dir = sandbox.CreateSubDirectory("games", "Cs2StaleTier0");
        await File.WriteAllBytesAsync(Path.Combine(cs2Dir, "cs2.exe"), [0x4D, 0x5A]);
        var staleCs2 = new GameRecord
        {
            GameId = "game-cs2-stale",
            Title = "Counter-Strike 2",
            InstallDir = cs2Dir,
            ExecutableRelPath = "cs2.exe",
            SteamAppId = 730,
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await sandbox.Database.UpsertGameAsync(staleCs2);

        int callsBeforeCs2 = hook2.CallCount;
        Volatile.Write(ref sandboxOrphans, ["leftover.exe (沙箱孤儿进程 PID 7777)"]);
        var blockedCs2 = await launchService2.LaunchGameAsync(staleCs2.GameId);
        Assert.False(blockedCs2.Succeeded);
        Assert.Equal("HIGH_RISK_ENVIRONMENT_BLOCKED", blockedCs2.ErrorCode);
        Assert.Equal(callsBeforeCs2 + 1, hook2.CallCount);
    }

    [Fact]
    public async Task M3A05_GameLaunchService_VerifiedOfflineBypass_NonAcExeDoesNotPurgeWhileAcLauncherStillProtects()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string erDir = sandbox.CreateSubDirectory("games", "EldenRingLaunch");
        await File.WriteAllBytesAsync(Path.Combine(erDir, "eldenring.exe"), [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(Path.Combine(erDir, "start_protected_game.exe"), [0x4D, 0x5A]);

        var eldenRingOffline = new GameRecord
        {
            GameId = "game-er-bypass",
            Title = "ELDEN RING",
            InstallDir = erDir,
            ExecutableRelPath = "eldenring.exe",
            SteamAppId = 1245620,
            AntiCheatTier = AntiCheatTier.Tier2,
            VerifiedOfflineBypass = true,
            VerifiedOfflineBypassAtUtc = DateTimeOffset.UtcNow,
        };
        await sandbox.Database.UpsertGameAsync(eldenRingOffline);

        var eldenRingProtected = eldenRingOffline with
        {
            GameId = "game-er-eac-launcher",
            ExecutableRelPath = "start_protected_game.exe",
        };
        await sandbox.Database.UpsertGameAsync(eldenRingProtected);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
        var probe = new FakeAntiCheatSystemProbe();
        using var gate = new TrainerSafetyGate(
            guard,
            sandbox.Database,
            new ManualTimeProvider(),
            probe,
            steamHttpMessageHandler: new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var hook = new FakeShutdownHook(_ => SensitiveWorkerShutdownReport.Clean);
        var starter = new StubGameProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            processStarter: starter,
            options: new GameLaunchOptions { ForceCreateNoWindow = true, ShellBreakObservationWindow = TimeSpan.Zero },
            sensitiveWorkerShutdownHooks: [hook],
            trainerSafetyGate: gate);

        // 1. VerifiedOfflineBypass on eldenring.exe -> direct launch, 0 LOOP-06 purges, static Tier stays Tier2!
        var bypassRes = await launchService.LaunchGameAsync(eldenRingOffline.GameId);
        Assert.True(bypassRes.Succeeded);
        Assert.Equal(AntiCheatTier.Tier2, bypassRes.Session!.AntiCheatTier);
        Assert.Equal(0, hook.CallCount);
        Assert.Equal(1, starter.DirectLaunchCount);
        Assert.Equal(0, starter.ExplorerLaunchCount);
        Assert.Equal(AntiCheatGuardPhase.IdleSafe, guard.CurrentSnapshot.Phase);
        await launchService.StopGameAsync(eldenRingOffline.GameId);

        // 2. VerifiedOfflineBypass configured on start_protected_game.exe -> MUST still enforce protected Tier 2 launch + LOOP-06!
        var eacRes = await launchService.LaunchGameAsync(eldenRingProtected.GameId);
        Assert.True(eacRes.Succeeded);
        Assert.Equal(1, hook.CallCount);
        Assert.Equal(1, starter.ExplorerLaunchCount);
        await launchService.WaitForGameExitAsync(eldenRingProtected.GameId);
    }

    [Fact]
    public void M3A05_HighRiskEnvironmentProbe_RenamedCeDriverMetadata_CedriverPrefix_AndSandboxOrphans_ZeroFalsePositives()
    {
        // 1. ResolveDriverImagePath across Windows SCM ImagePath conventions.
        Assert.Equal(@"C:\Tools\stealth.sys", HighRiskEnvironmentProbe.ResolveDriverImagePath(@"\??\C:\Tools\stealth.sys", @"C:\Windows"));
        Assert.Equal(@"C:\Windows\System32\drivers\ntfs.sys", HighRiskEnvironmentProbe.ResolveDriverImagePath(@"\SystemRoot\System32\drivers\ntfs.sys", @"C:\Windows"));
        Assert.Equal(@"C:\Windows\System32\drivers\kbdclass.sys", HighRiskEnvironmentProbe.ResolveDriverImagePath(@"System32\drivers\kbdclass.sys", @"C:\Windows"));
        Assert.Null(HighRiskEnvironmentProbe.ResolveDriverImagePath(@"\Device\HarddiskVolume1\foo.sys", @"C:\Windows"));

        // 2. Renamed CE driver metadata matches all 4 FileVersionInfo fields.
        Assert.True(HighRiskEnvironmentProbe.IsRenamedCheatEngineDriverMetadata(
            new DriverVersionMetadata("dbk64.sys", null, null, null), out _));
        Assert.True(HighRiskEnvironmentProbe.IsRenamedCheatEngineDriverMetadata(
            new DriverVersionMetadata(null, "DBK", null, null), out _));
        Assert.True(HighRiskEnvironmentProbe.IsRenamedCheatEngineDriverMetadata(
            new DriverVersionMetadata(null, null, "Cheat Engine 7.5", null), out _));
        Assert.True(HighRiskEnvironmentProbe.IsRenamedCheatEngineDriverMetadata(
            new DriverVersionMetadata(null, null, null, "DBK32 kernel driver"), out _));

        // Benign Windows / GPU / storage / network drivers -> ZERO false positives!
        DriverVersionMetadata[] benignMetadata =
        [
            new("ntfs.sys", "ntfs.sys", "Microsoft Windows Operating System", "NT File System Driver"),
            new("nvlddmkm.sys", "nvlddmkm", "NVIDIA Windows Kernel Mode Driver", "NVIDIA Display Driver"),
            new("kbdclass.sys", "kbdclass.sys", "Microsoft Windows Operating System", "Keyboard Class Driver"),
            new("rdbss.sys", "rdbss.sys", "Microsoft Windows Operating System", "Redirected Drive Buffering SubSystem Driver"),
            new("dbfilter.sys", "dbfilter", "Database Filter", "Disk buffer filter"),
            new("odbc32.sys", "odbc", "Open Database Connectivity", "ODBC helper"),
            new("adbk_audio.sys", "adbk_audio", "Audio Driver", "Analog Digital Buffer Kit"),
        ];

        foreach (var meta in benignMetadata)
        {
            Assert.False(
                HighRiskEnvironmentProbe.IsRenamedCheatEngineDriverMetadata(meta, out string? hit),
                $"Benign metadata unexpectedly flagged: {meta} -> {hit}");
        }

        // 3. QueryDosDeviceW exact CEDRIVER* prefix matching (zero false positives).
        Assert.True(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("CEDRIVER73"));
        Assert.True(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("CEDRIVER75"));
        Assert.True(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("cedriver_custom"));
        Assert.False(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("PhysicalDrive0"));
        Assert.False(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("C:"));
        Assert.False(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("COM1"));
        Assert.False(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("MYCEDRIVER75"));
        Assert.False(HighRiskEnvironmentProbe.IsCheatEngineDosDeviceName("CEDEVICE"));

        // 4. Integrated DetectHighRiskDrivers with injected SCM + FileVersionInfo + DOS devices.
        var activeServices = new ActiveKernelDriverInfo[]
        {
            new("ntfs", @"\SystemRoot\System32\drivers\ntfs.sys", @"C:\Windows\System32\drivers\ntfs.sys", "ntfs.sys"),
            new("MyTotallyNormalSvc", @"\??\C:\Temp\renamed_drv.sys", @"C:\Temp\renamed_drv.sys", "renamed_drv.sys"),
        };

        var metadataByPath = new Dictionary<string, DriverVersionMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Windows\System32\drivers\ntfs.sys"] = benignMetadata[0],
            [@"C:\Temp\renamed_drv.sys"] = new("dbk64.sys", "dbk64", "Cheat Engine", "Dark Byte Kernel"),
        };

        var detectedDrivers = HighRiskEnvironmentProbe.DetectHighRiskDrivers(
            loadedDriverBaseNamesProvider: static () => ["ntoskrnl.exe", "ntfs.sys", "nvlddmkm.sys"],
            activeDriverServicesProvider: () => activeServices,
            versionMetadataReader: path => metadataByPath.GetValueOrDefault(path),
            dosDeviceNamesProvider: static () => ["C:", "COM1", "PhysicalDrive0", "MYCEDRIVER75", "CEDRIVER75"]);

        Assert.Equal(2, detectedDrivers.Count);
        Assert.Contains(detectedDrivers, d => d.Contains("renamed_drv.sys", StringComparison.OrdinalIgnoreCase) && d.Contains("dbk64.sys", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("DOS:CEDRIVER75", detectedDrivers);
        Assert.DoesNotContain(detectedDrivers, d => d.Contains("ntfs.sys", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(detectedDrivers, d => d.Contains("MYCEDRIVER75", StringComparison.OrdinalIgnoreCase));

        // 5. Sandbox path prefix orphan probe.
        var orphans = HighRiskEnvironmentProbe.DetectSandboxOrphanProcesses(
            @"C:\Sandbox\Trainers",
            directoryProcessEnumerator: static _ =>
            [
                new SafeProcessEntry(Environment.ProcessId, 4, "testhost.exe", @"C:\Sandbox\Trainers\testhost.exe"),
                new SafeProcessEntry(4242, 4, "fling_stub.exe", @"C:\Sandbox\Trainers\g1\t1\fling_stub.exe"),
            ]);
        Assert.Single(orphans);
        Assert.Contains("fling_stub.exe", orphans[0], StringComparison.OrdinalIgnoreCase);

        // 6. Real OS read-only smoke test: enumerations succeed cleanly without throwing.
        Assert.NotNull(HighRiskEnvironmentProbe.EnumerateDosDeviceNames());
        Assert.NotNull(HighRiskEnvironmentProbe.EnumerateLoadedDriverBaseNames());
        Assert.NotNull(HighRiskEnvironmentProbe.EnumerateActiveDriverServices());
        Assert.NotNull(HighRiskEnvironmentProbe.DetectHighRiskDrivers());
    }

    [Fact]
    public void DependencyInjection_AddSpawnCore_RegistersTrainerSafetyGateSingletonAndWiresLauncherHooks()
    {
        using var sandbox = new TestSandbox();
        var services = new ServiceCollection();
        services.AddSpawnCore(sandbox.Paths);
        services.AddSpawnLauncher();
        services.AddSingleton<ISensitiveWorkerShutdownHook>(new FakeShutdownHook(_ => SensitiveWorkerShutdownReport.Clean));

        using var provider = services.BuildServiceProvider();
        var gate1 = provider.GetRequiredService<ITrainerSafetyGate>();
        var gate2 = provider.GetRequiredService<ITrainerSafetyGate>();
        Assert.Same(gate1, gate2);
        Assert.False(gate1.IsWatchdogActive);

        var launchService = provider.GetRequiredService<IGameLaunchService>();
        var libraryService = provider.GetRequiredService<IGameLibraryService>();
        Assert.NotNull(launchService);
        Assert.NotNull(libraryService);
    }

    // =========================================================================
    // Test Doubles & Helpers
    // =========================================================================

    private static string BuildSteamCategoriesJson(int appId, params (int Id, string Description)[] categories)
    {
        var items = categories.Select(c => $"{{\"id\":{c.Id},\"description\":\"{c.Description}\"}}");
        return $"{{\"{appId}\":{{\"success\":true,\"data\":{{\"categories\":[{string.Join(",", items)}]}}}}}}";
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeAntiCheatSystemProbe : IAntiCheatSystemProbe
    {
        private IReadOnlyList<SafeProcessEntry> _processes = [];
        private IReadOnlyList<string> _loadedDrivers = [];
        private IReadOnlyList<ActiveKernelDriverInfo> _activeServices = [];
        private int _processEnumCount;
        private int _driverEnumCount;

        public int ProcessEnumerationCount => Volatile.Read(ref _processEnumCount);
        public int DriverEnumerationCount => Volatile.Read(ref _driverEnumCount);

        public void SetProcesses(params SafeProcessEntry[] processes) => Volatile.Write(ref _processes, processes);
        public void SetLoadedDrivers(params string[] drivers) => Volatile.Write(ref _loadedDrivers, drivers);
        public void SetActiveServices(params ActiveKernelDriverInfo[] services) => Volatile.Write(ref _activeServices, services);

        public void ClearAll()
        {
            SetProcesses();
            SetLoadedDrivers();
            SetActiveServices();
        }

        public IReadOnlyList<SafeProcessEntry> EnumerateProcesses(bool resolveFullPaths)
        {
            Interlocked.Increment(ref _processEnumCount);
            return Volatile.Read(ref _processes);
        }

        public IReadOnlyList<string> EnumerateLoadedDriverBaseNames()
        {
            Interlocked.Increment(ref _driverEnumCount);
            return Volatile.Read(ref _loadedDrivers);
        }

        public IReadOnlyList<ActiveKernelDriverInfo> EnumerateActiveDriverServices() =>
            Volatile.Read(ref _activeServices);
    }

    private sealed class FakeShutdownHook(Func<TimeSpan, SensitiveWorkerShutdownReport> handler) : ISensitiveWorkerShutdownHook
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TimeSpan LastBudget { get; private set; }

        public Task<SensitiveWorkerShutdownReport> ShutdownAllForProtectedLaunchAsync(TimeSpan budget, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastBudget = budget;
            return Task.FromResult(handler(budget));
        }
    }

    private sealed class StubGameProcessStarter : IGameProcessStarter
    {
        private int _directCount;
        private int _explorerCount;

        public int DirectLaunchCount => Volatile.Read(ref _directCount);
        public int ExplorerLaunchCount => Volatile.Read(ref _explorerCount);

        public Process StartDirectProcess(GameProcessStartRequest request)
        {
            Interlocked.Increment(ref _directCount);
            // Spawn a silent, short-lived cmd.exe /c ping-free timeout stub that exits cleanly on StopGameAsync.
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/c exit 0",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start stub process.");
        }

        public Task StartExplorerDecoupledAsync(GameProcessStartRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _explorerCount);
            return Task.CompletedTask;
        }
    }

    private static async Task AdvanceUntilAsync(ManualTimeProvider clock, Func<bool> predicate, int maxSteps = 400)
    {
        for (int i = 0; i < maxSteps && !predicate(); i++)
        {
            await Task.Delay(2);
            if (predicate())
            {
                return;
            }

            clock.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.True(predicate(), "Condition did not become true within the virtual clock budget.");
    }
}
