using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Reservations;

/// <summary>
/// The v0 default <see cref="IReservationQuery"/>: hands out an <see cref="AlwaysFreeReservationView"/> for
/// any roadmap, so PathPlanning builds and plans standalone with no TrafficControl present. TrafficControl
/// overrides this registration with its live reservation-table-backed query once WS4 lands.
/// </summary>
public sealed class NullReservationQuery : IReservationQuery
{
    /// <inheritdoc />
    public IReservationView GetView(Guid roadmapId) => AlwaysFreeReservationView.Instance;
}
