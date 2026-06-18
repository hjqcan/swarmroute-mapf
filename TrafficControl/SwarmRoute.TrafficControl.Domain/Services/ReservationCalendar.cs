using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// Default <see cref="IReservationCalendar"/> implemented directly over the aggregate's interval indices.
/// Stateless → safe as a singleton.
/// </summary>
public sealed class ReservationCalendar : IReservationCalendar
{
    /// <inheritdoc />
    public IReadOnlyList<SafeInterval> FreeIntervals(ReservationTable table, ResourceRef resource)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.FreeIntervals(resource);
    }

    /// <inheritdoc />
    public bool IsFree(ReservationTable table, ResourceRef resource, TimeInterval interval)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.IsFree(resource, interval);
    }

    /// <inheritdoc />
    public long? EarliestFreeStart(ReservationTable table, ResourceRef resource, long earliestStartMs, long durationMs)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (durationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMs));

        foreach (var safe in table.FreeIntervals(resource))
        {
            var windowStart = Math.Max(safe.Interval.StartMs, earliestStartMs);
            if (windowStart > safe.Interval.EndMs)
                continue;

            // Does a [windowStart, windowStart+duration) fit inside this safe interval?
            if (safe.Interval.EndMs == long.MaxValue || safe.Interval.EndMs - windowStart >= durationMs)
                return windowStart;
        }
        return null;
    }
}
