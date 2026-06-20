using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Map.Domain.Shared.Enums;

namespace SwarmRoute.Simulation.Application;

internal sealed partial class FleetLoopRun
{
    // ── (FMS-V3) The lifelong-dispatch arm: continuous re-tasking on top of the FMS station lifecycle. ──────────────
    //
    // Every method here short-circuits when `_lifelong` is null, so a non-lifelong run never enters any of them and is
    // byte-identical. The model reuses the existing station machinery: every transport task is "drive to a workstation
    // dock CP, service briefly, clear to parking", and the workstation docks are ALL in `_stationByDock` (the lifelong
    // scenario builds one station per workstation). An AGV's lifecycle in a lifelong run:
    //
    //   IdleParked ──dispatcher hands a task──▶ Docking ──drive to dock──▶ InService ──service──▶ MovingToParking
    //     (at start / a parking slot)              (goal := the task's workstation dock)
    //   ──arrive at parking──▶ IdleParked ──(loop: request the next task)──▶ …   until the horizon elapses.
    //
    // The run is bounded by the horizon (set as `_maxTicks`), NOT by "all arrived" — see ShouldContinueDiscrete().

    /// <summary>
    /// (Lifelong only) Seed every AGV as idle-parked at its start, so the re-task arm assigns even the FIRST task
    /// through the dispatcher (uniform with every subsequent re-task). Called once at construction when a lifelong
    /// runtime is present; a no-op otherwise, so a non-lifelong fleet keeps its normal pending-at-start setup.
    /// </summary>
    private void LifelongInitFleet()
    {
        if (!LifelongActive)
            return;

        foreach (var ag in _fleet)
        {
            // Park the AGV on its start cell, ready for its first dispatch. It holds no lease and is its own obstacle
            // (in _parkedCells), exactly like an AGV that has cleared to a resting slot between tasks.
            ag.MissionState = AgvMissionState.IdleParked;
            ag.Done = true;
            ag.EnRoute = false;
            ag.Mobility = MobilityClass.Movable;
            ag.CpRoute = new[] { ag.Start };
            ag.Idx = 0;
            _parkedCells.Add(ag.Start);
        }
    }

    /// <summary>
    /// (Top of the lifelong tick, after the service countdown) Records completions and hands each idle-parked AGV its
    /// next task. For every AGV at <see cref="AgvMissionState.IdleParked"/>: mark its just-finished task complete
    /// (idempotent), then ask the dispatcher for the next eligible task — on a hit the AGV is woken toward the new
    /// workstation dock (re-planned next cycle), on a miss it rests another tick. Also samples the peak parked count
    /// for parking-saturation. Inert without a lifelong runtime.
    /// </summary>
    private void LifelongRetaskPass()
    {
        if (!LifelongActive)
            return;

        // Release any tasks whose release tick has arrived, so they are assignable this very tick.
        _lifelong!.AdvanceReleases(_tick);

        // Sample how many AGVs are currently parked (resting) — the peak feeds parking saturation.
        _lifelong.SampleParked(_fleet.Count(a => a.Done && a.MissionState == AgvMissionState.IdleParked));

        // Re-task in stable fleet order (the fleet list is already priority-then-id ordered) so the run is deterministic.
        foreach (var ag in _fleet)
        {
            if (!ag.Done || ag.MissionState != AgvMissionState.IdleParked)
                continue;

            // Attribute the completion of the task it just finished (no-op for an AGV that never had one — e.g. the
            // initial seed parked at its start with no active task).
            _lifelong.CompleteActiveTask(ag.Id, _tick);

            var next = _lifelong.TryAssignNext(ag.Id, ag.Position, _tick);
            if (next is not { } assigned)
                continue; // backlog empty / nothing eligible: rest here and retry next tick.

            // Wake the AGV toward the task's workstation dock. RetaskTo resets it to pending-at-current-cell; the
            // station init then sets the dock-or-buffer effective goal exactly as a fresh station AGV at construction.
            _parkedCells.Remove(ag.Position);
            ag.RetaskTo(assigned.Station.DockPoint, assigned.Station.StationId, AgvMissionState.MovingToPreDockBuffer);
            FmsInitStationGoal(ag, assigned.Station);
            _log?.Invoke(
                $"lifelong-assign@tick{_tick}: {ag.Id} (at {ag.Position}) takes task {assigned.Assignment.Task.TaskId} " +
                $"-> dock {assigned.Station.DockPoint}.");
        }
    }

    /// <summary>
    /// The discrete loop's continue-condition. A normal run continues while any agent is not done (ends at "all
    /// arrived"); a lifelong run continues until the horizon elapses regardless of momentary all-parked lulls (the
    /// horizon break inside the loop stops it), so it is driven purely by <see cref="_maxTicks"/>. Byte-identical for a
    /// non-lifelong run (the lifelong branch is never taken).
    /// </summary>
    private bool ShouldContinueDiscrete()
        => LifelongActive ? _tick < _maxTicks : _fleet.Any(a => !a.Done);

    /// <summary>The number of parking slots the lifelong scenario provides (the saturation denominator): every site
    /// the FMS overlay marks <see cref="SiteRole.Parking"/>. Zero outside a lifelong/FMS run.</summary>
    private int LifelongParkingCapacity()
        => _fms is null ? 0 : _fms.SiteRoles.Count(kv => kv.Value == SiteRole.Parking);

    /// <summary>(Lifelong only) The assembled lifelong metrics, or <see langword="null"/> for a non-lifelong run
    /// (omitted from the result ⇒ byte-identical). Called once from <see cref="BuildResult"/>.</summary>
    private LifelongMetricsDto? BuildLifelongMetrics()
        => _lifelong?.BuildMetrics(LifelongParkingCapacity());
}
