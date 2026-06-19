using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Host.Adapters;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// End-to-end grant-time prevention: the REAL <see cref="ReservationTable"/> wired with the REAL
/// <see cref="RagWouldCloseCycleDetector"/>. The inverse of <c>DeadlockClosedLoopIntegrationTests</c> — instead of
/// detecting and resolving a circular wait after it forms, prevention refuses the cycle-closing grant so the ring
/// never closes and the reactive RAG detector finds nothing to do.
/// </summary>
public sealed class CyclePreventionIntegrationTests
{
    private static SpaceTimePath Cp(string id, long start, long end)
        => new([new SpaceTimeCell(new ResourceRef(ResourceKind.CP, id), new TimeInterval(start, end))]);

    [Fact]
    public void Three_agent_circular_wait_is_averted_so_no_deadlock_forms()
    {
        var table = new ReservationTable(IResourceTopology.Empty, new RagWouldCloseCycleDetector());

        // Each agent holds one control point.
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Cp("S1", 0, 100), "AGV-A"));
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Cp("S2", 0, 100), "AGV-B"));
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Cp("S3", 0, 100), "AGV-C"));

        // A then waits on B's cell and B on C's — no cycle yet, so these queue normally (the Waits edges form a chain).
        Assert.Equal(AllocationOutcome.Queued, table.TryGrant(Cp("S2", 50, 150), "AGV-A"));
        Assert.Equal(AllocationOutcome.Queued, table.TryGrant(Cp("S3", 50, 150), "AGV-B"));

        // C waiting on A's cell would CLOSE the ring A→S2→B→S3→C→S1→A. Prevention averts it (C lacks right-of-way),
        // so the cycle-closing edge is never recorded.
        Assert.Equal(AllocationOutcome.CycleAverted, table.TryGrant(Cp("S1", 50, 150), "AGV-C"));

        // Therefore the reactive detector, reading the live table, sees NO circular wait — prevention front-ran it.
        var snapshot = new ResourceAllocationGraphSnapshot(
            table.ActiveLeases.Select(l => (l.AgentId, l.Resource)).ToList(),
            table.ContendedRequests.Select(r => (r.AgentId, r.Resource)).ToList());
        var cycles = new RagDeadlockDetector().Detect(snapshot);

        Assert.Empty(cycles);
        Assert.Equal(2, table.ContendedRequests.Count); // only A→S2 and B→S3; the closing C→S1 was refused
    }
}
