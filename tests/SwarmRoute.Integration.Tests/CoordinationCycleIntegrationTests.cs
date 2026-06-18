using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// M1 + M2 — the architecture's "vertical slice": a topology goes in, the REAL PathPlanning planner produces a
/// path, and the REAL TrafficControl reservation table serialises right-of-way. Everything runs in-process via
/// the contexts' own DI bootstrappers — no Postgres, no broker.
/// </summary>
public sealed class CoordinationCycleIntegrationTests
{
    private sealed class FixedClock(long nowMs) : IFleetClock
    {
        public long NowMs { get; } = nowMs;
    }

    private sealed class FixedTopology(
        IReadOnlyDictionary<ResourceRef, IReadOnlyCollection<ResourceRef>> closures) : IResourceTopology
    {
        public IReadOnlyCollection<ResourceRef> ClosureOf(ResourceRef resource)
            => closures.TryGetValue(resource, out var closure) ? closure : [resource];

        public bool IsBlacklisted(ResourceRef resource, string agentId) => false;
    }

    private static SpaceTimePath SingleCell(ResourceRef resource, long startMs, long endMs)
        => new([new SpaceTimeCell(resource, new TimeInterval(startMs, endMs))]);

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

    [Fact]
    public async Task Cycle_UsesFleetClockReleaseTime_ForNewReservations()
    {
        const long nowMs = 123_456_000;
        using var host = CoordinationTestHost.Build(FakeRoadmapQueryService.Chain("A", "B"), new FixedClock(nowMs));

        var report = await host.Cycle.RunCycleAsync(
            host.RoadmapId,
            [new AgentGoal("agv-1", "A", "B")]);

        var result = Assert.Single(report.Results);
        Assert.True(result.Reserved, result.FailureReason);
        Assert.NotNull(result.Path);
        Assert.All(result.Path!.Cells, cell => Assert.True(cell.Interval.StartMs >= nowMs));
    }

    // ── M2: multi-agent, no collision ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task M2_TwoAgents_SharingCorridor_AreSerialised_ThenSecondGrantedAfterRelease()
    {
        // Single shared corridor A-B-C-D. Both agents' shortest paths traverse B, C and the B-C / C-B lanes,
        // so the reservation table cannot grant both: it serialises them (invariant I1, no collision).
        var graph = FakeRoadmapQueryService.Chain("A", "B", "C", "D");
        using var host = CoordinationTestHost.Build(graph, new FixedClock(0));

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
        await host.Cycle.ReleaseAsync("agv-1", heldByFirst);

        // ── Re-run the cycle: with the corridor free, agv-2 can now be granted. ──
        var report2 = await host.Cycle.RunCycleAsync(host.RoadmapId, goals[1..]); // only agv-2 still pending
        var secondRetry = Assert.Single(report2.Results);

        Assert.Equal("agv-2", secondRetry.AgentId);
        Assert.True(secondRetry.Reserved, $"agv-2 should be Granted after release, got {secondRetry.Outcome}.");
        Assert.Equal(AllocationOutcome.Granted, secondRetry.Outcome);
    }

    [Fact]
    public async Task M2_Retry_PrunesOnlyBlockedResource_AndKeepsSharedPrefix()
    {
        // A-B-D is shortest; A-B-C-D is the valid detour that shares the A-B prefix. If retry prunes the
        // whole failed path, B disappears and the detour is incorrectly reported as no-route.
        var graph = FakeRoadmapQueryService.WeightedGraph(
            ["A", "B", "C", "D"],
            ("A", "B", 1.0),
            ("B", "D", 1.0),
            ("B", "C", 10.0),
            ("C", "D", 10.0));
        using var host = CoordinationTestHost.Build(graph, new FixedClock(0));
        var traffic = host.Services.GetRequiredService<ITrafficCoordinatorAppService>();

        Assert.Equal(
            AllocationOutcome.Granted,
            await traffic.TryReserveAsync(SingleCell(RoadmapGraph.LaneRef("B", "D"), 0, long.MaxValue), "blocker"));

        var report = await host.Cycle.RunCycleAsync(
            host.RoadmapId,
            [new AgentGoal("agv-1", "A", "D")]);

        var result = Assert.Single(report.Results);
        Assert.True(result.Reserved, $"expected retry to reserve through A-B-C-D, got {result.Outcome}: {result.FailureReason}");
        Assert.True(result.Attempts >= 2);

        var visitedSites = result.Path!.Cells
            .Where(c => c.Resource.Kind == ResourceKind.CP)
            .Select(c => c.Resource.Id)
            .ToList();
        Assert.Equal(new[] { "A", "B", "C", "D" }, visitedSites);

        var lanes = result.Path.Cells
            .Where(c => c.Resource.Kind == ResourceKind.Lane)
            .Select(c => c.Resource.Id)
            .ToList();
        Assert.DoesNotContain("B-D", lanes);
    }

    [Fact]
    public async Task M2_Retry_ProjectsClosureBlockConflict_ToPlannerPrunableCell()
    {
        // A-B-D is shortest; B is not directly occupied, but its topology closure includes Block:Z.
        // Retry must prune the candidate CP B, not pass Block:Z to a planner that cannot delete it.
        var graph = FakeRoadmapQueryService.WeightedGraph(
            ["A", "B", "C", "D"],
            ("A", "B", 1.0),
            ("B", "D", 1.0),
            ("A", "C", 10.0),
            ("C", "D", 10.0));
        var block = new ResourceRef(ResourceKind.Block, "Z");
        var topology = new FixedTopology(new Dictionary<ResourceRef, IReadOnlyCollection<ResourceRef>>
        {
            [RoadmapGraph.SiteRef("B")] = [RoadmapGraph.SiteRef("B"), block],
        });
        using var host = CoordinationTestHost.Build(graph, new FixedClock(0), topology);
        var traffic = host.Services.GetRequiredService<ITrafficCoordinatorAppService>();

        Assert.Equal(
            AllocationOutcome.Granted,
            await traffic.TryReserveAsync(SingleCell(block, 0, long.MaxValue), "blocker"));

        var report = await host.Cycle.RunCycleAsync(
            host.RoadmapId,
            [new AgentGoal("agv-1", "A", "D")]);

        var result = Assert.Single(report.Results);
        Assert.True(result.Reserved, $"expected retry to reserve through A-C-D, got {result.Outcome}: {result.FailureReason}");
        Assert.True(result.Attempts >= 2);

        var visitedSites = result.Path!.Cells
            .Where(c => c.Resource.Kind == ResourceKind.CP)
            .Select(c => c.Resource.Id)
            .ToList();
        Assert.Equal(new[] { "A", "C", "D" }, visitedSites);
    }

    [Fact]
    public async Task M3_TrafficControlContention_TriggersDeadlockScanThroughEventBus()
    {
        using var host = CoordinationTestHost.Build(FakeRoadmapQueryService.Graph(["R1", "R2"], ("R1", "R2")));
        var traffic = host.Services.GetRequiredService<ITrafficCoordinatorAppService>();

        Assert.Equal(AllocationOutcome.Granted, await traffic.TryReserveAsync(SingleCell(RoadmapGraph.SiteRef("R1"), 0, 100), "A"));
        Assert.Equal(AllocationOutcome.Granted, await traffic.TryReserveAsync(SingleCell(RoadmapGraph.SiteRef("R2"), 0, 100), "B"));

        // A waits for B's resource; no cycle yet.
        Assert.Equal(AllocationOutcome.Queued, await traffic.TryReserveAsync(SingleCell(RoadmapGraph.SiteRef("R2"), 50, 150), "A"));
        Assert.DoesNotContain(host.Events.Handled, e => e is DeadlockCaseDetectedEvent);

        // B waits for A's resource. The TrafficControl.Allocation.Contended event should now synchronously
        // reach the Deadlock subscriber, which scans TrafficControl's snapshot and publishes Deadlock events.
        Assert.Equal(AllocationOutcome.Queued, await traffic.TryReserveAsync(SingleCell(RoadmapGraph.SiteRef("R1"), 50, 150), "B"));

        var events = host.Events.Handled;
        Assert.Contains(events, e => e.GetType().Name == "AllocationContendedEvent");
        Assert.Contains(events, e => e is DeadlockCaseDetectedEvent);
        Assert.Contains(events, e => e is DeadlockCaseResolutionRequestedEvent);
    }
}
