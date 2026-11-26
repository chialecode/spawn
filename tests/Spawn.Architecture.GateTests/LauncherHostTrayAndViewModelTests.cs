using System.IO;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Host.Helpers;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Headless zero-window unit tests covering <c>Spawn.Host</c> row-level ViewModels,
/// background async image loading, default selection bug fix, and system tray runtime UX.
/// </summary>
public sealed class LauncherHostTrayAndViewModelTests
{
    [Fact]
    public void GameDataItemViewModel_DefaultSelection_OnlySelectsSafeExistingDefaultEntries()
    {
        int selectionChanges = 0;

        GameDataItemViewModel Make(bool isSafe, bool exists, bool defaultSelected, GameDataCategory category = GameDataCategory.SaveData) =>
            new(
                new GameDataEntryDto
                {
                    EntryId = Guid.NewGuid().ToString("N"),
                    GameId = "game-1",
                    Category = category,
                    SourceRule = GameDataSourceRule.NativeRule,
                    PathOrRegistryKey = @"C:\Synthetic\Saves\slot0.sav",
                    Exists = exists,
                    IsSafeToClean = isSafe,
                    DefaultSelected = defaultSelected,
                },
                static _ => { },
                static _ => { },
                () => selectionChanges++);

        // 1. Safe + Exists + DefaultSelected => checked
        var safeExistingSave = Make(isSafe: true, exists: true, defaultSelected: true, GameDataCategory.SaveData);
        Assert.True(safeExistingSave.IsSelected);

        // 2. Safe + Exists + DefaultSelected=false (e.g. ConfigAndLogs) => NOT checked!
        var existingConfig = Make(isSafe: true, exists: true, defaultSelected: false, GameDataCategory.ConfigAndLogs);
        Assert.False(existingConfig.IsSelected);

        // 3. Safe + !Exists + DefaultSelected=true (path not yet created on disk) => NOT checked!
        var missingSave = Make(isSafe: true, exists: false, defaultSelected: true, GameDataCategory.SaveData);
        Assert.False(missingSave.IsSelected);

        // 4. !Safe + Exists + DefaultSelected=true => NOT checked, and manual check attempt reverts to false!
        var unsafeEntry = Make(isSafe: false, exists: true, defaultSelected: true, GameDataCategory.SaveData);
        Assert.False(unsafeEntry.IsSelected);
        unsafeEntry.IsSelected = true;
        Assert.False(unsafeEntry.IsSelected);
        Assert.Equal(0, selectionChanges);
    }

    [Fact]
    public async Task GameListItemViewModel_PosterVsFallbackIcon_AsyncDecode_LiveStates_AndTriStateChannelDot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-gamelist-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string posterPath = Path.Combine(tempDir, "poster_600x900.png");
            string iconPath = Path.Combine(tempDir, "icon.png");
            await File.WriteAllBytesAsync(posterPath, CreateSolidPngBytes(60, 90, SKColors.SteelBlue));
            await File.WriteAllBytesAsync(iconPath, CreateSolidPngBytes(48, 48, SKColors.Orange));

            // 1. Dedicated poster cover => HasPosterCover == true, HasFallbackIcon == false, unplayed says "尚未游玩".
            var posterRecord = new GameRecord
            {
                GameId = "game-poster",
                Title = "Poster Title",
                InstallDir = tempDir,
                ExecutableRelPath = "game.exe",
                CoverImagePath = posterPath,
                IconPath = iconPath,
                AchievementCaptureMode = AchievementCaptureMode.SteamEmulator,
            };

            var posterVm = new GameListItemViewModel(posterRecord);
            await posterVm.PendingArtworkLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(posterVm.CoverImage);
            Assert.NotNull(posterVm.SmallIcon);
            Assert.True(posterVm.HasPosterCover);
            Assert.False(posterVm.HasFallbackIcon);
            Assert.False(posterVm.HasEverPlayed);
            Assert.Equal("尚未游玩", posterVm.PlaytimeSummaryText);
            Assert.Equal("尚未游玩", posterVm.LastPlayedSummaryText);
            Assert.Equal(AchievementChannelDotState.Ready, posterVm.ChannelDotState);
            Assert.True(posterVm.IsChannelReady);
            Assert.False(posterVm.IsChannelPendingPatch);
            Assert.False(posterVm.IsChannelNone);
            Assert.Equal("#10B981", posterVm.ChannelDotHexColor);

            // 2. Icon-only fallback cover => HasPosterCover == false, HasFallbackIcon == true.
            var iconOnlyRecord = posterRecord with
            {
                GameId = "game-icon-only",
                CoverImagePath = iconPath,
                HeroImagePath = null,
                IconPath = iconPath,
                AchievementCaptureMode = AchievementCaptureMode.PendingGbeForkActivation,
            };

            var iconOnlyVm = new GameListItemViewModel(iconOnlyRecord);
            await iconOnlyVm.PendingArtworkLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(iconOnlyVm.CoverImage);
            Assert.False(iconOnlyVm.HasPosterCover);
            Assert.True(iconOnlyVm.HasFallbackIcon);
            Assert.Equal(AchievementChannelDotState.PendingGbeFork, iconOnlyVm.ChannelDotState);
            Assert.True(iconOnlyVm.IsChannelPendingPatch);
            Assert.Equal("#F59E0B", iconOnlyVm.ChannelDotHexColor);

            // 3. No achievement channel => None (muted gray).
            iconOnlyVm.UpdateRecord(iconOnlyRecord with { AchievementCaptureMode = AchievementCaptureMode.None });
            Assert.Equal(AchievementChannelDotState.None, iconOnlyVm.ChannelDotState);
            Assert.True(iconOnlyVm.IsChannelNone);
            Assert.Equal("#9CA3AF", iconOnlyVm.ChannelDotHexColor);

            // 4. Real-time session activity states & current session duration.
            var started = DateTimeOffset.UtcNow.AddMinutes(-2);
            posterVm.UpdateSession(new ActiveGameSession
            {
                SessionId = "sess-1",
                GameId = posterRecord.GameId,
                GameTitle = posterRecord.Title,
                InstallDir = posterRecord.InstallDir,
                State = PlaySessionActivityState.ShellBreakingWatch,
                StartedUtc = started,
            });
            Assert.True(posterVm.IsRunning);
            Assert.Equal("等待游戏主进程", posterVm.LiveActivityStateText);
            Assert.Equal("等待游戏主进程", posterVm.LastPlayedSummaryText);

            posterVm.UpdateSession(new ActiveGameSession
            {
                SessionId = "sess-1",
                GameId = posterRecord.GameId,
                GameTitle = posterRecord.Title,
                InstallDir = posterRecord.InstallDir,
                State = PlaySessionActivityState.Active,
                StartedUtc = started,
                SessionActiveSec = 65,
                SessionBackgroundSec = 10,
                SessionAfkSec = 5,
            });
            Assert.Equal("有效游玩中", posterVm.LiveActivityStateText);
            Assert.Equal(80, posterVm.CurrentSessionTotalSec);
            Assert.Equal("本次 00:01:20", posterVm.CurrentSessionDurationText);
            Assert.Equal("有效游玩中 · 本次 00:01:20", posterVm.LiveSessionBadgeText);
            Assert.Equal("有效游玩中 · 本次 00:01:20", posterVm.LastPlayedSummaryText);
            Assert.Contains("有效游玩 00:01:05", posterVm.PlaytimeSummaryText, StringComparison.Ordinal);

            posterVm.UpdateSession(posterVm.ActiveSession! with { State = PlaySessionActivityState.Background });
            Assert.Equal("后台切出", posterVm.LiveActivityStateText);
            Assert.Contains("后台切出", posterVm.LastPlayedSummaryText, StringComparison.Ordinal);

            posterVm.UpdateSession(posterVm.ActiveSession! with { State = PlaySessionActivityState.Afk });
            Assert.Equal("挂机静默", posterVm.LiveActivityStateText);
            Assert.Contains("挂机静默", posterVm.LastPlayedSummaryText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GameListItemViewModel_InPlaceArtworkOverwriteAndDeletion_RefreshesCachedBitmapsOnUpdateRecord()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-artwork-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string iconPath = Path.Combine(tempDir, "icon.png");
            await File.WriteAllBytesAsync(iconPath, CreateSolidPngBytes(32, 32, SKColors.DodgerBlue));

            var record = new GameRecord
            {
                GameId = "game-inplace-refresh",
                Title = "InPlace Artwork Refresh",
                InstallDir = tempDir,
                ExecutableRelPath = "game.exe",
                CoverImagePath = iconPath,
                IconPath = iconPath,
            };

            var vm = new GameListItemViewModel(record);
            await vm.PendingArtworkLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            var initialCover = vm.CoverImage;
            var initialSmall = vm.SmallIcon;
            Assert.NotNull(initialCover);
            Assert.NotNull(initialSmall);

            // 1. Overwrite icon.png in-place with an upgraded 64x64 HD image and advance timestamp.
            await File.WriteAllBytesAsync(iconPath, CreateSolidPngBytes(64, 64, SKColors.Crimson));
            File.SetLastWriteTimeUtc(iconPath, DateTime.UtcNow.AddSeconds(5));

            vm.UpdateRecord(record);
            await vm.PendingArtworkLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(vm.CoverImage);
            Assert.NotNull(vm.SmallIcon);
            Assert.NotSame(initialCover, vm.CoverImage);
            Assert.NotSame(initialSmall, vm.SmallIcon);

            // Sample center pixel of updated CoverImage to verify Crimson pixels replaced DodgerBlue.
            byte[] pixel = new byte[4];
            vm.CoverImage!.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.True(pixel[2] > pixel[0] + 100, $"Expected updated Crimson pixel (R >> B), got B={pixel[0]}, G={pixel[1]}, R={pixel[2]}.");

            // 2. Delete icon.png on disk -> UpdateRecord clears cached bitmaps instead of keeping stale ghost images.
            File.Delete(iconPath);
            vm.UpdateRecord(record);
            await vm.PendingArtworkLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(vm.CoverImage);
            Assert.Null(vm.SmallIcon);
            Assert.False(vm.HasPosterCover);
            Assert.False(vm.HasFallbackIcon);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AchievementRowViewModel_LazyAsyncIcon_ToggleSpoiler_Progress_RarePercentDedup_AndChannelDot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-achrow-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string colorIcon = Path.Combine(tempDir, "ach_color.png");
            string grayIcon = Path.Combine(tempDir, "ach_gray.png");
            await File.WriteAllBytesAsync(colorIcon, CreateSolidPngBytes(64, 64, SKColors.Gold));
            await File.WriteAllBytesAsync(grayIcon, CreateSolidPngBytes(64, 64, SKColors.Gray));

            // 1. Hidden + Rare + Numeric Progress locked achievement
            var rareHiddenItem = new GameAchievementItemDto
            {
                Definition = new AchievementDefinitionDto
                {
                    ApiName = "ACH_SECRET_100",
                    DisplayName = "百步穿杨",
                    Description = "累计击败 100 名隐藏首领",
                    IsHidden = true,
                    GlobalPercent = 3.5,
                    IconPath = colorIcon,
                    IconGrayPath = grayIcon,
                },
                Unlock = new AchievementUnlockDto
                {
                    GameId = "game-1",
                    ApiName = "ACH_SECRET_100",
                    IsUnlocked = false,
                    CurrentProgress = 45,
                    MaxProgress = 100,
                },
            };

            var row = new AchievementRowViewModel(
                "game-1",
                "Test Game",
                rareHiddenItem,
                globalRevealSpoilers: false,
                static (_, _) => Task.CompletedTask,
                static _ => Task.CompletedTask,
                channelCaptureMode: AchievementCaptureMode.GbeForkActivated);

            // Lazy loading: constructor MUST NOT touch disk / start decoding until IconImage is bound or awaited!
            Assert.False(row.IsIconLoadStarted);

            // Trigger lazy async icon load and await deterministically.
            _ = row.IconImage;
            Assert.True(row.IsIconLoadStarted);
            await row.EnsureIconLoadedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(row.IconImage);
            Assert.Equal(0.92, row.IconOpacity, precision: 2);

            // Rare achievement deduplicates percentage: RareBadgeText shows "稀有 3.5%", HasGlobalPercent is false & GlobalPercentText is empty!
            Assert.True(row.IsRare);
            Assert.Equal("稀有 3.5%", row.RareBadgeText);
            Assert.False(row.HasGlobalPercent);
            Assert.Equal(string.Empty, row.GlobalPercentText);
            Assert.Equal(3.5, row.GlobalPercent);

            // Numeric progress exposure ("45 / 100", 0.45)
            Assert.True(row.HasNumericProgress);
            Assert.Equal(0.45, row.ProgressFraction, precision: 3);
            Assert.Equal("45 / 100", row.ProgressLabel);

            // Single-row spoiler reveal & collapse toggle
            Assert.True(row.CanClickToRevealSpoiler);
            Assert.False(row.CanCollapseRowSpoiler);
            Assert.Contains("隐藏成就", row.EffectiveDescription, StringComparison.Ordinal);

            row.ToggleRowSpoilerCommand.Execute(null);
            Assert.True(row.IsRowSpoilerRevealed);
            Assert.False(row.CanClickToRevealSpoiler);
            Assert.True(row.CanCollapseRowSpoiler);
            Assert.Equal("累计击败 100 名隐藏首领", row.EffectiveDescription);

            row.ToggleRowSpoilerCommand.Execute(null);
            Assert.False(row.IsRowSpoilerRevealed);
            Assert.True(row.CanClickToRevealSpoiler);
            Assert.False(row.CanCollapseRowSpoiler);

            // Tri-state channel dot: Ready
            Assert.Equal(AchievementChannelDotState.Ready, row.ChannelDotState);
            Assert.Equal("#10B981", row.ChannelDotHexColor);

            // 2. Non-rare unlocked achievement with no numeric progress
            var normalUnlocked = new GameAchievementItemDto
            {
                Definition = new AchievementDefinitionDto
                {
                    ApiName = "ACH_COMMON",
                    DisplayName = "初出茅庐",
                    Description = "完成教学关卡",
                    IsHidden = false,
                    GlobalPercent = 64.2,
                    IconPath = colorIcon,
                },
                Unlock = new AchievementUnlockDto
                {
                    GameId = "game-1",
                    ApiName = "ACH_COMMON",
                    IsUnlocked = true,
                },
            };

            var normalRow = new AchievementRowViewModel(
                "game-1",
                "Test Game",
                normalUnlocked,
                globalRevealSpoilers: false,
                static (_, _) => Task.CompletedTask,
                static _ => Task.CompletedTask,
                channelCaptureMode: AchievementCaptureMode.PendingGbeForkActivation);

            Assert.False(normalRow.IsRare);
            Assert.True(normalRow.HasGlobalPercent);
            Assert.Equal("全球 64.2%", normalRow.GlobalPercentText);
            Assert.False(normalRow.HasNumericProgress);
            Assert.Equal(0.0, normalRow.ProgressFraction);
            Assert.Equal(string.Empty, normalRow.ProgressLabel);
            Assert.Equal(AchievementChannelDotState.PendingGbeFork, normalRow.ChannelDotState);
            Assert.Equal("#F59E0B", normalRow.ChannelDotHexColor);

            await normalRow.EnsureIconLoadedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(normalRow.IconImage);
            Assert.Equal(1.0, normalRow.IconOpacity, precision: 2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void TrayIconService_DevVsReleaseMenuItems_RuntimeSummary_AndActionCallbacks()
    {
        // 1. Dev channel hides invalid "更新设置…" and keeps debug "测试跳杯弹窗".
        using var devTray = new TrayIconService(isDevChannelOverride: true, forceHeadless: true);
        var devMenu = devTray.BuildContextMenuItems();
        Assert.DoesNotContain(devMenu, m => m.CommandId == TrayIconService.CmdOpenUpdateSettings);
        Assert.Contains(devMenu, m => m.CommandId == TrayIconService.CmdTestToast);

        // 2. Release channel includes "更新设置…" and removes low-frequency debug "测试跳杯弹窗".
        using var releaseTray = new TrayIconService(isDevChannelOverride: false, forceHeadless: true);
        var releaseMenu = releaseTray.BuildContextMenuItems();
        Assert.Contains(releaseMenu, m => m.CommandId == TrayIconService.CmdOpenUpdateSettings);
        Assert.DoesNotContain(releaseMenu, m => m.CommandId == TrayIconService.CmdTestToast);

        // 3. Dynamic runtime summary updates tooltip & context menu + triggers StopTrainer / QuickLaunch / StopGame hooks.
        bool stopTrainerFired = false;
        string? quickLaunchedGameId = null;
        string? stoppedGameId = null;
        releaseTray.StopActiveTrainerRequested += () => stopTrainerFired = true;
        releaseTray.QuickLaunchGameRequested += id => quickLaunchedGameId = id;
        releaseTray.StopRunningGameRequested += id => stoppedGameId = id;

        releaseTray.UpdateRuntimeStatus(new TrayRuntimeStatusSnapshot
        {
            RunningGameId = "game-hk",
            RunningGameTitle = "Hollow Knight",
            RunningGameState = PlaySessionActivityState.Active,
            ActiveTrainerName = "Hollow Knight FLiNG +12",
            RecentGames =
            [
                new TrayRecentGameEntry("game-hk", "Hollow Knight"),
                new TrayRecentGameEntry("game-celeste", "Celeste"),
                new TrayRecentGameEntry("game-limbo", "LIMBO"),
            ],
        });

        Assert.Contains("Hollow Knight", releaseTray.CurrentTooltip, StringComparison.Ordinal);
        Assert.Contains("修改器运行中", releaseTray.CurrentTooltip, StringComparison.Ordinal);

        var activeMenu = releaseTray.BuildContextMenuItems();
        var runningHeader = Assert.Single(activeMenu, m => m.IsDisabled && m.Text.Contains("运行中：Hollow Knight", StringComparison.Ordinal));
        Assert.True(runningHeader.IsDisabled);

        var stopTrainerItem = Assert.Single(activeMenu, m => m.CommandId == TrayIconService.CmdStopActiveTrainer);
        Assert.Contains("Hollow Knight FLiNG +12", stopTrainerItem.Text, StringComparison.Ordinal);

        var stopGameItem = Assert.Single(activeMenu, m => m.CommandId == TrayIconService.CmdStopRunningGame);
        Assert.Equal("game-hk", stopGameItem.Tag);

        // Quick-launch items exclude the currently running game!
        var quickItems = activeMenu.Where(m => m.CommandId >= TrayIconService.CmdQuickLaunchBase).ToList();
        Assert.Equal(2, quickItems.Count);
        Assert.Equal("game-celeste", quickItems[0].Tag);
        Assert.Equal("game-limbo", quickItems[1].Tag);

        Assert.True(releaseTray.DispatchMenuCommand(stopTrainerItem.CommandId));
        Assert.True(stopTrainerFired);

        Assert.True(releaseTray.DispatchMenuCommand(quickItems[0].CommandId));
        Assert.Equal("game-celeste", quickLaunchedGameId);

        Assert.True(releaseTray.DispatchMenuCommand(stopGameItem.CommandId));
        Assert.Equal("game-hk", stoppedGameId);

        // 4. Anti-cheat cooldown surfaces in tooltip & menu and suppresses quick-launch shortcuts.
        releaseTray.UpdateRuntimeStatus(new TrayRuntimeStatusSnapshot
        {
            AntiCheatPhase = AntiCheatGuardPhase.CooldownLocked,
            AntiCheatCooldownSeconds = 11.2,
            RecentGames = [new TrayRecentGameEntry("game-celeste", "Celeste")],
        });

        Assert.Contains("反作弊冷却中 (12s)", releaseTray.CurrentTooltip, StringComparison.Ordinal);
        var cooldownMenu = releaseTray.BuildContextMenuItems();
        Assert.Contains(cooldownMenu, m => m.IsDisabled && m.Text.Contains("反作弊冷却中", StringComparison.Ordinal));
        Assert.DoesNotContain(cooldownMenu, m => m.CommandId >= TrayIconService.CmdQuickLaunchBase);
    }

    [Fact]
    public async Task AsyncArtworkAndAchievementIcon_DoNotPostBackToNonWpfSynchronizationContext()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-nonsyncctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string iconPath = Path.Combine(tempDir, "icon.png");
            string achPath = Path.Combine(tempDir, "ach.png");
            await File.WriteAllBytesAsync(iconPath, CreateSolidPngBytes(48, 48, SKColors.MediumSeaGreen));
            await File.WriteAllBytesAsync(achPath, CreateSolidPngBytes(48, 48, SKColors.Gold));

            var blockingContext = new NeverPostSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;

            GameListItemViewModel gameVm;
            AchievementRowViewModel achVm;
            try
            {
                SynchronizationContext.SetSynchronizationContext(blockingContext);
                gameVm = new GameListItemViewModel(new GameRecord
                {
                    GameId = "game-syncctx-test",
                    Title = "SyncContext Isolation",
                    InstallDir = tempDir,
                    ExecutableRelPath = "game.exe",
                    CoverImagePath = iconPath,
                    IconPath = iconPath,
                });

                achVm = new AchievementRowViewModel(
                    "game-syncctx-test",
                    "SyncContext Isolation",
                    new GameAchievementItemDto
                    {
                        Definition = new AchievementDefinitionDto
                        {
                            ApiName = "ACH_SYNC_CTX",
                            DisplayName = "No Starvation",
                            IconPath = achPath,
                        },
                        Unlock = new AchievementUnlockDto
                        {
                            GameId = "game-syncctx-test",
                            ApiName = "ACH_SYNC_CTX",
                            IsUnlocked = true,
                        },
                    },
                    globalRevealSpoilers: false,
                    static (_, _) => Task.CompletedTask,
                    static _ => Task.CompletedTask);

                _ = achVm.IconImage;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            await gameVm.PendingArtworkLoadTask.WaitAsync(TimeSpan.FromSeconds(10));
            await achVm.EnsureIconLoadedAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, blockingContext.PostCount);
            Assert.NotNull(gameVm.CoverImage);
            Assert.NotNull(gameVm.SmallIcon);
            Assert.NotNull(achVm.IconImage);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class NeverPostSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            // Intentionally drop callback to simulate a starved non-WPF SynchronizationContext.
        }
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
