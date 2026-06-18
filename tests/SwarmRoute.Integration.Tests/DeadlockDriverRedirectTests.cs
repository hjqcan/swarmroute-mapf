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
        var caseId = Guid.NewGuid();
        store.PublishRedirect(new RedirectIntent(caseId, "agv-1", AvoidSite));

        // Recovery pump: once the victim has had time to reach the siding, mark it recovered (cycle cleared).
        var calls = 0;
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> recoverTick = _ =>
        {
            calls++;
            if (calls >= 3 && !store.IsRecovered("agv-1", caseId))
                store.MarkRecovered(caseId, "agv-1");
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

    [Fact]
    public async Task Driver_DoesNotRecoverVictim_BeforeItPhysicallyReachesAvoidSite()
    {
        var graph = FakeRoadmapQueryService.Graph(
            ["A", "B", "C", AvoidSite],
            ("A", "B"), ("B", "C"), ("B", AvoidSite), (AvoidSite, "B"), ("B", "A"), ("C", "B"));
        var clock = new ManualFleetClock();
        using var host = CoordinationTestHost.Build(graph, clock: clock, avoidSite: AvoidSite);

        var driver = new FleetLoopDriver();
        var fleet = new[] { new FleetAgentSpec("agv-1", "A", "C", Priority: 0) };

        var store = new FleetRedirectStore();
        var caseId = Guid.NewGuid();
        store.PublishRedirect(new RedirectIntent(caseId, "agv-1", AvoidSite));

        var calls = 0;
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> recoverAfterRedirectStarts = _ =>
        {
            calls++;
            if (calls >= 2)
                store.MarkRecovered(caseId, "agv-1");
            return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
        };

        var result = await driver.RunToCompletionAsync(
            host.Cycle, host.RoadmapId, graph, fleet, maxTicks: 30,
            advanceClock: clock.SetTick, redirects: store, recoverTick: recoverAfterRedirectStarts);

        var firstAvoidTick = result.Frames
            .Where(f => f.Positions.Any(p => p.AgentId == "agv-1" && p.SiteId == AvoidSite))
            .Select(f => f.Tick)
            .First();
        var firstGoalTick = result.Frames
            .Where(f => f.Positions.Any(p => p.AgentId == "agv-1" && p.SiteId == "C"))
            .Select(f => f.Tick)
            .First();

        Assert.True(firstAvoidTick < firstGoalTick);
        Assert.True(result.Stats.Recoveries >= 1);
        Assert.Equal(1, result.Stats.Arrived);
    }

    [Fact]
    public async Task Driver_StillExecutesRedirect_WhenRecoverySignalArrivesBeforeFirstRedirectTick()
    {
        var graph = FakeRoadmapQueryService.Graph(
            ["A", "B", "C", AvoidSite],
            ("A", "B"), ("B", "C"), ("B", AvoidSite), (AvoidSite, "B"), ("B", "A"), ("C", "B"));
        var clock = new ManualFleetClock();
        using var host = CoordinationTestHost.Build(graph, clock: clock, avoidSite: AvoidSite);

        var driver = new FleetLoopDriver();
        var fleet = new[] { new FleetAgentSpec("agv-1", "A", "C", Priority: 0) };

        var store = new FleetRedirectStore();
        var caseId = Guid.NewGuid();
        store.PublishRedirect(new RedirectIntent(caseId, "agv-1", AvoidSite));

        Func<CancellationToken, Task<IReadOnlyCollection<string>>> recoverBeforeRedirectIsConsumed = _ =>
        {
            store.MarkRecovered(caseId, "agv-1");
            return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
        };

        var result = await driver.RunToCompletionAsync(
            host.Cycle, host.RoadmapId, graph, fleet, maxTicks: 30,
            advanceClock: clock.SetTick, redirects: store, recoverTick: recoverBeforeRedirectIsConsumed);

        Assert.Contains(
            result.Frames,
            f => f.Positions.Any(p => p.AgentId == "agv-1" && p.SiteId == AvoidSite));
        Assert.False(store.TryGetActiveRedirect("agv-1", out _));
        Assert.True(store.IsRecovered("agv-1", caseId));
        Assert.Equal("C", result.PerAgentRoute["agv-1"][^1]);
        Assert.Equal(1, result.Stats.Arrived);
    }

    [Fact]
    public void RedirectStore_DoesNotLetOldTerminalEventClearNewCase()
    {
        var store = new FleetRedirectStore();
        var oldCaseId = Guid.NewGuid();
        var newCaseId = Guid.NewGuid();

        store.PublishRedirect(new RedirectIntent(oldCaseId, "agv-1", "old-avoid"));
        store.PublishRedirect(new RedirectIntent(newCaseId, "agv-1", "new-avoid"));

        store.MarkRecovered(oldCaseId, "agv-1");

        Assert.True(store.TryGetActiveRedirect("agv-1", out var active));
        Assert.Equal(newCaseId, active.CaseId);
        Assert.Equal("new-avoid", active.AvoidSiteId);
        Assert.True(store.IsRecovered("agv-1", oldCaseId));
        Assert.False(store.IsRecovered("agv-1", newCaseId));
    }
}
