using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Regression for a "walled-out lone survivor" bug (reported from the visualizer; this is a reproducible
/// continuation instance — seed 66343 with explicit starts — of the same failure). The last unfinished agent
/// (agv-4) sits at r4c3 with its goal r6c3 sealed off by a PARKED vehicle (agv-13 on r5c3, the only approach).
/// Under RHCR, SIPP could still "succeed" each tick by reserving a degenerate single-cell wait-window — which
/// reset StuckTicks every tick, so the parked-gatekeeper step-aside (gated on StuckTicks) NEVER fired and the
/// agent idled out the entire tick budget (DidNotConverge, 15/16, 576 ticks). The fix treats a no-progress
/// wait-window as walled-out so StuckTicks accrues; the gatekeeper then steps the parked blocker aside and the
/// agent reaches its goal. Asserts the run now converges collision-free.
/// </summary>
public sealed class WalledLoneSurvivorTests
{
    [Fact]
    public void Walled_lone_survivor_is_freed_by_the_gatekeeper()
    {
        string[] starts =
        [
            "r1c3","r0c6","r0c1","r2c2","r6c0","r1c5","r3c2","r3c4",
            "r1c1","r2c5","r3c0","r4c5","r5c5","r5c0","r3c1","r5c1"
        ];
        var svc = new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
            new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance);
        var s = svc.RunAsync(new SimulationRequest(
            7, 7, 16, Seed: 66343, Planner: PlannerKind.Sipp,
            HorizonWindowMs: 8, StepAside: true, JointResolver: JointResolverKind.Pibt, Starts: starts)).GetAwaiter().GetResult();

        Assert.Equal("Completed", s.Stats.Status);
        Assert.Equal(16, s.Stats.Arrived);
        Assert.Equal(0, s.Stats.Collisions);
        // Pre-fix this idled out the full 576-tick budget; the gatekeeper-freed path converges in ~57.
        Assert.True(s.Stats.Ticks < 120, $"expected convergence after the walled-out fix, got {s.Stats.Ticks} ticks");
    }
}
