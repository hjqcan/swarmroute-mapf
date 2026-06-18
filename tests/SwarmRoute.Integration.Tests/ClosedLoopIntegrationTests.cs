using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Simulation.Application;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// CLOSED-LOOP test: drives the REAL Coordination + PathPlanning + TrafficControl services over MANY ticks,
/// simulating agents moving along their reserved paths and releasing resources behind them, until every agent
/// reaches its goal. This is the actual lifelong control loop closing — not a single cycle. Asserts:
/// <list type="bullet">
///   <item><b>Liveness</b>: every agent reaches its goal within a bounded number of ticks (no livelock/deadlock).</item>
///   <item><b>Safety (I1)</b>: no two agents ever occupy the same control point on the same tick (no collision).</item>
///   <item><b>No leak (I6)</b>: the reservation table holds zero leases once the fleet is idle.</item>
/// </list>
/// v0 model: whole-path spatial reservation (the planner is space-only); the loop serialises contenders and
/// they progress as holders release behind them.
/// <para>
/// The run-to-completion loop itself now lives in production code — <see cref="FleetLoopDriver"/> (the
/// Simulation API drives the same loop for its replay timeline). These tests exercise that extracted driver and
/// assert the liveness / safety / no-leak invariants over it (DRY: one validated loop, two callers).
/// </para>
/// </summary>
public sealed class ClosedLoopIntegrationTests
{
    private static readonly FleetLoopDriver Driver = new();

    /// <summary>
    /// Runs the extracted closed-loop driver to completion over the host's real cycle, then asserts the core
    /// invariants: every agent arrived (liveness) and there were zero collisions (safety). The driver throws if
    /// it fails to converge within <paramref name="maxTicks"/> or detects a collision, so reaching here already
    /// means the loop closed safely.
    /// </summary>
    private static async Task<FleetLoopResult> RunToCompletionAsync(
        CoordinationTestHost host, IReadOnlyList<FleetAgentSpec> fleet, int maxTicks)
    {
        var result = await Driver.RunToCompletionAsync(host.Cycle, host.RoadmapId, host.Graph, fleet, maxTicks);

        Assert.Equal(0, result.Stats.Collisions);
        Assert.Equal(fleet.Count, result.Stats.Arrived);
        return result;
    }

    private static void AssertNoLeasesLeak(CoordinationTestHost host)
    {
        var snapshot = host.Services.GetRequiredService<ITrafficControlSnapshotProvider>().GetSnapshot();
        Assert.True(
            snapshot.Owns.Count == 0,
            $"Reservation table leaked {snapshot.Owns.Count} lease(s) after fleet idle: " +
            string.Join(", ", snapshot.Owns.Select(o => $"{o.AgentId}:{o.Resource.Kind}/{o.Resource.Id}")));
    }

    // ── Closed loop A: independent agents run in PARALLEL to completion ──────────────────────────────────
    [Fact]
    public async Task ClosedLoop_IndependentAgents_AllReachGoals_InParallel_NoCollision_NoLeak()
    {
        // Two disjoint corridors on one chain A-B-C | D-E-F (no shared resource).
        using var host = CoordinationTestHost.Build(FakeRoadmapQueryService.Chain("A", "B", "C", "D", "E", "F"));
        var fleet = new List<FleetAgentSpec>
        {
            new("agv-1", "A", "C", Priority: 0),
            new("agv-2", "D", "F", Priority: 1),
        };

        var outcome = await RunToCompletionAsync(host, fleet, maxTicks: 20);

        Assert.Equal(fleet.Count, outcome.Stats.Arrived);
        Assert.True(outcome.MaxConcurrentEnRoute >= 2, "independent agents should move concurrently");
        AssertNoLeasesLeak(host);
    }

    // ── Closed loop B: two agents CROSS at an intersection — serialised, both still complete ─────────────
    [Fact]
    public async Task ClosedLoop_IntersectionCrossing_SerialisedThroughCentre_BothReachGoals_NoCollision_NoLeak()
    {
        // "+" intersection sharing the centre C0. agv-1 W→E and agv-2 N→S both need C0 → the table serialises
        // them; as the holder clears C0 the other proceeds. They end at distinct points (E, S) so neither blocks
        // the other's goal. The loop must still close.
        var graph = FakeRoadmapQueryService.Graph(
            ["W", "E", "N", "S", "C0"],
            ("W", "C0"), ("C0", "E"), ("N", "C0"), ("C0", "S"));
        using var host = CoordinationTestHost.Build(graph);

        var fleet = new List<FleetAgentSpec>
        {
            new("agv-1", "W", "E", Priority: 0),
            new("agv-2", "N", "S", Priority: 1),
        };

        var outcome = await RunToCompletionAsync(host, fleet, maxTicks: 30);

        Assert.Equal(fleet.Count, outcome.Stats.Arrived);
        AssertNoLeasesLeak(host);
    }

    // ── Closed loop C: a denser fleet (4 agents, two crossing pairs) still converges ─────────────────────
    [Fact]
    public async Task ClosedLoop_FourAgents_TwoCrossingPairs_AllReachGoals_NoCollision_NoLeak()
    {
        // Two stacked intersections sharing centres C0 and C1:  W-C0-E , N-C0-C1-Sx , and W2-C1-E2.
        var graph = FakeRoadmapQueryService.Graph(
            ["W", "E", "N", "C0", "C1", "Sx", "W2", "E2"],
            ("W", "C0"), ("C0", "E"), ("N", "C0"), ("C0", "C1"), ("C1", "Sx"), ("W2", "C1"), ("C1", "E2"));
        using var host = CoordinationTestHost.Build(graph);

        var fleet = new List<FleetAgentSpec>
        {
            new("agv-1", "W", "E", Priority: 0),    // through C0
            new("agv-2", "N", "Sx", Priority: 1),   // through C0 then C1
            new("agv-3", "W2", "E2", Priority: 2),  // through C1
            new("agv-4", "E", "W", Priority: 3),    // through C0, opposite agv-1 (ends at W, agv-1's start — free by then)
        };

        var outcome = await RunToCompletionAsync(host, fleet, maxTicks: 60);

        Assert.Equal(fleet.Count, outcome.Stats.Arrived);
        AssertNoLeasesLeak(host);
    }
}
