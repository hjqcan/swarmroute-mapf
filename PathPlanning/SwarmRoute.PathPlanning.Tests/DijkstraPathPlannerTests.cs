using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Tests.TestSupport;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Tests;

/// <summary>
/// Correctness of the v0 <see cref="DijkstraPathPlanner"/>: shortest-path selection (linear / diamond),
/// cross-checked against <see cref="RoadmapGraph.DistanceTo"/>; unreachable / unknown failures; and that the
/// produced <see cref="SpaceTimePath"/> is well-formed (monotonic, non-overlapping).
/// </summary>
public sealed class DijkstraPathPlannerTests
{
    private static readonly Guid Roadmap = Guid.NewGuid();
    private readonly DijkstraPathPlanner _planner = new();
    private readonly IReservationView _reservations = AlwaysFreeReservationView.Instance;

    private PlanResult Plan(RoadmapGraph graph, string from, string to, long release = 0)
        => _planner.Plan(graph, new PlanRequest(Roadmap, "AGV-1", from, to, release), _reservations);

    private PlanResult Plan(
        RoadmapGraph graph,
        string from,
        string to,
        long release,
        IEnumerable<ResourceRef> blacklist)
        => _planner.Plan(
            graph,
            new PlanRequest(Roadmap, "AGV-1", from, to, release, blacklistedResources: blacklist),
            _reservations);

    private static List<string> SitesOf(SpaceTimePath path)
        => path.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).Select(c => c.Resource.Id).ToList();

    private static List<string> LanesOf(SpaceTimePath path)
        => path.Cells.Where(c => c.Resource.Kind == ResourceKind.Lane).Select(c => c.Resource.Id).ToList();

    [Fact]
    public void Linear_chain_returns_the_only_path()
    {
        // A -> B -> C -> D, each edge distance 1.0 (weight 1000).
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Edge("C", "D").Build();

        var result = Plan(graph, "A", "D");

        Assert.True(result.Success);
        Assert.NotNull(result.Path);
        Assert.Equal(new[] { "A", "B", "C", "D" }, SitesOf(result.Path!));
    }

    [Fact]
    public void Diamond_returns_the_shorter_branch_and_matches_DistanceTo()
    {
        // A -> B -> D (1 + 1 = 2.0)  vs  A -> C -> D (1 + 5 = 6.0). Shorter is via B.
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 1.0).Edge("B", "D", 1.0)
            .Edge("A", "C", 1.0).Edge("C", "D", 5.0)
            .Build();

        var result = Plan(graph, "A", "D");

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "D" }, SitesOf(result.Path!));

        // Cross-check the cost against the graph's own Dijkstra distance (scaled weight units).
        var expectedDistance = graph.DistanceTo("A", "D");
        Assert.NotNull(expectedDistance);
        Assert.Equal(expectedDistance!.Value, result.Cost!.DistanceUnits);
        Assert.Equal(2000L, result.Cost!.DistanceUnits); // (1.0 + 1.0) * 1000
        Assert.Equal(2, result.Cost!.HopCount);
    }

    [Fact]
    public void Diamond_picks_whichever_branch_is_cheaper_when_weights_flip()
    {
        // Make the C branch cheaper this time; planner must switch to A -> C -> D.
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 5.0).Edge("B", "D", 5.0)
            .Edge("A", "C", 1.0).Edge("C", "D", 1.0)
            .Build();

        var result = Plan(graph, "A", "D");

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "C", "D" }, SitesOf(result.Path!));
        Assert.Equal(graph.DistanceTo("A", "D")!.Value, result.Cost!.DistanceUnits);
    }

    [Fact]
    public void Unreachable_goal_fails_with_NoRoute()
    {
        // Two disconnected components: A -> B and X -> Y. D is unreachable from A.
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("X", "Y").Build();

        var result = Plan(graph, "A", "Y");

        Assert.False(result.Success);
        Assert.Null(result.Path);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("PP-003", result.FailureReason); // NoRoute
    }

    [Fact]
    public void Directedness_is_respected_reverse_is_unreachable()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Build();

        // Forward succeeds...
        Assert.True(Plan(graph, "A", "C").Success);
        // ...reverse does not (edges are directed).
        Assert.False(Plan(graph, "C", "A").Success);
    }

    [Fact]
    public void Unknown_start_or_goal_fails_with_UnknownSite()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();

        var unknownStart = Plan(graph, "ZZZ", "B");
        Assert.False(unknownStart.Success);
        Assert.Contains("PP-002", unknownStart.FailureReason); // UnknownSite

        var unknownGoal = Plan(graph, "A", "ZZZ");
        Assert.False(unknownGoal.Success);
        Assert.Contains("PP-002", unknownGoal.FailureReason);
    }

    [Fact]
    public void Start_equals_goal_yields_single_cell_zero_cost()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();

        var result = Plan(graph, "A", "A", release: 100);

        Assert.True(result.Success);
        Assert.Equal(new[] { "A" }, SitesOf(result.Path!));
        Assert.Equal(PlanCost.Zero, result.Cost);
        var only = Assert.Single(result.Path!.Cells);
        Assert.Equal(100, only.Interval.StartMs);
        Assert.True(only.Interval.Duration > 0); // non-degenerate half-open interval
    }

    [Fact]
    public void Path_timeline_is_monotonic_and_non_overlapping()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 1.0).Edge("B", "C", 2.0).Edge("C", "D", 3.0)
            .Build();

        var result = Plan(graph, "A", "D", release: 5000);
        Assert.True(result.Success);
        var cells = result.Path!.Cells;

        // Four CP cells plus three directed Lane cells.
        Assert.Equal(7, cells.Count);

        // First cell starts exactly at the release time.
        Assert.Equal(5000, cells[0].Interval.StartMs);

        var cpCells = cells.Where(c => c.Resource.Kind == ResourceKind.CP).ToList();
        for (var i = 0; i < cpCells.Count; i++)
        {
            // Each interval is non-degenerate (Start < End) under half-open semantics.
            Assert.True(cpCells[i].Interval.StartMs < cpCells[i].Interval.EndMs,
                $"cell {i} must have positive duration");

            if (i > 0)
            {
                // Contiguous + strictly advancing: next starts exactly where the previous ended,
                // so no two cells overlap (touching endpoints do not overlap under [start,end)).
                Assert.Equal(cpCells[i - 1].Interval.EndMs, cpCells[i].Interval.StartMs);
                Assert.False(cpCells[i - 1].Interval.Overlaps(cpCells[i].Interval));
            }
        }

        // Move-cell durations equal the scaled edge weights (1000, 2000, 3000).
        Assert.Equal(1000, cpCells[0].Interval.Duration);
        Assert.Equal(2000, cpCells[1].Interval.Duration);
        Assert.Equal(3000, cpCells[2].Interval.Duration);
        Assert.Equal(new[] { "A-B", "B-C", "C-D" }, LanesOf(result.Path!));

        // Total distance == sum of edge weights == graph DistanceTo.
        Assert.Equal(6000L, result.Cost!.DistanceUnits);
        Assert.Equal(graph.DistanceTo("A", "D")!.Value, result.Cost!.DistanceUnits);
    }

    [Fact]
    public void Timeline_includes_directed_lane_cells_for_each_edge()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Build();

        var result = Plan(graph, "A", "C");

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "C" }, SitesOf(result.Path!));
        Assert.Equal(new[] { "A-B", "B-C" }, LanesOf(result.Path!));
    }

    [Fact]
    public void Blacklisted_intermediate_site_is_pruned_from_dijkstra()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 1.0).Edge("B", "D", 1.0)
            .Edge("A", "C", 2.0).Edge("C", "D", 2.0)
            .Build();

        var result = Plan(graph, "A", "D", 0, new[] { RoadmapGraph.SiteRef("B") });

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "C", "D" }, SitesOf(result.Path!));
    }

    [Fact]
    public void Blacklisted_lane_is_pruned_from_dijkstra()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 1.0).Edge("B", "D", 1.0)
            .Edge("A", "C", 2.0).Edge("C", "D", 2.0)
            .Build();

        var result = Plan(graph, "A", "D", 0, new[] { RoadmapGraph.LaneRef("A", "B") });

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "C", "D" }, SitesOf(result.Path!));
        Assert.DoesNotContain("A-B", LanesOf(result.Path!));
    }

    [Fact]
    public void Plan_rejects_null_arguments()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();
        var request = new PlanRequest(Roadmap, "AGV-1", "A", "B");

        Assert.Throws<ArgumentNullException>(() => _planner.Plan(null!, request, _reservations));
        Assert.Throws<ArgumentNullException>(() => _planner.Plan(graph, null!, _reservations));
        Assert.Throws<ArgumentNullException>(() => _planner.Plan(graph, request, null!));
    }
}
