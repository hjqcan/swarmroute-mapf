using SwarmRoute.TrafficControl.Application.Contract.Dtos;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Application.Services;

/// <summary>
/// Operator-console application service: read live occupancy and perform manual overrides against the
/// singleton <see cref="ReservationTable"/>. Maps domain leases / requests to the contract DTOs.
/// </summary>
public sealed class TrafficControlOperatorAppService : ITrafficControlOperatorAppService
{
    private readonly ReservationTable _table;

    public TrafficControlOperatorAppService(ReservationTable table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <inheritdoc />
    public OccupancyDto GetOccupancy()
    {
        var leases = _table.ActiveLeases
            .Select(l => new ResourceLeaseDto(
                l.Resource.Kind, l.Resource.Id, l.AgentId, l.Interval.StartMs, l.Interval.EndMs, l.State))
            .ToList();

        var contended = _table.ContendedRequests
            .Select(r => new ContendedRequestDto(r.AgentId, r.ResourceId, r.HadWaitedTime, r.Priority))
            .ToList();

        return new OccupancyDto(_table.StateVersion, leases, contended);
    }

    /// <inheritdoc />
    public int ManualUnlock(ManualUnlockRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId))
            throw new ArgumentException("agentId must be provided.", nameof(request));

        var freed = request.Resources is { Count: > 0 }
            ? _table.ReleaseBehind(request.AgentId, request.Resources)
            : _table.ReleaseAll(request.AgentId);

        return freed.Count;
    }
}
