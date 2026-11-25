using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Spawn.Contracts;
using Spawn.Host.Controls;
using Spawn.Host.ViewModels;
using Spawn.Host.ViewModels.Saves;
using Spawn.Host.Views.Saves;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Headless zero-window verification of <see cref="SaveWorkbenchViewModel"/> commands (snapshot, fork from
/// historical node, branch switch, running-game rollback block, pin/unpin, note edit, diff, cloud divergence
/// branch preservation) and STA <see cref="RenderTargetBitmap"/> rendering of <see cref="SaveGraphCellControl"/>
/// and <see cref="SaveWorkbenchControl"/> in both Light and Dark themes.
/// </summary>
[Collection(WpfStaHeadlessCollection.Name)]
public sealed class SaveWorkbenchUiTests
{
    [Fact]
    public async Task SaveWorkbenchViewModel_EndToEndBranchingRollbackDiffAndCloudDivergence_WorksHeadlessly()
    {
        var fakeLaunch = new FakeSaveGameLaunchService();
        var fakeSaves = FakeSaveTimeMachineService.CreateWithRichBranchingTree("elden-ring");
        var fakeCloud = new FakeCloudSaveSyncService(fakeSaves, triggerDivergence: true);
        var fakeWatcher = new FakeSaveWatcherService();
        var fakeResolver = new FakeLudusaviRuleResolver();

        var vm = new SaveWorkbenchViewModel(
            fakeLaunch,
            fakeSaves,
            fakeCloud,
            fakeWatcher,
            fakeResolver,
            openFolder: _ => { },
            pickExportFolder: () => Path.GetTempPath());

        var game = new GameListItemViewModel(new GameRecord
        {
            GameId = "elden-ring",
            Title = "ELDEN RING",
            InstallDir = @"C:\Games\EldenRing",
            ExecutableRelPath = "eldenring.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        });

        // 1. Load rich multi-branch save tree
        await vm.LoadForGameAsync(game);
        Assert.True(vm.HasSnapshots);
        Assert.Equal(4, vm.TotalBranchCount);
        Assert.Equal(7, vm.TotalSnapshotCount);
        Assert.Equal("main", vm.ActiveBranchNameText);
        Assert.NotNull(vm.SelectedNode);
        Assert.True(vm.SelectedNode!.IsHead);
        Assert.True(vm.HasSelectedNodeFiles);
        Assert.Contains(vm.SelectedNodeFiles, f => f.IsSaveCategory);
        Assert.Contains(vm.SelectedNodeFiles, f => f.IsConfigCategory);
        Assert.Contains(vm.SelectedNodeFiles, f => f.IsRegistryCategory);

        // 2. Create manual pinned snapshot
        vm.QuickSnapshotNote = "决战艾尔登之兽前";
        vm.PinNewSnapshot = true;
        await vm.CreateSnapshotAsync();
        Assert.Equal(8, vm.TotalSnapshotCount);
        Assert.Equal("决战艾尔登之兽前", vm.SelectedNode!.Note);
        Assert.True(vm.SelectedNode.IsPinned);
        Assert.True(vm.SelectedNode.IsHead);

        // 3. Fork a brand new branch from an OLDER historical node (s2)
        SaveSnapshotRowViewModel olderNode = vm.GraphRows.Single(r => r.SnapshotId == "snap-00000002");
        vm.SelectNodeCommand.Execute(olderNode);
        vm.OpenForkPopoverFromSelectedCommand.Execute(null);
        Assert.True(vm.IsForkPanelOpen);
        vm.ForkBranchNameInput = "rl1-challenge";
        vm.SelectForkColorCommand.Execute(SaveBranchColors.Palette[4]);
        vm.SwitchToForkedBranch = true;
        await vm.ConfirmForkBranchAsync();
        Assert.False(vm.IsForkPanelOpen);
        Assert.Equal(5, vm.TotalBranchCount);
        Assert.Equal("rl1-challenge", vm.ActiveBranchNameText);

        // 4. Running game MUST block rollback
        vm.UpdateSelectedGameRunningState(true);
        Assert.False(vm.CanRollbackSelected);
        int restoreCallsBefore = fakeSaves.RollbackCalls;
        await vm.ConfirmRollbackSelectedAsync();
        Assert.Equal(restoreCallsBefore, fakeSaves.RollbackCalls);

        // 5. Stopping game unlocks rollback & creates PreRollbackSafety
        vm.UpdateSelectedGameRunningState(false);
        Assert.True(vm.CanRollbackSelected);
        vm.SelectNodeCommand.Execute(vm.GraphRows.Single(r => r.SnapshotId == "snap-00000003"));
        await vm.ConfirmRollbackSelectedAsync();
        Assert.Equal(restoreCallsBefore + 1, fakeSaves.RollbackCalls);

        // 6. Toggle pin & edit note
        SaveSnapshotRowViewModel editTarget = vm.GraphRows.Single(r => r.SnapshotId == "snap-00000004");
        vm.SelectNodeCommand.Execute(editTarget);
        bool wasPinned = editTarget.IsPinned;
        await vm.TogglePinSelectedAsync();
        Assert.Equal(!wasPinned, vm.SelectedNode!.IsPinned);

        vm.BeginEditNoteCommand.Execute(null);
        vm.EditNoteInput = "女武神无伤前 · 更新备注";
        await vm.SaveEditNoteAsync();
        Assert.Equal("女武神无伤前 · 更新备注", vm.SelectedNode!.Note);

        // 7. Inspector Diff tab (Parent vs Current Disk)
        vm.InspectorTab = SaveInspectorTab.Diff;
        await Task.Delay(30);
        Assert.True(vm.HasSelectedNodeDiffItems);
        vm.DiffTargetMode = SaveDiffTargetMode.CurrentDisk;
        await Task.Delay(30);
        Assert.Contains("当前磁盘", vm.DiffSummaryText, StringComparison.Ordinal);

        // 8. Cloud Sync Plan detects TrueDivergence -> opens Divergence Modal -> non-destructive branch resolution
        await vm.PlanCloudSyncAsync();
        Assert.True(vm.IsDivergenceModalOpen);
        Assert.True(vm.HasUnresolvedCloudConflict);

        await vm.ResolveDivergenceAdoptRemoteAsync();
        Assert.False(vm.IsDivergenceModalOpen);
        Assert.False(vm.HasUnresolvedCloudConflict);
        Assert.Equal(CloudDivergenceResolution.AdoptRemoteAndBackupLocal, fakeCloud.LastResolutionChoice);
        Assert.Contains(vm.Branches, b => b.Name.Contains("Backup", StringComparison.OrdinalIgnoreCase));

        // 9. Narrow window (< 740 DIP) compact layout switches inspector to overlay
        vm.UpdateViewportWidth(680);
        Assert.True(vm.IsCompactLayout);
        Assert.False(vm.ShowDesktopInspector);
        vm.SelectNodeCommand.Execute(vm.GraphRows[0]);
        Assert.True(vm.ShowCompactInspectorOverlay);
        vm.CloseCompactInspectorCommand.Execute(null);
        Assert.False(vm.ShowCompactInspectorOverlay);
    }

    [Fact]
    public void SaveWorkbenchControlAndGraphCell_StaHeadlessRender_RendersDistinctLightAndDarkThemesWithBranchBeziers()
    {
        string? optionalRenderOutputDir = Environment.GetEnvironmentVariable("SPAWN_TEST_RENDER_DIR");

        Exception? staException = null;
        var staThread = new Thread(() =>
        {
            try
            {
                var fakeLaunch = new FakeSaveGameLaunchService();
                var fakeSaves = FakeSaveTimeMachineService.CreateWithRichBranchingTree("elden-ring");
                var fakeCloud = new FakeCloudSaveSyncService(fakeSaves, triggerDivergence: false);
                var fakeWatcher = new FakeSaveWatcherService();
                var fakeResolver = new FakeLudusaviRuleResolver();

                var vm = new SaveWorkbenchViewModel(
                    fakeLaunch,
                    fakeSaves,
                    fakeCloud,
                    fakeWatcher,
                    fakeResolver,
                    openFolder: _ => { },
                    pickExportFolder: () => null);

                var game = new GameListItemViewModel(new GameRecord
                {
                    GameId = "elden-ring",
                    Title = "ELDEN RING",
                    InstallDir = @"C:\Games\EldenRing",
                    ExecutableRelPath = "eldenring.exe",
                    AntiCheatTier = AntiCheatTier.Tier0,
                });

                vm.LoadForGameAsync(game).GetAwaiter().GetResult();

                // Pump dispatcher briefly so ObservableCollection/binding updates settle
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

                double lightAvgLuma = RenderAndVerifyWorkbench(
                    vm,
                    themeFile: "LightTheme.xaml",
                    outputPngPath: string.IsNullOrWhiteSpace(optionalRenderOutputDir)
                        ? null
                        : Path.Combine(optionalRenderOutputDir, "save-workbench-light.png"));

                double darkAvgLuma = RenderAndVerifyWorkbench(
                    vm,
                    themeFile: "DarkTheme.xaml",
                    outputPngPath: string.IsNullOrWhiteSpace(optionalRenderOutputDir)
                        ? null
                        : Path.Combine(optionalRenderOutputDir, "save-workbench-dark.png"));

                // Light theme must be significantly brighter than Dark theme!
                Assert.True(lightAvgLuma > 180.0, $"Expected Light theme mean luma > 180, got {lightAvgLuma:0.0}");
                Assert.True(darkAvgLuma < 80.0, $"Expected Dark theme mean luma < 80, got {darkAvgLuma:0.0}");

                // Standalone SaveGraphCellControl vector Bezier render verification
                SaveGraphRowLayout? curvedRow = vm.GraphRows
                    .Select(r => r.GraphLayout)
                    .FirstOrDefault(g => g.OutgoingCurves.Any(c => c.IsFork) || g.IncomingCurves.Any(c => c.IsFork))
                    ?? vm.GraphRows[0].GraphLayout;
                var cell = new SaveGraphCellControl
                {
                    Width = 110,
                    Height = 58,
                    RowLayout = curvedRow,
                    SurfaceBackgroundBrush = Brushes.White,
                    HeadRingBrush = Brushes.MediumSeaGreen,
                    PinAccentBrush = Brushes.Goldenrod,
                };
                cell.Measure(new Size(110, 58));
                cell.Arrange(new Rect(0, 0, 110, 58));
                cell.UpdateLayout();

                var cellRtb = new RenderTargetBitmap(110, 58, 96.0, 96.0, PixelFormats.Pbgra32);
                cellRtb.Render(cell);
                byte[] cellPixels = new byte[110 * 58 * 4];
                cellRtb.CopyPixels(cellPixels, 110 * 4, 0);
                int coloredPixels = 0;
                for (int i = 0; i < cellPixels.Length; i += 4)
                {
                    if (cellPixels[i + 3] > 20)
                    {
                        coloredPixels++;
                    }
                }

                Assert.True(coloredPixels > 80, $"Expected > 80 drawn vector pixels on SaveGraphCellControl, got {coloredPixels}");
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
        Assert.True(staThread.Join(TimeSpan.FromSeconds(60)), "STA headless SaveWorkbenchControl render test timed out.");
        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    private static double RenderAndVerifyWorkbench(
        SaveWorkbenchViewModel vm,
        string themeFile,
        string? outputPngPath)
    {
        const int renderW = 1200;
        const int renderH = 760;

        // Ensure WPF's pack:// URI & WebRequest prefix are registered without instantiating an Application singleton.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);

        string hostAsm = typeof(SaveWorkbenchControl).Assembly.GetName().Name ?? "Spawn.Host";
        var rootResources = new ResourceDictionary();
        rootResources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{hostAsm};component/Themes/{themeFile}", UriKind.Absolute),
        });
        rootResources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{hostAsm};component/Themes/Controls.xaml", UriKind.Absolute),
        });

        // Pre-bind Theme + Controls into a host FrameworkElement BEFORE instantiating SaveWorkbenchControl
        // so StaticResource lookups (SpawnUiFont, SpawnMonoFont, PrimaryCapsuleButtonStyle, etc.) resolve cleanly.
        var container = new System.Windows.Controls.Border
        {
            Width = renderW,
            Height = renderH,
            Padding = new Thickness(24, 16, 24, 16),
            Resources = rootResources,
        };
        container.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PageBackgroundBrush");

        var workbench = new SaveWorkbenchControl
        {
            DataContext = vm,
        };
        container.Child = workbench;

        vm.UpdateViewportWidth(renderW - 48);
        container.Measure(new Size(renderW, renderH));
        container.Arrange(new Rect(0, 0, renderW, renderH));
        container.UpdateLayout();

        var rtb = new RenderTargetBitmap(renderW, renderH, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(container);

        byte[] pixels = new byte[renderW * renderH * 4];
        rtb.CopyPixels(pixels, renderW * 4, 0);

        int nonTransparent = 0;
        int chromaticPixels = 0;
        double lumaSum = 0;
        int totalPixels = renderW * renderH;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];
            if (a > 0)
            {
                nonTransparent++;
            }

            lumaSum += (0.299 * r) + (0.587 * g) + (0.114 * b);
            int chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            if (chroma > 45)
            {
                chromaticPixels++;
            }
        }

        Assert.True(nonTransparent > totalPixels / 2, $"[{themeFile}] Expected > 50% rendered pixels, got {nonTransparent}");
        Assert.True(chromaticPixels > 1200, $"[{themeFile}] Expected colored branch lanes/badges/CTA (> 1200 chromatic pixels), got {chromaticPixels}");

        if (!string.IsNullOrWhiteSpace(outputPngPath))
        {
            string? dir = Path.GetDirectoryName(outputPngPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = new FileStream(outputPngPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(fs);
        }

        return lumaSum / totalPixels;
    }

    // -------------------------------------------------------------------------
    // Headless In-Memory Test Fakes
    // -------------------------------------------------------------------------

    private sealed class FakeSaveGameLaunchService : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged { add { } remove { } }
        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }

    private sealed class FakeSaveWatcherService : ISaveWatcherService
    {
        public event EventHandler<SaveWatcherStatusDto>? WatcherStatusChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StartWatchingGameAsync(string gameId, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopWatchingGameAsync(string gameId, CancellationToken ct = default) => Task.CompletedTask;
        public SaveWatcherStatusDto GetStatus(string gameId) => new() { GameId = gameId, Status = SaveWatcherStatusKind.Watching };
        public IReadOnlyList<SaveWatcherStatusDto> GetAllStatuses() => [GetStatus("elden-ring")];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLudusaviRuleResolver : ILudusaviRuleResolver
    {
        public Task<ResolvedSaveLayout> ResolveGameSaveLayoutAsync(string gameId, bool refreshOnlineRules = false, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedSaveLayout
            {
                GameId = gameId,
                MatchedRuleName = "ELDEN RING",
                SaveLocations =
                [
                    new ResolvedSaveLocation
                    {
                        LogicalTemplatePath = "<winAppData>/EldenRing/<storeUserId>/ER0000.sl2",
                        ResolvedFullPath = @"%APPDATA%\EldenRing\76561198000000000\ER0000.sl2",
                        Category = SaveFileCategory.Save,
                        Exists = true,
                        FileCount = 2,
                        TotalBytes = 28 * 1024 * 1024,
                    },
                ],
                ConfigLocations =
                [
                    new ResolvedSaveLocation
                    {
                        LogicalTemplatePath = "<winAppData>/EldenRing/GraphicsConfig.xml",
                        ResolvedFullPath = @"%APPDATA%\EldenRing\GraphicsConfig.xml",
                        Category = SaveFileCategory.Config,
                        Exists = true,
                        FileCount = 1,
                        TotalBytes = 4096,
                    },
                ],
                RegistryKeys =
                [
                    new ResolvedSaveLocation
                    {
                        LogicalTemplatePath = @"registry://HKCU/Software/FromSoftware/EldenRing",
                        ResolvedFullPath = @"HKCU\Software\FromSoftware\EldenRing",
                        Category = SaveFileCategory.RegistryExport,
                        Exists = true,
                        FileCount = 1,
                        TotalBytes = 512,
                    },
                ],
            });

        public string CanonicalizeToLogicalPath(string fullPathOrRegistryKey, GameRecord game) => fullPathOrRegistryKey;
        public string ExpandLogicalPath(string logicalPath, GameRecord game) => logicalPath;
        public Task<int> EnsureBuiltInRulesSeededAsync(CancellationToken ct = default) => Task.FromResult(120);
        public Task<int> ImportRulesJsonAsync(string rulesJson, CancellationToken ct = default) => Task.FromResult(120);
        public Task<int> GetRuleCountAsync(CancellationToken ct = default) => Task.FromResult(120);
    }

    private sealed class FakeSaveTimeMachineService : ISaveTimeMachineService
    {
        private readonly string _gameId;
        private readonly List<SaveBranchRecord> _branches = [];
        private readonly List<SaveSnapshotRecord> _snapshots = [];
        private SaveGameStateRecord _state;
        private int _seq = 10;

        public int RollbackCalls { get; private set; }
        public event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged;

        private FakeSaveTimeMachineService(string gameId)
        {
            _gameId = gameId;
            _state = new SaveGameStateRecord
            {
                GameId = gameId,
                ActiveBranchId = "b-main",
                CurrentHeadSnapshotId = "snap-00000007",
                AutoWatchEnabled = true,
                PostExitSnapshotEnabled = true,
            };
        }

        public static FakeSaveTimeMachineService CreateWithRichBranchingTree(string gameId)
        {
            var svc = new FakeSaveTimeMachineService(gameId);
            DateTimeOffset t0 = new(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);

            svc._branches.AddRange(
            [
                new SaveBranchRecord { BranchId = "b-main", GameId = gameId, Name = "main", ColorHex = SaveBranchColors.Palette[0], HeadSnapshotId = "snap-00000007", IsDefault = true, SnapshotCount = 3 },
                new SaveBranchRecord { BranchId = "b-boss", GameId = gameId, Name = "boss-rush", ColorHex = SaveBranchColors.Palette[1], HeadSnapshotId = "snap-00000004", BaseSnapshotId = "snap-00000002", SnapshotCount = 2 },
                new SaveBranchRecord { BranchId = "b-frenzied", GameId = gameId, Name = "frenzied-flame", ColorHex = SaveBranchColors.Palette[2], HeadSnapshotId = "snap-00000006", BaseSnapshotId = "snap-00000003", SnapshotCount = 1 },
                new SaveBranchRecord { BranchId = "b-deck", GameId = gameId, Name = "cloud-deck-backup", ColorHex = SaveBranchColors.Palette[3], HeadSnapshotId = "snap-00000005", BaseSnapshotId = "snap-00000002", SnapshotCount = 1 },
            ]);

            svc._snapshots.AddRange(
            [
                MakeSnap("snap-00000001", gameId, "b-main", null, SaveSnapshotTriggerKind.ManualPin, "宁姆格福初始赐福点", t0, 1800, 14_200_000, 3_800_000, pinned: true),
                MakeSnap("snap-00000002", gameId, "b-main", "snap-00000001", SaveSnapshotTriggerKind.PostExit, "王城罗德尔大门前（关键分叉点）", t0.AddHours(6), 14_400, 22_500_000, 5_100_000, pinned: true),
                MakeSnap("snap-00000003", gameId, "b-boss", "snap-00000002", SaveSnapshotTriggerKind.BranchFork, "圣树底层 · 女武神雾门前", t0.AddHours(8), 18_000, 24_100_000, 1_900_000, pinned: false),
                MakeSnap("snap-00000004", gameId, "b-boss", "snap-00000003", SaveSnapshotTriggerKind.AutoQuiescent, "女武神无伤挑战 · 阶段二存档", t0.AddHours(9), 19_200, 24_300_000, 1_100_000, pinned: false),
                MakeSnap("snap-00000005", gameId, "b-deck", "snap-00000002", SaveSnapshotTriggerKind.CloudDivergenceBackup, "掌机离线推进 · 火山官邸支线", t0.AddHours(10), 17_100, 23_800_000, 1_650_000, pinned: true),
                MakeSnap("snap-00000006", gameId, "b-frenzied", "snap-00000003", SaveSnapshotTriggerKind.Manual, "受赐癫火前 · 隐藏结局线", t0.AddHours(11), 21_600, 25_000_000, 1_400_000, pinned: false),
                MakeSnap("snap-00000007", gameId, "b-main", "snap-00000002", SaveSnapshotTriggerKind.PostExit, "灰城罗德尔 · 终战准备完成", t0.AddHours(13), 26_400, 26_800_000, 2_200_000, pinned: false),
            ]);

            return svc;
        }

        public SaveBranchRecord AddDivergenceBackupBranch(string name, string colorHex)
        {
            string branchId = $"b-div-{++_seq}";
            string snapId = $"snap-000000{_seq}";
            var branch = new SaveBranchRecord
            {
                BranchId = branchId,
                GameId = _gameId,
                Name = name,
                ColorHex = colorHex,
                HeadSnapshotId = snapId,
                BaseSnapshotId = _state.CurrentHeadSnapshotId,
                SnapshotCount = 1,
            };
            _branches.Add(branch);
            _snapshots.Add(MakeSnap(snapId, _gameId, branchId, _state.CurrentHeadSnapshotId, SaveSnapshotTriggerKind.CloudDivergenceBackup, "分叉保留分支", DateTimeOffset.UtcNow, 26_900, 27_000_000, 1_100_000, pinned: true));
            return branch;
        }

        private static SaveSnapshotRecord MakeSnap(
            string id,
            string gameId,
            string branchId,
            string? parentId,
            SaveSnapshotTriggerKind trigger,
            string note,
            DateTimeOffset createdUtc,
            long playtimeSec,
            long rawBytes,
            long storedBytes,
            bool pinned) =>
            new()
            {
                SnapshotId = id,
                GameId = gameId,
                BranchId = branchId,
                ParentSnapshotId = parentId,
                TriggerKind = trigger,
                Note = note,
                FileCount = 6,
                RegistryKeyCount = 2,
                TotalRawBytes = rawBytes,
                TotalStoredBytes = storedBytes,
                CreatedMachineId = "DESKTOP-MAIN",
                PlaytimeActiveSeconds = playtimeSec,
                CreatedAtUtc = createdUtc,
                IsPinned = pinned,
            };

        public Task<SaveGameStateRecord> GetGameStateAsync(string gameId, CancellationToken ct = default) => Task.FromResult(_state);

        public Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default)
        {
            _state = state;
            return Task.FromResult(_state);
        }

        public Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new SaveSettingsDto());
        public Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default) => Task.CompletedTask;

        public Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string gameId, CancellationToken ct = default)
        {
            var branchById = _branches.ToDictionary(b => b.BranchId, StringComparer.OrdinalIgnoreCase);
            var tipsBySnap = _branches
                .Where(b => !string.IsNullOrWhiteSpace(b.HeadSnapshotId))
                .GroupBy(b => b.HeadSnapshotId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(b => b.Name).ToList(), StringComparer.OrdinalIgnoreCase);

            var nodes = _snapshots.Select(s =>
            {
                SaveBranchRecord b = branchById[s.BranchId];
                tipsBySnap.TryGetValue(s.SnapshotId, out var tipNames);
                return new SaveTreeNodeDto
                {
                    Snapshot = s,
                    BranchName = b.Name,
                    BranchColorHex = b.ColorHex,
                    ParentSnapshotId = s.ParentSnapshotId,
                    IsCurrentHead = string.Equals(s.SnapshotId, _state.CurrentHeadSnapshotId, StringComparison.OrdinalIgnoreCase),
                    IsBranchTip = tipNames is { Count: > 0 },
                    BranchTipNames = tipNames ?? [],
                    IsCloudSynced = s.SnapshotId is "snap-00000001" or "snap-00000002" or "snap-00000007",
                };
            }).ToList();

            string activeName = _state.ActiveBranchId is not null && branchById.TryGetValue(_state.ActiveBranchId, out var ab)
                ? ab.Name
                : "main";

            return Task.FromResult(new SaveTreeGraphDto
            {
                GameId = gameId,
                ActiveBranchId = _state.ActiveBranchId,
                ActiveBranchName = activeName,
                CurrentHeadSnapshotId = _state.CurrentHeadSnapshotId,
                Branches = _branches.Select(b => b with { SnapshotCount = _snapshots.Count(s => s.BranchId == b.BranchId) }).ToList(),
                Nodes = nodes,
                TotalRawBytes = _snapshots.Sum(s => s.TotalRawBytes),
                TotalDeduplicatedBytes = _snapshots.Sum(s => s.TotalStoredBytes),
                HasUnresolvedCloudConflict = _state.HasUnresolvedCloudConflict,
                LastSyncedAtUtc = _state.LastSyncedAtUtc,
            });
        }

        public Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SaveLibraryGameOverviewDto>>(
            [
                new SaveLibraryGameOverviewDto
                {
                    GameId = _gameId,
                    GameTitle = "ELDEN RING",
                    ActiveBranchId = _state.ActiveBranchId,
                    ActiveBranchName = _branches.First(b => b.BranchId == _state.ActiveBranchId).Name,
                    BranchCount = _branches.Count,
                    SnapshotCount = _snapshots.Count,
                    PinnedCount = _snapshots.Count(s => s.IsPinned),
                    TotalRawBytes = _snapshots.Sum(s => s.TotalRawBytes),
                    TotalStoredBytes = _snapshots.Sum(s => s.TotalStoredBytes),
                    LastSnapshotAtUtc = _snapshots.Max(s => s.CreatedAtUtc),
                    HasCloudConflict = _state.HasUnresolvedCloudConflict,
                },
            ]);

        public Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SaveSnapshotFileRecord>>(
            [
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snapshotId,
                    LogicalPath = "<winAppData>/EldenRing/76561198000000000/ER0000.sl2",
                    OriginalPath = @"%APPDATA%\EldenRing\76561198000000000\ER0000.sl2",
                    Category = SaveFileCategory.Save,
                    FileSize = 26_410_240,
                    LastWriteTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-12),
                    FileBlake3 = "a3f8c1d490be12789012abcdef0123456789abcdef0123456789abcdef012345",
                },
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snapshotId,
                    LogicalPath = "<winAppData>/EldenRing/76561198000000000/ER0000.sl2.bak",
                    OriginalPath = @"%APPDATA%\EldenRing\76561198000000000\ER0000.sl2.bak",
                    Category = SaveFileCategory.Save,
                    FileSize = 380_928,
                    LastWriteTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-15),
                    FileBlake3 = "9b21e44a771c54901234567890abcdef0123456789abcdef0123456789abcdef",
                },
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snapshotId,
                    LogicalPath = "<winAppData>/EldenRing/GraphicsConfig.xml",
                    OriginalPath = @"%APPDATA%\EldenRing\GraphicsConfig.xml",
                    Category = SaveFileCategory.Config,
                    FileSize = 4_096,
                    LastWriteTimeUtc = DateTimeOffset.UtcNow.AddDays(-2),
                    FileBlake3 = "51c98a112f09a411223344556677889900aabbccddeeff112233445566778899",
                },
                new SaveSnapshotFileRecord
                {
                    SnapshotId = snapshotId,
                    LogicalPath = "registry://HKCU/Software/FromSoftware/EldenRing",
                    OriginalPath = @"HKCU\Software\FromSoftware\EldenRing",
                    Category = SaveFileCategory.RegistryExport,
                    FileSize = 512,
                    LastWriteTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-12),
                    FileBlake3 = "77df100c9921bc4511223344556677889900aabbccddeeff1122334455667788",
                },
            ]);

        public Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default) =>
            Task.FromResult(new CasVaultStatsDto
            {
                TotalChunkCount = 48,
                ReferencedChunkCount = 46,
                UnreferencedChunkCount = 2,
                TotalRawBytes = _snapshots.Sum(s => s.TotalRawBytes),
                TotalCompressedBytes = _snapshots.Sum(s => s.TotalStoredBytes),
                TotalSnapshotRawBytes = _snapshots.Sum(s => s.TotalRawBytes),
            });

        public Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(string leftSnapshotId, string rightSnapshotId, CancellationToken ct = default) =>
            Task.FromResult(new SaveSnapshotDiffDto
            {
                BaseLabel = $"#{leftSnapshotId[^8..]}",
                TargetLabel = $"#{rightSnapshotId[^8..]}",
                ModifiedCount = 1,
                AddedCount = 1,
                UnchangedCount = 2,
                Items =
                [
                    new SaveFileDiffItemDto
                    {
                        LogicalPath = "<winAppData>/EldenRing/76561198000000000/ER0000.sl2",
                        Category = SaveFileCategory.Save,
                        DiffKind = SaveFileDiffKind.Modified,
                        LeftSize = 25_100_000,
                        RightSize = 26_410_240,
                    },
                    new SaveFileDiffItemDto
                    {
                        LogicalPath = "<winAppData>/EldenRing/76561198000000000/slot_meta.bin",
                        Category = SaveFileCategory.Save,
                        DiffKind = SaveFileDiffKind.Added,
                        LeftSize = 0,
                        RightSize = 12_288,
                    },
                ],
            });

        public Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new SaveSnapshotDiffDto
            {
                BaseLabel = $"#{snapshotId[^8..]}",
                TargetLabel = "当前磁盘现场",
                ModifiedCount = 1,
                UnchangedCount = 3,
                Items =
                [
                    new SaveFileDiffItemDto
                    {
                        LogicalPath = "<winAppData>/EldenRing/76561198000000000/ER0000.sl2",
                        Category = SaveFileCategory.Save,
                        DiffKind = SaveFileDiffKind.Modified,
                        LeftSize = 26_410_240,
                        RightSize = 26_512_000,
                    },
                ],
            });

        public Task<CreateSaveSnapshotResult> CreateSnapshotAsync(CreateSaveSnapshotRequest request, CancellationToken ct = default)
        {
            string branchId = request.BranchId ?? _state.ActiveBranchId ?? "b-main";
            string id = $"snap-000000{++_seq}";
            var snap = MakeSnap(
                id,
                request.GameId,
                branchId,
                _state.CurrentHeadSnapshotId,
                request.TriggerKind,
                request.Note ?? "手动备份",
                DateTimeOffset.UtcNow,
                27_000,
                26_900_000,
                1_200_000,
                request.Pin);
            _snapshots.Add(snap);
            int idx = _branches.FindIndex(b => b.BranchId == branchId);
            if (idx >= 0)
            {
                _branches[idx] = _branches[idx] with { HeadSnapshotId = id };
            }

            _state = _state with { ActiveBranchId = branchId, CurrentHeadSnapshotId = id };
            SaveTreeChanged?.Invoke(this, new SaveTreeChangedEvent(request.GameId, branchId, id, "SnapshotCreated", DateTimeOffset.UtcNow));
            return Task.FromResult(new CreateSaveSnapshotResult
            {
                Succeeded = true,
                Message = "已创建增量存档快照",
                Snapshot = snap,
                DeduplicatedBytesSaved = 25_700_000,
            });
        }

        public Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default)
        {
            RollbackCalls++;
            SaveSnapshotRecord target = _snapshots.First(s => s.SnapshotId == request.SnapshotId);
            _state = _state with { ActiveBranchId = target.BranchId, CurrentHeadSnapshotId = target.SnapshotId };
            return Task.FromResult(new SaveRestoreOutcome
            {
                Succeeded = true,
                Message = $"已回滚至快照 #{target.ShortId}",
                TargetSnapshot = target,
                ActiveBranchId = target.BranchId,
                RestoredFileCount = 4,
            });
        }

        public Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default)
        {
            string branchId = $"b-custom-{++_seq}";
            string? baseId = request.FromSnapshotId ?? _state.CurrentHeadSnapshotId;
            var branch = new SaveBranchRecord
            {
                BranchId = branchId,
                GameId = request.GameId,
                Name = request.BranchName,
                Description = request.Description,
                ColorHex = request.ColorHex ?? SaveBranchColors.PickForIndex(_branches.Count),
                HeadSnapshotId = baseId,
                BaseSnapshotId = baseId,
                SnapshotCount = 0,
            };
            _branches.Add(branch);
            if (request.SwitchToNewBranch)
            {
                _state = _state with { ActiveBranchId = branchId, CurrentHeadSnapshotId = baseId };
            }

            return Task.FromResult(new ForkBranchResult
            {
                Succeeded = true,
                Message = $"已创建分支「{request.BranchName}」",
                Branch = branch,
            });
        }

        public Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default)
        {
            SaveBranchRecord target = _branches.First(b => b.BranchId == request.TargetBranchId);
            _state = _state with { ActiveBranchId = target.BranchId, CurrentHeadSnapshotId = target.HeadSnapshotId };
            return Task.FromResult(new SaveRestoreOutcome
            {
                Succeeded = true,
                Message = $"已切换至分支「{target.Name}」",
                ActiveBranchId = target.BranchId,
            });
        }

        public Task<SaveBranchRecord?> RenameBranchAsync(string branchId, string newName, string? description = null, string? colorHex = null, CancellationToken ct = default)
        {
            int idx = _branches.FindIndex(b => b.BranchId == branchId);
            if (idx < 0)
            {
                return Task.FromResult<SaveBranchRecord?>(null);
            }

            _branches[idx] = _branches[idx] with { Name = newName, ColorHex = colorHex ?? _branches[idx].ColorHex };
            return Task.FromResult<SaveBranchRecord?>(_branches[idx]);
        }

        public Task<DeleteBranchResult> DeleteBranchAsync(string branchId, bool deleteSnapshots = false, CancellationToken ct = default)
        {
            _branches.RemoveAll(b => b.BranchId == branchId);
            return Task.FromResult(new DeleteBranchResult(true, "已删除分支"));
        }

        public Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default)
        {
            int idx = _snapshots.FindIndex(s => s.SnapshotId == snapshotId);
            if (idx < 0)
            {
                return Task.FromResult(false);
            }

            _snapshots[idx] = _snapshots[idx] with { IsPinned = isPinned };
            return Task.FromResult(true);
        }

        public Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default)
        {
            int idx = _snapshots.FindIndex(s => s.SnapshotId == snapshotId);
            if (idx < 0)
            {
                return Task.FromResult(false);
            }

            _snapshots[idx] = _snapshots[idx] with { Note = note };
            return Task.FromResult(true);
        }

        public Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default)
        {
            _snapshots.RemoveAll(s => s.SnapshotId == snapshotId);
            return Task.FromResult(new DeleteSnapshotResult(true, "已删除快照"));
        }

        public Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(string snapshotId, string targetDirectory, CancellationToken ct = default) =>
            Task.FromResult(new ExportSnapshotResult(true, "已导出快照副本", targetDirectory, 4, 1));

        public Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult(new LegacyPreTrainerImportReport(0, 0, 0, [], []));

        public Task<SaveGcReport> RunCasGarbageCollectionAsync(TimeSpan? gracePeriodOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new SaveGcReport(2, 512_000, 46, 15_000_000));
    }

    private sealed class FakeCloudSaveSyncService(FakeSaveTimeMachineService saves, bool triggerDivergence) : ICloudSaveSyncService
    {
        private readonly List<CloudSyncEndpointRecord> _endpoints =
        [
            new CloudSyncEndpointRecord
            {
                EndpointId = "ep-local",
                DisplayName = "OneDrive SPPK 同步盘",
                ProviderType = CloudSyncProviderType.LocalFolder,
                EndpointUrl = @"C:\Sync\SpawnSaves",
                IsDefault = true,
            },
        ];

        public CloudDivergenceResolution? LastResolutionChoice { get; private set; }

        public Task<IReadOnlyList<CloudSyncEndpointRecord>> ListEndpointsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CloudSyncEndpointRecord>>(_endpoints);

        public Task<CloudSyncEndpointRecord> SaveEndpointAsync(CloudSyncEndpointRecord endpoint, CloudSyncEndpointSecrets? secrets = null, CancellationToken ct = default)
        {
            _endpoints.Add(endpoint);
            return Task.FromResult(endpoint);
        }

        public Task<bool> DeleteEndpointAsync(string endpointId, CancellationToken ct = default) =>
            Task.FromResult(_endpoints.RemoveAll(e => e.EndpointId == endpointId) > 0);

        public Task<CloudSyncEndpointTestResult> TestEndpointConnectionAsync(string endpointId, CancellationToken ct = default) =>
            Task.FromResult(new CloudSyncEndpointTestResult(true, "连接正常", 14));

        public async Task<CloudSyncPlanPreview> PlanSyncAsync(string gameId, string? endpointId = null, CancellationToken ct = default)
        {
            SaveTreeGraphDto graph = await saves.GetSaveTreeGraphAsync(gameId, ct);
            SaveSnapshotRecord? local = graph.Nodes.FirstOrDefault(n => n.IsCurrentHead)?.Snapshot;
            return new CloudSyncPlanPreview
            {
                GameId = gameId,
                EndpointId = endpointId ?? "ep-local",
                Relation = triggerDivergence ? CloudSyncRelation.Diverged : CloudSyncRelation.FastForwardPush,
                Message = triggerDivergence ? "本机与 Steam Deck 远端分支发生分叉" : "本机领先 2 个块，可快速推送",
                LocalHeadSnapshot = local,
                RemoteHead = new CloudRemoteHeadSummary
                {
                    MachineId = "STEAM-DECK-01",
                    MachineName = "Steam Deck OLED",
                    HeadSnapshotId = "snap-remote99",
                    BranchName = "main",
                    UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
                    PlaytimeActiveSeconds = 27_900,
                    FileCount = 6,
                    TotalRawBytes = 27_200_000,
                },
                CommonAncestorSnapshotId = "snap-00000002",
                MissingRemoteChunkCount = 2,
                EstimatedUploadBytes = 1_400_000,
                SaveFileCount = 2,
            };
        }

        public Task<CloudSyncExecutionReport> ExecuteSyncAsync(
            string gameId,
            string? endpointId = null,
            CloudDivergenceResolution? divergenceResolution = null,
            IProgress<CloudSyncProgressTick>? progress = null,
            CancellationToken ct = default)
        {
            LastResolutionChoice = divergenceResolution;
            SaveBranchRecord? backup = divergenceResolution is not null
                ? saves.AddDivergenceBackupBranch("Branch-Desktop-Backup", SaveBranchColors.Palette[5])
                : null;

            return Task.FromResult(new CloudSyncExecutionReport
            {
                Succeeded = true,
                Relation = CloudSyncRelation.UpToDate,
                Message = "已完成双端分叉保留同步",
                DivergenceBackupBranchId = backup?.BranchId,
                DivergenceBackupBranchName = backup?.Name,
            });
        }

        public Task<IReadOnlyList<CloudSyncExecutionReport>> SyncAllConfiguredGamesAsync(
            IProgress<CloudSyncProgressTick>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CloudSyncExecutionReport>>([]);
    }
}
