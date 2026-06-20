using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Domain.Endpoints;

/// <summary>
/// The seam that derives a <b>well-formed</b> <see cref="EndpointSet"/> from a roadmap and validates the
/// well-formedness invariant (端點策略).
/// <para>
/// "Well-formed" is the standard lifelong-MAPF endpoint condition adapted to FMS: long-term endpoints
/// (workstations / parkings / chargers / buffers — where a vehicle may rest indefinitely) are carved out of the
/// roadmap so that the remaining <em>transit core</em> stays connected, every endpoint keeps at least one egress
/// edge back into the core, and no endpoint is a cut vertex of the roadmap. Those three properties together
/// guarantee a vehicle parked at any endpoint can never wall off through-traffic, which is what keeps a
/// lifelong scenario from deadlocking on endpoint occupation.
/// </para>
/// </summary>
public interface IEndpointPolicy
{
    /// <summary>
    /// Builds a well-formed <see cref="EndpointSet"/> over <paramref name="graph"/> sized for
    /// <paramref name="agvCount"/> vehicles, seeded by <paramref name="seed"/> for reproducibility.
    /// </summary>
    /// <param name="graph">The roadmap the endpoints are drawn from.</param>
    /// <param name="agvCount">The fleet size the endpoint budget is sized for; must be &gt;= 0.</param>
    /// <param name="seed">Deterministic seed — the same (graph, count, seed) yields the same set.</param>
    /// <returns>A well-formed endpoint set satisfying <see cref="IsValidEndpointSet"/>.</returns>
    EndpointSet BuildEndpoints(RoadmapGraph graph, int agvCount, int seed);

    /// <summary>
    /// Tests whether <paramref name="endpoints"/> is well-formed against <paramref name="graph"/>: the transit
    /// core (all vertices minus every endpoint) is connected, every endpoint has at least one neighbour in the
    /// core, and no endpoint is an articulation point of the roadmap.
    /// </summary>
    /// <param name="graph">The roadmap to validate against.</param>
    /// <param name="endpoints">The candidate endpoint partition.</param>
    /// <returns><see langword="true"/> when the set is well-formed; otherwise <see langword="false"/>.</returns>
    bool IsValidEndpointSet(RoadmapGraph graph, EndpointSet endpoints);
}
