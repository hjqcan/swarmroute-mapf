namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// Read-only view over the current reservation state, as seen by a planner.
/// TrafficControl owns the authoritative reservation table and exposes it through this in-process
/// interface; PathPlanning reads (potentially thousands of times per plan) but never mutates.
/// </summary>
/// <remarks>
/// This is the frozen read seam between PathPlanning and TrafficControl. The write seam
/// (<c>TryReserve</c>/<c>Release</c>) lives in TrafficControl.Application.Contract.
/// </remarks>
public interface IReservationView
{
    /// <summary>
    /// Enumerates the maximal conflict-free (safe) intervals for <paramref name="resource"/>,
    /// ordered along the fleet clock. Used by SIPP-style search.
    /// </summary>
    IEnumerable<SafeInterval> FreeIntervals(ResourceRef resource);

    /// <summary>
    /// True when <paramref name="resource"/> is entirely free for the whole half-open
    /// <paramref name="interval"/> (no overlapping reservation by any other agent).
    /// </summary>
    bool IsFree(ResourceRef resource, TimeInterval interval);
}
