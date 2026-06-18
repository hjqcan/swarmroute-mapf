using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.TrafficControl.Application.Contract.Dtos;

/// <summary>
/// Operator-facing snapshot of the reservation table: the active leases, the contended requests and the
/// table's optimistic-concurrency version.
/// </summary>
/// <param name="StateVersion">The reservation table's current <c>StateVersion</c>.</param>
/// <param name="Leases">All active leases.</param>
/// <param name="ContendedRequests">All queued / contended requests (the "Waits" edges).</param>
public sealed record OccupancyDto(
    long StateVersion,
    IReadOnlyList<ResourceLeaseDto> Leases,
    IReadOnlyList<ContendedRequestDto> ContendedRequests);

/// <summary>Operator-facing view of a contended (queued) reservation request.</summary>
/// <param name="AgentId">The waiting agent.</param>
/// <param name="ResourceKind">The kind of resource being waited on.</param>
/// <param name="ResourceId">The resource being waited on.</param>
/// <param name="HadWaitedTimeSeconds">Accumulated wait time (aged for fairness).</param>
/// <param name="Priority">The request's scheduling priority.</param>
public sealed record ContendedRequestDto(
    string AgentId,
    ResourceKind ResourceKind,
    string ResourceId,
    int HadWaitedTimeSeconds,
    int Priority);
