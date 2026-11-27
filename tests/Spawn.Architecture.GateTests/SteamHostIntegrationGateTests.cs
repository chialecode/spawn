using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host;
using Spawn.Host.ViewModels;
using Spawn.OverlayHost.Audio;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Headless integration tests covering Host DI composition for Steam launch services,
/// <see cref="MainWindowViewModel"/> Steam shortcut sync failure cleanup (preventing permanently locked launch buttons),
/// and <see cref="SynthesizedUnlockSoundPlayer"/> propagation of <see cref="OverlayToastRequest.SteamInstallDirectoryOverride"/>.
/// </summary>
[Collection(WpfStaHeadlessCollection.Name)]
public sealed class SteamHostIntegrationGateTests
{
    [Fact]
    public async Task HostDiComposition_RegistersAndResolvesSteamServicesAndMainWindowViewModelCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-steam-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var paths = new SpawnPaths(explicitDataDir: Path.Combine(tempRoot, "data"), explicitIdentity: "spawn-dev-test");
            paths.EnsureDirectoriesExist();

            var services = new ServiceCollection();
            App.RegisterHostCoreAndModuleServices(services, paths);

            await using ServiceProvider sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<SpawnDatabase>();
            await db.InitializeAsync();

            var locator = sp.GetRequiredService<ISteamClientLocator>();
            var shortcutService = sp.GetRequiredService<ISteamShortcutService>();
            var launchService = sp.GetRequiredService<IGameLaunchService>();
            var achievementService = sp.GetRequiredService<IAchievementService>();
            var gameDataService = sp.GetRequiredService<IGameDataManagementService>();
            var settingsVm = sp.GetRequiredService<SettingsViewModel>();
            var mainVm = sp.GetRequiredService<MainWindowViewModel>();

            Assert.NotNull(locator);
            Assert.NotNull(shortcutService);
            Assert.NotNull(launchService);
            Assert.NotNull(achievementService);
            Assert.NotNull(gameDataService);
            Assert.NotNull(settingsVm);
            Assert.Same(locator, mainVm.SteamClientLocator);
            Assert.Same(shortcutService, mainVm.SteamShortcutService);
        }
        finally
        {
            TryDeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task MainWindowViewModel_SteamSyncFailureAndException_ClearLaunchingStateWithoutLockingPrimaryButton()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-steam-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var paths = new SpawnPaths(explicitDataDir: Path.Combine(tempRoot, "data"), explicitIdentity: "spawn-dev-test");
            paths.EnsureDirectoriesExist();

            string gameDir = Path.Combine(tempRoot, "games", "SyncFailureGame");
            Directory.CreateDirectory(gameDir);
            File.WriteAllBytes(Path.Combine(gameDir, "game.exe"), [0x4D, 0x5A]);

            var stubLocator = new StubSteamClientLocator();
            var stubShortcutService = new StubFailingSteamShortcutService();

            var services = new ServiceCollection();
            App.RegisterHostCoreAndModuleServices(services, paths);
            services.Replace(ServiceDescriptor.Singleton<ISteamClientLocator>(stubLocator));
            services.Replace(ServiceDescriptor.Singleton<ISteamShortcutService>(stubShortcutService));

            await using ServiceProvider sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<SpawnDatabase>();
            await db.InitializeAsync();

            var gameRecord = new GameRecord
            {
                GameId = SpawnId.NewUlid(),
                Title = "Sync Failure Recovery Game",
                InstallDir = gameDir,
                ExecutableRelPath = "game.exe",
                PlatformSource = PlatformSource.Standalone,
                AntiCheatTier = AntiCheatTier.Tier0,
            };
            await db.UpsertGameAsync(gameRecord);

            var mainVm = sp.GetRequiredService<MainWindowViewModel>();
            mainVm.ConfirmPromptHandler = _ => true;
            await mainVm.InitializeAsync();

            GameListItemViewModel gameVm = Assert.Single(mainVm.AllGames, g => g.GameId == gameRecord.GameId);
            mainVm.SelectLibraryGameCommand.Execute(gameVm);
            Assert.Equal(GameLaunchButtonPhase.Idle, mainVm.PrimaryLaunchButtonPhase);
            Assert.True(mainVm.IsPrimaryLaunchButtonEnabled);

            // Case 1: NeedsSteamRestartToSync -> sync returns Succeeded = false -> button MUST NOT stay stuck in Launching.
            stubShortcutService.NextPrepareStatus = SteamLaunchPreparationStatus.NeedsSteamRestartToSync;
            stubShortcutService.ThrowOnApply = false;
            await mainVm.LaunchGameWithModeAsync(gameVm, GameLaunchMode.Steam);

            Assert.Equal(GameLaunchButtonPhase.Idle, mainVm.PrimaryLaunchButtonPhase);
            Assert.True(mainVm.IsPrimaryLaunchButtonEnabled);
            Assert.False(mainVm.IsSyncingSteamShortcutForSelected);
            Assert.True(mainVm.IsStatusBannerError);

            // Case 2: NeedsSteamRestartToSync -> sync throws exception -> button MUST NOT stay stuck in Launching.
            stubShortcutService.ThrowOnApply = true;
            await mainVm.LaunchGameWithModeAsync(gameVm, GameLaunchMode.Steam);

            Assert.Equal(GameLaunchButtonPhase.Idle, mainVm.PrimaryLaunchButtonPhase);
            Assert.True(mainVm.IsPrimaryLaunchButtonEnabled);
            Assert.False(mainVm.IsSyncingSteamShortcutForSelected);
            Assert.True(mainVm.IsStatusBannerError);

            // Case 3: NeedsShortcutWriteSteamClosed -> sync returns Succeeded = false -> button MUST NOT stay stuck in Launching.
            stubShortcutService.NextPrepareStatus = SteamLaunchPreparationStatus.NeedsShortcutWriteSteamClosed;
            stubShortcutService.ThrowOnApply = false;
            await mainVm.LaunchGameWithModeAsync(gameVm, GameLaunchMode.Steam);

            Assert.Equal(GameLaunchButtonPhase.Idle, mainVm.PrimaryLaunchButtonPhase);
            Assert.True(mainVm.IsPrimaryLaunchButtonEnabled);
            Assert.False(mainVm.IsSyncingSteamShortcutForSelected);
            Assert.True(mainVm.IsStatusBannerError);
        }
        finally
        {
            TryDeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_SteamAuthentic_ForwardsSteamInstallDirectoryOverrideAndReprobesOnChange()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-steam-sound-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string steamDirA = Directory.CreateDirectory(Path.Combine(tempRoot, "SteamA")).FullName;
            string steamDirB = Directory.CreateDirectory(Path.Combine(tempRoot, "SteamB")).FullName;
            string wavA = Path.Combine(steamDirA, "toast_a.wav");
            string wavB = Path.Combine(steamDirB, "toast_b.wav");
            File.WriteAllBytes(wavA, SynthesizedUnlockSoundPlayer.SynthesizeWavBytes(SynthesizedUnlockSoundPlayer.ChimePreset.SteamCrystal, 0.5f));
            File.WriteAllBytes(wavB, SynthesizedUnlockSoundPlayer.SynthesizeWavBytes(SynthesizedUnlockSoundPlayer.ChimePreset.Ps5Trophy, 0.5f));

            var probedOverrides = new List<string?>();
            using var player = new SynthesizedUnlockSoundPlayer(
                forceSilent: true,
                steamProbe: overrideDir =>
                {
                    probedOverrides.Add(overrideDir);
                    string? wav = string.Equals(overrideDir, steamDirA, StringComparison.OrdinalIgnoreCase)
                        ? wavA
                        : (string.Equals(overrideDir, steamDirB, StringComparison.OrdinalIgnoreCase) ? wavB : null);
                    return new SteamClientSoundProbeResult(
                        IsSteamInstalled: wav is not null,
                        SteamInstallDirectory: overrideDir,
                        AchievementToastWavPath: wav,
                        GamepadUiAchievementToastWavPath: null,
                        StatusMessage: wav is not null ? "ok" : "missing");
                });

            var reqA = new OverlayToastRequest
            {
                GameTitle = "Test",
                AchievementApiName = "ACH_A",
                DisplayName = "A",
                SoundPreset = OverlaySoundPreset.SteamAuthentic,
                SteamInstallDirectoryOverride = steamDirA,
                EnhanceRareAndPlatinumSound = false,
            };

            var clipA1 = player.TryResolveDecodedExternalClip(reqA);
            Assert.NotNull(clipA1);
            Assert.Single(probedOverrides);
            Assert.Equal(steamDirA, probedOverrides[0]);

            // Repeated call with identical override reuses cached clip without re-probing.
            var clipA2 = player.TryResolveDecodedExternalClip(reqA);
            Assert.NotNull(clipA2);
            Assert.Single(probedOverrides);

            // Switching SteamInstallDirectoryOverride invalidates cache and re-probes with steamDirB!
            var reqB = reqA with { AchievementApiName = "ACH_B", SteamInstallDirectoryOverride = steamDirB };
            var clipB = player.TryResolveDecodedExternalClip(reqB);
            Assert.NotNull(clipB);
            Assert.Equal(2, probedOverrides.Count);
            Assert.Equal(steamDirB, probedOverrides[1]);
        }
        finally
        {
            TryDeleteTempDirectory(tempRoot);
        }
    }

    private static void TryDeleteTempDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private sealed class StubSteamClientLocator : ISteamClientLocator
    {
        public SteamClientDiscoveryResult Discover(string? explicitInstallDirOverride = null, uint? preferredAccountId = null) =>
            new()
            {
                IsDetected = true,
                InstallDirectory = @"C:\FakeSteam",
                SteamExecutablePath = @"C:\FakeSteam\steam.exe",
                Source = SteamPathDiscoverySource.UserOverride,
                IsUserOverride = true,
                IsSteamRunning = true,
                SteamProcessId = 9999,
                ActiveAccountId = 12345,
            };

        public bool TryNormalizeInstallDirectory(
            string? rawPathOrExe,
            out string? normalizedInstallDir,
            out string? steamExePath,
            out string? errorMessage)
        {
            normalizedInstallDir = rawPathOrExe;
            steamExePath = rawPathOrExe is null ? null : Path.Combine(rawPathOrExe, "steam.exe");
            errorMessage = null;
            return !string.IsNullOrWhiteSpace(rawPathOrExe);
        }
    }

    private sealed class StubFailingSteamShortcutService : ISteamShortcutService
    {
        public SteamLaunchPreparationStatus NextPrepareStatus { get; set; } = SteamLaunchPreparationStatus.NeedsSteamRestartToSync;
        public bool ThrowOnApply { get; set; }

        public Task<SteamLaunchPreparationResult> PrepareLaunchAsync(
            GameRecord game,
            GameSteamLaunchSettings settings,
            string? steamInstallDirOverride = null,
            uint? preferredAccountId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = NextPrepareStatus,
                TargetKind = SteamLaunchTargetKind.NonSteamShortcut,
                TargetAccountId = 12345,
                ShortcutAppName = game.Title,
            });

        public Task<SteamShortcutSyncResult> ApplyShortcutAndRestartIfNeededAsync(
            GameRecord game,
            GameSteamLaunchSettings settings,
            bool allowGracefulSteamRestart,
            string? steamInstallDirOverride = null,
            uint? preferredAccountId = null,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnApply)
            {
                throw new InvalidOperationException("Simulated Steam sync transport failure.");
            }

            return Task.FromResult(new SteamShortcutSyncResult
            {
                Succeeded = false,
                ErrorMessage = "模拟 Steam 重启同步超时。",
            });
        }
    }
}
