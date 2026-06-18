using AJR.Platform.Algorithms.DataStructures.Graphs;
using AJR.Platform.Algorithms.Graphs;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Services;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Tests;

public sealed class RoadmapGraphTests
{
    [Fact]
    public void Build_creates_expected_vertices_and_edges()
    {
        var roadmap = Builders.DiamondRoadmap();
        var graph = RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);

        Assert.Equal(4, graph.VertexCount);
        Assert.Equal(4, graph.EdgeCount);

        Assert.True(graph.HasSite("A"));
        Assert.True(graph.HasSite("D"));
        Assert.True(graph.HasLine("A", "B"));
        Assert.True(graph.HasLine("C", "D"));
        // Directed: there is no B→A edge.
        Assert.False(graph.HasLine("B", "A"));
    }

    [Fact]
    public void Edge_weight_is_distance_scaled_by_1000()
    {
        var roadmap = Builders.DiamondRoadmap();
        var graph = RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);

        Assert.Equal(2000, graph.EdgeWeight("A", "B"));
        Assert.Equal(5000, graph.EdgeWeight("A", "C"));
        Assert.Equal(1000, graph.EdgeWeight("C", "D"));
        Assert.Null(graph.EdgeWeight("A", "D")); // no direct edge
    }

    [Fact]
    public void Disabled_sites_are_excluded_from_the_graph()
    {
        var sites = new[]
        {
            Builders.Site("A"),
            Builders.Site("B"),
            Builders.Site("C", enable: false) // disabled
        };
        var lines = new[]
        {
            Builders.Line("A-B", "A", "B", 1.0),
            // Edge touching disabled C must be skipped (endpoint not a vertex).
            Builders.Line("A-C", "A", "C", 1.0)
        };
        var graph = RoadmapGraph.Build(sites, lines);

        Assert.Equal(2, graph.VertexCount);
        Assert.False(graph.HasSite("C"));
        Assert.Equal(1, graph.EdgeCount);
        Assert.True(graph.HasLine("A", "B"));
        Assert.False(graph.HasLine("A", "C"));
    }

    [Fact]
    public void DistanceTo_matches_hand_computed_shortest_path()
    {
        var roadmap = Builders.DiamondRoadmap();
        var graph = RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);

        // A→B→D = 2000 + 2000 = 4000 beats A→C→D = 5000 + 1000 = 6000.
        Assert.Equal(4000, graph.DistanceTo("A", "D"));
    }

    [Fact]
    public void DistanceTo_cross_checks_against_DijkstraShortestPaths()
    {
        var roadmap = Builders.DiamondRoadmap();
        var graph = RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);

        // Independently run Dijkstra over the same underlying graph and compare.
        var dijkstra = new DijkstraShortestPaths<DirectedWeightedSparseGraph<string>, string>(graph.Graph, "A");

        foreach (var target in new[] { "A", "B", "C", "D" })
        {
            var viaVo = graph.DistanceTo("A", target);
            var viaAlgo = dijkstra.HasPathTo(target) ? dijkstra.DistanceTo(target) : (long?)null;
            Assert.Equal(viaAlgo, viaVo);
        }
    }

    [Fact]
    public void ShortestPath_returns_ordered_site_ids()
    {
        var roadmap = Builders.DiamondRoadmap();
        var graph = RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);

        var path = graph.ShortestPath("A", "D");

        Assert.NotNull(path);
        Assert.Equal(new[] { "A", "B", "D" }, path);
    }

    [Fact]
    public void DistanceTo_unreachable_returns_null()
    {
        // Two disconnected components: A→B and an isolated island X→Y.
        var sites = new[]
        {
            Builders.Site("A"), Builders.Site("B"),
            Builders.Site("X"), Builders.Site("Y")
        };
        var lines = new[]
        {
            Builders.Line("A-B", "A", "B", 1.0),
            Builders.Line("X-Y", "X", "Y", 1.0)
        };
        var graph = RoadmapGraph.Build(sites, lines);

        Assert.Null(graph.DistanceTo("A", "Y"));
        Assert.Null(graph.ShortestPath("A", "Y"));
    }

    [Fact]
    public void Neighbours_returns_out_successors()
    {
        var roadmap = Builders.DiamondRoadmap();
        var graph = RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);

        var neighbours = graph.Neighbours("A").OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "B", "C" }, neighbours);
        Assert.Empty(graph.Neighbours("D")); // D is a sink
    }

    [Fact]
    public void Factory_builds_equivalent_graph_to_static_build()
    {
        var roadmap = Builders.DiamondRoadmap();
        IRoadmapGraphFactory factory = new RoadmapGraphFactory();

        var viaFactory = factory.Build(roadmap);
        var viaStatic = RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);

        Assert.Equal(viaStatic, viaFactory); // structural value equality
    }

    [Fact]
    public void Zero_length_line_is_clamped_to_weight_one()
    {
        var sites = new[] { Builders.Site("A"), Builders.Site("B") };
        var lines = new[] { Builders.Line("A-B", "A", "B", 0.0) };
        var graph = RoadmapGraph.Build(sites, lines);

        // Vendored graph treats weight 0 as "no edge"; we clamp to 1 so the edge survives.
        Assert.Equal(1, graph.EdgeCount);
        Assert.Equal(1, graph.EdgeWeight("A", "B"));
    }
}
