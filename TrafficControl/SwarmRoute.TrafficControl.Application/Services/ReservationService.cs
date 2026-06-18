using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Application.Services;

/// <summary>
/// The live implementation of PathPlanning's <see cref="IReservationQuery"/> seam (frozen contract). Hands the
/// planner an <see cref="IReservationView"/> backed by the singleton, authoritative <see cref="ReservationTable"/>,
/// so SIPP-style search reads real safe intervals instead of the always-free stub. Registering this overrides
/// PathPlanning's <c>NullReservationQuery</c> default.
/// </summary>
/// <remarks>
/// v0 holds a single global reservation table (one fleet, one clock), so <c>roadmapId</c> is accepted for
/// contract-shape compatibility but the same view is returned regardless. The view reads the live table; the
/// planner is expected to consume it within one planning cycle.
/// </remarks>
public sealed class ReservationService : IReservationQuery
{
    private readonly ReservationTable _table;

    public ReservationService(ReservationTable table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <inheritdoc />
    public IReservationView GetView(Guid roadmapId) => new ReservationTableView(_table);

    /// <summary>
    /// A thin <see cref="IReservationView"/> adapter over the live <see cref="ReservationTable"/>. The table's
    /// own methods already take the aggregate lock, so reads are consistent.
    /// </summary>
    private sealed class ReservationTableView : IReservationView
    {
        private readonly ReservationTable _table;

        public ReservationTableView(ReservationTable table) => _table = table;

        public IEnumerable<SafeInterval> FreeIntervals(ResourceRef resource) => _table.FreeIntervals(resource);

        public bool IsFree(ResourceRef resource, TimeInterval interval) => _table.IsFree(resource, interval);
    }
}
