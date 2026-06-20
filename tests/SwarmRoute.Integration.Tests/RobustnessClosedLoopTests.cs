using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab — Robust Execution) The ADG/TPG robustness summary driven through the REAL engine: present on
/// every run, internally consistent, and — on a dense, contended run — surfacing real cell-handoff dependencies.
/// </summary>
public sealed class RobustnessClosedLoopTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed) =>
        new SimulationService(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp)).GetAwaiter().GetResult();

    [Fact]
    public void Robustness_is_present_and_internally_consistent()
    {
        var r = Run(7, 7, 16, 3);

        Assert.NotNull(r.Robustness);
        var rb = r.Robustness!;
        Assert.True(rb.HandoffDependencies >= 0);
        Assert.InRange(rb.TightHandoffs, 0, rb.HandoffDependencies);   // tight handoffs are a subset
        Assert.True(rb.MinSlackTicks >= 0);                            // a collision-free plan has no negative slack
        Assert.True(rb.TightestCells.Count <= 6);
        if (rb.HandoffDependencies > 0)
            Assert.NotEmpty(rb.TightestCells);                        // shared cells ⇒ a tightest one exists
    }

    [Fact]
    public void A_contended_run_has_more_handoff_dependencies_than_a_sparse_one()
    {
        // More AGVs sharing the same grid ⇒ more cells are handed off between vehicles (more execution coupling).
        var sparse = Run(8, 8, 3, 5).Robustness!.HandoffDependencies;
        var dense = Run(8, 8, 16, 5).Robustness!.HandoffDependencies;

        Assert.True(dense > sparse, $"a denser fleet should imply more handoff dependencies (sparse {sparse} → dense {dense}).");
    }
}
