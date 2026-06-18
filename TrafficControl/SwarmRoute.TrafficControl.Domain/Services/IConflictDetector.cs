using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.ValueObjects;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// Classifies the spatio-temporal conflicts between a candidate <see cref="SpaceTimePath"/> for one agent and
/// the live leases held by others. Ports the MAPF conflict taxonomy (vertex / edge-swap / following) and adds
/// the AGV interference conflict the original engine modelled via interference sites/lines.
/// </summary>
public interface IConflictDetector
{
    /// <summary>
    /// Returns every conflict the <paramref name="candidate"/> path for <paramref name="agentId"/> would have
    /// against the current state of <paramref name="table"/>. An empty list means the path is conflict-free and
    /// could be granted.
    /// </summary>
    IReadOnlyList<Conflict> Detect(ReservationTable table, SpaceTimePath candidate, string agentId);
}
