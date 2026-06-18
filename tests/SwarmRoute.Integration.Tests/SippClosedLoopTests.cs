using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// End-to-end validation of the v1 core: the SIPP planner driven through the REAL engine (Coordination +
/// PathPlanning + TrafficControl) and executed by the schedule-faithful executor, A/B-compared against the v0
/// Dijkstra + greedy baseline over the in-memory simulation. The measured wins are:
/// <list type="bullet">
///   <item><b>Collision-free at every density</b> — the schedule-faithful executor never co-locates two agents
///     (hard invariant, holds even where greedy livelocks).</item>
///   <item><b>Converges where greedy livelocks</b> — routing in time lets dense fleets all arrive at densities
///     where the greedy one-CP-per-tick gate degrades to <c>DidNotConverge</c>.</item>
///   <item><b>Orders-of-magnitude fewer replans</b> — SIPP avoids contention up front instead of thrashing
///     reactively (prune-and-replan) at the reservation table.</item>
///   <item><b>Deterministic</b> — same seed ⇒ identical timeline.</item>
/// </list>
/// (Makespan is comparable; at very low density SIPP's strict in-time trailing can add a tick or two, so the
/// win is convergence + smoothness, not raw makespan — see the density sweep.)
/// </summary>
public sealed class SippClosedLoopTests
{
    private static SimulationResultDto Run(int width, int height, int agv, int seed, PlannerKind planner)
    {
        var service = new SimulationService(
            new GridFieldFactory(),
            new FleetLoopDriver(),
            new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);
        return service.RunAsync(new SimulationRequest(width, height, agv, seed, planner)).GetAwaiter().GetResult();
    }

    // ── Hard invariant: SIPP + schedule-faithful is collision-free and converges on solvable densities ──────
    [Theory]
    [InlineData(4, 4, 3)]
    [InlineData(5, 5, 5)]
    [InlineData(6, 6, 8)]
    public void Sipp_is_collision_free_and_converges(int width, int height, int agv)
    {
        var sipp = Run(width, height, agv, seed: 7, PlannerKind.Sipp);

        Assert.Equal(0, sipp.Stats.Collisions);
        Assert.Equal("Completed", sipp.Stats.Status);
        Assert.Equal(agv, sipp.Stats.Arrived);
    }

    // ── The headline win: SIPP converges at a density where Dijkstra + greedy mostly livelocks ──────────────
    [Fact]
    public void Sipp_converges_where_greedy_livelocks()
    {
        const int w = 7, h = 7, agv = 16;
        var seeds = Enumerable.Range(1, 6).ToList();

        var dijkstra = seeds.Select(s => Run(w, h, agv, s, PlannerKind.Dijkstra)).ToList();
        var sipp = seeds.Select(s => Run(w, h, agv, s, PlannerKind.Sipp)).ToList();

        // Never a collision under SIPP, even at this density.
        Assert.All(sipp, r => Assert.Equal(0, r.Stats.Collisions));

        var dConverged = dijkstra.Count(r => r.Stats.Status == "Completed");
        var sConverged = sipp.Count(r => r.Stats.Status == "Completed");

        // SIPP all-arrives strictly more often than the greedy baseline (routes in time vs. lockstep standoffs).
        Assert.True(
            sConverged > dConverged,
            $"SIPP should converge more often than Dijkstra at {w}x{h} agv={agv}: SIPP={sConverged}/{seeds.Count}, Dijkstra={dConverged}/{seeds.Count}.");
    }

    // ── The smoothness win: SIPP avoids contention up front, so it barely replans ───────────────────────────
    [Fact]
    public void Sipp_replans_far_less_than_greedy()
    {
        const int w = 6, h = 6, agv = 8;
        var seeds = Enumerable.Range(1, 6).ToList();

        var dijkstraReplans = seeds.Sum(s => Run(w, h, agv, s, PlannerKind.Dijkstra).Stats.Replans);
        var sippResults = seeds.Select(s => Run(w, h, agv, s, PlannerKind.Sipp)).ToList();
        var sippReplans = sippResults.Sum(r => r.Stats.Replans);

        Assert.All(sippResults, r => Assert.Equal(0, r.Stats.Collisions));

        // Measured ratio is ~150×; assert a conservative 5× to stay robust to incidental variation.
        Assert.True(
            sippReplans * 5 < dijkstraReplans,
            $"SIPP should replan far less than Dijkstra at {w}x{h} agv={agv}: SIPP={sippReplans}, Dijkstra={dijkstraReplans}.");
    }

    // ── Determinism: same seed + planner ⇒ identical timeline ────────────────────────────────────────────────
    [Fact]
    public void Sipp_is_deterministic_for_a_fixed_seed()
    {
        var first = Run(6, 6, 6, seed: 99, PlannerKind.Sipp);
        var second = Run(6, 6, 6, seed: 99, PlannerKind.Sipp);

        Assert.Equal(first.Stats.Ticks, second.Stats.Ticks);
        Assert.Equal(first.Stats.Status, second.Stats.Status);
        Assert.Equal(first.Stats.Arrived, second.Stats.Arrived);
        Assert.Equal(SerializeTimeline(first), SerializeTimeline(second));
    }

    private static string SerializeTimeline(SimulationResultDto result)
        => string.Join(";", result.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions
                .OrderBy(p => p.AgentId, StringComparer.Ordinal)
                .Select(p => $"{p.AgentId}@{p.SiteId}/{p.State}"))));
}
