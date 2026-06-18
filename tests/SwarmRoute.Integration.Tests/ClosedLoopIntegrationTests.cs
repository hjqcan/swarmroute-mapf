using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using Xunit;
using Xunit.Sdk;

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
/// </summary>
public sealed class ClosedLoopIntegrationTests
{
    private sealed class SimAgent(string id, string start, string goal, int priority)
    {
        public string Id { get; } = id;
        public string Start { get; } = start;
        public string Goal { get; } = goal;
        public int Priority { get; } = priority;

        public bool EnRoute { get; set; }
        public bool Done { get; set; }
        public IReadOnlyList<string> CpRoute { get; set; } = Array.Empty<string>();
        public IReadOnlyList<ResourceRef> AllResources { get; set; } = Array.Empty<ResourceRef>();
        public int Idx { get; set; }

        public string CurrentCp => CpRoute[Idx];
    }

    private sealed record LoopOutcome(int Ticks, int MaxConcurrentEnRoute);

    /// <summary>
    /// Runs the closed loop to completion. Each tick: (1) plan+reserve every idle agent via the real cycle;
    /// (2) advance each en-route agent one CP, releasing the CP+lane it left behind (and everything on arrival);
    /// (3) assert no two en-route agents share a CP. Throws if the loop doesn't close within <paramref name="maxTicks"/>.
    /// </summary>
    private static async Task<LoopOutcome> RunToCompletionAsync(
        CoordinationTestHost host, List<SimAgent> fleet, int maxTicks)
    {
        var tick = 0;
        var maxConcurrent = 0;

        while (fleet.Any(a => !a.Done))
        {
            if (++tick > maxTicks)
                throw new XunitException(
                    $"Closed loop did not converge within {maxTicks} ticks (likely livelock/deadlock). " +
                    $"Pending: {string.Join(", ", fleet.Where(a => !a.Done).Select(a => $"{a.Id}@{(a.EnRoute ? a.CurrentCp : a.Start)}->{a.Goal}"))}");

            // (1) Plan + reserve every agent that still needs right-of-way.
            var pending = fleet
                .Where(a => !a.Done && !a.EnRoute)
                .Select(a => new AgentGoal(a.Id, a.Start, a.Goal, a.Priority))
                .ToList();

            if (pending.Count > 0)
            {
                var report = await host.Cycle.RunCycleAsync(host.RoadmapId, pending);
                foreach (var r in report.Results.Where(r => r is { Reserved: true, Path: not null }))
                {
                    var ag = fleet.Single(a => a.Id == r.AgentId);
                    ag.EnRoute = true;
                    ag.Idx = 0;
                    ag.CpRoute = r.Path!.Cells
                        .Where(c => c.Resource.Kind == ResourceKind.CP)
                        .Select(c => c.Resource.Id)
                        .ToList();
                    ag.AllResources = r.Path!.Cells.Select(c => c.Resource).Distinct().ToList();
                    Assert.Equal(ag.Start, ag.CpRoute[0]);
                    Assert.Equal(ag.Goal, ag.CpRoute[^1]);
                }
            }

            // (2) Move each en-route agent one CP forward, releasing what it leaves behind.
            foreach (var ag in fleet.Where(a => a is { EnRoute: true, Done: false }).ToList())
            {
                if (ag.Idx < ag.CpRoute.Count - 1)
                {
                    var fromCp = ag.CpRoute[ag.Idx];
                    var toCp = ag.CpRoute[ag.Idx + 1];
                    ag.Idx++;
                    await host.Cycle.ReleaseAsync(ag.Id,
                    [
                        RoadmapGraph.SiteRef(fromCp),
                        new ResourceRef(ResourceKind.Lane, $"{fromCp}-{toCp}"),
                    ]);
                }

                if (ag.Idx >= ag.CpRoute.Count - 1)
                {
                    // Arrived: hand back everything still held (goal CP + any remainder) — no leak.
                    ag.Done = true;
                    ag.EnRoute = false;
                    await host.Cycle.ReleaseAsync(ag.Id, ag.AllResources);
                }
            }

            // (3) Safety: no two still-moving agents on the same control point this tick.
            var occupied = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ag in fleet.Where(a => a.EnRoute))
            {
                Assert.False(
                    occupied.TryGetValue(ag.CurrentCp, out var other),
                    $"COLLISION at CP '{ag.CurrentCp}' on tick {tick}: '{ag.Id}' and '{other}'.");
                occupied[ag.CurrentCp] = ag.Id;
            }

            maxConcurrent = Math.Max(maxConcurrent, fleet.Count(a => a.EnRoute));
        }

        return new LoopOutcome(tick, maxConcurrent);
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
        var fleet = new List<SimAgent>
        {
            new("agv-1", "A", "C", priority: 0),
            new("agv-2", "D", "F", priority: 1),
        };

        var outcome = await RunToCompletionAsync(host, fleet, maxTicks: 20);

        Assert.All(fleet, a => Assert.True(a.Done, $"{a.Id} never reached {a.Goal}"));
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

        var fleet = new List<SimAgent>
        {
            new("agv-1", "W", "E", priority: 0),
            new("agv-2", "N", "S", priority: 1),
        };

        var outcome = await RunToCompletionAsync(host, fleet, maxTicks: 30);

        Assert.All(fleet, a => Assert.True(a.Done, $"{a.Id} never reached {a.Goal}"));
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

        var fleet = new List<SimAgent>
        {
            new("agv-1", "W", "E", priority: 0),    // through C0
            new("agv-2", "N", "Sx", priority: 1),   // through C0 then C1
            new("agv-3", "W2", "E2", priority: 2),  // through C1
            new("agv-4", "E", "W", priority: 3),    // through C0, opposite agv-1 (ends at W, agv-1's start — free by then)
        };

        var outcome = await RunToCompletionAsync(host, fleet, maxTicks: 60);

        Assert.All(fleet, a => Assert.True(a.Done, $"{a.Id} never reached {a.Goal}"));
        AssertNoLeasesLeak(host);
    }
}
