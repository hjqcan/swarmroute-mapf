using SwarmRoute.Coordination.Application;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

internal sealed partial class FleetLoopRun
{
    // (v3 CCBS) Continuous-time joint standoff resolution. When the event loop stalls — no en-route agent can
    // advance yet agents remain — hand the stuck pending agents to the joint resolver before declaring
    // non-convergence. A continuous standoff is non-transient (unlike a discrete blocked streak that may clear on
    // its own), so every still-pending agent is made immediately eligible for cluster formation (its stuck counter
    // is floored past any trigger threshold); the ClusterFormation phase groups the mutually-blocking agents and
    // CCBS (continuous CBS over a SIPPwRT low level, via the cycle) solves + reserves each cluster atomically.
    // Returns true when at least one agent obtained a fresh reserved route (progress to re-loop on). Only
    // SolveClusterJointly (CBS) is honoured — PIBT is a per-tick drive the event loop does not run (Pibt+SIPPwRT is
    // rejected up front). Inert when no joint resolver is configured (the policy emits nothing).
    private async Task<bool> TryResolveContinuousStandoffAsync(long nowMs)
    {
        const int StandoffStuckFloor = 1 << 20; // dwarfs any JointResolverTriggerThreshold → immediately actionable
        // Floor BOTH stuck counters for every live agent so the policy seeds a cluster immediately. A continuous
        // standoff shows up as EN-ROUTE agents blocked at the gate (the cluster detector keys those off BlockedTicks)
        // just as much as walled-out PENDING ones (StuckTicks), and the event loop accumulates neither — so without
        // flooring both, the mutually-blocking component never crosses the trigger threshold and CCBS never engages.
        foreach (var a in _fleet.Where(a => !a.Done && !a.HoldingAtAvoidSite && a.RedirectTarget is null))
        {
            a.StuckTicks = Math.Max(a.StuckTicks, StandoffStuckFloor);
            a.BlockedTicks = Math.Max(a.BlockedTicks, StandoffStuckFloor);
        }

        _agentViews.Clear();
        foreach (var a in _fleet)
            _agentViews.Add(ViewOf(a));

        var progressed = false;
        foreach (var directive in _policy.Evaluate(new LivenessSnapshot(
                     (int)Math.Min(nowMs, int.MaxValue), LivenessPhase.ClusterFormation, _scheduleFaithful,
                     _agentViews, _parkedCells)))
        {
            if (directive is not SolveClusterJointly solve)
                continue;

            var members = solve.AgentIds.Select(id => _fleet.Single(a => a.Id == id)).ToList();
            foreach (var ag in members)
                await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);

            var clusterGoals = members
                .Select(a => new AgentGoal(a.Id, a.Position, a.EffectiveGoal, a.Priority))
                .ToList();
            var clusterCells = members.Select(a => a.Position).ToHashSet(StringComparer.Ordinal);
            var clusterReport = await _cycle.PlanClusterAsync(
                _roadmapId, clusterGoals, PhysicalBlockersExcept(clusterCells), _cancellationToken).ConfigureAwait(false);

            foreach (var res in clusterReport.Results)
            {
                var ag = _fleet.Single(a => a.Id == res.AgentId);
                ag.Replans++;
                if (res is { Reserved: true, Path: not null })
                {
                    SetEnRouteFromPath(ag, res.Path);
                    ag.BlockedTicks = 0; // SetEnRouteFromPath clears StuckTicks; clear the floored block streak too
                    progressed = true;
                }
            }
            _log?.Invoke($"ccbs@{nowMs}ms: solved a {members.Count}-agent continuous standoff cluster.");
        }
        return progressed;
    }

    // (v3 SIPPwRT) Continuous-time event-driven executor. Instead of one CP per integer tick, it jumps the
    // clock to the next CP-arrival event (the earliest future real-ms CpEntryTick across the fleet) and advances
    // whichever agents arrive then. Honouring the interval-exclusive SIPPwRT schedule is collision-free by
    // construction (the property ScheduleFaithful relies on); blocks (3)/(3b) stay the net. Standoff band-aids
    // are off in this mode (it pairs only with the conflict-free SIPPwRT planner). It populates the SAME
    // frames/status/collisions/... the discrete loop does, then falls through to the common result construction
    // below — so the discrete Greedy/ScheduleFaithful path is untouched and byte-identical.
    private async Task ExecuteContinuousAsync()
    {
        var nowMs = 0L;
        var events = 0;
        // A continuous standoff manifests as the event loop making no forward progress — en-route agents blocked at the
        // gate spin at a constant clock, never advancing. After ContinuousStallTrigger such no-progress events, hand the
        // jam to the joint resolver (CCBS); any progress resets the streak. Inert when JointResolver=None (never consulted).
        const int ContinuousStallTrigger = 2;
        var noProgressStreak = 0;
        while (_fleet.Any(a => !a.Done))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (++events > _maxTicks)
            {
                _status = FleetLoopStatus.DidNotConverge;
                break;
            }

            // (1) Plan + reserve every pending agent at the current instant (block-1 semantics).
            var pending = _fleet
                .Where(a => !a.Done && !a.EnRoute && !a.HoldingAtAvoidSite)
                .Select(a => new AgentGoal(a.Id, a.Start, a.EffectiveGoal, a.Priority))
                .ToList();
            var newlyReserved = 0;
            if (pending.Count > 0)
            {
                _advanceClock?.Invoke(nowMs);
                var report = await _cycle.RunCycleAsync(_roadmapId, pending, PhysicalBlockersExcept(), _cancellationToken).ConfigureAwait(false);
                foreach (var r in report.Results)
                {
                    var ag = _fleet.Single(a => a.Id == r.AgentId);
                    ag.Replans += Math.Max(0, r.Attempts - 1);
                    if (r is { Reserved: true, Path: not null })
                    {
                        SetEnRouteFromPath(ag, r.Path);
                        newlyReserved++;
                    }
                    else
                        ag.StuckTicks++;
                }
            }

            // (2) The next event: the earliest future CP-arrival among en-route movers.
            long? nextEvent = null;
            foreach (var ag in _fleet)
            {
                if (ag is not { EnRoute: true, Done: false })
                    continue;
                if (ag.CpEntryTicks.Count != ag.CpRoute.Count || ag.Idx + 1 >= ag.CpRoute.Count)
                    continue;
                var entry = Math.Max(nowMs, ag.CpEntryTicks[ag.Idx + 1]);
                if (nextEvent is null || entry < nextEvent.Value)
                    nextEvent = entry;
            }

            if (nextEvent is null)
            {
                // Nothing en-route can advance (pending could not reserve, or everyone is parked). Before giving
                // up, try a continuous joint (CCBS) solve over the stuck cluster; if it un-sticks at least one
                // agent, re-loop to advance it (the event budget bounds the retries). With no joint resolver — or
                // an unsolvable jam — it is a real standoff → DidNotConverge, never a crash. Byte-identical when
                // JointResolver=None (the CCBS attempt is skipped entirely).
                if (_fleet.Any(a => !a.Done)
                    && _policy.JointResolver != JointResolverKind.None
                    && await TryResolveContinuousStandoffAsync(nowMs).ConfigureAwait(false))
                {
                    noProgressStreak = 0;
                    continue;
                }

                if (_fleet.Any(a => !a.Done))
                    _status = FleetLoopStatus.DidNotConverge;
                break;
            }

            nowMs = nextEvent.Value;
            _tick = (int)Math.Min(nowMs, int.MaxValue); // frame timestamp (ms); clamp guards a pathologically long run
            _advanceClock?.Invoke(nowMs);
            var doneBefore = _fleet.Count(a => a.Done);

            // (3) Advance the agents whose next CP arrives now (schedule-faithful joint resolution, reused).
            _posBefore.Clear();
            foreach (var a in _fleet)
                _posBefore[a.Id] = a.Position;
            var scheduled = ResolveScheduleFaithfulAdvances(_fleet, nowMs, _parkedCells, _log);
            foreach (var ag in _fleet.Where(a => a is { EnRoute: true, Done: false } && scheduled.Contains(a.Id)).ToList())
            {
                var fromCp = ag.CpRoute[ag.Idx];
                ag.Idx++;
                var toCp = ag.CpRoute[ag.Idx];
                await _cycle.ReleaseAsync(ag.Id,
                    [RoadmapGraph.SiteRef(fromCp), new ResourceRef(ResourceKind.Lane, $"{fromCp}-{toCp}")],
                    _cancellationToken).ConfigureAwait(false);
            }

            // (3c) Reroute around parked vehicles. Like SIPP, SIPPwRT reserves a goal only for a finite dwell
            // (the executor releases on arrival), so an agent may legitimately PLAN through a cell that another
            // agent later parks on. Such a cell is then a permanent physical obstacle — the committed route can
            // never advance past it. Drop the stalled reservation and re-plan from the current pose next event;
            // PhysicalBlockersExcept() feeds parkedCells to the planner, so it routes around. This is the
            // continuous analogue of the discrete schedule-faithful stall-reroute, but immediate (parked ⇒
            // permanent, so there is no tick threshold to wait out). An agent that genuinely cannot route around
            // stays pending and the run ends in DidNotConverge (honest) — never a spin or a crash.
            foreach (var ag in _fleet.Where(a => a is { EnRoute: true, Done: false }
                         && a.Idx + 1 < a.CpRoute.Count
                         && _parkedCells.Contains(a.CpRoute[a.Idx + 1])).ToList())
            {
                ag.Replans++;
                await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);
            }

            // (3b) Arrival: agents now at the end of their committed route park (goal) or re-plan (RHCR frontier).
            foreach (var ag in _fleet.Where(a => a is { EnRoute: true, Done: false } && a.Idx >= a.CpRoute.Count - 1).ToList())
            {
                if (!ag.FrontierIsGoal)
                {
                    await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);
                }
                else
                {
                    ag.Done = true;
                    ag.EnRoute = false;
                    _parkedCells.Add(ag.Position);
                    _flowtimeTicks += _tick;
                    await _cycle.ReleaseAsync(ag.Id, ag.AllResources, _cancellationToken).ConfigureAwait(false);
                }
            }

            // (4) Safety nets (identical to the discrete blocks 3/3b), evaluated at the event.
            var occupied = _occupantNow;
            occupied.Clear();
            foreach (var ag in _fleet.Where(a => a.EnRoute || a.Done).OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (occupied.TryGetValue(ag.Position, out var other))
                {
                    _collisions++;
                    if (_collision is null)
                    {
                        _status = FleetLoopStatus.CollisionDetected;
                        _collision = new FleetCollisionInfo(_tick, ag.Position, [other, ag.Id]);
                    }
                }
                else
                {
                    occupied[ag.Position] = ag.Id;
                }
            }
            foreach (var ag in _fleet.Where(a => a.EnRoute || a.Done))
            {
                if (!_posBefore.TryGetValue(ag.Id, out var prev) || string.Equals(prev, ag.Position, StringComparison.Ordinal))
                    continue;
                if (occupied.TryGetValue(prev, out var other)
                    && StringComparer.Ordinal.Compare(ag.Id, other) < 0
                    && _posBefore.TryGetValue(other, out var otherPrev)
                    && string.Equals(otherPrev, ag.Position, StringComparison.Ordinal))
                {
                    _collisions++;
                    if (_collision is null)
                    {
                        _status = FleetLoopStatus.CollisionDetected;
                        _collision = new FleetCollisionInfo(_tick, ag.Position, [other, ag.Id]);
                    }
                }
            }

            _maxConcurrent = Math.Max(_maxConcurrent, _fleet.Count(a => a.EnRoute));

            _frames.Add(new FleetTickFrame(
                _tick,
                _fleet.OrderBy(a => a.Id, StringComparer.Ordinal)
                    .Select(a => new FleetTickPosition(a.Id, a.Position, a.State))
                    .ToList()));

            // Forward-progress check: a reservation, a CP advance, or an arrival resets the stall streak. A run of
            // no-progress events is a physical standoff the schedule cannot break on its own → hand it to CCBS (only
            // when a joint resolver is configured, so JointResolver=None stays byte-identical: this just counts).
            if (newlyReserved > 0 || scheduled.Count > 0 || _fleet.Count(a => a.Done) > doneBefore)
                noProgressStreak = 0;
            else if (++noProgressStreak >= ContinuousStallTrigger
                     && _policy.JointResolver != JointResolverKind.None)
            {
                await TryResolveContinuousStandoffAsync(nowMs).ConfigureAwait(false);
                noProgressStreak = 0;
            }

            if (_status == FleetLoopStatus.CollisionDetected)
                break;
        }
    }
}
