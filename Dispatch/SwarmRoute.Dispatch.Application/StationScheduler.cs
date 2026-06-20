using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The <see cref="IStationScheduler"/> implementation over the <see cref="IStationResourceCalendar"/>
/// (停靠准入排程器).
/// <para>
/// <b>V1 (no impact analyzer).</b> First-come-first-served against the calendar's window ledger: derive the
/// window <c>[EarliestStartMs, EarliestStartMs + ServiceDurationMs)</c>, cheaply reject when the station is
/// already held across it ("station busy"), otherwise attempt the authoritative reservation. When no
/// <see cref="ITrafficImpactAnalyzer"/> is supplied the scheduler is byte-for-byte the V1 policy.
/// </para>
/// <para>
/// <b>V2 (impact analyzer supplied).</b> After the cheap pre-check passes, the scheduler asks the
/// <see cref="ITrafficImpactAnalyzer"/> what holding the station's blocking closure for the window would do to the
/// fleet (resolving the closure from the catalog and the live plan from the optional
/// <see cref="IFleetPlanProvider"/>). It then applies <em>clearance-before-service</em>:
/// <list type="bullet">
///   <item>No affected vehicles → reserve as in V1.</item>
///   <item>The closure severs the transit core (<see cref="TrafficImpact.BlocksTransitCore"/>) → the affected
///   vehicles are trapped, so deny with reason <c>"let affected vehicles pass first"</c> and
///   <see cref="ServiceAdmissionDecision.VehiclesToClearFirst"/> = the affected ids. Priority never preempts a
///   severed core (there is nowhere for the displaced traffic to go).</item>
///   <item>Soft impact (affected vehicles, but a bypass survives — <see cref="TrafficImpact.HasBypass"/>) →
///   <b>priority-override rule:</b> if this request's <see cref="ServiceAdmissionRequest.Priority"/> strictly
///   exceeds <em>every</em> affected vehicle's priority <b>and there is no bypass dependency</b> (the displaced
///   traffic has a bypass, so preempting it does not strand it), the scheduler still attempts the reserve;
///   otherwise it denies with the same clearance batch so the lower-priority traffic passes first.</item>
/// </list>
/// All inputs beyond the request itself are resolved through the constructor, so
/// <see cref="RequestDockAdmissionAsync"/> keeps its frozen V1 signature. The decision is deterministic.
/// </para>
/// </summary>
public sealed class StationScheduler : IStationScheduler
{
    /// <summary>The reason returned when affected transit must clear the closure before this service is admitted.</summary>
    internal const string ClearFirstReason = "let affected vehicles pass first";

    private readonly IStationResourceCalendar _calendar;
    private readonly IStationCatalog _catalog;
    private readonly ITrafficImpactAnalyzer? _impactAnalyzer;
    private readonly IFleetPlanProvider? _fleetPlan;
    private readonly ICostBasedAdmissionPolicy? _costPolicy;

    /// <summary>Creates the scheduler over the service-window calendar and the station catalog.</summary>
    /// <param name="calendar">The long-lease calendar windows are reserved through.</param>
    /// <param name="catalog">Resolves a request's station id to its <see cref="StationDefinition"/> (for the blocking closure).</param>
    /// <param name="impactAnalyzer">
    /// Optional FMS-V2 traffic-impact analyser. When <see langword="null"/> the scheduler is exact V1 FCFS; when
    /// supplied it gates admission on transit impact (clearance-before-service + priority override).
    /// </param>
    /// <param name="fleetPlan">
    /// Optional snapshot of the fleet's planned resources and priorities, fed to the analyser and used for the
    /// priority-override rule. When <see langword="null"/> the analyser sees an empty fleet plan (so no vehicle is
    /// ever affected) and the V2 gate is inert — still exactly V1.
    /// </param>
    /// <param name="costPolicy">
    /// Optional FMS-V3 cost-based admission policy. When <see langword="null"/> the scheduler keeps its exact V2
    /// (binary clearance / priority-override) gate; when supplied <em>and</em> an <paramref name="impactAnalyzer"/>
    /// is also present, the binary gate is replaced by the numeric score (high score → admit now; low → defer with
    /// the affected vehicles to clear first). With no analyzer there is no impact to score, so this is inert.
    /// </param>
    public StationScheduler(
        IStationResourceCalendar calendar,
        IStationCatalog catalog,
        ITrafficImpactAnalyzer? impactAnalyzer = null,
        IFleetPlanProvider? fleetPlan = null,
        ICostBasedAdmissionPolicy? costPolicy = null)
    {
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _impactAnalyzer = impactAnalyzer;
        _fleetPlan = fleetPlan;
        _costPolicy = costPolicy;
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

        // FMS-V2 transit gate (only when an analyzer is supplied; otherwise this is exact V1 below).
        if (_impactAnalyzer is not null)
        {
            var gate = EvaluateTransitGate(request, station, window);
            if (gate is not null)
                return gate;
        }

        var granted = await _calendar
            .TryReserveServiceWindowAsync(station, request.AgentId, window, ct)
            .ConfigureAwait(false);

        return granted
            ? new ServiceAdmissionDecision(
                Granted: true,
                ServiceStartMs: window.StartMs,
                Reason: "granted",
                VehiclesToClearFirst: [])
            : Denied("service closure conflicts");
    }

    /// <summary>
    /// Applies the transit gate. Returns a <em>denial</em> decision when the service must wait for affected transit
    /// to clear, or <see langword="null"/> when admission may proceed to the authoritative reservation.
    /// <para>
    /// When an FMS-V3 <see cref="ICostBasedAdmissionPolicy"/> is supplied the verdict is its numeric score;
    /// otherwise the FMS-V2 binary clearance-before-service / priority-override rules apply.
    /// </para>
    /// </summary>
    private ServiceAdmissionDecision? EvaluateTransitGate(
        ServiceAdmissionRequest request,
        StationDefinition station,
        TimeInterval window)
    {
        var fleetPlanned = _fleetPlan?.GetPlannedResources() ?? EmptyPlan;
        var impact = _impactAnalyzer!.AnalyzeBlockingImpact(station.BlockingClosure, window, fleetPlanned);

        // No vehicle is impacted by holding the closure -> nothing to clear, admit (shared by V2 and V3).
        if (impact.AffectedAgentIds.Count == 0)
            return null;

        // FMS-V3 cost-based mode: replace the binary gate with the numeric score.
        if (_costPolicy is not null)
            return EvaluateCostGate(request, impact);

        // FMS-V2 binary gate.
        // A severed transit core leaves the affected vehicles no bypass: never preempt, clear them first.
        if (impact.BlocksTransitCore)
            return ClearFirst(impact);

        // Soft impact: the affected vehicles have a bypass. Priority-override rule — a request that strictly
        // outranks every affected vehicle, and whose displaced traffic is not bypass-dependent (a bypass exists),
        // may still take the window; everyone else yields to the lower-priority traffic first.
        return StrictlyHigherThanAllAffected(request, impact) && impact.HasBypass
            ? null
            : ClearFirst(impact);
    }

    /// <summary>
    /// The FMS-V3 cost-based verdict: a high score admits the service now (return <see langword="null"/> so the
    /// reservation proceeds); a low score defers with the affected vehicles to clear first.
    /// </summary>
    private ServiceAdmissionDecision? EvaluateCostGate(ServiceAdmissionRequest request, TrafficImpact impact)
    {
        var scored = _costPolicy!.Score(request, impact, _fleetPlan);
        return scored.Admit
            ? null
            : new ServiceAdmissionDecision(
                Granted: false,
                ServiceStartMs: null,
                Reason: ClearFirstReason,
                VehiclesToClearFirst: scored.VehiclesToClearFirst);
    }

    /// <summary>
    /// True when <paramref name="request"/>'s priority strictly exceeds the priority of every affected vehicle.
    /// An affected vehicle with an unknown priority is treated as priority <see cref="int.MinValue"/> (it can
    /// always be outranked) so a missing plan never blocks the override.
    /// </summary>
    private bool StrictlyHigherThanAllAffected(ServiceAdmissionRequest request, TrafficImpact impact)
    {
        foreach (var affectedId in impact.AffectedAgentIds)
        {
            var theirPriority = _fleetPlan?.GetPriority(affectedId) ?? int.MinValue;
            if (request.Priority <= theirPriority)
                return false;
        }

        return true;
    }

    private static ServiceAdmissionDecision ClearFirst(TrafficImpact impact)
        => new(
            Granted: false,
            ServiceStartMs: null,
            Reason: ClearFirstReason,
            VehiclesToClearFirst: impact.AffectedAgentIds);

    private static ServiceAdmissionDecision Denied(string reason)
        => new(Granted: false, ServiceStartMs: null, Reason: reason, VehiclesToClearFirst: []);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> EmptyPlan =
        new Dictionary<string, IReadOnlyList<ResourceRef>>(StringComparer.Ordinal);
}
