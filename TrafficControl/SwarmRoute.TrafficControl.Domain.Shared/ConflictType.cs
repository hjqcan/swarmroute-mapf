namespace SwarmRoute.TrafficControl.Domain.Shared;

/// <summary>
/// The kind of spatio-temporal conflict detected between a candidate path and the existing reservations.
/// Ports the MAPF conflict taxonomy (vertex / edge-swap / following) and adds the AGV-specific
/// interference conflict that the original engine modelled via interference sites/lines.
/// </summary>
public enum ConflictType
{
    /// <summary>Two agents occupy the same resource at the same time (vertex / same-cell conflict).</summary>
    VertexSame,

    /// <summary>Two agents traverse the same lane in opposite directions and swap places (edge / swap conflict).</summary>
    EdgeSwap,

    /// <summary>A trailing agent enters a resource before the leading agent has cleared it (following conflict).</summary>
    Following,

    /// <summary>Two agents occupy mutually-interfering resources at the same time (interference conflict).</summary>
    Interference
}
