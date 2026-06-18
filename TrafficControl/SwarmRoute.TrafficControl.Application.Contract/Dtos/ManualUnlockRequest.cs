using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.TrafficControl.Application.Contract.Dtos;

/// <summary>
/// Operator override request: force-release the leases an agent holds on specific resources (or all of them).
/// Used from the TrafficControl operator console to break a stuck hold manually.
/// </summary>
/// <param name="AgentId">The agent whose leases to release.</param>
/// <param name="Resources">
/// The specific resources to release (with their parent-block + interference closure). When null or empty,
/// every lease held by the agent is released.
/// </param>
public sealed record ManualUnlockRequest(
    string AgentId,
    IReadOnlyList<ResourceRef>? Resources);
