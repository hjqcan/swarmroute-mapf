using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Regression for a dense SIPP physical standoff (reported from the visualizer): 12 AGVs on a 10×8 grid,
/// seed 79948, formed a stalled chain in the centre that the RAG deadlock detector can't see (the agents hold,
/// not wait for, their reservations). The schedule-faithful executor's stall-triggered re-route breaks it —
/// after a grace window an agent stalled past its planned entry tick drops its reservation and re-plans, so the
/// chain unwinds. Asserts the fleet now converges, collision-free.
/// </summary>
public sealed class SippDenseStandoffTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed, PlannerKind p)
        => new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, p)).GetAwaiter().GetResult();

    [Theory]
    [InlineData(79948)]
    [InlineData(99940)] // the canonical deadlock seed from the UI work
    public void Sipp_breaks_a_dense_physical_standoff(int seed)
    {
        var s = Run(10, 8, 12, seed, PlannerKind.Sipp);

        Assert.Equal(0, s.Stats.Collisions);
        Assert.Equal("Completed", s.Stats.Status);
        Assert.Equal(12, s.Stats.Arrived);
    }
}
