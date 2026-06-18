using SwarmRoute.Coordination.Application;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// M1 + M2 — the architecture's "vertical slice": a topology goes in, the REAL PathPlanning planner produces a
/// path, and the REAL TrafficControl reservation table serialises right-of-way. Everything runs in-process via
/// the contexts' own DI bootstrappers — no Postgres, no broker.
/// </summary>
public sealed class CoordinationCycleIntegrationTests
{
    // ── M1: topology → path ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task M1_SingleAgent_PlansShortestPath_AndReservesGranted()
    {
        // A-B-C-D chain; one agent A→D.
        using var host = CoordinationTestHost.Build(FakeRoadmapQueryService.Chain("A", "B", "C", "D"));

        var report = await host.Cycle.RunCycleAsync(
            host.RoadmapId,
            [new AgentGoal("agv-1", "A", "D")]);

        var result = Assert.Single(report.Results);
        Assert.True(result.Planned, result.FailureReason);
        Assert.True(result.Reserved, $"expected Granted, got {result.Outcome}: {result.FailureReason}");
        Assert.Equal(AllocationOutcome.Granted, result.Outcome);

        // Planned path visits A,B,C,D in order (CP cells, in traversal order).
        Assert.NotNull(result.Path);
        var visitedSites = result.Path!.Cells
            .Where(c => c.Resource.Kind == ResourceKind.CP)
            .Select(c => c.Resource.Id)
            .ToList();
        Assert.Equal(new[] { "A", "B", "C", "D" }, visitedSites);
    }

    // ── M2: multi-agent, no collision ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task M2_TwoAgents_SharingCorridor_AreSerialised_ThenSecondGrantedAfterRelease()
    {
        // Single shared corridor A-B-C-D. Both agents' shortest paths traverse B, C and the B-C / C-B lanes,
        // so the reservation table cannot grant both: it serialises them (invariant I1, no collision).
        var graph = FakeRoadmapQueryService.Chain("A", "B", "C", "D");
        using var host = CoordinationTestHost.Build(graph);

        // agv-1 (priority 0) goes A→D; agv-2 (priority 1) goes D→A — head-on over the same corridor.
        var goals = new[]
        {
            new AgentGoal("agv-1", "A", "D", Priority: 0),
            new AgentGoal("agv-2", "D", "A", Priority: 1),
        };

        var report = await host.Cycle.RunCycleAsync(host.RoadmapId, goals);

        var first = report.Results.Single(r => r.AgentId == "agv-1");
        var second = report.Results.Single(r => r.AgentId == "agv-2");

        // First (higher priority) wins right-of-way; second is denied/queued — the table serialised them.
        Assert.True(first.Reserved, $"agv-1 should be Granted, got {first.Outcome}: {first.FailureReason}");
        Assert.Equal(AllocationOutcome.Granted, first.Outcome);

        Assert.False(second.Reserved, "agv-2 must NOT get right-of-way while agv-1 holds the corridor.");
        Assert.True(second.Planned, "agv-2 still plans a path (the corridor is its only route).");
        Assert.Equal(AllocationOutcome.Queued, second.Outcome);

        // ── First agent passes through and releases the whole corridor it held. ──
        // ReleaseBehind(agent, passed) frees the agent's leases whose resource is in `passed` (+ closure).
        var heldByFirst = first.Path!.Cells.Select(c => c.Resource).Distinct().ToList();
        host.Cycle.Release("agv-1", heldByFirst);

        // ── Re-run the cycle: with the corridor free, agv-2 can now be granted. ──
        var report2 = await host.Cycle.RunCycleAsync(host.RoadmapId, goals[1..]); // only agv-2 still pending
        var secondRetry = Assert.Single(report2.Results);

        Assert.Equal("agv-2", secondRetry.AgentId);
        Assert.True(secondRetry.Reserved, $"agv-2 should be Granted after release, got {secondRetry.Outcome}.");
        Assert.Equal(AllocationOutcome.Granted, secondRetry.Outcome);
    }
}
