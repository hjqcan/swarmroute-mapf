using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab — Robust Execution) The ADG/TPG-following executor driven through the REAL engine. On a dense,
/// tightly-coupled run it injects a delay into the most brittle AGV and demonstrates the headline contrast: the naive
/// timestamp replay collides, while the dependency-following replay stays collision-free (paying makespan instead).
/// </summary>
public sealed class DelayResilienceClosedLoopTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed) =>
        new SimulationService(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp)).GetAwaiter().GetResult();

    [Fact]
    public void Delay_resilience_is_internally_consistent_when_present()
    {
        var dr = Run(8, 8, 16, 5).DelayResilience;

        // A contended run shares cells, so the what-if applies; it must always be self-consistent.
        Assert.NotNull(dr);
        Assert.True(dr!.DelayTicks >= 1);
        Assert.False(string.IsNullOrEmpty(dr.DelayedAgent));
        Assert.Equal(0, dr.AdgCollisions);                 // the dependency-following executor never collides
        Assert.True(dr.NaiveCollisions >= 0);
        Assert.True(dr.AdgMakespanInflation >= 0);
        Assert.True(dr.PlannedMakespan >= 0);
    }

    [Fact]
    public void The_adg_executor_strictly_beats_naive_under_an_injected_delay()
    {
        // On a dense grid the tightest handoff is brittle: a single injected delay breaks the naive timestamp replay
        // (≥1 collision), while following the dependency graph absorbs it with zero collisions.
        var dr = Run(8, 8, 16, 5).DelayResilience!;

        Assert.True(dr.NaiveCollisions > dr.AdgCollisions,
            $"the ADG executor should collide less than naive (naive {dr.NaiveCollisions} vs ADG {dr.AdgCollisions}).");
        Assert.Equal(0, dr.AdgCollisions);
    }

    [Fact]
    public void A_sparse_uncontended_run_has_no_delay_scenario()
    {
        // One AGV alone never shares a cell, so there is no handoff to perturb.
        var dr = Run(8, 8, 1, 1).DelayResilience;

        Assert.Null(dr);
    }
}
