using AJR.Platform.Algorithms.DataStructures.Graphs;
using AJR.Platform.Algorithms.Graphs;
using NetDevPack.Domain;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Map.Domain.ValueObjects;

/// <summary>
/// Immutable value object wrapping a built directed-weighted graph of the roadmap.
/// <para>
/// Vertices are the ids of enabled sites; edges are the (enabled) lines, directed
/// <c>StartStation → EndStation</c> with weight <c>round(Distance * 1000)</c>. This is the in-process
/// read model returned by <c>IRoadmapQueryService</c> and consumed by PathPlanning / Coordination.
/// Ported from <c>AJR.Platform.GraphMapDP.GraphMap.Init</c> + <c>DistanceTo</c>.
/// </para>
/// <para>
/// The graph itself is mutable in principle (the vendored type exposes mutators) but is never mutated
/// after construction here; treat instances as read-only snapshots. Equality is by structural content
/// (vertices + weighted edges).
/// </para>
/// </summary>
public sealed class RoadmapGraph : ValueObject
{
    /// <summary>Multiplier applied to a line's metre <c>Distance</c> to obtain the integer edge weight.</summary>
    public const double WeightScale = 1000d;

    private readonly DirectedWeightedSparseGraph<string> _graph;

    private RoadmapGraph(DirectedWeightedSparseGraph<string> graph) => _graph = graph;

    /// <summary>
    /// Builds a <see cref="RoadmapGraph"/> from sites and lines. Disabled sites are excluded from the
    /// vertex set; lines whose endpoints are not both present as vertices are skipped (defensive — the
    /// owning <c>Roadmap</c> already guarantees endpoints resolve). Mirrors <c>GraphMap.Init</c>.
    /// </summary>
    public static RoadmapGraph Build(IEnumerable<MapSite> sites, IEnumerable<MapLine> lines)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(lines);

        var graph = new DirectedWeightedSparseGraph<string>();

        var vertexIds = sites.Where(s => s.Enable).Select(s => s.SiteId).ToArray();
        graph.AddVertices(vertexIds);

        var vertexSet = new HashSet<string>(vertexIds, StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (!vertexSet.Contains(line.StartStationId) || !vertexSet.Contains(line.EndStationId))
                continue;

            var weight = (long)Math.Round(line.Distance * WeightScale, MidpointRounding.AwayFromZero);
            // The vendored graph treats weight 0 as "no edge"; clamp degenerate zero-length lines to 1.
            if (weight <= 0)
                weight = 1;

            graph.AddEdge(line.StartStationId, line.EndStationId, weight);
        }

        return new RoadmapGraph(graph);
    }

    /// <summary>The underlying vendored graph. Exposed for advanced consumers (e.g. planners running Dijkstra/SIPP).</summary>
    public DirectedWeightedSparseGraph<string> Graph => _graph;

    /// <summary>Number of vertices (enabled sites).</summary>
    public int VertexCount => _graph.VerticesCount;

    /// <summary>Number of directed edges (enabled lines with both endpoints present).</summary>
    public int EdgeCount => _graph.EdgesCount;

    /// <summary>All vertex (site) ids in the graph.</summary>
    public IEnumerable<string> Vertices => _graph.Vertices;

    /// <summary>True when <paramref name="siteId"/> is a vertex of the graph.</summary>
    public bool HasSite(string siteId) => _graph.HasVertex(siteId);

    /// <summary>True when a directed edge exists from <paramref name="fromSiteId"/> to <paramref name="toSiteId"/>.</summary>
    public bool HasLine(string fromSiteId, string toSiteId) => _graph.HasEdge(fromSiteId, toSiteId);

    /// <summary>
    /// Out-neighbour site ids of <paramref name="siteId"/> (successors reachable via one directed edge).
    /// Returns an empty sequence when the site is unknown.
    /// </summary>
    public IEnumerable<string> Neighbours(string siteId)
    {
        var map = _graph.NeighboursMap(siteId);
        return map is null ? Enumerable.Empty<string>() : map.Keys;
    }

    /// <summary>
    /// The scaled weight of the edge <paramref name="fromSiteId"/> → <paramref name="toSiteId"/>
    /// (<c>round(Distance * 1000)</c>), or <c>null</c> if no such edge exists.
    /// </summary>
    public long? EdgeWeight(string fromSiteId, string toSiteId)
        => _graph.HasEdge(fromSiteId, toSiteId) ? _graph.GetEdgeWeight(fromSiteId, toSiteId) : null;

    /// <summary>
    /// Shortest-path distance (in scaled weight units) from <paramref name="startSiteId"/> to
    /// <paramref name="endSiteId"/> via Dijkstra, or <c>null</c> if either site is absent or unreachable.
    /// Mirrors <c>GraphMap.DistanceTo</c>.
    /// </summary>
    public long? DistanceTo(string startSiteId, string endSiteId)
    {
        if (!_graph.HasVertex(startSiteId) || !_graph.HasVertex(endSiteId))
            return null;

        var dijkstra = new DijkstraShortestPaths<DirectedWeightedSparseGraph<string>, string>(_graph, startSiteId);
        return dijkstra.HasPathTo(endSiteId) ? dijkstra.DistanceTo(endSiteId) : null;
    }

    /// <summary>
    /// The ordered list of site ids on the shortest path from <paramref name="startSiteId"/> to
    /// <paramref name="endSiteId"/> (inclusive of both ends), or <c>null</c> if unreachable / unknown.
    /// </summary>
    public IReadOnlyList<string>? ShortestPath(string startSiteId, string endSiteId)
    {
        if (!_graph.HasVertex(startSiteId) || !_graph.HasVertex(endSiteId))
            return null;

        if (string.Equals(startSiteId, endSiteId, StringComparison.Ordinal))
            return new[] { startSiteId };

        var dijkstra = new DijkstraShortestPaths<DirectedWeightedSparseGraph<string>, string>(_graph, startSiteId);
        var path = dijkstra.ShortestPathTo(endSiteId);
        return path?.ToList();
    }

    /// <summary>Convenience: wraps a vertex id as a Kernel <see cref="ResourceRef"/> of kind <see cref="ResourceKind.CP"/>.</summary>
    public static ResourceRef SiteRef(string siteId) => new(ResourceKind.CP, siteId);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        foreach (var v in _graph.Vertices.OrderBy(v => v, StringComparer.Ordinal))
            yield return v;

        foreach (var e in _graph.Edges
                     .OrderBy(e => e.Source, StringComparer.Ordinal)
                     .ThenBy(e => e.Destination, StringComparer.Ordinal))
        {
            yield return $"{e.Source}->{e.Destination}:{e.Weight}";
        }
    }
}
