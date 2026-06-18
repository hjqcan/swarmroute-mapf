using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Reservations;

/// <summary>
/// The in-process read seam between PathPlanning and TrafficControl, <b>declared here by PathPlanning and
/// implemented by TrafficControl</b> (per the frozen cross-context contract). It is the provider of a live
/// <see cref="IReservationView"/> for a given roadmap: the planner asks for the view once per plan and then
/// reads it (potentially thousands of times) during search, without mutating it.
/// </summary>
/// <remarks>
/// <para>
/// Kept deliberately minimal — a single factory method. The <see cref="IReservationView"/> it returns is the
/// Kernel contract (<c>IsFree</c> / <c>FreeIntervals</c>); the write seam (<c>TryReserve</c>/<c>Release</c>)
/// lives in TrafficControl.Application.Contract and is NOT part of this interface.
/// </para>
/// <para>
/// v0 ships <see cref="NullReservationQuery"/> (returns an <see cref="AlwaysFreeReservationView"/>) so
/// PathPlanning builds and runs standalone. TrafficControl overrides the registration with its
/// reservation-table-backed implementation once it lands (WS4); the planner is unchanged.
/// </para>
/// </remarks>
public interface IReservationQuery
{
    /// <summary>
    /// Returns the current read-only reservation view for <paramref name="roadmapId"/>. Implementations
    /// return a consistent snapshot for the duration of one plan; callers must not assume it updates live.
    /// </summary>
    IReservationView GetView(Guid roadmapId);
}
