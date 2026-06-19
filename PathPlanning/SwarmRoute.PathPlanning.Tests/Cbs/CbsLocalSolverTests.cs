using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Cbs;
using SwarmRoute.PathPlanning.Tests.TestSupport;
using SwarmRoute.SpatioTemporal.Kernel;
using Xunit;

namespace SwarmRoute.PathPlanning.Tests.Cbs;

/// <summary>
/// Unit tests for <see cref="CbsLocalSolver"/>: it cracks a head-on standoff greedy PIBT struggles with (by
/// routing the two agents onto opposite arcs), is deterministic, and degrades to a clean failure (never a wrong
/// or colliding answer) on the budget / infeasible / unsolvable edges.
/// </summary>
public sealed class CbsLocalSolverTests
{
    // A 4-cycle A-B-C-D (bidirectional): two opposite arcs between any pair, so head-on agents can be routed apart.
    private static RoadmapGraph FourCycle() => new RoadmapGraphBuilder()
        .Edge("A", "B").Edge("B", "A")
        .Edge("B", "C").Edge("C", "B")
        .Edge("C", "D").Edge("D", "C")
        .Edge("D", "A").Edge("A", "D")
        .Build();

    private static RoadmapGraph Line(params string[] sites)
    {
        var b = new RoadmapGraphBuilder();
        for (var i = 0; i < sites.Length - 1; i++)
            b.Edge(sites[i], sites[i + 1]).Edge(sites[i + 1], sites[i]);
        return b.Build();
    }

    private static CbsResult Solve(
        RoadmapGraph graph,
        IReadOnlyList<CbsAgent> agents,
        CbsOptions? options = null,
        IReadOnlySet<ResourceRef>? blockedResources = null)
        => new CbsLocalSolver(options).Solve(graph, agents, new FakeReservationView(), releaseTick: 0, blockedResources);

    private static string GoalReached(CbsResult r, string id)
    {
        var cells = r.Paths![id].Cells;
        var last = cells[^1];
        Assert.Equal(ResourceKind.CP, last.Resource.Kind); // terminal cell is always the parked CP
        return last.Resource.Id;
    }

    // ── Headline: a head-on standoff is solved by routing the two agents onto opposite arcs ───────────────────
    [Fact]
    public void Head_on_on_a_cycle_is_solved_by_routing_apart()
    {
        var agents = new[] { new CbsAgent("agv-1", "A", "C", 0), new CbsAgent("agv-2", "C", "A", 1) };

        var result = Solve(FourCycle(), agents);

        Assert.True(result.Solved, $"expected Solved, got {result.Status}: {result.FailureReason}");
        Assert.Equal("C", GoalReached(result, "agv-1"));
        Assert.Equal("A", GoalReached(result, "agv-2"));
    }

    // ── A clean root (agents that never cross) solves with no branching ───────────────────────────────────────
    [Fact]
    public void Non_crossing_agents_solve_at_the_root()
    {
        var agents = new[] { new CbsAgent("agv-1", "A", "B", 0), new CbsAgent("agv-2", "C", "D", 1) };

        var result = Solve(FourCycle(), agents);

        Assert.True(result.Solved);
        Assert.Equal(1, result.NodesExpanded); // root was already conflict-free
    }

    // ── Following (one tick apart, same direction) is NOT a conflict — half-open intervals just touch ─────────
    [Fact]
    public void Following_is_not_a_conflict()
    {
        // agv-1 trails agv-2 down a line; they never co-occupy a CP at the same tick.
        var agents = new[] { new CbsAgent("agv-1", "A", "D", 0), new CbsAgent("agv-2", "B", "E", 1) };

        var result = Solve(Line("A", "B", "C", "D", "E"), agents);

        Assert.True(result.Solved);
        Assert.Equal(1, result.NodesExpanded);
    }

    // ── Single-agent cluster is trivial ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void Single_agent_is_trivially_solved()
    {
        var result = Solve(FourCycle(), new[] { new CbsAgent("agv-1", "A", "C", 0) });

        Assert.True(result.Solved);
        Assert.Equal(1, result.NodesExpanded);
        Assert.Equal("C", GoalReached(result, "agv-1"));
    }

    // ── Executor physical blockers are static blacklist constraints for every CBS low-level search ─────────────
    [Fact]
    public void Static_blocked_resources_are_avoided_by_low_level_search()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B").Edge("B", "C")
            .Edge("A", "D").Edge("D", "C")
            .Build();

        var result = Solve(
            graph,
            new[] { new CbsAgent("agv-1", "A", "C", 0) },
            blockedResources: new HashSet<ResourceRef> { RoadmapGraph.SiteRef("B") });

        Assert.True(result.Solved, $"expected Solved, got {result.Status}: {result.FailureReason}");
        Assert.Equal(new[] { "A", "D", "C" }, CpRoute(result, "agv-1"));
    }

    // ── RHCR: CBS low-level plans only to the configured window frontier, not all the way to the goal ───────────
    [Fact]
    public void Time_horizon_ticks_limit_low_level_search_to_a_frontier()
    {
        var result = Solve(
            Line("A", "B", "C", "D"),
            new[] { new CbsAgent("agv-1", "A", "D", 0) },
            new CbsOptions(TimeHorizonTicks: 1));

        Assert.True(result.Solved, $"expected Solved, got {result.Status}: {result.FailureReason}");
        Assert.Equal(new[] { "A", "B" }, CpRoute(result, "agv-1"));
    }

    // ── Determinism: identical inputs ⇒ identical result (paths, cost, nodes expanded) ───────────────────────
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

    // ── Infeasible: an agent with no path at all is reported cleanly, not looped on ──────────────────────────
    [Fact]
    public void Unreachable_goal_returns_infeasible()
    {
        // Directed A→B only; agent starts at B with no out-edge to A.
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();

        var result = Solve(graph, new[] { new CbsAgent("agv-1", "B", "A", 0) });

        Assert.Equal(CbsStatus.Infeasible, result.Status);
        Assert.Null(result.Paths);
    }

    // ── Floor: an unsolvable swap on a 2-node graph exhausts the node budget cleanly (no throw, no paths) ─────
    [Fact]
    public void Unsolvable_swap_fails_cleanly_within_budget()
    {
        var graph = Line("A", "B"); // a single edge — two agents cannot swap ends
        var agents = new[] { new CbsAgent("agv-1", "A", "B", 0), new CbsAgent("agv-2", "B", "A", 1) };

        var result = Solve(graph, agents, new CbsOptions(HighLevelNodeBudget: 20));

        Assert.False(result.Solved);
        Assert.Null(result.Paths);
        Assert.True(result.Status is CbsStatus.BudgetExceeded or CbsStatus.NoSolution);
    }

    // ── Cluster larger than the cap declines immediately ──────────────────────────────────────────────────────
    [Fact]
    public void Cluster_over_max_agents_declines()
    {
        var agents = new[]
        {
            new CbsAgent("agv-1", "A", "C", 0),
            new CbsAgent("agv-2", "C", "A", 1),
            new CbsAgent("agv-3", "B", "D", 2),
        };

        var result = Solve(FourCycle(), agents, new CbsOptions(MaxAgents: 2));

        Assert.Equal(CbsStatus.BudgetExceeded, result.Status);
        Assert.Equal(0, result.NodesExpanded);
    }

    private static string Serialize(CbsResult r)
    {
        if (r.Paths is null)
            return $"{r.Status}";
        return string.Join(";", r.Paths
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + string.Join(",", kv.Value.Cells
                .Select(c => $"{c.Resource.Kind}:{c.Resource.Id}@[{c.Interval.StartMs},{c.Interval.EndMs})"))));
    }

    private static IReadOnlyList<string> CpRoute(CbsResult r, string id)
        => r.Paths![id].Cells
            .Where(c => c.Resource.Kind == ResourceKind.CP)
            .Select(c => c.Resource.Id)
            .ToList();
}
