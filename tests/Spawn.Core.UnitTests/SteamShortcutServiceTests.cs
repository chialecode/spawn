using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class SteamShortcutServiceTests
{
    private const string HkcuSteamSubKey = @"Software\Valve\Steam";
    private const string HkcuActiveProcessSubKey = @"Software\Valve\Steam\ActiveProcess";
    private const uint DefaultAccountId = 778899u;

    private sealed class FakeSteamProcessController : ISteamProcessController
    {
        public Func<string, Task<bool>>? OnRequestGracefulShutdown { get; set; }
        public Func<string, Task<bool>>? OnStartSteam { get; set; }

        public int ShutdownCallCount { get; private set; }
        public int StartCallCount { get; private set; }
        public List<string> CallLog { get; } = [];

        public Task<bool> RequestGracefulShutdownAsync(string steamExePath, CancellationToken cancellationToken = default)
        {
            ShutdownCallCount++;
            CallLog.Add($"shutdown:{steamExePath}");
            return OnRequestGracefulShutdown is not null
                ? OnRequestGracefulShutdown(steamExePath)
                : Task.FromResult(true);
        }

        public Task<bool> StartSteamAsync(string steamExePath, CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            CallLog.Add($"start:{steamExePath}");
            return OnStartSteam is not null
                ? OnStartSteam(steamExePath)
                : Task.FromResult(true);
        }
    }

    [Fact]
    public async Task OfficialSteamLocal_ReturnsReadyOfficialSteam_WithZeroWritesAndZeroRestarts()
    {
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, string steamDir, string shortcutsPath) = CreateSandboxSteam(sandbox, isRunning: true);
        var processController = new FakeSteamProcessController();
        var service = CreateService(probe, processController);

        string gameDir = sandbox.CreateSubDirectory("games", "OfficialGame");
        var officialGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Official Protected Steam Title",
            InstallDir = gameDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.SteamLocal,
            SteamAppId = 1145360,
            AntiCheatTier = AntiCheatTier.Tier3, // Official Steam Tier 3 still launches via official URI
        };
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        SteamLaunchPreparationResult prep = await service.PrepareLaunchAsync(officialGame, settings);
        Assert.Equal(SteamLaunchPreparationStatus.ReadyOfficialSteam, prep.Status);
        Assert.Equal(SteamLaunchTargetKind.OfficialSteamApp, prep.TargetKind);
        Assert.Equal("steam://rungameid/1145360", prep.LaunchUri);
        Assert.False(prep.IsBlocked);
        Assert.Empty(prep.Warnings);

        SteamShortcutSyncResult sync = await service.ApplyShortcutAndRestartIfNeededAsync(
            officialGame,
            settings,
            allowGracefulSteamRestart: true);
        Assert.True(sync.Succeeded);
        Assert.False(sync.RestartedSteam);
        Assert.Equal("steam://rungameid/1145360", sync.LaunchUri);
        Assert.False(File.Exists(shortcutsPath));
        Assert.Equal(0, processController.ShutdownCallCount);
        Assert.Equal(0, processController.StartCallCount);
        Assert.True(Directory.Exists(steamDir));
    }

    [Theory]
    [InlineData(AntiCheatTier.Tier2)]
    [InlineData(AntiCheatTier.Tier3)]
    public async Task NonSteam_Tier2AndTier3_AreBlockedWithZeroFileWrites(AntiCheatTier tier)
    {
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, _, string shortcutsPath) = CreateSandboxSteam(sandbox, isRunning: false);
        var processController = new FakeSteamProcessController();
        var service = CreateService(probe, processController);

        GameRecord game = CreateSandboxNonSteamGame(sandbox, $"Protected_{tier}", tier);
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        SteamLaunchPreparationResult prep = await service.PrepareLaunchAsync(game, settings);
        Assert.Equal(SteamLaunchPreparationStatus.BlockedTierAntiCheat, prep.Status);
        Assert.True(prep.IsBlocked);
        Assert.False(string.IsNullOrWhiteSpace(prep.ErrorMessage));

        SteamShortcutSyncResult sync = await service.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: true);
        Assert.False(sync.Succeeded);
        Assert.False(File.Exists(shortcutsPath));
        Assert.Equal(0, processController.ShutdownCallCount);
        Assert.Equal(0, processController.StartCallCount);
    }

    [Fact]
    public async Task NonSteam_Tier0And1_BlockedConditions_InvalidExecutable_SteamMissing_NoAccount()
    {
        await using var sandbox = new TestSandbox();
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        // 1. Steam not found
        var noSteamProbe = new FakeSteamEnvironmentProbe(sandbox.RootDirectory);
        var noSteamService = CreateService(noSteamProbe, new FakeSteamProcessController());
        GameRecord validGame = CreateSandboxNonSteamGame(sandbox, "ValidIndie", AntiCheatTier.Tier0);

        SteamLaunchPreparationResult missingSteam = await noSteamService.PrepareLaunchAsync(validGame, settings);
        Assert.Equal(SteamLaunchPreparationStatus.BlockedSteamNotFound, missingSteam.Status);
        Assert.True(missingSteam.IsBlocked);

        // 2. Missing executable
        (FakeSteamEnvironmentProbe validProbe, _, _) = CreateSandboxSteam(sandbox, isRunning: false);
        var service = CreateService(validProbe, new FakeSteamProcessController());
        var missingExeGame = new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = "Missing Exe",
            InstallDir = sandbox.CreateSubDirectory("games", "MissingExeDir"),
            ExecutableRelPath = "missing.exe",
            PlatformSource = PlatformSource.Standalone,
            AntiCheatTier = AntiCheatTier.Tier1,
        };

        SteamLaunchPreparationResult invalidExe = await service.PrepareLaunchAsync(missingExeGame, settings);
        Assert.Equal(SteamLaunchPreparationStatus.BlockedInvalidExecutable, invalidExe.Status);
        Assert.True(invalidExe.IsBlocked);

        // 3. Steam present but zero accounts (userdata has only "0" and no loginusers.vdf)
        string accountlessSteamDir = sandbox.CreateSubDirectory("steam-no-accounts");
        File.WriteAllBytes(Path.Combine(accountlessSteamDir, "steam.exe"), [0x4D, 0x5A]);
        Directory.CreateDirectory(Path.Combine(accountlessSteamDir, "userdata", "0"));

        var noAccountProbe = new FakeSteamEnvironmentProbe(sandbox.RootDirectory)
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", accountlessSteamDir);
        var noAccountService = CreateService(noAccountProbe, new FakeSteamProcessController());

        SteamLaunchPreparationResult noAccount = await noAccountService.PrepareLaunchAsync(validGame, settings);
        Assert.Equal(SteamLaunchPreparationStatus.BlockedNoSteamAccount, noAccount.Status);
        Assert.True(noAccount.IsBlocked);
    }

    [Fact]
    public async Task NonSteam_Tier0_AlreadySyncedInShortcutsVdf_ReturnsReadyWithZeroRestart()
    {
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, _, string shortcutsPath) = CreateSandboxSteam(sandbox, isRunning: true);
        var processController = new FakeSteamProcessController();
        var service = CreateService(probe, processController);

        GameRecord game = CreateSandboxNonSteamGame(sandbox, "SyncedGame", AntiCheatTier.Tier0, steamAppId: 48000);
        var settings = new GameSteamLaunchSettings
        {
            LaunchMode = GameLaunchMode.Steam,
            UseSteamAppIdAsShortcutName = true,
        };

        // Pre-populate matching shortcuts.vdf
        string exeFull = Path.GetFullPath(game.ExecutableFullPath);
        string startDir = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(exeFull)!);
        var doc = SteamShortcutsVdfSerializer.CreateEmptyShortcutsDocument();
        (int preAppId, _) = SteamShortcutsVdfSerializer.UpsertShortcutEntry(
            doc,
            new SteamShortcutDesiredSpec(
                AppName: "48000",
                NormalizedExeFullPath: exeFull,
                QuotedExe: SteamShortcutsVdfSerializer.QuoteShortcutPath(exeFull),
                NormalizedStartDir: startDir,
                QuotedStartDir: SteamShortcutsVdfSerializer.QuoteShortcutPath(startDir),
                IconPath: exeFull,
                LaunchOptions: string.Empty));
        SteamShortcutsVdfSerializer.WriteFileAtomically(shortcutsPath, doc);
        byte[] beforeBytes = await File.ReadAllBytesAsync(shortcutsPath);

        SteamLaunchPreparationResult prep = await service.PrepareLaunchAsync(game, settings);
        Assert.Equal(SteamLaunchPreparationStatus.ReadyNonSteamShortcut, prep.Status);
        Assert.Equal(preAppId, prep.ShortcutAppId);
        Assert.Equal("48000", prep.ShortcutAppName);
        Assert.Equal(SteamShortcutsVdfSerializer.BuildRunGameIdUri(preAppId), prep.LaunchUri);

        SteamShortcutSyncResult sync = await service.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: false);
        Assert.True(sync.Succeeded);
        Assert.False(sync.RestartedSteam);
        Assert.Equal(preAppId, sync.ShortcutAppId);
        Assert.Equal(0, processController.ShutdownCallCount);
        Assert.Equal(0, processController.StartCallCount);
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(shortcutsPath));
    }

    [Fact]
    public async Task NonSteam_UnsyncedAndSteamClosed_WritesShortcutsCleanlyWithoutRestartingSteam()
    {
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, _, string shortcutsPath) = CreateSandboxSteam(sandbox, isRunning: false);
        var processController = new FakeSteamProcessController();
        var service = CreateService(probe, processController);

        GameRecord game = CreateSandboxNonSteamGame(sandbox, "ClosedSteamGame", AntiCheatTier.Tier1, launchArguments: "-novid");
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        // Read-only prepare must NOT create shortcuts.vdf
        SteamLaunchPreparationResult prep = await service.PrepareLaunchAsync(game, settings);
        Assert.Equal(SteamLaunchPreparationStatus.NeedsShortcutWriteSteamClosed, prep.Status);
        Assert.False(File.Exists(shortcutsPath));

        SteamShortcutSyncResult sync = await service.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: false);
        Assert.True(sync.Succeeded);
        Assert.False(sync.RestartedSteam);
        Assert.NotNull(sync.ShortcutAppId);
        Assert.Equal(SteamShortcutsVdfSerializer.BuildRunGameIdUri(sync.ShortcutAppId!.Value), sync.LaunchUri);
        Assert.True(File.Exists(shortcutsPath));
        Assert.Equal(0, processController.ShutdownCallCount);
        Assert.Equal(0, processController.StartCallCount);

        // Subsequent prepare now sees entry as up to date
        SteamLaunchPreparationResult afterPrep = await service.PrepareLaunchAsync(
            game,
            settings with { RegisteredShortcutAppId = sync.ShortcutAppId });
        Assert.Equal(SteamLaunchPreparationStatus.ReadyNonSteamShortcut, afterPrep.Status);
        Assert.Equal(sync.ShortcutAppId, afterPrep.ShortcutAppId);
    }

    [Fact]
    public async Task NonSteam_UnsyncedAndSteamRunning_EnforcesConfirmation_GracefulRestartSequence_AndShutdownTimeoutSafety()
    {
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, _, string shortcutsPath) = CreateSandboxSteam(sandbox, isRunning: true);

        // Seed an existing shortcuts.vdf so we can verify preservation on cancel/timeout and backup on success
        BinaryVdfMap seedDoc = SteamShortcutsVdfSerializer.CreateEmptyShortcutsDocument();
        SteamShortcutsVdfSerializer.UpsertShortcutEntry(
            seedDoc,
            new SteamShortcutDesiredSpec(
                AppName: "Existing User Shortcut",
                NormalizedExeFullPath: @"D:\Other\existing.exe",
                QuotedExe: "\"D:\\Other\\existing.exe\"",
                NormalizedStartDir: @"D:\Other",
                QuotedStartDir: "\"D:\\Other\"",
                IconPath: @"D:\Other\existing.exe",
                LaunchOptions: string.Empty));
        SteamShortcutsVdfSerializer.WriteFileAtomically(shortcutsPath, seedDoc);
        byte[] originalSeedBytes = await File.ReadAllBytesAsync(shortcutsPath);

        GameRecord game = CreateSandboxNonSteamGame(sandbox, "RunningSteamGame", AntiCheatTier.Tier0);
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        // 1. allowGracefulSteamRestart: false -> aborts cleanly, zero Steam or file changes
        var deniedController = new FakeSteamProcessController();
        var deniedClock = new ManualTimeProvider();
        var deniedService = CreateService(probe, deniedController, deniedClock);

        SteamLaunchPreparationResult prep = await deniedService.PrepareLaunchAsync(game, settings);
        Assert.Equal(SteamLaunchPreparationStatus.NeedsSteamRestartToSync, prep.Status);

        SteamShortcutSyncResult deniedResult = await deniedService.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: false);
        Assert.False(deniedResult.Succeeded);
        Assert.False(deniedResult.RestartedSteam);
        Assert.Equal(0, deniedController.ShutdownCallCount);
        Assert.Equal(0, deniedController.StartCallCount);
        Assert.Equal(originalSeedBytes, await File.ReadAllBytesAsync(shortcutsPath));

        // 2. Shutdown timeout -> Steam stays running past ShutdownTimeout -> aborts cleanly without corrupting shortcuts.vdf
        var timeoutController = new FakeSteamProcessController();
        var timeoutClock = new ManualTimeProvider();
        var timeoutOptions = new SteamShortcutServiceOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(300),
            StartupLoginTimeout = TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(100),
        };
        var timeoutService = CreateService(probe, timeoutController, timeoutClock, timeoutOptions);

        Task<SteamShortcutSyncResult> timeoutTask = timeoutService.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: true);

        await DriveManualClockUntilCompletedAsync(timeoutClock, timeoutTask, TimeSpan.FromMilliseconds(100), maxSteps: 10);
        SteamShortcutSyncResult timeoutResult = await timeoutTask;

        Assert.False(timeoutResult.Succeeded);
        Assert.Equal(1, timeoutController.ShutdownCallCount);
        Assert.Equal(0, timeoutController.StartCallCount);
        Assert.Equal(originalSeedBytes, await File.ReadAllBytesAsync(shortcutsPath));
        Assert.False(File.Exists(shortcutsPath + ".spawn.bak"));

        // 3. Full graceful restart happy path:
        //    shutdown -> wait for exit -> write shortcuts.vdf -> start Steam -> wait for ActiveUser != 0
        var restartClock = new ManualTimeProvider();
        var restartOptions = new SteamShortcutServiceOptions
        {
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StartupLoginTimeout = TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(100),
        };

        int shutdownTicksRemaining = 2;
        int loginTicksRemaining = 2;
        bool wroteFileWhileSteamWasClosed = false;

        var restartController = new FakeSteamProcessController
        {
            OnRequestGracefulShutdown = _ => Task.FromResult(true),
            OnStartSteam = _ =>
            {
                // By the time StartSteam is called, shortcuts.vdf must already have been written while Steam was closed!
                wroteFileWhileSteamWasClosed = !probe.SteamRunning && File.Exists(shortcutsPath + ".spawn.bak");
                probe.SteamRunning = true;
                probe.SetRegistryDword(RegistryHive.CurrentUser, RegistryView.Default, HkcuActiveProcessSubKey, "ActiveUser", 0);
                return Task.FromResult(true);
            },
        };

        var restartService = CreateService(probe, restartController, restartClock, restartOptions);
        Task<SteamShortcutSyncResult> restartTask = restartService.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: true);

        await DriveManualClockUntilCompletedAsync(
            restartClock,
            restartTask,
            TimeSpan.FromMilliseconds(100),
            maxSteps: 20,
            onTick: () =>
            {
                if (restartController.ShutdownCallCount == 1 && restartController.StartCallCount == 0)
                {
                    shutdownTicksRemaining--;
                    if (shutdownTicksRemaining <= 0)
                    {
                        probe.SteamRunning = false;
                    }
                }
                else if (restartController.StartCallCount == 1)
                {
                    loginTicksRemaining--;
                    if (loginTicksRemaining <= 0)
                    {
                        probe.SetRegistryDword(
                            RegistryHive.CurrentUser,
                            RegistryView.Default,
                            HkcuActiveProcessSubKey,
                            "ActiveUser",
                            unchecked((int)DefaultAccountId));
                    }
                }
            });

        SteamShortcutSyncResult restartResult = await restartTask;
        Assert.True(restartResult.Succeeded);
        Assert.True(restartResult.RestartedSteam);
        Assert.True(wroteFileWhileSteamWasClosed);
        Assert.Equal(1, restartController.ShutdownCallCount);
        Assert.Equal(1, restartController.StartCallCount);
        Assert.NotNull(restartResult.ShortcutAppId);
        Assert.Equal(SteamShortcutsVdfSerializer.BuildRunGameIdUri(restartResult.ShortcutAppId!.Value), restartResult.LaunchUri);

        // Verify .spawn.bak preserved original seed AND shortcuts.vdf has both the existing user shortcut and the new entry
        Assert.Equal(originalSeedBytes, await File.ReadAllBytesAsync(shortcutsPath + ".spawn.bak"));
        BinaryVdfMap updatedDoc = SteamShortcutsVdfSerializer.ReadFile(shortcutsPath);
        Assert.Equal(2, updatedDoc.GetMap("shortcuts")!.Fields.Count);
    }

    [Fact]
    public async Task NonSteam_GracefulRestart_HandlesShutdownDispatchFailureAndStartupLoginTimeoutCleanly()
    {
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, _, string shortcutsPath) = CreateSandboxSteam(sandbox, isRunning: true);
        GameRecord game = CreateSandboxNonSteamGame(sandbox, "RestartEdgeCaseGame", AntiCheatTier.Tier0);
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        // 1. Shutdown dispatch returns false -> aborts immediately without writing shortcuts.vdf or starting Steam
        var failedShutdownController = new FakeSteamProcessController
        {
            OnRequestGracefulShutdown = _ => Task.FromResult(false),
        };
        var failedShutdownService = CreateService(probe, failedShutdownController, new ManualTimeProvider());
        SteamShortcutSyncResult dispatchFailed = await failedShutdownService.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: true);

        Assert.False(dispatchFailed.Succeeded);
        Assert.Equal(1, failedShutdownController.ShutdownCallCount);
        Assert.Equal(0, failedShutdownController.StartCallCount);
        Assert.False(File.Exists(shortcutsPath));

        // 2. Steam exits & writes shortcuts.vdf & restarts, but ActiveUser stays 0 until StartupLoginTimeout expires
        var loginTimeoutClock = new ManualTimeProvider();
        var loginTimeoutOptions = new SteamShortcutServiceOptions
        {
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            StartupLoginTimeout = TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(100),
        };
        var loginTimeoutController = new FakeSteamProcessController
        {
            OnRequestGracefulShutdown = _ =>
            {
                probe.SteamRunning = false;
                return Task.FromResult(true);
            },
            OnStartSteam = _ =>
            {
                probe.SteamRunning = true;
                probe.SetRegistryDword(RegistryHive.CurrentUser, RegistryView.Default, HkcuActiveProcessSubKey, "ActiveUser", 0);
                return Task.FromResult(true);
            },
        };

        var loginTimeoutService = CreateService(probe, loginTimeoutController, loginTimeoutClock, loginTimeoutOptions);
        Task<SteamShortcutSyncResult> loginTimeoutTask = loginTimeoutService.ApplyShortcutAndRestartIfNeededAsync(
            game,
            settings,
            allowGracefulSteamRestart: true);

        await DriveManualClockUntilCompletedAsync(loginTimeoutClock, loginTimeoutTask, TimeSpan.FromMilliseconds(100), maxSteps: 10);
        SteamShortcutSyncResult loginTimeoutResult = await loginTimeoutTask;

        Assert.False(loginTimeoutResult.Succeeded);
        Assert.True(loginTimeoutResult.RestartedSteam);
        Assert.NotNull(loginTimeoutResult.ShortcutAppId);
        Assert.False(string.IsNullOrWhiteSpace(loginTimeoutResult.LaunchUri));
        Assert.True(File.Exists(shortcutsPath));
    }

    [Fact]
    public async Task PrepareLaunch_DetectsOfflineEmulatorWarnings_GbeFork_OnlineFix_AndGeneric()
    {
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, _, _) = CreateSandboxSteam(sandbox, isRunning: false);
        var service = CreateService(probe, new FakeSteamProcessController());
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        // 1. gbe_fork via steam_settings directory
        GameRecord gbeGame = CreateSandboxNonSteamGame(sandbox, "GbeGame", AntiCheatTier.Tier0);
        Directory.CreateDirectory(Path.Combine(gbeGame.InstallDir, "steam_settings"));
        await File.WriteAllTextAsync(Path.Combine(gbeGame.InstallDir, "steam_settings", "configs.user.ini"), "[user::general]");

        SteamLaunchPreparationResult gbePrep = await service.PrepareLaunchAsync(gbeGame, settings);
        Assert.Contains(gbePrep.Warnings, w => w.Kind == SteamLaunchWarningKind.OfflineEmulatorGbeFork);
        Assert.DoesNotContain(gbePrep.Warnings, w => w.Kind == SteamLaunchWarningKind.OfflineEmulatorGeneric);

        // 2. OnlineFix.ini
        GameRecord onlineFixGame = CreateSandboxNonSteamGame(sandbox, "OnlineFixGame", AntiCheatTier.Tier0);
        await File.WriteAllTextAsync(Path.Combine(onlineFixGame.InstallDir, "OnlineFix.ini"), "[Main]\nRealAppId=12345");

        SteamLaunchPreparationResult onlineFixPrep = await service.PrepareLaunchAsync(onlineFixGame, settings);
        Assert.Contains(onlineFixPrep.Warnings, w => w.Kind == SteamLaunchWarningKind.OfflineEmulatorOnlineFix);

        // 3. Generic offline emulators (.cdx, .rne, steam_emu.ini)
        foreach (string indicator in new[] { "steam_api64.cdx", "steam_api64.rne", "steam_emu.ini" })
        {
            GameRecord genericGame = CreateSandboxNonSteamGame(sandbox, $"Generic_{indicator.Replace('.', '_')}", AntiCheatTier.Tier0);
            await File.WriteAllTextAsync(Path.Combine(genericGame.InstallDir, indicator), "AppId=48000");

            SteamLaunchPreparationResult genericPrep = await service.PrepareLaunchAsync(genericGame, settings);
            Assert.Contains(genericPrep.Warnings, w => w.Kind == SteamLaunchWarningKind.OfflineEmulatorGeneric);
        }
    }

    [Fact]
    public async Task PeElevationInspector_AndPrepareLaunch_DetectEmbeddedRtManifestElevationAndStayResilientToInvalidStreams()
    {
        const string requireAdminXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security>
                  <requestedPrivileges>
                    <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
                  </requestedPrivileges>
                </security>
              </trustInfo>
            </assembly>
            """;

        const string highestAvailableXml = """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security>
                  <requestedPrivileges>
                    <requestedExecutionLevel level="highestAvailable" uiAccess="false"/>
                  </requestedPrivileges>
                </security>
              </trustInfo>
            </assembly>
            """;

        const string asInvokerXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security>
                  <requestedPrivileges>
                    <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
                  </requestedPrivileges>
                </security>
              </trustInfo>
            </assembly>
            """;

        byte[] adminPeBytes = BuildSyntheticPeWithRtManifest(Encoding.UTF8.GetBytes(requireAdminXml));
        byte[] highestPeBytes = BuildSyntheticPeWithRtManifest(Encoding.UTF8.GetBytes(highestAvailableXml));
        byte[] invokerPeBytes = BuildSyntheticPeWithRtManifest(Encoding.UTF8.GetBytes(asInvokerXml));
        byte[] utf16AdminPeBytes = BuildSyntheticPeWithRtManifest([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(requireAdminXml)]);

        using (var ms = new MemoryStream(adminPeBytes))
        {
            Assert.True(PeElevationInspector.RequiresElevation(ms));
        }

        using (var ms = new MemoryStream(highestPeBytes))
        {
            Assert.True(PeElevationInspector.RequiresElevation(ms));
        }

        using (var ms = new MemoryStream(utf16AdminPeBytes))
        {
            Assert.True(PeElevationInspector.RequiresElevation(ms));
        }

        using (var ms = new MemoryStream(invokerPeBytes))
        {
            Assert.False(PeElevationInspector.RequiresElevation(ms));
        }

        // Invalid / empty / truncated streams fail safe to false without throwing
        using (var emptyMs = new MemoryStream([]))
        {
            Assert.False(PeElevationInspector.RequiresElevation(emptyMs));
        }

        using (var garbageMs = new MemoryStream("NOT A PE BINARY HEADER AT ALL"u8.ToArray()))
        {
            Assert.False(PeElevationInspector.RequiresElevation(garbageMs));
        }

        Assert.False(PeElevationInspector.RequiresElevation((string?)null));
        Assert.False(PeElevationInspector.RequiresElevation(@"Z:\NonExistent\fake.exe"));

        // End-to-end integration with SteamShortcutService.PrepareLaunchAsync
        await using var sandbox = new TestSandbox();
        (FakeSteamEnvironmentProbe probe, _, _) = CreateSandboxSteam(sandbox, isRunning: false);
        var service = CreateService(probe, new FakeSteamProcessController());
        var settings = new GameSteamLaunchSettings { LaunchMode = GameLaunchMode.Steam };

        GameRecord elevatedGame = CreateSandboxNonSteamGame(sandbox, "AdminGame", AntiCheatTier.Tier0, exeBytes: adminPeBytes);
        SteamLaunchPreparationResult elevatedPrep = await service.PrepareLaunchAsync(elevatedGame, settings);
        Assert.Contains(elevatedPrep.Warnings, w => w.Kind == SteamLaunchWarningKind.ElevatedExecutableUipi);

        GameRecord standardGame = CreateSandboxNonSteamGame(sandbox, "StandardGame", AntiCheatTier.Tier0, exeBytes: invokerPeBytes);
        SteamLaunchPreparationResult standardPrep = await service.PrepareLaunchAsync(standardGame, settings);
        Assert.DoesNotContain(standardPrep.Warnings, w => w.Kind == SteamLaunchWarningKind.ElevatedExecutableUipi);

        // External side-by-side <exe>.manifest requesting elevation is also detected
        await File.WriteAllTextAsync(standardGame.ExecutableFullPath + ".manifest", requireAdminXml);
        Assert.True(PeElevationInspector.RequiresElevation(standardGame.ExecutableFullPath));
    }

    private static (FakeSteamEnvironmentProbe Probe, string SteamDir, string ShortcutsPath) CreateSandboxSteam(
        TestSandbox sandbox,
        bool isRunning)
    {
        string steamDir = sandbox.CreateSubDirectory("fake-steam");
        File.WriteAllBytes(Path.Combine(steamDir, "steam.exe"), [0x4D, 0x5A]);
        Directory.CreateDirectory(Path.Combine(steamDir, "config"));
        Directory.CreateDirectory(Path.Combine(steamDir, "userdata", DefaultAccountId.ToString(), "config"));

        ulong steam64 = SteamLoginUsersVdfParser.ConvertAccountIdToSteamId64(DefaultAccountId);
        File.WriteAllText(
            Path.Combine(steamDir, "config", "loginusers.vdf"),
            $$"""
            "users"
            {
                "{{steam64}}"
                {
                    "AccountName" "sandbox_user"
                    "PersonaName" "Sandbox Player"
                    "MostRecent"  "1"
                    "Timestamp"   "1760000123"
                }
            }
            """);

        string shortcutsPath = Path.Combine(steamDir, "userdata", DefaultAccountId.ToString(), "config", "shortcuts.vdf");
        var probe = new FakeSteamEnvironmentProbe(sandbox.RootDirectory)
            .SetRegistryString(RegistryHive.CurrentUser, RegistryView.Default, HkcuSteamSubKey, "SteamPath", steamDir)
            .SetRegistryDword(RegistryHive.CurrentUser, RegistryView.Default, HkcuActiveProcessSubKey, "pid", isRunning ? 5555 : 0)
            .SetRegistryDword(
                RegistryHive.CurrentUser,
                RegistryView.Default,
                HkcuActiveProcessSubKey,
                "ActiveUser",
                isRunning ? unchecked((int)DefaultAccountId) : 0);

        probe.SteamRunning = isRunning;
        probe.LiveSteamPid = isRunning ? 5555 : null;

        return (probe, steamDir, shortcutsPath);
    }

    private static GameRecord CreateSandboxNonSteamGame(
        TestSandbox sandbox,
        string folderName,
        AntiCheatTier tier,
        int? steamAppId = null,
        string? launchArguments = null,
        byte[]? exeBytes = null)
    {
        string gameDir = sandbox.CreateSubDirectory("games", folderName);
        string exePath = Path.Combine(gameDir, "game.exe");
        File.WriteAllBytes(exePath, exeBytes ?? [0x4D, 0x5A, 0x90, 0x00]);

        return new GameRecord
        {
            GameId = SpawnId.NewUlid(),
            Title = folderName,
            InstallDir = gameDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.Standalone,
            AntiCheatTier = tier,
            SteamAppId = steamAppId,
            LaunchArguments = launchArguments,
        };
    }

    private static SteamShortcutService CreateService(
        FakeSteamEnvironmentProbe probe,
        ISteamProcessController controller,
        TimeProvider? timeProvider = null,
        SteamShortcutServiceOptions? options = null) =>
        new(
            new SteamClientLocator(probe),
            controller,
            timeProvider,
            options);

    private static async Task DriveManualClockUntilCompletedAsync(
        ManualTimeProvider clock,
        Task targetTask,
        TimeSpan step,
        int maxSteps,
        Action? onTick = null)
    {
        for (int i = 0; i < maxSteps && !targetTask.IsCompleted; i++)
        {
            var waitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            int yields = 0;
            while (!targetTask.IsCompleted && clock.PendingTimerCount == 0 && DateTime.UtcNow < waitDeadline)
            {
                if (yields < 20)
                {
                    await Task.Yield();
                    yields++;
                }
                else
                {
                    await Task.Delay(5);
                }
            }

            if (targetTask.IsCompleted)
            {
                break;
            }

            if (clock.PendingTimerCount == 0)
            {
                break;
            }

            onTick?.Invoke();
            clock.Advance(step);
        }

        await targetTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Builds a minimal valid PE32+ binary containing a single <c>.rsrc</c> section with a 3-level
    /// <c>RT_MANIFEST</c> (ID 24 -> ID 1 -> Lang 1033) resource pointing to <paramref name="manifestBytes"/>.
    /// </summary>
    private static byte[] BuildSyntheticPeWithRtManifest(byte[] manifestBytes)
    {
        const int peHeaderOffset = 0x80;
        const int optionalHeaderOffset = peHeaderOffset + 4 + 20; // 0x98
        const int optionalHeaderSize = 0xF0; // 240 bytes (PE32+)
        const int sectionTableOffset = optionalHeaderOffset + optionalHeaderSize; // 0x188
        const int rsrcFileOffset = 0x0200;
        const int rsrcVirtualAddress = 0x1000;

        const int level1Offset = 0x00; // 24 bytes
        const int level2Offset = 0x18; // 24 bytes
        const int level3Offset = 0x30; // 24 bytes
        const int dataEntryOffset = 0x48; // 16 bytes
        const int manifestOffset = 0x58;

        int rsrcVirtualSize = manifestOffset + manifestBytes.Length;
        int rsrcRawSize = ((rsrcVirtualSize + 0x1FF) / 0x200) * 0x200;
        int sectionVirtualSpan = ((rsrcVirtualSize + 0xFFF) / 0x1000) * 0x1000;
        int sizeOfImage = rsrcVirtualAddress + sectionVirtualSpan;

        byte[] pe = new byte[rsrcFileOffset + rsrcRawSize];

        // DOS Header
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C, 4), peHeaderOffset);

        // PE Signature
        pe[peHeaderOffset + 0] = (byte)'P';
        pe[peHeaderOffset + 1] = (byte)'E';
        pe[peHeaderOffset + 2] = 0;
        pe[peHeaderOffset + 3] = 0;

        // COFF Header (20 bytes)
        int coff = peHeaderOffset + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 0, 2), 0x8664); // Machine: AMD64
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 2, 2), 1); // NumberOfSections
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 16, 2), optionalHeaderSize); // SizeOfOptionalHeader
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 18, 2), 0x0022); // Characteristics

        // Optional Header (PE32+)
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(optionalHeaderOffset + 0, 2), 0x020B); // Magic: PE32+
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalHeaderOffset + 32, 4), 0x1000); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalHeaderOffset + 36, 4), 0x0200); // FileAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalHeaderOffset + 56, 4), (uint)sizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalHeaderOffset + 60, 4), rsrcFileOffset); // SizeOfHeaders
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(optionalHeaderOffset + 68, 2), 3); // Subsystem: CUI
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalHeaderOffset + 108, 4), 16); // NumberOfRvaAndSizes

        // DataDirectory[2] = Resource Table (offset 112 + 2*8 = 128)
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalHeaderOffset + 128, 4), rsrcVirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalHeaderOffset + 132, 4), (uint)rsrcVirtualSize);

        // Section Header: .rsrc (40 bytes)
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(pe, sectionTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionTableOffset + 8, 4), (uint)rsrcVirtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionTableOffset + 12, 4), rsrcVirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionTableOffset + 16, 4), (uint)rsrcRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionTableOffset + 20, 4), rsrcFileOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionTableOffset + 36, 4), 0x40000040u); // InitializedData | MemRead

        // .rsrc Level 1: Type = 24 (RT_MANIFEST) -> subdir at level2Offset
        Span<byte> rsrc = pe.AsSpan(rsrcFileOffset, rsrcRawSize);
        BinaryPrimitives.WriteUInt16LittleEndian(rsrc.Slice(level1Offset + 14, 2), 1); // NumberOfIdEntries
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(level1Offset + 16, 4), 24u); // RT_MANIFEST
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(level1Offset + 20, 4), 0x80000000u | (uint)level2Offset);

        // .rsrc Level 2: Name ID = 1 -> subdir at level3Offset
        BinaryPrimitives.WriteUInt16LittleEndian(rsrc.Slice(level2Offset + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(level2Offset + 16, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(level2Offset + 20, 4), 0x80000000u | (uint)level3Offset);

        // .rsrc Level 3: Lang ID = 1033 -> leaf IMAGE_RESOURCE_DATA_ENTRY at dataEntryOffset
        BinaryPrimitives.WriteUInt16LittleEndian(rsrc.Slice(level3Offset + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(level3Offset + 16, 4), 1033u);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(level3Offset + 20, 4), (uint)dataEntryOffset);

        // IMAGE_RESOURCE_DATA_ENTRY
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(dataEntryOffset + 0, 4), (uint)(rsrcVirtualAddress + manifestOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(dataEntryOffset + 4, 4), (uint)manifestBytes.Length);

        // Manifest payload
        manifestBytes.CopyTo(rsrc.Slice(manifestOffset));

        return pe;
    }
}
