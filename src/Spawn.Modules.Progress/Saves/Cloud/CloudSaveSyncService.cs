using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Decentralized 32 MiB <c>SPPK</c> Packfile cloud save synchronization service implementing
/// 64-generation lineage checking, strict tail-commit uploads (<c>packs/ -&gt; snapshots/ -&gt; heads/</c>),
/// range-based delta chunk downloads with BLAKE3 verification, and non-destructive divergence branch preservation.
/// </summary>
public sealed class CloudSaveSyncService : ICloudSaveSyncService
{
    public const int MaxLineageDepth = 64;
    private const string CredentialIdPrefix = "cloud-sync-";
    private const string CloudSyncHttpClientName = "SpawnCloudSync";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SpawnDatabase _database;
    private readonly CasChunkStore _casChunkStore;
    private readonly DpapiSecretStore _secretStore;
    private readonly ISaveTimeMachineService? _timeMachineService;
    private readonly ISpawnEventBus? _eventBus;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CloudSaveSyncService>? _logger;
    private readonly Func<CloudSyncEndpointRecord, CloudSyncEndpointSecrets?, ICloudSyncTransport>? _customTransportFactory;
    private readonly long _maxPackPayloadBytes;

    public CloudSaveSyncService(
        SpawnDatabase database,
        CasChunkStore casChunkStore,
        DpapiSecretStore secretStore,
        ISaveTimeMachineService? timeMachineService = null,
        ISpawnEventBus? eventBus = null,
        IHttpClientFactory? httpClientFactory = null,
        TimeProvider? timeProvider = null,
        ILogger<CloudSaveSyncService>? logger = null)
        : this(
            database,
            casChunkStore,
            secretStore,
            timeMachineService,
            eventBus,
            httpClientFactory,
            timeProvider,
            logger,
            customTransportFactory: null,
            maxPackPayloadBytes: SppkPackfileFormat.DefaultMaxPackPayloadBytes)
    {
    }

    internal CloudSaveSyncService(
        SpawnDatabase database,
        CasChunkStore casChunkStore,
        DpapiSecretStore secretStore,
        ISaveTimeMachineService? timeMachineService,
        ISpawnEventBus? eventBus,
        IHttpClientFactory? httpClientFactory,
        TimeProvider? timeProvider,
        ILogger<CloudSaveSyncService>? logger,
        Func<CloudSyncEndpointRecord, CloudSyncEndpointSecrets?, ICloudSyncTransport>? customTransportFactory,
        long maxPackPayloadBytes = SppkPackfileFormat.DefaultMaxPackPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(casChunkStore);
        ArgumentNullException.ThrowIfNull(secretStore);

        _database = database;
        _casChunkStore = casChunkStore;
        _secretStore = secretStore;
        _timeMachineService = timeMachineService;
        _eventBus = eventBus;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _customTransportFactory = customTransportFactory;
        _maxPackPayloadBytes = maxPackPayloadBytes > 0
            ? maxPackPayloadBytes
            : SppkPackfileFormat.DefaultMaxPackPayloadBytes;
    }

    // -------------------------------------------------------------------------
    // Endpoint CRUD & DPAPI Secrets
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<CloudSyncEndpointRecord>> ListEndpointsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CloudSyncEndpointRecord> rows = await _database
            .GetAllCloudSyncEndpointsAsync(ct)
            .ConfigureAwait(false);

        var result = new List<CloudSyncEndpointRecord>(rows.Count);
        foreach (CloudSyncEndpointRecord row in rows)
        {
            bool hasSecret = !string.IsNullOrWhiteSpace(row.SecretCredentialId)
                && _secretStore.TryReadSecret(row.SecretCredentialId) is not null;
            result.Add(row with { HasSecretConfigured = hasSecret });
        }

        return result;
    }

    public async Task<CloudSyncEndpointRecord> SaveEndpointAsync(
        CloudSyncEndpointRecord endpoint,
        CloudSyncEndpointSecrets? secrets = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.EndpointUrl);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string endpointId = string.IsNullOrWhiteSpace(endpoint.EndpointId)
            ? SpawnId.NewUlid(now)
            : endpoint.EndpointId.Trim();

        CloudSyncEndpointRecord? existing = await _database
            .GetCloudSyncEndpointByIdAsync(endpointId, ct)
            .ConfigureAwait(false);

        string? secretCredentialId = existing?.SecretCredentialId ?? endpoint.SecretCredentialId;
        bool hasSecretConfigured;

        if (secrets is not null)
        {
            string credId = $"{CredentialIdPrefix}{endpointId}";
            if (string.IsNullOrWhiteSpace(secrets.UsernameOrAccessKey)
                && string.IsNullOrWhiteSpace(secrets.PasswordOrSecretKey))
            {
                _secretStore.DeleteSecret(credId);
                secretCredentialId = null;
                hasSecretConfigured = false;
            }
            else
            {
                string secretJson = JsonSerializer.Serialize(secrets, JsonOptions);
                _secretStore.SaveSecret(credId, secretJson);
                secretCredentialId = credId;
                hasSecretConfigured = true;
            }
        }
        else
        {
            hasSecretConfigured = !string.IsNullOrWhiteSpace(secretCredentialId)
                && _secretStore.TryReadSecret(secretCredentialId) is not null;
        }

        CloudSyncEndpointRecord recordToSave = endpoint with
        {
            EndpointId = endpointId,
            DisplayName = endpoint.DisplayName.Trim(),
            EndpointUrl = endpoint.EndpointUrl.Trim(),
            BucketOrRoot = endpoint.BucketOrRoot?.Trim() ?? string.Empty,
            Region = string.IsNullOrWhiteSpace(endpoint.Region) ? null : endpoint.Region.Trim(),
            SecretCredentialId = secretCredentialId,
            HasSecretConfigured = hasSecretConfigured,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
        };

        await _database.UpsertCloudSyncEndpointAsync(recordToSave, ct).ConfigureAwait(false);
        return recordToSave;
    }

    public async Task<bool> DeleteEndpointAsync(string endpointId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        CloudSyncEndpointRecord? existing = await _database
            .GetCloudSyncEndpointByIdAsync(endpointId, ct)
            .ConfigureAwait(false);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.SecretCredentialId))
        {
            _secretStore.DeleteSecret(existing.SecretCredentialId);
        }

        return await _database.DeleteCloudSyncEndpointAsync(endpointId, ct).ConfigureAwait(false);
    }

    public async Task<CloudSyncEndpointTestResult> TestEndpointConnectionAsync(string endpointId, CancellationToken ct = default)
    {
        CloudSyncEndpointRecord? endpoint = await _database
            .GetCloudSyncEndpointByIdAsync(endpointId, ct)
            .ConfigureAwait(false);
        if (endpoint is null)
        {
            return new CloudSyncEndpointTestResult(false, $"未找到云同步端点 '{endpointId}'。");
        }

        try
        {
            using ICloudSyncTransport transport = CreateTransport(endpoint);
            return await transport.TestConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CloudSyncEndpointTestResult(false, $"云同步端点配置无效或无法连接：{ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // 3-Way Lineage Planning
    // -------------------------------------------------------------------------

    public async Task<CloudSyncPlanPreview> PlanSyncAsync(
        string gameId,
        string? endpointId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        SaveGameStateRecord state = await _database
            .GetOrCreateSaveGameStateAsync(gameId, _timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);

        CloudSyncEndpointRecord? endpoint = await ResolveEndpointAsync(state, endpointId, ct).ConfigureAwait(false);
        if (endpoint is null)
        {
            return new CloudSyncPlanPreview
            {
                GameId = gameId,
                Relation = CloudSyncRelation.NotConfigured,
                Message = "未配置云同步端点。",
            };
        }

        try
        {
            using ICloudSyncTransport transport = CreateTransport(endpoint);
            PlanContext context = await BuildPlanContextAsync(gameId, state, endpoint, transport, ct).ConfigureAwait(false);
            return context.Preview;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cloud sync planning failed for game {GameId}", gameId);
            return new CloudSyncPlanPreview
            {
                GameId = gameId,
                EndpointId = endpoint.EndpointId,
                Relation = CloudSyncRelation.Error,
                Message = $"云同步规划失败：{ex.Message}",
            };
        }
    }

    // -------------------------------------------------------------------------
    // Sync Execution (Push / Pull / Divergence Resolution)
    // -------------------------------------------------------------------------

    public async Task<CloudSyncExecutionReport> ExecuteSyncAsync(
        string gameId,
        string? endpointId = null,
        CloudDivergenceResolution? divergenceResolution = null,
        IProgress<CloudSyncProgressTick>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        SaveGameStateRecord state = await _database
            .GetOrCreateSaveGameStateAsync(gameId, _timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);

        CloudSyncEndpointRecord? endpoint = await ResolveEndpointAsync(state, endpointId, ct).ConfigureAwait(false);
        if (endpoint is null)
        {
            return new CloudSyncExecutionReport
            {
                Succeeded = false,
                Relation = CloudSyncRelation.NotConfigured,
                Message = "未配置云同步端点，请先在设置中配置 LocalFolder、WebDAV 或 S3/R2 端点。",
            };
        }

        try
        {
            using ICloudSyncTransport transport = CreateTransport(endpoint);
            progress?.Report(new CloudSyncProgressTick("Init", 0, 1, 0, "正在检查远端血缘状态..."));

            PlanContext ctx = await BuildPlanContextAsync(gameId, state, endpoint, transport, ct).ConfigureAwait(false);
            CloudSyncPlanPreview plan = ctx.Preview;

            switch (plan.Relation)
            {
                case CloudSyncRelation.UpToDate:
                {
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    await _database.SetSaveGameCloudSyncStatusAsync(gameId, now, hasUnresolvedConflict: false, now, ct)
                        .ConfigureAwait(false);
                    return new CloudSyncExecutionReport
                    {
                        Succeeded = true,
                        Relation = CloudSyncRelation.UpToDate,
                        Message = "本地与远端存档已处于最新一致状态。",
                        ResultHeadSnapshotId = plan.LocalHeadSnapshot?.SnapshotId,
                    };
                }

                case CloudSyncRelation.FastForwardPush:
                    return await ExecuteFastForwardPushAsync(ctx, transport, progress, ct).ConfigureAwait(false);

                case CloudSyncRelation.FastForwardPull:
                    return await ExecuteFastForwardPullAsync(
                        ctx,
                        transport,
                        targetBranchId: ctx.ActiveBranch.BranchId,
                        createSafetySnapshot: true,
                        progress,
                        ct).ConfigureAwait(false);

                case CloudSyncRelation.Diverged:
                    if (!divergenceResolution.HasValue)
                    {
                        DateTimeOffset now = _timeProvider.GetUtcNow();
                        await _database.SetSaveGameCloudSyncStatusAsync(gameId, lastSyncedAtUtc: null, hasUnresolvedConflict: true, now, ct)
                            .ConfigureAwait(false);
                        await PublishTreeChangedAsync(gameId, ctx.ActiveBranch.BranchId, ctx.LocalHeadSnapshot?.SnapshotId, "CloudConflictDetected", ct)
                            .ConfigureAwait(false);

                        return new CloudSyncExecutionReport
                        {
                            Succeeded = false,
                            Relation = CloudSyncRelation.Diverged,
                            Message = "检测到双端离线分叉：已拒绝按时间静默覆盖，请选择保留本机或采用远端（另一端将完整保留为独立分叉分支）。",
                            ResultHeadSnapshotId = ctx.LocalHeadSnapshot?.SnapshotId,
                        };
                    }

                    return divergenceResolution.Value == CloudDivergenceResolution.AdoptRemoteAndBackupLocal
                        ? await ExecuteAdoptRemoteAndBackupLocalAsync(ctx, transport, progress, ct).ConfigureAwait(false)
                        : await ExecuteKeepLocalAndBackupRemoteAsync(ctx, transport, progress, ct).ConfigureAwait(false);

                default:
                    return new CloudSyncExecutionReport
                    {
                        Succeeded = false,
                        Relation = plan.Relation,
                        Message = plan.Message,
                    };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cloud sync execution failed for game {GameId}", gameId);
            return new CloudSyncExecutionReport
            {
                Succeeded = false,
                Relation = CloudSyncRelation.Error,
                Message = $"云同步执行失败：{ex.Message}",
            };
        }
    }

    public async Task<IReadOnlyList<CloudSyncExecutionReport>> SyncAllConfiguredGamesAsync(
        IProgress<CloudSyncProgressTick>? progress = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<GameRecord> games = await _database.GetAllGamesAsync(ct).ConfigureAwait(false);
        IReadOnlyList<SaveGameStateRecord> states = await _database.GetAllSaveGameStatesAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, SaveGameAggregateStats> stats = await _database
            .GetSaveGameAggregateStatsAsync(ct)
            .ConfigureAwait(false);
        CloudSyncEndpointRecord? defaultEndpoint = await _database
            .GetDefaultCloudSyncEndpointAsync(ct)
            .ConfigureAwait(false);

        var stateMap = states.ToDictionary(static s => s.GameId, StringComparer.OrdinalIgnoreCase);
        var reports = new List<CloudSyncExecutionReport>();

        var targetGameIds = new List<string>();
        foreach (GameRecord game in games)
        {
            bool hasExplicitEndpoint = stateMap.TryGetValue(game.GameId, out SaveGameStateRecord? st)
                && !string.IsNullOrWhiteSpace(st.CloudEndpointId);
            bool hasSnapshotsOrState = stateMap.ContainsKey(game.GameId)
                || (stats.TryGetValue(game.GameId, out SaveGameAggregateStats? gStats) && gStats.SnapshotCount > 0);

            if (hasExplicitEndpoint || (defaultEndpoint is not null && hasSnapshotsOrState))
            {
                targetGameIds.Add(game.GameId);
            }
        }

        for (int i = 0; i < targetGameIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string gid = targetGameIds[i];
            progress?.Report(new CloudSyncProgressTick("SyncAll", i, targetGameIds.Count, 0, $"正在同步游戏 ({i + 1}/{targetGameIds.Count})..."));
            CloudSyncExecutionReport report = await ExecuteSyncAsync(gid, endpointId: null, divergenceResolution: null, progress: null, ct)
                .ConfigureAwait(false);
            reports.Add(report);
        }

        return reports;
    }

    // -------------------------------------------------------------------------
    // Core Planning & Lineage Analysis
    // -------------------------------------------------------------------------

    private async Task<PlanContext> BuildPlanContextAsync(
        string gameId,
        SaveGameStateRecord state,
        CloudSyncEndpointRecord endpoint,
        ICloudSyncTransport transport,
        CancellationToken ct)
    {
        SaveSettingsDto settings = await _database.GetSaveSettingsAsync(ct).ConfigureAwait(false);
        SaveBranchRecord activeBranch = await ResolveActiveBranchAsync(gameId, state, ct).ConfigureAwait(false);

        string? localHeadId = state.CurrentHeadSnapshotId ?? activeBranch.HeadSnapshotId;
        SaveSnapshotRecord? localHead = !string.IsNullOrWhiteSpace(localHeadId)
            ? await _database.GetSaveSnapshotByIdAsync(localHeadId, ct).ConfigureAwait(false)
            : null;

        IReadOnlyList<string> localAncestors = localHead is not null
            ? await GetLocalAncestorIdsBfsAsync(localHead.SnapshotId, MaxLineageDepth, ct).ConfigureAwait(false)
            : [];
        var localAncestorSet = new HashSet<string>(localAncestors, StringComparer.Ordinal);

        string safeGameId = CloudSyncTransportHelpers.SanitizePathSegment(gameId);
        IReadOnlyList<CloudSyncObjectInfo> headObjects = await transport
            .ListPrefixAsync($"heads/{safeGameId}/", ct)
            .ConfigureAwait(false);

        var allRemoteHeads = new List<CloudRemoteHeadSummary>();
        foreach (CloudSyncObjectInfo obj in headObjects)
        {
            if (!obj.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[]? raw = await transport.GetBytesAsync(obj.RelativePath, ct).ConfigureAwait(false);
            if (raw is null || raw.Length == 0)
            {
                continue;
            }

            RemoteMachineHeadDocument? doc = JsonSerializer.Deserialize<RemoteMachineHeadDocument>(raw, JsonOptions);
            if (doc is null || string.IsNullOrWhiteSpace(doc.HeadSnapshotId))
            {
                continue;
            }

            var ancestorList = new List<string>(doc.AncestorIds ?? []);
            if (!ancestorList.Contains(doc.HeadSnapshotId, StringComparer.Ordinal))
            {
                ancestorList.Insert(0, doc.HeadSnapshotId);
            }

            allRemoteHeads.Add(new CloudRemoteHeadSummary
            {
                MachineId = doc.MachineId,
                MachineName = doc.MachineName,
                HeadSnapshotId = doc.HeadSnapshotId,
                BranchName = doc.BranchName,
                UpdatedAtUtc = doc.UpdatedAtUtc,
                PlaytimeActiveSeconds = doc.PlaytimeActiveSeconds,
                FileCount = doc.FileCount,
                TotalRawBytes = doc.TotalRawBytes,
                AncestorIds = ancestorList,
            });
        }

        allRemoteHeads.Sort(static (a, b) => b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc));

        (CloudSyncRelation relation, CloudRemoteHeadSummary? selectedRemote, string? commonAncestor, string message) =
            ClassifyLineage(localHead, localAncestors, localAncestorSet, allRemoteHeads);

        int missingRemoteChunkCount = 0;
        long estimatedUploadBytes = 0;
        int missingLocalChunkCount = 0;
        long estimatedDownloadBytes = 0;
        int saveFileCount = localHead?.FileCount ?? selectedRemote?.FileCount ?? 0;

        if (relation == CloudSyncRelation.FastForwardPush && localHead is not null)
        {
            (IReadOnlyList<SppkChunkInput> missingToUpload, _, _, int syncableCount) = await ComputeMissingRemoteChunksAsync(
                gameId,
                localHead,
                transport,
                ct).ConfigureAwait(false);
            saveFileCount = syncableCount;
            missingRemoteChunkCount = missingToUpload.Count;
            estimatedUploadBytes = missingToUpload.Sum(static c => (long)c.CompressedBytes.Length);
        }
        else if ((relation == CloudSyncRelation.FastForwardPull || relation == CloudSyncRelation.Diverged)
            && selectedRemote is not null)
        {
            RemoteSnapshotManifest? remoteManifest = await TryFetchRemoteSnapshotManifestAsync(
                gameId,
                selectedRemote.HeadSnapshotId,
                transport,
                ct).ConfigureAwait(false);
            if (remoteManifest is not null)
            {
                RemoteSnapshotFileEntry[] syncableRemoteFiles = remoteManifest.Files
                    .Where(static f => IsCloudSyncableFile(f.Category))
                    .ToArray();
                saveFileCount = syncableRemoteFiles.Length;
                var uniqueMissing = new HashSet<string>(StringComparer.Ordinal);
                foreach (RemoteSnapshotFileEntry f in syncableRemoteFiles)
                {
                    foreach (string hash in f.ChunkHashes)
                    {
                        string normalized = hash.Trim().ToLowerInvariant();
                        if (!_casChunkStore.ChunkFileExists(normalized))
                        {
                            uniqueMissing.Add(normalized);
                        }
                    }
                }

                missingLocalChunkCount = uniqueMissing.Count;
                estimatedDownloadBytes = uniqueMissing.Count > 0 ? remoteManifest.TotalStoredBytes : 0;
            }
        }

        var preview = new CloudSyncPlanPreview
        {
            GameId = gameId,
            EndpointId = endpoint.EndpointId,
            Relation = relation,
            Message = message,
            LocalHeadSnapshot = localHead,
            RemoteHead = selectedRemote,
            AllRemoteHeads = allRemoteHeads,
            CommonAncestorSnapshotId = commonAncestor,
            MissingRemoteChunkCount = missingRemoteChunkCount,
            EstimatedUploadBytes = estimatedUploadBytes,
            MissingLocalChunkCount = missingLocalChunkCount,
            EstimatedDownloadBytes = estimatedDownloadBytes,
            SaveFileCount = saveFileCount,
        };

        return new PlanContext(
            GameId: gameId,
            Settings: settings,
            GameState: state,
            ActiveBranch: activeBranch,
            Endpoint: endpoint,
            LocalHeadSnapshot: localHead,
            LocalAncestors: localAncestors,
            SelectedRemoteHead: selectedRemote,
            CommonAncestorSnapshotId: commonAncestor,
            Preview: preview);
    }

    private static (CloudSyncRelation Relation, CloudRemoteHeadSummary? SelectedRemote, string? CommonAncestor, string Message)
        ClassifyLineage(
            SaveSnapshotRecord? localHead,
            IReadOnlyList<string> localAncestors,
            HashSet<string> localAncestorSet,
            List<CloudRemoteHeadSummary> allRemoteHeads)
    {
        if (localHead is null && allRemoteHeads.Count == 0)
        {
            return (CloudSyncRelation.UpToDate, null, null, "本地与远端均暂无存档快照。");
        }

        if (localHead is not null && allRemoteHeads.Count == 0)
        {
            return (CloudSyncRelation.FastForwardPush, null, null, "远端暂无记录，可快进推送本地存档至云端。");
        }

        if (localHead is null && allRemoteHeads.Count > 0)
        {
            CloudRemoteHeadSummary newest = PickMaximalRemoteHeads(allRemoteHeads)[0];
            return (
                CloudSyncRelation.FastForwardPull,
                newest,
                null,
                $"本地尚无快照，可从设备 '{newest.MachineName ?? newest.MachineId}' 快进拉取。");
        }

        // Both local HEAD and at least one remote HEAD exist.
        string localId = localHead!.SnapshotId;
        var aheadOfLocal = new List<CloudRemoteHeadSummary>();
        var divergedFromLocal = new List<CloudRemoteHeadSummary>();
        bool anyRemoteMatchesLocal = false;

        foreach (CloudRemoteHeadSummary remote in allRemoteHeads)
        {
            if (string.Equals(remote.HeadSnapshotId, localId, StringComparison.Ordinal))
            {
                anyRemoteMatchesLocal = true;
                continue;
            }

            if (localAncestorSet.Contains(remote.HeadSnapshotId))
            {
                // Local already contains this remote head in its 64-generation ancestry.
                continue;
            }

            if (remote.AncestorIds.Contains(localId, StringComparer.Ordinal))
            {
                // Remote is a strict descendant of local HEAD.
                aheadOfLocal.Add(remote);
            }
            else
            {
                // Neither is an ancestor of the other => True Divergence!
                divergedFromLocal.Add(remote);
            }
        }

        if (divergedFromLocal.Count > 0)
        {
            CloudRemoteHeadSummary conflictRemote = PickMaximalRemoteHeads(divergedFromLocal)[0];
            string? commonAncestor = FindFirstCommonAncestor(localAncestors, conflictRemote.AncestorIds);
            return (
                CloudSyncRelation.Diverged,
                conflictRemote,
                commonAncestor,
                $"与设备 '{conflictRemote.MachineName ?? conflictRemote.MachineId}' 发生双端离线分叉，请手动选择保留方向。");
        }

        if (aheadOfLocal.Count > 0)
        {
            List<CloudRemoteHeadSummary> tips = PickMaximalRemoteHeads(aheadOfLocal);
            if (tips.Count > 1)
            {
                CloudRemoteHeadSummary latestTip = tips[0];
                return (
                    CloudSyncRelation.Diverged,
                    latestTip,
                    localId,
                    $"远端多台设备存在未合并分叉（如 '{latestTip.MachineName ?? latestTip.MachineId}'），请选择要采用的目标分支。");
            }

            CloudRemoteHeadSummary pullTarget = tips[0];
            return (
                CloudSyncRelation.FastForwardPull,
                pullTarget,
                localId,
                $"检测到设备 '{pullTarget.MachineName ?? pullTarget.MachineId}' 有更新进度，可安全快进拉取。");
        }

        if (anyRemoteMatchesLocal)
        {
            CloudRemoteHeadSummary matching = allRemoteHeads.First(
                r => string.Equals(r.HeadSnapshotId, localId, StringComparison.Ordinal));
            return (CloudSyncRelation.UpToDate, matching, localId, "本地与远端存档已同步一致。");
        }

        // Local is strictly ahead of every remote head.
        CloudRemoteHeadSummary latestRemote = allRemoteHeads[0];
        return (
            CloudSyncRelation.FastForwardPush,
            latestRemote,
            latestRemote.HeadSnapshotId,
            "本地有新存档进度，可快进推送至云端。");
    }

    private static List<CloudRemoteHeadSummary> PickMaximalRemoteHeads(List<CloudRemoteHeadSummary> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var maximal = new List<CloudRemoteHeadSummary>();
        foreach (CloudRemoteHeadSummary c in candidates)
        {
            bool strictlyContainedInOther = candidates.Any(other =>
                !ReferenceEquals(other, c)
                && !string.Equals(other.HeadSnapshotId, c.HeadSnapshotId, StringComparison.Ordinal)
                && other.AncestorIds.Contains(c.HeadSnapshotId, StringComparer.Ordinal));

            if (!strictlyContainedInOther
                && !maximal.Any(m => string.Equals(m.HeadSnapshotId, c.HeadSnapshotId, StringComparison.Ordinal)))
            {
                maximal.Add(c);
            }
        }

        if (maximal.Count == 0)
        {
            return [candidates[0]];
        }

        maximal.Sort(static (a, b) => b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc));
        return maximal;
    }

    private static string? FindFirstCommonAncestor(
        IReadOnlyList<string> localAncestors,
        IReadOnlyList<string> remoteAncestors)
    {
        if (localAncestors.Count == 0 || remoteAncestors.Count == 0)
        {
            return null;
        }

        var remoteSet = new HashSet<string>(remoteAncestors, StringComparer.Ordinal);
        foreach (string id in localAncestors)
        {
            if (remoteSet.Contains(id))
            {
                return id;
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Fast-Forward Push (Strict tail-commit: packs/ -> snapshots/ -> heads/)
    // -------------------------------------------------------------------------

    private async Task<CloudSyncExecutionReport> ExecuteFastForwardPushAsync(
        PlanContext ctx,
        ICloudSyncTransport transport,
        IProgress<CloudSyncProgressTick>? progress,
        CancellationToken ct)
    {
        SaveSnapshotRecord localHead = ctx.LocalHeadSnapshot
            ?? throw new InvalidOperationException("Cannot push when no local snapshot exists.");

        await transport.EnsureRepoInitializedAsync(ct).ConfigureAwait(false);

        // Collect the local snapshot chain that does not yet exist on remote (oldest -> newest).
        var snapshotsToUpload = new List<SaveSnapshotRecord>();
        string safeGameId = CloudSyncTransportHelpers.SanitizePathSegment(ctx.GameId);
        string? cursor = localHead.SnapshotId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(cursor) && snapshotsToUpload.Count < MaxLineageDepth && visited.Add(cursor))
        {
            bool remoteExists = await transport
                .ExistsAsync($"snapshots/{safeGameId}/{cursor}.json", ct)
                .ConfigureAwait(false);
            if (remoteExists && cursor != localHead.SnapshotId)
            {
                break;
            }

            SaveSnapshotRecord? snap = await _database.GetSaveSnapshotByIdAsync(cursor, ct).ConfigureAwait(false);
            if (snap is null)
            {
                break;
            }

            if (!remoteExists)
            {
                snapshotsToUpload.Add(snap);
            }

            cursor = snap.ParentSnapshotId;
        }

        snapshotsToUpload.Reverse(); // oldest first

        // Gather all files & missing chunks across snapshots to upload.
        IReadOnlyList<CloudSyncObjectInfo> remotePacks = await transport.ListPrefixAsync("packs/", ct).ConfigureAwait(false);
        var remotePackIds = new HashSet<string>(
            remotePacks
                .Select(static p => Path.GetFileNameWithoutExtension(p.RelativePath))
                .Where(static id => !string.IsNullOrWhiteSpace(id))!,
            StringComparer.Ordinal);

        var chunkPackMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingChunkInputs = new List<SppkChunkInput>();
        var seenMissing = new HashSet<string>(StringComparer.Ordinal);
        var filesBySnapshot = new Dictionary<string, IReadOnlyList<SaveSnapshotFileRecord>>(StringComparer.Ordinal);

        foreach (SaveSnapshotRecord snap in snapshotsToUpload)
        {
            IReadOnlyList<SaveSnapshotFileRecord> allFiles = await _database
                .GetSnapshotFilesAsync(snap.SnapshotId, ct)
                .ConfigureAwait(false);
            // Cloud sync strictly transmits [save] + HKCU registry exports and NEVER uploads [config] files.
            SaveSnapshotFileRecord[] files = allFiles
                .Where(static f => IsCloudSyncableFile(f.Category))
                .ToArray();
            filesBySnapshot[snap.SnapshotId] = files;

            var allHashes = files
                .SelectMany(static f => f.ChunkHashes)
                .Select(static h => h.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            IReadOnlyDictionary<string, CasChunkRecord> chunkRecords = await _database
                .GetCasChunksByHashesAsync(allHashes, ct)
                .ConfigureAwait(false);

            foreach (string hash in allHashes)
            {
                if (chunkPackMap.ContainsKey(hash))
                {
                    continue;
                }

                if (chunkRecords.TryGetValue(hash, out CasChunkRecord? rec)
                    && !string.IsNullOrWhiteSpace(rec.PackId)
                    && remotePackIds.Contains(rec.PackId))
                {
                    chunkPackMap[hash] = rec.PackId;
                    continue;
                }

                if (seenMissing.Add(hash))
                {
                    byte[] compressed = await _casChunkStore
                        .ReadChunkCompressedAsync(hash, ct)
                        .ConfigureAwait(false);
                    uint rawLen = chunkRecords.TryGetValue(hash, out CasChunkRecord? meta)
                        ? checked((uint)Math.Max(0, meta.RawSizeBytes))
                        : checked((uint)(await _casChunkStore.ReadChunkRawAsync(hash, verifyHash: false, ct).ConfigureAwait(false)).Length);
                    missingChunkInputs.Add(new SppkChunkInput(hash, compressed, rawLen));
                }
            }
        }

        // STEP 1: Pack & upload SPPK packfiles (<= 32 MiB target per pack)
        IReadOnlyList<IReadOnlyList<SppkChunkInput>> packBatches = SppkPackfileFormat.PartitionChunksForPacks(
            missingChunkInputs,
            _maxPackPayloadBytes);

        int uploadedPacks = 0;
        long uploadedBytes = 0;

        for (int i = 0; i < packBatches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<SppkChunkInput> batch = packBatches[i];
            SppkPackBuildResult builtPack = SppkPackfileFormat.WritePack(batch);

            progress?.Report(new CloudSyncProgressTick(
                "UploadPacks",
                i,
                packBatches.Count,
                uploadedBytes,
                $"正在上传 SPPK 数据包 ({i + 1}/{packBatches.Count})..."));

            if (!remotePackIds.Contains(builtPack.PackId))
            {
                await transport.PutBytesAsync(
                    builtPack.RelativePackPath,
                    builtPack.PackBytes,
                    "application/octet-stream",
                    ct).ConfigureAwait(false);
                remotePackIds.Add(builtPack.PackId);
                uploadedPacks++;
                uploadedBytes += builtPack.PackBytes.Length;
            }

            string[] batchHashes = batch.Select(static c => c.ChunkBlake3Hex).ToArray();
            await _database.UpdateCasChunkPackIdsAsync(batchHashes, builtPack.PackId, ct).ConfigureAwait(false);
            foreach (string h in batchHashes)
            {
                chunkPackMap[h] = builtPack.PackId;
            }
        }

        // STEP 2: Upload immutable snapshot manifests (oldest -> newest)
        for (int i = 0; i < snapshotsToUpload.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            SaveSnapshotRecord snap = snapshotsToUpload[i];
            IReadOnlyList<SaveSnapshotFileRecord> files = filesBySnapshot[snap.SnapshotId];
            var snapChunkMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string h in files.SelectMany(static f => f.ChunkHashes).Select(static h => h.Trim().ToLowerInvariant()))
            {
                if (chunkPackMap.TryGetValue(h, out string? pid))
                {
                    snapChunkMap[h] = pid;
                }
            }

            SaveBranchRecord? snapBranch = await _database.GetSaveBranchByIdAsync(snap.BranchId, ct).ConfigureAwait(false);
            int syncFileCount = files.Count(static f => f.Category == SaveFileCategory.Save);
            int syncRegCount = files.Count(static f => f.Category == SaveFileCategory.RegistryExport);
            long syncRawBytes = files.Sum(static f => Math.Max(0, f.FileSize));
            var manifest = new RemoteSnapshotManifest
            {
                FormatVersion = 1,
                SnapshotId = snap.SnapshotId,
                GameId = snap.GameId,
                ParentSnapshotId = snap.ParentSnapshotId,
                MergeParentSnapshotId = snap.MergeParentSnapshotId,
                BranchName = snapBranch?.Name ?? ctx.ActiveBranch.Name,
                ProfileId = snap.ProfileId,
                ModProfileHash = snap.ModProfileHash,
                TriggerKind = snap.TriggerKind,
                Note = snap.Note,
                FileCount = syncFileCount,
                RegistryKeyCount = syncRegCount > 0 ? Math.Max(syncRegCount, snap.RegistryKeyCount) : 0,
                TotalRawBytes = syncRawBytes,
                TotalStoredBytes = snap.TotalStoredBytes,
                CreatedMachineId = snap.CreatedMachineId,
                CreatedMachineName = ctx.Settings.MachineDisplayName,
                PlaytimeActiveSeconds = snap.PlaytimeActiveSeconds,
                CreatedAtUtc = snap.CreatedAtUtc,
                IsPinned = snap.IsPinned,
                Files = files.Select(static f => new RemoteSnapshotFileEntry
                {
                    LogicalPath = f.LogicalPath,
                    OriginalPath = f.OriginalPath,
                    Category = f.Category,
                    FileSize = f.FileSize,
                    LastWriteTimeUtc = f.LastWriteTimeUtc,
                    FileBlake3 = f.FileBlake3,
                    ChunkHashes = f.ChunkHashes,
                }).ToArray(),
                ChunkPackMap = snapChunkMap,
            };

            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await transport.PutBytesAsync(
                $"snapshots/{safeGameId}/{snap.SnapshotId}.json",
                manifestBytes,
                "application/json",
                ct).ConfigureAwait(false);
            uploadedBytes += manifestBytes.Length;
        }

        // STEP 3 (Tail-Commit): Atomically publish heads/<GameId>/<MachineId>.json LAST
        IReadOnlyList<string> headAncestors = await GetLocalAncestorIdsBfsAsync(localHead.SnapshotId, MaxLineageDepth, ct)
            .ConfigureAwait(false);
        byte[] headBytes = await PublishMachineHeadAsync(
            ctx.GameId,
            ctx.Settings,
            localHead,
            ctx.ActiveBranch.Name,
            headAncestors,
            transport,
            ct).ConfigureAwait(false);
        uploadedBytes += headBytes.Length;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _database.SetSaveGameCloudSyncStatusAsync(ctx.GameId, now, hasUnresolvedConflict: false, now, ct)
            .ConfigureAwait(false);
        await PublishTreeChangedAsync(ctx.GameId, ctx.ActiveBranch.BranchId, localHead.SnapshotId, "CloudPush", ct)
            .ConfigureAwait(false);

        progress?.Report(new CloudSyncProgressTick("Completed", 1, 1, uploadedBytes, "云端推送完成。"));

        return new CloudSyncExecutionReport
        {
            Succeeded = true,
            Relation = CloudSyncRelation.FastForwardPush,
            Message = uploadedPacks > 0
                ? $"已封装 {uploadedPacks} 个 SPPK 数据包并推送最新快照至云端。"
                : "已增量同步快照清单与分支指针至云端（所有块已去重复用）。",
            UploadedPackCount = uploadedPacks,
            UploadedBytes = uploadedBytes,
            ResultHeadSnapshotId = localHead.SnapshotId,
        };
    }

    // -------------------------------------------------------------------------
    // Fast-Forward Pull (Range-based SPPK delta download + BLAKE3 verification)
    // -------------------------------------------------------------------------

    private async Task<CloudSyncExecutionReport> ExecuteFastForwardPullAsync(
        PlanContext ctx,
        ICloudSyncTransport transport,
        string targetBranchId,
        bool createSafetySnapshot,
        IProgress<CloudSyncProgressTick>? progress,
        CancellationToken ct)
    {
        CloudRemoteHeadSummary remoteHead = ctx.SelectedRemoteHead
            ?? throw new InvalidOperationException("No remote head available for pull.");

        // When restoring to disk via ISaveTimeMachineService, defer advancing branch & active HEAD until
        // RollbackToSnapshotAsync succeeds so: (a) a blocked restore (e.g. game running) leaves HEAD untouched
        // and re-plannable as FastForwardPull, (b) PreRollbackSafety attaches to old local HEAD, and
        // (c) AdoptRemoteAndBackupLocal diffs outgoing files against old local HEAD to prune stale [save] files.
        bool deferHeadAdvance = _timeMachineService is not null;
        (int downloadedChunks, long downloadedBytes, SaveSnapshotRecord importedTip) =
            await PullRemoteChainIntoBranchAsync(
                ctx.GameId,
                remoteHead.HeadSnapshotId,
                targetBranchId,
                advanceBranchHead: !deferHeadAdvance,
                updateGameActiveHead: !deferHeadAdvance,
                pinTip: false,
                transport,
                progress,
                ct).ConfigureAwait(false);

        var warnings = new List<string>();
        if (_timeMachineService is not null)
        {
            progress?.Report(new CloudSyncProgressTick("Restore", 0, 1, downloadedBytes, "正在原子还原云端存档至本地目录..."));
            SaveRestoreOutcome restore = await _timeMachineService.RollbackToSnapshotAsync(
                new RollbackSaveRequest
                {
                    SnapshotId = importedTip.SnapshotId,
                    // Cloud sync strictly restores [save] only and never overwrites machine-local [config] files.
                    IncludeConfigFiles = false,
                    ModMismatchPolicy = SaveModMismatchPolicy.ProceedAnyway,
                    CreateSafetySnapshot = createSafetySnapshot,
                },
                ct).ConfigureAwait(false);

            if (!restore.Succeeded)
            {
                return new CloudSyncExecutionReport
                {
                    Succeeded = false,
                    Relation = CloudSyncRelation.FastForwardPull,
                    Message = $"云端快照已下载至本地仓库，但应用到磁盘失败：{restore.Message}",
                    DownloadedChunkCount = downloadedChunks,
                    DownloadedBytes = downloadedBytes,
                    ResultHeadSnapshotId = ctx.LocalHeadSnapshot?.SnapshotId,
                    Warnings = restore.Warnings,
                };
            }

            warnings.AddRange(restore.Warnings);

            // Ensure branch HEAD and active game HEAD point cleanly at the pulled remote tip.
            DateTimeOffset restoreTs = _timeProvider.GetUtcNow();
            await _database.UpdateSaveBranchHeadAsync(targetBranchId, importedTip.SnapshotId, restoreTs, ct)
                .ConfigureAwait(false);
            await _database.SetActiveSaveHeadAsync(ctx.GameId, targetBranchId, importedTip.SnapshotId, restoreTs, ct)
                .ConfigureAwait(false);
        }

        // Align this machine's remote head pointer so subsequent PlanSync calls see UpToDate.
        IReadOnlyList<string> alignedAncestors = await GetLocalAncestorIdsBfsAsync(importedTip.SnapshotId, MaxLineageDepth, ct)
            .ConfigureAwait(false);
        await PublishMachineHeadAsync(
            ctx.GameId,
            ctx.Settings,
            importedTip,
            ctx.ActiveBranch.Name,
            alignedAncestors,
            transport,
            ct).ConfigureAwait(false);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _database.SetSaveGameCloudSyncStatusAsync(ctx.GameId, now, hasUnresolvedConflict: false, now, ct)
            .ConfigureAwait(false);
        await PublishTreeChangedAsync(ctx.GameId, targetBranchId, importedTip.SnapshotId, "CloudPull", ct)
            .ConfigureAwait(false);

        progress?.Report(new CloudSyncProgressTick("Completed", 1, 1, downloadedBytes, "云端拉取完成。"));

        return new CloudSyncExecutionReport
        {
            Succeeded = true,
            Relation = CloudSyncRelation.FastForwardPull,
            Message = $"已从设备 '{remoteHead.MachineName ?? remoteHead.MachineId}' 快进拉取存档（下载 {downloadedChunks} 个缺失块）。",
            DownloadedChunkCount = downloadedChunks,
            DownloadedBytes = downloadedBytes,
            ResultHeadSnapshotId = importedTip.SnapshotId,
            Warnings = warnings,
        };
    }

    // -------------------------------------------------------------------------
    // Non-Destructive Divergence Resolution (Branch Preservation)
    // -------------------------------------------------------------------------

    private async Task<CloudSyncExecutionReport> ExecuteAdoptRemoteAndBackupLocalAsync(
        PlanContext ctx,
        ICloudSyncTransport transport,
        IProgress<CloudSyncProgressTick>? progress,
        CancellationToken ct)
    {
        SaveSnapshotRecord localHead = ctx.LocalHeadSnapshot
            ?? throw new InvalidOperationException("Local head snapshot is required to resolve divergence.");
        CloudRemoteHeadSummary remoteHead = ctx.SelectedRemoteHead
            ?? throw new InvalidOperationException("Remote head summary is required to resolve divergence.");

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string safeLocalMachine = CloudSyncTransportHelpers.SanitizePathSegment(
            string.IsNullOrWhiteSpace(ctx.Settings.MachineDisplayName) ? ctx.Settings.MachineId : ctx.Settings.MachineDisplayName);
        string backupBranchName = await GenerateUniqueBranchNameAsync(
            ctx.GameId,
            $"backup-{safeLocalMachine}-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}",
            ct).ConfigureAwait(false);

        IReadOnlyList<SaveBranchRecord> existingBranches = await _database
            .GetSaveBranchesByGameAsync(ctx.GameId, ct)
            .ConfigureAwait(false);
        var backupBranch = new SaveBranchRecord
        {
            BranchId = SpawnId.NewUlid(now),
            GameId = ctx.GameId,
            Name = backupBranchName,
            Description = $"云同步分叉保留的本机进度（原分支：{ctx.ActiveBranch.Name}）",
            ColorHex = SaveBranchColors.PickForIndex(existingBranches.Count),
            BaseSnapshotId = ctx.CommonAncestorSnapshotId ?? localHead.SnapshotId,
            HeadSnapshotId = localHead.SnapshotId,
            IsDefault = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await _database.UpsertSaveBranchAsync(backupBranch, ct).ConfigureAwait(false);
        await _database.SetSaveSnapshotPinnedAsync(localHead.SnapshotId, isPinned: true, ct).ConfigureAwait(false);

        // Create a zero-overhead pinned anchor snapshot on the backup branch so the branch owns
        // a dedicated lane node in the branching save tree with SnapshotCount >= 1.
        IReadOnlyList<SaveSnapshotFileRecord> localFiles = await _database
            .GetSnapshotFilesAsync(localHead.SnapshotId, ct)
            .ConfigureAwait(false);
        string backupAnchorId = SpawnId.NewUlid(now);
        var backupAnchorSnapshot = new SaveSnapshotRecord
        {
            SnapshotId = backupAnchorId,
            GameId = ctx.GameId,
            BranchId = backupBranch.BranchId,
            ParentSnapshotId = localHead.SnapshotId,
            ProfileId = localHead.ProfileId,
            ModProfileHash = localHead.ModProfileHash,
            TriggerKind = SaveSnapshotTriggerKind.CloudDivergenceBackup,
            Note = $"云同步分叉保留的本机进度（原快照 {localHead.ShortId}）",
            FileCount = localHead.FileCount,
            RegistryKeyCount = localHead.RegistryKeyCount,
            TotalRawBytes = localHead.TotalRawBytes,
            TotalStoredBytes = 0,
            CreatedMachineId = ctx.Settings.MachineId,
            PlaytimeActiveSeconds = localHead.PlaytimeActiveSeconds,
            CreatedAtUtc = now,
            IsPinned = true,
        };
        SaveSnapshotFileRecord[] backupAnchorFiles = localFiles
            .Select(f => f with { SnapshotId = backupAnchorId })
            .ToArray();
        await _database.CreateSaveSnapshotWithFilesAsync(
            backupAnchorSnapshot,
            backupAnchorFiles,
            advanceBranchHead: true,
            updateGameActiveHead: false,
            cancellationToken: ct).ConfigureAwait(false);

        CloudSyncExecutionReport pullReport = await ExecuteFastForwardPullAsync(
            ctx,
            transport,
            targetBranchId: ctx.ActiveBranch.BranchId,
            createSafetySnapshot: false,
            progress,
            ct).ConfigureAwait(false);

        if (!pullReport.Succeeded)
        {
            return pullReport with
            {
                Relation = CloudSyncRelation.Diverged,
                DivergenceBackupBranchId = backupBranch.BranchId,
                DivergenceBackupBranchName = backupBranch.Name,
            };
        }

        return pullReport with
        {
            Relation = CloudSyncRelation.Diverged,
            Message = $"已采用远端 '{remoteHead.MachineName ?? remoteHead.MachineId}' 进度，本机原进度已完整保留至固定分支 '{backupBranch.Name}'。",
            DivergenceBackupBranchId = backupBranch.BranchId,
            DivergenceBackupBranchName = backupBranch.Name,
        };
    }

    private async Task<CloudSyncExecutionReport> ExecuteKeepLocalAndBackupRemoteAsync(
        PlanContext ctx,
        ICloudSyncTransport transport,
        IProgress<CloudSyncProgressTick>? progress,
        CancellationToken ct)
    {
        SaveSnapshotRecord localHead = ctx.LocalHeadSnapshot
            ?? throw new InvalidOperationException("Local head snapshot is required to resolve divergence.");
        CloudRemoteHeadSummary remoteHead = ctx.SelectedRemoteHead
            ?? throw new InvalidOperationException("Remote head summary is required to resolve divergence.");

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string safeRemoteMachine = CloudSyncTransportHelpers.SanitizePathSegment(
            string.IsNullOrWhiteSpace(remoteHead.MachineName) ? remoteHead.MachineId : remoteHead.MachineName);
        string backupBranchName = await GenerateUniqueBranchNameAsync(
            ctx.GameId,
            $"remote-{safeRemoteMachine}-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}",
            ct).ConfigureAwait(false);

        IReadOnlyList<SaveBranchRecord> existingBranches = await _database
            .GetSaveBranchesByGameAsync(ctx.GameId, ct)
            .ConfigureAwait(false);
        var remoteBackupBranch = new SaveBranchRecord
        {
            BranchId = SpawnId.NewUlid(now),
            GameId = ctx.GameId,
            Name = backupBranchName,
            Description = $"云同步分叉保留的远端进度（来源：{remoteHead.MachineName ?? remoteHead.MachineId}）",
            ColorHex = SaveBranchColors.PickForIndex(existingBranches.Count),
            BaseSnapshotId = ctx.CommonAncestorSnapshotId,
            HeadSnapshotId = null,
            IsDefault = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await _database.UpsertSaveBranchAsync(remoteBackupBranch, ct).ConfigureAwait(false);

        // Import the remote diverged snapshot into the dedicated preserved branch (without touching active branch or disk).
        (int downloadedChunks, long downloadedBytes, SaveSnapshotRecord importedRemoteTip) =
            await PullRemoteChainIntoBranchAsync(
                ctx.GameId,
                remoteHead.HeadSnapshotId,
                remoteBackupBranch.BranchId,
                advanceBranchHead: true,
                updateGameActiveHead: false,
                pinTip: true,
                transport,
                progress,
                ct).ConfigureAwait(false);

        // Create a merge-resolution snapshot on the active branch referencing BOTH localHead and importedRemoteTip
        // so both histories are preserved in the DAG and subsequent syncs fast-forward cleanly.
        IReadOnlyList<SaveSnapshotFileRecord> localFiles = await _database
            .GetSnapshotFilesAsync(localHead.SnapshotId, ct)
            .ConfigureAwait(false);
        DateTimeOffset mergeNow = _timeProvider.GetUtcNow();
        string mergeSnapshotId = SpawnId.NewUlid(mergeNow);
        var mergeSnapshot = new SaveSnapshotRecord
        {
            SnapshotId = mergeSnapshotId,
            GameId = ctx.GameId,
            BranchId = ctx.ActiveBranch.BranchId,
            ParentSnapshotId = localHead.SnapshotId,
            MergeParentSnapshotId = importedRemoteTip.SnapshotId,
            ProfileId = localHead.ProfileId,
            ModProfileHash = localHead.ModProfileHash,
            TriggerKind = SaveSnapshotTriggerKind.CloudDivergenceBackup,
            Note = $"保留本机进度并归档远端分支 ({remoteBackupBranch.Name})",
            FileCount = localHead.FileCount,
            RegistryKeyCount = localHead.RegistryKeyCount,
            TotalRawBytes = localHead.TotalRawBytes,
            TotalStoredBytes = 0, // Pure CAS deduplication reuse
            CreatedMachineId = ctx.Settings.MachineId,
            PlaytimeActiveSeconds = localHead.PlaytimeActiveSeconds,
            CreatedAtUtc = mergeNow,
            IsPinned = false,
        };

        SaveSnapshotFileRecord[] mergeFiles = localFiles
            .Select(f => f with { SnapshotId = mergeSnapshotId })
            .ToArray();

        await _database.CreateSaveSnapshotWithFilesAsync(
            mergeSnapshot,
            mergeFiles,
            advanceBranchHead: true,
            updateGameActiveHead: true,
            cancellationToken: ct).ConfigureAwait(false);

        PlanContext pushCtx = ctx with
        {
            LocalHeadSnapshot = mergeSnapshot,
            LocalAncestors = await GetLocalAncestorIdsBfsAsync(mergeSnapshotId, MaxLineageDepth, ct).ConfigureAwait(false),
        };

        CloudSyncExecutionReport pushReport = await ExecuteFastForwardPushAsync(pushCtx, transport, progress, ct)
            .ConfigureAwait(false);

        return pushReport with
        {
            Relation = CloudSyncRelation.Diverged,
            Message = $"已保留本机进度并推送至云端，远端进度已完整保留至固定分支 '{remoteBackupBranch.Name}'。",
            DownloadedChunkCount = downloadedChunks,
            DownloadedBytes = downloadedBytes,
            DivergenceBackupBranchId = remoteBackupBranch.BranchId,
            DivergenceBackupBranchName = remoteBackupBranch.Name,
        };
    }

    // -------------------------------------------------------------------------
    // SPPK Delta Pull & Range Extraction Helpers
    // -------------------------------------------------------------------------

    private async Task<(int DownloadedChunks, long DownloadedBytes, SaveSnapshotRecord TipSnapshot)>
        PullRemoteChainIntoBranchAsync(
            string gameId,
            string tipSnapshotId,
            string targetBranchId,
            bool advanceBranchHead,
            bool updateGameActiveHead,
            bool pinTip,
            ICloudSyncTransport transport,
            IProgress<CloudSyncProgressTick>? progress,
            CancellationToken ct)
    {
        // Walk remote snapshot manifests backwards until we hit a snapshot that already exists locally.
        var manifestsToImport = new List<RemoteSnapshotManifest>();
        string? cursor = tipSnapshotId;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (!string.IsNullOrWhiteSpace(cursor) && manifestsToImport.Count < MaxLineageDepth && visited.Add(cursor))
        {
            SaveSnapshotRecord? existingLocal = await _database.GetSaveSnapshotByIdAsync(cursor, ct).ConfigureAwait(false);
            if (existingLocal is not null)
            {
                break;
            }

            RemoteSnapshotManifest? manifest = await TryFetchRemoteSnapshotManifestAsync(gameId, cursor, transport, ct)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                if (cursor == tipSnapshotId)
                {
                    throw new InvalidDataException($"远端缺少目标快照清单 '{tipSnapshotId}'。");
                }

                break;
            }

            manifestsToImport.Add(manifest);
            cursor = manifest.ParentSnapshotId;
        }

        if (manifestsToImport.Count == 0)
        {
            SaveSnapshotRecord alreadyLocal = await _database.GetSaveSnapshotByIdAsync(tipSnapshotId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"快照 '{tipSnapshotId}' 不存在。");
            if (pinTip)
            {
                await _database.SetSaveSnapshotPinnedAsync(alreadyLocal.SnapshotId, isPinned: true, ct).ConfigureAwait(false);
            }

            if (advanceBranchHead)
            {
                await _database.UpdateSaveBranchHeadAsync(targetBranchId, alreadyLocal.SnapshotId, _timeProvider.GetUtcNow(), ct)
                    .ConfigureAwait(false);
            }

            if (updateGameActiveHead)
            {
                await _database.SetActiveSaveHeadAsync(gameId, targetBranchId, alreadyLocal.SnapshotId, _timeProvider.GetUtcNow(), ct)
                    .ConfigureAwait(false);
            }

            return (0, 0, alreadyLocal);
        }

        manifestsToImport.Reverse(); // oldest -> newest

        // Hold shared CAS write scope across download + SQLite snapshot commit so concurrent GC cannot delete newly fetched chunks.
        await using IAsyncDisposable casWriteScope = await _casChunkStore
            .BeginChunkWriteScopeAsync(ct)
            .ConfigureAwait(false);

        // Identify all missing chunks across the manifests (strictly excluding [config]) and group by remote SPPK packId.
        var missingChunksByPack = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var unmappedMissingChunks = new HashSet<string>(StringComparer.Ordinal);

        foreach (RemoteSnapshotManifest m in manifestsToImport)
        {
            foreach (RemoteSnapshotFileEntry file in m.Files.Where(static f => IsCloudSyncableFile(f.Category)))
            {
                foreach (string rawHash in file.ChunkHashes)
                {
                    string hash = rawHash.Trim().ToLowerInvariant();
                    if (_casChunkStore.ChunkFileExists(hash))
                    {
                        continue;
                    }

                    if (m.ChunkPackMap.TryGetValue(hash, out string? packId) && !string.IsNullOrWhiteSpace(packId))
                    {
                        if (!missingChunksByPack.TryGetValue(packId, out HashSet<string>? set))
                        {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            missingChunksByPack[packId] = set;
                        }

                        set.Add(hash);
                    }
                    else
                    {
                        unmappedMissingChunks.Add(hash);
                    }
                }
            }
        }

        // Fallback pack discovery for any chunk without an explicit ChunkPackMap entry.
        var footerCache = new Dictionary<string, SppkParsedFooter>(StringComparer.Ordinal);
        if (unmappedMissingChunks.Count > 0)
        {
            IReadOnlyList<CloudSyncObjectInfo> remotePacks = await transport.ListPrefixAsync("packs/", ct).ConfigureAwait(false);
            foreach (CloudSyncObjectInfo packObj in remotePacks)
            {
                if (unmappedMissingChunks.Count == 0
                    || !packObj.RelativePath.EndsWith(".sppk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string packId = Path.GetFileNameWithoutExtension(packObj.RelativePath);
                SppkParsedFooter footer = await ReadRemotePackFooterAsync(transport, packObj.RelativePath, ct)
                    .ConfigureAwait(false);
                footerCache[packId] = footer;

                foreach (SppkIndexEntry entry in footer.Entries)
                {
                    if (unmappedMissingChunks.Remove(entry.ChunkBlake3Hex))
                    {
                        if (!missingChunksByPack.TryGetValue(packId, out HashSet<string>? set))
                        {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            missingChunksByPack[packId] = set;
                        }

                        set.Add(entry.ChunkBlake3Hex);
                    }
                }
            }

            if (unmappedMissingChunks.Count > 0)
            {
                throw new InvalidDataException(
                    $"远端 SPPK 包索引中缺失 {unmappedMissingChunks.Count} 个数据块（如 '{unmappedMissingChunks.First()}'）。");
            }
        }

        // Download every missing chunk via HTTP/file Range requests & verify BLAKE3 directly into CasChunkStore.
        int totalMissing = missingChunksByPack.Values.Sum(static s => s.Count);
        int downloadedChunks = 0;
        long downloadedBytes = 0;

        foreach (var (packId, neededHashes) in missingChunksByPack)
        {
            ct.ThrowIfCancellationRequested();
            string packPath = $"packs/{packId}.sppk";
            if (!footerCache.TryGetValue(packId, out SppkParsedFooter? footer))
            {
                footer = await ReadRemotePackFooterAsync(transport, packPath, ct).ConfigureAwait(false);
                footerCache[packId] = footer;
            }

            var entryMap = footer.Entries.ToDictionary(static e => e.ChunkBlake3Hex, StringComparer.Ordinal);
            foreach (string hash in neededHashes)
            {
                ct.ThrowIfCancellationRequested();
                if (!entryMap.TryGetValue(hash, out SppkIndexEntry? entry))
                {
                    throw new InvalidDataException($"SPPK 数据包 '{packId}' Footer 中未找到块 '{hash}'。");
                }

                CloudSyncRangeResult? chunkRange = await transport
                    .GetRangeAsync(packPath, checked((long)entry.PackOffset), checked((int)entry.ZstdLength), ct)
                    .ConfigureAwait(false);
                if (chunkRange is null || chunkRange.Data.Length != entry.ZstdLength)
                {
                    throw new InvalidDataException(
                        $"从 SPPK '{packId}' 拉取块 '{hash}' 字节范围失败（预期 {entry.ZstdLength} 字节，实际 {chunkRange?.Data.Length ?? 0} 字节）。");
                }

                await _casChunkStore.StoreVerifiedCompressedChunkAsync(
                    entry.ChunkBlake3Hex,
                    chunkRange.Data,
                    packId: packId,
                    cancellationToken: ct).ConfigureAwait(false);

                downloadedChunks++;
                downloadedBytes += chunkRange.Data.Length;
                progress?.Report(new CloudSyncProgressTick(
                    "DownloadChunks",
                    downloadedChunks,
                    Math.Max(1, totalMissing),
                    downloadedBytes,
                    $"正在按范围拉取并校验存档块 ({downloadedChunks}/{totalMissing})..."));
            }
        }

        // Commit imported snapshots into local SQLite oldest -> newest.
        SaveSnapshotRecord? lastImported = null;
        for (int i = 0; i < manifestsToImport.Count; i++)
        {
            RemoteSnapshotManifest m = manifestsToImport[i];
            bool isTip = i == manifestsToImport.Count - 1;

            // Ensure FK integrity: parent_snapshot_id & merge_parent_snapshot_id must exist locally or be null.
            string? resolvedParentId = !string.IsNullOrWhiteSpace(m.ParentSnapshotId)
                && await _database.GetSaveSnapshotByIdAsync(m.ParentSnapshotId, ct).ConfigureAwait(false) is not null
                    ? m.ParentSnapshotId
                    : null;
            string? resolvedMergeParentId = !string.IsNullOrWhiteSpace(m.MergeParentSnapshotId)
                && await _database.GetSaveSnapshotByIdAsync(m.MergeParentSnapshotId, ct).ConfigureAwait(false) is not null
                    ? m.MergeParentSnapshotId
                    : null;

            SaveSnapshotFileRecord[] fileRecords = m.Files
                .Where(static f => IsCloudSyncableFile(f.Category))
                .Select(f => new SaveSnapshotFileRecord
                {
                    SnapshotId = m.SnapshotId,
                    LogicalPath = f.LogicalPath,
                    OriginalPath = f.OriginalPath,
                    Category = f.Category,
                    FileSize = f.FileSize,
                    LastWriteTimeUtc = f.LastWriteTimeUtc,
                    FileBlake3 = f.FileBlake3,
                    ChunkHashes = f.ChunkHashes,
                }).ToArray();

            var snapRecord = new SaveSnapshotRecord
            {
                SnapshotId = m.SnapshotId,
                GameId = gameId,
                BranchId = targetBranchId,
                ParentSnapshotId = resolvedParentId,
                MergeParentSnapshotId = resolvedMergeParentId,
                ProfileId = m.ProfileId,
                ModProfileHash = m.ModProfileHash ?? string.Empty,
                TriggerKind = isTip ? SaveSnapshotTriggerKind.CloudPull : m.TriggerKind,
                Note = string.IsNullOrWhiteSpace(m.Note)
                    ? $"来自远端设备 '{m.CreatedMachineName ?? m.CreatedMachineId}'"
                    : m.Note,
                FileCount = fileRecords.Count(static f => f.Category == SaveFileCategory.Save),
                RegistryKeyCount = fileRecords.Any(static f => f.Category == SaveFileCategory.RegistryExport)
                    ? Math.Max(1, m.RegistryKeyCount)
                    : 0,
                TotalRawBytes = fileRecords.Sum(static f => Math.Max(0, f.FileSize)),
                TotalStoredBytes = m.TotalStoredBytes,
                CreatedMachineId = m.CreatedMachineId,
                PlaytimeActiveSeconds = m.PlaytimeActiveSeconds,
                CreatedAtUtc = m.CreatedAtUtc,
                IsPinned = (isTip && pinTip) || m.IsPinned,
            };

            await _database.CreateSaveSnapshotWithFilesAsync(
                snapRecord,
                fileRecords,
                advanceBranchHead: isTip && advanceBranchHead,
                updateGameActiveHead: isTip && updateGameActiveHead,
                cancellationToken: ct).ConfigureAwait(false);

            lastImported = snapRecord;
        }

        return (downloadedChunks, downloadedBytes, lastImported!);
    }

    private static async Task<SppkParsedFooter> ReadRemotePackFooterAsync(
        ICloudSyncTransport transport,
        string packRelativePath,
        CancellationToken ct)
    {
        CloudSyncRangeResult? initialTail = await transport
            .GetRangeAsync(packRelativePath, offset: -1, length: SppkPackfileFormat.DefaultTailReadBytes, ct)
            .ConfigureAwait(false);
        if (initialTail is null || initialTail.Data.Length == 0)
        {
            throw new InvalidDataException($"无法读取远端 SPPK 数据包尾部索引：'{packRelativePath}'。");
        }

        if (SppkPackfileFormat.TryParseTailFooter(
            initialTail.Data,
            initialTail.TotalLength,
            out SppkParsedFooter? parsed,
            out int requiredTailBytes)
            && parsed is not null)
        {
            return parsed;
        }

        CloudSyncRangeResult? fullTail = await transport
            .GetRangeAsync(packRelativePath, offset: -1, length: requiredTailBytes, ct)
            .ConfigureAwait(false);
        if (fullTail is null || fullTail.Data.Length < requiredTailBytes)
        {
            throw new InvalidDataException(
                $"无法完整读取远端 SPPK 数据包 Footer：'{packRelativePath}'（需要 {requiredTailBytes} 字节）。");
        }

        return SppkPackfileFormat.ParseTailFooter(fullTail.Data, fullTail.TotalLength);
    }

    private static bool IsCloudSyncableFile(SaveFileCategory category) =>
        category != SaveFileCategory.Config;

    private async Task<(IReadOnlyList<SppkChunkInput> MissingChunks, Dictionary<string, string> ExistingPackMap, HashSet<string> RemotePackIds, int SyncableFileCount)>
        ComputeMissingRemoteChunksAsync(
            string gameId,
            SaveSnapshotRecord localHead,
            ICloudSyncTransport transport,
            CancellationToken ct)
    {
        IReadOnlyList<CloudSyncObjectInfo> remotePacks = await transport.ListPrefixAsync("packs/", ct).ConfigureAwait(false);
        var remotePackIds = new HashSet<string>(
            remotePacks
                .Select(static p => Path.GetFileNameWithoutExtension(p.RelativePath))
                .Where(static id => !string.IsNullOrWhiteSpace(id))!,
            StringComparer.Ordinal);

        IReadOnlyList<SaveSnapshotFileRecord> allFiles = await _database
            .GetSnapshotFilesAsync(localHead.SnapshotId, ct)
            .ConfigureAwait(false);
        SaveSnapshotFileRecord[] files = allFiles
            .Where(static f => IsCloudSyncableFile(f.Category))
            .ToArray();
        string[] allHashes = files
            .SelectMany(static f => f.ChunkHashes)
            .Select(static h => h.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        IReadOnlyDictionary<string, CasChunkRecord> chunkRecords = await _database
            .GetCasChunksByHashesAsync(allHashes, ct)
            .ConfigureAwait(false);

        var existingPackMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<SppkChunkInput>();

        foreach (string hash in allHashes)
        {
            if (chunkRecords.TryGetValue(hash, out CasChunkRecord? rec)
                && !string.IsNullOrWhiteSpace(rec.PackId)
                && remotePackIds.Contains(rec.PackId))
            {
                existingPackMap[hash] = rec.PackId;
                continue;
            }

            if (_casChunkStore.ChunkFileExists(hash))
            {
                byte[] compressed = await _casChunkStore.ReadChunkCompressedAsync(hash, ct).ConfigureAwait(false);
                uint rawLen = chunkRecords.TryGetValue(hash, out CasChunkRecord? meta)
                    ? checked((uint)Math.Max(0, meta.RawSizeBytes))
                    : 0;
                missing.Add(new SppkChunkInput(hash, compressed, rawLen));
            }
        }

        return (missing, existingPackMap, remotePackIds, files.Length);
    }

    private async Task<byte[]> PublishMachineHeadAsync(
        string gameId,
        SaveSettingsDto settings,
        SaveSnapshotRecord headSnapshot,
        string branchName,
        IReadOnlyList<string> ancestorIds,
        ICloudSyncTransport transport,
        CancellationToken ct)
    {
        string safeGameId = CloudSyncTransportHelpers.SanitizePathSegment(gameId);
        string safeMachineId = CloudSyncTransportHelpers.SanitizePathSegment(settings.MachineId);

        var headDoc = new RemoteMachineHeadDocument
        {
            FormatVersion = 1,
            GameId = gameId,
            MachineId = settings.MachineId,
            MachineName = settings.MachineDisplayName,
            HeadSnapshotId = headSnapshot.SnapshotId,
            BranchName = branchName,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            PlaytimeActiveSeconds = headSnapshot.PlaytimeActiveSeconds,
            FileCount = headSnapshot.FileCount,
            TotalRawBytes = headSnapshot.TotalRawBytes,
            AncestorIds = ancestorIds,
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(headDoc, JsonOptions);
        await transport.PutBytesAsync(
            $"heads/{safeGameId}/{safeMachineId}.json",
            bytes,
            "application/json",
            ct).ConfigureAwait(false);
        return bytes;
    }

    private static async Task<RemoteSnapshotManifest?> TryFetchRemoteSnapshotManifestAsync(
        string gameId,
        string snapshotId,
        ICloudSyncTransport transport,
        CancellationToken ct)
    {
        string safeGameId = CloudSyncTransportHelpers.SanitizePathSegment(gameId);
        byte[]? raw = await transport
            .GetBytesAsync($"snapshots/{safeGameId}/{snapshotId}.json", ct)
            .ConfigureAwait(false);
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<RemoteSnapshotManifest>(raw, JsonOptions);
    }

    /// <summary>
    /// Walks both <c>parent_snapshot_id</c> and <c>merge_parent_snapshot_id</c> in breadth-first order up to
    /// <paramref name="maxDepth"/> generations (default 64) with cycle protection.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetLocalAncestorIdsBfsAsync(
        string startSnapshotId,
        int maxDepth,
        CancellationToken ct)
    {
        var result = new List<string>(Math.Min(maxDepth, MaxLineageDepth));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(startSnapshotId);
        visited.Add(startSnapshotId);

        while (queue.Count > 0 && result.Count < maxDepth)
        {
            string current = queue.Dequeue();
            SaveSnapshotRecord? snap = await _database.GetSaveSnapshotByIdAsync(current, ct).ConfigureAwait(false);
            if (snap is null)
            {
                continue;
            }

            result.Add(snap.SnapshotId);
            if (!string.IsNullOrWhiteSpace(snap.ParentSnapshotId) && visited.Add(snap.ParentSnapshotId))
            {
                queue.Enqueue(snap.ParentSnapshotId);
            }

            if (!string.IsNullOrWhiteSpace(snap.MergeParentSnapshotId) && visited.Add(snap.MergeParentSnapshotId))
            {
                queue.Enqueue(snap.MergeParentSnapshotId);
            }
        }

        return result;
    }

    private async Task<CloudSyncEndpointRecord?> ResolveEndpointAsync(
        SaveGameStateRecord state,
        string? explicitEndpointId,
        CancellationToken ct)
    {
        string? targetId = !string.IsNullOrWhiteSpace(explicitEndpointId)
            ? explicitEndpointId.Trim()
            : state.CloudEndpointId;

        if (!string.IsNullOrWhiteSpace(targetId))
        {
            CloudSyncEndpointRecord? byId = await _database
                .GetCloudSyncEndpointByIdAsync(targetId, ct)
                .ConfigureAwait(false);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await _database.GetDefaultCloudSyncEndpointAsync(ct).ConfigureAwait(false);
    }

    private async Task<SaveBranchRecord> ResolveActiveBranchAsync(
        string gameId,
        SaveGameStateRecord state,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(state.ActiveBranchId))
        {
            SaveBranchRecord? active = await _database
                .GetSaveBranchByIdAsync(state.ActiveBranchId, ct)
                .ConfigureAwait(false);
            if (active is not null)
            {
                return active;
            }
        }

        return await _database.EnsureDefaultSaveBranchAsync(gameId, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
    }

    private async Task<string> GenerateUniqueBranchNameAsync(string gameId, string baseName, CancellationToken ct)
    {
        string candidate = baseName;
        for (int suffix = 2; suffix < 1000; suffix++)
        {
            SaveBranchRecord? existing = await _database.GetSaveBranchByNameAsync(gameId, candidate, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return candidate;
            }

            candidate = $"{baseName}-{suffix}";
        }

        return $"{baseName}-{SpawnId.NewUlid(_timeProvider.GetUtcNow())[^6..]}";
    }

    private ICloudSyncTransport CreateTransport(CloudSyncEndpointRecord endpoint)
    {
        CloudSyncEndpointSecrets? secrets = ReadSecrets(endpoint.SecretCredentialId);
        if (_customTransportFactory is not null)
        {
            return _customTransportFactory(endpoint, secrets);
        }

        return endpoint.ProviderType switch
        {
            CloudSyncProviderType.LocalFolder => new LocalFolderSyncTransport(
                endpoint.EndpointUrl,
                endpoint.BucketOrRoot,
                _timeProvider),
            CloudSyncProviderType.WebDav => new WebDavSyncTransport(
                endpoint.EndpointUrl,
                endpoint.BucketOrRoot,
                secrets,
                _httpClientFactory?.CreateClient(CloudSyncHttpClientName),
                _timeProvider),
            CloudSyncProviderType.S3 => new S3SyncTransport(
                endpoint.EndpointUrl,
                endpoint.BucketOrRoot,
                endpoint.Region,
                endpoint.PathStyleAddressing,
                secrets,
                _httpClientFactory?.CreateClient(CloudSyncHttpClientName),
                _timeProvider),
            _ => throw new NotSupportedException($"Unsupported cloud sync provider: {endpoint.ProviderType}."),
        };
    }

    private CloudSyncEndpointSecrets? ReadSecrets(string? credentialId)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            return null;
        }

        string? json = _secretStore.TryReadSecret(credentialId);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CloudSyncEndpointSecrets>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task PublishTreeChangedAsync(
        string gameId,
        string? branchId,
        string? snapshotId,
        string reason,
        CancellationToken ct)
    {
        if (_eventBus is not null)
        {
            await _eventBus.PublishAsync(
                new SaveTreeChangedEvent(gameId, branchId, snapshotId, reason, _timeProvider.GetUtcNow()),
                ct).ConfigureAwait(false);
        }
    }

    private sealed record PlanContext(
        string GameId,
        SaveSettingsDto Settings,
        SaveGameStateRecord GameState,
        SaveBranchRecord ActiveBranch,
        CloudSyncEndpointRecord Endpoint,
        SaveSnapshotRecord? LocalHeadSnapshot,
        IReadOnlyList<string> LocalAncestors,
        CloudRemoteHeadSummary? SelectedRemoteHead,
        string? CommonAncestorSnapshotId,
        CloudSyncPlanPreview Preview);

    private sealed record RemoteMachineHeadDocument
    {
        public int FormatVersion { get; init; } = 1;
        public required string GameId { get; init; }
        public required string MachineId { get; init; }
        public string? MachineName { get; init; }
        public required string HeadSnapshotId { get; init; }
        public string? BranchName { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; init; }
        public long PlaytimeActiveSeconds { get; init; }
        public int FileCount { get; init; }
        public long TotalRawBytes { get; init; }
        public IReadOnlyList<string> AncestorIds { get; init; } = [];
    }

    private sealed record RemoteSnapshotManifest
    {
        public int FormatVersion { get; init; } = 1;
        public required string SnapshotId { get; init; }
        public required string GameId { get; init; }
        public string? ParentSnapshotId { get; init; }
        public string? MergeParentSnapshotId { get; init; }
        public string? BranchName { get; init; }
        public string? ProfileId { get; init; }
        public string ModProfileHash { get; init; } = string.Empty;
        public SaveSnapshotTriggerKind TriggerKind { get; init; }
        public string? Note { get; init; }
        public int FileCount { get; init; }
        public int RegistryKeyCount { get; init; }
        public long TotalRawBytes { get; init; }
        public long TotalStoredBytes { get; init; }
        public required string CreatedMachineId { get; init; }
        public string? CreatedMachineName { get; init; }
        public long PlaytimeActiveSeconds { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public bool IsPinned { get; init; }
        public RemoteSnapshotFileEntry[] Files { get; init; } = [];
        public Dictionary<string, string> ChunkPackMap { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record RemoteSnapshotFileEntry
    {
        public required string LogicalPath { get; init; }
        public required string OriginalPath { get; init; }
        public SaveFileCategory Category { get; init; } = SaveFileCategory.Save;
        public long FileSize { get; init; }
        public DateTimeOffset LastWriteTimeUtc { get; init; }
        public required string FileBlake3 { get; init; }
        public IReadOnlyList<string> ChunkHashes { get; init; } = [];
    }
}
