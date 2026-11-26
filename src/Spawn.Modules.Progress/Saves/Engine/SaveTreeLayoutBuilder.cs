using Spawn.Contracts;

namespace Spawn.Modules.Progress.Saves.Engine;

/// <summary>
/// Deterministic DAG layout builder transforming persisted <see cref="SaveBranchRecord"/> and
/// <see cref="SaveSnapshotRecord"/> rows into a lane-assigned <see cref="SaveTreeGraphDto"/>
/// (newest at row 0, active branch pinned to lane 0, secondary branches recycled on lowest free lane).
/// </summary>
public static class SaveTreeLayoutBuilder
{
    public static SaveTreeGraphDto BuildGraph(
        string gameId,
        SaveGameStateRecord? state,
        IReadOnlyList<SaveBranchRecord> branches,
        IReadOnlyList<SaveSnapshotRecord> snapshots,
        long totalDeduplicatedBytes) =>
        BuildGraph(gameId, state, branches, snapshots, totalDeduplicatedBytes, syncedSnapshotIds: null);

    public static SaveTreeGraphDto BuildGraph(
        string gameId,
        SaveGameStateRecord? state,
        IReadOnlyList<SaveBranchRecord> branches,
        IReadOnlyList<SaveSnapshotRecord> snapshots,
        long totalDeduplicatedBytes,
        IReadOnlySet<string>? syncedSnapshotIds,
        string? effectiveCloudGameKey = null,
        int unimportedLegacyPreTrainerCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(snapshots);

        SaveBranchRecord? activeBranch = ResolveActiveBranch(state, branches);
        string? activeBranchId = activeBranch?.BranchId ?? state?.ActiveBranchId;
        string activeBranchName = activeBranch?.Name ?? "main";
        string? currentHeadId = state?.CurrentHeadSnapshotId ?? activeBranch?.HeadSnapshotId;
        bool isDetachedHead = !string.IsNullOrWhiteSpace(currentHeadId)
                              && activeBranch is not null
                              && !string.Equals(activeBranch.HeadSnapshotId, currentHeadId, StringComparison.Ordinal);

        if (snapshots.Count == 0)
        {
            return new SaveTreeGraphDto
            {
                GameId = gameId,
                ActiveBranchId = activeBranchId,
                ActiveBranchName = activeBranchName,
                CurrentHeadSnapshotId = currentHeadId,
                IsDetachedHead = false,
                Branches = branches,
                Nodes = [],
                Edges = [],
                TotalRawBytes = 0,
                TotalDeduplicatedBytes = Math.Max(0, totalDeduplicatedBytes),
                HasUnresolvedCloudConflict = state?.HasUnresolvedCloudConflict ?? false,
                LastSyncedAtUtc = state?.LastSyncedAtUtc,
                EffectiveCloudGameKey = effectiveCloudGameKey,
                UnimportedLegacyPreTrainerCount = Math.Max(0, unimportedLegacyPreTrainerCount),
            };
        }

        var branchById = branches.ToDictionary(b => b.BranchId, StringComparer.Ordinal);
        var snapById = snapshots.ToDictionary(s => s.SnapshotId, StringComparer.Ordinal);

        // Map snapshotId -> list of branch names whose tip points to this snapshot.
        var tipBranchesBySnap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (SaveBranchRecord b in branches)
        {
            if (!string.IsNullOrWhiteSpace(b.HeadSnapshotId) && snapById.ContainsKey(b.HeadSnapshotId))
            {
                if (!tipBranchesBySnap.TryGetValue(b.HeadSnapshotId, out List<string>? names))
                {
                    names = [];
                    tipBranchesBySnap[b.HeadSnapshotId] = names;
                }

                names.Add(b.Name);
            }
        }

        // Build child lists (parent -> children) for both primary parent and merge parent.
        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (SaveSnapshotRecord s in snapshots)
        {
            RegisterChild(childrenByParent, snapById, s.ParentSnapshotId, s.SnapshotId);
            RegisterChild(childrenByParent, snapById, s.MergeParentSnapshotId, s.SnapshotId);
        }

        // 1. Reverse-chronological topological order (children strictly above their parents).
        List<SaveSnapshotRecord> ordered = BuildReverseTopologicalOrder(snapshots, snapById, childrenByParent);

        // 2. Identify active branch mainline trunk so it stays pinned to lane 0.
        var activeMainlineIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(activeBranch?.HeadSnapshotId))
        {
            string? cursor = activeBranch.HeadSnapshotId;
            while (!string.IsNullOrWhiteSpace(cursor)
                   && snapById.TryGetValue(cursor, out SaveSnapshotRecord? cur)
                   && activeMainlineIds.Add(cursor))
            {
                cursor = cur.ParentSnapshotId;
            }
        }

        // 3. Deterministic lane assignment with recycling once a forked branch meets its fork ancestor.
        //    activeLanes[lane] = expected next snapshotId moving downward along that lane (null = free).
        var activeLanes = new List<string?> { null }; // Reserve index 0 for active mainline.
        var laneBySnap = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);
        var rowBySnap = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);

        for (int row = 0; row < ordered.Count; row++)
        {
            SaveSnapshotRecord snap = ordered[row];
            rowBySnap[snap.SnapshotId] = row;

            // Find all lanes currently expecting this snapshot (multiple lanes converge at a fork point).
            var matchingLanes = new List<int>();
            for (int lane = 0; lane < activeLanes.Count; lane++)
            {
                if (string.Equals(activeLanes[lane], snap.SnapshotId, StringComparison.Ordinal))
                {
                    matchingLanes.Add(lane);
                }
            }

            int assignedLane;
            if (activeMainlineIds.Contains(snap.SnapshotId))
            {
                assignedLane = 0;
                foreach (int lane in matchingLanes)
                {
                    if (lane != 0)
                    {
                        activeLanes[lane] = null;
                    }
                }
            }
            else if (matchingLanes.Count > 0)
            {
                assignedLane = matchingLanes[0];
                for (int i = 1; i < matchingLanes.Count; i++)
                {
                    activeLanes[matchingLanes[i]] = null;
                }
            }
            else
            {
                // Branch tip or detached root starting a new visual lane.
                assignedLane = AllocateLowestFreeLane(activeLanes, allowLaneZero: activeMainlineIds.Count == 0);
            }

            laneBySnap[snap.SnapshotId] = assignedLane;

            // Advance assignedLane to primary parent if parent exists and is not already claimed by a lower lane.
            string? parentId = !string.IsNullOrWhiteSpace(snap.ParentSnapshotId) && snapById.ContainsKey(snap.ParentSnapshotId)
                ? snap.ParentSnapshotId
                : null;

            if (parentId is null)
            {
                activeLanes[assignedLane] = null;
            }
            else
            {
                int existingParentLane = FindLaneExpecting(activeLanes, parentId);
                if (existingParentLane >= 0 && existingParentLane != assignedLane)
                {
                    // Another lane already routes to this parent; keep the lower lane and recycle the higher one.
                    if (assignedLane < existingParentLane)
                    {
                        activeLanes[existingParentLane] = null;
                        activeLanes[assignedLane] = parentId;
                    }
                    else
                    {
                        activeLanes[assignedLane] = null;
                    }
                }
                else
                {
                    activeLanes[assignedLane] = parentId;
                }
            }

            // If this snapshot has a merge parent, ensure a lane routes to it if not already tracked.
            if (!string.IsNullOrWhiteSpace(snap.MergeParentSnapshotId)
                && snapById.ContainsKey(snap.MergeParentSnapshotId)
                && FindLaneExpecting(activeLanes, snap.MergeParentSnapshotId) < 0)
            {
                int mergeLane = activeMainlineIds.Contains(snap.MergeParentSnapshotId) && activeLanes[0] is null
                    ? 0
                    : AllocateLowestFreeLane(activeLanes, allowLaneZero: false);
                activeLanes[mergeLane] = snap.MergeParentSnapshotId;
            }
        }

        // 4. Materialize positioned nodes and directed edges.
        var nodes = new List<SaveTreeNodeDto>(ordered.Count);
        var edges = new List<SaveTreeEdgeDto>(ordered.Count);
        long totalRawBytes = 0;

        for (int row = 0; row < ordered.Count; row++)
        {
            SaveSnapshotRecord snap = ordered[row];
            totalRawBytes += Math.Max(0, snap.TotalRawBytes);

            branchById.TryGetValue(snap.BranchId, out SaveBranchRecord? branch);
            string bName = branch?.Name ?? "main";
            string bColor = !string.IsNullOrWhiteSpace(branch?.ColorHex)
                ? branch.ColorHex
                : SaveBranchColors.PickForIndex(laneBySnap[snap.SnapshotId]);

            bool isHead = string.Equals(snap.SnapshotId, currentHeadId, StringComparison.Ordinal);
            List<string> tipNames = tipBranchesBySnap.TryGetValue(snap.SnapshotId, out List<string>? tips)
                ? tips
                : [];
            List<string> childIds = childrenByParent.TryGetValue(snap.SnapshotId, out List<string>? kids)
                ? kids
                : [];
            bool isCloudSynced = syncedSnapshotIds is not null
                && syncedSnapshotIds.Contains(snap.SnapshotId);

            int fromLane = laneBySnap[snap.SnapshotId];
            nodes.Add(new SaveTreeNodeDto
            {
                Snapshot = snap,
                BranchName = bName,
                BranchColorHex = bColor,
                LaneIndex = fromLane,
                RowIndex = row,
                ParentSnapshotId = snap.ParentSnapshotId,
                MergeParentSnapshotId = snap.MergeParentSnapshotId,
                ChildSnapshotIds = childIds,
                IsCurrentHead = isHead,
                IsBranchTip = tipNames.Count > 0,
                BranchTipNames = tipNames,
                IsDetachedHead = isHead && isDetachedHead,
                IsCloudSynced = isCloudSynced,
            });

            if (!string.IsNullOrWhiteSpace(snap.ParentSnapshotId)
                && rowBySnap.TryGetValue(snap.ParentSnapshotId, out int parentRow))
            {
                int toLane = laneBySnap[snap.ParentSnapshotId];
                edges.Add(new SaveTreeEdgeDto
                {
                    FromSnapshotId = snap.SnapshotId,
                    ToSnapshotId = snap.ParentSnapshotId,
                    FromLane = fromLane,
                    ToLane = toLane,
                    FromRow = row,
                    ToRow = parentRow,
                    ColorHex = bColor,
                    IsForkEdge = fromLane != toLane,
                    IsMergeEdge = false,
                });
            }

            if (!string.IsNullOrWhiteSpace(snap.MergeParentSnapshotId)
                && rowBySnap.TryGetValue(snap.MergeParentSnapshotId, out int mergeRow))
            {
                int toLane = laneBySnap[snap.MergeParentSnapshotId];
                string mergeColor = snapById.TryGetValue(snap.MergeParentSnapshotId, out SaveSnapshotRecord? mp)
                                    && branchById.TryGetValue(mp.BranchId, out SaveBranchRecord? mBranch)
                                    && !string.IsNullOrWhiteSpace(mBranch.ColorHex)
                    ? mBranch.ColorHex
                    : bColor;

                edges.Add(new SaveTreeEdgeDto
                {
                    FromSnapshotId = snap.SnapshotId,
                    ToSnapshotId = snap.MergeParentSnapshotId,
                    FromLane = fromLane,
                    ToLane = toLane,
                    FromRow = row,
                    ToRow = mergeRow,
                    ColorHex = mergeColor,
                    IsForkEdge = false,
                    IsMergeEdge = true,
                });
            }
        }

        return new SaveTreeGraphDto
        {
            GameId = gameId,
            ActiveBranchId = activeBranchId,
            ActiveBranchName = activeBranchName,
            CurrentHeadSnapshotId = currentHeadId,
            IsDetachedHead = isDetachedHead,
            Branches = branches,
            Nodes = nodes,
            Edges = edges,
            TotalRawBytes = totalRawBytes,
            TotalDeduplicatedBytes = Math.Max(0, totalDeduplicatedBytes),
            HasUnresolvedCloudConflict = state?.HasUnresolvedCloudConflict ?? false,
            LastSyncedAtUtc = state?.LastSyncedAtUtc,
            EffectiveCloudGameKey = effectiveCloudGameKey,
            UnimportedLegacyPreTrainerCount = Math.Max(0, unimportedLegacyPreTrainerCount),
        };
    }

    private static SaveBranchRecord? ResolveActiveBranch(
        SaveGameStateRecord? state,
        IReadOnlyList<SaveBranchRecord> branches)
    {
        if (!string.IsNullOrWhiteSpace(state?.ActiveBranchId))
        {
            SaveBranchRecord? match = branches.FirstOrDefault(
                b => string.Equals(b.BranchId, state.ActiveBranchId, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return branches.FirstOrDefault(static b => b.IsDefault)
               ?? (branches.Count > 0 ? branches[0] : null);
    }

    private static void RegisterChild(
        Dictionary<string, List<string>> childrenByParent,
        Dictionary<string, SaveSnapshotRecord> snapById,
        string? parentId,
        string childId)
    {
        if (string.IsNullOrWhiteSpace(parentId)
            || string.Equals(parentId, childId, StringComparison.Ordinal)
            || !snapById.ContainsKey(parentId))
        {
            return;
        }

        if (!childrenByParent.TryGetValue(parentId, out List<string>? kids))
        {
            kids = [];
            childrenByParent[parentId] = kids;
        }

        if (!kids.Contains(childId, StringComparer.Ordinal))
        {
            kids.Add(childId);
        }
    }

    private static List<SaveSnapshotRecord> BuildReverseTopologicalOrder(
        IReadOnlyList<SaveSnapshotRecord> snapshots,
        Dictionary<string, SaveSnapshotRecord> snapById,
        Dictionary<string, List<string>> childrenByParent)
    {
        // In-degree here = number of unplaced children (leaves/tips have 0 children and go to top rows first).
        var remainingChildren = new Dictionary<string, int>(snapshots.Count, StringComparer.Ordinal);
        foreach (SaveSnapshotRecord s in snapshots)
        {
            remainingChildren[s.SnapshotId] = childrenByParent.TryGetValue(s.SnapshotId, out List<string>? kids)
                ? kids.Count
                : 0;
        }

        var ready = new PriorityQueue<SaveSnapshotRecord, (long NegTicks, string InvId)>();
        foreach (SaveSnapshotRecord s in snapshots)
        {
            if (remainingChildren[s.SnapshotId] == 0)
            {
                ready.Enqueue(s, (-s.CreatedAtUtc.UtcTicks, s.SnapshotId));
            }
        }

        var result = new List<SaveSnapshotRecord>(snapshots.Count);
        var placed = new HashSet<string>(snapshots.Count, StringComparer.Ordinal);

        while (ready.Count > 0)
        {
            SaveSnapshotRecord current = ready.Dequeue();
            if (!placed.Add(current.SnapshotId))
            {
                continue;
            }

            result.Add(current);

            DecrementParent(current.ParentSnapshotId, snapById, remainingChildren, placed, ready);
            if (!string.Equals(current.MergeParentSnapshotId, current.ParentSnapshotId, StringComparison.Ordinal))
            {
                DecrementParent(current.MergeParentSnapshotId, snapById, remainingChildren, placed, ready);
            }
        }

        // Cycle fallback (defensive): append any remaining unplaced nodes newest-first.
        if (result.Count < snapshots.Count)
        {
            foreach (SaveSnapshotRecord s in snapshots
                         .Where(s => !placed.Contains(s.SnapshotId))
                         .OrderByDescending(s => s.CreatedAtUtc)
                         .ThenByDescending(s => s.SnapshotId, StringComparer.Ordinal))
            {
                result.Add(s);
            }
        }

        return result;
    }

    private static void DecrementParent(
        string? parentId,
        Dictionary<string, SaveSnapshotRecord> snapById,
        Dictionary<string, int> remainingChildren,
        HashSet<string> placed,
        PriorityQueue<SaveSnapshotRecord, (long NegTicks, string InvId)> ready)
    {
        if (string.IsNullOrWhiteSpace(parentId)
            || placed.Contains(parentId)
            || !snapById.TryGetValue(parentId, out SaveSnapshotRecord? parent))
        {
            return;
        }

        int next = Math.Max(0, remainingChildren[parentId] - 1);
        remainingChildren[parentId] = next;
        if (next == 0)
        {
            ready.Enqueue(parent, (-parent.CreatedAtUtc.UtcTicks, parent.SnapshotId));
        }
    }

    private static int FindLaneExpecting(List<string?> activeLanes, string snapshotId)
    {
        for (int i = 0; i < activeLanes.Count; i++)
        {
            if (string.Equals(activeLanes[i], snapshotId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int AllocateLowestFreeLane(List<string?> activeLanes, bool allowLaneZero)
    {
        int start = allowLaneZero ? 0 : 1;
        for (int i = start; i < activeLanes.Count; i++)
        {
            if (activeLanes[i] is null)
            {
                return i;
            }
        }

        activeLanes.Add(null);
        return activeLanes.Count - 1;
    }
}
