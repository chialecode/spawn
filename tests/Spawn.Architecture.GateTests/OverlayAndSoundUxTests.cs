using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Services;
using Spawn.OverlayHost.Audio;
using Spawn.OverlayHost.Interop;
using Spawn.OverlayHost.Services;
using Spawn.OverlayHost.Views;
using Xunit;
using Path = System.IO.Path;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Offline, zero-focus, zero-audio unit tests covering Overlay visual/layout fixes, fullscreen monitor placement,
/// queue congestion governance, preview replacement, OverlayHostProcessManager cold-start diagnostics,
/// and SynthesizedUnlockSoundPlayer rare/platinum enhancement, WAV validation, and Steam re-probe behavior.
/// </summary>
[Collection(WpfStaHeadlessCollection.Name)]
public sealed class OverlayAndSoundUxTests
{
    [Fact]
    public void OverlayToastWindow_XboxTheme_NonRareUsesGreenPaletteWithoutPurpleDiamond_AndRareShowsPurpleDiamond()
    {
        RunOnSta(() =>
        {
            using var silentPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: true);
            var window = new OverlayToastWindow(silentPlayer);
            try
            {
                // 1. Standard non-rare Xbox achievement: green palette everywhere, purple diamond pill collapsed.
                window.PrepareForHeadlessRender(new OverlayToastRequest
                {
                    GameTitle = "LIMBO",
                    AchievementApiName = "ACH_COMMON",
                    DisplayName = "方向错误",
                    Description = "这是不对的。",
                    Theme = OverlayThemeKind.XboxRare,
                    IsRare = false,
                    GlobalPercent = 54.2,
                    MuteSound = true,
                    SoundVolume = 0.0,
                });

                var xboxCard = Assert.IsType<Border>(window.FindName("XboxCard"));
                var xboxIconBorder = Assert.IsType<Border>(window.FindName("XboxIconBorder"));
                var xboxFallback = Assert.IsType<System.Windows.Shapes.Path>(window.FindName("XboxFallbackGlyph"));
                var xboxHeader = Assert.IsType<TextBlock>(window.FindName("XboxHeaderText"));
                var xboxDiamond = Assert.IsType<Border>(window.FindName("XboxDiamondBadge"));

                Assert.Equal(Visibility.Visible, xboxCard.Visibility);
                Assert.Equal(Visibility.Collapsed, xboxDiamond.Visibility);

                Color cardBorder = Assert.IsType<SolidColorBrush>(xboxCard.BorderBrush).Color;
                Color iconBorder = Assert.IsType<SolidColorBrush>(xboxIconBorder.BorderBrush).Color;
                Color iconBg = Assert.IsType<SolidColorBrush>(xboxIconBorder.Background).Color;
                Color fallbackFill = Assert.IsType<SolidColorBrush>(xboxFallback.Fill).Color;
                Color headerFg = Assert.IsType<SolidColorBrush>(xboxHeader.Foreground).Color;

                AssertGreenDominant(cardBorder, "Xbox non-rare card border");
                AssertGreenDominant(iconBorder, "Xbox non-rare icon border");
                AssertGreenDominant(iconBg, "Xbox non-rare icon background");
                AssertGreenDominant(fallbackFill, "Xbox non-rare fallback glyph");
                AssertGreenDominant(headerFg, "Xbox non-rare header foreground");

                // 2. Rare (<10%) Xbox achievement: purple border/circle + visible diamond rarity badge.
                window.PrepareForHeadlessRender(new OverlayToastRequest
                {
                    GameTitle = "LIMBO",
                    AchievementApiName = "ACH_RARE",
                    DisplayName = "叮当声！",
                    Description = "收集隐藏彩蛋并通关",
                    Theme = OverlayThemeKind.XboxRare,
                    IsRare = false,
                    GlobalPercent = 3.2,
                    MuteSound = true,
                    SoundVolume = 0.0,
                });

                Assert.Equal(Visibility.Visible, xboxDiamond.Visibility);
                var percentText = Assert.IsType<TextBlock>(window.FindName("XboxPercentText"));
                Assert.Equal("3.2%", percentText.Text);

                Color rareBorder = Assert.IsType<SolidColorBrush>(xboxCard.BorderBrush).Color;
                Color rareIconBorder = Assert.IsType<SolidColorBrush>(xboxIconBorder.BorderBrush).Color;
                AssertPurpleDominant(rareBorder, "Xbox rare card border");
                AssertPurpleDominant(rareIconBorder, "Xbox rare icon border");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OverlayToastWindow_SteamHeaderGridEllipsisAndTwoLineDescription_NeverPushesRareBadgeOffscreen()
    {
        RunOnSta(() =>
        {
            using var silentPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: true);
            var window = new OverlayToastWindow(silentPlayer);
            try
            {
                string veryLongTitle = "The Elder Scrolls V: Skyrim Anniversary Definitive Ultra Director's Cut Extended Edition (2026)";
                string twoLineDesc = "在不死亡超过五次且不触发任何隐藏机关的前提下连续完成全部核心章节并开启最终秘境终点大门";

                window.PrepareForHeadlessRender(new OverlayToastRequest
                {
                    GameTitle = veryLongTitle,
                    AchievementApiName = "ACH_LONG",
                    DisplayName = "超长标题成就验证测试条目名称不会撑破布局",
                    Description = twoLineDesc,
                    Theme = OverlayThemeKind.Steam,
                    IsRare = true,
                    GlobalPercent = 1.8,
                    MuteSound = true,
                    SoundVolume = 0.0,
                });

                var stage = window.VisualStage;
                const double renderW = 404;
                const double renderH = 112;
                stage.Measure(new Size(renderW, renderH));
                stage.Arrange(new Rect(0, 0, renderW, renderH));
                stage.UpdateLayout();

                var steamHeader = Assert.IsType<TextBlock>(window.FindName("SteamHeaderText"));
                var steamRareBadge = Assert.IsType<Border>(window.FindName("SteamRareBadge"));
                var steamTitle = Assert.IsType<TextBlock>(window.FindName("SteamTitleText"));
                var steamDesc = Assert.IsType<TextBlock>(window.FindName("SteamDescText"));
                var ps5Grade = Assert.IsType<TextBlock>(window.FindName("Ps5GradeLabel"));
                var ps5Desc = Assert.IsType<TextBlock>(window.FindName("Ps5DescText"));
                var xboxHeader = Assert.IsType<TextBlock>(window.FindName("XboxHeaderText"));
                var xboxDesc = Assert.IsType<TextBlock>(window.FindName("XboxDescText"));

                // Steam header container must be a Grid(*, Auto) rather than a DockPanel so long titles shrink cleanly.
                var headerGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(steamHeader));
                Assert.Equal(2, headerGrid.ColumnDefinitions.Count);
                Assert.True(headerGrid.ColumnDefinitions[0].Width.IsStar);
                Assert.True(headerGrid.ColumnDefinitions[1].Width.IsAuto);

                // All headers and titles must trim with CharacterEllipsis.
                Assert.Equal(TextTrimming.CharacterEllipsis, steamHeader.TextTrimming);
                Assert.Equal(TextTrimming.CharacterEllipsis, steamTitle.TextTrimming);
                Assert.Equal(TextTrimming.CharacterEllipsis, ps5Grade.TextTrimming);
                Assert.Equal(TextTrimming.CharacterEllipsis, xboxHeader.TextTrimming);

                // All 3 theme descriptions support 2-line wrap without single-line truncation.
                Assert.Equal(TextWrapping.Wrap, steamDesc.TextWrapping);
                Assert.Equal(TextWrapping.Wrap, ps5Desc.TextWrapping);
                Assert.Equal(TextWrapping.Wrap, xboxDesc.TextWrapping);
                Assert.True(steamDesc.MaxHeight >= 30, $"Expected SteamDesc MaxHeight >= 30 for 2 lines, got {steamDesc.MaxHeight}");
                Assert.True(steamDesc.ActualHeight > 20, $"Expected wrapped description to span 2 lines (>20px), got {steamDesc.ActualHeight}");

                // SteamRareBadge remains completely visible inside the card bounds despite the 95-char game title.
                Assert.Equal(Visibility.Visible, steamRareBadge.Visibility);
                Assert.True(steamRareBadge.ActualWidth >= 40, $"Expected SteamRareBadge ActualWidth >= 40, got {steamRareBadge.ActualWidth}");
                Point badgeTopRight = steamRareBadge.TransformToAncestor(stage).Transform(new Point(steamRareBadge.ActualWidth, 0));
                Assert.True(badgeTopRight.X <= renderW, $"SteamRareBadge overflowed stage width ({badgeTopRight.X} > {renderW}).");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task OverlayToastWindow_EntranceStartsAtZeroOpacity_AndOffUiThreadDecodeEliminatesSkiaPngReencode()
    {
        // 1. Source guard: OverlayToastWindow.xaml.cs must not re-encode loaded images back to PNG via Skia.
        string repoRoot = LocateRepositoryRoot();
        string csPath = Path.Combine(repoRoot, "src", "Spawn.OverlayHost", "Views", "OverlayToastWindow.xaml.cs");
        string source = await File.ReadAllTextAsync(csPath, Encoding.UTF8);
        Assert.DoesNotContain("SKEncodedImageFormat.Png", source, StringComparison.Ordinal);

        // 2. Decode a temp PNG on a background thread pool thread and verify frozen cross-thread BitmapSource.
        string tempPng = Path.Combine(Path.GetTempPath(), $"spawn-overlay-icon-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul))
            {
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(new SKColor(40, 140, 240, 255));
                }

                using var img = SKImage.FromBitmap(bmp);
                await File.WriteAllBytesAsync(tempPng, img.Encode(SKEncodedImageFormat.Png, 95).ToArray());
            }

            BitmapSource? decodedOnBg = await Task.Run(() => OverlayToastWindow.LoadBitmapOffUiThread(tempPng));
            Assert.NotNull(decodedOnBg);
            Assert.True(decodedOnBg!.IsFrozen);
            Assert.Equal(64, decodedOnBg.PixelWidth);
            Assert.Equal(64, decodedOnBg.PixelHeight);

            // 3. Entrance preparation keeps Opacity = 0 before animation, while headless static render keeps Opacity = 1.
            RunOnSta(() =>
            {
                using var silentPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: true);
                var window = new OverlayToastWindow(silentPlayer);
                try
                {
                    var req = new OverlayToastRequest
                    {
                        GameTitle = "LIMBO",
                        AchievementApiName = "ACH_1",
                        DisplayName = "首帧防闪烁测试",
                        IconPath = tempPng,
                        Theme = OverlayThemeKind.Steam,
                        MuteSound = true,
                        SoundVolume = 0.0,
                    };

                    window.PrepareForEntranceBeforeAnimation(req, decodedOnBg);
                    Assert.Equal(0.0, window.VisualStage.Opacity, precision: 3);

                    window.PrepareForHeadlessRender(req);
                    Assert.Equal(1.0, window.VisualStage.Opacity, precision: 3);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    [Fact]
    public void OverlayMonitorPlacement_FullscreenForegroundUsesRcMonitor_WindowedAndShellUseRcWork()
    {
        var rcMonitor = new OverlayMonitorPlacement.ScreenRect(0, 0, 2560, 1440);
        var rcWork = new OverlayMonitorPlacement.ScreenRect(0, 0, 2560, 1392); // 48px taskbar at bottom

        // 1. Borderless fullscreen game covering rcMonitor -> selects full rcMonitor (no floating above hidden taskbar).
        var fullscreenGameRect = new OverlayMonitorPlacement.ScreenRect(0, 0, 2560, 1440);
        var fullscreenBounds = OverlayMonitorPlacement.SelectEffectiveBounds(
            rcMonitor, rcWork, fullscreenGameRect, isShellOrDesktopWindow: false, dpiX: 144, dpiY: 144);
        Assert.True(fullscreenBounds.UsesFullscreenMonitorBounds);
        Assert.Equal(1440, fullscreenBounds.HeightPx);
        Assert.Equal(2560, fullscreenBounds.WidthPx);
        Assert.Equal(144u, fullscreenBounds.DpiX);

        // 2. Normal windowed app -> uses taskbar-aware rcWork.
        var windowedRect = new OverlayMonitorPlacement.ScreenRect(120, 80, 1400, 900);
        var windowedBounds = OverlayMonitorPlacement.SelectEffectiveBounds(
            rcMonitor, rcWork, windowedRect, isShellOrDesktopWindow: false, dpiX: 144, dpiY: 144);
        Assert.False(windowedBounds.UsesFullscreenMonitorBounds);
        Assert.Equal(1392, windowedBounds.HeightPx);

        // 3. Desktop shell window (Progman/WorkerW) covering full monitor -> still uses rcWork.
        var shellBounds = OverlayMonitorPlacement.SelectEffectiveBounds(
            rcMonitor, rcWork, fullscreenGameRect, isShellOrDesktopWindow: true, dpiX: 96, dpiY: 96);
        Assert.False(shellBounds.UsesFullscreenMonitorBounds);
        Assert.Equal(1392, shellBounds.HeightPx);
    }

    [Fact]
    public async Task OverlayToastQueueCoordinator_AcceleratesOver3_CollapsesAt8_ReplacesPreviewImmediately_AndClearsQueue()
    {
        // --- Case 1: Backlog > 3 shortens DisplayDurationMs to 2200ms ---
        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented1 = new List<OverlayToastRequest>();
        var all5Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var coord = new OverlayToastQueueCoordinator(async (toast, ct) =>
        {
            if (toast.AchievementApiName == "A1")
            {
                await gate1.Task.WaitAsync(ct);
            }

            lock (presented1)
            {
                presented1.Add(toast);
                if (presented1.Count == 5)
                {
                    all5Done.TrySetResult();
                }
            }
        }, interToastDelayMs: 0))
        {
            coord.Enqueue(MakeToast("A1", durationMs: 4500));
            // Enqueue 4 more while A1 is presenting -> queued backlog == 4 (> 3, < 8)
            coord.EnqueueBatch([MakeToast("A2", 4500), MakeToast("A3", 4500), MakeToast("A4", 4500), MakeToast("A5", 4500)]);
            gate1.TrySetResult();
            await all5Done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Equal(5, presented1.Count);
        Assert.Equal(OverlayToastQueueCoordinator.AcceleratedDisplayDurationMs, presented1[1].DisplayDurationMs);
        Assert.Equal(4500, presented1[4].DurationMs); // backlog dropped back to <= 3

        // --- Case 2: Backlog >= 8 collapses into one summary toast ("已连续解锁 N 项成就") ---
        var collapsedPresented = new List<OverlayToastRequest>();
        var collapsedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var coord = new OverlayToastQueueCoordinator((toast, _) =>
        {
            lock (collapsedPresented)
            {
                collapsedPresented.Add(toast);
                collapsedDone.TrySetResult();
            }

            return Task.CompletedTask;
        }, interToastDelayMs: 0))
        {
            var burst = Enumerable.Range(1, 10)
                .Select(i => MakeToast($"BURST_{i}", 4500, isRare: i == 5, isPlatinum: i == 10))
                .ToList();
            coord.EnqueueBatch(burst);
            await collapsedDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
        }

        Assert.Single(collapsedPresented);
        Assert.Equal(OverlayToastQueueCoordinator.SyntheticBatchSummaryApiName, collapsedPresented[0].AchievementApiName);
        Assert.Equal("已连续解锁 10 项成就", collapsedPresented[0].DisplayName);
        Assert.True(collapsedPresented[0].IsRare);
        Assert.True(collapsedPresented[0].IsPlatinumCompletion);

        // --- Case 3: ReplaceActivePreview immediately aborts active preview and presents latest style ---
        var firstPreviewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPreviewCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPreviewDelivered = new TaskCompletionSource<OverlayToastRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var coord = new OverlayToastQueueCoordinator(async (toast, ct) =>
        {
            if (toast.AchievementApiName == "PREVIEW_OLD")
            {
                firstPreviewStarted.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException)
                {
                    firstPreviewCancelled.TrySetResult();
                    throw;
                }
            }
            else if (toast.AchievementApiName == "PREVIEW_NEW")
            {
                secondPreviewDelivered.TrySetResult(toast);
            }
        }, interToastDelayMs: 0))
        {
            coord.Enqueue(MakeToast("PREVIEW_OLD", 8000, replaceActivePreview: true, theme: OverlayThemeKind.Steam));
            await firstPreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            coord.Enqueue(MakeToast("PREVIEW_NEW", 4500, replaceActivePreview: true, theme: OverlayThemeKind.XboxRare));
            await firstPreviewCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            OverlayToastRequest latest = await secondPreviewDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(OverlayThemeKind.XboxRare, latest.Theme);
        }

        // --- Case 4: ClearQueue immediately aborts active toast and empties remaining queue ---
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int afterClearCount = 0;

        await using (var coord = new OverlayToastQueueCoordinator(async (toast, ct) =>
        {
            if (toast.AchievementApiName == "HOLD_1")
            {
                activeStarted.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException)
                {
                    activeCancelled.TrySetResult();
                    throw;
                }
            }
            else
            {
                Interlocked.Increment(ref afterClearCount);
            }
        }, interToastDelayMs: 0))
        {
            coord.Enqueue(MakeToast("HOLD_1", 8000));
            await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            coord.Enqueue(MakeToast("QUEUED_2", 4500));
            coord.Enqueue(MakeToast("QUEUED_3", 4500));

            int cleared = coord.ClearQueue();
            Assert.Equal(3, cleared);
            await activeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            Assert.Equal(0, coord.PendingToastCount);
            Assert.Equal(0, Volatile.Read(ref afterClearCount));
        }
    }

    [Fact]
    public async Task OverlayHostProcessManager_ColdStartSkipsRedundantSecondPingAndRecordsLastLaunchError()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-overlay-mgr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            var fakeClient = new CountingNullOverlayIpcClient(paths.OverlayPipeName);

            await using var manager = OverlayHostProcessManager.CreateForTesting(
                paths,
                fakeClient,
                forceHeadlessOverride: false,
                overlayExeResolver: () => Path.Combine(tempDir, "Missing.OverlayHost.exe"));

            OverlayHeartbeatReply? reply = await manager.EnsureOverlayRunningAsync();
            Assert.Null(reply);
            // Must NOT perform the old double ping (once outside lock + once inside lock) on cold start.
            Assert.Equal(1, fakeClient.PingCount);
            Assert.False(string.IsNullOrWhiteSpace(manager.LastLaunchError));
            Assert.Contains("Spawn.OverlayHost.exe", manager.LastLaunchError!, StringComparison.OrdinalIgnoreCase);
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
    public async Task SynthesizedUnlockSoundPlayer_RarePlatinumEnhancement_WavValidation_AndSteamReprobe()
    {
        // 1. SteamAuthentic rare/platinum enhancement switch (default ON)
        var commonSteam = MakeToast("COMMON", 4500) with
        {
            SoundPreset = OverlaySoundPreset.SteamAuthentic,
            IsRare = false,
            GlobalPercent = 65.0,
            IsPlatinumCompletion = false,
        };
        var rareSteam = commonSteam with { GlobalPercent = 4.5 };
        var platinumSteam = commonSteam with { IsPlatinumCompletion = true };
        var rareEnhancementOff = rareSteam with { EnhanceRareAndPlatinumSound = false };

        var commonPlan = SynthesizedUnlockSoundPlayer.ResolvePlaybackPlan(commonSteam, hasExternalClip: true);
        Assert.True(commonPlan.UseExternalClip);
        Assert.False(commonPlan.IsEnhancedRareOrPlatinum);

        var rarePlan = SynthesizedUnlockSoundPlayer.ResolvePlaybackPlan(rareSteam, hasExternalClip: true);
        Assert.False(rarePlan.UseExternalClip);
        Assert.True(rarePlan.IsEnhancedRareOrPlatinum);
        Assert.Equal(SynthesizedUnlockSoundPlayer.ChimePreset.RareDiamond, rarePlan.SynthesizedPreset);

        var platPlan = SynthesizedUnlockSoundPlayer.ResolvePlaybackPlan(platinumSteam, hasExternalClip: true);
        Assert.False(platPlan.UseExternalClip);
        Assert.True(platPlan.IsEnhancedRareOrPlatinum);
        Assert.Equal(SynthesizedUnlockSoundPlayer.ChimePreset.PlatinumCelebration, platPlan.SynthesizedPreset);

        var disabledEnhancementPlan = SynthesizedUnlockSoundPlayer.ResolvePlaybackPlan(rareEnhancementOff, hasExternalClip: true);
        Assert.True(disabledEnhancementPlan.UseExternalClip);
        Assert.False(disabledEnhancementPlan.IsEnhancedRareOrPlatinum);

        // 2. ValidateCustomWavFile: accepts PCM & IEEE Float, rejects ADPCM / non-WAV / missing with clear Chinese reason
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-wav-val-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string pcmPath = Path.Combine(tempDir, "valid_pcm.wav");
            await File.WriteAllBytesAsync(pcmPath, SynthesizedUnlockSoundPlayer.SynthesizeWavBytes(
                SynthesizedUnlockSoundPlayer.ChimePreset.SteamCrystal, 0.8f));
            var pcmResult = SynthesizedUnlockSoundPlayer.ValidateCustomWavFile(pcmPath);
            Assert.True(pcmResult.IsValid, pcmResult.Message);
            Assert.Equal(48000, pcmResult.SampleRate);
            Assert.Equal(2, pcmResult.Channels);
            Assert.Equal(16, pcmResult.BitsPerSample);
            Assert.True(pcmResult.DurationSeconds > 0.2);

            string floatPath = Path.Combine(tempDir, "valid_float32.wav");
            await File.WriteAllBytesAsync(floatPath, BuildMinimalIeeeFloat32Wav(48000, 2, 480));
            var floatResult = SynthesizedUnlockSoundPlayer.ValidateCustomWavFile(floatPath);
            Assert.True(floatResult.IsValid, floatResult.Message);
            Assert.Equal(32, floatResult.BitsPerSample);

            string adpcmPath = Path.Combine(tempDir, "unsupported_adpcm.wav");
            await File.WriteAllBytesAsync(adpcmPath, BuildUnsupportedFormatTagWav(formatTag: 0x0002)); // MS ADPCM
            var adpcmResult = SynthesizedUnlockSoundPlayer.ValidateCustomWavFile(adpcmPath);
            Assert.False(adpcmResult.IsValid);
            Assert.Contains("ADPCM", adpcmResult.Message, StringComparison.OrdinalIgnoreCase);

            string fakeMp3Path = Path.Combine(tempDir, "renamed_mp3.wav");
            await File.WriteAllBytesAsync(fakeMp3Path, new byte[64]); // not RIFF WAVE
            var fakeMp3Result = SynthesizedUnlockSoundPlayer.ValidateCustomWavFile(fakeMp3Path);
            Assert.False(fakeMp3Result.IsValid);
            Assert.Contains("RIFF WAVE", fakeMp3Result.Message, StringComparison.OrdinalIgnoreCase);

            var missingResult = SynthesizedUnlockSoundPlayer.ValidateCustomWavFile(Path.Combine(tempDir, "missing.wav"));
            Assert.False(missingResult.IsValid);
            Assert.Contains("找不到", missingResult.Message, StringComparison.Ordinal);

            // 3. Steam probe cache is NOT permanently stuck on absence: re-probes after cooldown or invalidation.
            int probeCalls = 0;
            bool steamInstalledNow = false;
            DateTimeOffset clock = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

            using var player = new SynthesizedUnlockSoundPlayer(
                forceSilent: true,
                steamProbe: () =>
                {
                    probeCalls++;
                    return steamInstalledNow
                        ? new SteamClientSoundProbeResult(true, tempDir, pcmPath, null, "OK")
                        : new SteamClientSoundProbeResult(false, null, null, null, "Not installed");
                },
                utcNowProvider: () => clock,
                missingSteamReprobeInterval: TimeSpan.FromSeconds(30));

            Assert.Null(player.TryResolveDecodedExternalClip(commonSteam));
            Assert.Equal(1, probeCalls);

            // Within 30s cooldown: throttled
            clock = clock.AddSeconds(10);
            Assert.Null(player.TryResolveDecodedExternalClip(commonSteam));
            Assert.Equal(1, probeCalls);

            // Steam gets installed and cooldown elapses -> re-probes automatically and loads clip!
            steamInstalledNow = true;
            clock = clock.AddSeconds(35);
            var loadedClip = player.TryResolveDecodedExternalClip(commonSteam);
            Assert.NotNull(loadedClip);
            Assert.Equal(2, probeCalls);
            Assert.True(loadedClip!.Value.FrameCount > 100);

            // Explicit invalidation forces immediate re-probe on next call
            player.InvalidateSteamSoundCache();
            Assert.NotNull(player.TryResolveDecodedExternalClip(commonSteam));
            Assert.Equal(3, probeCalls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static OverlayToastRequest MakeToast(
        string apiName,
        int durationMs = 4500,
        bool isRare = false,
        bool isPlatinum = false,
        bool replaceActivePreview = false,
        OverlayThemeKind theme = OverlayThemeKind.Steam) =>
        new()
        {
            GameId = "game-1",
            GameTitle = "LIMBO",
            AchievementApiName = apiName,
            DisplayName = $"成就 {apiName}",
            Description = "测试描述",
            Theme = theme,
            IsRare = isRare,
            GlobalPercent = isRare ? 3.5 : 42.0,
            IsPlatinumCompletion = isPlatinum,
            ReplaceActivePreview = replaceActivePreview,
            DurationMs = durationMs,
            MuteSound = true,
            SoundVolume = 0.0,
        };

    private static byte[] BuildMinimalIeeeFloat32Wav(int sampleRate, short channels, int frames)
    {
        int dataBytes = frames * channels * sizeof(float);
        byte[] wav = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + dataBytes);
        "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
        "fmt "u8.CopyTo(wav.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 3); // IEEE Float
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), sampleRate * channels * sizeof(float));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), (short)(channels * sizeof(float)));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), 32);
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataBytes);
        return wav;
    }

    private static byte[] BuildUnsupportedFormatTagWav(ushort formatTag)
    {
        byte[] wav = new byte[64];
        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 56);
        "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
        "fmt "u8.CopyTo(wav.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), formatTag);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), 2);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), 44100);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), 4);
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), 20);
        return wav;
    }

    private static void AssertGreenDominant(Color color, string label)
    {
        Assert.True(
            color.G > color.R + 20 && color.G > color.B,
            $"Expected Xbox green-dominant color for {label}, got #{color.R:X2}{color.G:X2}{color.B:X2}.");
    }

    private static void AssertPurpleDominant(Color color, string label)
    {
        Assert.True(
            color.B > color.G + 30 && color.R > color.G + 15,
            $"Expected Xbox purple-dominant color for {label}, got #{color.R:X2}{color.G:X2}{color.B:X2}.");
    }

    private static void RunOnSta(Action action)
    {
        Exception? staException = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA overlay UX test timed out.");

        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    private static string LocateRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Spawn.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Spawn.sln.");
    }

    private sealed class CountingNullOverlayIpcClient(string pipeName) : IOverlayIpcClient
    {
        private int _pingCount;
        public int PingCount => Volatile.Read(ref _pingCount);
        public string PipeName { get; } = pipeName;

        public Task<bool> SendToastAsync(OverlayToastRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> SendToastBatchAsync(IReadOnlyList<OverlayToastRequest> requests, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<OverlayHeartbeatReply?> PingAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _pingCount);
            return Task.FromResult<OverlayHeartbeatReply?>(null);
        }

        public Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
