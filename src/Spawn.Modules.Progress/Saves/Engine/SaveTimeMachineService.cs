using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Saves.Cloud;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress.Saves.Engine;

/// <summary>
/// Full Git-like Save Time Machine &amp; Branching Save Tree service implementing <see cref="ISaveTimeMachineService"/>
/// (<c>REQ-PRG-01</c>, <c>REQ-PRG-02</c>).
/// </summary>
public sealed class SaveTimeMachineService : ISaveTimeMachineService
{
    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly CasChunkStore _casStore;
    private readonly ILudusaviRuleResolver _ruleResolver;
    private readonly IGameDataManagementService _gameDataService;
    private readonly IGameLaunchService _gameLaunchService;
    private readonly ISpawnEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SaveTimeMachineService> _logger;
    private readonly AntiTearFileCapture _fileCapture;
    private readonly AtomicSaveRestorer _restorer;
    private readonly LegacyPreTrainerSnapshotBridge _legacyBridge;
    private readonly Func<string, bool>? _isWatchingProbe;
    private readonly long? _maxSnapshotBytesOverrideForTest;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gameLocks = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged;

    public SaveTimeMachineService(
        SpawnDatabase database,
        SpawnPaths paths,
        CasChunkStore casStore,
        ILudusaviRuleResolver ruleResolver,
        IGameDataManagementService gameDataService,
        IGameLaunchService gameLaunchService,
        ISpawnEventBus eventBus,
        TimeProvider? timeProvider = null,
        ILogger<SaveTimeMachineService>? logger = null,
        Func<string, bool>? isWatchingProbe = null)
        : this(
            database,
            paths,
            casStore,
            ruleResolver,
            gameDataService,
            gameLaunchService,
            eventBus,
            timeProvider,
            logger,
            isWatchingProbe,
            afterReadConsistencyTestHook: null,
            maxSnapshotBytesOverrideForTest: null)
    {
    }

    internal SaveTimeMachineService(
        SpawnDatabase database,
        SpawnPaths paths,
        CasChunkStore casStore,
        ILudusaviRuleResolver ruleResolver,
        IGameDataManagementService gameDataService,
        IGameLaunchService gameLaunchService,
        ISpawnEventBus eventBus,
        TimeProvider? timeProvider,
        ILogger<SaveTimeMachineService>? logger,
        Func<string, bool>? isWatchingProbe,
        Action<string, int>? afterReadConsistencyTestHook,
        long? maxSnapshotBytesOverrideForTest = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(casStore);
        ArgumentNullException.ThrowIfNull(ruleResolver);
        ArgumentNullException.ThrowIfNull(gameDataService);
        ArgumentNullException.ThrowIfNull(gameLaunchService);
        ArgumentNullException.ThrowIfNull(eventBus);

        _database = database;
        _paths = paths;
        _casStore = casStore;
        _ruleResolver = ruleResolver;
        _gameDataService = gameDataService;
        _gameLaunchService = gameLaunchService;
        _eventBus = eventBus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<SaveTimeMachineService>.Instance;
        _isWatchingProbe = isWatchingProbe;
        _maxSnapshotBytesOverrideForTest = maxSnapshotBytesOverrideForTest;

        _fileCapture = new AntiTearFileCapture(_casStore, _ruleResolver, _timeProvider, afterReadConsistencyTestHook);
        _restorer = new AtomicSaveRestorer(_casStore, _ruleResolver, _gameDataService, _paths.DataRoot);
        _legacyBridge = new LegacyPreTrainerSnapshotBridge(_database, _paths, _casStore, _ruleResolver);
    }

    public Task<SaveGameStateRecord> GetGameStateAsync(string gameId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return _database.GetOrCreateSaveGameStateAsync(gameId, _timeProvider.GetUtcNow(), ct);
    }

    public async Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        SaveGameStateRecord updated = state with { UpdatedAtUtc = _timeProvider.GetUtcNow() };
        await _database.UpsertSaveGameStateAsync(updated, ct).ConfigureAwait(false);
        await PublishTreeChangedAsync(state.GameId, state.ActiveBranchId, state.CurrentHeadSnapshotId, "StateUpdated", ct)
            .ConfigureAwait(false);
        return updated;
    }

    public Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        _database.GetSaveSettingsAsync(ct);

    public Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default) =>
        _database.SaveSaveSettingsAsync(settings, ct);

    public async Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string gameId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        await _database.EnsureDefaultSaveBranchAsync(gameId, now, ct).ConfigureAwait(false);
        SaveGameStateRecord? state = await _database.GetSaveGameStateAsync(gameId, ct).ConfigureAwait(false);
        IReadOnlyList<SaveBranchRecord> branches = await _database.GetSaveBranchesByGameAsync(gameId, ct).ConfigureAwait(false);
        IReadOnlyList<SaveSnapshotRecord> snapshots = await _database.GetSaveSnapshotsByGameAsync(gameId, branchId: null, limit: 1000, ct).ConfigureAwait(false);
        long dedupBytes = await _database.GetGameUniqueChunkStoredBytesAsync(gameId, ct).ConfigureAwait(false);

        CloudSyncEndpointRecord? defaultEndpoint = await _database.GetDefaultCloudSyncEndpointAsync(ct).ConfigureAwait(false);
        string? effectiveEndpointId = state?.CloudEndpointId ?? defaultEndpoint?.EndpointId;
        HashSet<string> syncedSnapshotIds = await _database
            .GetCloudSyncedSnapshotIdsForGameAsync(gameId, effectiveEndpointId, ct)
            .ConfigureAwait(false);
        string effectiveCloudKey = await CloudGameKeyResolver
            .ResolveAsync(_database, gameId, state, ct: ct)
            .ConfigureAwait(false);
        int unimportedLegacyCount = await _database
            .GetUnimportedLegacyPreTrainerCountForGameAsync(gameId, ct)
            .ConfigureAwait(false);

        return SaveTreeLayoutBuilder.BuildGraph(
            gameId,
            state,
            branches,
            snapshots,
            dedupBytes,
            syncedSnapshotIds,
            effectiveCloudKey,
            unimportedLegacyCount);
    }

    public async Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GameRecord> games = await _database.GetAllGamesAsync(ct).ConfigureAwait(false);
        IReadOnlyList<SaveGameStateRecord> states = await _database.GetAllSaveGameStatesAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, SaveGameAggregateStats> statsMap = await _database.GetSaveGameAggregateStatsAsync(ct).ConfigureAwait(false);
        CloudSyncEndpointRecord? defaultEndpoint = await _database.GetDefaultCloudSyncEndpointAsync(ct).ConfigureAwait(false);

        var stateByGame = states.ToDictionary(s => s.GameId, StringComparer.OrdinalIgnoreCase);
        var list = new List<SaveLibraryGameOverviewDto>(games.Count);

        foreach (GameRecord game in games)
        {
            ct.ThrowIfCancellationRequested();
            stateByGame.TryGetValue(game.GameId, out SaveGameStateRecord? state);
            statsMap.TryGetValue(game.GameId, out SaveGameAggregateStats? agg);

            string activeBranchName = SpawnDatabase.DefaultSaveBranchName;
            if (!string.IsNullOrWhiteSpace(state?.ActiveBranchId))
            {
                SaveBranchRecord? activeBranch = await _database.GetSaveBranchByIdAsync(state.ActiveBranchId, ct).ConfigureAwait(false);
                if (activeBranch is not null)
                {
                    activeBranchName = activeBranch.Name;
                }
            }

            string? effectiveEndpointId = state?.CloudEndpointId ?? defaultEndpoint?.EndpointId;

            list.Add(new SaveLibraryGameOverviewDto
            {
                GameId = game.GameId,
                GameTitle = game.Title,
                IconPath = game.IconPath,
                ActiveBranchId = state?.ActiveBranchId,
                ActiveBranchName = activeBranchName,
                BranchCount = agg?.BranchCount ?? 0,
                SnapshotCount = agg?.SnapshotCount ?? 0,
                PinnedCount = agg?.PinnedCount ?? 0,
                TotalRawBytes = agg?.TotalRawBytes ?? 0,
                TotalStoredBytes = agg?.TotalStoredBytes ?? 0,
                LastSnapshotAtUtc = agg?.LastSnapshotAtUtc,
                IsWatching = _isWatchingProbe?.Invoke(game.GameId) ?? false,
                IsGameRunning = _gameLaunchService.GetActiveSession(game.GameId) is not null,
                HasCloudEndpoint = !string.IsNullOrWhiteSpace(effectiveEndpointId),
                CloudEndpointId = effectiveEndpointId,
                HasCloudConflict = state?.HasUnresolvedCloudConflict ?? false,
                LastSyncedAtUtc = state?.LastSyncedAtUtc,
                HasLegacyPreTrainerSnapshots = (agg?.UnimportedLegacyPreTrainerCount ?? 0) > 0,
            });
        }

        return list;
    }

    public Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default) =>
        _database.GetSnapshotFilesAsync(snapshotId, ct);

    public Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default) =>
        _database.GetCasVaultStatsAsync(ct);

    public async Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(
        string leftSnapshotId,
        string rightSnapshotId,
        CancellationToken ct = default)
    {
        SaveSnapshotRecord? left = await _database.GetSaveSnapshotByIdAsync(leftSnapshotId, ct).ConfigureAwait(false);
        SaveSnapshotRecord? right = await _database.GetSaveSnapshotByIdAsync(rightSnapshotId, ct).ConfigureAwait(false);

        IReadOnlyList<SaveSnapshotFileRecord> leftFiles = left is not null
            ? await _database.GetSnapshotFilesAsync(left.SnapshotId, ct).ConfigureAwait(false)
            : [];
        IReadOnlyList<SaveSnapshotFileRecord> rightFiles = right is not null
            ? await _database.GetSnapshotFilesAsync(right.SnapshotId, ct).ConfigureAwait(false)
            : [];

        string leftLabel = left is not null ? $"{left.Note ?? left.ShortId} (#{left.ShortId})" : "空基线";
        string rightLabel = right is not null ? $"{right.Note ?? right.ShortId} (#{right.ShortId})" : "空目标";

        return ComputeFileManifestDiff(
            leftLabel,
            rightLabel,
            leftFiles.ToDictionary(f => f.LogicalPath, f => (f.Category, f.FileSize, f.FileBlake3), StringComparer.OrdinalIgnoreCase),
            rightFiles.ToDictionary(f => f.LogicalPath, f => (f.Category, f.FileSize, f.FileBlake3), StringComparer.OrdinalIgnoreCase));
    }

    public async Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string snapshotId, CancellationToken ct = default)
    {
        SaveSnapshotRecord? snapshot = await _database.GetSaveSnapshotByIdAsync(snapshotId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new SaveSnapshotDiffDto { BaseLabel = "未知快照", TargetLabel = "当前磁盘" };
        }

        GameRecord? game = await _database.GetGameByIdAsync(snapshot.GameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return new SaveSnapshotDiffDto { BaseLabel = $"#{snapshot.ShortId}", TargetLabel = "当前磁盘" };
        }

        IReadOnlyList<SaveSnapshotFileRecord> snapFiles = await _database.GetSnapshotFilesAsync(snapshot.SnapshotId, ct)
            .ConfigureAwait(false);
        bool includeConfig = snapFiles.Any(f => f.Category == SaveFileCategory.Config);

        ResolvedSaveLayout layout = await _ruleResolver.ResolveGameSaveLayoutAsync(game.GameId, refreshOnlineRules: false, ct)
            .ConfigureAwait(false);
        var diskMap = await ScanDiskFilesReadOnlyAsync(game, layout, includeConfig, ct).ConfigureAwait(false);

        var leftMap = snapFiles.ToDictionary(
            f => f.LogicalPath,
            f => (f.Category, f.FileSize, f.FileBlake3),
            StringComparer.OrdinalIgnoreCase);

        return ComputeFileManifestDiff(
            $"{snapshot.Note ?? snapshot.ShortId} (#{snapshot.ShortId})",
            "当前磁盘",
            leftMap,
            diskMap);
    }

    public async Task<CreateSaveSnapshotResult> CreateSnapshotAsync(
        CreateSaveSnapshotRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            return FailedSnapshot("游戏标识不能为空。");
        }

        SemaphoreSlim gate = GetGameLock(request.GameId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CreateSnapshotCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            return new ForkBranchResult { Succeeded = false, Message = "游戏标识不能为空。" };
        }

        string trimmedName = request.BranchName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            return new ForkBranchResult { Succeeded = false, Message = "分支名称不能为空。" };
        }

        SemaphoreSlim gate = GetGameLock(request.GameId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GameRecord? game = await _database.GetGameByIdAsync(request.GameId, ct).ConfigureAwait(false);
            if (game is null)
            {
                return new ForkBranchResult { Succeeded = false, Message = $"未找到游戏记录：{request.GameId}" };
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            SaveBranchRecord defaultBranch = await _database.EnsureDefaultSaveBranchAsync(game.GameId, now, ct).ConfigureAwait(false);
            SaveGameStateRecord state = await _database.GetOrCreateSaveGameStateAsync(game.GameId, now, ct).ConfigureAwait(false);

            SaveBranchRecord? duplicate = await _database.GetSaveBranchByNameAsync(game.GameId, trimmedName, ct).ConfigureAwait(false);
            if (duplicate is not null)
            {
                return new ForkBranchResult { Succeeded = false, Message = $"已存在同名分支「{trimmedName}」。" };
            }

            string? baseSnapshotId = request.FromSnapshotId;
            if (string.IsNullOrWhiteSpace(baseSnapshotId))
            {
                SaveBranchRecord? activeBranch = !string.IsNullOrWhiteSpace(state.ActiveBranchId)
                    ? await _database.GetSaveBranchByIdAsync(state.ActiveBranchId, ct).ConfigureAwait(false)
                    : defaultBranch;
                baseSnapshotId = state.CurrentHeadSnapshotId ?? activeBranch?.HeadSnapshotId;
            }
            else
            {
                SaveSnapshotRecord? baseSnap = await _database.GetSaveSnapshotByIdAsync(baseSnapshotId, ct).ConfigureAwait(false);
                if (baseSnap is null || !string.Equals(baseSnap.GameId, game.GameId, StringComparison.OrdinalIgnoreCase))
                {
                    return new ForkBranchResult { Succeeded = false, Message = $"未找到有效的分叉源快照：{baseSnapshotId}" };
                }
            }

            bool needsDiskCheckout = request.SwitchToNewBranch
                                     && !string.IsNullOrWhiteSpace(baseSnapshotId)
                                     && !string.Equals(baseSnapshotId, state.CurrentHeadSnapshotId, StringComparison.Ordinal);

            if (needsDiskCheckout && _gameLaunchService.GetActiveSession(game.GameId) is not null)
            {
                return new ForkBranchResult
                {
                    Succeeded = false,
                    Message = $"《{game.Title}》当前正在运行中，无法立即检出历史节点存档；请关闭游戏后再切换分支，或取消“分叉后立即检出”。",
                };
            }

            IReadOnlyList<SaveBranchRecord> existingBranches = await _database.GetSaveBranchesByGameAsync(game.GameId, ct).ConfigureAwait(false);
            string colorHex = !string.IsNullOrWhiteSpace(request.ColorHex)
                ? request.ColorHex.Trim()
                : PickUnusedBranchColor(existingBranches);

            var newBranch = new SaveBranchRecord
            {
                BranchId = SpawnId.NewUlid(now),
                GameId = game.GameId,
                Name = trimmedName,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                ColorHex = colorHex,
                HeadSnapshotId = baseSnapshotId,
                BaseSnapshotId = baseSnapshotId,
                IsDefault = false,
                IsArchived = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            await _database.UpsertSaveBranchAsync(newBranch, ct).ConfigureAwait(false);

            SaveRestoreOutcome? checkoutOutcome = null;
            if (request.SwitchToNewBranch)
            {
                if (needsDiskCheckout && !string.IsNullOrWhiteSpace(baseSnapshotId))
                {
                    checkoutOutcome = await RestoreSnapshotToDiskCoreAsync(
                        game,
                        state,
                        baseSnapshotId,
                        targetBranchId: newBranch.BranchId,
                        createSafetySnapshot: request.CreateSafetySnapshotIfSwitching,
                        safetyTriggerKind: SaveSnapshotTriggerKind.PreBranchSwitchSafety,
                        includeConfigFiles: request.IncludeConfigFilesOnCheckout,
                        modMismatchPolicy: SaveModMismatchPolicy.ProceedAnyway,
                        ct: ct).ConfigureAwait(false);

                    if (!checkoutOutcome.Succeeded)
                    {
                        return new ForkBranchResult
                        {
                            Succeeded = false,
                            Message = $"已创建分支「{trimmedName}」，但检出存档失败：{checkoutOutcome.Message}",
                            Branch = newBranch,
                            CheckoutOutcome = checkoutOutcome,
                        };
                    }
                }
                else
                {
                    await _database.SetActiveSaveHeadAsync(game.GameId, newBranch.BranchId, baseSnapshotId, now, ct)
                        .ConfigureAwait(false);
                }
            }

            await PublishTreeChangedAsync(game.GameId, newBranch.BranchId, baseSnapshotId, "BranchCreated", ct)
                .ConfigureAwait(false);

            return new ForkBranchResult
            {
                Succeeded = true,
                Message = request.SwitchToNewBranch
                    ? $"已创建并切换至新分支「{trimmedName}」。"
                    : $"已从节点创建分支「{trimmedName}」。",
                Branch = newBranch,
                CheckoutOutcome = checkoutOutcome,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GameId) || string.IsNullOrWhiteSpace(request.TargetBranchId))
        {
            return new SaveRestoreOutcome { Succeeded = false, Message = "游戏标识或目标分支不能为空。" };
        }

        SemaphoreSlim gate = GetGameLock(request.GameId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GameRecord? game = await _database.GetGameByIdAsync(request.GameId, ct).ConfigureAwait(false);
            if (game is null)
            {
                return new SaveRestoreOutcome { Succeeded = false, Message = $"未找到游戏记录：{request.GameId}" };
            }

            if (_gameLaunchService.GetActiveSession(game.GameId) is not null)
            {
                return new SaveRestoreOutcome
                {
                    Succeeded = false,
                    Message = $"《{game.Title}》当前正在运行中，已禁止切换存档分支，请先退出游戏。",
                };
            }

            SaveBranchRecord? targetBranch = await _database.GetSaveBranchByIdAsync(request.TargetBranchId, ct).ConfigureAwait(false);
            if (targetBranch is null || !string.Equals(targetBranch.GameId, game.GameId, StringComparison.OrdinalIgnoreCase))
            {
                return new SaveRestoreOutcome { Succeeded = false, Message = $"未找到目标存档分支：{request.TargetBranchId}" };
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            SaveGameStateRecord state = await _database.GetOrCreateSaveGameStateAsync(game.GameId, now, ct).ConfigureAwait(false);

            if (string.Equals(state.ActiveBranchId, targetBranch.BranchId, StringComparison.Ordinal)
                && string.Equals(state.CurrentHeadSnapshotId, targetBranch.HeadSnapshotId, StringComparison.Ordinal))
            {
                return new SaveRestoreOutcome
                {
                    Succeeded = true,
                    Message = $"当前已位于分支「{targetBranch.Name}」最新节点。",
                    ActiveBranchId = targetBranch.BranchId,
                };
            }

            if (string.IsNullOrWhiteSpace(targetBranch.HeadSnapshotId))
            {
                SaveSnapshotRecord? safetySnap = null;
                string? safetyTargetBranchId = !string.IsNullOrWhiteSpace(request.SafetySnapshotBranchId)
                    ? request.SafetySnapshotBranchId
                    : state.ActiveBranchId;
                if (request.CreateSafetySnapshot && !string.IsNullOrWhiteSpace(safetyTargetBranchId))
                {
                    CreateSaveSnapshotResult safetyRes = await CreateSnapshotCoreAsync(
                        new CreateSaveSnapshotRequest
                        {
                            GameId = game.GameId,
                            BranchId = safetyTargetBranchId,
                            TriggerKind = SaveSnapshotTriggerKind.PreBranchSwitchSafety,
                            Note = $"切换至「{targetBranch.Name}」前自动保护",
                            SkipIfUnchangedFromHead = !request.ForceSafetySnapshotEvenIfUnchanged,
                        },
                        ct).ConfigureAwait(false);
                    safetySnap = safetyRes.Snapshot;
                }

                await _database.SetActiveSaveHeadAsync(game.GameId, targetBranch.BranchId, null, now, ct).ConfigureAwait(false);
                await PublishTreeChangedAsync(game.GameId, targetBranch.BranchId, null, "BranchSwitched", ct).ConfigureAwait(false);
                return new SaveRestoreOutcome
                {
                    Succeeded = true,
                    Message = $"已切换至空分支「{targetBranch.Name}」。",
                    ActiveBranchId = targetBranch.BranchId,
                    SafetySnapshot = safetySnap,
                };
            }

            SaveRestoreOutcome outcome = await RestoreSnapshotToDiskCoreAsync(
                game,
                state,
                targetBranch.HeadSnapshotId,
                targetBranchId: targetBranch.BranchId,
                createSafetySnapshot: request.CreateSafetySnapshot,
                safetyTriggerKind: SaveSnapshotTriggerKind.PreBranchSwitchSafety,
                includeConfigFiles: request.IncludeConfigFiles,
                modMismatchPolicy: request.ModMismatchPolicy,
                safetySnapshotBranchId: request.SafetySnapshotBranchId,
                forceSafetySnapshotEvenIfUnchanged: request.ForceSafetySnapshotEvenIfUnchanged,
                ct: ct).ConfigureAwait(false);

            if (outcome.Succeeded)
            {
                await PublishTreeChangedAsync(game.GameId, targetBranch.BranchId, targetBranch.HeadSnapshotId, "BranchSwitched", ct)
                    .ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SnapshotId))
        {
            return new SaveRestoreOutcome { Succeeded = false, Message = "目标快照标识不能为空。" };
        }

        SaveSnapshotRecord? snapshot = await _database.GetSaveSnapshotByIdAsync(request.SnapshotId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new SaveRestoreOutcome { Succeeded = false, Message = $"未找到指定的存档快照：{request.SnapshotId}" };
        }

        SemaphoreSlim gate = GetGameLock(snapshot.GameId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GameRecord? game = await _database.GetGameByIdAsync(snapshot.GameId, ct).ConfigureAwait(false);
            if (game is null)
            {
                return new SaveRestoreOutcome { Succeeded = false, Message = $"未找到游戏记录：{snapshot.GameId}" };
            }

            if (_gameLaunchService.GetActiveSession(game.GameId) is not null)
            {
                return new SaveRestoreOutcome
                {
                    Succeeded = false,
                    Message = $"《{game.Title}》当前正在运行中，已拒绝回滚存档，请先退出游戏后再试。",
                };
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            SaveGameStateRecord state = await _database.GetOrCreateSaveGameStateAsync(game.GameId, now, ct).ConfigureAwait(false);

            // Validate ModProfileHash mismatch policy BEFORE creating any forked branch so retrying after
            // user confirmation never collides with an orphaned duplicate branch name.
            SaveRestoreOutcome? modMismatchFailure = await CheckModProfileMismatchAsync(
                state,
                snapshot,
                request.ModMismatchPolicy,
                ct).ConfigureAwait(false);
            if (modMismatchFailure is not null)
            {
                return modMismatchFailure;
            }

            string targetBranchId = snapshot.BranchId;
            if (!string.IsNullOrWhiteSpace(request.ForkNewBranchName))
            {
                string branchName = request.ForkNewBranchName.Trim();
                SaveBranchRecord? dup = await _database.GetSaveBranchByNameAsync(game.GameId, branchName, ct).ConfigureAwait(false);
                if (dup is not null)
                {
                    return new SaveRestoreOutcome { Succeeded = false, Message = $"已存在同名分支「{branchName}」。" };
                }

                IReadOnlyList<SaveBranchRecord> existingBranches = await _database.GetSaveBranchesByGameAsync(game.GameId, ct).ConfigureAwait(false);
                var forkedBranch = new SaveBranchRecord
                {
                    BranchId = SpawnId.NewUlid(now),
                    GameId = game.GameId,
                    Name = branchName,
                    ColorHex = !string.IsNullOrWhiteSpace(request.ForkNewBranchColorHex)
                        ? request.ForkNewBranchColorHex.Trim()
                        : PickUnusedBranchColor(existingBranches),
                    HeadSnapshotId = snapshot.SnapshotId,
                    BaseSnapshotId = snapshot.SnapshotId,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await _database.UpsertSaveBranchAsync(forkedBranch, ct).ConfigureAwait(false);
                targetBranchId = forkedBranch.BranchId;
            }

            SaveRestoreOutcome outcome = await RestoreSnapshotToDiskCoreAsync(
                game,
                state,
                snapshot.SnapshotId,
                targetBranchId: targetBranchId,
                createSafetySnapshot: request.CreateSafetySnapshot,
                safetyTriggerKind: SaveSnapshotTriggerKind.PreRollbackSafety,
                includeConfigFiles: request.IncludeConfigFiles,
                modMismatchPolicy: request.ModMismatchPolicy,
                ct: ct).ConfigureAwait(false);

            if (outcome.Succeeded)
            {
                await PublishTreeChangedAsync(game.GameId, targetBranchId, snapshot.SnapshotId, "RolledBack", ct)
                    .ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveBranchRecord?> RenameBranchAsync(
        string branchId,
        string newName,
        string? description = null,
        string? colorHex = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchId) || string.IsNullOrWhiteSpace(newName))
        {
            return null;
        }

        SaveBranchRecord? existing = await _database.GetSaveBranchByIdAsync(branchId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        string trimmed = newName.Trim();
        SaveBranchRecord? conflict = await _database.GetSaveBranchByNameAsync(existing.GameId, trimmed, ct).ConfigureAwait(false);
        if (conflict is not null && !string.Equals(conflict.BranchId, branchId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"已存在同名分支「{trimmed}」。");
        }

        await _database.UpdateSaveBranchMetadataAsync(branchId, trimmed, description, colorHex, _timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);

        SaveBranchRecord? updated = await _database.GetSaveBranchByIdAsync(branchId, ct).ConfigureAwait(false);
        await PublishTreeChangedAsync(existing.GameId, branchId, existing.HeadSnapshotId, "BranchRenamed", ct)
            .ConfigureAwait(false);
        return updated;
    }

    public async Task<DeleteBranchResult> DeleteBranchAsync(
        string branchId,
        bool deleteSnapshots = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchId))
        {
            return new DeleteBranchResult(false, "分支标识不能为空。");
        }

        SaveBranchRecord? branch = await _database.GetSaveBranchByIdAsync(branchId, ct).ConfigureAwait(false);
        if (branch is null)
        {
            return new DeleteBranchResult(false, "未找到指定的分支。");
        }

        if (branch.IsDefault)
        {
            return new DeleteBranchResult(false, $"不能删除默认主分支「{branch.Name}」。");
        }

        SemaphoreSlim gate = GetGameLock(branch.GameId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SaveGameStateRecord? state = await _database.GetSaveGameStateAsync(branch.GameId, ct).ConfigureAwait(false);
            if (string.Equals(state?.ActiveBranchId, branch.BranchId, StringComparison.Ordinal))
            {
                return new DeleteBranchResult(false, $"分支「{branch.Name}」当前处于激活状态，请先切换到其他分支后再删除。");
            }

            IReadOnlyList<SaveSnapshotRecord> branchSnaps = await _database
                .GetSaveSnapshotsByGameAsync(branch.GameId, branch.BranchId, limit: 1000, ct)
                .ConfigureAwait(false);

            if (branchSnaps.Count > 0 && !deleteSnapshots)
            {
                return new DeleteBranchResult(false, $"分支「{branch.Name}」仍包含 {branchSnaps.Count} 个快照，请确认一并删除分支快照。");
            }

            if (branchSnaps.Any(s => s.IsPinned))
            {
                return new DeleteBranchResult(false, $"分支「{branch.Name}」内存在已固定的重要快照（★），请先取消固定后再删除。");
            }

            // Refuse if another branch forks off or points its Base/Head at a snapshot owned by this branch,
            // or if the game's current active HEAD snapshot belongs to this branch.
            var branchSnapIds = branchSnaps.Select(s => s.SnapshotId).ToHashSet(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(state?.CurrentHeadSnapshotId)
                && branchSnapIds.Contains(state.CurrentHeadSnapshotId))
            {
                return new DeleteBranchResult(false, $"分支「{branch.Name}」包含当前激活 HEAD 快照，请先切换节点后再删除。");
            }

            IReadOnlyList<SaveBranchRecord> allBranches = await _database
                .GetSaveBranchesByGameAsync(branch.GameId, ct)
                .ConfigureAwait(false);
            foreach (SaveBranchRecord other in allBranches)
            {
                if (string.Equals(other.BranchId, branch.BranchId, StringComparison.Ordinal))
                {
                    continue;
                }

                if ((!string.IsNullOrWhiteSpace(other.BaseSnapshotId) && branchSnapIds.Contains(other.BaseSnapshotId))
                    || (!string.IsNullOrWhiteSpace(other.HeadSnapshotId) && branchSnapIds.Contains(other.HeadSnapshotId)))
                {
                    return new DeleteBranchResult(false, $"分支「{branch.Name}」的节点正被分支「{other.Name}」引用，无法直接删除。");
                }
            }

            foreach (SaveSnapshotRecord snap in branchSnaps)
            {
                IReadOnlyList<SaveSnapshotRecord> children = await _database.GetChildSaveSnapshotsAsync(snap.SnapshotId, ct)
                    .ConfigureAwait(false);
                if (children.Any(c => !branchSnapIds.Contains(c.SnapshotId)))
                {
                    return new DeleteBranchResult(false, $"分支「{branch.Name}」的节点被其他子分支依赖，无法直接删除。");
                }
            }

            var (deleted, deletedCount) = await _database
                .DeleteSaveBranchAsync(branchId, deleteSnapshots, _timeProvider.GetUtcNow(), ct)
                .ConfigureAwait(false);

            if (!deleted)
            {
                return new DeleteBranchResult(false, "删除分支失败。");
            }

            await PublishTreeChangedAsync(branch.GameId, null, null, "BranchDeleted", ct).ConfigureAwait(false);
            return new DeleteBranchResult(true, $"已删除分支「{branch.Name}」（移除 {deletedCount} 个快照）。", deletedCount);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default)
    {
        SaveSnapshotRecord? snap = await _database.GetSaveSnapshotByIdAsync(snapshotId, ct).ConfigureAwait(false);
        if (snap is null)
        {
            return false;
        }

        bool ok = await _database.SetSaveSnapshotPinnedAsync(snapshotId, isPinned, ct).ConfigureAwait(false);
        if (ok)
        {
            await PublishTreeChangedAsync(snap.GameId, snap.BranchId, snap.SnapshotId, isPinned ? "SnapshotPinned" : "SnapshotUnpinned", ct)
                .ConfigureAwait(false);
        }

        return ok;
    }

    public async Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default)
    {
        SaveSnapshotRecord? snap = await _database.GetSaveSnapshotByIdAsync(snapshotId, ct).ConfigureAwait(false);
        if (snap is null)
        {
            return false;
        }

        bool ok = await _database.UpdateSaveSnapshotNoteAsync(snapshotId, note, ct).ConfigureAwait(false);
        if (ok)
        {
            await PublishTreeChangedAsync(snap.GameId, snap.BranchId, snap.SnapshotId, "SnapshotNoteUpdated", ct)
                .ConfigureAwait(false);
        }

        return ok;
    }

    public async Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return new DeleteSnapshotResult(false, "快照标识不能为空。");
        }

        SaveSnapshotRecord? snap = await _database.GetSaveSnapshotByIdAsync(snapshotId, ct).ConfigureAwait(false);
        if (snap is null)
        {
            return new DeleteSnapshotResult(false, "未找到指定的快照。");
        }

        SemaphoreSlim gate = GetGameLock(snap.GameId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (snap.IsPinned)
            {
                return new DeleteSnapshotResult(false, "该快照已固定（★），请先取消固定后再删除。");
            }

            SaveGameStateRecord? state = await _database.GetSaveGameStateAsync(snap.GameId, ct).ConfigureAwait(false);
            if (string.Equals(state?.CurrentHeadSnapshotId, snap.SnapshotId, StringComparison.Ordinal))
            {
                return new DeleteSnapshotResult(false, "不能删除当前激活 HEAD 节点，请先回滚或切换至其他节点。");
            }

            IReadOnlyList<SaveSnapshotRecord> children = await _database.GetChildSaveSnapshotsAsync(snap.SnapshotId, ct)
                .ConfigureAwait(false);
            if (children.Count >= 2)
            {
                return new DeleteSnapshotResult(false, $"该快照存在 {children.Count} 个分支子节点依赖（分叉枢纽节点），禁止直接删除以保持存档树完整。");
            }

            // Protect any other branch whose BaseSnapshotId or HeadSnapshotId points at this snapshot
            // (e.g., a freshly forked empty branch) so deleting a snapshot never leaves dangling IDs.
            IReadOnlyList<SaveBranchRecord> branches = await _database.GetSaveBranchesByGameAsync(snap.GameId, ct)
                .ConfigureAwait(false);
            foreach (SaveBranchRecord other in branches)
            {
                if (string.Equals(other.BranchId, snap.BranchId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(other.HeadSnapshotId, snap.SnapshotId, StringComparison.Ordinal)
                    || string.Equals(other.BaseSnapshotId, snap.SnapshotId, StringComparison.Ordinal))
                {
                    return new DeleteSnapshotResult(
                        false,
                        $"该快照正被分支「{other.Name}」作为起点或最新节点引用，无法直接删除。");
                }
            }

            if (children.Count == 1 && !string.Equals(children[0].BranchId, snap.BranchId, StringComparison.Ordinal))
            {
                return new DeleteSnapshotResult(false, "该快照被其他分支子节点直接依赖，无法直接删除。");
            }

            // Update own branch's Head / Base pointers cleanly before deleting & re-linking children.
            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (SaveBranchRecord b in branches)
            {
                if (string.Equals(b.HeadSnapshotId, snap.SnapshotId, StringComparison.Ordinal))
                {
                    await _database.UpdateSaveBranchHeadAsync(b.BranchId, snap.ParentSnapshotId, now, ct).ConfigureAwait(false);
                }

                if (string.Equals(b.BaseSnapshotId, snap.SnapshotId, StringComparison.Ordinal))
                {
                    string? nextBase = snap.ParentSnapshotId ?? (children.Count == 1 ? children[0].SnapshotId : null);
                    await _database.UpdateSaveBranchBaseAsync(b.BranchId, nextBase, now, ct).ConfigureAwait(false);
                }
            }

            var (deleted, unrefCount) = children.Count == 1
                ? await _database.DeleteSaveSnapshotAndRelinkChildrenAsync(snap.SnapshotId, snap.ParentSnapshotId, now, ct).ConfigureAwait(false)
                : await _database.DeleteSaveSnapshotAsync(snap.SnapshotId, now, ct).ConfigureAwait(false);
            if (!deleted)
            {
                return new DeleteSnapshotResult(false, "删除快照失败。");
            }

            await PublishTreeChangedAsync(snap.GameId, snap.BranchId, snap.SnapshotId, "SnapshotDeleted", ct)
                .ConfigureAwait(false);

            return new DeleteSnapshotResult(
                true,
                children.Count == 1 ? "已删除该中间快照，并自动重连子节点的父指针。" : "已删除快照。",
                unrefCount);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(
        string snapshotId,
        string targetDirectory,
        CancellationToken ct = default)
    {
        SaveSnapshotRecord? snapshot = await _database.GetSaveSnapshotByIdAsync(snapshotId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new ExportSnapshotResult(false, $"未找到快照：{snapshotId}");
        }

        IReadOnlyList<SaveSnapshotFileRecord> files = await _database.GetSnapshotFilesAsync(snapshotId, ct).ConfigureAwait(false);
        return await _restorer.ExportToFolderAsync(snapshot, files, targetDirectory, ct).ConfigureAwait(false);
    }

    public async Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(
        string gameId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        SemaphoreSlim gate = GetGameLock(gameId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
            if (game is null)
            {
                return new LegacyPreTrainerImportReport(0, 0, 0, [], [$"未找到游戏记录：{gameId}"]);
            }

            SaveSettingsDto settings = await _database.GetSaveSettingsAsync(ct).ConfigureAwait(false);
            LegacyPreTrainerImportReport report = await _legacyBridge
                .ImportForGameAsync(game, settings.MachineId, ct)
                .ConfigureAwait(false);

            if (report.ImportedCount > 0)
            {
                await PublishTreeChangedAsync(gameId, null, report.ImportedSnapshotIds[^1], "LegacyImported", ct)
                    .ConfigureAwait(false);
            }

            return report;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveGcReport> RunCasGarbageCollectionAsync(
        TimeSpan? gracePeriodOverride = null,
        CancellationToken ct = default)
    {
        SaveSettingsDto settings = await _database.GetSaveSettingsAsync(ct).ConfigureAwait(false);
        TimeSpan grace = gracePeriodOverride ?? TimeSpan.FromHours(Math.Max(0, settings.GcGracePeriodHours));
        return await _casStore.RunGarbageCollectionAsync(grace, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Core Internal Operations (Caller Holds Per-Game Lock)
    // -------------------------------------------------------------------------

    private async Task<CreateSaveSnapshotResult> CreateSnapshotCoreAsync(
        CreateSaveSnapshotRequest request,
        CancellationToken ct)
    {
        GameRecord? game = await _database.GetGameByIdAsync(request.GameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return FailedSnapshot($"未找到游戏记录：{request.GameId}");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        SaveSettingsDto globalSettings = await _database.GetSaveSettingsAsync(ct).ConfigureAwait(false);
        SaveBranchRecord defaultBranch = await _database.EnsureDefaultSaveBranchAsync(game.GameId, now, ct).ConfigureAwait(false);
        SaveGameStateRecord state = await _database.GetOrCreateSaveGameStateAsync(game.GameId, now, ct).ConfigureAwait(false);

        string targetBranchId = !string.IsNullOrWhiteSpace(request.BranchId)
            ? request.BranchId
            : (state.ActiveBranchId ?? defaultBranch.BranchId);

        SaveBranchRecord targetBranch = await _database.GetSaveBranchByIdAsync(targetBranchId, ct).ConfigureAwait(false)
            ?? defaultBranch;
        targetBranchId = targetBranch.BranchId;

        bool isSafetySnapshot = request.TriggerKind is SaveSnapshotTriggerKind.PreRollbackSafety
            or SaveSnapshotTriggerKind.PreBranchSwitchSafety;

        // Determine parent node in the branching DAG.
        string? parentSnapshotId = request.ExpectedParentSnapshotId;
        if (string.IsNullOrWhiteSpace(parentSnapshotId))
        {
            bool onActiveBranch = string.Equals(targetBranchId, state.ActiveBranchId, StringComparison.Ordinal);
            parentSnapshotId = onActiveBranch
                ? (state.CurrentHeadSnapshotId ?? targetBranch.HeadSnapshotId)
                : targetBranch.HeadSnapshotId;
        }

        bool includeConfig = request.IncludeConfigFiles ?? state.IncludeConfigFiles;
        long maxBytes = _maxSnapshotBytesOverrideForTest
                        ?? ((long)Math.Clamp(globalSettings.DefaultMaxSnapshotSizeMb, 16, 8192) * 1024L * 1024L);

        ResolvedSaveLayout layout = await _ruleResolver
            .ResolveGameSaveLayoutAsync(game.GameId, refreshOnlineRules: false, ct)
            .ConfigureAwait(false);

        // Hold shared CAS write scope across file capture + SQLite ref_count increment so concurrent GC
        // (including Settings' TimeSpan.Zero manual GC) can never delete chunks before commit.
        await using IAsyncDisposable casWriteScope = await _casStore
            .BeginChunkWriteScopeAsync(ct)
            .ConfigureAwait(false);

        string snapshotId = SpawnId.NewUlid(now);
        AntiTearCaptureOutcome capture = await _fileCapture
            .CaptureAsync(snapshotId, game, layout, includeConfig, maxBytes, ct)
            .ConfigureAwait(false);

        if (!capture.Succeeded)
        {
            return new CreateSaveSnapshotResult
            {
                Succeeded = false,
                Skipped = capture.Skipped,
                SkipReason = capture.SkipReason,
                Message = capture.Message,
                Warnings = capture.Warnings,
            };
        }

        // Optional unchanged-from-HEAD deduplication (used by AutoQuiescent, PostExit, and PreBranchSwitchSafety).
        if (request.SkipIfUnchangedFromHead && !string.IsNullOrWhiteSpace(parentSnapshotId))
        {
            IReadOnlyList<SaveSnapshotFileRecord> parentFiles = await _database
                .GetSnapshotFilesAsync(parentSnapshotId, ct)
                .ConfigureAwait(false);

            if (ManifestsAreIdentical(parentFiles, capture.Files))
            {
                return new CreateSaveSnapshotResult
                {
                    Succeeded = true,
                    Skipped = true,
                    SkipReason = SaveSnapshotSkipReason.UnchangedFromHead,
                    Message = "当前磁盘存档与最新节点完全一致，无需重复创建快照。",
                    Warnings = capture.Warnings,
                };
            }
        }

        // Handle Detached HEAD safely: NEVER overwrite targetBranch.HeadSnapshotId!
        // - For safety snapshots: keep advanceBranchHead = false so original branch tip is untouched.
        // - For normal user/auto snapshots while detached: auto-fork a visible branch rooted at CurrentHeadSnapshotId
        //   so new progress becomes its own branch while preserving the original branch's newer tip intact.
        bool isTargetingActiveBranch = string.Equals(targetBranchId, state.ActiveBranchId, StringComparison.Ordinal);
        bool isDetached = isTargetingActiveBranch
                          && !string.IsNullOrWhiteSpace(state.CurrentHeadSnapshotId)
                          && !string.IsNullOrWhiteSpace(targetBranch.HeadSnapshotId)
                          && !string.Equals(targetBranch.HeadSnapshotId, state.CurrentHeadSnapshotId, StringComparison.Ordinal);

        bool advanceBranchHead;
        bool updateGameActiveHead;
        if (isSafetySnapshot)
        {
            advanceBranchHead = false;
            updateGameActiveHead = false;
        }
        else if (isDetached)
        {
            IReadOnlyList<SaveBranchRecord> existingBranches = await _database
                .GetSaveBranchesByGameAsync(game.GameId, ct)
                .ConfigureAwait(false);
            string baseForkName = $"fork-{now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
            string uniqueForkName = baseForkName;
            var existingNames = existingBranches.Select(static b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int suffix = 2;
            while (existingNames.Contains(uniqueForkName))
            {
                uniqueForkName = $"{baseForkName}-{suffix++}";
            }

            var autoForkBranch = new SaveBranchRecord
            {
                BranchId = SpawnId.NewUlid(now),
                GameId = game.GameId,
                Name = uniqueForkName,
                Description = $"从历史节点自动分叉（原分支：{targetBranch.Name}）",
                ColorHex = PickUnusedBranchColor(existingBranches),
                BaseSnapshotId = parentSnapshotId,
                HeadSnapshotId = null,
                IsDefault = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await _database.UpsertSaveBranchAsync(autoForkBranch, ct).ConfigureAwait(false);
            targetBranchId = autoForkBranch.BranchId;
            advanceBranchHead = true;
            updateGameActiveHead = true;
        }
        else
        {
            advanceBranchHead = true;
            updateGameActiveHead = true;
        }

        var record = new SaveSnapshotRecord
        {
            SnapshotId = snapshotId,
            GameId = game.GameId,
            BranchId = targetBranchId,
            ParentSnapshotId = parentSnapshotId,
            ProfileId = request.ProfileId,
            ModProfileHash = request.ModProfileHash ?? string.Empty,
            TriggerKind = request.Pin && request.TriggerKind == SaveSnapshotTriggerKind.Manual
                ? SaveSnapshotTriggerKind.ManualPin
                : request.TriggerKind,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            FileCount = capture.FileCount,
            RegistryKeyCount = capture.RegistryKeyCount,
            TotalRawBytes = capture.TotalRawBytes,
            TotalStoredBytes = capture.TotalStoredBytes,
            CreatedMachineId = globalSettings.MachineId,
            PlaytimeActiveSeconds = game.TotalActiveSec,
            CreatedAtUtc = now,
            IsPinned = request.Pin || request.TriggerKind == SaveSnapshotTriggerKind.ManualPin,
        };

        await _database.CreateSaveSnapshotWithFilesAsync(
            record,
            capture.Files,
            advanceBranchHead: advanceBranchHead,
            updateGameActiveHead: updateGameActiveHead,
            cancellationToken: ct).ConfigureAwait(false);

        // Enforce per-game rolling retention without ever removing pinned nodes, safety backups, HEAD, branch tips/bases, or fork parents.
        await EnforceRollingRetentionCoreAsync(game.GameId, state.MaxRollingSnapshots, now, ct).ConfigureAwait(false);

        await PublishTreeChangedAsync(game.GameId, targetBranchId, snapshotId, "SnapshotCreated", ct).ConfigureAwait(false);

        return new CreateSaveSnapshotResult
        {
            Succeeded = true,
            Skipped = false,
            Message = capture.Message,
            Snapshot = record,
            DeduplicatedBytesSaved = capture.DeduplicatedBytesSaved,
            NewChunkCount = capture.NewChunkCount,
            ReusedChunkCount = capture.ReusedChunkCount,
            Warnings = capture.Warnings,
        };
    }

    private async Task<SaveRestoreOutcome> RestoreSnapshotToDiskCoreAsync(
        GameRecord game,
        SaveGameStateRecord state,
        string targetSnapshotId,
        string targetBranchId,
        bool createSafetySnapshot,
        SaveSnapshotTriggerKind safetyTriggerKind,
        bool includeConfigFiles,
        SaveModMismatchPolicy modMismatchPolicy,
        string? safetySnapshotBranchId = null,
        bool forceSafetySnapshotEvenIfUnchanged = false,
        CancellationToken ct = default)
    {
        SaveSnapshotRecord? targetSnapshot = await _database.GetSaveSnapshotByIdAsync(targetSnapshotId, ct).ConfigureAwait(false);
        if (targetSnapshot is null)
        {
            return new SaveRestoreOutcome { Succeeded = false, Message = $"未找到目标快照：{targetSnapshotId}" };
        }

        SaveRestoreOutcome? modMismatch = await CheckModProfileMismatchAsync(state, targetSnapshot, modMismatchPolicy, ct)
            .ConfigureAwait(false);
        if (modMismatch is not null)
        {
            return modMismatch;
        }

        SaveSnapshotRecord? safetySnapshot = null;
        if (createSafetySnapshot)
        {
            string? effectiveSafetyBranchId = !string.IsNullOrWhiteSpace(safetySnapshotBranchId)
                ? safetySnapshotBranchId
                : state.ActiveBranchId;
            bool skipUnchanged = !forceSafetySnapshotEvenIfUnchanged
                && safetyTriggerKind == SaveSnapshotTriggerKind.PreBranchSwitchSafety;

            CreateSaveSnapshotResult safetyRes = await CreateSnapshotCoreAsync(
                new CreateSaveSnapshotRequest
                {
                    GameId = game.GameId,
                    BranchId = effectiveSafetyBranchId,
                    TriggerKind = safetyTriggerKind,
                    Note = safetyTriggerKind == SaveSnapshotTriggerKind.PreBranchSwitchSafety
                        ? "切换分支前自动保护快照"
                        : "回滚前自动保护快照",
                    IncludeConfigFiles = includeConfigFiles,
                    SkipIfUnchangedFromHead = skipUnchanged,
                },
                ct).ConfigureAwait(false);

            if (!safetyRes.Succeeded && safetyRes.SkipReason != SaveSnapshotSkipReason.NoSaveLocationsFound)
            {
                return new SaveRestoreOutcome
                {
                    Succeeded = false,
                    Message = $"回滚前创建安全保护快照失败，已中止以防丢档：{safetyRes.Message}",
                    TargetSnapshot = targetSnapshot,
                };
            }

            safetySnapshot = safetyRes.Snapshot;
        }

        IReadOnlyList<SaveSnapshotFileRecord> targetFiles = await _database
            .GetSnapshotFilesAsync(targetSnapshot.SnapshotId, ct)
            .ConfigureAwait(false);

        IReadOnlyList<SaveSnapshotFileRecord>? outgoingFiles = null;
        string? outgoingSnapId = safetySnapshot?.SnapshotId ?? state.CurrentHeadSnapshotId;
        if (!string.IsNullOrWhiteSpace(outgoingSnapId))
        {
            outgoingFiles = await _database.GetSnapshotFilesAsync(outgoingSnapId, ct).ConfigureAwait(false);
        }

        SaveRestoreOutcome restoreOutcome = await _restorer
            .RestoreAsync(
                game,
                targetSnapshot,
                targetFiles,
                includeConfigFiles,
                outgoingFiles,
                safetySnapshot,
                ct)
            .ConfigureAwait(false);

        if (!restoreOutcome.Succeeded)
        {
            return restoreOutcome;
        }

        await _database.SetActiveSaveHeadAsync(game.GameId, targetBranchId, targetSnapshot.SnapshotId, _timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);

        return restoreOutcome with { ActiveBranchId = targetBranchId };
    }

    private async Task<SaveRestoreOutcome?> CheckModProfileMismatchAsync(
        SaveGameStateRecord state,
        SaveSnapshotRecord targetSnapshot,
        SaveModMismatchPolicy modMismatchPolicy,
        CancellationToken ct)
    {
        if (modMismatchPolicy != SaveModMismatchPolicy.AbortAndPrompt
            || string.IsNullOrWhiteSpace(targetSnapshot.ModProfileHash)
            || string.IsNullOrWhiteSpace(state.CurrentHeadSnapshotId))
        {
            return null;
        }

        SaveSnapshotRecord? currentHead = await _database
            .GetSaveSnapshotByIdAsync(state.CurrentHeadSnapshotId, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(currentHead?.ModProfileHash)
            || string.Equals(targetSnapshot.ModProfileHash, currentHead.ModProfileHash, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SaveRestoreOutcome
        {
            Succeeded = false,
            RequiresModMismatchConfirmation = true,
            ExpectedModProfileHash = targetSnapshot.ModProfileHash,
            CurrentModProfileHash = currentHead.ModProfileHash,
            TargetSnapshot = targetSnapshot,
            Message = "目标快照关联的 Mod 配置指纹与当前不一致，请确认是否继续回滚。",
        };
    }

    private async Task EnforceRollingRetentionCoreAsync(
        string gameId,
        int maxRollingSnapshots,
        DateTimeOffset now,
        CancellationToken ct)
    {
        int limit = Math.Clamp(maxRollingSnapshots, 1, 500);
        IReadOnlyList<SaveSnapshotRecord> allSnaps = await _database
            .GetSaveSnapshotsByGameAsync(gameId, branchId: null, limit: 1000, ct)
            .ConfigureAwait(false);

        // Count only regular non-pinned, non-safety snapshots toward rolling quota.
        var rollingCandidates = allSnaps
            .Where(s => !s.IsPinned
                        && s.TriggerKind is not SaveSnapshotTriggerKind.PreRollbackSafety
                        && s.TriggerKind is not SaveSnapshotTriggerKind.PreBranchSwitchSafety)
            .OrderBy(s => s.CreatedAtUtc)
            .ThenBy(s => s.SnapshotId, StringComparer.Ordinal)
            .ToList();

        int excess = rollingCandidates.Count - limit;
        if (excess <= 0)
        {
            return;
        }

        SaveGameStateRecord? state = await _database.GetSaveGameStateAsync(gameId, ct).ConfigureAwait(false);
        IReadOnlyList<SaveBranchRecord> branches = await _database.GetSaveBranchesByGameAsync(gameId, ct).ConfigureAwait(false);

        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(state?.CurrentHeadSnapshotId))
        {
            protectedIds.Add(state.CurrentHeadSnapshotId);
        }

        foreach (SaveBranchRecord b in branches)
        {
            if (!string.IsNullOrWhiteSpace(b.HeadSnapshotId))
            {
                protectedIds.Add(b.HeadSnapshotId);
            }

            if (!string.IsNullOrWhiteSpace(b.BaseSnapshotId))
            {
                protectedIds.Add(b.BaseSnapshotId);
            }
        }

        // Track mutable lineage pointers so rolling retention can safely splice out oldest unpinned linear
        // middle nodes via parent re-linking (preventing an early pinned ★ node from freezing retention).
        var parentBySnap = new Dictionary<string, string?>(allSnaps.Count, StringComparer.Ordinal);
        var branchBySnap = new Dictionary<string, string>(allSnaps.Count, StringComparer.Ordinal);
        var childrenBySnap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (SaveSnapshotRecord s in allSnaps)
        {
            parentBySnap[s.SnapshotId] = s.ParentSnapshotId;
            branchBySnap[s.SnapshotId] = s.BranchId;
            if (!string.IsNullOrWhiteSpace(s.ParentSnapshotId))
            {
                if (!childrenBySnap.TryGetValue(s.ParentSnapshotId, out List<string>? kids))
                {
                    kids = [];
                    childrenBySnap[s.ParentSnapshotId] = kids;
                }

                kids.Add(s.SnapshotId);
            }

            if (!string.IsNullOrWhiteSpace(s.MergeParentSnapshotId))
            {
                if (!childrenBySnap.TryGetValue(s.MergeParentSnapshotId, out List<string>? kids))
                {
                    kids = [];
                    childrenBySnap[s.MergeParentSnapshotId] = kids;
                }

                kids.Add(s.SnapshotId);
            }
        }

        foreach (SaveSnapshotRecord candidate in rollingCandidates)
        {
            if (excess <= 0)
            {
                break;
            }

            if (protectedIds.Contains(candidate.SnapshotId))
            {
                continue;
            }

            childrenBySnap.TryGetValue(candidate.SnapshotId, out List<string>? kids);
            int kidCount = kids?.Count ?? 0;

            // Never break fork hubs (nodes with >= 2 children) or cross-branch parents.
            if (kidCount >= 2)
            {
                continue;
            }

            string? currentParentId = parentBySnap.GetValueOrDefault(candidate.SnapshotId);
            if (kidCount == 1)
            {
                string onlyChildId = kids![0];
                if (!branchBySnap.TryGetValue(onlyChildId, out string? childBranchId)
                    || !string.Equals(childBranchId, candidate.BranchId, StringComparison.Ordinal))
                {
                    continue;
                }

                var (deleted, _) = await _database
                    .DeleteSaveSnapshotAndRelinkChildrenAsync(candidate.SnapshotId, currentParentId, now, ct)
                    .ConfigureAwait(false);
                if (deleted)
                {
                    parentBySnap[onlyChildId] = currentParentId;
                    if (!string.IsNullOrWhiteSpace(currentParentId)
                        && childrenBySnap.TryGetValue(currentParentId, out List<string>? parentKids))
                    {
                        parentKids.Remove(candidate.SnapshotId);
                        parentKids.Add(onlyChildId);
                    }

                    childrenBySnap.Remove(candidate.SnapshotId);
                    excess--;
                }
            }
            else
            {
                var (deleted, _) = await _database.DeleteSaveSnapshotAsync(candidate.SnapshotId, now, ct).ConfigureAwait(false);
                if (deleted)
                {
                    if (!string.IsNullOrWhiteSpace(currentParentId)
                        && childrenBySnap.TryGetValue(currentParentId, out List<string>? parentKids))
                    {
                        parentKids.Remove(candidate.SnapshotId);
                    }

                    childrenBySnap.Remove(candidate.SnapshotId);
                    excess--;
                }
            }
        }
    }

    private async Task<Dictionary<string, (SaveFileCategory Category, long Size, string Hash)>> ScanDiskFilesReadOnlyAsync(
        GameRecord game,
        ResolvedSaveLayout layout,
        bool includeConfig,
        CancellationToken ct)
    {
        var map = new Dictionary<string, (SaveFileCategory Category, long Size, string Hash)>(StringComparer.OrdinalIgnoreCase);

        var roots = new List<ResolvedSaveLocation>(layout.SaveLocations.Where(l => l.Exists && l.IsSafe));
        if (includeConfig)
        {
            roots.AddRange(layout.ConfigLocations.Where(l => l.Exists && l.IsSafe));
        }

        foreach (ResolvedSaveLocation loc in AntiTearFileCapture.DeduplicateNestedLocations(roots))
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc.ResolvedFullPath));
            if (AntiTearFileCapture.IsReparsePoint(path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                await TryHashDiskFileAsync(path, loc.Category, game, map, ct).ConfigureAwait(false);
            }
            else if (Directory.Exists(path))
            {
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 32,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (string file in Directory.EnumerateFiles(path, "*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    if (file.EndsWith(AtomicSaveRestorer.RestoreStageSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    await TryHashDiskFileAsync(file, loc.Category, game, map, ct).ConfigureAwait(false);
                }
            }
        }

        var validKeys = new List<string>();
        foreach (ResolvedSaveLocation reg in layout.RegistryKeys)
        {
            if (reg.IsSafe
                && DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(reg.ResolvedFullPath, out string canonical, out _, out _))
            {
                validKeys.Add(canonical);
            }
        }

        if (validKeys.Count > 0)
        {
            DeterministicRegistrySnapshot regSnap = DeterministicHkcuRegistrySerializer.ExportKeys(validKeys);
            if (regSnap.Keys.Count > 0)
            {
                byte[] bytes = DeterministicHkcuRegistrySerializer.SerializeToUtf8Bytes(regSnap);
                map[AntiTearFileCapture.RegistryLogicalPath] = (
                    SaveFileCategory.RegistryExport,
                    bytes.Length,
                    CasChunkStore.ComputeBlake3Hex(bytes));
            }
        }

        return map;
    }

    private async Task TryHashDiskFileAsync(
        string fullPath,
        SaveFileCategory category,
        GameRecord game,
        Dictionary<string, (SaveFileCategory Category, long Size, string Hash)> sink,
        CancellationToken ct)
    {
        try
        {
            byte[] data = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            string logical = _ruleResolver.CanonicalizeToLogicalPath(fullPath, game);
            sink[logical] = (category, data.Length, CasChunkStore.ComputeBlake3Hex(data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore transiently locked or inaccessible files during read-only diff.
        }
    }

    private static SaveSnapshotDiffDto ComputeFileManifestDiff(
        string baseLabel,
        string targetLabel,
        Dictionary<string, (SaveFileCategory Category, long Size, string Hash)> left,
        Dictionary<string, (SaveFileCategory Category, long Size, string Hash)> right)
    {
        var allPaths = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<SaveFileDiffItemDto>(allPaths.Count);
        int added = 0, modified = 0, deleted = 0, unchanged = 0;

        foreach (string path in allPaths)
        {
            bool inLeft = left.TryGetValue(path, out var l);
            bool inRight = right.TryGetValue(path, out var r);

            if (!inLeft && inRight)
            {
                added++;
                items.Add(new SaveFileDiffItemDto
                {
                    LogicalPath = path,
                    Category = r.Category,
                    DiffKind = SaveFileDiffKind.Added,
                    LeftSize = 0,
                    RightSize = r.Size,
                    LeftHash = null,
                    RightHash = r.Hash,
                });
            }
            else if (inLeft && !inRight)
            {
                deleted++;
                items.Add(new SaveFileDiffItemDto
                {
                    LogicalPath = path,
                    Category = l.Category,
                    DiffKind = SaveFileDiffKind.Deleted,
                    LeftSize = l.Size,
                    RightSize = 0,
                    LeftHash = l.Hash,
                    RightHash = null,
                });
            }
            else if (!string.Equals(l.Hash, r.Hash, StringComparison.OrdinalIgnoreCase))
            {
                modified++;
                items.Add(new SaveFileDiffItemDto
                {
                    LogicalPath = path,
                    Category = r.Category,
                    DiffKind = SaveFileDiffKind.Modified,
                    LeftSize = l.Size,
                    RightSize = r.Size,
                    LeftHash = l.Hash,
                    RightHash = r.Hash,
                });
            }
            else
            {
                unchanged++;
                items.Add(new SaveFileDiffItemDto
                {
                    LogicalPath = path,
                    Category = r.Category,
                    DiffKind = SaveFileDiffKind.Unchanged,
                    LeftSize = l.Size,
                    RightSize = r.Size,
                    LeftHash = l.Hash,
                    RightHash = r.Hash,
                });
            }
        }

        return new SaveSnapshotDiffDto
        {
            BaseLabel = baseLabel,
            TargetLabel = targetLabel,
            Items = items,
            AddedCount = added,
            ModifiedCount = modified,
            DeletedCount = deleted,
            UnchangedCount = unchanged,
        };
    }

    private static bool ManifestsAreIdentical(
        IReadOnlyList<SaveSnapshotFileRecord> a,
        IReadOnlyList<SaveSnapshotFileRecord> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var mapA = a.ToDictionary(f => f.LogicalPath, f => f.FileBlake3, StringComparer.OrdinalIgnoreCase);
        foreach (SaveSnapshotFileRecord fb in b)
        {
            if (!mapA.TryGetValue(fb.LogicalPath, out string? hashA)
                || !string.Equals(hashA, fb.FileBlake3, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string PickUnusedBranchColor(IReadOnlyList<SaveBranchRecord> existingBranches)
    {
        var used = existingBranches
            .Select(b => b.ColorHex)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string color in SaveBranchColors.Palette)
        {
            if (!used.Contains(color))
            {
                return color;
            }
        }

        return SaveBranchColors.PickForIndex(existingBranches.Count);
    }

    private SemaphoreSlim GetGameLock(string gameId) =>
        _gameLocks.GetOrAdd(gameId, static _ => new SemaphoreSlim(1, 1));

    private async Task PublishTreeChangedAsync(
        string gameId,
        string? branchId,
        string? snapshotId,
        string reason,
        CancellationToken ct)
    {
        var ev = new SaveTreeChangedEvent(gameId, branchId, snapshotId, reason, _timeProvider.GetUtcNow());
        try
        {
            SaveTreeChanged?.Invoke(this, ev);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveTreeChanged subscriber threw for game {GameId}.", gameId);
        }

        await _eventBus.PublishAsync(ev, ct).ConfigureAwait(false);
    }

    private static CreateSaveSnapshotResult FailedSnapshot(string message) =>
        new()
        {
            Succeeded = false,
            Skipped = false,
            Message = message,
        };
}
