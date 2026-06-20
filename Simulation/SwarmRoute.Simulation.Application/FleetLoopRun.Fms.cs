using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

internal sealed partial class FleetLoopRun
{
    // ── (FMS-V1 R2) The station service lifecycle the discrete executor honours when an FmsScenario is supplied. ──
    //
    // Every method here short-circuits when `_fms` is null, so a non-FMS run never enters any of them and stays
    // byte-identical. The lifecycle an AGV bound for a station dock point walks (HopMs == 1, so ms == ticks):
    //
    //   MovingToPreDockBuffer ──arrive at buffer──▶ WaitingDockAdmission ──admission granted──▶ Docking
    //        (goal := buffer)                          (held at buffer)        (goal := dock)
    //   ──arrive at dock──▶ InService ──service ticks elapse──▶ MovingToParking ──arrive at park──▶ IdleParked (done)
    //     (immovable, holds dock)                          (goal := nearest parking, dock released)
    //
    // The Phase-1 InService gate (LivenessPolicy / ParkedRelocationSelector) already guarantees an InService vehicle
    // is never relocated, PIBT/CBS-driven, or yielded — this arm only drives the state transitions + dock/park goals.

    /// <summary>True when an FMS scenario is active for this run (every station arm is otherwise skipped).</summary>
    private bool FmsActive => _fms is not null;

    /// <summary>
    /// (Top of the discrete tick) Counts down each in-service vehicle's remaining service time. When a vehicle's
    /// service completes this tick it leaves service: it becomes <see cref="MobilityClass.Movable"/> again, its dock
    /// service window + dock lease are released, and under <see cref="ArrivalPolicy.ClearToParking"/> it is redirected
    /// to the nearest parking slot (re-planned from the dock next cycle); under <see cref="ArrivalPolicy.PermanentPark"/>
    /// it simply parks on the dock. Inert without an FMS scenario.
    /// </summary>
    private async Task FmsTickServiceCountdownAsync()
    {
        if (!FmsActive)
            return;

        foreach (var ag in _fleet.Where(a => a.InService))
        {
            if (ag.ServiceTicksRemaining > 0)
                ag.ServiceTicksRemaining--;
            if (ag.ServiceTicksRemaining > 0)
                continue;

            // Service complete: release the dock-point service window (dock CP + blocking closure) reserved at
            // admission, so transit traffic may flow through the (now vacated) closure again.
            var here = ag.Position;
            ag.Mobility = MobilityClass.Movable;
            if (ag.AllResources.Count > 0)
                await _cycle.ReleaseAsync(ag.Id, ag.AllResources, _cancellationToken).ConfigureAwait(false);
            ag.AllResources = Array.Empty<ResourceRef>();

            if (_fms!.Arrival == ArrivalPolicy.ClearToParking
                && ChooseParkingSlot(here) is { } park
                && !string.Equals(park, here, StringComparison.Ordinal))
            {
                // Drive off the dock to a parking slot: re-plan from the dock toward parking next cycle.
                ag.MissionState = AgvMissionState.MovingToParking;
                ag.FmsGoalOverride = park;
                ag.EnRoute = false;
                ag.Done = false;
                ag.Start = here;
                ag.CpRoute = new[] { here };
                ag.CpEntryTicks = Array.Empty<long>();
                ag.Idx = 0;
                ag.StuckTicks = 0;
                _log?.Invoke($"service-complete@tick{_tick}: {ag.Id} leaves dock {here}, heading to parking {park}.");
            }
            else
            {
                // No parking move (PermanentPark, or already on a parking slot / none reachable): park on the dock.
                ag.MissionState = AgvMissionState.IdleParked;
                ag.FmsGoalOverride = null;
                ag.Done = true;
                ag.EnRoute = false;
                ag.Start = here;
                ag.CpRoute = new[] { here };
                ag.Idx = 0;
                _parkedCells.Add(here);
                _flowtimeTicks += _tick;
                _log?.Invoke($"service-complete@tick{_tick}: {ag.Id} parks on dock {here} ({_fms!.Arrival}).");
            }
        }
    }

    /// <summary>
    /// (Before plan+reserve) The per-tick dock-admission gate: for every AGV physically standing on its station's
    /// pre-dock buffer and still awaiting admission, ask the <see cref="IStationScheduler"/> whether its service
    /// window is free. Granted ⇒ the AGV's goal becomes the dock point and it plans buffer→dock this cycle (Docking);
    /// denied ⇒ it holds at the buffer (WaitingDockAdmission) — its goal stays the buffer, so the planner never drives
    /// it toward the contended dock. Because the AGV waits at the (off-corridor, non-blocking) buffer, following
    /// transit vehicles pass the station's blocking closure first; admission then grants once that closure is free —
    /// this is "clearance before service" for V1. Inert without an FMS scenario or a scheduler.
    /// </summary>
    private async Task FmsDockAdmissionPassAsync()
    {
        if (!FmsActive || _stationScheduler is null || _stationByBuffer.Count == 0)
            return;

        foreach (var ag in _fleet)
        {
            // A candidate is staged on a buffer, not yet admitted, and not en route / done / in service.
            if (ag.EnRoute || ag.Done || ag.InService)
                continue;
            if (ag.MissionState is not (AgvMissionState.MovingToPreDockBuffer or AgvMissionState.WaitingDockAdmission))
                continue;
            if (!_stationByBuffer.TryGetValue(ag.Position, out var station))
                continue;
            // Only its OWN station's dock is the target (a buffer shared across stations would otherwise misfire).
            if (!string.Equals(station.StationId, ag.StationId, StringComparison.Ordinal))
                continue;

            var request = new ServiceAdmissionRequest(
                AgentId: ag.Id,
                StationId: station.StationId,
                PreDockBuffer: ag.Position,
                DockPoint: station.DockPoint,
                ServiceDurationMs: station.ServiceDurationMs,
                Priority: Math.Max(0, ag.Priority),
                EarliestStartMs: _tick,
                DeadlineMs: null);

            var decision = await _stationScheduler
                .RequestDockAdmissionAsync(request, _cancellationToken)
                .ConfigureAwait(false);

            if (decision.Granted)
            {
                // Admitted: the service window (dock CP + blocking closure) is now reserved for this AGV. Drop the
                // buffer override so EffectiveGoal reverts to the real goal (the dock) and plan buffer→dock.
                ag.FmsGoalOverride = null;
                ag.MissionState = AgvMissionState.Docking;
                ag.StuckTicks = 0;

                // Release ONLY the dock-point CP from the just-reserved window so the AGV can plan its own approach
                // onto the dock: the reservation VIEW the planner reads is not owner-aware (a cell this agent itself
                // reserved still reads as "unsafe"), so leaving the dock CP held would force SIPP to schedule the
                // arrival after the window — the AGV would idle at the buffer for the whole service duration. The
                // dock AGV re-holds the dock via its navigation plan (and the in-service AllResources) the moment it
                // docks; the BLOCKING CLOSURE stays reserved throughout, which is what actually keeps transit out
                // (clearance-before-service). Same-agent leases merge in the table, so the re-hold is seamless.
                await _cycle
                    .ReleaseAsync(ag.Id, new[] { RoadmapGraph.SiteRef(station.DockPoint) }, _cancellationToken)
                    .ConfigureAwait(false);

                _log?.Invoke($"dock-admit@tick{_tick}: {ag.Id} admitted to {station.StationId} dock {station.DockPoint} from buffer {ag.Position}.");
            }
            else if (ag.MissionState != AgvMissionState.WaitingDockAdmission)
            {
                ag.MissionState = AgvMissionState.WaitingDockAdmission;
                _log?.Invoke($"dock-hold@tick{_tick}: {ag.Id} holds at buffer {ag.Position} for {station.StationId} ({decision.Reason}).");
            }
        }
    }

    /// <summary>
    /// (FMS-V1 R2) When an FMS scenario is active, an AGV's goal is its station's dock point but it must first reach
    /// the pre-dock buffer for admission — so a station AGV's <em>effective</em> goal starts at the buffer. Called
    /// once at construction for each station-bound AGV. Picks the buffer reachable from the AGV's start (the first
    /// pre-dock buffer with a path) so the AGV genuinely routes to it; falls back to the first buffer.
    /// <para>
    /// (FMS-V2) A station with NO pre-dock buffer (e.g. a SoftBlocking warehouse workstation whose closure never
    /// severs the core, so there is nothing to clear before service) skips the admission stage entirely: the AGV is
    /// marked <see cref="AgvMissionState.Docking"/> at once and heads straight to the dock CP, where the arrival arm
    /// puts it in service. (M-F1's stations always carry a buffer, so this branch never fires there ⇒ byte-identical.)
    /// </para>
    /// </summary>
    private void FmsInitStationGoal(RunAgent ag, StationDefinition station)
    {
        if (station.PreDockBuffers.Count == 0)
        {
            // No buffer ⇒ no admission staging: head straight to the dock and dock on arrival (admission skipped).
            ag.MissionState = AgvMissionState.Docking;
            return;
        }

        var buffer = station.PreDockBuffers
            .FirstOrDefault(b => _graph.ShortestPath(ag.Start, b) is { Count: >= 1 })
            ?? station.PreDockBuffers[0];
        ag.FmsGoalOverride = buffer;
    }

    /// <summary>
    /// (FMS-V1 R2) Arrival hook, called from the discrete arrival branches the instant an agent reaches the end of
    /// its committed route on cell <paramref name="here"/>. Returns <see langword="true"/> when the FMS lifecycle has
    /// handled the arrival (the caller must then NOT mark the agent done / park it / release its leases); returns
    /// <see langword="false"/> to fall through to today's arrival behaviour. Inert (always false) without an FMS run.
    /// <list type="bullet">
    ///   <item>Arrived at its station's <b>pre-dock buffer</b> while still awaiting admission ⇒ become pending at the
    ///     buffer (WaitingDockAdmission); the next tick's admission pass decides whether it may dock.</item>
    ///   <item>Arrived at its station's <b>dock point</b> having been admitted ⇒ go in service: hold the dock lease,
    ///     become immovable for the station's service duration. The InService gate keeps it from being relocated.</item>
    /// </list>
    /// </summary>
    private async Task<bool> FmsHandleArrivalAsync(RunAgent ag, string here)
    {
        if (!FmsActive)
            return false;

        // (a) Reached the pre-dock buffer, still awaiting admission: stage the AGV (pending) at the buffer.
        if ((ag.MissionState is AgvMissionState.MovingToPreDockBuffer or AgvMissionState.WaitingDockAdmission)
            && _stationByBuffer.TryGetValue(here, out var bufStation)
            && string.Equals(bufStation.StationId, ag.StationId, StringComparison.Ordinal))
        {
            // Keep nothing reserved (a buffer is non-blocking and the AGV simply waits); release the spent leg so the
            // reservation table is clean while it queues, and rejoin as pending at the buffer.
            if (ag.AllResources.Count > 0)
                await _cycle.ReleaseAsync(ag.Id, ag.AllResources, _cancellationToken).ConfigureAwait(false);
            ag.EnRoute = false;
            ag.Start = here;
            ag.CpRoute = new[] { here };
            ag.CpEntryTicks = Array.Empty<long>();
            ag.Idx = 0;
            ag.AllResources = Array.Empty<ResourceRef>();
            ag.MissionState = AgvMissionState.WaitingDockAdmission;
            return true;
        }

        // (b) Reached the dock point, admitted: begin service. Hold the dock lease (genuinely occupying it), go
        //     immovable, and count down the station's service duration. Only when the arrival policy honours service.
        if (ag.MissionState == AgvMissionState.Docking
            && (_fms!.Arrival is ArrivalPolicy.ClearToParking or ArrivalPolicy.PermanentPark)
            && _stationByDock.TryGetValue(here, out var dockStation)
            && string.Equals(dockStation.StationId, ag.StationId, StringComparison.Ordinal))
        {
            // ms == ticks (HopMs == 1); clamp to at least one tick so a service is always observable.
            var ticks = (int)Math.Min(int.MaxValue, Math.Max(1, dockStation.ServiceDurationMs));
            ag.MissionState = AgvMissionState.InService;
            ag.Mobility = MobilityClass.ImmovableUntilServiceComplete;
            ag.ServiceTicksRemaining = ticks;
            ag.FmsGoalOverride = null;
            // Represent the in-service vehicle as pending-at-dock holding its WHOLE service window: !EnRoute (so the
            // schedule executor leaves it alone) and !Done (so it is not parked), with AllResources = the dock CP plus
            // every blocking-closure resource, so the executor releases the entire window on service completion (which
            // re-opens the corridor) and so the reservation table keeps protecting the closure meanwhile. Its cell
            // feeds PhysicalBlockersExcept so the fleet routes around the dock.
            ag.EnRoute = false;
            ag.Start = here;
            ag.CpRoute = new[] { here };
            ag.CpEntryTicks = Array.Empty<long>();
            ag.Idx = 0;
            ag.AllResources = ServiceWindowResources(dockStation);
            _log?.Invoke($"in-service@tick{_tick}: {ag.Id} docked at {here} ({dockStation.StationId}), service {ticks} ticks.");
            return true;
        }

        // (c) Reached a parking slot after service (MovingToParking): mark it idle-parked and drop the parking goal
        //     override, THEN fall through (return false) to today's "arrived" behaviour (mark done + park + release).
        //     Settling MissionState to IdleParked is the terminal state of a serviced AGV; it does not change the
        //     recorded frame (a done AGV is Arrived either way) so a non-lifelong run is byte-identical, and it is what
        //     the (FMS-V3) lifelong re-task arm keys on to hand the AGV its next task. Other arrivals: return false.
        if (ag.MissionState == AgvMissionState.MovingToParking)
        {
            ag.MissionState = AgvMissionState.IdleParked;
            ag.FmsGoalOverride = null;
        }
        return false;
    }

    /// <summary>
    /// The resources a station's service window holds: the dock-point CP plus every <see cref="StationDefinition.BlockingClosure"/>
    /// member (deduplicated — the dock point may itself appear in the closure). This mirrors the calendar's
    /// reserved set, so releasing it on service completion frees exactly what admission reserved (re-opening the
    /// corridor for transit).
    /// </summary>
    private static IReadOnlyList<ResourceRef> ServiceWindowResources(StationDefinition station)
    {
        var dock = RoadmapGraph.SiteRef(station.DockPoint);
        var resources = new List<ResourceRef>(station.BlockingClosure.Count + 1) { dock };
        foreach (var member in station.BlockingClosure)
            if (member != dock)
                resources.Add(member);
        return resources;
    }

    /// <summary>
    /// (FMS-V2) Chooses where a serviced vehicle leaving the dock at <paramref name="from"/> clears to. When an
    /// <see cref="IParkingManager"/> is supplied it picks the nearest FREE <see cref="SiteRole.Parking"/> (falling
    /// back to <see cref="SiteRole.Buffer"/>) avoiding every cell currently occupied or parked by ANOTHER vehicle —
    /// so two serviced vehicles never target the same slot. Without a manager it falls back to the FMS-V1 inline
    /// nearest-<see cref="SiteRole.Parking"/> pick (<see cref="NearestRole"/>), which ignores occupancy — byte-identical.
    /// </summary>
    private string? ChooseParkingSlot(string from)
    {
        if (_parkingManager is null)
            return NearestRole(from, SiteRole.Parking);

        // Cells taken by OTHER vehicles (en-route/waiting positions + parked cells): never a relocation target. The
        // vehicle's own current cell is excluded so it is never asked to "clear" to where it already stands.
        var occupied = new HashSet<string>(_parkedCells, StringComparer.Ordinal);
        foreach (var other in _fleet)
            if (!string.Equals(other.Position, from, StringComparison.Ordinal))
                occupied.Add(other.Position);

        return _parkingManager.AssignParking(from, _graph, occupied, _fms!.SiteRoles);
    }

    /// <summary>
    /// The nearest control point with FMS <see cref="SiteRole"/> <paramref name="role"/> reachable from
    /// <paramref name="from"/> (least hop count via <see cref="RoadmapGraph.ShortestPath"/>, ties by ordinal id), or
    /// <see langword="null"/> when the scenario defines none reachable. Used to send a serviced vehicle to parking.
    /// </summary>
    private string? NearestRole(string from, SiteRole role)
    {
        if (!FmsActive)
            return null;

        string? best = null;
        var bestHops = int.MaxValue;
        foreach (var (siteId, siteRole) in _fms!.SiteRoles)
        {
            if (siteRole != role)
                continue;
            var path = _graph.ShortestPath(from, siteId);
            if (path is null)
                continue;
            var hops = path.Count - 1;
            if (hops < bestHops || (hops == bestHops && (best is null || string.CompareOrdinal(siteId, best) < 0)))
            {
                bestHops = hops;
                best = siteId;
            }
        }
        return best;
    }
}
