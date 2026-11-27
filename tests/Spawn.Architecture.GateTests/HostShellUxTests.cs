using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Spawn.Host.Views;
using Spawn.Modules.Progress.Security;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Headless isolated unit tests for Track 1 Host Shell UX & Safety Guards:
/// destructive action confirmations, Game Properties editor, global feedback severity/persistence,
/// Library & Achievements sidebar/list upgrades, Settings persistence & hot-apply, narrow drawer,
/// window placement clamping, Win11 HTMAXBUTTON hit-test, and dark/light implicit control styles.
/// </summary>
[Collection(WpfStaHeadlessCollection.Name)]
public sealed class HostShellUxTests
{
    [Fact]
    public async Task RemoveGame_CancelPreservesGame_ConfirmShowsSummaryAndSyncsSaveSelection()
    {
        await using var env = await TestShellHarness.CreateAsync();
        string gameDir = env.CreateFakeGameDirectory("Hollow", "Hollow.exe");
        var game = new GameRecord
        {
            GameId = "game-hollow",
            Title = "Hollow",
            InstallDir = gameDir,
            ExecutableRelPath = "Hollow.exe",
            PlatformSource = PlatformSource.Steamrip,
            AchievementCaptureMode = AchievementCaptureMode.GbeForkActivated,
            TotalActiveSec = 7200,
        };
        await env.SeedGameAsync(game);
        env.SaveService.SetGraph(game.GameId, branchCount: 2, nodeCount: 5);
        env.TrainerCatalog.SetTrainers(game.GameId, [CreateTrainerRecord("tr-1", game.GameId)]);

        await env.MainVm.InitializeAsync();
        env.MainVm.SelectLibraryGameCommand.Execute(env.MainVm.AllGames.Single());
        env.MainVm.SelectSaveGameItemCommand.Execute(env.MainVm.AllGames.Single());
        Assert.NotNull(env.MainVm.SelectedSaveGame);
        Assert.False(env.MainVm.IsSaveOverviewSelected);

        ShellConfirmPrompt? capturedPrompt = null;
        env.MainVm.ConfirmPromptHandler = prompt =>
        {
            capturedPrompt = prompt;
            return false;
        };

        await env.MainVm.RemoveSelectedGameFromLibraryCommand.ExecuteAsync(null);

        Assert.NotNull(capturedPrompt);
        Assert.True(capturedPrompt!.IsDanger);
        Assert.Contains("5 份快照", capturedPrompt.Message, StringComparison.Ordinal);
        Assert.Contains("2 条分支", capturedPrompt.Message, StringComparison.Ordinal);
        Assert.Contains("1 项", capturedPrompt.Message, StringComparison.Ordinal);
        Assert.Contains("完好保留", capturedPrompt.Message, StringComparison.Ordinal);
        Assert.Contains("gbe_fork", capturedPrompt.Message, StringComparison.Ordinal);
        Assert.Single(env.MainVm.AllGames);

        env.MainVm.ConfirmPromptHandler = _ => true;
        int overviewCallsBefore = env.SaveService.OverviewCallCount;
        await env.MainVm.RemoveSelectedGameFromLibraryCommand.ExecuteAsync(null);

        Assert.Empty(env.MainVm.AllGames);
        Assert.Null(env.MainVm.SelectedGame);
        Assert.Null(env.MainVm.SelectedSaveGame);
        Assert.True(env.MainVm.IsSaveOverviewSelected);
        Assert.True(env.SaveService.OverviewCallCount > overviewCallsBefore);
    }

    [Fact]
    public async Task CleanGameData_DefaultResetFalse_ConfirmsCounts_AndDeduplicatesAchievementReset()
    {
        await using var env = await TestShellHarness.CreateAsync();
        string gameDir = env.CreateFakeGameDirectory("Celeste", "Celeste.exe");
        var game = new GameRecord
        {
            GameId = "game-celeste",
            Title = "Celeste",
            InstallDir = gameDir,
            ExecutableRelPath = "Celeste.exe",
            PlatformSource = PlatformSource.GogOffline,
        };
        await env.SeedGameAsync(game);
        env.GameData.SetEntries(game.GameId,
        [
            new GameDataEntryDto
            {
                EntryId = "e-save",
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.Ludusavi,
                PathOrRegistryKey = Path.Combine(gameDir, "Saves"),
                Exists = true,
                FileCount = 4,
                TotalBytes = 8192,
                IsSafeToClean = true,
                DefaultSelected = true,
            },
            new GameDataEntryDto
            {
                EntryId = "e-reg",
                Category = GameDataCategory.RegistryData,
                SourceRule = GameDataSourceRule.Ludusavi,
                PathOrRegistryKey = @"HKCU\Software\CelesteTest",
                Exists = true,
                FileCount = 2,
                IsSafeToClean = true,
                DefaultSelected = true,
            },
        ]);

        await env.MainVm.InitializeAsync();
        Assert.False(env.MainVm.ResetSpawnStateOnClean);

        env.MainVm.SelectLibraryGameCommand.Execute(env.MainVm.AllGames.Single());
        env.MainVm.OpenGameDataSubTabCommand.Execute(null);
        await WaitUntilAsync(() => env.MainVm.AllGameDataItems.Count == 2);

        // Cancel confirmation -> CleanGameDataAsync must not execute.
        ShellConfirmPrompt? capturedPrompt = null;
        env.MainVm.ConfirmPromptHandler = prompt =>
        {
            capturedPrompt = prompt;
            return false;
        };

        await env.MainVm.CleanSelectedGameDataCommand.ExecuteAsync(null);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("4 个", capturedPrompt!.Message, StringComparison.Ordinal);
        Assert.Contains("1 项", capturedPrompt.Message, StringComparison.Ordinal);
        Assert.Contains("否", capturedPrompt.Message, StringComparison.Ordinal);
        Assert.Equal(0, env.GameData.CleanCallCount);

        // Confirm with ResetSpawnStateOnClean = true AND SpawnStateReset = true -> no duplicate ResetGameAchievementStateAsync.
        env.MainVm.ResetSpawnStateOnClean = true;
        env.GameData.NextSpawnStateReset = true;
        env.MainVm.ConfirmPromptHandler = _ => true;
        await env.MainVm.CleanSelectedGameDataCommand.ExecuteAsync(null);

        Assert.Equal(1, env.GameData.CleanCallCount);
        Assert.Equal(0, env.Achievements.ResetCallCount);

        // When backend reports SpawnStateReset = false -> fallback calls ResetGameAchievementStateAsync once.
        env.GameData.NextSpawnStateReset = false;
        await env.MainVm.CleanSelectedGameDataCommand.ExecuteAsync(null);
        Assert.Equal(2, env.GameData.CleanCallCount);
        Assert.Equal(1, env.Achievements.ResetCallCount);
    }

    [Fact]
    public async Task LaunchStateMachine_CooldownDisablesLaunch_StopPromptsConfirmation()
    {
        await using var env = await TestShellHarness.CreateAsync();
        string gameDir = env.CreateFakeGameDirectory("Hades", "Hades.exe");
        var game = new GameRecord
        {
            GameId = "game-hades",
            Title = "Hades",
            InstallDir = gameDir,
            ExecutableRelPath = "Hades.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        await env.SeedGameAsync(game);
        await env.MainVm.InitializeAsync();
        env.MainVm.SelectLibraryGameCommand.Execute(env.MainVm.AllGames.Single());

        Assert.Equal(GameLaunchButtonPhase.Idle, env.MainVm.PrimaryLaunchButtonPhase);
        Assert.True(env.MainVm.IsPrimaryLaunchButtonEnabled);

        // Cooldown locks launch button and surfaces remaining seconds + Settings warning color.
        env.Guard.SetSnapshot(new AntiCheatGuardSnapshot(
            AntiCheatGuardPhase.CooldownLocked,
            IsHighRiskLocked: true,
            RemainingCooldownSeconds: 11.4,
            ActiveProtectedGameId: null,
            DetectedResidualProcesses: []));

        Assert.Equal(GameLaunchButtonPhase.GuardCooldownLocked, env.MainVm.PrimaryLaunchButtonPhase);
        Assert.False(env.MainVm.IsPrimaryLaunchButtonEnabled);
        Assert.Contains("12s", env.MainVm.PrimaryLaunchButtonText, StringComparison.Ordinal);
        Assert.Contains("11.4", env.MainVm.PrimaryLaunchButtonToolTip, StringComparison.Ordinal);
        Assert.True(env.SettingsVm.IsAntiCheatGuardWarning);

        // Return to idle -> launch transitions cleanly.
        env.Guard.SetSnapshot(new AntiCheatGuardSnapshot(
            AntiCheatGuardPhase.IdleSafe,
            IsHighRiskLocked: false,
            RemainingCooldownSeconds: 0,
            ActiveProtectedGameId: null,
            DetectedResidualProcesses: []));
        Assert.True(env.MainVm.IsPrimaryLaunchButtonEnabled);

        // Simulate ShellBreakingWatch (waiting for window) -> disabled.
        var watchingSession = new ActiveGameSession
        {
            SessionId = "s-1",
            GameId = game.GameId,
            GameTitle = game.Title,
            InstallDir = game.InstallDir,
            State = PlaySessionActivityState.ShellBreakingWatch,
            StartedUtc = DateTimeOffset.UtcNow,
        };
        env.Launch.SetSession(game.GameId, watchingSession);
        Assert.Equal(GameLaunchButtonPhase.WaitingForWindow, env.MainVm.PrimaryLaunchButtonPhase);
        Assert.False(env.MainVm.IsPrimaryLaunchButtonEnabled);

        // Active running -> enabled + Tag == "Running", stop requires confirm.
        var activeSession = watchingSession with { State = PlaySessionActivityState.Active };
        env.Launch.SetSession(game.GameId, activeSession);
        Assert.Equal(GameLaunchButtonPhase.Running, env.MainVm.PrimaryLaunchButtonPhase);
        Assert.Equal("Running", env.MainVm.PrimaryLaunchButtonTag);
        Assert.True(env.MainVm.IsPrimaryLaunchButtonEnabled);

        env.MainVm.ConfirmPromptHandler = _ => false;
        await env.MainVm.PrimaryLaunchOrStopCommand.ExecuteAsync(null);
        Assert.Equal(0, env.Launch.StopCallCount);

        env.MainVm.ConfirmPromptHandler = _ => true;
        await env.MainVm.PrimaryLaunchOrStopCommand.ExecuteAsync(null);
        Assert.Equal(1, env.Launch.StopCallCount);
    }

    [Fact]
    public async Task GbeForkActivateAndRestore_BlocksWhileRunning_AndConfirmsWhenStopped()
    {
        await using var env = await TestShellHarness.CreateAsync();
        string gameDir = env.CreateFakeGameDirectory("Silksong", "Silksong.exe");
        var game = new GameRecord
        {
            GameId = "game-silk",
            Title = "Silksong",
            InstallDir = gameDir,
            ExecutableRelPath = "Silksong.exe",
            PlatformSource = PlatformSource.Steamrip,
            AchievementCaptureMode = AchievementCaptureMode.PendingGbeForkActivation,
        };
        await env.SeedGameAsync(game);
        await env.MainVm.InitializeAsync();
        env.MainVm.SelectLibraryGameCommand.Execute(env.MainVm.AllGames.Single());

        // Block while running.
        env.Launch.SetSession(game.GameId, new ActiveGameSession
        {
            SessionId = "s-silk",
            GameId = game.GameId,
            GameTitle = game.Title,
            InstallDir = gameDir,
            State = PlaySessionActivityState.Active,
            StartedUtc = DateTimeOffset.UtcNow,
        });

        await env.MainVm.ActivateGbeForkPatchCommand.ExecuteAsync(null);
        Assert.True(env.MainVm.IsStatusBannerError);
        Assert.Equal(0, env.Achievements.ActivateGbeCallCount);

        await env.MainVm.RestoreOriginalDllCommand.ExecuteAsync(null);
        Assert.True(env.MainVm.IsStatusBannerError);
        Assert.Equal(0, env.Achievements.RestoreDllCallCount);

        // Stop game -> confirm dialog explains DLL backup & GSE Saves.
        env.Launch.SetSession(game.GameId, null);
        ShellConfirmPrompt? activatePrompt = null;
        env.MainVm.ConfirmPromptHandler = p =>
        {
            activatePrompt = p;
            return false;
        };

        await env.MainVm.ActivateGbeForkPatchCommand.ExecuteAsync(null);
        Assert.NotNull(activatePrompt);
        Assert.Contains("GSE Saves", activatePrompt!.Message, StringComparison.Ordinal);
        Assert.Contains(".spawn_vanilla_bak", activatePrompt.Message, StringComparison.Ordinal);
        Assert.Equal(0, env.Achievements.ActivateGbeCallCount);

        env.MainVm.ConfirmPromptHandler = _ => true;
        await env.MainVm.ActivateGbeForkPatchCommand.ExecuteAsync(null);
        Assert.Equal(1, env.Achievements.ActivateGbeCallCount);

        await env.MainVm.RestoreOriginalDllCommand.ExecuteAsync(null);
        Assert.Equal(1, env.Achievements.RestoreDllCallCount);
    }

    [Fact]
    public async Task EditGameProperties_EnumeratesCandidateExes_PersistsUpdateAndRefreshesSchema()
    {
        await using var env = await TestShellHarness.CreateAsync();
        string gameDir = env.CreateFakeGameDirectory("Ori", "Ori.exe");
        string subDir = Path.Combine(gameDir, "Binaries", "Win64");
        Directory.CreateDirectory(subDir);
        File.WriteAllBytes(Path.Combine(subDir, "Ori-Win64-Shipping.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(gameDir, "unins000.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(gameDir, "UnityCrashHandler64.exe"), [0x4D, 0x5A]);

        var candidates = MainWindowViewModel.EnumerateCandidateExecutables(gameDir, "Ori.exe");
        Assert.Contains("Ori.exe", candidates);
        Assert.Contains(@"Binaries\Win64\Ori-Win64-Shipping.exe", candidates);
        Assert.DoesNotContain("unins000.exe", candidates);
        Assert.DoesNotContain("UnityCrashHandler64.exe", candidates);

        var game = new GameRecord
        {
            GameId = "game-ori",
            Title = "Ori",
            InstallDir = gameDir,
            ExecutableRelPath = "Ori.exe",
            PlatformSource = PlatformSource.Standalone,
            SteamAppId = 100,
        };
        await env.SeedGameAsync(game);
        await env.MainVm.InitializeAsync();
        env.MainVm.SelectLibraryGameCommand.Execute(env.MainVm.AllGames.Single());

        GamePropertiesEditRequest? capturedReq = null;
        env.MainVm.EditGamePropertiesHandler = req =>
        {
            capturedReq = req;
            return new GamePropertiesEditResult(
                Title: "Ori Definitive",
                ExecutableRelPath: @"Binaries\Win64\Ori-Win64-Shipping.exe",
                LaunchArguments: "-windowed -dx11",
                SteamAppId: 1057090,
                GogGameId: "123456789",
                CoverImagePath: Path.Combine(gameDir, "cover.png"),
                IconPath: Path.Combine(gameDir, "icon.png"));
        };

        await env.MainVm.EditGamePropertiesCommand.ExecuteAsync(null);

        Assert.NotNull(capturedReq);
        Assert.Contains(@"Binaries\Win64\Ori-Win64-Shipping.exe", capturedReq!.CandidateExecutables);
        Assert.Equal(1, env.Library.UpdateMetadataCallCount);
        Assert.Equal("Ori Definitive", env.MainVm.SelectedGame?.Title);
        Assert.Equal("-windowed -dx11", env.MainVm.SelectedGame?.Record.LaunchArguments);
        Assert.Equal(1057090, env.MainVm.SelectedGame?.Record.SteamAppId);
        Assert.Equal("123456789", env.MainVm.SelectedGame?.Record.GogGameId);
        Assert.Equal(1, env.Achievements.SyncSchemaCallCount);
    }

    [Fact]
    public async Task FeedbackBannerSeverity_PersistsOnError_CopiesText_AndAggregatesBusyState()
    {
        await using var env = await TestShellHarness.CreateAsync();
        await env.MainVm.InitializeAsync();

        // Trigger an error via SettingsViewModel (invalid Steam API key format).
        env.SettingsVm.CustomSteamApiKeyInput = "not-a-valid-hex-key";
        await env.SettingsVm.SaveCustomSteamApiKeyCommand.ExecuteAsync(null);

        Assert.True(env.MainVm.IsStatusBannerError);
        Assert.Contains("32 位", env.MainVm.StatusBannerMessage, StringComparison.Ordinal);

        env.MainVm.CopyStatusBannerCommand.Execute(null);
        Assert.Equal(env.MainVm.StatusBannerMessage, env.LastCopiedClipboardText);

        env.MainVm.DismissStatusBannerCommand.Execute(null);
        Assert.Equal(string.Empty, env.MainVm.StatusBannerMessage);
        Assert.False(env.MainVm.IsStatusBannerError);

        // Busy state aggregation across child states.
        Assert.False(env.MainVm.IsAnyBusyWorking);
        env.MainVm.IsBusyWorking = true;
        Assert.True(env.MainVm.IsAnyBusyWorking);
        env.MainVm.IsBusyWorking = false;
        env.MainVm.IsInspectingGameData = true;
        Assert.True(env.MainVm.IsAnyBusyWorking);
        env.MainVm.IsInspectingGameData = false;
        Assert.False(env.MainVm.IsAnyBusyWorking);
    }

    [Fact]
    public async Task LibrarySidebar_ZeroDuplicates_EmptyGroupsHidden_SearchSortAndSourceChipsWork()
    {
        await using var env = await TestShellHarness.CreateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await env.SeedGameAsync(new GameRecord
        {
            GameId = "g-gog",
            Title = "Alpha GOG",
            InstallDir = env.CreateFakeGameDirectory("Alpha", "a.exe"),
            ExecutableRelPath = "a.exe",
            PlatformSource = PlatformSource.GogOffline,
            TotalActiveSec = 600,
            LastPlayedUtc = now.AddDays(-2),
        });
        await env.SeedGameAsync(new GameRecord
        {
            GameId = "g-rip",
            Title = "Bravo Rip",
            InstallDir = env.CreateFakeGameDirectory("Bravo", "b.exe"),
            ExecutableRelPath = "b.exe",
            PlatformSource = PlatformSource.Steamrip,
            TotalActiveSec = 120,
            LastPlayedUtc = now.AddHours(-1),
        });
        await env.SeedGameAsync(new GameRecord
        {
            GameId = "g-local",
            Title = "Charlie Local",
            InstallDir = env.CreateFakeGameDirectory("Charlie", "c.exe"),
            ExecutableRelPath = "c.exe",
            PlatformSource = PlatformSource.Standalone,
            TotalActiveSec = 3600,
            LastPlayedUtc = null,
        });

        await env.MainVm.InitializeAsync();

        // Legacy LibraryGroups[0] stays intact for backward compatibility.
        Assert.Equal(3, env.MainVm.LibraryGroups[0].Items.Count);

        // VisibleLibraryGroups has ZERO duplicates and ZERO empty groups.
        var allVisibleIds = env.MainVm.VisibleLibraryGroups.SelectMany(g => g.Items).Select(i => i.GameId).ToList();
        Assert.Equal(3, allVisibleIds.Count);
        Assert.Equal(3, allVisibleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(env.MainVm.VisibleLibraryGroups, g => Assert.True(g.Count > 0));

        // Source chip filtering.
        env.MainVm.SelectedLibrarySourceFilter = LibrarySourceFilter.GogOffline;
        Assert.Single(env.MainVm.VisibleLibraryGroups);
        Assert.Equal("g-gog", env.MainVm.VisibleLibraryGroups[0].Items.Single().GameId);

        // Sort by LastPlayed across All.
        env.MainVm.SelectedLibrarySourceFilter = LibrarySourceFilter.All;
        env.MainVm.SelectedLibrarySortMode = LibrarySortMode.LastPlayed;
        Assert.Equal(["g-rip", "g-gog", "g-local"], env.MainVm.VisibleLibraryGroups.Single().Items.Select(i => i.GameId).ToArray());

        // Sort by Playtime across All.
        env.MainVm.SelectedLibrarySortMode = LibrarySortMode.Playtime;
        Assert.Equal(["g-local", "g-gog", "g-rip"], env.MainVm.VisibleLibraryGroups.Single().Items.Select(i => i.GameId).ToArray());

        // Search + no-match indicator.
        env.MainVm.LibrarySidebarSearchText = "Bravo";
        Assert.Equal(["g-rip"], env.MainVm.VisibleLibraryGroups.SelectMany(g => g.Items).Select(i => i.GameId).ToArray());
        Assert.False(env.MainVm.HasLibrarySearchNoMatches);

        env.MainVm.LibrarySidebarSearchText = "DoesNotExistXYZ";
        Assert.Empty(env.MainVm.VisibleLibraryGroups);
        Assert.True(env.MainVm.HasLibrarySearchNoMatches);
    }

    [Fact]
    public async Task AchievementsCenter_ReloadsOnReturn_ClearsSearch_PreservesRowSpoilerCache_AndCountsTabs()
    {
        await using var env = await TestShellHarness.CreateAsync();
        var gameA = new GameRecord
        {
            GameId = "game-a",
            Title = "Game A",
            InstallDir = env.CreateFakeGameDirectory("GameA", "a.exe"),
            ExecutableRelPath = "a.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        var gameB = new GameRecord
        {
            GameId = "game-b",
            Title = "Game B",
            InstallDir = env.CreateFakeGameDirectory("GameB", "b.exe"),
            ExecutableRelPath = "b.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        var gameZero = new GameRecord
        {
            GameId = "game-zero",
            Title = "Zero Ach",
            InstallDir = env.CreateFakeGameDirectory("Zero", "z.exe"),
            ExecutableRelPath = "z.exe",
            PlatformSource = PlatformSource.Standalone,
        };
        await env.SeedGameAsync(gameA);
        await env.SeedGameAsync(gameB);
        await env.SeedGameAsync(gameZero);

        env.Achievements.SetSummary("game-a",
        [
            CreateAchItem("A_1", "Alpha One", unlocked: true, hidden: false, globalPct: 50.0),
        ]);
        env.Achievements.SetSummary("game-b",
        [
            CreateAchItem("B_1", "Beta Rare", unlocked: true, hidden: false, globalPct: 3.2),
            CreateAchItem("B_2", "Secret Boss", unlocked: false, hidden: true, globalPct: 12.0),
            CreateAchItem("B_3", "Common Finish", unlocked: false, hidden: false, globalPct: 68.0),
        ]);
        env.Achievements.SetSummary("game-zero", []);

        await env.MainVm.InitializeAsync();

        // Hide 0 achievements filter on sidebar.
        Assert.Equal(3, env.MainVm.AchievementSidebarGames.Count);
        env.MainVm.HideZeroAchievementGames = true;
        Assert.Equal(2, env.MainVm.AchievementSidebarGames.Count);
        Assert.DoesNotContain(env.MainVm.AchievementSidebarGames, g => g.GameId == "game-zero");

        // Select game B in Achievements Center -> tab counts & row spoiler cache.
        var sidebarB = env.MainVm.AchievementSidebarGames.Single(g => g.GameId == "game-b");
        env.MainVm.SelectAchievementSidebarGameCommand.Execute(sidebarB);
        await WaitUntilAsync(() => env.MainVm.CurrentAchievementSummary?.GameId == "game-b");

        Assert.True(sidebarB.IsSelected);
        Assert.Equal("全部 (3)", env.MainVm.AchievementTabAllLabel);
        Assert.Equal("已解锁 (1)", env.MainVm.AchievementTabUnlockedLabel);
        Assert.Equal("未解锁 (2)", env.MainVm.AchievementTabLockedLabel);
        Assert.Equal("隐藏成就 (1)", env.MainVm.AchievementTabHiddenLabel);

        // Reveal single row spoiler -> filter/sort -> verify same instance & spoiler state preserved!
        var secretRow = env.MainVm.FilteredAchievements.Single(r => r.ApiName == "B_2");
        secretRow.RevealRowSpoilerCommand.Execute(null);
        Assert.True(secretRow.IsRowSpoilerRevealed);

        env.MainVm.SelectedAchievementSortMode = AchievementSortMode.RarityAsc;
        Assert.Equal("B_1", env.MainVm.FilteredAchievements[0].ApiName);
        var secretAfterSort = env.MainVm.FilteredAchievements.Single(r => r.ApiName == "B_2");
        Assert.Same(secretRow, secretAfterSort);
        Assert.True(secretAfterSort.IsRowSpoilerRevealed);

        // Cross-game switch bug fix: go to Library (select game A), then switch back to Achievements -> reloads game B!
        env.MainVm.AchievementSearchText = "Secret";
        env.MainVm.SelectLibraryGameCommand.Execute(env.MainVm.AllGames.Single(g => g.GameId == "game-a"));
        await WaitUntilAsync(() => env.MainVm.CurrentAchievementSummary?.GameId == "game-a");
        Assert.Equal(string.Empty, env.MainVm.AchievementSearchText);

        env.MainVm.ActiveSection = PrimaryNavSection.Achievements;
        await WaitUntilAsync(() => env.MainVm.CurrentAchievementSummary?.GameId == "game-b");
        Assert.Equal("game-b", env.MainVm.CurrentAchievementSummary?.GameId);
    }

    [Fact]
    public async Task SettingsUpgrades_ThemeSystem_Dnd_AutoStart_ManualImport_HotAudio_AndAutoDefenderProbe()
    {
        bool? appliedAutoStart = null;
        await using var env = await TestShellHarness.CreateAsync(
            detectSystemTheme: () => SpawnUiThemeKind.Dark,
            applyAutoStart: enabled => appliedAutoStart = enabled);

        await env.MainVm.InitializeAsync();

        // 1. Follow Windows System theme.
        env.SettingsVm.SelectedUiThemeMode = UiThemePreferenceMode.System;
        Assert.Equal(SpawnUiThemeKind.Dark, env.SettingsVm.SelectedUiTheme);
        await WaitUntilAsync(async () =>
            string.Equals(await env.Db.GetSettingAsync(AppSettingKeys.UiTheme), "System", StringComparison.OrdinalIgnoreCase));

        // 2. Overlay DND + Achievement Language + Auto-start + Window placement round-trip.
        env.SettingsVm.OverlayEnabled = false;
        env.SettingsVm.SelectedAchievementLanguage = "english";
        env.SettingsVm.StartWithWindowsMinimizedToTray = true;
        Assert.True(appliedAutoStart);
        await env.SettingsVm.SaveWindowPlacementAsync(new WindowPlacementDto(120, 80, 1280, 800, IsMaximized: false));
        await env.SettingsVm.MarkFirstMinimizeToTrayNotifiedAsync();

        // Steam sound status only visible for SteamAuthentic; dragging volume auto-unmutes.
        Assert.True(env.SettingsVm.ShowSteamSoundStatus);
        env.SettingsVm.SelectedOverlaySoundPreset = OverlaySoundPreset.ThemeSynthesized;
        Assert.False(env.SettingsVm.ShowSteamSoundStatus);
        env.SettingsVm.OverlayMuteSound = true;
        Assert.Equal(0.45, env.SettingsVm.OverlayVolumeSliderOpacity, precision: 2);
        env.SettingsVm.OverlayVolumePercent = 65;
        Assert.False(env.SettingsVm.OverlayMuteSound);
        Assert.Equal(1.0, env.SettingsVm.OverlayVolumeSliderOpacity, precision: 2);

        // 3. Manual import failure preserves path; success stays on Settings & enables quick jump.
        env.MainVm.ActiveSection = PrimaryNavSection.Settings;
        env.SettingsVm.ManualGameImportPathText = @"Z:\NonExistent\GamePath_404";
        await env.SettingsVm.ImportManualGamePathAsync();
        Assert.Equal(@"Z:\NonExistent\GamePath_404", env.SettingsVm.ManualGameImportPathText);
        Assert.True(env.MainVm.IsStatusBannerError);

        string importDir = env.CreateFakeGameDirectory("ImportedTitle", "Imported.exe");
        env.Library.RegisterImportPath(importDir, new GameRecord
        {
            GameId = "game-imp",
            Title = "ImportedTitle",
            InstallDir = importDir,
            ExecutableRelPath = "Imported.exe",
            PlatformSource = PlatformSource.Standalone,
        });
        env.SettingsVm.ManualGameImportPathText = importDir;
        await env.SettingsVm.ImportManualGamePathAsync();
        Assert.Equal(PrimaryNavSection.Settings, env.MainVm.ActiveSection);
        Assert.Equal(string.Empty, env.SettingsVm.ManualGameImportPathText);
        Assert.True(env.SettingsVm.HasLastImportedGame);
        Assert.Equal("game-imp", env.SettingsVm.LastImportedGameId);

        env.SettingsVm.OpenLastImportedGameCommand.Execute(null);
        Assert.Equal(PrimaryNavSection.Library, env.MainVm.ActiveSection);
        Assert.Equal("game-imp", env.MainVm.SelectedGame?.GameId);

        // 4. Hot-apply trainer audio policy & Vanguard safety notification.
        int safetyNotified = 0;
        env.SettingsVm.TrainerSafetySettingsChanged += () => safetyNotified++;
        env.SettingsVm.SelectedTrainerAudioMode = TrainerAudioMode.Passthrough;
        await WaitUntilAsync(() => env.AudioController.ApplyPolicyCallCount >= 1);
        env.SettingsVm.TrainerStrictVanguardMode = true;
        Assert.True(safetyNotified >= 1);

        // 5. Auto silent read-only Defender probe when entering TrainerAndSandbox category.
        env.Defender.NextReport = new DefenderExclusionStatusReport
        {
            Status = DefenderExclusionStatus.TamperProtectionBlocked,
            SandboxPath = env.SettingsVm.EffectiveTrainerSandboxRoot,
            IsExcluded = false,
            Message = "Tamper protection active",
        };
        env.SettingsVm.SelectedCategory = SettingsCategory.TrainerAndSandbox;
        await WaitUntilAsync(() => env.SettingsVm.DefenderStatusReport is not null);
        Assert.Equal(1, env.Defender.InspectCallCount);
        Assert.True(env.SettingsVm.ShowDefenderManualGuidance);
        Assert.True(env.SettingsVm.IsDefenderNotExcluded);

        // 6. Non-ASCII sandbox path detection.
        env.SettingsVm.AppliedCustomTrainerSandboxRoot = @"C:\玩家目录\Trainers";
        Assert.True(env.SettingsVm.HasNonAsciiTrainerSandboxPath);
        env.SettingsVm.AppliedCustomTrainerSandboxRoot = @"C:\SpawnSandbox\Trainers";
        Assert.False(env.SettingsVm.HasNonAsciiTrainerSandboxPath);

        // 7. Re-initializing a fresh SettingsViewModel restores all persisted values cleanly.
        using var reloadedSettings = new SettingsViewModel(
            env.Paths,
            env.Db,
            env.SteamKeyProvider,
            env.OverlayIpc,
            env.Guard,
            env.ThemeManager,
            detectSystemTheme: () => SpawnUiThemeKind.Dark,
            applyAutoStart: _ => { },
            volumeDebounceMs: 0);
        await reloadedSettings.InitializeAsync();
        Assert.Equal(UiThemePreferenceMode.System, reloadedSettings.SelectedUiThemeMode);
        Assert.Equal(SpawnUiThemeKind.Dark, reloadedSettings.SelectedUiTheme);
        Assert.False(reloadedSettings.OverlayEnabled);
        Assert.Equal("english", reloadedSettings.SelectedAchievementLanguage);
        Assert.True(reloadedSettings.StartWithWindowsMinimizedToTray);
        Assert.True(reloadedSettings.HasShownFirstMinimizeToTrayNotice);
        Assert.NotNull(reloadedSettings.SavedWindowPlacement);
        Assert.Equal(1280, reloadedSettings.SavedWindowPlacement!.Width);
    }

    [Fact]
    public async Task NarrowDrawerAutoCloses_WindowBoundsClamp_AndMaxButtonHitTest()
    {
        await using var env = await TestShellHarness.CreateAsync();
        await env.SeedGameAsync(new GameRecord
        {
            GameId = "g-1",
            Title = "Single",
            InstallDir = env.CreateFakeGameDirectory("Single", "s.exe"),
            ExecutableRelPath = "s.exe",
            PlatformSource = PlatformSource.Standalone,
        });
        await env.MainVm.InitializeAsync();
        var item = env.MainVm.AllGames.Single();
        env.MainVm.SelectLibraryGameCommand.Execute(item);

        // Narrow window switches from docked to overlay drawer.
        env.MainVm.UpdateViewportWidth(900);
        Assert.True(env.MainVm.IsNarrowWindow);
        Assert.False(env.MainVm.IsDockedSidebarVisible);
        Assert.False(env.MainVm.IsDrawerSidebarOpen);

        env.MainVm.ToggleNarrowSidebarCommand.Execute(null);
        Assert.True(env.MainVm.IsDrawerSidebarOpen);

        // Re-selecting the ALREADY selected game still auto-closes the drawer!
        env.MainVm.SelectLibraryGameCommand.Execute(item);
        Assert.False(env.MainVm.IsDrawerSidebarOpen);

        // Switching Settings category auto-closes the drawer too.
        env.MainVm.ToggleNarrowSidebarCommand.Execute(null);
        Assert.True(env.MainVm.IsDrawerSidebarOpen);
        env.SettingsVm.SelectedCategory = SettingsCategory.AppearanceTheme;
        Assert.False(env.MainVm.IsDrawerSidebarOpen);

        // Window placement clamping handles off-screen / disconnected monitor coordinates cleanly.
        var primaryWorkArea = new Rect(0, 0, 1920, 1040);
        var clamped = MainWindow.ClampWindowPlacementToWorkArea(
            new WindowPlacementDto(Left: 3200, Top: -500, Width: 2400, Height: 400, IsMaximized: true),
            primaryWorkArea,
            minWidth: 760,
            minHeight: 560);
        Assert.Equal(0, clamped.Left);
        Assert.Equal(0, clamped.Top);
        Assert.Equal(1920, clamped.Width);
        Assert.Equal(560, clamped.Height);
        Assert.True(clamped.IsMaximized);

        // Win11 HTMAXBUTTON hit-test bounds.
        Assert.True(MainWindow.IsPointInsideMaximizeButton(new Point(23, 18), new Size(46, 36), isVisible: true));
        Assert.False(MainWindow.IsPointInsideMaximizeButton(new Point(-1, 18), new Size(46, 36), isVisible: true));
        Assert.False(MainWindow.IsPointInsideMaximizeButton(new Point(23, 18), new Size(46, 36), isVisible: false));
    }

    [Fact]
    public void MainWindow_HeadlessSta_ResolvesImplicitDarkLightComboBoxCheckBoxAndNamedControls()
    {
        Exception? staException = null;
        var staThread = new Thread(() =>
        {
            try
            {
                using var env = TestShellHarness.CreateAsync().GetAwaiter().GetResult();
                using var tray = new TrayIconService();
                var window = new MainWindow(env.MainVm, tray, () => { });
                try
                {
                    Assert.NotNull(window.FindName("MaximizeRestoreButton"));
                    Assert.NotNull(window.FindName("LibrarySidebarSearchTextBox"));
                    Assert.NotNull(window.FindName("AchievementSidebarSearchTextBox"));
                    Assert.NotNull(window.FindName("GuardRailToggle"));
                    Assert.NotNull(window.FindName("SettingsContentScrollViewer"));
                    Assert.NotNull(window.FindName("SteamApiKeyPasswordBox"));

                    var comboStyle = window.TryFindResource(typeof(ComboBox)) as Style;
                    var checkStyle = window.TryFindResource(typeof(CheckBox)) as Style;
                    Assert.NotNull(comboStyle);
                    Assert.NotNull(checkStyle);
                }
                finally
                {
                    window.PrepareForApplicationExit();
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(60)), "STA headless test timed out.");
        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers & Isolated Fakes
    // -------------------------------------------------------------------------

    private static GameAchievementItemDto CreateAchItem(
        string apiName,
        string displayName,
        bool unlocked,
        bool hidden,
        double? globalPct) =>
        new()
        {
            Definition = new AchievementDefinitionDto
            {
                ApiName = apiName,
                DisplayName = displayName,
                Description = $"Description for {displayName}",
                HiddenSpoilerDescription = hidden ? $"Spoiler for {displayName}" : null,
                IsHidden = hidden,
                GlobalPercent = globalPct,
            },
            Unlock = unlocked
                ? new AchievementUnlockDto
                {
                    GameId = "test",
                    ApiName = apiName,
                    IsUnlocked = true,
                    UnlockedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                }
                : null,
        };

    private static TrainerCatalogRecord CreateTrainerRecord(string trainerId, string gameId) =>
        new()
        {
            TrainerId = trainerId,
            GameId = gameId,
            DisplayName = "FLiNG Test Trainer",
            SandboxRelativePath = $"{gameId}\\trainer.exe",
            FileSha256 = new string('A', 64),
            FileSize = 1024,
            ImportedAtUtc = DateTimeOffset.UtcNow,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition was not satisfied in time.");
            }

            await Task.Delay(15);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Async condition was not satisfied in time.");
            }

            await Task.Delay(15);
        }
    }

    private sealed class TestShellHarness : IAsyncDisposable, IDisposable
    {
        public string TempRoot { get; }
        public SpawnPaths Paths { get; }
        public SpawnDatabase Db { get; }
        public InMemorySpawnEventBus EventBus { get; } = new();
        public ThemeManagerService ThemeManager { get; } = new();
        public FakeSteamApiKeyProvider SteamKeyProvider { get; } = new();
        public FakeOverlayIpcClient OverlayIpc { get; } = new();
        public FakeAntiCheatGuardService Guard { get; } = new();
        public FakeGameLibraryService Library { get; }
        public FakeGameLaunchService Launch { get; } = new();
        public FakeAchievementService Achievements { get; } = new();
        public FakeGameDataService GameData { get; } = new();
        public FakeTrainerCatalogService TrainerCatalog { get; } = new();
        public FakeTrainerRuntimeService TrainerRuntime { get; } = new();
        public FakeTrainerSafetyGate TrainerSafety { get; } = new();
        public FakeSaveTimeMachineService SaveService { get; } = new();
        public FakeDefenderAssistant Defender { get; } = new();
        public FakeTrainerAudioController AudioController { get; } = new();
        public SettingsViewModel SettingsVm { get; }
        public TrainerWorkbenchViewModel TrainerVm { get; }
        public MainWindowViewModel MainVm { get; }
        public string? LastCopiedClipboardText { get; private set; }

        private TestShellHarness(
            string tempRoot,
            SpawnPaths paths,
            SpawnDatabase db,
            Func<SpawnUiThemeKind>? detectSystemTheme,
            Action<bool>? applyAutoStart)
        {
            TempRoot = tempRoot;
            Paths = paths;
            Db = db;
            Library = new FakeGameLibraryService(db, EventBus);

            SettingsVm = new SettingsViewModel(
                paths,
                db,
                SteamKeyProvider,
                OverlayIpc,
                Guard,
                ThemeManager,
                defenderAssistant: Defender,
                trainerAudioController: AudioController,
                openExternalUrl: _ => { },
                trainerRuntime: TrainerRuntime,
                saveService: SaveService,
                detectSystemTheme: detectSystemTheme,
                applyAutoStart: applyAutoStart ?? (_ => { }),
                volumeDebounceMs: 0);

            TrainerVm = new TrainerWorkbenchViewModel(
                paths,
                db,
                TrainerCatalog,
                TrainerRuntime,
                new FakePreTrainerSnapshotService(),
                TrainerSafety,
                Guard,
                Library,
                Launch,
                openExternalUrl: _ => { },
                copyToClipboard: txt => LastCopiedClipboardText = txt);

            MainVm = new MainWindowViewModel(
                paths,
                db,
                Library,
                Launch,
                Achievements,
                GameData,
                Guard,
                OverlayIpc,
                EventBus,
                TrainerCatalog,
                TrainerRuntime,
                TrainerSafety,
                SettingsVm,
                TrainerVm,
                openExternalUrl: _ => { },
                saveTimeMachineService: SaveService,
                copyToClipboard: txt => LastCopiedClipboardText = txt,
                achievementSearchDebounceMs: 0);
        }

        public static async Task<TestShellHarness> CreateAsync(
            Func<SpawnUiThemeKind>? detectSystemTheme = null,
            Action<bool>? applyAutoStart = null)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-shell-ux-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            var paths = new SpawnPaths(explicitDataDir: Path.Combine(tempRoot, "data"), explicitIdentity: "spawn-dev-test");
            paths.EnsureDirectoriesExist();
            var db = new SpawnDatabase(paths);
            await db.InitializeAsync();
            return new TestShellHarness(tempRoot, paths, db, detectSystemTheme, applyAutoStart);
        }

        public string CreateFakeGameDirectory(string name, string exeName)
        {
            string dir = Path.Combine(TempRoot, "games", name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, exeName), [0x4D, 0x5A]);
            return dir;
        }

        public Task SeedGameAsync(GameRecord record) => Db.UpsertGameAsync(record);

        public void Dispose()
        {
            MainVm.Dispose();
            Db.Dispose();
            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSteamApiKeyProvider : ISteamApiKeyProvider
    {
        private string? _custom;

        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_custom);

        public ValueTask<SteamApiKeyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(string.IsNullOrWhiteSpace(_custom)
                ? new SteamApiKeyStatusDto(false, "未配置", null)
                : new SteamApiKeyStatusDto(true, "自定义加密密钥", "****" + _custom[^4..]));

        public ValueTask SaveCustomApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
        {
            _custom = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOverlayIpcClient : IOverlayIpcClient
    {
        public string PipeName => "spawn-test-pipe";
        public Task<bool> SendToastAsync(OverlayToastRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<int> SendToastBatchAsync(IReadOnlyList<OverlayToastRequest> requests, CancellationToken cancellationToken = default) => Task.FromResult(requests.Count);
        public Task<OverlayHeartbeatReply?> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult<OverlayHeartbeatReply?>(null);
        public Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAntiCheatGuardService : IAntiCheatGuardService
    {
        private AntiCheatGuardSnapshot _snap = new(AntiCheatGuardPhase.IdleSafe, false, 0, null, []);
        public AntiCheatGuardSnapshot CurrentSnapshot => _snap;
        public event EventHandler<AntiCheatGuardSnapshot>? StateChanged;

        public void SetSnapshot(AntiCheatGuardSnapshot snap)
        {
            _snap = snap;
            StateChanged?.Invoke(this, snap);
        }

        public AntiCheatInspectionResult InspectGameDirectory(string installDir) =>
            new(AntiCheatTier.Tier0, null, []);

        public HighRiskEnvironmentReport InspectHighRiskEnvironment() =>
            HighRiskEnvironmentReport.Clean;

        public void EnsureHighRiskOperationPermitted(AntiCheatTier targetGameTier, string operationName) { }
        public void NotifyProtectedGameStarted(string gameId, AntiCheatTier tier) { }
        public Task NotifyProtectedGameExitedAsync(string gameId, AntiCheatTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGameLibraryService(SpawnDatabase db, InMemorySpawnEventBus eventBus) : IGameLibraryService
    {
        private readonly Dictionary<string, GameRecord> _importByPath = new(StringComparer.OrdinalIgnoreCase);
        public int UpdateMetadataCallCount { get; private set; }

        public void RegisterImportPath(string path, GameRecord record) =>
            _importByPath[Path.GetFullPath(path)] = record;

        public Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default) =>
            db.GetAllGamesAsync(cancellationToken);

        public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) =>
            db.GetGameByIdAsync(gameId, cancellationToken);

        public async Task<GameRecord?> ImportGameAsync(string directoryOrExePath, CancellationToken cancellationToken = default)
        {
            string full = Path.GetFullPath(directoryOrExePath);
            if (!_importByPath.TryGetValue(full, out var rec))
            {
                return null;
            }

            await db.UpsertGameAsync(rec, cancellationToken);
            await eventBus.PublishAsync(new GameImportedOrRescannedEvent(rec, IsNewImport: true, DateTimeOffset.UtcNow), cancellationToken);
            return rec;
        }

        public Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameRecord>>([]);

        public Task<GameRecord?> RescanGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            db.GetGameByIdAsync(gameId, cancellationToken);

        public async Task<GameRecord?> UpdateGameMetadataAsync(string gameId, GameMetadataUpdate update, CancellationToken cancellationToken = default)
        {
            UpdateMetadataCallCount++;
            var existing = await db.GetGameByIdAsync(gameId, cancellationToken);
            if (existing is null)
            {
                return null;
            }

            var updated = existing with
            {
                Title = !string.IsNullOrWhiteSpace(update.Title) ? update.Title.Trim() : existing.Title,
                SortTitle = update.SortTitle ?? existing.SortTitle,
                ExecutableRelPath = !string.IsNullOrWhiteSpace(update.ExecutableRelPath) ? update.ExecutableRelPath.Trim() : existing.ExecutableRelPath,
                LaunchArguments = update.LaunchArguments ?? existing.LaunchArguments,
                SteamAppId = update.SteamAppId ?? existing.SteamAppId,
                GogGameId = update.GogGameId ?? existing.GogGameId,
                CoverImagePath = update.CoverImagePath ?? existing.CoverImagePath,
                IconPath = update.IconPath ?? existing.IconPath,
            };
            await db.UpsertGameAsync(updated, cancellationToken);
            return updated;
        }

        public Task<bool> RemoveGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            db.DeleteGameAsync(gameId, cancellationToken);
    }

    private sealed class FakeGameLaunchService : IGameLaunchService
    {
        private readonly Dictionary<string, ActiveGameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions => _sessions;
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged;
        public int StopCallCount { get; private set; }

        public void SetSession(string gameId, ActiveGameSession? session)
        {
            if (session is null)
            {
                _sessions.Remove(gameId);
                SessionChanged?.Invoke(this, new ActiveGameSessionChangedEvent(gameId, null, PlaySessionActivityState.Stopped));
            }
            else
            {
                _sessions[gameId] = session;
                SessionChanged?.Invoke(this, new ActiveGameSessionChangedEvent(gameId, session, session.State));
            }
        }

        public ActiveGameSession? GetActiveSession(string gameId) =>
            _sessions.TryGetValue(gameId, out var s) ? s : null;

        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default)
        {
            var session = new ActiveGameSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                GameId = gameId,
                GameTitle = gameId,
                InstallDir = Path.GetTempPath(),
                State = PlaySessionActivityState.Active,
                StartedUtc = DateTimeOffset.UtcNow,
            };
            _sessions[gameId] = session;
            return Task.FromResult(new GameLaunchResult { Succeeded = true, Session = session });
        }

        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            _sessions.Remove(gameId);
            return Task.FromResult(true);
        }

        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }

    private sealed class FakeAchievementService : IAchievementService
    {
        private readonly Dictionary<string, IReadOnlyList<GameAchievementItemDto>> _itemsByGame = new(StringComparer.OrdinalIgnoreCase);
        public int ResetCallCount { get; private set; }
        public int ActivateGbeCallCount { get; private set; }
        public int RestoreDllCallCount { get; private set; }
        public int SyncSchemaCallCount { get; private set; }

        public event EventHandler<AchievementUnlockedNotification>? AchievementUnlocked { add { } remove { } }

        public void SetSummary(string gameId, IReadOnlyList<GameAchievementItemDto> items) =>
            _itemsByGame[gameId] = items;

        public Task<GameAchievementSummaryDto> GetGameAchievementsAsync(string gameId, CancellationToken cancellationToken = default)
        {
            _itemsByGame.TryGetValue(gameId, out var items);
            var list = items ?? [];
            return Task.FromResult(new GameAchievementSummaryDto
            {
                GameId = gameId,
                ChannelStatusText = "就绪",
                UnlockedCount = list.Count(static x => x.IsUnlocked),
                TotalCount = list.Count,
                Items = list,
            });
        }

        public Task<IReadOnlyList<AchievementUnlockDto>> GetRecentUnlocksAsync(int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AchievementUnlockDto>>([]);

        public Task<GameAchievementSummaryDto> SyncGameSchemaAsync(string gameId, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            SyncSchemaCallCount++;
            return GetGameAchievementsAsync(gameId, cancellationToken);
        }

        public Task<int> ScanLocalUnlocksAsync(string gameId, bool silentSeed = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<bool> SetAchievementUnlockedAsync(string gameId, string apiName, bool unlocked, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<GbeForkActivationResult> ActivateGbeForkAsync(string gameId, CancellationToken cancellationToken = default)
        {
            ActivateGbeCallCount++;
            return Task.FromResult(new GbeForkActivationResult { Succeeded = true, Message = "ok" });
        }

        public Task<GbeForkActivationResult> RestoreOriginalDllAsync(string gameId, CancellationToken cancellationToken = default)
        {
            RestoreDllCallCount++;
            return Task.FromResult(new GbeForkActivationResult { Succeeded = true, Message = "ok" });
        }

        public Task SendTestToastAsync(string? gameId = null, OverlayThemeKind? themeOverride = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetGameAchievementStateAsync(string gameId, CancellationToken cancellationToken = default)
        {
            ResetCallCount++;
            return Task.CompletedTask;
        }

        public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGameDataService : IGameDataManagementService
    {
        private readonly Dictionary<string, IReadOnlyList<GameDataEntryDto>> _entriesByGame = new(StringComparer.OrdinalIgnoreCase);
        public int CleanCallCount { get; private set; }
        public bool NextSpawnStateReset { get; set; } = true;

        public void SetEntries(string gameId, IReadOnlyList<GameDataEntryDto> entries) =>
            _entriesByGame[gameId] = entries;

        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(string gameId, bool refreshOnline = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entriesByGame.TryGetValue(gameId, out var list) ? list : []);

        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default)
        {
            CleanCallCount++;
            return Task.FromResult(new GameDataCleanResult
            {
                Succeeded = true,
                Message = "cleaned",
                SpawnStateReset = request.ResetSpawnAchievementsAndPlaytime && NextSpawnStateReset,
            });
        }

        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);

        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDataCleanResult { Succeeded = true, Message = "restored" });
    }

    private sealed class FakeTrainerCatalogService : ITrainerCatalogService
    {
        private readonly Dictionary<string, IReadOnlyList<TrainerCatalogRecord>> _byGame = new(StringComparer.OrdinalIgnoreCase);
        public void SetTrainers(string gameId, IReadOnlyList<TrainerCatalogRecord> list) => _byGame[gameId] = list;

        public Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerMatchResult { Succeeded = true, GameId = gameId, EffectiveQuery = userSearchOverride ?? gameId });

        public Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false, Message = "stub" });

        public Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false, Message = "stub" });

        public Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult(new TrainerIntegrityReport { TrainerId = trainerId, IsValid = true });

        public Task RemoveAsync(string trainerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TrainerCatalogRecord>> ListForGameAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult(_byGame.TryGetValue(gameId, out var list) ? list : []);
    }

    private sealed class FakeTrainerRuntimeService : ITrainerRuntimeService
    {
        public TrainerRuntimeState State => TrainerRuntimeState.Unbound;
        public TrainerSessionHandle? ActiveSession => null;
        public event EventHandler<TrainerRuntimeStateChanged>? StateChanged { add { } remove { } }

        public Task<TrainerLaunchOutcome> LaunchAsync(string trainerId, TrainerLaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new TrainerLaunchOutcome { Succeeded = false, Message = "stub" });

        public Task TriggerHotkeyAsync(string sessionId, string entryId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOriginalWindowVisibleAsync(string sessionId, bool visible, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeTrainerSafetyGate : ITrainerSafetyGate
    {
        public bool IsWatchdogActive => false;
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered { add { } remove { } }

        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport { EffectiveTier = game.AntiCheatTier });

        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveAntiCheatProbeReport());

        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default) =>
            Task.FromResult(new VerifiedOfflineBypass { GameId = game.GameId });

        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default) =>
            Task.FromResult(new TrainerAllowanceDecision { IsAllowed = true, Outcome = TrainerAllowanceOutcome.Allowed, Message = "allowed" });

        public void NotifyTrainerSessionStarted(string sessionId, string gameId) { }
        public void NotifyTrainerSessionEnded(string sessionId) { }
    }

    private sealed class FakePreTrainerSnapshotService : IPreTrainerSaveSnapshotService
    {
        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerSnapshotResult { Succeeded = true, Status = PreTrainerSnapshotStatus.Skipped });

        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>([]);

        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerRestoreResult { Succeeded = true });
    }

    private sealed class FakeSaveTimeMachineService : ISaveTimeMachineService
    {
        private readonly Dictionary<string, SaveTreeGraphDto> _graphs = new(StringComparer.OrdinalIgnoreCase);
        public int OverviewCallCount { get; private set; }
        public event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged { add { } remove { } }

        public void SetGraph(string gameId, int branchCount, int nodeCount)
        {
            var branches = Enumerable.Range(0, branchCount)
                .Select(i => new SaveBranchRecord
                {
                    BranchId = $"b-{i}",
                    GameId = gameId,
                    Name = i == 0 ? "main" : $"branch-{i}",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                })
                .ToList();
            var nodes = Enumerable.Range(0, nodeCount)
                .Select(i => new SaveTreeNodeDto
                {
                    Snapshot = new SaveSnapshotRecord
                    {
                        SnapshotId = $"s-{i}",
                        GameId = gameId,
                        BranchId = branches[0].BranchId,
                        CreatedMachineId = "test-machine",
                        TriggerKind = SaveSnapshotTriggerKind.Manual,
                    },
                    BranchName = branches[0].Name,
                })
                .ToList();
            _graphs[gameId] = new SaveTreeGraphDto
            {
                GameId = gameId,
                Branches = branches,
                Nodes = nodes,
            };
        }

        public Task<SaveGameStateRecord> GetGameStateAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult(new SaveGameStateRecord { GameId = gameId });

        public Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default) =>
            Task.FromResult(state);

        public Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult(new SaveSettingsDto());

        public Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult(_graphs.TryGetValue(gameId, out var g) ? g : new SaveTreeGraphDto { GameId = gameId });

        public Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default)
        {
            OverviewCallCount++;
            return Task.FromResult<IReadOnlyList<SaveLibraryGameOverviewDto>>([]);
        }

        public Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SaveSnapshotFileRecord>>([]);

        public Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default) =>
            Task.FromResult(new CasVaultStatsDto());

        public Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(string leftSnapshotId, string rightSnapshotId, CancellationToken ct = default) =>
            Task.FromResult(new SaveSnapshotDiffDto { BaseLabel = leftSnapshotId, TargetLabel = rightSnapshotId });

        public Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new SaveSnapshotDiffDto { BaseLabel = snapshotId, TargetLabel = "disk" });

        public Task<CreateSaveSnapshotResult> CreateSnapshotAsync(CreateSaveSnapshotRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CreateSaveSnapshotResult { Succeeded = true, Skipped = true, Message = "skipped" });

        public Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SaveRestoreOutcome { Succeeded = true, Message = "ok" });

        public Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ForkBranchResult { Succeeded = true, Message = "ok" });

        public Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SaveRestoreOutcome { Succeeded = true, Message = "ok" });

        public Task<SaveBranchRecord?> RenameBranchAsync(string branchId, string newName, string? description = null, string? colorHex = null, CancellationToken ct = default) =>
            Task.FromResult<SaveBranchRecord?>(null);

        public Task<DeleteBranchResult> DeleteBranchAsync(string branchId, bool deleteSnapshots = false, CancellationToken ct = default) =>
            Task.FromResult(new DeleteBranchResult(true, "ok", 0));

        public Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new DeleteSnapshotResult(true, "ok", 0));

        public Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(string snapshotId, string targetDirectory, CancellationToken ct = default) =>
            Task.FromResult(new ExportSnapshotResult(true, "ok", targetDirectory, 0, 0));

        public Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult(new LegacyPreTrainerImportReport(0, 0, 0, [], []));

        public Task<SaveGcReport> RunCasGarbageCollectionAsync(TimeSpan? gracePeriodOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new SaveGcReport(0, 0, 0, 0));
    }

    private sealed class FakeDefenderAssistant : IDefenderExclusionAssistant
    {
        public int InspectCallCount { get; private set; }
        public DefenderExclusionStatusReport NextReport { get; set; } = new()
        {
            Status = DefenderExclusionStatus.Unknown,
            SandboxPath = string.Empty,
            IsExcluded = false,
            Message = "not excluded",
        };

        public Task<DefenderExclusionStatusReport> InspectStatusAsync(string sandboxPath, CancellationToken ct = default)
        {
            InspectCallCount++;
            return Task.FromResult(NextReport with { SandboxPath = sandboxPath });
        }

        public Task<DefenderExclusionStatusReport> RequestExclusionAsync(string sandboxPath, CancellationToken ct = default) =>
            Task.FromResult(new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.AddedAndVerified,
                SandboxPath = sandboxPath,
                IsExcluded = true,
                Message = "excluded",
            });
    }

    private sealed class FakeTrainerAudioController : ITrainerAudioController
    {
        public int ApplyPolicyCallCount { get; private set; }
        public TrainerAudioPolicy? LastPolicy { get; private set; }
        public event Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>? FallbackRequired { add { } remove { } }

        public Task PrepareAsync(TrainerAudioPolicy policy, string sandboxRoot, CancellationToken ct = default) => Task.CompletedTask;
        public void TrackProcess(int pid) { }

        public Task ApplyPolicyAsync(TrainerAudioPolicy policy, CancellationToken ct = default)
        {
            ApplyPolicyCallCount++;
            LastPolicy = policy;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
