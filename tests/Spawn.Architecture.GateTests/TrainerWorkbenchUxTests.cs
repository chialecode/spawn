using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Helpers;
using Spawn.Host.ViewModels;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Headless UX &amp; safety regression tests for Issue #63 covering <see cref="TrainerWorkbenchViewModel"/>,
/// <see cref="TrainerHotkeyRowViewModel"/>, <see cref="TrainerCandidateItemViewModel"/>,
/// <see cref="PreTrainerSnapshotItemViewModel"/>, and <see cref="UiFormatters"/>.
/// 100% isolated temp DB/paths, zero UI popups, zero network, zero audio.
/// </summary>
public sealed class TrainerWorkbenchUxTests
{
    [Fact]
    public void UiFormatters_AllTrainerAndAntiCheatEnums_ReturnLocalizedChineseWithoutRawEnumNames()
    {
        Assert.Equal("普通权限 (Level 1)", UiFormatters.FormatTrainerElevationLevel(TrainerElevationLevel.Level1MediumIl));
        Assert.Equal("管理员权限 (Level 2 UAC)", UiFormatters.FormatTrainerElevationLevel(TrainerElevationLevel.Level2HighIlUac));

        foreach (TrainerTitleSource src in Enum.GetValues<TrainerTitleSource>())
        {
            string formatted = UiFormatters.FormatTrainerTitleSource(src);
            Assert.False(string.IsNullOrWhiteSpace(formatted));
            Assert.DoesNotContain(src.ToString(), formatted, StringComparison.Ordinal);
        }

        foreach (PreTrainerSnapshotSkipReason reason in Enum.GetValues<PreTrainerSnapshotSkipReason>())
        {
            if (reason == PreTrainerSnapshotSkipReason.None)
            {
                continue;
            }

            string fromEnum = UiFormatters.FormatPreTrainerSnapshotSkipReason(reason);
            string fromRaw = UiFormatters.FormatPreTrainerSnapshotSkipReason(reason.ToString());
            Assert.False(string.IsNullOrWhiteSpace(fromEnum));
            Assert.Equal(fromEnum, fromRaw);
            Assert.DoesNotContain(reason.ToString(), fromEnum, StringComparison.Ordinal);

            string statusText = UiFormatters.FormatPreTrainerSnapshotStatus(PreTrainerSnapshotStatus.Skipped, reason.ToString());
            Assert.Contains(fromEnum, statusText, StringComparison.Ordinal);
            Assert.DoesNotContain(reason.ToString(), statusText, StringComparison.Ordinal);
        }

        foreach (AntiCheatGuardPhase phase in Enum.GetValues<AntiCheatGuardPhase>())
        {
            string formatted = UiFormatters.FormatAntiCheatGuardPhase(phase);
            Assert.False(string.IsNullOrWhiteSpace(formatted));
            Assert.DoesNotContain(phase.ToString(), formatted, StringComparison.Ordinal);
        }

        // Snapshot item MetricsText must not echo raw English SkipReason.
        var skippedRow = new PreTrainerSnapshotItemViewModel(
            new PreTrainerSnapshotRecord
            {
                SnapshotId = "snap-skip-1",
                GameId = "g1",
                Status = PreTrainerSnapshotStatus.Skipped,
                SkipReason = nameof(PreTrainerSnapshotSkipReason.SizeThresholdExceeded),
            },
            isGameRunning: false,
            onRestore: _ => Task.CompletedTask);

        Assert.Contains("存档体积超过备份上限", skippedRow.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeThresholdExceeded", skippedRow.StatusText, StringComparison.Ordinal);
        Assert.Equal("未写入文件", skippedRow.MetricsText);
        Assert.DoesNotContain("SizeThresholdExceeded", skippedRow.MetricsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrainerWorkbench_HotkeyValidationMappingRunningMenuAndInlineConfirmations_WorkEndToEnd()
    {
        await using var env = await WorkbenchTestEnv.CreateAsync();

        var game1 = new GameRecord
        {
            GameId = "game-hk-1",
            Title = "Hollow Knight",
            InstallDir = Path.Combine(env.TempDir, "HK"),
            ExecutableRelPath = "hk.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        var game2 = new GameRecord
        {
            GameId = "game-hk-2",
            Title = "Celeste",
            InstallDir = Path.Combine(env.TempDir, "Celeste"),
            ExecutableRelPath = "celeste.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await env.Db.UpsertGameAsync(game1);
        await env.Db.UpsertGameAsync(game2);

        var trainer1 = new TrainerCatalogRecord
        {
            TrainerId = "tr-v1",
            GameId = game1.GameId,
            Provider = TrainerProviderKind.FlingMain,
            DisplayName = "HK Trainer v1",
            SourcePageUrl = "https://flingtrainer.com/trainer/hollow-knight/",
            SandboxRelativePath = "game-hk-1\\tr-v1\\hk_v1.exe",
            FileSha256 = new string('a', 64),
            ParseMode = TrainerParseMode.Structured,
        };
        var trainer2 = trainer1 with
        {
            TrainerId = "tr-v2",
            DisplayName = "HK Trainer v2",
            SandboxRelativePath = "game-hk-1\\tr-v2\\hk_v2.exe",
        };
        await env.Db.UpsertTrainerAsync(trainer1);
        await env.Db.UpsertTrainerAsync(trainer2);
        env.Catalog.SetTrainers(game1.GameId, [trainer1, trainer2]);

        await env.Db.ReplaceTrainerHotkeyBindingsAsync(trainer1.TrainerId,
        [
            new TrainerHotkeyBindingRecord
            {
                TrainerId = trainer1.TrainerId,
                EntryId = "god",
                Label = "无限生命",
                OriginalChord = "Num 1",
                EntryKind = TrainerHotkeyEntryKind.Toggle,
                SortOrder = 0,
            },
        ]);

        env.Snapshots.SnapshotsByGame[game1.GameId] =
        [
            new PreTrainerSnapshotRecord
            {
                SnapshotId = "snap-1",
                GameId = game1.GameId,
                Status = PreTrainerSnapshotStatus.Completed,
                FileCount = 2,
                TotalBytes = 1024,
            },
        ];

        using var vm = env.CreateViewModel();
        var feedbacks = new List<(string Msg, bool IsError)>();
        vm.StatusFeedbackRequested += (m, err) => feedbacks.Add((m, err));

        await vm.LoadForGameAsync(new GameListItemViewModel(game1));

        // P0-4: Single consolidated safety snapshot call (not 3 separate probes!).
        Assert.Equal(1, env.SafetyGate.EvaluateSnapshotCalls);

        // P1-1: Multi-version bound trainers switcher.
        Assert.True(vm.HasMultipleBoundTrainers);
        Assert.Equal(2, vm.BoundTrainers.Count);
        Assert.Equal("tr-v1", vm.ActiveTrainer!.TrainerId);
        await vm.SelectTrainerByIdAsync("tr-v2");
        Assert.Equal("tr-v2", vm.ActiveTrainer!.TrainerId);
        await vm.SelectTrainerByIdAsync("tr-v1");
        Assert.Equal("tr-v1", vm.ActiveTrainer!.TrainerId);

        // P0-3c: Invalid manual hotkey is rejected before saving; valid TriggerOnce entry succeeds.
        vm.NewManualHotkeyLabel = "无效测试";
        vm.NewManualHotkeyChord = "NotARealKey+++";
        await vm.AddManualHotkeyCommand.ExecuteAsync(null);
        Assert.Single(vm.Hotkeys);
        Assert.True(feedbacks.Last().IsError);

        vm.NewManualHotkeyLabel = "一键加金币";
        vm.NewManualHotkeyChord = "Ctrl+F3";
        vm.NewManualHotkeyIsTriggerOnce = true;
        await vm.AddManualHotkeyCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Hotkeys.Count);
        Assert.Equal(TrainerHotkeyEntryKind.Trigger, vm.Hotkeys[1].EntryKind);
        Assert.False(vm.Hotkeys[1].IsToggle);

        // P0-3c: Per-row MappedChord editing validates & persists.
        TrainerHotkeyRowViewModel firstRow = vm.Hotkeys[0];
        firstRow.BeginEditMappedChordCommand.Execute(null);
        Assert.True(firstRow.IsEditingMappedChord);
        firstRow.EditingMappedChordText = "BadKey???";
        await firstRow.SaveMappedChordCommand.ExecuteAsync(null);
        Assert.Null(firstRow.MappedChord);
        Assert.True(firstRow.IsEditingMappedChord);

        firstRow.EditingMappedChordText = "Alt+Num 2";
        await firstRow.SaveMappedChordCommand.ExecuteAsync(null);
        Assert.Equal("Alt+Num 2", firstRow.MappedChord);
        Assert.Equal("Alt+Num 2", firstRow.EffectiveChord);
        Assert.False(firstRow.IsEditingMappedChord);

        // P0-1a: Manual snapshot passes isManual: true.
        await vm.CreateManualSnapshotCommand.ExecuteAsync(null);
        Assert.Equal([true], env.Snapshots.ManualFlags);

        // P0-1c & P1-1: Inline rollback confirmation prevents direct overwrite on first click, emits clear prompt,
        // and supports BOTH ConfirmRollbackCommand AND second-click on same RestoreCommand (MainWindow.xaml compatible!).
        PreTrainerSnapshotItemViewModel snapItem = vm.PreTrainerSnapshots[0];
        await snapItem.RestoreCommand.ExecuteAsync(null);
        Assert.True(vm.IsRollbackConfirmOpen);
        Assert.Same(snapItem, vm.PendingRollbackSnapshot);
        Assert.Equal(0, env.Snapshots.RestoreCalls);
        Assert.Contains("一键回滚", feedbacks.Last().Msg, StringComparison.Ordinal);

        // Second click on same row's RestoreCommand completes confirmation rollback!
        await snapItem.RestoreCommand.ExecuteAsync(null);
        Assert.False(vm.IsRollbackConfirmOpen);
        Assert.Equal(1, env.Snapshots.RestoreCalls);

        // Cancel & explicit ConfirmRollbackCommand path also works.
        snapItem = vm.PreTrainerSnapshots[0];
        await snapItem.RestoreCommand.ExecuteAsync(null);
        Assert.True(vm.IsRollbackConfirmOpen);
        vm.CancelRollbackCommand.Execute(null);
        Assert.False(vm.IsRollbackConfirmOpen);

        await snapItem.RestoreCommand.ExecuteAsync(null);
        await vm.ConfirmRollbackCommand.ExecuteAsync(null);
        Assert.False(vm.IsRollbackConfirmOpen);
        Assert.Equal(2, env.Snapshots.RestoreCalls);

        // P0-3a & P1-4b: Launch -> More menu stays visible while CanModifyBoundTrainer becomes false; window toggle with 0 affected does not flip.
        env.Runtime.NextLaunchOutcome = new TrainerLaunchOutcome
        {
            Succeeded = true,
            IsTargetGameLaunchedExternally = true,
            ExternalLaunchNotice = "检测到游戏非通过 SPAWN 启动，退出游戏后请手动停止修改器。",
            Session = new TrainerSessionHandle
            {
                SessionId = "s-run-1",
                GameId = game1.GameId,
                TrainerId = trainer1.TrainerId,
                BackendKind = "ExternalTrainer",
                ElevationLevel = TrainerElevationLevel.Level1MediumIl,
                WorkerProcessId = 1001,
                TrainerProcessId = 2001,
            },
        };
        await vm.LaunchTrainerCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunningForCurrentGame);
        Assert.True(vm.ShowReadyMoreMenuButton);
        Assert.False(vm.CanModifyBoundTrainer);
        Assert.True(vm.HasExternalGameLaunchNotice);
        Assert.DoesNotContain("Level1MediumIl", feedbacks.Last().Msg, StringComparison.Ordinal);

        bool beforeVis = vm.IsOriginalWindowVisible;
        env.Runtime.NextWindowVisibilityResponse = new TrainerSetWindowVisibilityResponse { Succeeded = true, AffectedWindowCount = 0 };
        await vm.ToggleOriginalWindowVisibilityCommand.ExecuteAsync(null);
        Assert.Equal(beforeVis, vm.IsOriginalWindowVisible); // Did NOT flip on 0 windows!

        env.Runtime.NextWindowVisibilityResponse = new TrainerSetWindowVisibilityResponse { Succeeded = true, AffectedWindowCount = 1 };
        await vm.ToggleOriginalWindowVisibilityCommand.ExecuteAsync(null);
        Assert.NotEqual(beforeVis, vm.IsOriginalWindowVisible);

        // Trigger hotkey & verify stop resets in-memory ProxyState.
        await vm.TriggerHotkeyRowAsync(firstRow);
        Assert.True(firstRow.ProxyState);

        // P0-4c & P1-4a: Switching games clears SearchOverrideText & exposes OtherRunningTrainerGameTitle.
        vm.SearchOverrideText = "Hollow Knight Custom";
        await vm.LoadForGameAsync(new GameListItemViewModel(game2));
        Assert.Equal(string.Empty, vm.SearchOverrideText);
        Assert.True(vm.IsRunningForOtherGame);
        Assert.Equal("Hollow Knight", vm.OtherRunningTrainerGameTitle);
        Assert.Contains("Hollow Knight", vm.OtherRunningTrainerConflictText, StringComparison.Ordinal);

        // Switch back to game1 & stop -> ProxyState resets & inline remove confirmation protects removal.
        await vm.LoadForGameAsync(new GameListItemViewModel(game1));
        await vm.StopTrainerCommand.ExecuteAsync(null);
        Assert.All(vm.Hotkeys, h => Assert.False(h.ProxyState));
        Assert.False(vm.HasExternalGameLaunchNotice);

        await vm.RemoveTrainerCommand.ExecuteAsync(null);
        Assert.True(vm.IsRemoveConfirmOpen);
        Assert.Empty(env.Catalog.RemovedTrainerIds);
        Assert.Contains("确认", feedbacks.Last().Msg, StringComparison.Ordinal);
        Assert.Contains("移除", feedbacks.Last().Msg, StringComparison.Ordinal);

        // Second click on RemoveTrainerCommand (compatible with existing MainWindow.xaml) completes removal.
        await vm.RemoveTrainerCommand.ExecuteAsync(null);
        Assert.False(vm.IsRemoveConfirmOpen);
        Assert.Contains("tr-v1", env.Catalog.RemovedTrainerIds);
    }

    [Fact]
    public async Task TrainerWorkbench_WithInjectedSaveTimeMachine_UnifiesSnapshotListManualCreateRollbackAndDiResolution()
    {
        await using var env = await WorkbenchTestEnv.CreateAsync();

        var game = new GameRecord
        {
            GameId = "game-tm-unified",
            Title = "Unified Snapshot RPG",
            InstallDir = Path.Combine(env.TempDir, "RPG"),
            ExecutableRelPath = "rpg.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await env.Db.UpsertGameAsync(game);

        var trainer = new TrainerCatalogRecord
        {
            TrainerId = "tr-tm-1",
            GameId = game.GameId,
            Provider = TrainerProviderKind.LocalImport,
            DisplayName = "RPG Trainer",
            SandboxRelativePath = "game-tm-unified\\tr-tm-1\\trainer.exe",
            FileSha256 = new string('d', 64),
        };
        await env.Db.UpsertTrainerAsync(trainer);
        env.Catalog.SetTrainers(game.GameId, [trainer]);

        DateTimeOffset t0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        env.Snapshots.SnapshotsByGame[game.GameId] =
        [
            new PreTrainerSnapshotRecord
            {
                SnapshotId = "legacy-only-1",
                GameId = game.GameId,
                Status = PreTrainerSnapshotStatus.Completed,
                FileCount = 1,
                TotalBytes = 512,
                CreatedAtUtc = t0,
            },
            new PreTrainerSnapshotRecord
            {
                SnapshotId = "legacy-already-imported",
                GameId = game.GameId,
                Status = PreTrainerSnapshotStatus.Completed,
                FileCount = 1,
                TotalBytes = 512,
                CreatedAtUtc = t0.AddMinutes(1),
            },
        ];

        var timeMachine = new FakeTimeMachineService(game.GameId,
        [
            new SaveSnapshotRecord
            {
                SnapshotId = "tm-pretrainer-1",
                GameId = game.GameId,
                BranchId = "main",
                TriggerKind = SaveSnapshotTriggerKind.PreTrainerImport,
                FileCount = 3,
                TotalRawBytes = 4096,
                CreatedMachineId = "m1",
                CreatedAtUtc = t0.AddMinutes(5),
                LegacyPreTrainerSnapshotId = "legacy-already-imported",
            },
            new SaveSnapshotRecord
            {
                SnapshotId = "tm-manual-non-trainer",
                GameId = game.GameId,
                BranchId = "main",
                TriggerKind = SaveSnapshotTriggerKind.Manual,
                FileCount = 2,
                TotalRawBytes = 2048,
                CreatedMachineId = "m1",
                CreatedAtUtc = t0.AddMinutes(6),
            },
        ]);

        // Verify Microsoft.Extensions.DependencyInjection resolves TrainerWorkbenchViewModel cleanly and injects ISaveTimeMachineService!
        var services = new ServiceCollection();
        services.AddSingleton(env.Paths);
        services.AddSingleton(env.Db);
        services.AddSingleton<ITrainerCatalogService>(env.Catalog);
        services.AddSingleton<ITrainerRuntimeService>(env.Runtime);
        services.AddSingleton<IPreTrainerSaveSnapshotService>(env.Snapshots);
        services.AddSingleton<ITrainerSafetyGate>(env.SafetyGate);
        services.AddSingleton<IAntiCheatGuardService>(env.Guard);
        services.AddSingleton<IGameLibraryService>(env.Library);
        services.AddSingleton<IGameLaunchService>(env.Launch);
        services.AddSingleton<ISaveTimeMachineService>(timeMachine);
        services.AddSingleton<TrainerWorkbenchViewModel>();

        await using ServiceProvider sp = services.BuildServiceProvider();
        TrainerWorkbenchViewModel vm = sp.GetRequiredService<TrainerWorkbenchViewModel>();

        await vm.LoadForGameAsync(new GameListItemViewModel(game));

        // Merged list contains Time Machine PreTrainerImport + non-duplicate legacy snapshot (deduplicated & sorted newest first).
        Assert.Equal(2, vm.PreTrainerSnapshots.Count);
        Assert.Equal("tm-pretrainer-1", vm.PreTrainerSnapshots[0].SnapshotId);
        Assert.True(vm.PreTrainerSnapshots[0].IsTimeMachineSnapshot);
        Assert.Contains("时光机", vm.PreTrainerSnapshots[0].TriggerBadgeText, StringComparison.Ordinal);
        Assert.Equal("legacy-only-1", vm.PreTrainerSnapshots[1].SnapshotId);
        Assert.False(vm.PreTrainerSnapshots[1].IsTimeMachineSnapshot);

        // LaunchTrainerAsync preserves outcome.PreSnapshot.Message verbatim when Completed!
        env.Runtime.NextLaunchOutcome = new TrainerLaunchOutcome
        {
            Succeeded = true,
            PreSnapshot = new PreTrainerSnapshotResult
            {
                Succeeded = true,
                Status = PreTrainerSnapshotStatus.Completed,
                Message = "已通过存档时光机创建启动前快照（3 个文件）。",
                Snapshot = new PreTrainerSnapshotRecord
                {
                    SnapshotId = "tm-pretrainer-1",
                    GameId = game.GameId,
                    Status = PreTrainerSnapshotStatus.Completed,
                    FileCount = 3,
                },
            },
            Session = new TrainerSessionHandle
            {
                SessionId = "s-tm-1",
                GameId = game.GameId,
                TrainerId = trainer.TrainerId,
                BackendKind = "ExternalTrainer",
                ElevationLevel = TrainerElevationLevel.Level1MediumIl,
                WorkerProcessId = 1001,
                TrainerProcessId = 2001,
            },
        };
        await vm.LaunchTrainerCommand.ExecuteAsync(null);
        Assert.Equal("已通过存档时光机创建启动前快照（3 个文件）。", vm.LatestSnapshotNoticeText);
        await vm.StopTrainerCommand.ExecuteAsync(null);

        // Manual snapshot prefers ISaveTimeMachineService (PreTrainerImport) and immediately appears in PreTrainerSnapshots!
        await vm.CreateManualSnapshotCommand.ExecuteAsync(null);
        Assert.Single(timeMachine.CreateRequests);
        Assert.Equal(SaveSnapshotTriggerKind.PreTrainerImport, timeMachine.CreateRequests[0].TriggerKind);
        Assert.Empty(env.Snapshots.ManualFlags);
        Assert.Equal(3, vm.PreTrainerSnapshots.Count);
        Assert.Equal("tm-created-1", vm.PreTrainerSnapshots[0].SnapshotId);

        // Rollback routes Time Machine snapshot to ISaveTimeMachineService.RollbackToSnapshotAsync (CreateSafetySnapshot = true),
        // and legacy snapshot to IPreTrainerSaveSnapshotService.RestoreAsync!
        PreTrainerSnapshotItemViewModel tmItem = vm.PreTrainerSnapshots[0];
        await tmItem.RestoreCommand.ExecuteAsync(null);
        await tmItem.RestoreCommand.ExecuteAsync(null);
        Assert.Single(timeMachine.RollbackRequests);
        Assert.Equal("tm-created-1", timeMachine.RollbackRequests[0].SnapshotId);
        Assert.True(timeMachine.RollbackRequests[0].CreateSafetySnapshot);
        Assert.Equal(0, env.Snapshots.RestoreCalls);

        PreTrainerSnapshotItemViewModel legacyItem = vm.PreTrainerSnapshots.Single(s => s.SnapshotId == "legacy-only-1");
        await legacyItem.RestoreCommand.ExecuteAsync(null);
        await legacyItem.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(1, env.Snapshots.RestoreCalls);
    }

    [Fact]
    public async Task TrainerWorkbench_GuardBannerHybridLauncherWarningOfflineRetryAndCandidateChallenge_BehaveAccurately()
    {
        await using var env = await WorkbenchTestEnv.CreateAsync();

        var protectedGame = new GameRecord
        {
            GameId = "g-cs2",
            Title = "Counter-Strike 2",
            InstallDir = Path.Combine(env.TempDir, "CS2"),
            ExecutableRelPath = "cs2.exe",
            AntiCheatTier = AntiCheatTier.Tier2,
        };
        var hybridGame = new GameRecord
        {
            GameId = "g-er",
            Title = "Elden Ring",
            InstallDir = Path.Combine(env.TempDir, "ER"),
            ExecutableRelPath = "Game\\start_protected_game.exe",
            AntiCheatTier = AntiCheatTier.Tier2,
        };
        await env.Db.UpsertGameAsync(protectedGame);
        await env.Db.UpsertGameAsync(hybridGame);

        using var vm = env.CreateViewModel();

        // P0-2: Guard lock banner lists exact residual process/driver names and active protected game title.
        vm.UpdateGuardSnapshot(new AntiCheatGuardSnapshot(
            AntiCheatGuardPhase.ResidualGuardWatch,
            IsHighRiskLocked: true,
            RemainingCooldownSeconds: 0,
            ActiveProtectedGameId: null,
            DetectedResidualProcesses: ["EasyAntiCheat_EOS.exe", "BEDaisy.sys"]));
        Assert.Contains("EasyAntiCheat_EOS.exe", vm.GuardLockBannerText, StringComparison.Ordinal);
        Assert.Contains("BEDaisy.sys", vm.GuardLockBannerText, StringComparison.Ordinal);

        env.Guard.CurrentSnapshot = new AntiCheatGuardSnapshot(
            AntiCheatGuardPhase.ProtectedRunning,
            IsHighRiskLocked: true,
            RemainingCooldownSeconds: 0,
            ActiveProtectedGameId: protectedGame.GameId,
            DetectedResidualProcesses: []);
        vm.UpdateGuardSnapshot(env.Guard.CurrentSnapshot);
        await vm.RecheckGuardStatusCommand.ExecuteAsync(null);
        Assert.Contains("Counter-Strike 2", vm.GuardLockBannerText, StringComparison.Ordinal);

        vm.UpdateGuardSnapshot(new AntiCheatGuardSnapshot(
            AntiCheatGuardPhase.CooldownLocked,
            IsHighRiskLocked: true,
            RemainingCooldownSeconds: 12.5,
            ActiveProtectedGameId: null,
            DetectedResidualProcesses: []));
        Assert.Contains("余温冷却中", vm.GuardLockBannerText, StringComparison.Ordinal);

        // P0-4b: Hybrid Tier 2 game configured with start_protected_game.exe shows AC launcher warning.
        env.SafetyGate.NextSnapshotFactory = g => new TrainerGameSafetySnapshot
        {
            FourLayerReport = new FourLayerAntiCheatReport { EffectiveTier = AntiCheatTier.Tier2, HasSinglePlayerCategory = true },
            LiveProbe = new LiveAntiCheatProbeReport(),
            OfflineBypass = new VerifiedOfflineBypass
            {
                GameId = g.GameId,
                StaticAntiCheatTier = AntiCheatTier.Tier2,
                IsEligible = true,
                HasSinglePlayerMode = true,
                IsLiveAntiCheatClear = true,
            },
            AllowanceDecision = new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.RequiresOfflineConfirmation,
                IsAllowed = false,
                CanConfirmOfflineBypass = true,
                Message = "请确认离线无 AC 模式。",
                StaticGameTier = AntiCheatTier.Tier2,
                GuardPhase = AntiCheatGuardPhase.IdleSafe,
            },
        };
        await vm.LoadForGameAsync(new GameListItemViewModel(hybridGame));
        Assert.True(vm.ShowHybridOfflinePanel);
        Assert.True(vm.IsHybridExecutableAntiCheatLauncher);
        Assert.Contains("start_protected_game.exe", vm.HybridAntiCheatLauncherWarningText, StringComparison.Ordinal);
        Assert.Contains("离线无 AC 主程序", vm.HybridAntiCheatLauncherWarningText, StringComparison.Ordinal);

        // P0-2: Offline Tier 2 conservative block exposes RetrySafetyVerificationCommand.
        env.SafetyGate.NextSnapshotFactory = g => new TrainerGameSafetySnapshot
        {
            FourLayerReport = new FourLayerAntiCheatReport { EffectiveTier = AntiCheatTier.Tier2, OfflineConservativeFallbackApplied = true },
            LiveProbe = new LiveAntiCheatProbeReport(),
            OfflineBypass = new VerifiedOfflineBypass { GameId = g.GameId, StaticAntiCheatTier = AntiCheatTier.Tier2 },
            AllowanceDecision = new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.BlockedByTier,
                IsAllowed = false,
                IsOfflineSinglePlayerVerificationPending = true,
                Message = "当前处于离线状态，无法联网核验单人模式资格，请联网后点击重试。",
                StaticGameTier = AntiCheatTier.Tier2,
                GuardPhase = AntiCheatGuardPhase.IdleSafe,
            },
        };
        await vm.LoadForGameAsync(new GameListItemViewModel(protectedGame));
        Assert.True(vm.ShowProtectedFenceCard);
        Assert.True(vm.IsOfflineSinglePlayerVerificationPending);
        Assert.True(vm.CanRetryOfflineVerification);
        Assert.Contains("离线核验保守拦截", vm.ProtectedFenceTitleText, StringComparison.Ordinal);

        // P1-3: Candidate without loaded attachments defaults primary action to loading details (not blind download) & triggers browser fallback card on challenge!
        var tier0 = new GameRecord
        {
            GameId = "g-t0",
            Title = "Test Indie",
            InstallDir = Path.Combine(env.TempDir, "Indie"),
            ExecutableRelPath = "indie.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        };
        await env.Db.UpsertGameAsync(tier0);
        env.SafetyGate.NextSnapshotFactory = null;
        await vm.LoadForGameAsync(new GameListItemViewModel(tier0));

        env.Catalog.NextMatchResult = new TrainerMatchResult
        {
            GameId = tier0.GameId,
            EffectiveQuery = "Test Indie",
            QuerySource = TrainerTitleSource.AcfName,
            Candidates =
            [
                new TrainerCandidate
                {
                    CandidateId = "c-no-att",
                    Provider = TrainerProviderKind.FlingMain,
                    Title = "Test Indie Trainer",
                    PageUrl = "https://flingtrainer.com/trainer/test-indie/",
                    ConfidenceReason = "按 Steam BuildId 匹配",
                    Attachments = [],
                },
            ],
        };
        env.Catalog.NextDetailsResult = new TrainerCandidateDetailsResult
        {
            Candidate = env.Catalog.NextMatchResult.Candidates[0],
            Succeeded = false,
            ChallengeDetected = true,
            FallbackBrowserUrl = "https://flingtrainer.com/trainer/test-indie/",
            ErrorMessage = "详情页触发 Cloudflare 验证。",
        };

        await vm.MatchTrainersCommand.ExecuteAsync(null);
        Assert.Contains("Steam 清单名称", vm.EffectiveSearchQueryLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("AcfName", vm.EffectiveSearchQueryLabel, StringComparison.Ordinal);

        TrainerCandidateItemViewModel candVm = Assert.Single(vm.MatchCandidates);
        Assert.Equal("查看版本与热键", candVm.DownloadButtonLabel);
        Assert.Equal("按 Steam BuildId 匹配", candVm.ConfidenceReasonText);

        await candVm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(0, env.Catalog.DownloadCalls); // Did NOT blind-download!
        Assert.Equal(1, env.Catalog.DetailsCalls);
        Assert.True(vm.ShowBrowserFallbackCard);
        Assert.Equal("https://flingtrainer.com/trainer/test-indie/", vm.FallbackBrowserUrl);

        // P1-3: Multi-EXE choice exposes size, arch, and elevation summary text.
        var choice = new TrainerExecutableChoice
        {
            RelativePath = "bin/Trainer_x64.exe",
            FileSize = 2048 * 1024,
            PeMachine = "x64",
            RequiresAdmin = true,
        };
        Assert.Contains("2.00 MB", choice.DisplaySummary, StringComparison.Ordinal);
        Assert.Contains("x64", choice.DisplaySummary, StringComparison.Ordinal);
        Assert.Contains("需管理员", choice.DisplaySummary, StringComparison.Ordinal);
    }

    private sealed class WorkbenchTestEnv : IAsyncDisposable
    {
        public string TempDir { get; }
        public SpawnPaths Paths { get; }
        public SpawnDatabase Db { get; }
        public FakeGuard Guard { get; } = new();
        public FakeCatalog Catalog { get; }
        public FakeRuntime Runtime { get; } = new();
        public FakeSnapshots Snapshots { get; } = new();
        public FakeSafetyGate SafetyGate { get; } = new();
        public FakeLibrary Library { get; }
        public FakeLaunch Launch { get; } = new();

        private WorkbenchTestEnv(string tempDir, SpawnPaths paths, SpawnDatabase db)
        {
            TempDir = tempDir;
            Paths = paths;
            Db = db;
            Catalog = new FakeCatalog(db);
            Library = new FakeLibrary(db);
        }

        public static async Task<WorkbenchTestEnv> CreateAsync()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-wb-ux-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            var db = new SpawnDatabase(paths);
            await db.InitializeAsync();
            return new WorkbenchTestEnv(tempDir, paths, db);
        }

        public TrainerWorkbenchViewModel CreateViewModel(Func<string, IReadOnlyList<SafeProcessEntry>>? processFinder = null) =>
            new(
                Paths,
                Db,
                Catalog,
                Runtime,
                Snapshots,
                SafetyGate,
                Guard,
                Library,
                Launch,
                openExternalUrl: _ => { },
                copyToClipboard: _ => { },
                directoryProcessFinder: processFinder ?? (_ => []));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(TempDir))
                {
                    Directory.Delete(TempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private sealed class FakeGuard : IAntiCheatGuardService
    {
        public AntiCheatGuardSnapshot CurrentSnapshot { get; set; } = new(AntiCheatGuardPhase.IdleSafe, false, 0, null, []);
        public event EventHandler<AntiCheatGuardSnapshot>? StateChanged { add { } remove { } }
        public AntiCheatInspectionResult InspectGameDirectory(string installDir) => new(AntiCheatTier.Tier0, null, []);
        public HighRiskEnvironmentReport InspectHighRiskEnvironment() => new([], []);
        public void EnsureHighRiskOperationPermitted(AntiCheatTier targetGameTier, string operationName) { }
        public void NotifyProtectedGameStarted(string gameId, AntiCheatTier tier) { }
        public Task NotifyProtectedGameExitedAsync(string gameId, AntiCheatTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSafetyGate : ITrainerSafetyGate
    {
        public int EvaluateSnapshotCalls { get; private set; }
        public Func<GameRecord, TrainerGameSafetySnapshot>? NextSnapshotFactory { get; set; }
        public bool IsWatchdogActive => false;
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered { add { } remove { } }

        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport { EffectiveTier = game.AntiCheatTier });
        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveAntiCheatProbeReport());
        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default) =>
            Task.FromResult(new VerifiedOfflineBypass { GameId = game.GameId, StaticAntiCheatTier = game.AntiCheatTier, IsLiveAntiCheatClear = true });
        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default) =>
            Task.FromResult(new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.Allowed,
                IsAllowed = true,
                Message = "允许",
                StaticGameTier = game.AntiCheatTier,
                GuardPhase = AntiCheatGuardPhase.IdleSafe,
            });

        public Task<TrainerGameSafetySnapshot> EvaluateGameSafetySnapshotAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default)
        {
            EvaluateSnapshotCalls++;
            if (NextSnapshotFactory is not null)
            {
                return Task.FromResult(NextSnapshotFactory(game));
            }

            return Task.FromResult(new TrainerGameSafetySnapshot
            {
                FourLayerReport = new FourLayerAntiCheatReport { EffectiveTier = game.AntiCheatTier },
                LiveProbe = new LiveAntiCheatProbeReport(),
                OfflineBypass = new VerifiedOfflineBypass { GameId = game.GameId, StaticAntiCheatTier = game.AntiCheatTier, IsLiveAntiCheatClear = true },
                AllowanceDecision = new TrainerAllowanceDecision
                {
                    Outcome = TrainerAllowanceOutcome.Allowed,
                    IsAllowed = true,
                    Message = "允许",
                    StaticGameTier = game.AntiCheatTier,
                    GuardPhase = AntiCheatGuardPhase.IdleSafe,
                },
            });
        }

        public void NotifyTrainerSessionStarted(string sessionId, string gameId) { }
        public void NotifyTrainerSessionEnded(string sessionId) { }
    }

    private sealed class FakeCatalog(SpawnDatabase db) : ITrainerCatalogService
    {
        private readonly Dictionary<string, List<TrainerCatalogRecord>> _byGame = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RemovedTrainerIds { get; } = [];
        public int DownloadCalls { get; private set; }
        public int DetailsCalls { get; private set; }
        public TrainerMatchResult? NextMatchResult { get; set; }
        public TrainerCandidateDetailsResult? NextDetailsResult { get; set; }

        public void SetTrainers(string gameId, IReadOnlyList<TrainerCatalogRecord> trainers) =>
            _byGame[gameId] = [.. trainers];

        public Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default) =>
            Task.FromResult(NextMatchResult ?? new TrainerMatchResult { GameId = gameId, EffectiveQuery = "Demo" });

        public Task<TrainerCandidateDetailsResult> LoadCandidateDetailsWithDiagnosticsAsync(string gameId, TrainerCandidate candidate, CancellationToken ct = default)
        {
            DetailsCalls++;
            return Task.FromResult(NextDetailsResult ?? new TrainerCandidateDetailsResult { Candidate = candidate, Succeeded = true });
        }

        public Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            return Task.FromResult(new TrainerImportResult { Succeeded = false });
        }

        public Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default) =>
            Task.FromResult(new TrainerImportResult { Succeeded = false });

        public Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult(new TrainerIntegrityReport { TrainerId = trainerId, IsValid = true, FileExists = true, HashMatches = true, IsInsideSandbox = true });

        public Task RemoveAsync(string trainerId, CancellationToken ct = default)
        {
            RemovedTrainerIds.Add(trainerId);
            foreach (var list in _byGame.Values)
            {
                list.RemoveAll(t => t.TrainerId == trainerId);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TrainerCatalogRecord>> ListForGameAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrainerCatalogRecord>>(_byGame.TryGetValue(gameId, out var list) ? list.ToArray() : []);

        public Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
            db.GetTrainerHotkeyBindingsAsync(trainerId, ct);

        public Task SaveHotkeyBindingsAsync(string trainerId, IReadOnlyList<TrainerHotkeyBindingRecord> bindings, CancellationToken ct = default) =>
            db.ReplaceTrainerHotkeyBindingsAsync(trainerId, bindings, ct);
    }

    private sealed class FakeRuntime : ITrainerRuntimeService
    {
        public TrainerRuntimeState State { get; private set; } = TrainerRuntimeState.Ready;
        public TrainerSessionHandle? ActiveSession { get; private set; }
        public TrainerLaunchOutcome? NextLaunchOutcome { get; set; }
        public TrainerSetWindowVisibilityResponse NextWindowVisibilityResponse { get; set; } = new() { Succeeded = true, AffectedWindowCount = 1 };
        public event EventHandler<TrainerRuntimeStateChanged>? StateChanged;

        public Task<TrainerLaunchOutcome> LaunchAsync(string trainerId, TrainerLaunchOptions options, CancellationToken ct = default)
        {
            TrainerLaunchOutcome outcome = NextLaunchOutcome ?? new TrainerLaunchOutcome { Succeeded = false, Message = "unconfigured" };
            if (outcome.Succeeded && outcome.Session is not null)
            {
                ActiveSession = outcome.Session;
                State = TrainerRuntimeState.Running;
            }

            return Task.FromResult(outcome);
        }

        public Task TriggerHotkeyAsync(string sessionId, string entryId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOriginalWindowVisibleAsync(string sessionId, bool visible, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TrainerSetWindowVisibilityResponse> SetOriginalWindowVisibleWithResultAsync(string sessionId, bool visible, CancellationToken ct = default) =>
            Task.FromResult(NextWindowVisibilityResponse);

        public Task StopAsync(string sessionId, CancellationToken ct = default)
        {
            TrainerSessionHandle? prev = ActiveSession;
            ActiveSession = null;
            State = TrainerRuntimeState.Ready;
            if (prev is not null)
            {
                StateChanged?.Invoke(this, new TrainerRuntimeStateChanged
                {
                    GameId = prev.GameId,
                    TrainerId = prev.TrainerId,
                    PreviousState = TrainerRuntimeState.Running,
                    CurrentState = TrainerRuntimeState.Ready,
                });
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeSnapshots : IPreTrainerSaveSnapshotService
    {
        public Dictionary<string, List<PreTrainerSnapshotRecord>> SnapshotsByGame { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<bool> ManualFlags { get; } = [];
        public int RestoreCalls { get; private set; }

        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default) =>
            CreateAsync(gameId, trainerId, isManual: false, ct);

        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, bool isManual, CancellationToken ct = default)
        {
            ManualFlags.Add(isManual);
            return Task.FromResult(new PreTrainerSnapshotResult
            {
                Succeeded = true,
                Status = PreTrainerSnapshotStatus.Completed,
                Message = "手动快照已创建",
            });
        }

        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>(
                SnapshotsByGame.TryGetValue(gameId, out var list) ? list.ToArray() : []);

        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default)
        {
            RestoreCalls++;
            return Task.FromResult(new PreTrainerRestoreResult { Succeeded = true, RestoredFileCount = 2 });
        }
    }

    private sealed class FakeLibrary(SpawnDatabase db) : IGameLibraryService
    {
        public Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default) => db.GetAllGamesAsync(cancellationToken);
        public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public Task<GameRecord?> ImportGameAsync(string directoryOrExePath, CancellationToken cancellationToken = default) => Task.FromResult<GameRecord?>(null);
        public Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameRecord>>([]);
        public Task<GameRecord?> RescanGameAsync(string gameId, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public Task<GameRecord?> UpdateGameMetadataAsync(string gameId, GameMetadataUpdate update, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public Task<bool> RemoveGameAsync(string gameId, CancellationToken cancellationToken = default) => db.DeleteGameAsync(gameId, cancellationToken);
    }

    private sealed class FakeLaunch : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged { add { } remove { } }
        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameLaunchResult.Failure("UNUSED", "Unused"));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }

    private sealed class FakeTimeMachineService(string gameId, IReadOnlyList<SaveSnapshotRecord> initialSnapshots)
        : ISaveTimeMachineService
    {
        private readonly List<SaveSnapshotRecord> _snapshots = [.. initialSnapshots];
        private int _createdSeq;

        public List<CreateSaveSnapshotRequest> CreateRequests { get; } = [];
        public List<RollbackSaveRequest> RollbackRequests { get; } = [];
        public event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged { add { } remove { } }

        public Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string requestedGameId, CancellationToken ct = default)
        {
            var nodes = _snapshots
                .Where(s => string.Equals(s.GameId, requestedGameId, StringComparison.OrdinalIgnoreCase))
                .Select(s => new SaveTreeNodeDto
                {
                    Snapshot = s,
                    BranchName = "main",
                })
                .ToArray();

            return Task.FromResult(new SaveTreeGraphDto
            {
                GameId = requestedGameId,
                Nodes = nodes,
            });
        }

        public Task<CreateSaveSnapshotResult> CreateSnapshotAsync(CreateSaveSnapshotRequest request, CancellationToken ct = default)
        {
            CreateRequests.Add(request);
            _createdSeq++;
            var snap = new SaveSnapshotRecord
            {
                SnapshotId = $"tm-created-{_createdSeq}",
                GameId = request.GameId,
                BranchId = "main",
                TriggerKind = request.TriggerKind,
                Note = request.Note,
                FileCount = 4,
                TotalRawBytes = 8192,
                CreatedMachineId = "m1",
                CreatedAtUtc = new DateTimeOffset(2026, 11, 21, 12, 0, _createdSeq, TimeSpan.Zero),
            };
            _snapshots.Add(snap);
            return Task.FromResult(new CreateSaveSnapshotResult
            {
                Succeeded = true,
                Message = $"已通过存档时光机创建快照（{snap.FileCount} 个文件）",
                Snapshot = snap,
            });
        }

        public Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default)
        {
            RollbackRequests.Add(request);
            return Task.FromResult(new SaveRestoreOutcome
            {
                Succeeded = true,
                Message = "已通过存档时光机恢复快照。",
                RestoredFileCount = 4,
            });
        }

        public Task<SaveGameStateRecord> GetGameStateAsync(string g, CancellationToken ct = default) =>
            Task.FromResult(new SaveGameStateRecord { GameId = g });
        public Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default) =>
            Task.FromResult(state);
        public Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new SaveSettingsDto());
        public Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SaveLibraryGameOverviewDto>>([]);
        public Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SaveSnapshotFileRecord>>([]);
        public Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default) => Task.FromResult(new CasVaultStatsDto());
        public Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(string l, string r, CancellationToken ct = default) =>
            Task.FromResult(new SaveSnapshotDiffDto { BaseLabel = l, TargetLabel = r });
        public Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(new SaveSnapshotDiffDto { BaseLabel = id, TargetLabel = "disk" });
        public Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ForkBranchResult { Succeeded = false, Message = gameId });
        public Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SaveRestoreOutcome { Succeeded = false, Message = "unused" });
        public Task<SaveBranchRecord?> RenameBranchAsync(string branchId, string newName, string? description = null, string? colorHex = null, CancellationToken ct = default) =>
            Task.FromResult<SaveBranchRecord?>(null);
        public Task<DeleteBranchResult> DeleteBranchAsync(string branchId, bool deleteSnapshots = false, CancellationToken ct = default) =>
            Task.FromResult(new DeleteBranchResult(false, "unused"));
        public Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default) => Task.FromResult(false);
        public Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default) =>
            Task.FromResult(new DeleteSnapshotResult(false, "unused"));
        public Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(string snapshotId, string targetDirectory, CancellationToken ct = default) =>
            Task.FromResult(new ExportSnapshotResult(false, "unused"));
        public Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(string g, CancellationToken ct = default) =>
            Task.FromResult(new LegacyPreTrainerImportReport(0, 0, 0, [], []));
        public Task<SaveGcReport> RunCasGarbageCollectionAsync(TimeSpan? gracePeriodOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new SaveGcReport(0, 0, 0, 0));
    }
}
