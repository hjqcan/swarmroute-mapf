using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Real-enough <see cref="IDetourReservationService"/>: reserves the chosen avoidance site for the victim by
/// going through TrafficControl's <see cref="ITrafficCoordinatorAppService.TryReserve"/> — the same write seam
/// the control loop uses, so the detour can never create a new collision (invariant I1). The detour is
/// modelled as a hold on the avoid site (and, via the table's topology closure, its parent block + interference)
/// for a bounded window starting "now".
/// </summary>
/// <remarks>
/// v0 keeps the detour minimal (a single-cell hold on the destination avoid site) rather than planning a full
/// multi-hop route, because the victim's exact current pose is owned by the (not-yet-present) fleet-state
/// context; the reservation still exercises the real allocator and respects every existing lease. If the avoid
/// site is already taken the detour is reported as not granted and the resolver escalates.
/// </remarks>
public sealed class TrafficDetourReservationAdapter : IDetourReservationService
{
    /// <summary>Length of the detour hold window in fleet-clock ms (bounded so the lease sweeps out).</summary>
    private const long DetourHoldMs = 60_000;

    private readonly ITrafficCoordinatorAppService _traffic;

    public TrafficDetourReservationAdapter(ITrafficCoordinatorAppService traffic)
        => _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));

    /// <inheritdoc />
    public bool TryReserveDetour(string victimAgentId, string avoidanceSiteId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId) || string.IsNullOrWhiteSpace(avoidanceSiteId))
            return false;

        var cell = new SpaceTimeCell(
            RoadmapGraph.SiteRef(avoidanceSiteId),
            new TimeInterval(0, DetourHoldMs));
        var path = new SpaceTimePath(new[] { cell });

        return _traffic.TryReserve(path, victimAgentId) == AllocationOutcome.Granted;
    }
}
