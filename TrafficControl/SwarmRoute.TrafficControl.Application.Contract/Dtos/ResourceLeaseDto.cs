using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Application.Contract.Dtos;

/// <summary>Operator-facing view of a single live resource lease.</summary>
/// <param name="ResourceKind">The kind of resource held (CP / Lane / Block / Zone).</param>
/// <param name="ResourceId">The resource id.</param>
/// <param name="AgentId">The agent holding the lease.</param>
/// <param name="StartMs">Inclusive start of the lease window (fleet-clock ms).</param>
/// <param name="EndMs">Exclusive end of the lease window (fleet-clock ms).</param>
/// <param name="State">The lease lifecycle state.</param>
public sealed record ResourceLeaseDto(
    ResourceKind ResourceKind,
    string ResourceId,
    string AgentId,
    long StartMs,
    long EndMs,
    LeaseState State);
