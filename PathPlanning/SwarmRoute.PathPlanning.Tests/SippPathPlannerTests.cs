using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Tests.TestSupport;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Tests;

/// <summary>
/// Correctness of the v1 <see cref="SippPathPlanner"/>: like Dijkstra it must find routes and report the same
/// unknown/unreachable failures, but unlike Dijkstra it minimises <em>arrival time</em> on the unified
/// <see cref="TimeAxis.HopMs"/> axis (hops + waits), consults the <see cref="IReservationView"/> to insert waits
/// around busy control points / lanes, and lets a follower arrive exactly as a resource frees (touching
/// half-open intervals).
/// </summary>
public sealed class SippPathPlannerTests
{
    private static readonly Guid Roadmap = Guid.NewGuid();
    private readonly SippPathPlanner _planner = new();

    private PlanResult Plan(RoadmapGraph graph, string from, string to, long release = 0, IReservationView? view = null)
        => _planner.Plan(graph, new PlanRequest(Roadmap, "AGV-1", from, to, release), view ?? AlwaysFreeReservationView.Instance);

    private static List<string> SitesOf(SpaceTimePath path)
        => path.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).Select(c => c.Resource.Id).ToList();

    private static List<string> LanesOf(SpaceTimePath path)
        => path.Cells.Where(c => c.Resource.Kind == ResourceKind.Lane).Select(c => c.Resource.Id).ToList();

    private static List<SpaceTimeCell> CpCells(SpaceTimePath path)
        => path.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).ToList();

    // ── Space-only correctness (no reservations) ────────────────────────────────────────────────────────

    [Fact]
    public void Linear_chain_returns_the_only_path()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Edge("C", "D").Build();

        var result = Plan(graph, "A", "D");

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "C", "D" }, SitesOf(result.Path!));
        Assert.Equal(new[] { "A-B", "B-C", "C-D" }, LanesOf(result.Path!));
    }

    [Fact]
    public void Minimises_hops_not_weighted_distance()
    {
        // Direct A->D is one long hop (distance 10 → weight 10000); A->B->C->D is three short hops (weight 3000).
        // Dijkstra would take the cheaper 3-hop chain; SIPP minimises ARRIVAL TIME (hops at HopMs=1) → the 1-hop
        // direct edge arrives sooner, so SIPP takes it. This is the defining behavioural difference.
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "D", 10.0)
            .Edge("A", "B", 1.0).Edge("B", "C", 1.0).Edge("C", "D", 1.0)
            .Build();

        var result = Plan(graph, "A", "D");

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "D" }, SitesOf(result.Path!));
        Assert.Equal(1, result.Cost!.HopCount);
    }

    [Fact]
    public void Unreachable_goal_fails_with_NoRoute()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("X", "Y").Build();

        var result = Plan(graph, "A", "Y");

        Assert.False(result.Success);
        Assert.Null(result.Path);
        Assert.Contains("PP-003", result.FailureReason);
    }

    [Fact]
    public void Unknown_start_or_goal_fails_with_UnknownSite()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();

        Assert.Contains("PP-002", Plan(graph, "ZZZ", "B").FailureReason);
        Assert.Contains("PP-002", Plan(graph, "A", "ZZZ").FailureReason);
    }

    [Fact]
    public void Directedness_is_respected_reverse_is_unreachable()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Build();

        Assert.True(Plan(graph, "A", "C").Success);
        Assert.False(Plan(graph, "C", "A").Success);
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
        Assert.True(only.Interval.Duration > 0);
    }

    [Fact]
    public void Timeline_is_unit_spaced_on_the_hop_axis()
    {
        // Edge weights differ (2,3,1) but SIPP times every hop at HopMs=1 — so all CP cells are unit-length and
        // contiguous, regardless of distance. (Contrast Dijkstra, whose cell durations equal the edge weights.)
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 2.0).Edge("B", "C", 3.0).Edge("C", "D", 1.0)
            .Build();

        var result = Plan(graph, "A", "D", release: 5000);
        Assert.True(result.Success);

        var cells = result.Path!.Cells;
        Assert.Equal(7, cells.Count); // 4 CP + 3 Lane
        Assert.Equal(5000, cells[0].Interval.StartMs);

        var cp = CpCells(result.Path!);
        for (var i = 0; i < cp.Count; i++)
        {
            Assert.Equal(SippPathPlanner.HopMs, cp[i].Interval.Duration);
            if (i > 0)
            {
                Assert.Equal(cp[i - 1].Interval.EndMs, cp[i].Interval.StartMs);
                Assert.False(cp[i - 1].Interval.Overlaps(cp[i].Interval));
            }
        }

        Assert.Equal(3, result.Cost!.HopCount);
        Assert.Equal(4, result.Cost!.DurationMs); // 3 hops + 1 goal dwell, all at HopMs=1
    }

    // ── Reservation-aware behaviour ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Waits_at_origin_until_a_busy_control_point_frees()
    {
        // Only route is A->B->C, but B is reserved by another agent during [0,3). Departing at t=0 would enter B
        // at t=1 (conflict). SIPP holds at A and enters B exactly at t=3, the instant it frees (touching the
        // half-open boundary — back-to-back is legal).
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Build();
        var view = new FakeReservationView().Reserve(RoadmapGraph.SiteRef("B"), 0, 3);

        var result = Plan(graph, "A", "C", release: 0, view);

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "C" }, SitesOf(result.Path!));

        var cp = CpCells(result.Path!);
        Assert.Equal(0, cp[0].Interval.StartMs);   // A: held from release...
        Assert.Equal(3, cp[0].Interval.EndMs);     // ...until the move into B completes at t=3 (a 3-tick wait)
        Assert.Equal(3, cp[1].Interval.StartMs);   // B entered exactly as it frees
        Assert.Equal(4, cp[2].Interval.StartMs);   // C reached one tick later
    }

    [Fact]
    public void Waits_for_a_busy_lane_before_traversing_it()
    {
        // The lane A-B is busy during [0,2) — e.g. because the reversed lane B-A is held by an oncoming vehicle.
        // SIPP must not traverse A-B in that window; it waits and departs at t=2 (entering B at t=3).
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Build();
        var view = new FakeReservationView().Reserve(RoadmapGraph.LaneRef("A", "B"), 0, 2);

        var result = Plan(graph, "A", "C", release: 0, view);

        Assert.True(result.Success);
        var cp = CpCells(result.Path!);
        Assert.Equal(3, cp[0].Interval.EndMs);   // A held [0,3): waited for the lane, then traversed [2,3)
        Assert.Equal(3, cp[1].Interval.StartMs); // arrived at B at t=3
    }

    [Fact]
    public void Detours_around_a_permanently_blocked_control_point()
    {
        // B is reserved forever ([0, ∞)); the only conflict-free route to D is the longer A->C->...->D branch.
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B").Edge("B", "D")
            .Edge("A", "C").Edge("C", "E").Edge("E", "D")
            .Build();
        var view = new FakeReservationView().Reserve(RoadmapGraph.SiteRef("B"), 0, long.MaxValue);

        var result = Plan(graph, "A", "D", release: 0, view);

        Assert.True(result.Success);
        Assert.DoesNotContain("B", SitesOf(result.Path!));
        Assert.Equal(new[] { "A", "C", "E", "D" }, SitesOf(result.Path!));
    }

    // ── Determinism + guards ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Is_deterministic_for_identical_inputs()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B").Edge("A", "C").Edge("B", "D").Edge("C", "D").Edge("D", "E")
            .Build();
        var view = new FakeReservationView().Reserve(RoadmapGraph.SiteRef("B"), 0, 5);

        var first = Plan(graph, "A", "E", release: 0, view);
        var second = Plan(graph, "A", "E", release: 0, view);

        Assert.True(first.Success);
        Assert.Equal(first, second); // PlanResult is a value object
    }

    [Fact]
    public void Plan_rejects_null_arguments()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();
        var request = new PlanRequest(Roadmap, "AGV-1", "A", "B");

        Assert.Throws<ArgumentNullException>(() => _planner.Plan(null!, request, AlwaysFreeReservationView.Instance));
        Assert.Throws<ArgumentNullException>(() => _planner.Plan(graph, null!, AlwaysFreeReservationView.Instance));
        Assert.Throws<ArgumentNullException>(() => _planner.Plan(graph, request, null!));
    }
}
