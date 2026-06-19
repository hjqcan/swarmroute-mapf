using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Cbs;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Tests.TestSupport;
using SwarmRoute.SpatioTemporal.Kernel;
using Xunit;

namespace SwarmRoute.PathPlanning.Tests.Cbs;

/// <summary>
/// CCBS = continuous CBS: the discrete <see cref="CbsLocalSolver"/> with the continuous-time
/// <see cref="SippwrtPathPlanner"/> low level and <c>Continuous: true</c> (each constraint forbids the other
/// agent's whole occupation interval, motion-aware). Conflict detection already uses half-open <c>Overlaps</c>,
/// so it works on real-ms intervals unchanged; <c>Continuous: false</c> stays byte-identical to discrete CBS
/// (proven by the existing <c>CbsLocalSolverTests</c>).
/// </summary>
public sealed class CcbsLocalSolverTests
{
    private static RoadmapGraph FourCycle(double w = 1.0) => new RoadmapGraphBuilder()
        .Edge("A", "B", w).Edge("B", "A", w)
        .Edge("B", "C", w).Edge("C", "B", w)
        .Edge("C", "D", w).Edge("D", "C", w)
        .Edge("D", "A", w).Edge("A", "D", w)
        .Build();

    private static CbsResult Solve(RoadmapGraph graph, IReadOnlyList<CbsAgent> agents, CbsOptions? options = null) =>
        new CbsLocalSolver(options ?? new CbsOptions(Continuous: true), new SippwrtPathPlanner())
            .Solve(graph, agents, new FakeReservationView(), releaseTick: 0);

    private static string Goal(CbsResult r, string id) =>
        r.Paths![id].Cells.Last(c => c.Resource.Kind == ResourceKind.CP).Resource.Id;

    // ── CCBS resolves a head-on by routing the agents onto opposite arcs (continuous low level) ───────────────
    [Fact]
    public void Resolves_head_on_by_routing_apart()
    {
        var agents = new[] { new CbsAgent("agv-1", "A", "C", 0), new CbsAgent("agv-2", "C", "A", 1) };

        var result = Solve(FourCycle(), agents);

        Assert.True(result.Solved, $"expected Solved, got {result.Status}: {result.FailureReason}");
        Assert.Equal("C", Goal(result, "agv-1"));
        Assert.Equal("A", Goal(result, "agv-2"));
    }

    // ── Constructor guard: continuous constraints and the low-level planner are one contract ───────────────────
    [Fact]
    public void Continuous_options_default_to_sippwrt_low_level()
    {
        var agents = new[] { new CbsAgent("agv-1", "A", "C", 0), new CbsAgent("agv-2", "C", "A", 1) };

        var result = new CbsLocalSolver(new CbsOptions(Continuous: true))
            .Solve(FourCycle(), agents, new FakeReservationView(), releaseTick: 0);

        Assert.True(result.Solved, $"expected Solved, got {result.Status}: {result.FailureReason}");
    }

    [Fact]
    public void Rejects_continuous_constraints_with_discrete_low_level()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new CbsLocalSolver(new CbsOptions(Continuous: true), new SippPathPlanner()));

        Assert.Contains("Continuous=true requires SippwrtPathPlanner", ex.Message);
    }

    [Fact]
    public void Rejects_sippwrt_low_level_with_discrete_constraints()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new CbsLocalSolver(new CbsOptions(Continuous: false), new SippwrtPathPlanner()));

        Assert.Contains("Continuous=false requires a discrete time-axis low-level planner", ex.Message);
    }

    // ── Works on a NON-uniform map (real edge durations) where a 1-tick discrete constraint would mis-handle ──
    [Fact]
    public void Resolves_head_on_on_a_non_uniform_cycle()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 2.0).Edge("B", "A", 2.0)
            .Edge("B", "C", 1.0).Edge("C", "B", 1.0)
            .Edge("C", "D", 2.0).Edge("D", "C", 2.0)
            .Edge("D", "A", 1.0).Edge("A", "D", 1.0)
            .Build();
        var agents = new[] { new CbsAgent("agv-1", "A", "C", 0), new CbsAgent("agv-2", "C", "A", 1) };

        var result = Solve(graph, agents);

        Assert.True(result.Solved, $"expected Solved, got {result.Status}: {result.FailureReason}");
        Assert.Equal("C", Goal(result, "agv-1"));
        Assert.Equal("A", Goal(result, "agv-2"));
    }

    // ── Determinism: identical inputs ⇒ identical result, even on the continuous axis ────────────────────────
    [Fact]
    public void Is_deterministic_for_identical_inputs()
    {
        var agents = new[] { new CbsAgent("agv-1", "A", "C", 0), new CbsAgent("agv-2", "C", "A", 1) };

        var first = Solve(FourCycle(), agents);
        var second = Solve(FourCycle(), agents);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.SumOfCosts, second.SumOfCosts);
        Assert.Equal(first.NodesExpanded, second.NodesExpanded);
        Assert.Equal(Serialize(first), Serialize(second));
    }

    // ── Floor: an unsolvable swap fails cleanly (never a wrong/colliding answer) ──────────────────────────────
    [Fact]
    public void Unsolvable_swap_fails_cleanly()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B", 1.0).Edge("B", "A", 1.0).Build();
        var agents = new[] { new CbsAgent("agv-1", "A", "B", 0), new CbsAgent("agv-2", "B", "A", 1) };

        var result = Solve(graph, agents, new CbsOptions(HighLevelNodeBudget: 30, Continuous: true));

        Assert.False(result.Solved);
        Assert.Null(result.Paths);
        Assert.True(result.Status is CbsStatus.BudgetExceeded or CbsStatus.NoSolution);
    }

    private static string Serialize(CbsResult r) =>
        r.Paths is null
            ? r.Status.ToString()
            : string.Join(";", r.Paths.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                kv.Key + "=" + string.Join(",", kv.Value.Cells.Select(c => $"{c.Resource.Kind}:{c.Resource.Id}@[{c.Interval.StartMs},{c.Interval.EndMs})"))));
}
