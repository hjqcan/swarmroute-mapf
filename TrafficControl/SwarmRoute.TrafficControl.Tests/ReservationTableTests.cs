using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using SwarmRoute.TrafficControl.Domain.ValueObjects;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class ReservationTableTests
{
    // ---- Invariant: no two conflicting leases coexist ----

    [Fact]
    public void Grant_then_other_agent_overlapping_same_resource_is_denied()
    {
        var table = new ReservationTable(EmptyTopology);

        var a = table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        Assert.Equal(AllocationOutcome.Granted, a);

        // Same resource, overlapping interval, different agent -> cannot coexist.
        var b = table.TryGrant(Path(Cell(Cp("S1"), 50, 150)), "AGV-B");
        Assert.Equal(AllocationOutcome.Queued, b);

        // Only AGV-A holds S1.
        Assert.Single(table.ActiveLeases);
        Assert.Equal("AGV-A", table.ActiveLeases[0].AgentId);
    }

    [Fact]
    public void Two_agents_may_share_a_resource_at_disjoint_times()
    {
        var table = new ReservationTable(EmptyTopology);

        // Half-open: [0,100) and [100,200) only touch -> not a conflict.
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A"));
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 100, 200)), "AGV-B"));

        Assert.Equal(2, table.ActiveLeases.Count);
    }

    [Fact]
    public void Insert_invariant_is_enforced_at_the_aggregate_boundary()
    {
        var table = new ReservationTable(EmptyTopology);
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A"));

        // The grant path filters conflicts to Queued; the low-level invariant guard is also exercised here
        // indirectly: a second overlapping grant by another agent never produces a second lease.
        table.TryGrant(Path(Cell(Cp("S1"), 10, 20)), "AGV-B");
        Assert.Single(table.ActiveLeases);
    }

    // ---- Whole-path TryReserve: grant then a crossing path is queued ----

    [Fact]
    public void WholePath_grant_then_crossing_path_is_queued()
    {
        var table = new ReservationTable(EmptyTopology);

        // AGV-A reserves the corridor S1->S2->S3 over [0,300).
        var a = table.TryGrant(CpPath(start: 0, dwell: 100, "S1", "S2", "S3"), "AGV-A");
        Assert.Equal(AllocationOutcome.Granted, a);
        Assert.Equal(3, table.ActiveLeases.Count);

        // AGV-B's path crosses at S2 over an overlapping window -> whole path denied (all-or-nothing).
        var b = table.TryGrant(CpPath(start: 50, dwell: 100, "X1", "S2", "X3"), "AGV-B");
        Assert.Equal(AllocationOutcome.Queued, b);

        // No AGV-B leases were created (whole-path lock semantics).
        Assert.DoesNotContain(table.ActiveLeases, l => l.AgentId == "AGV-B");
        // A contended request was recorded for the crossing resource.
        Assert.Contains(table.ContendedRequests, r => r.AgentId == "AGV-B" && r.ResourceId == "S2");
    }

    [Fact]
    public void WholePath_grant_succeeds_when_paths_are_time_separated()
    {
        var table = new ReservationTable(EmptyTopology);
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(CpPath(0, 100, "S1", "S2", "S3"), "AGV-A"));
        // AGV-B uses S2 strictly after AGV-A cleared it.
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(CpPath(300, 100, "X1", "S2", "X3"), "AGV-B"));
    }

    // ---- Release-no-leak regression: ParentBlock + interference closure freed ----

    [Fact]
    public void ReleaseBehind_frees_parent_block_and_interference_closure_no_leak()
    {
        // S1's closure: itself + parent block B1 + interference CP I1. (The original UnlockPath left these locked.)
        var s1 = Cp("S1");
        var topology = ClosureTopology(s1, Block("B1"), Cp("I1"));
        var table = new ReservationTable(topology);

        // Grant a path whose first cell is S1 (closure-expanded) plus S2.
        var path = Path(Cell(s1, 0, 100), Cell(Cp("S2"), 100, 200));
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(path, "AGV-A"));

        // Closure expansion: S1, B1, I1 (over [0,100)) + S2 over [100,200) = 4 leases.
        Assert.Equal(4, table.ActiveLeases.Count);
        Assert.Contains(table.ActiveLeases, l => l.Resource == Block("B1"));
        Assert.Contains(table.ActiveLeases, l => l.Resource == Cp("I1"));

        // Release behind on S1: must free S1 AND its parent block B1 AND interference I1.
        var freed = table.ReleaseBehind("AGV-A", new[] { s1 });

        Assert.Contains(freed, l => l.Resource == s1);
        Assert.Contains(freed, l => l.Resource == Block("B1"));
        Assert.Contains(freed, l => l.Resource == Cp("I1"));

        // No block / interference lease leaked — only S2 remains.
        Assert.Single(table.ActiveLeases);
        Assert.Equal(Cp("S2"), table.ActiveLeases[0].Resource);
        Assert.DoesNotContain(table.ActiveLeases, l => l.Resource == Block("B1"));
        Assert.DoesNotContain(table.ActiveLeases, l => l.Resource == Cp("I1"));
    }

    [Fact]
    public void ReleaseAll_removes_every_lease_and_contended_request_for_the_agent()
    {
        var topology = ClosureTopology(Cp("S1"), Block("B1"), Cp("I1"));
        var table = new ReservationTable(topology);

        table.TryGrant(Path(Cell(Cp("S1"), 0, 100), Cell(Cp("S2"), 100, 200)), "AGV-A");
        // Create a contended request for AGV-A on a resource held by AGV-B.
        table.TryGrant(Path(Cell(Cp("Z"), 0, 100)), "AGV-B");
        table.TryGrant(Path(Cell(Cp("Z"), 50, 150)), "AGV-A"); // queued

        Assert.Contains(table.ContendedRequests, r => r.AgentId == "AGV-A");

        var freed = table.ReleaseAll("AGV-A");

        Assert.NotEmpty(freed);
        Assert.DoesNotContain(table.ActiveLeases, l => l.AgentId == "AGV-A");
        Assert.DoesNotContain(table.ContendedRequests, r => r.AgentId == "AGV-A");
        // AGV-B's lease on Z is untouched.
        Assert.Contains(table.ActiveLeases, l => l.AgentId == "AGV-B" && l.Resource == Cp("Z"));
    }

    // ---- Read view + state version ----

    [Fact]
    public void FreeIntervals_reports_the_gaps_between_leases()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 100, 200)), "AGV-A");

        var free = table.FreeIntervals(Cp("S1"));

        // Expect [0,100) free, then [200, MaxValue) free.
        Assert.Contains(free, s => s.Interval.StartMs == 0 && s.Interval.EndMs == 100);
        Assert.Contains(free, s => s.Interval.StartMs == 200 && s.Interval.EndMs == long.MaxValue);
        Assert.False(table.IsFree(Cp("S1"), T(150, 160)));
        Assert.True(table.IsFree(Cp("S1"), T(0, 100)));
        Assert.True(table.IsFree(Cp("S1"), T(200, 250)));
    }

    [Fact]
    public void StateVersion_increments_on_every_mutation()
    {
        var table = new ReservationTable(EmptyTopology);
        var v0 = table.StateVersion;

        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        var v1 = table.StateVersion;
        Assert.True(v1 > v0);

        table.ReleaseAll("AGV-A");
        Assert.True(table.StateVersion > v1);
    }

    [Fact]
    public void Refresh_evicts_expired_leases()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        table.TryGrant(Path(Cell(Cp("S2"), 0, 500)), "AGV-A");

        var evicted = table.Refresh(nowMs: 200);

        Assert.Single(evicted);
        Assert.Equal(Cp("S1"), evicted[0].Resource);
        Assert.Single(table.ActiveLeases);
        Assert.Equal(Cp("S2"), table.ActiveLeases[0].Resource);
    }

    [Fact]
    public void Blacklisted_resource_yields_Blocked()
    {
        var topology = new Application.Topology.DictionaryResourceTopology.Builder()
            .WithBlacklist(Cp("S1"), "AGV-A")
            .Build();
        var table = new ReservationTable(topology);

        var outcome = table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        Assert.Equal(AllocationOutcome.Blocked, outcome);
        Assert.Empty(table.ActiveLeases);
    }

    [Fact]
    public void Same_agent_may_hold_overlapping_windows_of_one_resource()
    {
        var table = new ReservationTable(EmptyTopology);
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A"));
        // Same agent, overlapping window on the same resource is allowed (not a conflict).
        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 50, 150)), "AGV-A"));
        Assert.Equal(2, table.ActiveLeases.Count);
    }
}
