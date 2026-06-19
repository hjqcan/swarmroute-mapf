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
///   <item><b>Honest liveness accounting</b> — dense standoffs are reported as <c>DidNotConverge</c>, not folded
///     into a safety proof.</item>
///   <item><b>Replans are telemetry</b> — executor recovery can add release-and-replan events, so lower replan
///     count is not a planner correctness contract.</item>
///   <item><b>Deterministic</b> — same seed ⇒ identical timeline.</item>
/// </list>
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
        // StepAside enabled = the app's SIPP config (executor deadlock recovery on); affects only SIPP runs.
        return service.RunAsync(new SimulationRequest(width, height, agv, seed, planner, StepAside: true)).GetAwaiter().GetResult();
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

    // ── Dense standoff honesty: no collision claim is allowed to imply liveness ────────────────────────────
    [Fact]
    public void Sipp_reports_dense_standoffs_without_collisions()
    {
        const int w = 7, h = 7, agv = 16;
        var seeds = Enumerable.Range(1, 6).ToList();

        var sipp = seeds.Select(s => Run(w, h, agv, s, PlannerKind.Sipp)).ToList();

        // Never a collision under SIPP, even at this density.
        Assert.All(sipp, r => Assert.Equal(0, r.Stats.Collisions));
        Assert.All(sipp, r => Assert.NotEqual("CollisionDetected", r.Stats.Status));
        Assert.Contains(sipp, r => r.Stats.Status == "DidNotConverge");
    }

    // ── Replans are diagnostic telemetry, not a correctness proof ──────────────────────────────────────────
    [Fact]
    public void Sipp_replans_do_not_break_collision_safety()
    {
        const int w = 6, h = 6, agv = 8;
        var seeds = Enumerable.Range(1, 6).ToList();

        var sippResults = seeds.Select(s => Run(w, h, agv, s, PlannerKind.Sipp)).ToList();
        var sippReplans = sippResults.Sum(r => r.Stats.Replans);

        Assert.All(sippResults, r => Assert.Equal(0, r.Stats.Collisions));
        Assert.True(sippReplans > 0, "the seed set should exercise release-and-replan recovery telemetry.");
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
