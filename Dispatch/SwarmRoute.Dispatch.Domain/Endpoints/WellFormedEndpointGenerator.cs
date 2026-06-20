using SwarmRoute.Dispatch.Domain.Topology;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Domain.Endpoints;

/// <summary>
/// The deterministic <see cref="IEndpointPolicy"/>: carves a well-formed <see cref="EndpointSet"/> out of a
/// <see cref="RoadmapGraph"/> (良構端點產生器).
/// <para>
/// <b>Algorithm.</b> A vertex is an eligible endpoint only when carving it out cannot harm transit: it must have
/// at least one incident edge (an egress back into the core) and must not be an articulation point of the
/// roadmap. Eligible vertices are visited in a seed-shuffled order and greedily accepted one at a time, each
/// acceptance re-checked so the running transit core (all vertices minus the accepted endpoints) stays
/// connected and the just-added endpoint keeps an egress into that shrinking core. The accepted endpoints are
/// then dealt round-robin into the four FMS roles (workstations, parkings, buffers, chargers) in ordinal order,
/// so the partition is stable. The result satisfies <see cref="IsValidEndpointSet"/> by construction.
/// </para>
/// <para>
/// <b>Determinism.</b> Every set the algorithm iterates is ordinal-ordered before use, and the only randomness
/// is a <see cref="seed"/>-seeded Fisher–Yates shuffle of the eligible-vertex list; identical
/// <c>(graph, agvCount, seed)</c> inputs therefore always yield an identical set. The generator never mutates
/// the graph.
/// </para>
/// </summary>
public sealed class WellFormedEndpointGenerator : IEndpointPolicy
{
    /// <inheritdoc />
    public EndpointSet BuildEndpoints(RoadmapGraph graph, int agvCount, int seed)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (agvCount < 0)
            throw new ArgumentException($"AGV count must be >= 0, but was {agvCount}.", nameof(agvCount));

        var allVertices = OrdinalSet(graph.Vertices);

        // A vertex may be carved out only if doing so cannot wall off the core: it has an egress edge and is not
        // a cut vertex of the whole roadmap. Visited in ordinal order, then deterministically shuffled by seed.
        var eligible = new List<string>();
        foreach (var vertex in allVertices.OrderBy(v => v, StringComparer.Ordinal))
        {
            if (HasIncidentEdge(graph, vertex) &&
                !TransitCoreTopology.IsArticulationPoint(graph, allVertices, vertex))
            {
                eligible.Add(vertex);
            }
        }

        Shuffle(eligible, seed);

        // Greedily accept endpoints subject to the well-formedness invariant. Two adjacent vertices are never both
        // carved out: keeping the endpoint set an *independent set* of the roadmap guarantees every endpoint's
        // neighbours all stay in the core, so each endpoint keeps its egress no matter how many others are carved
        // later (an incremental "neighbour in the running core" check is not enough — a later carve-out can erode an
        // earlier endpoint's only egress). Connectivity of the shrinking core is still checked per acceptance.
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in eligible)
        {
            if (IsAdjacentToAny(graph, candidate, endpoints))
                continue;

            endpoints.Add(candidate);

            var core = Remainder(allVertices, endpoints);
            if (TransitCoreTopology.IsConnected(graph, core) && HasNeighbourIn(graph, candidate, core))
                continue;

            // Carving this one out would disconnect the core (or strand the endpoint); leave it in the core.
            endpoints.Remove(candidate);
        }

        return Partition(endpoints, agvCount);
    }

    /// <inheritdoc />
    public bool IsValidEndpointSet(RoadmapGraph graph, EndpointSet endpoints)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(endpoints);

        var allVertices = OrdinalSet(graph.Vertices);
        var endpointSites = AllEndpoints(endpoints);

        // Invariant 0: every declared endpoint is a real vertex of the roadmap.
        foreach (var endpoint in endpointSites)
        {
            if (!allVertices.Contains(endpoint))
                return false;
        }

        var core = Remainder(allVertices, endpointSites);

        // Invariant 1: the transit core (V minus endpoints) is connected.
        if (!TransitCoreTopology.IsConnected(graph, core))
            return false;

        foreach (var endpoint in endpointSites)
        {
            // Invariant 2: every endpoint has >= 1 neighbour in the transit core (egress).
            if (!HasNeighbourIn(graph, endpoint, core))
                return false;

            // Invariant 3: no endpoint is an articulation point of the roadmap.
            if (TransitCoreTopology.IsArticulationPoint(graph, allVertices, endpoint))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Deals the accepted endpoint sites round-robin into the four FMS roles in ordinal order. Parkings are
    /// filled first so an <paramref name="agvCount"/>-sized fleet has somewhere to rest; the remaining roles
    /// follow in the fixed order workstation → buffer → charger. With fewer endpoints than roles some buckets
    /// stay empty — still well-formed, just sparse.
    /// </summary>
    private static EndpointSet Partition(IReadOnlySet<string> endpoints, int agvCount)
    {
        var workstations = new HashSet<string>(StringComparer.Ordinal);
        var parkings = new HashSet<string>(StringComparer.Ordinal);
        var buffers = new HashSet<string>(StringComparer.Ordinal);
        var chargers = new HashSet<string>(StringComparer.Ordinal);

        var ordered = endpoints.OrderBy(e => e, StringComparer.Ordinal).ToList();

        // Reserve up to agvCount endpoints as parkings (capped at what is available) so the fleet can be stored.
        var parkingBudget = Math.Min(agvCount, ordered.Count);
        var index = 0;
        for (; index < parkingBudget; index++)
            parkings.Add(ordered[index]);

        // Remaining endpoints cycle through the four roles, parkings included, so leftovers are spread evenly.
        for (var role = 0; index < ordered.Count; index++, role++)
        {
            switch (role % 4)
            {
                case 0:
                    workstations.Add(ordered[index]);
                    break;
                case 1:
                    parkings.Add(ordered[index]);
                    break;
                case 2:
                    buffers.Add(ordered[index]);
                    break;
                default:
                    chargers.Add(ordered[index]);
                    break;
            }
        }

        return new EndpointSet(workstations, parkings, buffers, chargers);
    }

    /// <summary>The union of all four endpoint roles as one ordinal set.</summary>
    private static IReadOnlySet<string> AllEndpoints(EndpointSet endpoints)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        all.UnionWith(endpoints.Workstations);
        all.UnionWith(endpoints.Parkings);
        all.UnionWith(endpoints.Buffers);
        all.UnionWith(endpoints.Chargers);
        return all;
    }

    /// <summary><paramref name="all"/> minus <paramref name="removed"/>, as a fresh ordinal set.</summary>
    private static IReadOnlySet<string> Remainder(IReadOnlySet<string> all, IReadOnlySet<string> removed)
    {
        var remainder = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vertex in all)
        {
            if (!removed.Contains(vertex))
                remainder.Add(vertex);
        }

        return remainder;
    }

    /// <summary>True when <paramref name="site"/> has at least one (directed, either way) incident edge.</summary>
    private static bool HasIncidentEdge(RoadmapGraph graph, string site)
    {
        foreach (var _ in graph.Neighbours(site))
            return true;

        // Also count incoming edges so a sink vertex with only inbound lanes is still egress-able.
        foreach (var other in graph.Vertices)
        {
            if (string.Equals(other, site, StringComparison.Ordinal))
                continue;

            foreach (var successor in graph.Neighbours(other))
            {
                if (string.Equals(successor, site, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="site"/> is an undirected neighbour of any vertex in <paramref name="others"/>
    /// (i.e. carving it out would put two endpoints adjacent). Cheap: scans only the candidate set.
    /// </summary>
    private static bool IsAdjacentToAny(RoadmapGraph graph, string site, IReadOnlySet<string> others)
    {
        if (others.Count == 0)
            return false;

        foreach (var successor in graph.Neighbours(site))
        {
            if (others.Contains(successor))
                return true;
        }

        foreach (var other in others)
        {
            foreach (var successor in graph.Neighbours(other))
            {
                if (string.Equals(successor, site, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="site"/> has at least one undirected neighbour inside <paramref name="core"/>.</summary>
    private static bool HasNeighbourIn(RoadmapGraph graph, string site, IReadOnlySet<string> core)
    {
        foreach (var successor in graph.Neighbours(site))
        {
            if (core.Contains(successor))
                return true;
        }

        foreach (var candidate in core)
        {
            foreach (var successor in graph.Neighbours(candidate))
            {
                if (string.Equals(successor, site, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>In-place deterministic Fisher–Yates shuffle seeded by <paramref name="seed"/>.</summary>
    private static void Shuffle(List<string> items, int seed)
    {
        var rng = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static IReadOnlySet<string> OrdinalSet(IEnumerable<string> values)
        => new HashSet<string>(values, StringComparer.Ordinal);
}
