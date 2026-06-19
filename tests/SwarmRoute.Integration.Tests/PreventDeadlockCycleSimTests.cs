using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// The grant-time deadlock-prevention A/B switch (<see cref="SimulationRequest.PreventDeadlockCycles"/>) plumbed
/// end-to-end through the engine factory. These tests lock in two truths:
/// <list type="bullet">
///   <item><b>Plumbed and safe</b> — flipping prevention on never introduces a collision and never regresses
///     convergence (the run still completes whenever the prevention-off run does).</item>
///   <item><b>Inert in the <c>FleetLoopDriver</c> simulation</b> — the on and off timelines are IDENTICAL, because
///     this sim's deadlocks are <em>physical</em> standoffs (agents hold granted, interval-exclusive reservations
///     and block each other — "not a RAG cycle"), and SIPP's interval-exclusive plans do not form reservation-
///     level wait-for cycles. So there is nothing for <c>WouldCloseCycle</c> to avert here.</item>
/// </list>
/// The feature's actual averting behaviour is exercised where reservation cycles really form — the seeded ring in
/// <see cref="CyclePreventionIntegrationTests"/> and the aggregate/detector unit tests — not in this executor sim.
/// </summary>
public sealed class PreventDeadlockCycleSimTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed, PlannerKind planner, bool prevent)
        => new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, planner, Starts: null,
                HorizonWindowMs: long.MaxValue, StepAside: false, PreventDeadlockCycles: prevent))
            .GetAwaiter().GetResult();

    [Theory]
    [InlineData(PlannerKind.Sipp, 7, 7, 16)]
    [InlineData(PlannerKind.Sipp, 10, 8, 16)]
    [InlineData(PlannerKind.Dijkstra, 6, 6, 8)]
    public void Prevention_toggle_is_collision_free_and_does_not_regress(PlannerKind planner, int w, int h, int agv)
    {
        foreach (var seed in Enumerable.Range(1, 6))
        {
            var off = Run(w, h, agv, seed, planner, prevent: false);
            var on = Run(w, h, agv, seed, planner, prevent: true);

            Assert.Equal(0, off.Stats.Collisions);
            Assert.Equal(0, on.Stats.Collisions); // prevention never introduces a collision
            // Never converges LESS with prevention on (it is at worst inert here).
            if (off.Stats.Status == "Completed")
                Assert.Equal("Completed", on.Stats.Status);
        }
    }

    [Fact]
    public void Prevention_is_inert_in_the_executor_sim_identical_timeline()
    {
        // Characterisation: no reservation-level wait-for cycle forms in the FleetLoopDriver sim, so prevention
        // changes nothing — the timelines are byte-identical. (If a future change makes reservation cycles form
        // here, this will fail and should be revisited — that would mean prevention is now engaging in the sim.)
        var off = Run(7, 7, 16, seed: 3, planner: PlannerKind.Sipp, prevent: false);
        var on = Run(7, 7, 16, seed: 3, planner: PlannerKind.Sipp, prevent: true);

        Assert.Equal(SerializeTimeline(off), SerializeTimeline(on));
    }

    private static string SerializeTimeline(SimulationResultDto result)
        => string.Join(";", result.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions
                .OrderBy(p => p.AgentId, StringComparer.Ordinal)
                .Select(p => $"{p.AgentId}@{p.SiteId}/{p.State}"))));
}
