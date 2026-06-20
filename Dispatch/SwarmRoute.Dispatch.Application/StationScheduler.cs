using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The Foundations-phase <see cref="IStationScheduler"/>: a minimal, deterministic V1 admission policy over the
/// <see cref="IStationResourceCalendar"/> (停靠准入排程器).
/// <para>
/// V1 admits first-come-first-served against the calendar's window ledger: it derives the window
/// <c>[EarliestStartMs, EarliestStartMs + ServiceDurationMs)</c>, cheaply rejects when the station is already held
/// across it, and otherwise attempts the authoritative reservation. Traffic-impact weighing,
/// clearance-before-service (<see cref="ServiceAdmissionDecision.VehiclesToClearFirst"/>) and cost/priority-based
/// admission are deferred to later FMS rounds.
/// </para>
/// </summary>
public sealed class StationScheduler : IStationScheduler
{
    private readonly IStationResourceCalendar _calendar;
    private readonly IStationCatalog _catalog;

    /// <summary>Creates the scheduler over the service-window calendar and the station catalog.</summary>
    /// <param name="calendar">The long-lease calendar windows are reserved through.</param>
    /// <param name="catalog">Resolves a request's station id to its <see cref="StationDefinition"/> (for the blocking closure).</param>
    public StationScheduler(IStationResourceCalendar calendar, IStationCatalog catalog)
    {
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public async Task<ServiceAdmissionDecision> RequestDockAdmissionAsync(
        ServiceAdmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_catalog.TryGet(request.StationId, out var station) || station is null)
            return Denied("unknown station");

        var window = new TimeInterval(
            request.EarliestStartMs,
            request.EarliestStartMs + request.ServiceDurationMs);

        // Cheap, in-memory pre-check: skip the reservation attempt when this calendar already holds an overlapping
        // window for the station. (Authoritative contention is still decided by TryReserveServiceWindowAsync.)
        if (!_calendar.CanReserveServiceWindow(request.StationId, window))
            return Denied("station busy");

        var granted = await _calendar
            .TryReserveServiceWindowAsync(station, request.AgentId, window, ct)
            .ConfigureAwait(false);

        // TODO(FMS-V2): weigh TrafficImpact / cost, and on a soft-blocking denial populate VehiclesToClearFirst
        // with the blocking-closure occupants (clearance-before-service) instead of a bare denial.
        return granted
            ? new ServiceAdmissionDecision(
                Granted: true,
                ServiceStartMs: window.StartMs,
                Reason: "granted",
                VehiclesToClearFirst: [])
            : Denied("service closure conflicts");
    }

    private static ServiceAdmissionDecision Denied(string reason)
        => new(Granted: false, ServiceStartMs: null, Reason: reason, VehiclesToClearFirst: []);
}
