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
    /// Builds a test host whose coordination cycle reads a tick-driven <see cref="ManualFleetClock"/> (the same
    /// wiring the Simulation API uses), so reservation intervals and execution ticks share one axis. The driver
    /// advances this clock each tick.
    /// </summary>
    private static CoordinationTestHost BuildHost(RoadmapGraph graph, out ManualFleetClock clock)
    {
        clock = new ManualFleetClock();
        return CoordinationTestHost.Build(graph, clock: clock);
    }

    /// <summary>
    /// Runs the extracted closed-loop driver to completion over the host's real cycle, then asserts the core
    /// invariants: every agent arrived (liveness) and there were zero collisions (safety). The executor's
    /// right-of-way gate makes collisions impossible by construction, so reaching here means the loop closed
    /// safely and live.
    /// </summary>
    private static async Task<FleetLoopResult> RunToCompletionAsync(
        CoordinationTestHost host, ManualFleetClock clock, IReadOnlyList<FleetAgentSpec> fleet, int maxTicks)
    {
        var result = await Driver.RunToCompletionAsync(
            host.Cycle, host.RoadmapId, host.Graph, fleet, maxTicks, advanceClock: clock.SetTick);

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
        using var host = BuildHost(FakeRoadmapQueryService.Chain("A", "B", "C", "D", "E", "F"), out var clock);
        var fleet = new List<FleetAgentSpec>
        {
            new("agv-1", "A", "C", Priority: 0),
            new("agv-2", "D", "F", Priority: 1),
        };

        var outcome = await RunToCompletionAsync(host, clock, fleet, maxTicks: 20);

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
        using var host = BuildHost(graph, out var clock);

        var fleet = new List<FleetAgentSpec>
        {
            new("agv-1", "W", "E", Priority: 0),
            new("agv-2", "N", "S", Priority: 1),
        };

        var outcome = await RunToCompletionAsync(host, clock, fleet, maxTicks: 30);

        Assert.Equal(fleet.Count, outcome.Stats.Arrived);
        AssertNoLeasesLeak(host);
    }

    // ── Closed loop C: a denser fleet (4 agents rotating around a grid's perimeter) still converges ──────
    [Fact]
    public async Task ClosedLoop_FourAgents_PerimeterRotation_AllReachGoals_NoCollision_NoLeak()
    {
        // Four agents rotate one quarter-turn around a 4×4 grid's perimeter (a 4-cycle: each agent's start is
        // the previous agent's goal). Each follows one edge of the square through otherwise-empty cells, so the
        // shared corners are used at different ticks and there are no head-on swaps — a scenario the v0
        // shortest-path planner can solve under the executor's right-of-way gate. (The earlier hand-built
        // intersection had agv-1 W→E and agv-4 E→W swapping through a single vertex with no siding: genuinely
        // unsolvable for a shortest-path planner, so it is not a fair convergence test.)
        var graph = new GridFieldFactory().BuildGrid(4, 4).Graph;
        using var host = BuildHost(graph, out var clock);

        string Cell(int r, int c) => GridFieldFactory.SiteId(r, c);
        var fleet = new List<FleetAgentSpec>
        {
            new("agv-1", Cell(0, 0), Cell(0, 3), Priority: 0), // top row, west → east
            new("agv-2", Cell(0, 3), Cell(3, 3), Priority: 1), // right column, north → south
            new("agv-3", Cell(3, 3), Cell(3, 0), Priority: 2), // bottom row, east → west
            new("agv-4", Cell(3, 0), Cell(0, 0), Priority: 3), // left column, south → north
        };

        var outcome = await RunToCompletionAsync(host, clock, fleet, maxTicks: 80);

        Assert.Equal(fleet.Count, outcome.Stats.Arrived);
        Assert.True(outcome.MaxConcurrentEnRoute >= 2, "perimeter agents should move concurrently");
        AssertNoLeasesLeak(host);
    }
}
