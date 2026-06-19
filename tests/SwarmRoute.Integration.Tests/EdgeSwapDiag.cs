using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Edge-collision safety: under schedule-faithful execution NO two AGVs may EVER pass through each other (swap
/// cells across one lane) — distinct end cells, so the CP-distinctness check alone can't see it. The resolver
/// forbids the swap; any liveness recovery must release and re-plan through TrafficControl before moving.
/// Reported from the visualizer (10×8/16, seed 22816: agv-7 ↔ agv-15 crossed). Scans the whole timeline for any
/// swap across a density sweep.
/// </summary>
public sealed class EdgeCollisionSafetyTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed, PlannerKind p, bool stepAside = false)
        => new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, p, StepAside: stepAside)).GetAwaiter().GetResult();

    private static int CountSwaps(SimulationResultDto result)
    {
        var frames = result.Timeline.Frames;
        var swaps = 0;
        for (var t = 0; t + 1 < frames.Count; t++)
        {
            var a = frames[t].Positions.ToDictionary(p => p.AgentId, p => p.SiteId);
            var b = frames[t + 1].Positions.ToDictionary(p => p.AgentId, p => p.SiteId);
            var ids = a.Keys.ToList();
            for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                    if (a[ids[i]] != a[ids[j]] && a[ids[i]] == b[ids[j]] && a[ids[j]] == b[ids[i]])
                        swaps++;
        }
        return swaps;
    }

    [Fact]
    public void Reported_seed_22816_has_no_swap_without_physical_sidestep()
    {
        var s = Run(10, 8, 16, 22816, PlannerKind.Sipp, stepAside: true); // app config (parked-gatekeeper recovery on)
        Assert.Equal(0, CountSwaps(s));
        Assert.Equal(0, s.Stats.Collisions);
        Assert.NotEqual("CollisionDetected", s.Stats.Status);
    }

    [Theory]
    [InlineData(10, 8, 16)]
    [InlineData(8, 8, 12)]
    [InlineData(6, 6, 8)]
    public void No_edge_swaps_across_a_density_sweep(int w, int h, int agv)
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var s = Run(w, h, agv, seed, PlannerKind.Sipp);
            Assert.Equal(0, CountSwaps(s));   // never pass through another AGV
            Assert.Equal(0, s.Stats.Collisions);
        }
    }
}
