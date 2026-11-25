using System.Text;
using Spawn.Contracts;
using Spawn.Modules.Progress.Saves.Engine;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Saves;

/// <summary>
/// Headless deterministic tests for anti-tear retry/skip protection, running-game guards,
/// safety-fence enforcement, and <see cref="SaveWatcherService"/> quiescent debounce + PostExit capture.
/// </summary>
public sealed class SaveWatcherAndRollbackTests
{
    [Fact]
    public async Task AntiTearCapture_RetriesOnTransientMutationAndSkipsOnPersistentTear()
    {
        int transientMutateCalls = 0;
        await using var envTransient = await SaveEngineTestFixture.CreateAsync(
            afterReadHook: (path, attempt) =>
            {
                transientMutateCalls++;
                if (attempt == 1)
                {
                    File.WriteAllText(path, "MUTATED-ONCE-STABLE-ON-RETRY", Encoding.UTF8);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
                }
            });

        await envTransient.WriteSaveFileAsync("hero.sav", "INITIAL-CONTENT");
        CreateSaveSnapshotResult retryRes = await envTransient.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = envTransient.Game.GameId,
        });
        Assert.True(retryRes.Succeeded, retryRes.Message);
        Assert.Equal(2, transientMutateCalls);

        // Persistent tear on all 3 attempts -> returns structured Skipped (ConcurrentModificationDetected)
        int persistentMutateCalls = 0;
        await using var envPersistent = await SaveEngineTestFixture.CreateAsync(
            afterReadHook: (path, attempt) =>
            {
                persistentMutateCalls++;
                File.WriteAllText(path, $"MUTATED-ATTEMPT-{attempt}-{new string('X', attempt * 10)}", Encoding.UTF8);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(attempt * 10));
            });

        await envPersistent.WriteSaveFileAsync("hero.sav", "START");
        CreateSaveSnapshotResult tearRes = await envPersistent.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = envPersistent.Game.GameId,
        });
        Assert.False(tearRes.Succeeded);
        Assert.True(tearRes.Skipped);
        Assert.Equal(SaveSnapshotSkipReason.ConcurrentModificationDetected, tearRes.SkipReason);
        Assert.Equal(AntiTearFileCapture.MaxConsistencyAttempts, persistentMutateCalls);
    }

    [Fact]
    public async Task AntiTearCapture_SkipsOnExclusiveLockAndSizeThresholdExceeded()
    {
        // 1. Exclusive file lock (ERROR_SHARING_VIOLATION) -> structured skip, never throws
        await using var envLock = await SaveEngineTestFixture.CreateAsync();
        await envLock.WriteSaveFileAsync("locked.sav", "LOCKED-DATA");
        string lockedPath = Path.Combine(envLock.SaveDir, "locked.sav");

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            CreateSaveSnapshotResult res = await envLock.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
            {
                GameId = envLock.Game.GameId,
            });
            Assert.False(res.Succeeded);
            Assert.True(res.Skipped);
            Assert.Equal(SaveSnapshotSkipReason.FileLockedSharingViolation, res.SkipReason);
        }

        // 2. Size threshold exceeded -> non-blocking skip
        await using var envCap = await SaveEngineTestFixture.CreateAsync(maxBytesOverride: 64);
        await envCap.WriteSaveFileAsync("big.sav", new string('A', 256));
        CreateSaveSnapshotResult capRes = await envCap.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = envCap.Game.GameId,
        });
        Assert.False(capRes.Succeeded);
        Assert.True(capRes.Skipped);
        Assert.Equal(SaveSnapshotSkipReason.SizeThresholdExceeded, capRes.SkipReason);
    }

    [Fact]
    public async Task RunningGameAndSafetyFence_BlockRollbackAndBranchSwitchWithoutTouchingDisk()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync();

        await env.WriteSaveFileAsync("slot.sav", "V1");
        CreateSaveSnapshotResult s1 = await env.Service.CreateSnapshotAsync(new CreateSaveSnapshotRequest
        {
            GameId = env.Game.GameId,
        });
        ForkBranchResult b2 = await env.Service.CreateBranchAsync(new ForkBranchRequest
        {
            GameId = env.Game.GameId,
            FromSnapshotId = s1.Snapshot!.SnapshotId,
            BranchName = "alt",
            SwitchToNewBranch = false,
        });
        Assert.True(b2.Succeeded);

        env.AdvanceMinutes(5);
        await env.WriteSaveFileAsync("slot.sav", "V2-LIVE");

        // 1. While game is running, both RollbackToSnapshotAsync and SwitchBranchAsync MUST refuse!
        env.GameLaunchService.SetRunning(env.Game.GameId, true);
        SaveRestoreOutcome runningRollback = await env.Service.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = s1.Snapshot.SnapshotId,
        });
        Assert.False(runningRollback.Succeeded);
        Assert.Contains("正在运行", runningRollback.Message);

        SaveRestoreOutcome runningSwitch = await env.Service.SwitchBranchAsync(new SwitchBranchRequest
        {
            GameId = env.Game.GameId,
            TargetBranchId = b2.Branch!.BranchId,
        });
        Assert.False(runningSwitch.Succeeded);
        Assert.Contains("正在运行", runningSwitch.Message);
        Assert.Equal("V2-LIVE", await env.ReadSaveFileAsync("slot.sav"));

        // 2. Stop game, enable safety fence block -> rollback MUST refuse before touching disk!
        env.GameLaunchService.SetRunning(env.Game.GameId, false);
        env.GameDataService.BlockAllRestoreTargets = true;
        SaveRestoreOutcome fenced = await env.Service.RollbackToSnapshotAsync(new RollbackSaveRequest
        {
            SnapshotId = s1.Snapshot.SnapshotId,
            CreateSafetySnapshot = false,
        });
        Assert.False(fenced.Succeeded);
        Assert.Equal("V2-LIVE", await env.ReadSaveFileAsync("slot.sav"));
    }

    [Fact]
    public async Task SaveWatcherService_CapturesAfterQuiescentDebounceAndOnPostExitViaEventBus()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync(explicitTimeProvider: TimeProvider.System);
        await env.WriteSaveFileAsync("world.sav", "INITIAL-WORLD");

        await using var watcher = new SaveWatcherService(
            env.Service,
            env.RuleResolver,
            env.EventBus,
            env.Database,
            TimeProvider.System,
            new SaveWatcherOptions
            {
                QuiescentDebounceOverride = TimeSpan.FromMilliseconds(120),
                PostExitSettleDelay = TimeSpan.FromMilliseconds(30),
            });

        await watcher.StartAsync();

        var quiescentCaptured = new TaskCompletionSource<SaveTreeChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postExitCaptured = new TaskCompletionSource<SaveTreeChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        env.Service.SaveTreeChanged += (_, ev) =>
        {
            if (ev.Reason == "SnapshotCreated")
            {
                if (!quiescentCaptured.Task.IsCompleted)
                {
                    quiescentCaptured.TrySetResult(ev);
                }
                else
                {
                    postExitCaptured.TrySetResult(ev);
                }
            }
        };

        // 1. Simulate game start event on ISpawnEventBus -> watcher attaches automatically
        await env.EventBus.PublishAsync(new ActiveGameSessionChangedEvent(
            GameId: env.Game.GameId,
            Session: new ActiveGameSession
            {
                SessionId = "s1",
                GameId = env.Game.GameId,
                GameTitle = env.Game.Title,
                InstallDir = env.Game.InstallDir,
                StartedUtc = DateTimeOffset.UtcNow,
                State = PlaySessionActivityState.Active,
            },
            CurrentState: PlaySessionActivityState.Active));

        Assert.Equal(SaveWatcherStatusKind.Watching, watcher.GetStatus(env.Game.GameId).Status);

        // 2. Write save file -> watcher debounces and creates an AutoQuiescent snapshot!
        await env.WriteSaveFileAsync("world.sav", "QUICKSAVE-AFTER-BOSS");
        SaveTreeChangedEvent qEvent = await quiescentCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(qEvent.SnapshotId);

        SaveSnapshotRecord? qSnap = await env.Database.GetSaveSnapshotByIdAsync(qEvent.SnapshotId!);
        Assert.NotNull(qSnap);
        Assert.Equal(SaveSnapshotTriggerKind.AutoQuiescent, qSnap!.TriggerKind);

        // 3. Mutate save right before exit and publish Stopped -> watcher stops FS watching and creates PostExit snapshot!
        await env.WriteSaveFileAsync("world.sav", "FINAL-EXIT-SAVE");
        await env.EventBus.PublishAsync(new ActiveGameSessionChangedEvent(
            GameId: env.Game.GameId,
            Session: null,
            CurrentState: PlaySessionActivityState.Stopped));

        SaveTreeChangedEvent exitEvent = await postExitCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(exitEvent.SnapshotId);

        SaveSnapshotRecord? exitSnap = await env.Database.GetSaveSnapshotByIdAsync(exitEvent.SnapshotId!);
        Assert.NotNull(exitSnap);
        Assert.Equal(SaveSnapshotTriggerKind.PostExit, exitSnap!.TriggerKind);
        Assert.Equal(SaveWatcherStatusKind.Stopped, watcher.GetStatus(env.Game.GameId).Status);
    }

    [Fact]
    public async Task SaveWatcherService_RapidBurstFileChanges_NeverThrowsObjectDisposedException()
    {
        await using var env = await SaveEngineTestFixture.CreateAsync(explicitTimeProvider: TimeProvider.System);
        await env.WriteSaveFileAsync("burst.sav", "INIT");

        await using var watcher = new SaveWatcherService(
            env.Service,
            env.RuleResolver,
            env.EventBus,
            env.Database,
            TimeProvider.System,
            new SaveWatcherOptions
            {
                QuiescentDebounceOverride = TimeSpan.FromMilliseconds(50),
                PostExitSettleDelay = TimeSpan.FromMilliseconds(20),
            });

        var errorStatuses = new System.Collections.Concurrent.ConcurrentBag<SaveWatcherStatusDto>();
        watcher.WatcherStatusChanged += (_, s) =>
        {
            if (s.Status == SaveWatcherStatusKind.Error)
            {
                errorStatuses.Add(s);
            }
        };

        var captured = new TaskCompletionSource<SaveTreeChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Service.SaveTreeChanged += (_, ev) =>
        {
            if (ev.Reason == "SnapshotCreated")
            {
                captured.TrySetResult(ev);
            }
        };

        await watcher.StartWatchingGameAsync(env.Game.GameId);

        // Fire rapid concurrent burst writes from 4 parallel tasks to stress ResetDebounceTimer() CTS cancellation/disposal.
        Task[] writers = Enumerable.Range(0, 4)
            .Select(worker => Task.Run(async () =>
            {
                for (int i = 0; i < 15; i++)
                {
                    await env.WriteSaveFileAsync($"burst_{worker}.sav", $"W{worker}-ITER-{i}");
                }
            }))
            .ToArray();
        await Task.WhenAll(writers);

        SaveTreeChangedEvent ev = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(ev.SnapshotId);
        Assert.Empty(errorStatuses);
        await watcher.StopWatchingGameAsync(env.Game.GameId);
    }
}
