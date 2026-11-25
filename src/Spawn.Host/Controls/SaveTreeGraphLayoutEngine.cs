using Spawn.Contracts;

namespace Spawn.Host.Controls;

/// <summary>
/// Filter applied to the branching save tree viewport.
/// </summary>
public enum SaveTreeFilterMode
{
    All = 0,
    CurrentBranch = 1,
    PinnedOnly = 2,
}

/// <summary>
/// Straight vertical pass-through lane rail drawn from top (y = 0) to bottom (y = RowHeight) of a row.
/// </summary>
public sealed record SaveGraphRailSegment(
    int Lane,
    int ColorIndex,
    string ColorHex);

/// <summary>
/// Half-height connector segment inside a row: either incoming (y = 0 -> MidY) or outgoing (y = MidY -> RowHeight).
/// When <see cref="FromLane"/> != <see cref="ToLane"/>, rendered as a smooth cubic Bezier fork/merge curve.
/// </summary>
public sealed record SaveGraphCurveSegment(
    int FromLane,
    int ToLane,
    int ColorIndex,
    string ColorHex,
    bool IsFork,
    bool IsMerge);

/// <summary>
/// Precomputed immutable graph geometry for a single virtualized row in the Branching Save Tree.
/// </summary>
public sealed record SaveGraphRowLayout
{
    public required SaveTreeNodeDto Node { get; init; }
    public required int RowIndex { get; init; }
    public required int NodeLane { get; init; }
    public required int ColorIndex { get; init; }
    public required string BranchColorHex { get; init; }
    public required int TotalLaneColumns { get; init; }
    public IReadOnlyList<SaveGraphRailSegment> PassThroughRails { get; init; } = [];
    public IReadOnlyList<SaveGraphCurveSegment> IncomingCurves { get; init; } = [];
    public IReadOnlyList<SaveGraphCurveSegment> OutgoingCurves { get; init; } = [];
    public bool IsHead { get; init; }
    public bool IsDetachedHead { get; init; }
    public bool IsBranchTip { get; init; }
    public bool IsPinned { get; init; }
    public bool IsSafetyNode { get; init; }
    public bool IsRoot { get; init; }
    public bool HasChildren { get; init; }
}

/// <summary>
/// Complete deterministic layout computation result for a game's branching save tree.
/// </summary>
public sealed record SaveGraphLayoutResult
{
    public IReadOnlyList<SaveGraphRowLayout> Rows { get; init; } = [];
    public int MaxLaneCount { get; init; }
    public double RecommendedGraphWidth { get; init; } = 48.0;
}

/// <summary>
/// Pure-C# deterministic DAG / branching save tree lane allocator (GitKraken / VS Code Git Graph style).
/// Pins the active branch's mainline lineage to Lane 0, allocates lowest-free lanes for forked branches,
/// recycles closed lanes once a fork merges back into its ancestor, and produces seamless half-row Bezier
/// descriptors suitable for 60 FPS virtualized WPF rendering and headless unit testing.
/// </summary>
public static class SaveTreeGraphLayoutEngine
{
    public const double DefaultRowHeight = 58.0;
    public const double DefaultLaneSpacing = 22.0;
    public const double DefaultLeftPadding = 16.0;
    public const double MinGraphWidth = 44.0;
    public const double MaxGraphWidth = 196.0;

    public static SaveGraphLayoutResult ComputeLayout(
        SaveTreeGraphDto? graph,
        SaveTreeFilterMode filterMode = SaveTreeFilterMode.All,
        string? searchKeyword = null,
        string? activeBranchIdOverride = null)
    {
        if (graph is null || graph.Nodes.Count == 0)
        {
            return new SaveGraphLayoutResult
            {
                Rows = [],
                MaxLaneCount = 0,
                RecommendedGraphWidth = MinGraphWidth,
            };
        }

        string? activeBranchId = activeBranchIdOverride ?? graph.ActiveBranchId;
        var branchColorById = new Dictionary<string, (int ColorIndex, string Hex)>(StringComparer.OrdinalIgnoreCase);
        int branchIdx = 0;
        foreach (SaveBranchRecord branch in graph.Branches)
        {
            int resolvedIndex = ResolvePaletteIndex(branch.ColorHex, branchIdx);
            string resolvedHex = string.IsNullOrWhiteSpace(branch.ColorHex)
                ? SaveBranchColors.PickForIndex(resolvedIndex)
                : branch.ColorHex;
            branchColorById[branch.BranchId] = (resolvedIndex, resolvedHex);
            branchIdx++;
        }

        // 1. Apply optional filter before topological ordering.
        string trimmedQuery = searchKeyword?.Trim() ?? string.Empty;
        var filteredNodes = new List<SaveTreeNodeDto>(graph.Nodes.Count);
        foreach (SaveTreeNodeDto node in graph.Nodes)
        {
            if (filterMode == SaveTreeFilterMode.CurrentBranch
                && !string.IsNullOrWhiteSpace(activeBranchId)
                && !string.Equals(node.Snapshot.BranchId, activeBranchId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filterMode == SaveTreeFilterMode.PinnedOnly && !node.Snapshot.IsPinned)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(trimmedQuery) && !MatchesSearch(node, trimmedQuery))
            {
                continue;
            }

            filteredNodes.Add(node);
        }

        if (filteredNodes.Count == 0)
        {
            return new SaveGraphLayoutResult
            {
                Rows = [],
                MaxLaneCount = 0,
                RecommendedGraphWidth = MinGraphWidth,
            };
        }

        // 2. Build visible lookup & topological reverse-chronological order (children strictly above parents).
        var visibleById = filteredNodes.ToDictionary(n => n.Snapshot.SnapshotId, StringComparer.OrdinalIgnoreCase);
        List<SaveTreeNodeDto> ordered = TopologicalSortNewestFirst(filteredNodes, visibleById, activeBranchId);

        // 3. Identify active-branch mainline chain so Lane 0 stays dedicated to the primary trunk.
        var mainlineIds = ComputeMainlineSet(graph, visibleById, activeBranchId);

        // 4. Sweep top-to-bottom allocating lanes & recording seamless vertical/Bezier half-row edges.
        var laneSlots = new List<LaneSlot?>();
        int maxLaneCount = 1;
        var rawRows = new List<SaveGraphRowLayout>(ordered.Count);

        for (int rowIndex = 0; rowIndex < ordered.Count; rowIndex++)
        {
            SaveTreeNodeDto node = ordered[rowIndex];
            string snapshotId = node.Snapshot.SnapshotId;
            bool isMainline = mainlineIds.Contains(snapshotId);

            (int nodeColorIndex, string nodeColorHex) = ResolveNodeColor(node, branchColorById);

            // Find all active lanes currently descending toward this snapshot.
            var matchingLanes = new List<int>();
            for (int lane = 0; lane < laneSlots.Count; lane++)
            {
                if (laneSlots[lane] is { } slot
                    && string.Equals(slot.ExpectedSnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
                {
                    matchingLanes.Add(lane);
                }
            }

            int nodeLane;
            if (matchingLanes.Count > 0)
            {
                nodeLane = isMainline && matchingLanes.Contains(0)
                    ? 0
                    : matchingLanes[0];
            }
            else if (isMainline && (laneSlots.Count == 0 || laneSlots[0] is null))
            {
                EnsureLaneCapacity(laneSlots, 0);
                nodeLane = 0;
            }
            else
            {
                nodeLane = AllocateFreeLane(laneSlots, reserveLaneZero: !isMainline && mainlineIds.Count > 0);
            }

            var incoming = new List<SaveGraphCurveSegment>();
            var passThrough = new List<SaveGraphRailSegment>();
            var outgoing = new List<SaveGraphCurveSegment>();

            // Build incoming edges from all matching lanes and release merged/converged lanes.
            foreach (int incomingLane in matchingLanes)
            {
                LaneSlot slot = laneSlots[incomingLane]!;
                bool isConvergingCurve = incomingLane != nodeLane;
                incoming.Add(new SaveGraphCurveSegment(
                    FromLane: incomingLane,
                    ToLane: nodeLane,
                    ColorIndex: slot.ColorIndex,
                    ColorHex: slot.ColorHex,
                    IsFork: isConvergingCurve && !slot.IsMerge,
                    IsMerge: slot.IsMerge));

                laneSlots[incomingLane] = null;
            }

            // Snapshot the remaining occupied lanes as straight pass-through vertical rails.
            for (int lane = 0; lane < laneSlots.Count; lane++)
            {
                if (lane == nodeLane)
                {
                    continue;
                }

                if (laneSlots[lane] is { } activeSlot)
                {
                    passThrough.Add(new SaveGraphRailSegment(lane, activeSlot.ColorIndex, activeSlot.ColorHex));
                }
            }

            // Connect primary parent downward.
            string? visibleParentId = !string.IsNullOrWhiteSpace(node.ParentSnapshotId)
                && visibleById.ContainsKey(node.ParentSnapshotId)
                    ? node.ParentSnapshotId
                    : null;

            bool hasVisibleParent = visibleParentId is not null;
            if (visibleParentId is not null)
            {
                int existingParentLane = FindLaneExpecting(laneSlots, visibleParentId);
                bool parentIsMainline = mainlineIds.Contains(visibleParentId);
                bool keepMainlineStraightOnLaneZero = isMainline && parentIsMainline && nodeLane == 0 && existingParentLane != 0;

                if (existingParentLane >= 0 && !keepMainlineStraightOnLaneZero)
                {
                    // Forked branch curves smoothly into the already-descending parent lane and closes nodeLane!
                    bool isForkCurve = existingParentLane != nodeLane;
                    outgoing.Add(new SaveGraphCurveSegment(
                        FromLane: nodeLane,
                        ToLane: existingParentLane,
                        ColorIndex: nodeColorIndex,
                        ColorHex: nodeColorHex,
                        IsFork: isForkCurve,
                        IsMerge: false));
                }
                else
                {
                    // Continue downward on nodeLane (or shift onto Lane 0 if parent belongs to mainline and Lane 0 is free).
                    int targetLane = nodeLane;
                    if (parentIsMainline
                        && nodeLane != 0
                        && (laneSlots.Count == 0 || laneSlots[0] is null))
                    {
                        EnsureLaneCapacity(laneSlots, 0);
                        targetLane = 0;
                    }

                    EnsureLaneCapacity(laneSlots, targetLane);
                    laneSlots[targetLane] = new LaneSlot(visibleParentId, nodeColorIndex, nodeColorHex, IsMerge: false);
                    outgoing.Add(new SaveGraphCurveSegment(
                        FromLane: nodeLane,
                        ToLane: targetLane,
                        ColorIndex: nodeColorIndex,
                        ColorHex: nodeColorHex,
                        IsFork: targetLane != nodeLane,
                        IsMerge: false));
                }
            }

            // Connect optional merge parent downward.
            string? visibleMergeParentId = !string.IsNullOrWhiteSpace(node.MergeParentSnapshotId)
                && visibleById.ContainsKey(node.MergeParentSnapshotId)
                && !string.Equals(node.MergeParentSnapshotId, visibleParentId, StringComparison.OrdinalIgnoreCase)
                    ? node.MergeParentSnapshotId
                    : null;

            if (visibleMergeParentId is not null)
            {
                int mergeTargetLane = FindLaneExpecting(laneSlots, visibleMergeParentId);
                if (mergeTargetLane < 0)
                {
                    mergeTargetLane = AllocateFreeLane(laneSlots, reserveLaneZero: mainlineIds.Count > 0 && !mainlineIds.Contains(visibleMergeParentId));
                    laneSlots[mergeTargetLane] = new LaneSlot(visibleMergeParentId, nodeColorIndex, nodeColorHex, IsMerge: true);
                }

                outgoing.Add(new SaveGraphCurveSegment(
                    FromLane: nodeLane,
                    ToLane: mergeTargetLane,
                    ColorIndex: nodeColorIndex,
                    ColorHex: nodeColorHex,
                    IsFork: false,
                    IsMerge: true));
            }

            // Trim trailing null lanes so MaxLaneCount reflects true visual concurrency.
            while (laneSlots.Count > 0 && laneSlots[^1] is null)
            {
                laneSlots.RemoveAt(laneSlots.Count - 1);
            }

            int rowPeakLane = nodeLane;
            foreach (SaveGraphRailSegment rail in passThrough)
            {
                rowPeakLane = Math.Max(rowPeakLane, rail.Lane);
            }

            foreach (SaveGraphCurveSegment c in incoming)
            {
                rowPeakLane = Math.Max(rowPeakLane, Math.Max(c.FromLane, c.ToLane));
            }

            foreach (SaveGraphCurveSegment c in outgoing)
            {
                rowPeakLane = Math.Max(rowPeakLane, Math.Max(c.FromLane, c.ToLane));
            }

            maxLaneCount = Math.Max(maxLaneCount, rowPeakLane + 1);

            bool isSafetyNode = node.Snapshot.TriggerKind is SaveSnapshotTriggerKind.PreRollbackSafety
                or SaveSnapshotTriggerKind.PreBranchSwitchSafety;

            rawRows.Add(new SaveGraphRowLayout
            {
                Node = node,
                RowIndex = rowIndex,
                NodeLane = nodeLane,
                ColorIndex = nodeColorIndex,
                BranchColorHex = nodeColorHex,
                TotalLaneColumns = maxLaneCount,
                PassThroughRails = passThrough,
                IncomingCurves = incoming,
                OutgoingCurves = outgoing,
                IsHead = node.IsCurrentHead,
                IsDetachedHead = node.IsDetachedHead,
                IsBranchTip = node.IsBranchTip || node.BranchTipNames.Count > 0,
                IsPinned = node.Snapshot.IsPinned,
                IsSafetyNode = isSafetyNode,
                IsRoot = !hasVisibleParent && string.IsNullOrWhiteSpace(node.ParentSnapshotId),
                HasChildren = node.ChildSnapshotIds.Count > 0 || matchingLanes.Count > 0,
            });
        }

        // Normalize TotalLaneColumns across every row so horizontal text alignment stays razor-straight!
        var finalRows = rawRows
            .Select(r => r with { TotalLaneColumns = maxLaneCount })
            .ToList();

        double width = Math.Clamp(
            (DefaultLeftPadding * 2.0) + Math.Max(0, maxLaneCount - 1) * DefaultLaneSpacing + 8.0,
            MinGraphWidth,
            MaxGraphWidth);

        return new SaveGraphLayoutResult
        {
            Rows = finalRows,
            MaxLaneCount = maxLaneCount,
            RecommendedGraphWidth = width,
        };
    }

    public static int ResolvePaletteIndex(string? colorHex, int fallbackIndex = 0)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            for (int i = 0; i < SaveBranchColors.Palette.Count; i++)
            {
                if (string.Equals(SaveBranchColors.Palette[i], colorHex.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return Math.Abs(fallbackIndex) % SaveBranchColors.Palette.Count;
    }

    private static (int ColorIndex, string ColorHex) ResolveNodeColor(
        SaveTreeNodeDto node,
        Dictionary<string, (int ColorIndex, string Hex)> branchColorById)
    {
        if (branchColorById.TryGetValue(node.Snapshot.BranchId, out var pair))
        {
            return pair;
        }

        int idx = ResolvePaletteIndex(node.BranchColorHex, node.LaneIndex);
        string hex = string.IsNullOrWhiteSpace(node.BranchColorHex)
            ? SaveBranchColors.PickForIndex(idx)
            : node.BranchColorHex;
        return (idx, hex);
    }

    private static HashSet<string> ComputeMainlineSet(
        SaveTreeGraphDto graph,
        Dictionary<string, SaveTreeNodeDto> visibleById,
        string? activeBranchId)
    {
        var mainline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? startId = null;
        if (!string.IsNullOrWhiteSpace(activeBranchId))
        {
            SaveBranchRecord? activeBranch = graph.Branches.FirstOrDefault(
                b => string.Equals(b.BranchId, activeBranchId, StringComparison.OrdinalIgnoreCase));
            if (activeBranch?.HeadSnapshotId is { } branchHead && visibleById.ContainsKey(branchHead))
            {
                startId = branchHead;
            }
        }

        if (startId is null && !string.IsNullOrWhiteSpace(graph.CurrentHeadSnapshotId)
            && visibleById.ContainsKey(graph.CurrentHeadSnapshotId))
        {
            startId = graph.CurrentHeadSnapshotId;
        }

        if (startId is null)
        {
            SaveTreeNodeDto? fallback = visibleById.Values
                .OrderByDescending(n => !string.IsNullOrWhiteSpace(activeBranchId)
                    && string.Equals(n.Snapshot.BranchId, activeBranchId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(n => n.Snapshot.CreatedAtUtc)
                .FirstOrDefault();
            startId = fallback?.Snapshot.SnapshotId;
        }

        string? cursor = startId;
        int guard = 0;
        while (!string.IsNullOrWhiteSpace(cursor)
               && visibleById.TryGetValue(cursor, out SaveTreeNodeDto? node)
               && guard++ <= visibleById.Count)
        {
            if (!mainline.Add(cursor))
            {
                break;
            }

            cursor = node.ParentSnapshotId;
        }

        return mainline;
    }

    private static List<SaveTreeNodeDto> TopologicalSortNewestFirst(
        List<SaveTreeNodeDto> nodes,
        Dictionary<string, SaveTreeNodeDto> visibleById,
        string? activeBranchId)
    {
        // Directed edge: child -> parent (so children have in-degree 0 when no other visible node points to them).
        var incomingChildCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parentsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (SaveTreeNodeDto node in nodes)
        {
            string id = node.Snapshot.SnapshotId;
            incomingChildCount[id] = 0;
            parentsOf[id] = [];
        }

        foreach (SaveTreeNodeDto node in nodes)
        {
            string id = node.Snapshot.SnapshotId;
            if (!string.IsNullOrWhiteSpace(node.ParentSnapshotId)
                && visibleById.ContainsKey(node.ParentSnapshotId)
                && !string.Equals(node.ParentSnapshotId, id, StringComparison.OrdinalIgnoreCase))
            {
                parentsOf[id].Add(node.ParentSnapshotId);
                incomingChildCount[node.ParentSnapshotId]++;
            }

            if (!string.IsNullOrWhiteSpace(node.MergeParentSnapshotId)
                && visibleById.ContainsKey(node.MergeParentSnapshotId)
                && !string.Equals(node.MergeParentSnapshotId, id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(node.MergeParentSnapshotId, node.ParentSnapshotId, StringComparison.OrdinalIgnoreCase))
            {
                parentsOf[id].Add(node.MergeParentSnapshotId);
                incomingChildCount[node.MergeParentSnapshotId]++;
            }
        }

        var ready = new List<SaveTreeNodeDto>();
        foreach (SaveTreeNodeDto node in nodes)
        {
            if (incomingChildCount[node.Snapshot.SnapshotId] == 0)
            {
                ready.Add(node);
            }
        }

        Comparison<SaveTreeNodeDto> compare = (a, b) =>
        {
            int timeCmp = b.Snapshot.CreatedAtUtc.CompareTo(a.Snapshot.CreatedAtUtc);
            if (timeCmp != 0)
            {
                return timeCmp;
            }

            bool aActive = !string.IsNullOrWhiteSpace(activeBranchId)
                && string.Equals(a.Snapshot.BranchId, activeBranchId, StringComparison.OrdinalIgnoreCase);
            bool bActive = !string.IsNullOrWhiteSpace(activeBranchId)
                && string.Equals(b.Snapshot.BranchId, activeBranchId, StringComparison.OrdinalIgnoreCase);
            if (aActive != bActive)
            {
                return aActive ? -1 : 1;
            }

            return string.Compare(b.Snapshot.SnapshotId, a.Snapshot.SnapshotId, StringComparison.OrdinalIgnoreCase);
        };

        var result = new List<SaveTreeNodeDto>(nodes.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (ready.Count > 0)
        {
            ready.Sort(compare);
            SaveTreeNodeDto next = ready[0];
            ready.RemoveAt(0);

            string id = next.Snapshot.SnapshotId;
            if (!emitted.Add(id))
            {
                continue;
            }

            result.Add(next);
            foreach (string parentId in parentsOf[id])
            {
                incomingChildCount[parentId]--;
                if (incomingChildCount[parentId] == 0 && visibleById.TryGetValue(parentId, out SaveTreeNodeDto? parentNode))
                {
                    ready.Add(parentNode);
                }
            }
        }

        // Defensive cycle fallback: append any remaining nodes newest-first.
        if (result.Count < nodes.Count)
        {
            var remaining = nodes
                .Where(n => !emitted.Contains(n.Snapshot.SnapshotId))
                .ToList();
            remaining.Sort(compare);
            result.AddRange(remaining);
        }

        return result;
    }

    private static bool MatchesSearch(SaveTreeNodeDto node, string keyword)
    {
        if (node.BranchName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || node.Snapshot.ShortId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || node.Snapshot.SnapshotId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(node.Snapshot.Note)
                && node.Snapshot.Note.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (string tip in node.BranchTipNames)
        {
            if (tip.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindLaneExpecting(List<LaneSlot?> slots, string snapshotId)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] is { } slot
                && string.Equals(slot.ExpectedSnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int AllocateFreeLane(List<LaneSlot?> slots, bool reserveLaneZero)
    {
        int start = reserveLaneZero ? 1 : 0;
        for (int i = start; i < slots.Count; i++)
        {
            if (slots[i] is null)
            {
                return i;
            }
        }

        while (slots.Count < start)
        {
            slots.Add(null);
        }

        slots.Add(null);
        return slots.Count - 1;
    }

    private static void EnsureLaneCapacity(List<LaneSlot?> slots, int index)
    {
        while (slots.Count <= index)
        {
            slots.Add(null);
        }
    }

    private sealed record LaneSlot(
        string ExpectedSnapshotId,
        int ColorIndex,
        string ColorHex,
        bool IsMerge);
}
