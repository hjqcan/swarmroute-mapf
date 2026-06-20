using SwarmRoute.Dispatch.Application;
using SwarmRoute.Map.Domain.Shared.Enums;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// Behaviour of the FMS-V2/V3 <see cref="ParkingManager"/>: nearest-reachable resting-site selection (parking,
/// then buffer, then none) and greedy path-clearing relocations of parked blockers.
/// </summary>
public sealed class ParkingManagerTests
{
    private static IReadOnlyDictionary<string, SiteRole> Roles(params (string Site, SiteRole Role)[] roles)
    {
        var map = new Dictionary<string, SiteRole>(StringComparer.Ordinal);
        foreach (var (site, role) in roles)
            map[site] = role;
        return map;
    }

    private static IReadOnlySet<string> Occupied(params string[] sites)
        => new HashSet<string>(sites, StringComparer.Ordinal);

    // ---- AssignParking -----------------------------------------------------------------------------------

    [Fact]
    public void AssignParking_PicksNearestFreeParkingByShortestPath()
    {
        // Line A-B-C-D-E. A is current; C and E are parkings. C (distance 2) beats E (distance 4).
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C", "D", "E"],
            ("A", "B"), ("B", "C"), ("C", "D"), ("D", "E"));
        var roles = Roles(("C", SiteRole.Parking), ("E", SiteRole.Parking));

        var target = new ParkingManager().AssignParking("A", graph, Occupied(), roles);

        Assert.Equal("C", target);
    }

    [Fact]
    public void AssignParking_SkipsOccupiedParking_AndTakesTheNextNearest()
    {
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C", "D", "E"],
            ("A", "B"), ("B", "C"), ("C", "D"), ("D", "E"));
        var roles = Roles(("C", SiteRole.Parking), ("E", SiteRole.Parking));

        // The nearer parking C is taken -> fall through to E.
        var target = new ParkingManager().AssignParking("A", graph, Occupied("C"), roles);

        Assert.Equal("E", target);
    }

    [Fact]
    public void AssignParking_FallsBackToBuffer_WhenNoParkingIsReachableAndFree()
    {
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C"], ("A", "B"), ("B", "C"));
        // Only buffers exist (no parking role at all) -> buffer fallback.
        var roles = Roles(("B", SiteRole.Buffer), ("C", SiteRole.Buffer));

        var target = new ParkingManager().AssignParking("A", graph, Occupied(), roles);

        Assert.Equal("B", target);
    }

    [Fact]
    public void AssignParking_PrefersParkingOverACloserBuffer()
    {
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C"], ("A", "B"), ("B", "C"));
        // B is a closer buffer, C a farther parking -> parking still wins (role preference, not distance).
        var roles = Roles(("B", SiteRole.Buffer), ("C", SiteRole.Parking));

        var target = new ParkingManager().AssignParking("A", graph, Occupied(), roles);

        Assert.Equal("C", target);
    }

    [Fact]
    public void AssignParking_ReturnsNull_WhenNothingIsReachable()
    {
        // Parking X exists but is disconnected from A (no edge reaches it).
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "X"], ("A", "B"));
        var roles = Roles(("X", SiteRole.Parking));

        var target = new ParkingManager().AssignParking("A", graph, Occupied(), roles);

        Assert.Null(target);
    }

    [Fact]
    public void AssignParking_TieBreaksOnOrdinalLeastSiteId()
    {
        // Both parkings are exactly distance 1 from A; ordinal-least ("B") wins deterministically.
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "Z"], ("A", "B"), ("A", "Z"));
        var roles = Roles(("B", SiteRole.Parking), ("Z", SiteRole.Parking));

        var target = new ParkingManager().AssignParking("A", graph, Occupied(), roles);

        Assert.Equal("B", target);
    }

    // ---- FindRelocationsForWalledAgent -------------------------------------------------------------------

    [Fact]
    public void FindRelocations_MovesParkedBlockersOffTheShortestPathToFreeBuffers()
    {
        // Path A->B->C->D for the walled agent; B and C hold parked vehicles; F and G are free buffers off-path.
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C", "D", "F", "G"],
            ("A", "B"), ("B", "C"), ("C", "D"),
            ("B", "F"), ("C", "G"));
        var roles = Roles(("F", SiteRole.Buffer), ("G", SiteRole.Buffer));

        var parkedBySite = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["B"] = "AGV-B",
            ["C"] = "AGV-C",
        };
        var occupied = Occupied("B", "C");

        var relocations = new ParkingManager().FindRelocationsForWalledAgent(
            "A", "D", graph, parkedBySite, occupied, roles);

        Assert.Equal(2, relocations.Count);
        Assert.Contains(relocations, r => r is { AgentId: "AGV-B", FromSite: "B", ToBuffer: "F" });
        Assert.Contains(relocations, r => r is { AgentId: "AGV-C", FromSite: "C", ToBuffer: "G" });
    }

    [Fact]
    public void FindRelocations_IsEmpty_WhenThePathIsClear()
    {
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C"], ("A", "B"), ("B", "C"));
        var roles = Roles(("B", SiteRole.Transit), ("C", SiteRole.Workstation));

        var relocations = new ParkingManager().FindRelocationsForWalledAgent(
            "A", "C", graph,
            parkedBySite: new Dictionary<string, string>(StringComparer.Ordinal),
            occupiedOrParked: Occupied(),
            siteRoles: roles);

        Assert.Empty(relocations);
    }

    [Fact]
    public void FindRelocations_IsEmpty_WhenNoPathExists()
    {
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "X"], ("A", "B"));
        var roles = Roles(("F", SiteRole.Buffer));

        var relocations = new ParkingManager().FindRelocationsForWalledAgent(
            "A", "X", graph,
            parkedBySite: new Dictionary<string, string>(StringComparer.Ordinal) { ["B"] = "AGV-B" },
            occupiedOrParked: Occupied("B"),
            siteRoles: roles);

        Assert.Empty(relocations);
    }

    [Fact]
    public void FindRelocations_SkipsABlocker_WhenNoFreeBufferIsAvailableForIt()
    {
        // Two blockers B and C, but only one free buffer F (reachable from B). C gets no target -> skipped.
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C", "D", "F"],
            ("A", "B"), ("B", "C"), ("C", "D"), ("B", "F"));
        var roles = Roles(("F", SiteRole.Buffer));

        var parkedBySite = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["B"] = "AGV-B",
            ["C"] = "AGV-C",
        };

        var relocations = new ParkingManager().FindRelocationsForWalledAgent(
            "A", "D", graph, parkedBySite, Occupied("B", "C"), roles);

        var single = Assert.Single(relocations);
        Assert.Equal("AGV-B", single.AgentId);
        Assert.Equal("F", single.ToBuffer);
    }
}
