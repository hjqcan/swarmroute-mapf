using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwarmRoute.Coordination.Application.Deadlock;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// SIM-DRIVER side of the closed loop: proves the <see cref="FleetLoopDriver"/> (the v0 execution layer)
/// actually enacts a deadlock resolution — it redirects a victim from its current control point to the
/// avoidance site, holds it there, then restores the original goal once recovery fires — driving the REAL
/// Coordination cycle + TrafficControl reservation the whole time, with no collision. The redirect/recovery
/// signals are injected through the same <see cref="IFleetRedirectQuery"/> / recover-pump seams the Deadlock
/// consumer + recovery service populate in production.
/// </summary>
public sealed class DeadlockDriverRedirectTests
{
    private const string AvoidSite = "V";

    [Fact]
    public async Task Driver_RedirectsVictimToAvoidSite_ThenRestoresGoal_OnRecovery()
    {
        // A→B→C corridor with an avoidance siding V hanging off B (both directions where the victim travels).
        var graph = FakeRoadmapQueryService.Graph(
            ["A", "B", "C", AvoidSite],
            ("A", "B"), ("B", "C"), ("B", AvoidSite), (AvoidSite, "B"), ("B", "A"), ("C", "B"));
        var clock = new ManualFleetClock();
        using var host = CoordinationTestHost.Build(graph, clock: clock, avoidSite: AvoidSite);

        var driver = new FleetLoopDriver();
        var fleet = new[] { new FleetAgentSpec("agv-1", "A", "C", Priority: 0) };

        // Redirect store seeded with an active redirect agv-1 → V (what the Deadlock consumer would publish).
        var store = new FleetRedirectStore();
        store.PublishRedirect(new RedirectIntent(Guid.NewGuid(), "agv-1", AvoidSite));

        // Recovery pump: once the victim has had time to reach the siding, mark it recovered (cycle cleared).
        var calls = 0;
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> recoverTick = _ =>
        {
            calls++;
            if (calls >= 3 && !store.IsRecovered("agv-1"))
                store.MarkRecovered("agv-1");
            return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
        };

        var result = await driver.RunToCompletionAsync(
            host.Cycle, host.RoadmapId, graph, fleet, maxTicks: 30,
            advanceClock: clock.SetTick, redirects: store, recoverTick: recoverTick);

        Assert.Equal(1, result.Stats.Arrived);                 // reached its REAL goal C
        Assert.Equal(0, result.Stats.Collisions);
        Assert.True(result.Stats.Redirects >= 1, "the victim should have been redirected at least once");
        Assert.True(result.Stats.Recoveries >= 1, "the victim should have been recovered once cleared");
        Assert.Contains(
            result.Frames,
            f => f.Positions.Any(p => p.AgentId == "agv-1" && p.SiteId == AvoidSite));
        Assert.Equal("C", result.PerAgentRoute["agv-1"][^1]);  // final reserved route ends at the real goal
    }
}
