using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Application.Services;

/// <summary>
/// The TrafficControl implementation of PathPlanning's <see cref="IReservationQuery"/> seam (frozen contract).
/// Hands the planner an <see cref="IReservationView"/> backed by a point-in-time copy of the singleton,
/// authoritative <see cref="ReservationTable"/>, so SIPP-style search reads real safe intervals instead of the
/// always-free stub. Registering this overrides
/// PathPlanning's <c>NullReservationQuery</c> default.
/// </summary>
/// <remarks>
/// v0 holds a single global reservation table (one fleet, one clock), so <c>roadmapId</c> is accepted for
/// contract-shape compatibility but the same table is snapshotted regardless. The returned view is immutable:
/// later grants/releases are visible only through a fresh <see cref="GetView(Guid)"/> call.
/// </remarks>
public sealed class ReservationService : IReservationQuery
{
    private readonly ReservationTable _table;

    public ReservationService(ReservationTable table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <inheritdoc />
    public IReservationView GetView(Guid roadmapId) => _table.CreateSnapshotView();
}
