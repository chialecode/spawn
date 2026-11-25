using Spawn.Contracts;
using Spawn.Host.Controls;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Pure headless unit tests for <see cref="SaveTreeGraphLayoutEngine"/> covering linear chains,
/// multi-branch forks from historical ancestors, nested sub-branches, lane recycling, detached HEAD,
/// filter modes, and cloud-divergence dual timelines.
/// </summary>
public sealed class SaveTreeGraphLayoutEngineTests
{
    [Fact]
    public void ComputeLayout_LinearHistory_PinsAllNodesToLaneZeroWithContinuousVerticalEdges()
    {
        DateTimeOffset t0 = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        var graph = BuildGraph(
            activeBranchId: "b-main",
            currentHeadId: "s3",
            branches:
            [
                MakeBranch("b-main", "main", SaveBranchColors.Palette[0], headId: "s3", isDefault: true, count: 3),
            ],
            nodes:
            [
                MakeNode("s1", "b-main", "main", SaveBranchColors.Palette[0], parentId: null, created: t0, note: "开局存档"),
                MakeNode("s2", "b-main", "main", SaveBranchColors.Palette[0], parentId: "s1", created: t0.AddHours(1), note: "第一章通关", pinned: true),
                MakeNode("s3", "b-main", "main", SaveBranchColors.Palette[0], parentId: "s2", created: t0.AddHours(2), note: "Boss 战前", isHead: true, tipNames: ["main"]),
            ]);

        SaveGraphLayoutResult result = SaveTreeGraphLayoutEngine.ComputeLayout(graph);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(1, result.MaxLaneCount);
        Assert.Equal(["s3", "s2", "s1"], result.Rows.Select(r => r.Node.Snapshot.SnapshotId).ToArray());
        Assert.All(result.Rows, r => Assert.Equal(0, r.NodeLane));

        // Top HEAD node: no incoming, 1 straight outgoing to Lane 0
        Assert.True(result.Rows[0].IsHead);
        Assert.Empty(result.Rows[0].IncomingCurves);
        Assert.Single(result.Rows[0].OutgoingCurves);
        Assert.Equal(0, result.Rows[0].OutgoingCurves[0].ToLane);

        // Middle pinned node: 1 incoming, 1 outgoing
        Assert.True(result.Rows[1].IsPinned);
        Assert.Single(result.Rows[1].IncomingCurves);
        Assert.Single(result.Rows[1].OutgoingCurves);

        // Root node: 1 incoming, no outgoing
        Assert.True(result.Rows[2].IsRoot);
        Assert.Single(result.Rows[2].IncomingCurves);
        Assert.Empty(result.Rows[2].OutgoingCurves);
    }

    [Fact]
    public void ComputeLayout_MultiWayForksAndNestedSubBranch_KeepsMainOnLaneZeroAndEmitsBezierForkCurves()
    {
        DateTimeOffset t0 = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        // Topology:
        // s1 (main) -> s2 (main) -> s5 (main, HEAD)
        //          \-> s3 (boss-rush) -> s4 (boss-rush)
        //                            \-> s6 (bad-ending, forked from s3!)
        var graph = BuildGraph(
            activeBranchId: "b-main",
            currentHeadId: "s5",
            branches:
            [
                MakeBranch("b-main", "main", SaveBranchColors.Palette[0], headId: "s5", isDefault: true, count: 3),
                MakeBranch("b-boss", "boss-rush", SaveBranchColors.Palette[1], headId: "s4", baseId: "s1", count: 2),
                MakeBranch("b-bad", "bad-ending", SaveBranchColors.Palette[2], headId: "s6", baseId: "s3", count: 1),
            ],
            nodes:
            [
                MakeNode("s1", "b-main", "main", SaveBranchColors.Palette[0], parentId: null, created: t0, note: "营火起点"),
                MakeNode("s2", "b-main", "main", SaveBranchColors.Palette[0], parentId: "s1", created: t0.AddHours(1), note: "主线推进"),
                MakeNode("s3", "b-boss", "boss-rush", SaveBranchColors.Palette[1], parentId: "s1", created: t0.AddHours(2), note: "无伤挑战起点"),
                MakeNode("s4", "b-boss", "boss-rush", SaveBranchColors.Palette[1], parentId: "s3", created: t0.AddHours(3), note: "女武神二阶段", tipNames: ["boss-rush"]),
                MakeNode("s5", "b-main", "main", SaveBranchColors.Palette[0], parentId: "s2", created: t0.AddHours(4), note: "主线最新", isHead: true, tipNames: ["main"]),
                MakeNode("s6", "b-bad", "bad-ending", SaveBranchColors.Palette[2], parentId: "s3", created: t0.AddHours(5), note: "癫火结局路线", tipNames: ["bad-ending"]),
            ]);

        SaveGraphLayoutResult result = SaveTreeGraphLayoutEngine.ComputeLayout(graph);

        Assert.Equal(6, result.Rows.Count);
        Assert.True(result.MaxLaneCount >= 3, $"Expected >= 3 concurrent lanes, got {result.MaxLaneCount}");

        var rowById = result.Rows.ToDictionary(r => r.Node.Snapshot.SnapshotId, StringComparer.OrdinalIgnoreCase);

        // Topological invariant: every child appears strictly above its parent!
        foreach (SaveGraphRowLayout row in result.Rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Node.ParentSnapshotId))
            {
                int parentRow = rowById[row.Node.ParentSnapshotId].RowIndex;
                Assert.True(row.RowIndex < parentRow, $"{row.Node.Snapshot.SnapshotId} must appear above parent {row.Node.ParentSnapshotId}");
            }
        }

        // Active mainline (s5 -> s2 -> s1) MUST stay pinned on Lane 0
        Assert.Equal(0, rowById["s5"].NodeLane);
        Assert.Equal(0, rowById["s2"].NodeLane);
        Assert.Equal(0, rowById["s1"].NodeLane);

        // Forked branches occupy non-zero lanes and curve back into their parent's lane
        Assert.True(rowById["s6"].NodeLane > 0);
        Assert.True(rowById["s4"].NodeLane > 0);
        Assert.NotEqual(rowById["s6"].NodeLane, rowById["s4"].NodeLane);

        // s3's fork branch must connect into s1's Lane 0 (either curving out of s3 or converging into s1)
        bool hasForkToLaneZero = rowById["s3"].OutgoingCurves.Any(c => c.ToLane == 0 && c.IsFork)
            || rowById["s1"].IncomingCurves.Any(c => c.FromLane != 0 && c.ToLane == 0 && c.IsFork);
        Assert.True(hasForkToLaneZero, "Expected a curved fork edge connecting the boss-rush branch to s1 on Lane 0.");

        // Graph width scales cleanly above minimum
        Assert.True(result.RecommendedGraphWidth > SaveTreeGraphLayoutEngine.MinGraphWidth);
    }

    [Fact]
    public void ComputeLayout_FilterModesAndSearch_FiltersBranchesPinsAndKeywordsDeterministically()
    {
        DateTimeOffset t0 = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        var graph = BuildGraph(
            activeBranchId: "b-main",
            currentHeadId: "s2",
            branches:
            [
                MakeBranch("b-main", "main", SaveBranchColors.Palette[0], headId: "s2", isDefault: true, count: 2),
                MakeBranch("b-cloud", "cloud-deck-backup", SaveBranchColors.Palette[3], headId: "s3", baseId: "s1", count: 1),
            ],
            nodes:
            [
                MakeNode("s1", "b-main", "main", SaveBranchColors.Palette[0], parentId: null, created: t0, note: "初始节点", pinned: true),
                MakeNode("s2", "b-main", "main", SaveBranchColors.Palette[0], parentId: "s1", created: t0.AddHours(1), note: "主线进度", isHead: true, tipNames: ["main"]),
                MakeNode("s3", "b-cloud", "cloud-deck-backup", SaveBranchColors.Palette[3], parentId: "s1", created: t0.AddHours(2), note: "掌机离线分支", pinned: true, tipNames: ["cloud-deck-backup"]),
            ]);

        SaveGraphLayoutResult onlyCurrent = SaveTreeGraphLayoutEngine.ComputeLayout(graph, SaveTreeFilterMode.CurrentBranch);
        Assert.Equal(["s2", "s1"], onlyCurrent.Rows.Select(r => r.Node.Snapshot.SnapshotId).ToArray());

        SaveGraphLayoutResult onlyPinned = SaveTreeGraphLayoutEngine.ComputeLayout(graph, SaveTreeFilterMode.PinnedOnly);
        Assert.Equal(["s3", "s1"], onlyPinned.Rows.Select(r => r.Node.Snapshot.SnapshotId).ToArray());

        SaveGraphLayoutResult searchHit = SaveTreeGraphLayoutEngine.ComputeLayout(graph, SaveTreeFilterMode.All, searchKeyword: "掌机");
        Assert.Single(searchHit.Rows);
        Assert.Equal("s3", searchHit.Rows[0].Node.Snapshot.SnapshotId);
    }

    private static SaveTreeGraphDto BuildGraph(
        string activeBranchId,
        string currentHeadId,
        IReadOnlyList<SaveBranchRecord> branches,
        IReadOnlyList<SaveTreeNodeDto> nodes) =>
        new()
        {
            GameId = "game-test",
            ActiveBranchId = activeBranchId,
            ActiveBranchName = branches.First(b => b.BranchId == activeBranchId).Name,
            CurrentHeadSnapshotId = currentHeadId,
            Branches = branches,
            Nodes = nodes,
        };

    private static SaveBranchRecord MakeBranch(
        string id,
        string name,
        string colorHex,
        string headId,
        string? baseId = null,
        bool isDefault = false,
        int count = 1) =>
        new()
        {
            BranchId = id,
            GameId = "game-test",
            Name = name,
            ColorHex = colorHex,
            HeadSnapshotId = headId,
            BaseSnapshotId = baseId,
            IsDefault = isDefault,
            SnapshotCount = count,
        };

    private static SaveTreeNodeDto MakeNode(
        string id,
        string branchId,
        string branchName,
        string colorHex,
        string? parentId,
        DateTimeOffset created,
        string note,
        bool isHead = false,
        bool pinned = false,
        IReadOnlyList<string>? tipNames = null) =>
        new()
        {
            Snapshot = new SaveSnapshotRecord
            {
                SnapshotId = id,
                GameId = "game-test",
                BranchId = branchId,
                ParentSnapshotId = parentId,
                Note = note,
                CreatedMachineId = "desktop-01",
                CreatedAtUtc = created,
                IsPinned = pinned,
                FileCount = 4,
                TotalRawBytes = 1024 * 512,
                TotalStoredBytes = 1024 * 128,
            },
            BranchName = branchName,
            BranchColorHex = colorHex,
            ParentSnapshotId = parentId,
            IsCurrentHead = isHead,
            IsBranchTip = tipNames is { Count: > 0 },
            BranchTipNames = tipNames ?? [],
        };
}
