using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Reservations;

/// <summary>
/// A v0 stub <see cref="IReservationView"/> that treats every resource as free for all time. Reservation
/// awareness arrives in v1 (SIPP + TrafficControl); until then the planner reads this view (so the call sites
/// are wired) but every query reports "free".
/// </summary>
/// <remarks>
/// <see cref="IsFree"/> always returns <c>true</c>; <see cref="FreeIntervals"/> yields a single maximal open
/// interval <c>[0, long.MaxValue)</c> for the queried resource.
/// </remarks>
public sealed class AlwaysFreeReservationView : IReservationView
{
    /// <summary>A shared, stateless instance (the view holds no per-resource state).</summary>
    public static AlwaysFreeReservationView Instance { get; } = new();

    /// <inheritdoc />
    public IEnumerable<SafeInterval> FreeIntervals(ResourceRef resource)
    {
        yield return new SafeInterval(resource, new TimeInterval(0, long.MaxValue));
    }

    /// <inheritdoc />
    public bool IsFree(ResourceRef resource, TimeInterval interval) => true;
}
