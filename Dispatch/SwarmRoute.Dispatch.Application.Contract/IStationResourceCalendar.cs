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

    // ---- FMS-V3 look-ahead (additive) -------------------------------------------------------------------

    /// <summary>
    /// Reports the gaps between the windows this calendar currently holds on <paramref name="stationId"/>, within
    /// the look-ahead horizon <c>[fromMs, fromMs + horizonMs)</c> (空檔查詢). A pure read — no state changes.
    /// <para>
    /// <b>FMS-V3 semantics.</b> Lets the scheduler plan a <em>future</em> window instead of only "now": each
    /// returned half-open <see cref="TimeInterval"/> is a contiguous span, clipped to the horizon, in which no
    /// held window overlaps — so any sub-window of a returned gap is a candidate the scheduler may then confirm
    /// authoritatively via <see cref="TryReserveServiceWindowAsync"/>. Windows are returned in ascending start
    /// order and never overlap. Like <see cref="CanReserveServiceWindow"/>, this consults only this calendar's
    /// ledger — traffic booked by other paths on the reservation table is invisible here.
    /// </para>
    /// </summary>
    /// <param name="stationId">The station whose free gaps to report.</param>
    /// <param name="fromMs">The inclusive start of the look-ahead horizon, in fleet-clock milliseconds; must be &gt;= 0.</param>
    /// <param name="horizonMs">The length of the look-ahead horizon in milliseconds; must be &gt;= 0.</param>
    /// <returns>The free gaps within the horizon, ascending by start; empty when the horizon is fully occupied (or zero-length).</returns>
    IReadOnlyList<TimeInterval> FreeWindows(string stationId, long fromMs, long horizonMs);

    /// <summary>
    /// The earliest fleet-clock instant at or after <paramref name="fromMs"/> at which a
    /// <paramref name="durationMs"/>-long service window for <paramref name="station"/> could be granted against
    /// this calendar's ledger, or <see langword="null"/> when no such gap exists within the
    /// <paramref name="horizonMs"/> look-ahead (最早可授與起始). A pure read — no state changes.
    /// <para>
    /// Skips every busy span: returns <paramref name="fromMs"/> when the station is already free for the duration
    /// there, otherwise the start of the first gap long enough to host the window. The verdict is advisory (same
    /// ledger-only visibility as <see cref="FreeWindows"/>); the authoritative grant is still
    /// <see cref="TryReserveServiceWindowAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="station">The station the window would be reserved on (its id selects the ledger).</param>
    /// <param name="durationMs">The required service-window length in milliseconds; must be &gt; 0.</param>
    /// <param name="fromMs">The earliest instant the window may start, in fleet-clock milliseconds; must be &gt;= 0.</param>
    /// <param name="horizonMs">
    /// How far past <paramref name="fromMs"/> to search; must be &gt;= 0. A window must fit entirely within
    /// <c>[fromMs, fromMs + horizonMs)</c>. Defaults to <see cref="long.MaxValue"/> (search effectively unbounded).
    /// </param>
    /// <returns>The earliest grantable start, or <see langword="null"/> if none fits within the horizon.</returns>
    long? EarliestGrantableStart(
        StationDefinition station,
        long durationMs,
        long fromMs,
        long horizonMs = long.MaxValue);
}
