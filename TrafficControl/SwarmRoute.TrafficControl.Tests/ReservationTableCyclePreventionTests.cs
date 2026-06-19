using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

/// <summary>
/// Grant-time deadlock prevention (v2) at the aggregate boundary. Uses a scripted <see cref="IWouldCloseCycleDetector"/>
/// so the aggregate's REFUSE logic is tested independently of the RAG cycle-detection itself: when granting a
/// contended path would close a cycle and the agent lacks right-of-way, <see cref="ReservationTable.TryGrant"/>
/// must return <see cref="AllocationOutcome.CycleAverted"/> and leave NO lease and NO contended (Waits) edge —
/// otherwise it behaves exactly as v0/v1.
/// </summary>
public sealed class ReservationTableCyclePreventionTests
{
    [Fact]
    public void Cycle_closing_grant_is_averted_with_no_lease_and_no_contended_edge()
    {
        var table = new ReservationTable(EmptyTopology, new ScriptedDetector(wouldClose: true));
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A"));

        // AGV-B (ordinally after A, so it lacks right-of-way) is blocked by A on S1, and the detector says queuing
        // would close a cycle → averted: no lease for B, and crucially NO contended edge (the cycle never forms).
        var outcome = table.TryGrant(Path(Cell(Cp("S1"), 50, 150)), "AGV-B");

        Assert.Equal(AllocationOutcome.CycleAverted, outcome);
        Assert.Single(table.ActiveLeases);
        Assert.Equal("AGV-A", table.ActiveLeases[0].AgentId);
        Assert.Empty(table.ContendedRequests); // the cycle-closing Waits edge was NOT recorded
    }

    [Fact]
    public void Agent_with_right_of_way_is_not_averted_even_if_a_cycle_would_close()
    {
        var table = new ReservationTable(EmptyTopology, new ScriptedDetector(wouldClose: true));
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-B"));

        // AGV-A is ordinally BEFORE B, so it has right-of-way over the holder — it must NOT yield; B re-routes on
        // its own turn instead. So even with the detector asserting a cycle, A queues normally (records the edge).
        var outcome = table.TryGrant(Path(Cell(Cp("S1"), 50, 150)), "AGV-A");

        Assert.Equal(AllocationOutcome.Queued, outcome);
        Assert.NotEmpty(table.ContendedRequests);
    }

    [Fact]
    public void No_cycle_means_ordinary_queue_even_with_prevention_on()
    {
        var table = new ReservationTable(EmptyTopology, new ScriptedDetector(wouldClose: false));
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A"));

        var outcome = table.TryGrant(Path(Cell(Cp("S1"), 50, 150)), "AGV-B");

        Assert.Equal(AllocationOutcome.Queued, outcome); // prevention on, but no cycle → unchanged behaviour
        Assert.NotEmpty(table.ContendedRequests);
    }

    [Fact]
    public void Prevention_off_by_default_queues_exactly_as_v0()
    {
        // The default ctor wires the Null detector → prevention off → byte-identical v0/v1 behaviour.
        var table = new ReservationTable(EmptyTopology);
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A"));

        var outcome = table.TryGrant(Path(Cell(Cp("S1"), 50, 150)), "AGV-B");

        Assert.Equal(AllocationOutcome.Queued, outcome);
        Assert.NotEmpty(table.ContendedRequests);
    }

    /// <summary>A detector with a fixed verdict, so the aggregate's refuse logic is tested in isolation.</summary>
    private sealed class ScriptedDetector(bool wouldClose) : IWouldCloseCycleDetector
    {
        public bool WouldCloseCycle(
            ResourceAllocationGraphSnapshot currentEdges,
            string candidateAgentId,
            IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges)
            => wouldClose && candidateWaitEdges.Count > 0;
    }
}
