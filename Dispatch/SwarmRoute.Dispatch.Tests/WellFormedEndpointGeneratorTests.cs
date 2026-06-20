using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Endpoints;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// Behaviour of the FMS-V2 <see cref="WellFormedEndpointGenerator"/>: the generated set satisfies the
/// well-formedness invariant, generation is deterministic by seed, and <see cref="IEndpointPolicy.IsValidEndpointSet"/>
/// accepts well-formed sets while rejecting ill-formed ones (a cut-vertex endpoint, a stranded endpoint, a
/// disconnected core, a non-vertex endpoint).
/// </summary>
public sealed class WellFormedEndpointGeneratorTests
{
    private static IReadOnlySet<string> Set(params string[] ids)
        => new HashSet<string>(ids, StringComparer.Ordinal);

    private static IReadOnlySet<string> AllEndpoints(EndpointSet s)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        all.UnionWith(s.Workstations);
        all.UnionWith(s.Parkings);
        all.UnionWith(s.Buffers);
        all.UnionWith(s.Chargers);
        return all;
    }

    [Fact]
    public void BuildEndpoints_ProducesAWellFormedSet_OnARing()
    {
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Ring();

        var endpoints = policy.BuildEndpoints(graph, agvCount: 2, seed: 1);

        Assert.True(policy.IsValidEndpointSet(graph, endpoints));
    }

    [Fact]
    public void BuildEndpoints_IsDeterministicForTheSameSeed_AndStableAcrossRuns()
    {
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Ring();

        var a = policy.BuildEndpoints(graph, agvCount: 2, seed: 42);
        var b = policy.BuildEndpoints(graph, agvCount: 2, seed: 42);

        Assert.Equal(AllEndpoints(a), AllEndpoints(b));
        Assert.Equal(a.Parkings, b.Parkings);
        Assert.Equal(a.Workstations, b.Workstations);
        Assert.Equal(a.Buffers, b.Buffers);
        Assert.Equal(a.Chargers, b.Chargers);
    }

    [Fact]
    public void BuildEndpoints_KeepsTheTransitCoreConnected_OnABarbell()
    {
        // On the barbell the bridge vertices C and D are articulation points and must never be carved out.
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Barbell();

        var endpoints = policy.BuildEndpoints(graph, agvCount: 1, seed: 7);
        var all = AllEndpoints(endpoints);

        Assert.True(policy.IsValidEndpointSet(graph, endpoints));
        Assert.DoesNotContain("C", all); // bridge articulation point
        Assert.DoesNotContain("D", all); // bridge articulation point
    }

    [Fact]
    public void BuildEndpoints_ReservesUpToAgvCountParkings_WhenEnoughEndpointsExist()
    {
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Ring(); // A,B,C,D — removing any one keeps a connected 3-path

        // A ring's interior of carve-able vertices is limited, but at least one parking should be reserved.
        var endpoints = policy.BuildEndpoints(graph, agvCount: 1, seed: 3);

        Assert.True(policy.IsValidEndpointSet(graph, endpoints));
        Assert.NotEmpty(endpoints.Parkings);
    }

    [Fact]
    public void IsValidEndpointSet_RejectsACutVertexEndpoint()
    {
        // Barbell: making the bridge vertex C an endpoint disconnects {A,B} from {D,E}.
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Barbell();

        var illFormed = new EndpointSet(
            Workstations: Set("C"),
            Parkings: Set(),
            Buffers: Set(),
            Chargers: Set());

        Assert.False(policy.IsValidEndpointSet(graph, illFormed));
    }

    [Fact]
    public void IsValidEndpointSet_RejectsADisconnectedTransitCore()
    {
        // Carving out BOTH bridge endpoints C and D shatters the core into {A,B} and {E}.
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Barbell();

        var illFormed = new EndpointSet(
            Workstations: Set("C", "D"),
            Parkings: Set(),
            Buffers: Set(),
            Chargers: Set());

        Assert.False(policy.IsValidEndpointSet(graph, illFormed));
    }

    [Fact]
    public void IsValidEndpointSet_RejectsAnEndpointThatIsNotAGraphVertex()
    {
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Ring();

        var illFormed = new EndpointSet(
            Workstations: Set("NOT-A-VERTEX"),
            Parkings: Set(),
            Buffers: Set(),
            Chargers: Set());

        Assert.False(policy.IsValidEndpointSet(graph, illFormed));
    }

    [Fact]
    public void IsValidEndpointSet_AcceptsASingleLeafEndpoint()
    {
        // A,B,C core path with a leaf L hanging off B: L is a valid endpoint (egress to B, not a cut vertex).
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C", "L"],
            ("A", "B"), ("B", "C"), ("B", "L"));

        var endpoints = new EndpointSet(
            Workstations: Set(),
            Parkings: Set("L"),
            Buffers: Set(),
            Chargers: Set());

        Assert.True(policy.IsValidEndpointSet(graph, endpoints));
    }

    [Fact]
    public void IsValidEndpointSet_RejectsAStrandedEndpointWithNoEgress()
    {
        // L hangs only off B; if B is ALSO an endpoint, L loses its only egress into the core.
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "C", "L"],
            ("A", "B"), ("B", "C"), ("B", "L"));

        var illFormed = new EndpointSet(
            Workstations: Set(),
            Parkings: Set("L", "B"),
            Buffers: Set(),
            Chargers: Set());

        // Removing B and L leaves core {A,C} disconnected (their only link was through B) -> invalid anyway,
        // and L has no core neighbour. Either way the set is rejected.
        Assert.False(policy.IsValidEndpointSet(graph, illFormed));
    }

    [Fact]
    public void BuildEndpoints_RejectsNegativeAgvCount()
    {
        var policy = new WellFormedEndpointGenerator();
        var graph = RoadmapGraphFixtures.Ring();

        Assert.Throws<ArgumentException>(() => policy.BuildEndpoints(graph, agvCount: -1, seed: 0));
    }
}
