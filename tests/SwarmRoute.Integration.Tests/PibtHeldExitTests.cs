using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Regression for a PIBT starvation bug (reported from the visualizer, seed 64253, 7×7, 16 AGVs, RHCR-8): a lone
/// survivor of a dissolved congestion cluster (agv-15) was driven to a cell whose only goal-improving neighbour
/// was a PARKED vehicle, where PIBT's no-retreat candidate ordering prefers "stay" over a longer detour — so it
/// idled out the ENTIRE episode budget (2×VertexCount = 98 ticks) before re-planning, inflating the makespan
/// from ~34 to 117 even though its detour was clear the whole time. The fix hands a PIBT agent held for
/// <c>pibtHeldExitThreshold</c> consecutive ticks back to prioritized-SIPP, which routes around the parked
/// obstacle in one re-plan. The run still converges collision-free; it just no longer starves one agent.
/// </summary>
public sealed class PibtHeldExitTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed, long? window, bool usePibt)
        => new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp,
                HorizonWindowMs: window ?? long.MaxValue, StepAside: true, UsePibt: usePibt))
            .GetAwaiter().GetResult();

    [Fact]
    public void Pibt_does_not_starve_a_lone_agent_behind_parked_vehicles()
    {
        var s = Run(7, 7, 16, seed: 64253, window: 8, usePibt: true);

        Assert.Equal("Completed", s.Stats.Status);
        Assert.Equal(16, s.Stats.Arrived);
        Assert.Equal(0, s.Stats.Collisions);
        // Pre-fix this run took 117 ticks (one agent idled ~97). The detour is ~34; assert well under the old
        // episode-budget-bound stall so the starvation can't silently regress.
        Assert.True(s.Stats.Ticks < 60, $"expected a short makespan after the held-exit fix, got {s.Stats.Ticks} ticks");
    }
}
