using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Cbs;

/// <summary>Whether two agents collide on a control point (vertex) or by traversing one lane head-on (edge).</summary>
public enum CbsConflictKind
{
    /// <summary>Both agents occupy the same control point over overlapping intervals.</summary>
    Vertex,

    /// <summary>The two agents traverse the same physical lane in opposite directions over overlapping intervals.</summary>
    Edge
}

/// <summary>
/// The chosen conflict between two agents' paths in a CBS node. <see cref="AgentA"/> and <see cref="AgentB"/> are
/// in canonical ordinal order. For a vertex conflict <see cref="ResourceA"/> == <see cref="ResourceB"/> (the
/// shared CP); for an edge conflict they are the two opposite directed lanes (A's and B's own direction).
/// <see cref="Tick"/> is the first instant of the overlap — the single tick the two child constraints forbid.
/// </summary>
public sealed record CbsConflict(
    CbsConflictKind Kind,
    string AgentA,
    string AgentB,
    ResourceRef ResourceA,
    ResourceRef ResourceB,
    long Tick);
