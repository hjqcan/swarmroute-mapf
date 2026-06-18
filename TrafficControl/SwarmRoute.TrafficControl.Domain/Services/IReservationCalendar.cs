using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.ValueObjects;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// Free-interval math over the live leases — the calendar of when each resource is available. In v0 this is
/// only used to answer the read view; at v1 it is exactly the data the SIPP planner consumes (safe intervals
/// per resource), which is why it is modelled as its own domain service from the start.
/// </summary>
public interface IReservationCalendar
{
    /// <summary>The maximal conflict-free (safe) intervals for <paramref name="resource"/>, ordered along the clock.</summary>
    IReadOnlyList<SafeInterval> FreeIntervals(ReservationTable table, ResourceRef resource);

    /// <summary>True when <paramref name="resource"/> is entirely free over the whole half-open <paramref name="interval"/>.</summary>
    bool IsFree(ReservationTable table, ResourceRef resource, TimeInterval interval);

    /// <summary>
    /// The earliest start &gt;= <paramref name="earliestStartMs"/> at which <paramref name="resource"/> is free
    /// for a contiguous window of <paramref name="durationMs"/>, or null if none exists before end-of-time.
    /// The seed of SIPP's "earliest arrival" step.
    /// </summary>
    long? EarliestFreeStart(ReservationTable table, ResourceRef resource, long earliestStartMs, long durationMs);
}
