using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class ConflictDetectorTests
{
    private static ReservationTable TableWithLease(IResourceTopology topology, ResourceRef r, long start, long end, string agent)
    {
        var table = new ReservationTable(topology);
        table.TryGrant(Path(Cell(r, start, end)), agent);
        return table;
    }

    [Fact]
    public void Detects_vertex_same_conflict()
    {
        var detector = new ConflictDetector(EmptyTopology);
        var table = TableWithLease(EmptyTopology, Cp("S1"), 0, 100, "AGV-B");

        // Candidate enters at the same time (start not after incumbent) -> VertexSame.
        var conflicts = detector.Detect(table, Path(Cell(Cp("S1"), 0, 100)), "AGV-A");

        Assert.Contains(conflicts, c => c.Type == ConflictType.VertexSame && c.AgentA == "AGV-A" && c.AgentB == "AGV-B");
    }

    [Fact]
    public void Detects_following_conflict_when_candidate_enters_later()
    {
        var detector = new ConflictDetector(EmptyTopology);
        var table = TableWithLease(EmptyTopology, Cp("S1"), 0, 100, "AGV-B");

        // Candidate enters strictly after the incumbent (trailing into a not-yet-cleared cell) -> Following.
        var conflicts = detector.Detect(table, Path(Cell(Cp("S1"), 50, 150)), "AGV-A");

        Assert.Contains(conflicts, c => c.Type == ConflictType.Following);
    }

    [Fact]
    public void Detects_edge_swap_conflict_on_reversed_lane()
    {
        var detector = new ConflictDetector(EmptyTopology);
        // Incumbent holds lane "B-A"; candidate wants the reverse "A-B" at an overlapping time.
        var table = TableWithLease(EmptyTopology, Lane("B-A"), 0, 100, "AGV-B");

        var conflicts = detector.Detect(table, Path(Cell(Lane("A-B"), 0, 100)), "AGV-A");

        Assert.Contains(conflicts, c => c.Type == ConflictType.EdgeSwap);
    }

    [Fact]
    public void Detects_interference_conflict_via_closure()
    {
        // Candidate cell S1 interferes with I1; AGV-B holds I1.
        var topology = ClosureTopology(Cp("S1"), Cp("I1"));
        var detector = new ConflictDetector(topology);
        var table = TableWithLease(topology, Cp("I1"), 0, 100, "AGV-B");

        var conflicts = detector.Detect(table, Path(Cell(Cp("S1"), 0, 100)), "AGV-A");

        Assert.Contains(conflicts, c => c.Type == ConflictType.Interference && c.ResourceB == Cp("I1"));
    }

    [Fact]
    public void No_conflict_when_time_separated()
    {
        var detector = new ConflictDetector(EmptyTopology);
        var table = TableWithLease(EmptyTopology, Cp("S1"), 0, 100, "AGV-B");

        var conflicts = detector.Detect(table, Path(Cell(Cp("S1"), 100, 200)), "AGV-A");

        Assert.Empty(conflicts);
    }

    [Fact]
    public void No_self_conflict_with_own_leases()
    {
        var detector = new ConflictDetector(EmptyTopology);
        var table = TableWithLease(EmptyTopology, Cp("S1"), 0, 100, "AGV-A");

        var conflicts = detector.Detect(table, Path(Cell(Cp("S1"), 0, 100)), "AGV-A");

        Assert.Empty(conflicts);
    }
}
