using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Regression for a standoff-DETECTION gap (reported from the visualizer; reproducible continuation instance
/// seed 59359 + starts). Two agents (agv-7 r4c0 ↔ agv-12 r3c0) want to trade cells in the column-0 corridor —
/// a head-on swap with free side cells, trivially solvable by stepping one aside. But one member had dropped to
/// the walled-out PENDING state, and the old StuckClusterDetector keyed off the en-route reservation flag, so the
/// pair never formed a cluster and the joint resolver (PIBT/local CBS) never saw it: it idled out the whole tick
/// budget (DidNotConverge). The detector now keys off INTENT + pose + a unified stuckness counter, so the swap is
/// detected regardless of reservation state and handed to the cluster owner. Local CBS — the complete owner —
/// then resolves it. (Greedy PIBT still mis-resolves this particular topology, which is exactly why CBS exists;
/// the detection fix is what lets CBS see it at all.)
/// </summary>
public sealed class SwapStandoffDetectionTests
{
    [Fact]
    public void Cbs_resolves_a_corridor_swap_once_detection_includes_pending_agents()
    {
        string[] starts =
        [
            "r0c3","r1c0","r5c5","r2c0","r2c4","r5c0","r2c2","r4c4",
            "r0c1","r4c2","r3c3","r1c3","r6c5","r6c2","r6c6","r1c5"
        ];
        var svc = new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
            new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance);
        var s = svc.RunAsync(new SimulationRequest(
            7, 7, 16, Seed: 59359, Planner: PlannerKind.Sipp,
            HorizonWindowMs: 8, StepAside: true, JointResolver: JointResolverKind.Cbs, Starts: starts)).GetAwaiter().GetResult();

        Assert.Equal("Completed", s.Stats.Status);
        Assert.Equal(16, s.Stats.Arrived);
        Assert.Equal(0, s.Stats.Collisions);
        // Pre-fix this idled out the full 576-tick budget; detected + CBS-solved it converges in ~57.
        Assert.True(s.Stats.Ticks < 150, $"expected convergence after the detection fix, got {s.Stats.Ticks} ticks");
    }
}
