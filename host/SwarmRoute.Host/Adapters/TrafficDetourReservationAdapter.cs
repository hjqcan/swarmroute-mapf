using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Real-enough <see cref="IDetourReservationService"/>: reserves the chosen avoidance site for the victim by
/// going through TrafficControl's <see cref="ITrafficCoordinatorAppService.TryReserveAsync"/> — the same write seam
/// the control loop uses, so the detour can never create a new collision (invariant I1). The detour is
/// modelled as a hold on the avoid site (and, via the table's topology closure, its parent block + interference)
/// for a bounded window.
/// </summary>
/// <remarks>
/// v0 is deliberately only a bounded destination hold over <c>[now, now + 60000)</c>. It is not a completed detour
/// resolution because the victim's current pose and path-to-avoid-site reservation belong to the not-yet-present
/// fleet-state/dispatch integration. This adapter only prevents overclaiming while still exercising the real
/// allocator and respecting every existing lease.
/// </remarks>
public sealed class TrafficDetourReservationAdapter : IDetourReservationService
{
    /// <summary>Length of the detour hold window in fleet-clock ms (bounded so the lease sweeps out).</summary>
    private const long DetourHoldMs = 60_000;

    private readonly ITrafficCoordinatorAppService _traffic;
    private readonly IFleetClock _clock;

    public TrafficDetourReservationAdapter(ITrafficCoordinatorAppService traffic, IFleetClock clock)
    {
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveDetourAsync(
        string victimAgentId,
        string avoidanceSiteId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId) || string.IsNullOrWhiteSpace(avoidanceSiteId))
            return false;

        var nowMs = _clock.NowMs;
        var cell = new SpaceTimeCell(
            RoadmapGraph.SiteRef(avoidanceSiteId),
            new TimeInterval(nowMs, nowMs + DetourHoldMs));
        var path = new SpaceTimePath(new[] { cell });

        var outcome = await _traffic.TryReserveAsync(path, victimAgentId, cancellationToken)
            .ConfigureAwait(false);
        return outcome == AllocationOutcome.Granted;
    }
}
