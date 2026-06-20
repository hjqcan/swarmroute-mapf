using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Domain.Topology;

/// <summary>
/// Pure, deterministic graph-connectivity helpers shared by the Dispatch domain's FMS reasoning
/// (traffic-impact analysis, well-formed endpoint generation) over a <see cref="RoadmapGraph"/> snapshot
/// (過境核心拓樸工具).
/// <para>
/// All reachability here is computed on the <em>undirected projection</em> of the directed roadmap: two
/// vertices are connected when a path exists ignoring lane direction. That matches the FMS notion of a
/// "transit core" — a region the fleet can move through — because for every directed lane the roadmap (almost
/// always) carries the reverse lane, and "is the core still navigable when this zone is sealed off?" is a
/// connectivity, not a one-way-reachability, question. The undirected projection makes the predicate
/// symmetric and direction-robust.
/// </para>
/// <para>
/// These helpers never mutate the graph; they read its <see cref="RoadmapGraph.Vertices"/> /
/// <see cref="RoadmapGraph.Neighbours"/> only. Iteration order is made deterministic by ordinal-sorting every
/// vertex/neighbour set the traversal touches, so results are reproducible across runs and platforms.
/// </para>
/// </summary>
public static class TransitCoreTopology
{
    /// <summary>
    /// Maps a blocking closure of <see cref="ResourceRef"/>s to the set of roadmap <em>site ids</em> it seals
    /// off: every <see cref="ResourceKind.CP"/> member whose id is a vertex of <paramref name="graph"/>, plus
    /// the endpoints of every <see cref="ResourceKind.Lane"/> member (a sealed lane removes the ability to
    /// transit through its endpoints for closure purposes only when both endpoints are graph vertices). Non-CP,
    /// non-Lane members (zones / blocks) carry no intrinsic vertex and are ignored here — callers that need
    /// zone semantics expand the zone to its members upstream.
    /// </summary>
    /// <param name="closure">The blocking closure to project onto graph vertices.</param>
    /// <param name="graph">The roadmap whose vertex space the closure is interpreted against.</param>
    /// <returns>The ordinal set of site ids the closure removes from the transit core.</returns>
    public static IReadOnlySet<string> ClosureSites(IReadOnlySet<ResourceRef> closure, RoadmapGraph graph)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(graph);

        var sites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in closure)
        {
            switch (resource.Kind)
            {
                case ResourceKind.CP when graph.HasSite(resource.Id):
                    sites.Add(resource.Id);
                    break;

                case ResourceKind.Lane:
                    foreach (var endpoint in LaneEndpoints(resource.Id))
                    {
                        if (graph.HasSite(endpoint))
                            sites.Add(endpoint);
                    }

                    break;
            }
        }

        return sites;
    }

    /// <summary>
    /// True when the sub-graph induced by <paramref name="graph"/>'s vertices <em>minus</em>
    /// <paramref name="removed"/> is connected under the undirected projection (every remaining vertex
    /// reachable from every other). An empty remainder (all vertices removed) and a single-vertex remainder
    /// both count as connected — there is nothing to disconnect.
    /// </summary>
    /// <param name="graph">The roadmap to test.</param>
    /// <param name="removed">Site ids to exclude from the remainder (e.g. a sealed blocking closure).</param>
    /// <returns><see langword="true"/> when the remainder is connected; otherwise <see langword="false"/>.</returns>
    public static bool RemainderConnected(RoadmapGraph graph, IReadOnlySet<string> removed)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(removed);

        var remaining = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vertex in graph.Vertices)
        {
            if (!removed.Contains(vertex))
                remaining.Add(vertex);
        }

        return IsConnected(graph, remaining);
    }

    /// <summary>
    /// True when <paramref name="vertices"/> form a connected sub-graph of <paramref name="graph"/> under the
    /// undirected projection. A vertex outside the graph is treated as having no incident edges. Zero or one
    /// vertex is trivially connected.
    /// </summary>
    /// <param name="graph">The roadmap whose adjacency is read.</param>
    /// <param name="vertices">The vertex subset to test for connectivity.</param>
    /// <returns><see langword="true"/> when the subset is connected; otherwise <see langword="false"/>.</returns>
    public static bool IsConnected(RoadmapGraph graph, IReadOnlySet<string> vertices)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(vertices);

        if (vertices.Count <= 1)
            return true;

        // Deterministic BFS from the ordinal-least vertex over the undirected projection, confined to the
        // induced subset; the subset is connected iff the traversal visits every member.
        var start = MinOrdinal(vertices);
        var visited = BreadthFirst(graph, start, vertices);
        return visited.Count == vertices.Count;
    }

    /// <summary>
    /// True when removing <paramref name="vertex"/> from the induced sub-graph <paramref name="vertices"/>
    /// disconnects what remains — i.e. <paramref name="vertex"/> is an articulation point (cut vertex) of that
    /// connected subset. A vertex whose removal leaves zero or one vertex is, by convention, not an
    /// articulation point.
    /// </summary>
    /// <param name="graph">The roadmap whose adjacency is read.</param>
    /// <param name="vertices">The connected vertex subset the articulation test is relative to.</param>
    /// <param name="vertex">The candidate cut vertex (must be a member of <paramref name="vertices"/>).</param>
    /// <returns><see langword="true"/> when <paramref name="vertex"/> is an articulation point of the subset.</returns>
    public static bool IsArticulationPoint(RoadmapGraph graph, IReadOnlySet<string> vertices, string vertex)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertex);

        if (!vertices.Contains(vertex) || vertices.Count <= 2)
            return false;

        var remainder = new HashSet<string>(vertices, StringComparer.Ordinal);
        remainder.Remove(vertex);
        return !IsConnected(graph, remainder);
    }

    /// <summary>
    /// The site ids reachable (under the undirected projection, confined to <paramref name="allowed"/>) from
    /// <paramref name="start"/>, inclusive of <paramref name="start"/> itself. Deterministic: neighbours are
    /// visited in ordinal order.
    /// </summary>
    private static HashSet<string> BreadthFirst(RoadmapGraph graph, string start, IReadOnlySet<string> allowed)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (!allowed.Contains(start))
            return visited;

        var queue = new Queue<string>();
        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var neighbour in UndirectedNeighbours(graph, current, allowed))
            {
                if (visited.Add(neighbour))
                    queue.Enqueue(neighbour);
            }
        }

        return visited;
    }

    /// <summary>
    /// The ordinal-sorted undirected neighbours of <paramref name="site"/> that lie within
    /// <paramref name="allowed"/>: every out-neighbour of <paramref name="site"/>, plus every vertex of which
    /// <paramref name="site"/> is itself an out-neighbour (so a one-way lane still connects both endpoints for
    /// connectivity purposes).
    /// </summary>
    private static IEnumerable<string> UndirectedNeighbours(
        RoadmapGraph graph,
        string site,
        IReadOnlySet<string> allowed)
    {
        var neighbours = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var successor in graph.Neighbours(site))
        {
            if (allowed.Contains(successor))
                neighbours.Add(successor);
        }

        // Reverse adjacency: scan allowed vertices that list `site` as a successor. The roadmap is small
        // (warehouse roadmaps are sparse) and this keeps the projection correct for one-way lanes without
        // depending on a reverse-adjacency accessor the frozen RoadmapGraph does not expose.
        foreach (var candidate in allowed)
        {
            if (!string.Equals(candidate, site, StringComparison.Ordinal) &&
                GraphHasEdge(graph, candidate, site))
            {
                neighbours.Add(candidate);
            }
        }

        return neighbours;
    }

    private static bool GraphHasEdge(RoadmapGraph graph, string from, string to)
    {
        foreach (var successor in graph.Neighbours(from))
        {
            if (string.Equals(successor, to, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Splits a <c>"start-end"</c> lane id into its two endpoint site ids; empty on a malformed id.</summary>
    private static IEnumerable<string> LaneEndpoints(string laneId)
    {
        var dash = laneId.IndexOf('-');
        if (dash <= 0 || dash >= laneId.Length - 1)
            yield break;

        yield return laneId[..dash];
        yield return laneId[(dash + 1)..];
    }

    private static string MinOrdinal(IReadOnlySet<string> vertices)
    {
        string? min = null;
        foreach (var vertex in vertices)
        {
            if (min is null || string.CompareOrdinal(vertex, min) < 0)
                min = vertex;
        }

        return min!;
    }
}
