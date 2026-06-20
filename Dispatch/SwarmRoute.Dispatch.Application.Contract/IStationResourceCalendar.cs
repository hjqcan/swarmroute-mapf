using SwarmRoute.Dispatch.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Application.Contract;

/// <summary>
/// The long-lease calendar for station service windows: the seam through which the station scheduler reserves and
/// releases a station's dock point plus its blocking closure for the duration of a service (站點資源日曆).
/// <para>
/// <b>FMS semantics.</b> A station service window is a <em>long interval-lease</em> over the station's dock-point
/// control point (<see cref="ResourceKind.CP"/>) <em>and</em> its
/// <see cref="StationDefinition.BlockingClosure"/> zone, expressed as a single half-open
/// <see cref="TimeInterval"/> <c>[start, start + serviceMs)</c>. The lease is granted <b>only when that whole
/// closure is free</b> for the entire window — booking it severs (for a <see cref="StationType.HardBlocking"/>
/// station) or degrades nearby transit flow until the window elapses or is released. Per ADR-F2 this is realised
/// on the existing frozen Kernel / reservation vocabulary rather than a new <c>ResourceKind.Station</c>;
/// implementations (the Foundations phase, on top of <c>TrafficControl</c>) translate the window into the
/// underlying dock-point + closure reservations.
/// </para>
/// </summary>
public interface IStationResourceCalendar
{
    /// <summary>
    /// Tests, without taking the lease, whether the given station's dock point and blocking closure are entirely
    /// free for <paramref name="window"/>. A pure read — no state changes.
    /// </summary>
    /// <param name="stationId">The station whose dock point + closure to check.</param>
    /// <param name="window">The proposed service window, half-open <c>[StartMs, EndMs)</c>.</param>
    /// <returns><see langword="true"/> if the whole closure can be held for the window; otherwise <see langword="false"/>.</returns>
    bool CanReserveServiceWindow(string stationId, TimeInterval window);

    /// <summary>
    /// Attempts to atomically reserve <paramref name="station"/>'s dock point and blocking closure for
    /// <paramref name="agentId"/> across <paramref name="window"/>. Succeeds only when the whole closure is free;
    /// on success the window is held until <see cref="ReleaseServiceWindowAsync"/> (or natural expiry per the
    /// implementation's lease model).
    /// </summary>
    /// <param name="station">The full station definition (supplies the dock point and blocking closure).</param>
    /// <param name="agentId">The vehicle the lease is granted to.</param>
    /// <param name="window">The service window to reserve, half-open <c>[StartMs, EndMs)</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the window was reserved; <see langword="false"/> if the closure was contended.</returns>
    Task<bool> TryReserveServiceWindowAsync(
        StationDefinition station,
        string agentId,
        TimeInterval window,
        CancellationToken ct = default);

    /// <summary>
    /// Releases any service-window lease held by <paramref name="agentId"/> on <paramref name="stationId"/>,
    /// freeing the dock point and blocking closure for other traffic. Idempotent: releasing a station the agent
    /// does not hold is a no-op.
    /// </summary>
    /// <param name="stationId">The station whose lease to release.</param>
    /// <param name="agentId">The vehicle whose lease to release.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseServiceWindowAsync(
        string stationId,
        string agentId,
        CancellationToken ct = default);
}
