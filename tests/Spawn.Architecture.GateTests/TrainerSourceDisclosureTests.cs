using System.IO;
using System.Text;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Spawn.Modules.Progress.Catalog;
using Xunit;

namespace Spawn.Architecture.GateTests;

public sealed class TrainerSourceDisclosureTests
{
    [Fact]
    public async Task TrainerWorkbenchViewModel_ExternalWebsiteVsLocalImport_SurfacesProvenanceAndGuardsHttpsSourceLinks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-tr-prov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            var game = new GameRecord
            {
                GameId = "game-prov-01",
                Title = "Hollow Knight",
                InstallDir = Path.Combine(tempDir, "HK"),
                ExecutableRelPath = "hk.exe",
                AntiCheatTier = AntiCheatTier.Tier0,
            };
            await db.UpsertGameAsync(game);

            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));

            var catalog = new ProvenanceTestCatalogService();
            var runtime = new ProvenanceTestRuntimeService();
            var snapshots = new ProvenanceTestSnapshotService();
            var safetyGate = new ProvenanceTestSafetyGate();
            var library = new ProvenanceTestLibraryService(db);
            var launch = new ProvenanceTestLaunchService();

            var openedUrls = new List<string>();
            var copiedTexts = new List<string>();

            using var vm = new TrainerWorkbenchViewModel(
                paths,
                db,
                catalog,
                runtime,
                snapshots,
                safetyGate,
                guard,
                library,
                launch,
                openExternalUrl: url => openedUrls.Add(url),
                copyToClipboard: text => copiedTexts.Add(text));

            await vm.LoadForGameAsync(new GameListItemViewModel(game));
            Assert.Contains("第三方外部网站", vm.OnlineDiscoveryDisclosureTitle, StringComparison.Ordinal);
            Assert.Contains("flingtrainer.com", vm.OnlineDiscoveryDisclosureBody, StringComparison.Ordinal);
            Assert.Contains("本地导入 · 用户手动添加", vm.UnboundProvenanceHintText, StringComparison.Ordinal);

            // 1. Online discovery candidates display external website badges, host, and HTTPS URLs without auto-opening browser.
            await vm.MatchTrainersCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.MatchCandidates.Count);
            Assert.Empty(openedUrls);
            Assert.Empty(copiedTexts);

            TrainerCandidateItemViewModel mainCand = vm.MatchCandidates[0];
            Assert.Equal("外部网站 · FLiNG Trainer (flingtrainer.com)", mainCand.ProviderLabel);
            Assert.True(mainCand.HasValidHttpsPageUrl);
            Assert.Equal("https://flingtrainer.com/trainers/hollow-knight-trainer/", mainCand.ValidatedHttpsPageUrl);
            Assert.Contains("来源网页：https://flingtrainer.com/trainers/hollow-knight-trainer/", mainCand.SourceUrlDisplayText, StringComparison.Ordinal);

            TrainerCandidateItemViewModel archiveCand = vm.MatchCandidates[1];
            Assert.Equal("外部网站 · FLiNG Trainer 归档站 (archive.flingtrainer.com)", archiveCand.ProviderLabel);
            Assert.True(archiveCand.HasValidHttpsPageUrl);

            mainCand.CopyPageUrlCommand.Execute(null);
            Assert.Single(copiedTexts);
            Assert.Equal("https://flingtrainer.com/trainers/hollow-knight-trainer/", copiedTexts[0]);
            Assert.Empty(openedUrls);

            mainCand.OpenInBrowserCommand.Execute(null);
            Assert.Single(openedUrls);
            Assert.Equal("https://flingtrainer.com/trainers/hollow-knight-trainer/", openedUrls[0]);

            // 2. Downloading an external website candidate binds trainer with full external provenance & secondary menu URL actions.
            openedUrls.Clear();
            copiedTexts.Clear();
            await mainCand.DownloadCommand.ExecuteAsync(null);

            Assert.True(vm.HasBoundTrainer);
            Assert.True(vm.IsActiveTrainerExternalWebSource);
            Assert.Equal("外部网站 · FLiNG Trainer (flingtrainer.com)", vm.TrainerSourceDomainText);
            Assert.Equal("flingtrainer.com", vm.TrainerSourceHostText);
            Assert.True(vm.HasValidExternalSourceUrl);
            Assert.Equal("https://flingtrainer.com/trainers/hollow-knight-trainer/", vm.TrainerSourcePageUrl);
            Assert.Equal("来源网页：https://flingtrainer.com/trainers/hollow-knight-trainer/", vm.TrainerSourcePageDisplayLine);
            Assert.Contains("第三方外部网站", vm.TrainerSourceProvenanceNoteText, StringComparison.Ordinal);
            Assert.Contains("非 SPAWN 内置", vm.TrainerSourceProvenanceNoteText, StringComparison.Ordinal);
            Assert.Contains("SHA-256", vm.TrainerSourceProvenanceNoteText, StringComparison.Ordinal);
            Assert.Contains("TrainerWorker 沙箱", vm.TrainerSourceProvenanceNoteText, StringComparison.Ordinal);

            vm.CopyActiveTrainerSourceUrlCommand.Execute(null);
            Assert.Single(copiedTexts);
            Assert.Equal("https://flingtrainer.com/trainers/hollow-knight-trainer/", copiedTexts[0]);

            vm.OpenActiveTrainerSourcePageCommand.Execute(null);
            Assert.Single(openedUrls);
            Assert.Equal("https://flingtrainer.com/trainers/hollow-knight-trainer/", openedUrls[0]);

            // 3. Non-HTTPS or unsafe SourcePageUrl is strictly rejected by both HasValidExternalSourceUrl and commands.
            openedUrls.Clear();
            copiedTexts.Clear();
            catalog.SetSingleBoundTrainer(
                game.GameId,
                vm.ActiveTrainer! with { SourcePageUrl = "file:///C:/Windows/System32/calc.exe" });
            await vm.ReloadCurrentGameIfMatchesAsync(game.GameId);

            Assert.True(vm.IsActiveTrainerExternalWebSource);
            Assert.False(vm.HasValidExternalSourceUrl);
            Assert.Equal(string.Empty, vm.TrainerSourcePageUrl);
            vm.OpenActiveTrainerSourcePageCommand.Execute(null);
            vm.CopyActiveTrainerSourceUrlCommand.Execute(null);
            Assert.Empty(openedUrls);
            Assert.Empty(copiedTexts);

            // 4. Local user-imported trainer clearly distinguishes as "本地导入 · 用户手动添加" without external web badge.
            string localTrainerStub = Path.Combine(tempDir, "local_trainer.exe");
            File.WriteAllBytes(localTrainerStub, [0x4D, 0x5A]);
            await vm.ImportLocalTrainerPathAsync(localTrainerStub);

            Assert.True(vm.HasBoundTrainer);
            Assert.False(vm.IsActiveTrainerExternalWebSource);
            Assert.Equal("本地导入 · 用户手动添加", vm.TrainerSourceDomainText);
            Assert.False(vm.HasValidExternalSourceUrl);
            Assert.Equal(string.Empty, vm.TrainerSourcePageDisplayLine);
            Assert.Contains("用户从本机手动添加", vm.TrainerSourceProvenanceNoteText, StringComparison.Ordinal);
            Assert.Contains("非外部在线检索", vm.TrainerSourceProvenanceNoteText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public async Task ManagedTrainerItemAndSettingsViewModels_DistinguishExternalWebFromLocalImportAndEnforceHttps()
    {
        var openedUrls = new List<string>();
        var copiedTexts = new List<string>();

        var externalRec = new TrainerCatalogRecord
        {
            TrainerId = "tr-1",
            GameId = "g-1",
            Provider = TrainerProviderKind.FlingMain,
            DisplayName = "Hollow Knight Trainer",
            SourcePageUrl = "https://flingtrainer.com/trainers/hollow-knight-trainer/",
            SandboxRelativePath = @"g-1\tr-1\hk_trainer.exe",
            FileSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };

        var externalVm = CreateManagedItem(externalRec, openedUrls, copiedTexts);
        Assert.True(externalVm.IsExternalWebsiteSource);
        Assert.Equal("外部网站 · FLiNG Trainer (flingtrainer.com)", externalVm.ProviderLabel);
        Assert.True(externalVm.HasValidSourcePageUrl);
        Assert.Contains("第三方外部网站（非 SPAWN 内置）", externalVm.SourceOriginSummaryText, StringComparison.Ordinal);

        externalVm.CopySourcePageUrlCommand.Execute(null);
        externalVm.OpenSourcePageCommand.Execute(null);
        Assert.Equal(["https://flingtrainer.com/trainers/hollow-knight-trainer/"], copiedTexts);
        Assert.Equal(["https://flingtrainer.com/trainers/hollow-knight-trainer/"], openedUrls);

        openedUrls.Clear();
        copiedTexts.Clear();

        var localRec = new TrainerCatalogRecord
        {
            TrainerId = "tr-2",
            GameId = "g-1",
            Provider = TrainerProviderKind.LocalImport,
            DisplayName = "My Local Trainer",
            SandboxRelativePath = @"g-1\tr-2\my_local.exe",
            FileSha256 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
        };

        var localVm = CreateManagedItem(localRec, openedUrls, copiedTexts);
        Assert.False(localVm.IsExternalWebsiteSource);
        Assert.Equal("本地导入 · 用户手动添加", localVm.ProviderLabel);
        Assert.False(localVm.HasValidSourcePageUrl);
        Assert.Contains("my_local.exe", localVm.SourceOriginSummaryText, StringComparison.Ordinal);

        localVm.CopySourcePageUrlCommand.Execute(null);
        localVm.OpenSourcePageCommand.Execute(null);
        Assert.Empty(copiedTexts);
        Assert.Empty(openedUrls);

        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-tr-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            await using var db = new SpawnDatabase(paths);
            var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
            await using var ipcClient = new OverlayIpcClient(paths);
            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            var themeMgr = new ThemeManagerService();

            var settingsVm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
            Assert.Equal("修改器来源声明与外部网站边界", settingsVm.TrainerSourceProvenanceTitle);
            Assert.Contains("https://flingtrainer.com", settingsVm.TrainerSourceProvenanceDescription, StringComparison.Ordinal);
            Assert.Contains("https://archive.flingtrainer.com", settingsVm.TrainerSourceProvenanceDescription, StringComparison.Ordinal);
            Assert.Contains("本地导入 · 用户手动添加", settingsVm.TrainerSourceProvenanceDescription, StringComparison.Ordinal);
            Assert.Contains("SPAWN 自身不内置", settingsVm.TrainerOverviewProvenanceSummary, StringComparison.Ordinal);
            Assert.Contains("第三方外部网站", settingsVm.TrainerOverviewProvenanceSummary, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void MainWindowXamlAndTrainerViewModels_BindProvenanceAcrossAllSurfacesWithoutOfficialSourceMisnomer()
    {
        string repoRoot = LocateRepositoryRoot();
        string mainWindowXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "Spawn.Host", "Views", "MainWindow.xaml"), Encoding.UTF8);
        string workbenchCs = File.ReadAllText(Path.Combine(repoRoot, "src", "Spawn.Host", "ViewModels", "TrainerWorkbenchViewModel.cs"), Encoding.UTF8);

        // Provenance bindings across Overview, Workbench Header, Online Discovery, Secondary ⋯ Menus, and Settings Category 6.
        Assert.Contains("OnlineDiscoveryDisclosureTitle", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OnlineDiscoveryDisclosureBody", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("UnboundProvenanceHintText", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainerSourceProvenanceNoteText", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainerSourcePageDisplayLine", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OpenActiveTrainerSourcePageCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("CopyActiveTrainerSourceUrlCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OpenSourcePageCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("CopySourcePageUrlCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.TrainerOverviewProvenanceSummary", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.TrainerOverviewEmptyStateDescription", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainerSourceProvenanceTitle", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainerSourceProvenanceDescription", mainWindowXaml, StringComparison.Ordinal);

        // Eliminate ambiguous "官方修改器" / "官方源" phrasing in trainer UI copy.
        Assert.DoesNotContain("匹配官方修改器", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FLiNG 官方源", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FLiNG 官方源", workbenchCs, StringComparison.Ordinal);
        Assert.DoesNotContain("从官方源下载", workbenchCs, StringComparison.Ordinal);
    }

    private static ManagedTrainerItemViewModel CreateManagedItem(
        TrainerCatalogRecord record,
        List<string> openedUrls,
        List<string> copiedTexts) =>
        new(
            record,
            game: null,
            isRunning: false,
            isGuardLocked: false,
            isSafetyBlocked: false,
            onPrimaryAction: static _ => Task.CompletedTask,
            onOpenWorkbench: static _ => { },
            onOpenSandboxFolder: static _ => Task.CompletedTask,
            onVerifyIntegrity: static _ => Task.CompletedTask,
            onOpenVirusTotal: static _ => { },
            onRemove: static _ => Task.CompletedTask,
            onOpenExternalUrl: url => openedUrls.Add(url),
            onCopyText: text => copiedTexts.Add(text));

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

    private sealed class ProvenanceTestCatalogService : ITrainerCatalogService
    {
        private readonly Dictionary<string, List<TrainerCatalogRecord>> _byGame = new(StringComparer.OrdinalIgnoreCase);

        public void SetSingleBoundTrainer(string gameId, TrainerCatalogRecord record) =>
            _byGame[gameId] = [record];

        public Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerMatchResult
            {
                GameId = gameId,
                EffectiveQuery = userSearchOverride ?? "Hollow Knight",
                QuerySource = TrainerTitleSource.Cleaner,
                Candidates =
                [
                    new TrainerCandidate
                    {
                        CandidateId = "cand-main",
                        Provider = TrainerProviderKind.FlingMain,
                        Title = "Hollow Knight v1.5+ Plus 10 Trainer",
                        PageUrl = "https://flingtrainer.com/trainers/hollow-knight-trainer/",
                        VersionLabel = "v1.5+",
                        Attachments =
                        [
                            new TrainerAttachmentCandidate
                            {
                                AttachmentId = "att-1",
                                FileName = "Hollow.Knight.v1.5.Plus.10.Trainer-FLiNG.zip",
                                DownloadUrl = "https://flingtrainer.com/downloads/hk-v15.zip",
                            },
                        ],
                    },
                    new TrainerCandidate
                    {
                        CandidateId = "cand-archive",
                        Provider = TrainerProviderKind.FlingArchive,
                        Title = "Hollow Knight v1.0 Plus 8 Trainer",
                        PageUrl = "https://archive.flingtrainer.com/files/Hollow.Knight.v1.0.Trainer.rar",
                        VersionLabel = "v1.0",
                    },
                ],
            });

        public Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var record = new TrainerCatalogRecord
            {
                TrainerId = $"tr-ext-{gameId}",
                GameId = gameId,
                Provider = candidate.Provider,
                DisplayName = candidate.Title,
                VersionLabel = candidate.VersionLabel,
                SourcePageUrl = candidate.PageUrl,
                SandboxRelativePath = $"{gameId}\\tr-ext-{gameId}\\hk_trainer.exe",
                FileSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                FileSize = 2048,
                PeMachine = "x64",
                ParseMode = TrainerParseMode.Structured,
            };
            _byGame[gameId] = [record];
            return Task.FromResult(new TrainerImportResult
            {
                Succeeded = true,
                Trainer = record,
                Hotkeys = [],
            });
        }

        public Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default)
        {
            var record = new TrainerCatalogRecord
            {
                TrainerId = $"tr-loc-{gameId}",
                GameId = gameId,
                Provider = TrainerProviderKind.LocalImport,
                DisplayName = Path.GetFileNameWithoutExtension(userSelectedPath),
                SandboxRelativePath = $"{gameId}\\tr-loc-{gameId}\\{Path.GetFileName(userSelectedPath)}",
                FileSha256 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
                FileSize = 1024,
                PeMachine = "x64",
                ParseMode = TrainerParseMode.FreeHosted,
            };
            _byGame[gameId] = [record];
            return Task.FromResult(new TrainerImportResult
            {
                Succeeded = true,
                Trainer = record,
                Hotkeys = [],
            });
        }

        public Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult(new TrainerIntegrityReport { TrainerId = trainerId, IsValid = true, FileExists = true, HashMatches = true, IsInsideSandbox = true });

        public Task RemoveAsync(string trainerId, CancellationToken ct = default)
        {
            foreach (var list in _byGame.Values)
            {
                list.RemoveAll(t => t.TrainerId == trainerId);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TrainerCatalogRecord>> ListForGameAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrainerCatalogRecord>>(_byGame.TryGetValue(gameId, out var list) ? list : []);

        public Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrainerHotkeyBindingRecord>>([]);
    }

    private sealed class ProvenanceTestRuntimeService : ITrainerRuntimeService
    {
        public TrainerRuntimeState State => TrainerRuntimeState.Ready;
        public TrainerSessionHandle? ActiveSession => null;
        public event EventHandler<TrainerRuntimeStateChanged>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<TrainerLaunchOutcome> LaunchAsync(string trainerId, TrainerLaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new TrainerLaunchOutcome { Succeeded = false, FailureKind = TrainerLaunchFailureKind.TrainerNotFound });

        public Task TriggerHotkeyAsync(string sessionId, string entryId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOriginalWindowVisibleAsync(string sessionId, bool visible, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ProvenanceTestSnapshotService : IPreTrainerSaveSnapshotService
    {
        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerSnapshotResult { Succeeded = true, Status = PreTrainerSnapshotStatus.Skipped, SkipReason = PreTrainerSnapshotSkipReason.NoSaveLocationsFound });

        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>([]);

        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerRestoreResult { Succeeded = false });
    }

    private sealed class ProvenanceTestSafetyGate : ITrainerSafetyGate
    {
        public bool IsWatchdogActive => false;
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered
        {
            add { }
            remove { }
        }

        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport { EffectiveTier = AntiCheatTier.Tier0, HasSinglePlayerCategory = true });

        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveAntiCheatProbeReport());

        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default) =>
            Task.FromResult(new VerifiedOfflineBypass { GameId = game.GameId, StaticAntiCheatTier = game.AntiCheatTier, HasSinglePlayerMode = true, IsLiveAntiCheatClear = true });

        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default) =>
            Task.FromResult(new TrainerAllowanceDecision { Outcome = TrainerAllowanceOutcome.Allowed, IsAllowed = true, Message = "Tier 0", StaticGameTier = AntiCheatTier.Tier0 });

        public void NotifyTrainerSessionStarted(string sessionId, string gameId) { }
        public void NotifyTrainerSessionEnded(string sessionId) { }
    }

    private sealed class ProvenanceTestLibraryService(SpawnDatabase db) : IGameLibraryService
    {
        public Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default) => db.GetAllGamesAsync(cancellationToken);
        public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public Task<GameRecord?> ImportGameAsync(string directoryOrExePath, CancellationToken cancellationToken = default) => Task.FromResult<GameRecord?>(null);
        public Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameRecord>>([]);
        public Task<GameRecord?> RescanGameAsync(string gameId, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public Task<GameRecord?> UpdateGameMetadataAsync(string gameId, GameMetadataUpdate update, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public Task<bool> RemoveGameAsync(string gameId, CancellationToken cancellationToken = default) => db.DeleteGameAsync(gameId, cancellationToken);
    }

    private sealed class ProvenanceTestLaunchService : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
        {
            add { }
            remove { }
        }

        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(GameLaunchResult.Failure("STUB", "stub"));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }
}
