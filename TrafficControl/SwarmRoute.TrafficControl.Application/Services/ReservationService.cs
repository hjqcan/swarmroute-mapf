using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.ValueObjects;

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
    public IReservationView GetView(Guid roadmapId) => new ReservationTableView(_table.ActiveLeases);

    /// <summary>
    /// Immutable <see cref="IReservationView"/> over the active leases copied at query time.
    /// </summary>
    private sealed class ReservationTableView : IReservationView
    {
        private readonly IReadOnlyList<ResourceLease> _leases;

        public ReservationTableView(IReadOnlyList<ResourceLease> leases) => _leases = leases;

        public IEnumerable<SafeInterval> FreeIntervals(ResourceRef resource)
        {
            var result = new List<SafeInterval>();
            long cursor = 0;

            var leases = _leases
                .Where(lease => ResourcesConflict(resource, lease.Resource))
                .OrderBy(lease => lease.Interval.StartMs)
                .ToList();

            if (leases.Count > 0)
            {
                long coveredEnd = long.MinValue;
                foreach (var lease in leases)
                {
                    var s = lease.Interval.StartMs;
                    var e = lease.Interval.EndMs;

                    if (s > coveredEnd)
                    {
                        if (s > cursor)
                            result.Add(new SafeInterval(resource, new TimeInterval(cursor, s)));
                        coveredEnd = e;
                    }
                    else if (e > coveredEnd)
                    {
                        coveredEnd = e;
                    }

                    if (coveredEnd > cursor)
                        cursor = coveredEnd;
                }
            }

            if (cursor < long.MaxValue)
                result.Add(new SafeInterval(resource, new TimeInterval(cursor, long.MaxValue)));

            return result;
        }

        public bool IsFree(ResourceRef resource, TimeInterval interval)
            => !_leases.Any(lease => ResourcesConflict(resource, lease.Resource) && lease.Interval.Overlaps(interval));

        private static bool ResourcesConflict(ResourceRef a, ResourceRef b)
            => a.Equals(b) || IsReversedLane(a, b);

        private static bool IsReversedLane(ResourceRef a, ResourceRef b)
        {
            if (a.Kind != ResourceKind.Lane || b.Kind != ResourceKind.Lane)
                return false;

            var dashA = a.Id.IndexOf('-');
            var dashB = b.Id.IndexOf('-');
            if (dashA <= 0 || dashB <= 0)
                return false;

            var aStart = a.Id.AsSpan(0, dashA);
            var aEnd = a.Id.AsSpan(dashA + 1);
            var bStart = b.Id.AsSpan(0, dashB);
            var bEnd = b.Id.AsSpan(dashB + 1);

            return aStart.SequenceEqual(bEnd) && aEnd.SequenceEqual(bStart);
        }
    }
}
