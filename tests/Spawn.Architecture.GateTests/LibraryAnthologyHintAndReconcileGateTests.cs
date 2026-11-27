using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Threading;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Architecture.GateTests;

[Collection(WpfStaHeadlessCollection.Name)]
public sealed class LibraryAnthologyHintAndReconcileGateTests
{
    [Fact]
    public async Task MainWindowViewModel_InitializeAndDefaultScan_AutoRetiresStaleAnthologyParentRow_AndSuppressesFalseUnimportedHint()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-anthology-hint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string workspaceRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "workspace")).FullName;
            string dataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "data")).FullName;
            string localGamesRoot = Directory.CreateDirectory(Path.Combine(workspaceRoot, ".local", "games")).FullName;

            // 1. Build realistic .local/games layout: 3 single games + 1 expanded 3-game anthology + _OST non-game dir
            string limboDir = Directory.CreateDirectory(Path.Combine(localGamesRoot, "LIMBO")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(limboDir, "goggame-1724299977.info"),
                """{"gameId":"1724299977","name":"LIMBO","playTasks":[{"isPrimary":true,"category":"game","type":"FileTask","path":"limbo.exe"}]}""",
                Encoding.UTF8);
            await File.WriteAllBytesAsync(Path.Combine(limboDir, "limbo.exe"), new byte[8192]);

            string stanleyDir = Directory.CreateDirectory(Path.Combine(localGamesRoot, "The Stanley Synthetic")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(stanleyDir, "Stanley.exe"), new byte[16384]);
            await File.WriteAllTextAsync(Path.Combine(stanleyDir, "steam_appid.txt"), "1703340\n", Encoding.UTF8);

            string vsDir = Directory.CreateDirectory(Path.Combine(localGamesRoot, "Vampire Synthetic")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(vsDir, "Vampire.exe"), new byte[16384]);
            await File.WriteAllTextAsync(Path.Combine(vsDir, "steam_appid.txt"), "1794680\n", Encoding.UTF8);

            string ostDir = Directory.CreateDirectory(Path.Combine(localGamesRoot, "_OST")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(ostDir, "AlbumPlayer.exe"), new byte[4096]);

            string anthologyDir = CreateSyntheticAnthologyInLocalGames(localGamesRoot);

            var paths = SpawnPaths.CreateIsolated(dataRoot, workspaceRootHint: workspaceRoot);
            paths.EnsureDirectoriesExist();
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(localGamesRoot),
                Path.TrimEndingDirectorySeparator(paths.LocalGamesDirectory!),
                ignoreCase: true);

            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            var eventBus = new InMemorySpawnEventBus();
            var scanOptions = new GameScanOptions { AllowOnlineMetadataLookup = false };
            var scanner = new GameDirectoryScanner(
                guard,
                new GameArtworkExtractor(paths),
                options: scanOptions);
            var library = new GameLibraryService(db, paths, scanner, eventBus, scanOptions: scanOptions);

            // Import the 3 single games + expand anthology into 3 child games (6 legitimate games total)
            await library.ScanLibraryDirectoryAsync(localGamesRoot);
            Assert.Equal(6, (await db.GetAllGamesAsync()).Count);

            // Pre-seed the buggy 7th row (4th anthology duplicate pointing to anthologyDir, unpinned, 0 unlocks)
            string staleFourthId = SpawnId.NewUlid();
            await db.UpsertGameAsync(new GameRecord
            {
                GameId = staleFourthId,
                Title = "Grand Theft Auto III – The Definitive Edition",
                SortTitle = "Grand Theft Auto III – The Definitive Edition",
                InstallDir = anthologyDir,
                ExecutableRelPath = Path.Combine("Episode III - Definitive Edition", "Gameface", "Binaries", "Win64", "Episode3.exe"),
                PlatformSource = PlatformSource.Steamrip,
                SteamAppId = 902001,
                AchievementCaptureMode = AchievementCaptureMode.SteamEmulator,
            });
            Assert.Equal(7, (await db.GetAllGamesAsync()).Count);

            ExceptionDispatchInfo? capturedError = null;
            var staThread = new Thread(() =>
            {
                var staDispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                _ = staDispatcher.InvokeAsync(() => _ = ExecuteOnStaAsync());
                Dispatcher.PushFrame(frame);
                staDispatcher.InvokeShutdown();

                async Task ExecuteOnStaAsync()
                {
                    try
                    {
                        var achievements = new NoOpAchievementService();
                        var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
                        await using var ipcClient = new OverlayIpcClient(paths);
                        var themeMgr = new ThemeManagerService();
                        var fakeLaunch = new NoOpGameLaunchService();
                        var fakeGameData = new NoOpGameDataManagementService();
                        var fakeGate = new NoOpTrainerSafetyGate();
                        var fakeCatalog = new NoOpTrainerCatalogService();
                        var fakeRuntime = new NoOpTrainerRuntimeService();
                        var fakeSnapshots = new NoOpPreTrainerSnapshotService();

                        var settingsVm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
                        var trainerVm = new TrainerWorkbenchViewModel(
                            paths, db, fakeCatalog, fakeRuntime, fakeSnapshots, fakeGate, guard, library, fakeLaunch, static _ => { });
                        using var mainVm = new MainWindowViewModel(
                            paths, db, library, fakeLaunch, achievements, fakeGameData, guard, ipcClient,
                            eventBus, fakeCatalog, fakeRuntime, fakeGate, settingsVm, trainerVm, static _ => { },
                            uiDispatcher: staDispatcher);

                        await mainVm.InitializeAsync();
                        await staDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);

                        // 1. Startup InitializeAsync automatically retires the stale 4th anthology parent row -> 6 games total!
                        Assert.Equal(6, mainVm.AllGames.Count);
                        Assert.Null(await db.GetGameByIdAsync(staleFourthId));
                        Assert.DoesNotContain(
                            mainVm.AllGames,
                            g => string.Equals(
                                Path.TrimEndingDirectorySeparator(g.Record.InstallDir),
                                Path.TrimEndingDirectorySeparator(anthologyDir),
                                StringComparison.OrdinalIgnoreCase));

                        // 2. Expanded anthology container & _OST MUST NOT trigger a false-positive unimported hint!
                        Assert.False(mainVm.HasUnimportedLocalGamesHint);
                        Assert.Equal(string.Empty, mainVm.UnimportedLocalGamesHintText);

                        // 3. Drop a genuinely new standalone game folder into .local/games -> hint shows ONLY 1 pending game
                        string newGameDir = Directory.CreateDirectory(Path.Combine(localGamesRoot, "Celeste Synthetic")).FullName;
                        File.WriteAllBytes(Path.Combine(newGameDir, "Celeste.exe"), new byte[12288]);
                        File.WriteAllText(Path.Combine(newGameDir, "steam_appid.txt"), "504230\n", Encoding.UTF8);

                        var pending = GameDirectoryScanner.FindUnimportedLocalGameFolderNames(
                            paths.LocalGamesDirectory,
                            mainVm.AllGames.Select(static g => g.Record.InstallDir));
                        Assert.Equal(["Celeste Synthetic"], pending);

                        await mainVm.InitializeAsync();
                        await staDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                        Assert.True(mainVm.HasUnimportedLocalGamesHint);
                        Assert.Equal("检测到本地游戏目录存在 1 款待入库游戏（Celeste Synthetic）", mainVm.UnimportedLocalGamesHintText);

                        // 4. Running ScanDefaultLibraryCommand imports the 7th game cleanly WITHOUT re-creating any anthology parent duplicate!
                        await mainVm.ScanDefaultLibraryCommand.ExecuteAsync(null);
                        await staDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);

                        Assert.Equal(7, mainVm.AllGames.Count);
                        Assert.False(mainVm.HasUnimportedLocalGamesHint);
                        Assert.Equal(string.Empty, mainVm.UnimportedLocalGamesHintText);
                        Assert.DoesNotContain(
                            mainVm.AllGames,
                            g => string.Equals(
                                Path.TrimEndingDirectorySeparator(g.Record.InstallDir),
                                Path.TrimEndingDirectorySeparator(anthologyDir),
                                StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex)
                    {
                        capturedError = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();
            Assert.True(staThread.Join(TimeSpan.FromSeconds(30)), "STA anthology hint & reconcile gate test timed out.");

            capturedError?.Throw();
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp directory cleanup.
            }
        }
    }

    private static string CreateSyntheticAnthologyInLocalGames(string localGamesRoot)
    {
        string anthologyDir = Directory.CreateDirectory(Path.Combine(localGamesRoot, "Synthetic Trilogy - DE")).FullName;
        File.WriteAllBytes(Path.Combine(anthologyDir, "Launcher.exe"), new byte[16384]);

        (string Folder, string StubExe, string RealExe, int AppId)[] episodes =
        [
            ("Episode III - Definitive Edition", "PlayEp3.exe", "Episode3.exe", 902001),
            ("Episode VC - Definitive Edition", "PlayEpVC.exe", "EpisodeVC.exe", 902002),
            ("Episode SA - Definitive Edition", "PlayEpSA.exe", "EpisodeSA.exe", 902003),
        ];

        foreach (var ep in episodes)
        {
            string subRoot = Directory.CreateDirectory(Path.Combine(anthologyDir, ep.Folder)).FullName;
            File.WriteAllBytes(Path.Combine(subRoot, ep.StubExe), new byte[4096]);
            File.WriteAllBytes(Path.Combine(subRoot, "title.rgl"), [0x52, 0x47, 0x4C, 0x4D]);

            Directory.CreateDirectory(Path.Combine(subRoot, "Engine", "Binaries", "Win64"));
            string steamworks = Directory.CreateDirectory(
                Path.Combine(subRoot, "Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv147", "Win64")).FullName;
            File.WriteAllBytes(Path.Combine(steamworks, "steam_api64.dll"), [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(steamworks, "steam_api64.cdx"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(steamworks, "steam_emu.ini"), $"[Settings]\nAppId={ep.AppId}\n", Encoding.UTF8);

            Directory.CreateDirectory(Path.Combine(subRoot, "Gameface", "Content", "Paks"));
            string win64 = Directory.CreateDirectory(Path.Combine(subRoot, "Gameface", "Binaries", "Win64")).FullName;
            File.WriteAllBytes(Path.Combine(win64, ep.RealExe), new byte[65536]);
        }

        return anthologyDir;
    }

    private sealed class NoOpAchievementService : IAchievementService
    {
        public event EventHandler<AchievementUnlockedNotification>? AchievementUnlocked
        {
            add { }
            remove { }
        }

        public Task<GameAchievementSummaryDto> GetGameAchievementsAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameAchievementSummaryDto
            {
                GameId = gameId,
                ChannelStatusText = "就绪",
            });

        public Task<IReadOnlyList<AchievementUnlockDto>> GetRecentUnlocksAsync(int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AchievementUnlockDto>>([]);

        public Task<GameAchievementSummaryDto> SyncGameSchemaAsync(string gameId, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            GetGameAchievementsAsync(gameId, cancellationToken);

        public Task<int> ScanLocalUnlocksAsync(string gameId, bool silentSeed = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<bool> SetAchievementUnlockedAsync(string gameId, string apiName, bool unlocked, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<GbeForkActivationResult> ActivateGbeForkAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GbeForkActivationResult { Succeeded = false, Message = "stub" });

        public Task<GbeForkActivationResult> RestoreOriginalDllAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GbeForkActivationResult { Succeeded = false, Message = "stub" });

        public Task SendTestToastAsync(string? gameId = null, OverlayThemeKind? themeOverride = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpGameLaunchService : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
        {
            add { }
            remove { }
        }

        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameLaunchResult.Failure("STUB", "stub"));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }

    private sealed class NoOpGameDataManagementService : IGameDataManagementService
    {
        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(string gameId, bool refreshOnlineRules = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameDataEntryDto>>([]);
        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDataCleanResult { Succeeded = false, Message = "stub" });
        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);
        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDataCleanResult { Succeeded = false, Message = "stub" });
    }

    private sealed class NoOpTrainerSafetyGate : ITrainerSafetyGate
    {
        public bool IsWatchdogActive => false;
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered
        {
            add { }
            remove { }
        }

        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport
            {
                EffectiveTier = game.AntiCheatTier,
                HasSinglePlayerCategory = true,
                IsMultiplayerOnly = false,
            });

        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveAntiCheatProbeReport());

        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default) =>
            Task.FromResult(new VerifiedOfflineBypass
            {
                GameId = game.GameId,
                StaticAntiCheatTier = game.AntiCheatTier,
                HasSinglePlayerMode = true,
                IsLiveAntiCheatClear = true,
            });

        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default) =>
            Task.FromResult(new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.Allowed,
                IsAllowed = true,
                Message = "Tier 0 纯离线单机就绪。",
                StaticGameTier = game.AntiCheatTier,
            });

        public void NotifyTrainerSessionStarted(string sessionId, string gameId) { }
        public void NotifyTrainerSessionEnded(string sessionId) { }
    }

    private sealed class NoOpTrainerCatalogService : ITrainerCatalogService
    {
        public Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerMatchResult { GameId = gameId, EffectiveQuery = userSearchOverride ?? string.Empty });
        public Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false });
        public Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false });
        public Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult(new TrainerIntegrityReport { TrainerId = trainerId, IsValid = true, FileExists = true, HashMatches = true, IsInsideSandbox = true });
        public Task RemoveAsync(string trainerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TrainerCatalogRecord>> ListForGameAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrainerCatalogRecord>>([]);
        public Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrainerHotkeyBindingRecord>>([]);
    }

    private sealed class NoOpTrainerRuntimeService : ITrainerRuntimeService
    {
        public TrainerRuntimeState State => TrainerRuntimeState.Ready;
        public TrainerSessionHandle? ActiveSession => null;
        public event EventHandler<TrainerRuntimeStateChanged>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<TrainerLaunchOutcome> LaunchAsync(string trainerId, TrainerLaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new TrainerLaunchOutcome { Succeeded = false });
        public Task TriggerHotkeyAsync(string sessionId, string entryId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOriginalWindowVisibleAsync(string sessionId, bool visible, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpPreTrainerSnapshotService : IPreTrainerSaveSnapshotService
    {
        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerSnapshotResult { Succeeded = false, Status = PreTrainerSnapshotStatus.Skipped });
        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>([]);
        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerRestoreResult { Succeeded = false, Message = "stub" });
    }
}
