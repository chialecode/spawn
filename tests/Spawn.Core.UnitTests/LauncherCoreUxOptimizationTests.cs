using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Launcher;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class LauncherCoreUxOptimizationTests
{
    [Fact]
    public async Task StopGameAsync_GracefulMode_DoesNotForceKillResistingProcess_AndExplicitForceKillTerminatesCleanly()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string fakeGameDir = sandbox.CreateSubDirectory("games", "GracefulStopTestGame");
        string dummyExe = Path.Combine(fakeGameDir, "game.exe");
        await File.WriteAllBytesAsync(dummyExe, [0x4D, 0x5A]);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Graceful Stop Guard Game",
            InstallDir = fakeGameDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var eventBus = new InMemorySpawnEventBus();
        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);

        var starter = new LongRunningHiddenProcessStarter();
        var options = new GameLaunchOptions
        {
            ForceCreateNoWindow = true,
            PlaytimeSampleInterval = TimeSpan.FromMilliseconds(50),
            ShellBreakTriggerThreshold = TimeSpan.Zero,
            ShellBreakObservationWindow = TimeSpan.Zero,
            GracefulStopTimeout = TimeSpan.FromMilliseconds(220),
            ForceKillWaitTimeout = TimeSpan.FromSeconds(5),
        };

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            eventBus,
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            options: options);

        try
        {
            var launchResult = await launchService.LaunchGameAsync(game.GameId);
            Assert.True(launchResult.Succeeded, launchResult.ErrorMessage);
            Assert.NotNull(starter.LastStartedProcess);
            int startedPid = starter.LastStartedProcess!.Id;
            Assert.True(SafeProcessInspector.IsProcessAlive(startedPid));

            // 1. Default StopGameAsync (graceful-only) MUST NOT kill a process that ignores CloseMainWindow!
            bool defaultStopped = await launchService.StopGameAsync(game.GameId);
            Assert.False(defaultStopped);
            Assert.True(SafeProcessInspector.IsProcessAlive(startedPid));

            // 2. Explicit graceful call returns GracefulTimeoutStillRunning and restores active tracking state.
            GameStopResult gracefulResult = await launchService.StopGameAsync(
                game.GameId,
                forceKill: false,
                gracefulTimeout: TimeSpan.FromMilliseconds(200));
            Assert.False(gracefulResult.Succeeded);
            Assert.Equal(GameStopOutcomeKind.GracefulTimeoutStillRunning, gracefulResult.Outcome);
            Assert.True(gracefulResult.StillRunning);
            Assert.True(SafeProcessInspector.IsProcessAlive(startedPid));

            ActiveGameSession? stillTracked = launchService.GetActiveSession(game.GameId);
            Assert.NotNull(stillTracked);
            Assert.NotEqual(PlaySessionActivityState.Stopping, stillTracked!.State);
            Assert.NotEqual(PlaySessionActivityState.Stopped, stillTracked.State);

            // 3. Explicit forceKill: true terminates the process tree and flushes the session ledger.
            GameStopResult forceResult = await launchService.StopGameAsync(game.GameId, forceKill: true);
            Assert.True(forceResult.Succeeded);
            Assert.Equal(GameStopOutcomeKind.ForceKilled, forceResult.Outcome);
            Assert.False(forceResult.StillRunning);
            Assert.False(SafeProcessInspector.IsProcessAlive(startedPid));
            Assert.Null(launchService.GetActiveSession(game.GameId));

            var sessions = await sandbox.Database.GetPlaySessionsByGameAsync(game.GameId);
            Assert.Single(sessions);
            Assert.NotNull(sessions[0].EndedUtc);
        }
        finally
        {
            starter.EnsureCleanedUp();
        }
    }

    [Fact]
    public async Task StopGameAsync_GracefulMode_SucceedsWhenProcessExitsWithinGraceWindow()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string fakeGameDir = sandbox.CreateSubDirectory("games", "GracefulExitGame");
        await File.WriteAllBytesAsync(Path.Combine(fakeGameDir, "game.exe"), [0x4D, 0x5A]);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Quick Graceful Exit Game",
            InstallDir = fakeGameDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await sandbox.Database.UpsertGameAsync(game);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);
        var starter = new ShortLivedHiddenProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            options: new GameLaunchOptions
            {
                ForceCreateNoWindow = true,
                PlaytimeSampleInterval = TimeSpan.FromMilliseconds(30),
                ShellBreakTriggerThreshold = TimeSpan.Zero,
                ShellBreakObservationWindow = TimeSpan.Zero,
                GracefulStopTimeout = TimeSpan.FromSeconds(5),
            });

        var launchRes = await launchService.LaunchGameAsync(game.GameId);
        Assert.True(launchRes.Succeeded, launchRes.ErrorMessage);

        GameStopResult stopRes = await launchService.StopGameAsync(
            game.GameId,
            forceKill: false,
            gracefulTimeout: TimeSpan.FromSeconds(5));
        Assert.True(stopRes.Succeeded);
        Assert.Equal(GameStopOutcomeKind.StoppedGracefully, stopRes.Outcome);
        Assert.False(stopRes.StillRunning);
        Assert.Null(launchService.GetActiveSession(game.GameId));

        // Stopping an already stopped game reports NotRunning cleanly.
        GameStopResult secondStop = await launchService.StopGameAsync(game.GameId, forceKill: false);
        Assert.False(secondStop.Succeeded);
        Assert.Equal(GameStopOutcomeKind.NotRunning, secondStop.Outcome);
    }

    [Fact]
    public async Task StopGameAsync_PreservesStoppingAcrossSampleTicks_RestoresOnCancel_AndIsolatesConcurrentForceKill()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string fakeGameDir = sandbox.CreateSubDirectory("games", "StoppingStateMachineGame");
        await File.WriteAllBytesAsync(Path.Combine(fakeGameDir, "game.exe"), [0x4D, 0x5A]);

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Stopping State Machine Game",
            InstallDir = fakeGameDir,
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await sandbox.Database.UpsertGameAsync(game);

        using var guard = new AntiCheatGuardService(
            residualProcessProbe: static () => [],
            highRiskEnvironmentProbe: static () => HighRiskEnvironmentReport.Clean);
        var starter = new LongRunningHiddenProcessStarter();

        await using var launchService = new GameLaunchService(
            sandbox.Database,
            guard,
            new InMemorySpawnEventBus(),
            activityProbe: new StaticUserActivityProbe(),
            processStarter: starter,
            options: new GameLaunchOptions
            {
                ForceCreateNoWindow = true,
                PlaytimeSampleInterval = TimeSpan.FromMilliseconds(40),
                ShellBreakTriggerThreshold = TimeSpan.Zero,
                ShellBreakObservationWindow = TimeSpan.Zero,
                GracefulStopTimeout = TimeSpan.FromMilliseconds(360),
                ForceKillWaitTimeout = TimeSpan.FromSeconds(5),
            });

        try
        {
            var launchRes = await launchService.LaunchGameAsync(game.GameId);
            Assert.True(launchRes.Succeeded, launchRes.ErrorMessage);
            int pid = starter.LastStartedProcess!.Id;

            // 1. During graceful stop spanning multiple 40ms sample ticks (with generous 5s timeout so CI scheduling
            // jitter cannot expire it early), State MUST remain Stopping and NEVER be overwritten by Active/Background/Afk.
            // Cancelling CancellationToken mid-graceful-stop then cleans up StopRequested and restores running state.
            var observedDuringStop = new List<PlaySessionActivityState>();
            using (var cancelCts = new CancellationTokenSource())
            {
                Task<GameStopResult> cancelableGracefulTask = launchService.StopGameAsync(
                    game.GameId,
                    forceKill: false,
                    gracefulTimeout: TimeSpan.FromSeconds(5),
                    cancellationToken: cancelCts.Token);

                // Wait until Stopping is entered before sampling across multiple 40ms monitor ticks.
                DateTime waitEnteredDeadline = DateTime.UtcNow.AddSeconds(2);
                while (launchService.GetActiveSession(game.GameId)?.State != PlaySessionActivityState.Stopping
                       && DateTime.UtcNow < waitEnteredDeadline)
                {
                    await Task.Delay(20);
                }

                for (int i = 0; i < 5; i++)
                {
                    var snap = launchService.GetActiveSession(game.GameId);
                    if (snap is not null)
                    {
                        observedDuringStop.Add(snap.State);
                    }

                    await Task.Delay(50);
                }

                Assert.False(cancelableGracefulTask.IsCompleted);
                Assert.NotEmpty(observedDuringStop);
                Assert.All(observedDuringStop, s => Assert.Equal(PlaySessionActivityState.Stopping, s));

                cancelCts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelableGracefulTask);
            }

            ActiveGameSession? afterCancel = launchService.GetActiveSession(game.GameId);
            Assert.NotNull(afterCancel);
            Assert.NotEqual(PlaySessionActivityState.Stopping, afterCancel!.State);
            Assert.True(SafeProcessInspector.IsProcessAlive(pid));

            // 2. Natural graceful timeout returns GracefulTimeoutStillRunning and restores non-Stopping state.
            GameStopResult timedOut = await launchService.StopGameAsync(
                game.GameId,
                forceKill: false,
                gracefulTimeout: TimeSpan.FromMilliseconds(220));
            Assert.False(timedOut.Succeeded);
            Assert.Equal(GameStopOutcomeKind.GracefulTimeoutStillRunning, timedOut.Outcome);
            Assert.NotEqual(PlaySessionActivityState.Stopping, launchService.GetActiveSession(game.GameId)!.State);
            Assert.True(SafeProcessInspector.IsProcessAlive(pid));

            // 3. Concurrent forceKill: true while a graceful stop is waiting cleanly supersedes without reverting state.
            Task<GameStopResult> longGracefulTask = launchService.StopGameAsync(
                game.GameId,
                forceKill: false,
                gracefulTimeout: TimeSpan.FromSeconds(5));

            DateTime waitForceDeadline = DateTime.UtcNow.AddSeconds(2);
            while (launchService.GetActiveSession(game.GameId)?.State != PlaySessionActivityState.Stopping
                   && DateTime.UtcNow < waitForceDeadline)
            {
                await Task.Delay(20);
            }

            GameStopResult forceRes = await launchService.StopGameAsync(game.GameId, forceKill: true);
            GameStopResult supersededGracefulRes = await longGracefulTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(forceRes.Succeeded);
            Assert.Equal(GameStopOutcomeKind.ForceKilled, forceRes.Outcome);
            Assert.True(supersededGracefulRes.Succeeded);
            Assert.False(SafeProcessInspector.IsProcessAlive(pid));
            Assert.Null(launchService.GetActiveSession(game.GameId));
        }
        finally
        {
            starter.EnsureCleanedUp();
        }
    }

    [Fact]
    public async Task ResolveImportTarget_LnkWithNonExistentBinaryLinkInfo_FallsBackToComIShellLink()
    {
        await using var sandbox = new TestSandbox();
        string gameDir = sandbox.CreateSubDirectory("games", "中文快捷方式回退测试");
        string targetExe = Path.Combine(gameDir, "RealComTarget.exe");
        await File.WriteAllBytesAsync(targetExe, new byte[8192]);

        string desktopDir = sandbox.CreateSubDirectory("desktop");
        string lnkPath = Path.Combine(desktopDir, "ComFallbackShortcut.lnk");

        // Build a real Windows .lnk via COM IShellLinkW (contains valid LinkTargetIDList + LinkInfo),
        // then mutate ONLY the LinkInfo path bytes to a non-existent path of identical length so binary LinkInfo
        // returns a missing path while Shell IDList stays intact -> forces COM IShellLinkW fallback!
        CreateRealWindowsShortcutViaCom(lnkPath, targetExe, gameDir, "--com-fallback-arg");

        byte[] freshBytes = await File.ReadAllBytesAsync(lnkPath);
        int linkInfoOffset = LocateLinkInfoSectionOffset(freshBytes);
        Assert.True(linkInfoOffset > 76, "Expected real Windows .lnk to contain LinkTargetIDList followed by LinkInfo.");

        byte[] mutated = (byte[])freshBytes.Clone();
        int mutatedInLinkInfo = ReplaceBytePatternFromOffset(
            mutated,
            linkInfoOffset,
            Encoding.Unicode.GetBytes("RealComTarget.exe"),
            Encoding.Unicode.GetBytes("MissingBinary.exe"));
        mutatedInLinkInfo += ReplaceBytePatternFromOffset(
            mutated,
            linkInfoOffset,
            Encoding.ASCII.GetBytes("RealComTarget.exe"),
            Encoding.ASCII.GetBytes("MissingBinary.exe"));
        Assert.True(mutatedInLinkInfo > 0, "Expected LinkInfo section to contain target filename.");
        await File.WriteAllBytesAsync(lnkPath, mutated);

        ResolvedImportTarget? resolved = GameDirectoryScanner.ResolveImportTargetDetailed(lnkPath);
        Assert.NotNull(resolved);
        Assert.True(resolved!.ResolvedFromShortcut);
        Assert.Equal(Path.TrimEndingDirectorySeparator(gameDir), resolved.InstallDir, ignoreCase: true);
        Assert.Equal("RealComTarget.exe", resolved.ExplicitExeRelPath, ignoreCase: true);
        Assert.Equal("--com-fallback-arg", resolved.LaunchArguments);
    }

    [Fact]
    public async Task ResolveImportTarget_WindowsLnkShortcut_ExtractsTargetExeLaunchArgumentsAndImportsGame()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "中文测试游戏 - Synthetic");
        Directory.CreateDirectory(Path.Combine(gameDir, "Engine"));
        string win64Dir = Directory.CreateDirectory(Path.Combine(gameDir, "ProjectX", "Binaries", "Win64")).FullName;
        string targetExe = Path.Combine(win64Dir, "ProjectX-Win64-Shipping.exe");
        await File.WriteAllBytesAsync(targetExe, new byte[8192]);
        await File.WriteAllTextAsync(Path.Combine(gameDir, "steam_appid.txt"), "504230\n", Encoding.UTF8);

        string desktopDir = sandbox.CreateSubDirectory("desktop");
        string lnkPath = Path.Combine(desktopDir, "ProjectX Desktop.lnk");
        WriteSyntheticShellLinkFile(
            lnkPath,
            targetPath: targetExe,
            workingDir: win64Dir,
            arguments: "--windowed -dx11 --skip-intro");

        // 1. Legacy 2-tuple ResolveImportTarget supports .lnk and infers Unreal root above ProjectX\Binaries\Win64.
        var basicResolved = GameDirectoryScanner.ResolveImportTarget(lnkPath);
        Assert.NotNull(basicResolved);
        Assert.Equal(Path.TrimEndingDirectorySeparator(gameDir), basicResolved!.Value.InstallDir, ignoreCase: true);
        Assert.Equal(Path.Combine("ProjectX", "Binaries", "Win64", "ProjectX-Win64-Shipping.exe"), basicResolved.Value.ExplicitExeRelPath);

        // 2. Detailed resolution also extracts shortcut command-line arguments and working directory.
        ResolvedImportTarget? detailed = GameDirectoryScanner.ResolveImportTargetDetailed(lnkPath);
        Assert.NotNull(detailed);
        Assert.True(detailed!.ResolvedFromShortcut);
        Assert.Equal("--windowed -dx11 --skip-intro", detailed.LaunchArguments);
        Assert.Equal(win64Dir, detailed.WorkingDirectory, ignoreCase: true);

        // 3. Relative-path-only .lnk (without LinkInfo) also resolves cleanly.
        string relLnkPath = Path.Combine(desktopDir, "RelativeShortcut.lnk");
        string relTarget = Path.GetRelativePath(desktopDir, targetExe);
        WriteSyntheticShellLinkFile(
            relLnkPath,
            targetPath: null,
            relativePath: relTarget,
            arguments: "-borderless");
        ResolvedImportTarget? relResolved = GameDirectoryScanner.ResolveImportTargetDetailed(relLnkPath);
        Assert.NotNull(relResolved);
        Assert.True(relResolved!.ResolvedFromShortcut);
        Assert.Equal(Path.TrimEndingDirectorySeparator(gameDir), relResolved.InstallDir, ignoreCase: true);
        Assert.Equal("-borderless", relResolved.LaunchArguments);

        // 4. Corrupt or non-exe .lnk returns null gracefully without throwing.
        string badLnk = Path.Combine(desktopDir, "Corrupt.lnk");
        await File.WriteAllBytesAsync(badLnk, [0x01, 0x02, 0x03, 0x04]);
        Assert.Null(GameDirectoryScanner.ResolveImportTarget(badLnk));

        string txtFile = Path.Combine(desktopDir, "readme.txt");
        await File.WriteAllTextAsync(txtFile, "not an exe");
        string txtLnk = Path.Combine(desktopDir, "Readme.lnk");
        WriteSyntheticShellLinkFile(txtLnk, targetPath: txtFile);
        Assert.Null(GameDirectoryScanner.ResolveImportTarget(txtLnk));

        // 5. Importing the .lnk via GameLibraryService persists shortcut LaunchArguments onto the imported GameRecord.
        using var guard = new AntiCheatGuardService(residualProcessProbe: static () => []);
        var scanner = new GameDirectoryScanner(
            guard,
            new GameArtworkExtractor(sandbox.Paths),
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, new InMemorySpawnEventBus());

        GameImportBatchResult batch = await library.ImportGameDetailedAsync(lnkPath);
        Assert.True(batch.Succeeded);
        Assert.True(batch.ResolvedFromShortcut);
        Assert.Equal(1, batch.AddedCount);
        Assert.NotNull(batch.PrimaryGame);
        Assert.Equal("--windowed -dx11 --skip-intro", batch.PrimaryGame!.LaunchArguments);
        Assert.Equal(504230, batch.PrimaryGame.SteamAppId);
    }

    [Fact]
    public async Task DiscoverLocalPlatformLibraryDirectories_ParsesModernAndLegacySteamVdfAndGogRoots_ReadOnly()
    {
        await using var sandbox = new TestSandbox();

        string steamMain = sandbox.CreateSubDirectory("platform", "Steam");
        string steamMainCommon = Directory.CreateDirectory(Path.Combine(steamMain, "steamapps", "common")).FullName;
        Directory.CreateDirectory(Path.Combine(steamMainCommon, "Hollow Knight"));
        Directory.CreateDirectory(Path.Combine(steamMainCommon, "Celeste"));

        string steamSecond = sandbox.CreateSubDirectory("platform", "SteamLibraryD");
        string steamSecondCommon = Directory.CreateDirectory(Path.Combine(steamSecond, "steamapps", "common")).FullName;
        Directory.CreateDirectory(Path.Combine(steamSecondCommon, "Hades"));

        string missingSteamLib = Path.Combine(sandbox.RootDirectory, "platform", "DisconnectedDriveSteam");

        string modernVdf = $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"      "{{steamMain.Replace(@"\", @"\\", StringComparison.Ordinal)}}"
                    "label"     ""
                    "apps"
                    {
                        "367520" "123456"
                    }
                }
                "1"
                {
                    "path"      "{{steamSecond.Replace(@"\", @"\\", StringComparison.Ordinal)}}"
                    "apps"
                    {
                        "1145360" "987654"
                    }
                }
                "2"
                {
                    "path"      "{{missingSteamLib.Replace(@"\", @"\\", StringComparison.Ordinal)}}"
                }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(steamMain, "steamapps", "libraryfolders.vdf"), modernVdf, Encoding.UTF8);

        // Verify both modern & legacy VDF text parsing.
        var parsedModern = GameDirectoryScanner.ParseSteamLibraryFoldersVdf(modernVdf);
        Assert.Equal(3, parsedModern.Count);
        Assert.Contains(steamMain, parsedModern, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(steamSecond, parsedModern, StringComparer.OrdinalIgnoreCase);

        const string legacyVdf = """
            "LibraryFolders"
            {
                "TimeNextStatsReport" "1590000000"
                "1" "D:\\LegacySteamLib"
                "2" "E:\\Games\\SteamTwo"
            }
            """;
        var parsedLegacy = GameDirectoryScanner.ParseSteamLibraryFoldersVdf(legacyVdf);
        Assert.Equal(2, parsedLegacy.Count);
        Assert.Contains(@"D:\LegacySteamLib", parsedLegacy, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"E:\Games\SteamTwo", parsedLegacy, StringComparer.OrdinalIgnoreCase);

        // Synthetic GOG root directory.
        string gogRoot = sandbox.CreateSubDirectory("platform", "GOG Games");
        Directory.CreateDirectory(Path.Combine(gogRoot, "LIMBO"));
        string missingGogRoot = Path.Combine(sandbox.RootDirectory, "platform", "MissingGogGames");

        var discovered = GameDirectoryScanner.DiscoverLocalPlatformLibraryDirectories(
            steamInstallDirHints: [steamMain],
            gogRootDirHints: [gogRoot, missingGogRoot],
            probeSystemDefaults: false);

        Assert.Equal(3, discovered.Count);
        var mainEntry = Assert.Single(discovered, d => string.Equals(d.Path, steamMainCommon, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PlatformSource.SteamLocal, mainEntry.PlatformSource);
        Assert.Equal(2, mainEntry.DetectedGameHintCount);

        var secondEntry = Assert.Single(discovered, d => string.Equals(d.Path, steamSecondCommon, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PlatformSource.SteamLocal, secondEntry.PlatformSource);
        Assert.Equal(1, secondEntry.DetectedGameHintCount);

        var gogEntry = Assert.Single(discovered, d => string.Equals(d.Path, gogRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PlatformSource.GogOffline, gogEntry.PlatformSource);
        Assert.Equal(1, gogEntry.DetectedGameHintCount);

        Assert.DoesNotContain(discovered, d => d.Path.Contains("DisconnectedDriveSteam", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(discovered, d => d.Path.Contains("MissingGogGames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanLibraryAndAnthologyImportDetailed_AccuratelyReportsAddedUpdatedInaccessibleAndAllExpandedGames()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string libRoot = sandbox.CreateSubDirectory("library-stats");

        // Single game A
        string singleA = Directory.CreateDirectory(Path.Combine(libRoot, "Alpha Standalone")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(singleA, "Alpha.exe"), new byte[12288]);
        await File.WriteAllTextAsync(Path.Combine(singleA, "steam_appid.txt"), "504230\n", Encoding.UTF8);

        // Multi-game anthology container with 2 independent Unity child games
        string anthologyDir = Directory.CreateDirectory(Path.Combine(libRoot, "Dual Collection Pack")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(anthologyDir, "Launcher.exe"), new byte[8192]);

        string childOne = Directory.CreateDirectory(Path.Combine(anthologyDir, "Chapter One")).FullName;
        Directory.CreateDirectory(Path.Combine(childOne, "ChapterOne_Data"));
        await File.WriteAllBytesAsync(Path.Combine(childOne, "ChapterOne.exe"), new byte[16384]);
        await File.WriteAllTextAsync(Path.Combine(childOne, "steam_appid.txt"), "910001\n", Encoding.UTF8);

        string childTwo = Directory.CreateDirectory(Path.Combine(anthologyDir, "Chapter Two")).FullName;
        Directory.CreateDirectory(Path.Combine(childTwo, "ChapterTwo_Data"));
        await File.WriteAllBytesAsync(Path.Combine(childTwo, "ChapterTwo.exe"), new byte[16384]);
        await File.WriteAllTextAsync(Path.Combine(childTwo, "steam_appid.txt"), "910002\n", Encoding.UTF8);

        using var guard = new AntiCheatGuardService(residualProcessProbe: static () => []);
        var scanner = new GameDirectoryScanner(
            guard,
            new GameArtworkExtractor(sandbox.Paths),
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, new InMemorySpawnEventBus());

        // 1. Anthology ImportGameDetailedAsync returns BOTH child games, not just the first!
        GameImportBatchResult anthologyImport = await library.ImportGameDetailedAsync(anthologyDir);
        Assert.True(anthologyImport.Succeeded);
        Assert.True(anthologyImport.ExpandedAnthology);
        Assert.Equal(2, anthologyImport.TotalCount);
        Assert.Equal(2, anthologyImport.AddedCount);
        Assert.Equal(0, anthologyImport.UpdatedCount);

        // Re-importing the same anthology reports 0 added, 2 updated.
        GameImportBatchResult anthologyReimport = await library.ImportGameDetailedAsync(anthologyDir);
        Assert.Equal(2, anthologyReimport.TotalCount);
        Assert.Equal(0, anthologyReimport.AddedCount);
        Assert.Equal(2, anthologyReimport.UpdatedCount);

        // 2. ScanLibraryDirectoryDetailedAsync distinguishes 1 genuinely new game (Alpha) vs 2 updated anthology games.
        LibraryScanReport firstScan = await library.ScanLibraryDirectoryDetailedAsync(libRoot);
        Assert.True(firstScan.RootAccessible);
        Assert.Equal(1, firstScan.AddedCount);
        Assert.Equal(2, firstScan.UpdatedCount);
        Assert.Equal(0, firstScan.InaccessibleCount);
        Assert.Single(firstScan.AddedGames, g => g.SteamAppId == 504230);

        // Second scan reports 0 added, 3 updated.
        LibraryScanReport secondScan = await library.ScanLibraryDirectoryDetailedAsync(libRoot);
        Assert.True(secondScan.RootAccessible);
        Assert.Equal(0, secondScan.AddedCount);
        Assert.Equal(3, secondScan.UpdatedCount);

        // 3. Scanning a missing / unreachable directory explicitly reports RootAccessible == false and InaccessibleDirectories.
        string unreachableDir = Path.Combine(sandbox.RootDirectory, "OfflineDrive_Missing");
        LibraryScanReport unreachableReport = await library.ScanLibraryDirectoryDetailedAsync(unreachableDir);
        Assert.False(unreachableReport.RootAccessible);
        Assert.Equal(1, unreachableReport.InaccessibleCount);
        Assert.Equal(0, unreachableReport.AddedCount);
        Assert.Equal(0, unreachableReport.UpdatedCount);
    }

    [Fact]
    public async Task UserPinnedTitle_SurvivesRescan_AndRelocateGameDirectory_PreservesGameIdAndAllHistory()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string origDir = sandbox.CreateSubDirectory("drive_d", "Celeste.v1.4-SteamRIP");
        await File.WriteAllBytesAsync(Path.Combine(origDir, "Celeste.exe"), new byte[12288]);
        await File.WriteAllTextAsync(Path.Combine(origDir, "steam_appid.txt"), "504230\n", Encoding.UTF8);

        using var guard = new AntiCheatGuardService(residualProcessProbe: static () => []);
        var scanner = new GameDirectoryScanner(
            guard,
            new GameArtworkExtractor(sandbox.Paths),
            options: new GameScanOptions { AllowOnlineMetadataLookup = false });
        var library = new GameLibraryService(sandbox.Database, sandbox.Paths, scanner, new InMemorySpawnEventBus());

        GameRecord? imported = await library.ImportGameAsync(origDir);
        Assert.NotNull(imported);
        string gameId = imported!.GameId;

        // Seed playtime sessions, achievement unlocks, and bound trainer history under gameId.
        var sessionStart = DateTimeOffset.UtcNow.AddHours(-2);
        await sandbox.Database.CompletePlaySessionAndAccumulateAsync(new PlaySessionRecord
        {
            SessionId = SpawnId.NewUlid(),
            GameId = gameId,
            StartedUtc = sessionStart,
            EndedUtc = sessionStart.AddMinutes(45),
            ActiveSec = 2400,
            BackgroundSec = 200,
            AfkSec = 100,
            ExitCode = 0,
        });
        await sandbox.Database.UpsertAchievementSchemaAsync(
            gameId,
            "Steam_504230",
            AchievementSchemaSource.LocalEmulatorSchema,
            "schinese",
            [new AchievementDefinitionDto { ApiName = "ACH_SUMMIT", DisplayName = "登顶", Description = "登上塞莱斯特山" }]);
        await sandbox.Database.UpsertAchievementUnlockAsync(new AchievementUnlockDto
        {
            GameId = gameId,
            ApiName = "ACH_SUMMIT",
            IsUnlocked = true,
            UnlockedUtc = sessionStart.AddMinutes(30),
            SourceProvider = "Test",
        });
        await sandbox.Database.UpsertTrainerAsync(new TrainerCatalogRecord
        {
            TrainerId = SpawnId.NewUlid(),
            GameId = gameId,
            Provider = TrainerProviderKind.LocalImport,
            DisplayName = "Celeste FLiNG",
            SandboxRelativePath = "celeste/trainer.exe",
            FileSha256 = new string('b', 64),
            FileSize = 4096,
            ImportedAtUtc = sessionStart,
        });

        // 1. Manual title edit pins user_pinned_title and survives RescanGameAsync!
        GameRecord? renamed = await library.UpdateGameMetadataAsync(gameId, new GameMetadataUpdate
        {
            Title = "蔚蓝 · 私人定制标题",
            LaunchArguments = "--fullscreen",
        });
        Assert.NotNull(renamed);
        Assert.Equal("蔚蓝 · 私人定制标题", renamed!.Title);

        GameRecord? afterRescan = await library.RescanGameAsync(gameId);
        Assert.NotNull(afterRescan);
        Assert.Equal("蔚蓝 · 私人定制标题", afterRescan!.Title);

        // 2. Relocate game directory to another drive/renamed folder -> preserves gameId + pinned title + all history!
        string relocatedDir = sandbox.CreateSubDirectory("drive_e", "Moved_Celeste_SSD");
        await File.WriteAllBytesAsync(Path.Combine(relocatedDir, "Celeste.exe"), new byte[12288]);
        await File.WriteAllTextAsync(Path.Combine(relocatedDir, "steam_appid.txt"), "504230\n", Encoding.UTF8);

        GameRecord? relocated = await library.RelocateGameDirectoryAsync(gameId, relocatedDir);
        Assert.NotNull(relocated);
        Assert.Equal(gameId, relocated!.GameId);
        Assert.Equal(Path.TrimEndingDirectorySeparator(relocatedDir), relocated.InstallDir, ignoreCase: true);
        Assert.Equal("Celeste.exe", relocated.ExecutableRelPath);
        Assert.Equal("蔚蓝 · 私人定制标题", relocated.Title);
        Assert.Equal("--fullscreen", relocated.LaunchArguments);
        Assert.Equal(2400, relocated.TotalActiveSec);
        Assert.Equal(200, relocated.TotalBackgroundSec);
        Assert.Equal(100, relocated.TotalAfkSec);

        Assert.Single(await sandbox.Database.GetPlaySessionsByGameAsync(gameId));
        Assert.Single(await sandbox.Database.GetAchievementUnlocksAsync(gameId), u => u.IsUnlocked);
        Assert.Single(await sandbox.Database.GetTrainersByGameIdAsync(gameId));

        // 3. Relocating onto another already-owned game directory is refused safely.
        string otherDir = sandbox.CreateSubDirectory("drive_e", "OtherOwnedGame");
        await File.WriteAllBytesAsync(Path.Combine(otherDir, "Other.exe"), new byte[8192]);
        GameRecord? otherGame = await library.ImportGameAsync(otherDir);
        Assert.NotNull(otherGame);
        Assert.Null(await library.RelocateGameDirectoryAsync(gameId, otherDir));

        // 4. Explicit PinTitle = false unpins title so rescan refreshes it from directory metadata.
        await library.UpdateGameMetadataAsync(gameId, new GameMetadataUpdate { PinTitle = false });
        GameRecord? unpinnedRescan = await library.RescanGameAsync(gameId);
        Assert.NotNull(unpinnedRescan);
        Assert.NotEqual("蔚蓝 · 私人定制标题", unpinnedRescan!.Title);
    }

    [Fact]
    public async Task GameDataManagement_MultiSnapshotTrash_ListsAllRestoresSpecificSnapshotAndPurgesSafely()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
        string installDir = sandbox.CreateSubDirectory("games", "TrashMultiSnapshotGame");
        await File.WriteAllBytesAsync(Path.Combine(installDir, "Game.exe"), new byte[4096]);
        string saveDir = Directory.CreateDirectory(Path.Combine(installDir, "saves")).FullName;
        string saveFile = Path.Combine(saveDir, "slot.sav");

        var game = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Trash Multi Snapshot Game",
            InstallDir = installDir,
            ExecutableRelPath = "Game.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        await sandbox.Database.UpsertGameAsync(game);

        var options = new GameDataManagementOptions
        {
            AllowOnlinePcGamingWikiLookup = false,
            AppDataRoamingOverride = sandbox.CreateSubDirectory("env", "Roaming"),
            LocalAppDataOverride = sandbox.CreateSubDirectory("env", "Local"),
            LocalLowOverride = sandbox.CreateSubDirectory("env", "LocalLow"),
            PublicDocumentsOverride = sandbox.CreateSubDirectory("env", "PublicDocs"),
            UserDocumentsOverride = sandbox.CreateSubDirectory("env", "UserDocs"),
            UserProfileOverride = sandbox.CreateSubDirectory("env", "UserProfile"),
            SavedGamesOverride = sandbox.CreateSubDirectory("env", "SavedGames"),
            ProgramDataOverride = sandbox.CreateSubDirectory("env", "ProgramData"),
            SteamInstallDirOverride = sandbox.CreateSubDirectory("env", "Steam"),
            HkcuSoftwarePrefix = $@"Software\SpawnTests_{Guid.NewGuid():N}",
        };

        var service = new GameDataManagementService(
            sandbox.Database,
            sandbox.Paths,
            new IdleLaunchServiceStub(),
            options: options,
            timeProvider: clock);

        // Snapshot #1 (v1 save)
        await File.WriteAllTextAsync(saveFile, "SAVE_VERSION_ONE_CONTENT", Encoding.UTF8);
        var entries1 = await service.InspectGameDataAsync(game.GameId);
        var clean1 = await service.CleanGameDataAsync(new GameDataCleanRequest
        {
            GameId = game.GameId,
            EntryIds = entries1.Where(e => e.Exists && e.IsSafeToClean).Select(e => e.EntryId).ToList(),
            ResetSpawnAchievementsAndPlaytime = false,
            CreateUndoBackup = true,
        });
        Assert.True(clean1.Succeeded, clean1.Message);
        Assert.NotNull(clean1.UndoSnapshot);

        // Snapshot #2 (v2 save, 15 minutes later)
        clock.Advance(TimeSpan.FromMinutes(15));
        Directory.CreateDirectory(saveDir);
        await File.WriteAllTextAsync(saveFile, "SAVE_VERSION_TWO_LARGER_PAYLOAD_DATA", Encoding.UTF8);
        var entries2 = await service.InspectGameDataAsync(game.GameId);
        var clean2 = await service.CleanGameDataAsync(new GameDataCleanRequest
        {
            GameId = game.GameId,
            EntryIds = entries2.Where(e => e.Exists && e.IsSafeToClean).Select(e => e.EntryId).ToList(),
            ResetSpawnAchievementsAndPlaytime = false,
            CreateUndoBackup = true,
        });
        Assert.True(clean2.Succeeded, clean2.Message);
        Assert.NotNull(clean2.UndoSnapshot);

        // 1. ListUndoSnapshotsAsync returns BOTH snapshots sorted newest-first with file counts & sizes.
        var snapshots = await service.ListUndoSnapshotsAsync(game.GameId);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(clean2.UndoSnapshot!.SnapshotId, snapshots[0].SnapshotId);
        Assert.Equal(clean1.UndoSnapshot!.SnapshotId, snapshots[1].SnapshotId);
        Assert.All(snapshots, s =>
        {
            Assert.True(s.BackedUpEntryCount >= 1);
            Assert.True(s.BackedUpFileCount >= 1);
            Assert.True(s.TotalBytes > 0);
            Assert.NotEmpty(s.BackedUpTargets);
        });

        // 2. Restore the OLDER specific snapshot (#1) -> restores v1 payload while leaving #2 untouched in trash!
        var restoreOld = await service.RestoreUndoSnapshotAsync(game.GameId, clean1.UndoSnapshot.SnapshotId);
        Assert.True(restoreOld.Succeeded, restoreOld.Message);
        Assert.Equal("SAVE_VERSION_ONE_CONTENT", await File.ReadAllTextAsync(saveFile, Encoding.UTF8));

        var afterRestoreOld = await service.ListUndoSnapshotsAsync(game.GameId);
        Assert.Single(afterRestoreOld);
        Assert.Equal(clean2.UndoSnapshot.SnapshotId, afterRestoreOld[0].SnapshotId);

        // 3. Path traversal identifiers are rejected by both restore and purge.
        Assert.False((await service.RestoreUndoSnapshotAsync(game.GameId, @"..\escape")).Succeeded);
        Assert.False((await service.PurgeTrashSnapshotsAsync(game.GameId, @"..\escape")).Succeeded);

        // 4. Create snapshot #3 so two exist, purge one specific snapshot, then purge all remaining.
        clock.Advance(TimeSpan.FromMinutes(15));
        var entries3 = await service.InspectGameDataAsync(game.GameId);
        var clean3 = await service.CleanGameDataAsync(new GameDataCleanRequest
        {
            GameId = game.GameId,
            EntryIds = entries3.Where(e => e.Exists && e.IsSafeToClean).Select(e => e.EntryId).ToList(),
            ResetSpawnAchievementsAndPlaytime = false,
            CreateUndoBackup = true,
        });
        Assert.True(clean3.Succeeded);
        Assert.Equal(2, (await service.ListUndoSnapshotsAsync(game.GameId)).Count);

        var purgeSingle = await service.PurgeTrashSnapshotsAsync(game.GameId, clean2.UndoSnapshot.SnapshotId);
        Assert.True(purgeSingle.Succeeded, purgeSingle.Message);
        Assert.Equal(1, purgeSingle.DeletedSnapshotCount);
        Assert.True(purgeSingle.FreedBytes > 0);
        Assert.Single(await service.ListUndoSnapshotsAsync(game.GameId));

        var purgeAll = await service.PurgeTrashSnapshotsAsync(game.GameId);
        Assert.True(purgeAll.Succeeded, purgeAll.Message);
        Assert.Equal(1, purgeAll.DeletedSnapshotCount);
        Assert.True(purgeAll.FreedBytes > 0);
        Assert.Empty(await service.ListUndoSnapshotsAsync(game.GameId));
    }

    [Fact]
    public async Task GameArtworkExtractor_TryExtractExeIconToPng_ExtractsHighResolutionNonEmptyIcon()
    {
        await using var sandbox = new TestSandbox();
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? probeExe = new[]
        {
            Path.Combine(windowsDir, "explorer.exe"),
            Path.Combine(windowsDir, "notepad.exe"),
            Path.Combine(windowsDir, "System32", "cmd.exe"),
        }.FirstOrDefault(File.Exists);

        Assert.False(string.IsNullOrWhiteSpace(probeExe), "Expected a standard Windows system executable for read-only icon extraction.");

        string artworkDir = sandbox.CreateSubDirectory("artwork-hd");
        string? iconPng = GameArtworkExtractor.TryExtractExeIconToPng(probeExe, artworkDir);
        Assert.False(string.IsNullOrWhiteSpace(iconPng));
        Assert.True(File.Exists(iconPng!));

        using SKBitmap? decoded = SKBitmap.Decode(iconPng);
        Assert.NotNull(decoded);
        Assert.True(decoded!.Width >= 48, $"Expected high-resolution shell icon width >= 48px (SHIL_EXTRALARGE/JUMBO), got {decoded.Width}px.");
        Assert.True(decoded.Height >= 48, $"Expected high-resolution shell icon height >= 48px, got {decoded.Height}px.");

        int opaquePixels = 0;
        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                if (decoded.GetPixel(x, y).Alpha > 16)
                {
                    opaquePixels++;
                }
            }
        }

        Assert.True(opaquePixels > 100, $"Expected > 100 non-transparent icon pixels, got {opaquePixels}.");
    }

    /// <summary>
    /// Writes a standards-compliant binary [MS-SHLLINK] <c>.lnk</c> file without invoking external shell tools.
    /// </summary>
    private static void WriteSyntheticShellLinkFile(
        string lnkPath,
        string? targetPath = null,
        string? relativePath = null,
        string? workingDir = null,
        string? arguments = null)
    {
        const uint FlagHasLinkInfo = 0x00000002;
        const uint FlagHasRelativePath = 0x00000008;
        const uint FlagHasWorkingDir = 0x00000010;
        const uint FlagHasArguments = 0x00000020;
        const uint FlagIsUnicode = 0x00000080;

        uint flags = FlagIsUnicode;
        if (!string.IsNullOrEmpty(targetPath)) flags |= FlagHasLinkInfo;
        if (!string.IsNullOrEmpty(relativePath)) flags |= FlagHasRelativePath;
        if (!string.IsNullOrEmpty(workingDir)) flags |= FlagHasWorkingDir;
        if (!string.IsNullOrEmpty(arguments)) flags |= FlagHasArguments;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 76-byte ShellLinkHeader
        bw.Write(0x0000004Cu); // HeaderSize
        bw.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray()); // CLSID_ShellLink
        bw.Write(flags);
        bw.Write(new byte[52]); // FileAttributes, Timestamps, FileSize, IconIndex, ShowCommand, HotKey, Reserved

        // LinkInfo with Unicode LocalBasePathOffsetUnicode (header size 0x24)
        if (!string.IsNullOrEmpty(targetPath))
        {
            byte[] ansiPath = Encoding.ASCII.GetBytes(targetPath + "\0");
            byte[] unicodePath = Encoding.Unicode.GetBytes(targetPath + "\0");
            byte[] ansiEmpty = [0];
            byte[] unicodeEmpty = [0, 0];

            const int headerSize = 0x24;
            const int volumeIdSize = 16;
            int volumeIdOffset = headerSize;
            int localBaseAnsiOffset = volumeIdOffset + volumeIdSize;
            int commonSuffixAnsiOffset = localBaseAnsiOffset + ansiPath.Length;
            int localBaseUnicodeOffset = commonSuffixAnsiOffset + ansiEmpty.Length;
            int commonSuffixUnicodeOffset = localBaseUnicodeOffset + unicodePath.Length;
            int totalLinkInfoSize = commonSuffixUnicodeOffset + unicodeEmpty.Length;

            bw.Write(totalLinkInfoSize);
            bw.Write(headerSize);
            bw.Write(0x00000001u); // VolumeIDAndLocalBasePath
            bw.Write(volumeIdOffset);
            bw.Write(localBaseAnsiOffset);
            bw.Write(0); // CommonNetworkRelativeLinkOffset
            bw.Write(commonSuffixAnsiOffset);
            bw.Write(localBaseUnicodeOffset);
            bw.Write(commonSuffixUnicodeOffset);

            // Minimal VolumeID (16 bytes)
            bw.Write(volumeIdSize);
            bw.Write(3u); // DRIVE_FIXED
            bw.Write(0u);
            bw.Write(16u);

            bw.Write(ansiPath);
            bw.Write(ansiEmpty);
            bw.Write(unicodePath);
            bw.Write(unicodeEmpty);
        }

        static void WriteCountedUnicode(BinaryWriter writer, string value)
        {
            writer.Write((ushort)value.Length);
            writer.Write(Encoding.Unicode.GetBytes(value));
        }

        if (!string.IsNullOrEmpty(relativePath)) WriteCountedUnicode(bw, relativePath);
        if (!string.IsNullOrEmpty(workingDir)) WriteCountedUnicode(bw, workingDir);
        if (!string.IsNullOrEmpty(arguments)) WriteCountedUnicode(bw, arguments);

        // Terminal ExtraData zero block
        bw.Write(0u);
        bw.Flush();
        File.WriteAllBytes(lnkPath, ms.ToArray());
    }

    private sealed class StaticUserActivityProbe : IUserActivityProbe
    {
        public ForegroundWindowSnapshot GetForegroundWindow() =>
            new(new nint(0x5555), ProcessId: 0, WindowClassName: "ConsoleWindowClass");
        public double GetKeyboardMouseIdleSeconds() => 0.0;
        public double GetGamepadIdleSeconds() => 9999.0;
    }

    private sealed class LongRunningHiddenProcessStarter : IGameProcessStarter
    {
        public Process? LastStartedProcess { get; private set; }

        public Process StartDirectProcess(GameProcessStartRequest request)
        {
            string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var psi = new ProcessStartInfo
            {
                FileName = comSpec,
                Arguments = "/c \"ping -n 30 127.0.0.1 >nul\"",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            LastStartedProcess = Process.Start(psi)!;
            return LastStartedProcess;
        }

        public Task StartExplorerDecoupledAsync(GameProcessStartRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void EnsureCleanedUp()
        {
            try
            {
                if (LastStartedProcess is { HasExited: false })
                {
                    LastStartedProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore cleanup race.
            }
        }
    }

    private sealed class ShortLivedHiddenProcessStarter : IGameProcessStarter
    {
        public Process StartDirectProcess(GameProcessStartRequest request)
        {
            string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var psi = new ProcessStartInfo
            {
                FileName = comSpec,
                Arguments = "/c \"ping -n 2 127.0.0.1 >nul\"",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            return Process.Start(psi)!;
        }

        public Task StartExplorerDecoupledAsync(GameProcessStartRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class IdleLaunchServiceStub : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } =
            new Dictionary<string, ActiveGameSession>();

        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
        {
            add { }
            remove { }
        }

        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameLaunchResult.Failure("STUB", "stub"));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }

    private static void CreateRealWindowsShortcutViaCom(string lnkPath, string targetPath, string workingDir, string arguments)
    {
        object? comObj = null;
        try
        {
            Type shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
            comObj = Activator.CreateInstance(shellLinkType)!;
            var shellLink = (ITestShellLinkW)comObj;
            shellLink.SetPath(targetPath);
            shellLink.SetWorkingDirectory(workingDir);
            shellLink.SetArguments(arguments);
            ((ITestPersistFile)comObj).Save(lnkPath, fRemember: true);
        }
        finally
        {
            if (comObj is not null && Marshal.IsComObject(comObj))
            {
                Marshal.FinalReleaseComObject(comObj);
            }
        }
    }

    private static int LocateLinkInfoSectionOffset(byte[] data)
    {
        if (data.Length < 78)
        {
            return -1;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4));
        int pos = 76;
        if ((flags & 0x01) != 0)
        {
            ushort idListSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2 + idListSize;
        }

        return (flags & 0x02) != 0 && pos < data.Length ? pos : -1;
    }

    private static int ReplaceBytePatternFromOffset(byte[] buffer, int startOffset, byte[] search, byte[] replacement)
    {
        int count = 0;
        for (int i = startOffset; i <= buffer.Length - search.Length; i++)
        {
            if (buffer.AsSpan(i, search.Length).SequenceEqual(search))
            {
                replacement.CopyTo(buffer.AsSpan(i, replacement.Length));
                count++;
                i += search.Length - 1;
            }
        }

        return count;
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITestShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITestPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
