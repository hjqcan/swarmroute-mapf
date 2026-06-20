using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

internal sealed partial class FleetLoopRun
{
    /// <summary>
    /// The discrete-tick executor (Greedy / ScheduleFaithful). One integer tick = at most one CP hop per agent;
    /// it advances en-route agents through the right-of-way gate (or the schedule-faithful resolution), applies
    /// the liveness policy's directives in each phase, and records a frame per tick until everyone arrives or the
    /// tick budget is exhausted. This is the body of the original method's <c>while</c> loop; behaviour is
    /// byte-identical.
    /// </summary>
    private async Task ExecuteDiscreteAsync()
    {
        while (_fleet.Any(a => !a.Done))
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (_tick + 1 > _maxTicks)
            {
                // Tick budget exhausted before everyone arrived: report non-convergence (don't throw).
                _status = FleetLoopStatus.DidNotConverge;
                break;
            }

            _tick++;

            // Advance the fleet clock to this tick BEFORE planning, so every interval reserved this cycle is
            // expressed in tick units (the axis the executor below advances on). This is what couples the
            // reservation table's interval collision-freedom to actual execution.
            _advanceClock?.Invoke(_tick);

            // (0.4) (FMS-V1 R2) Station lifecycle, BEFORE planning. First count down in-service vehicles (a service
            //     that completes this tick releases its dock + closure and redirects the vehicle to parking), then run
            //     the per-tick dock-admission gate (an AGV staged at a pre-dock buffer is admitted onto the dock only
            //     once its service window is free, else it holds at the buffer). Both are inert without an FmsScenario,
            //     so a non-FMS run is byte-identical.
            await FmsTickServiceCountdownAsync().ConfigureAwait(false);
            await FmsDockAdmissionPassAsync().ConfigureAwait(false);

            // (0.6) Liveness policy — BeforePlanning phase. The policy decides the parked-gatekeeper recovery and
            //     step-aside (a WAITING agent walled out of its goal by finished vehicles on the only approach — a
            //     goal-blocking deadlock the RAG detector can't see, since parked vehicles hold no lease). It emits
            //     RestoreGoal (a gatekeeper whose yield window elapses re-plans back to its own goal) and
            //     RelocateParked (send a parked blocker aside so the walled agent can plan through). The decrement
            //     of each yielding gatekeeper's window is the executor's clock-tick mechanism, applied here.
            foreach (var p in _fleet.Where(a => a.YieldTicksRemaining > 0))
                p.YieldTicksRemaining--;

            _agentViews.Clear();
            foreach (var a in _fleet)
                _agentViews.Add(ViewOf(a));
            var beforePlanning = _policy.Evaluate(new LivenessSnapshot(
                _tick, LivenessPhase.BeforePlanning, _scheduleFaithful,
                _agentViews, _parkedCells));

            foreach (var directive in beforePlanning)
                switch (directive)
                {
                    case RestoreGoal rg:
                        // The gatekeeper's window has just elapsed: it is now pending and re-plans to its real goal.
                        _fleet.Single(a => a.Id == rg.AgentId).RedirectTarget = null;
                        break;

                    case RelocateParked rp:
                    {
                        var blocker = _fleet.Single(a => a.Id == rp.BlockerId);
                        var cell = blocker.Position;
                        _log?.Invoke($"gatekeeper-aside@tick{_tick}: {blocker.Id} (parked {cell}) steps to {rp.Dest} so {rp.WalledAgentId} can reach goal {_fleet.Single(a => a.Id == rp.WalledAgentId).Goal}.");
                        blocker.Done = false;
                        blocker.EnRoute = false;
                        blocker.Start = cell;          // it physically sits on its goal cell; re-plan from there to Dest
                        blocker.CpRoute = new[] { cell };
                        blocker.CpEntryTicks = Array.Empty<long>();
                        blocker.Idx = 0;
                        blocker.AllResources = Array.Empty<ResourceRef>();
                        blocker.RedirectTarget = rp.Dest; // EffectiveGoal becomes Dest; it holds there until recovery
                        blocker.YieldTicksRemaining = rp.YieldWindow;
                        blocker.StuckTicks = 0;
                        _parkedCells.Remove(cell);
                        _fleet.Single(a => a.Id == rp.WalledAgentId).StuckTicks = 0;
                        break;
                    }
                }

            // (1) Plan + reserve every agent that still needs right-of-way. A victim holding at its avoidance
            //     site (waiting for recovery) is intentionally NOT re-planned. (FMS-V1 R2) Two station states are
            //     owned by the FMS arm rather than the planner and are excluded here: an in-service vehicle is a hard
            //     immovable obstacle holding its dock lease, and a vehicle awaiting dock admission is held at its
            //     buffer (re-planning it would only re-derive a degenerate wait at the buffer). Both predicates are
            //     false for a non-FMS agent (MissionState stays Idle), so the pending set is byte-identical when off.
            var pending = _fleet
                .Where(a => !a.Done && !a.EnRoute && !a.HoldingAtAvoidSite && !a.PibtActive
                    && !a.InService && a.MissionState != AgvMissionState.WaitingDockAdmission)
                .Select(a => new AgentGoal(a.Id, a.Start, a.EffectiveGoal, a.Priority))
                .ToList();

            if (pending.Count > 0)
            {
                // Feed physical occupancy that is NOT represented by an active lease back into planning.
                // En-route agents still hold reservations, so TrafficControl already protects them. Parked
                // vehicles and waiting agents do not rely on active leases, but they still occupy their CP.
                // The planner exempts each agent's own start/goal, so adding every waiting Position does not
                // prevent that agent from departing its current CP.
                var report = await _cycle.RunCycleAsync(_roadmapId, pending, PhysicalBlockersExcept(), _cancellationToken).ConfigureAwait(false);
                foreach (var r in report.Results)
                {
                    var ag = _fleet.Single(a => a.Id == r.AgentId);
                    // Attempts beyond the first are prune-and-replan retries within this cycle.
                    ag.Replans += Math.Max(0, r.Attempts - 1);

                    if (r is { Reserved: true, Path: not null })
                    {
                        // The reserved route must still start where the agent physically stands; the terminal-equals-
                        // goal check is RELAXED for RHCR (a window may legitimately end at a frontier short of goal).
                        // BUT distinguish a *degenerate* window: when every forward move is blocked within the
                        // horizon, RHCR-SIPP can only reserve the current cell ("wait here, re-plan next window").
                        // That is a successful plan but makes NO progress, so committing it would reset StuckTicks
                        // every tick and the walled-out parked-gatekeeper step-aside would NEVER fire — a lone
                        // survivor whose path is sealed by parked vehicles then idles out the whole tick budget.
                        // Treat a no-progress wait-window as walled-out instead: release the wait lease, stay
                        // pending, and accrue StuckTicks so the gatekeeper clears the parked blockers (then the
                        // next plan progresses and StuckTicks resets). A brief legitimate wait (< the gatekeeper
                        // threshold) is harmless; only a sustained seal trips recovery.
                        var routeCps = r.Path!.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).Select(c => c.Resource.Id).ToList();
                        var madeProgress = routeCps.Count > 1
                            || (routeCps.Count == 1 && string.Equals(routeCps[0], ag.EffectiveGoal, StringComparison.Ordinal));
                        if (madeProgress)
                        {
                            SetEnRouteFromPath(ag, r.Path);
                        }
                        else
                        {
                            await _cycle.ReleaseAsync(ag.Id, r.Path!.Cells.Select(c => c.Resource).Distinct().ToList(), _cancellationToken).ConfigureAwait(false);
                            ag.StuckTicks++;
                        }
                    }
                    else
                    {
                        // Still couldn't get a route this cycle — count toward the walled-out (gatekeeper) trigger.
                        ag.StuckTicks++;
                    }
                }
            }

            // (2) Advance each en-route agent at most one CP forward — with an execution-time right-of-way gate:
            //     "if a vehicle is on the next control point, you wait." A move is taken only when the target CP
            //     is empty this tick AND not already claimed by a higher-priority mover; otherwise the agent
            //     holds position (keeping its leases) and retries next tick. This makes a same-CP collision
            //     impossible by construction — the reservation table coordinates *who plans through where*, and
            //     this gate is the final guarantee at the executor.
            //
            //     `occupantNow` = which agent physically sits on each CP at the start of the tick (pending agents
            //     wait on their origin, en-route on their current CP, arrived on their goal CP). `claimedNext` =
            //     the CPs that will be occupied after this tick; non-movers (pending/arrived) keep their CP so a
            //     mover can never step onto one. Conservative (a trailing agent waits one tick for the cell ahead
            //     to clear), which trades a little throughput for a hard no-collision guarantee.
            //
            //     Liveness: if the blocker on the next CP is a *parked* (arrived) vehicle, waiting would never
            //     clear — so instead the agent drops its reservation and rejoins planning from its current CP
            //     (a re-route). Next cycle the planner routes it around the parked cell (which is now in
            //     `parkedCells`). A transient blocker (a moving/waiting vehicle) just makes it wait one tick.
            // Snapshot every agent's pose at the START of this tick (before any advance), so block (3) can detect
            // an edge swap by comparing against their pose after the moves below.
            _posBefore.Clear();
            foreach (var a in _fleet)
                _posBefore[a.Id] = a.Position;

            // NB: .Add (not the indexer) preserves ToDictionary's throw-on-duplicate-key semantics — two agents
            // sharing a Position here is an invariant breach the original surfaced as an ArgumentException, and a
            // last-write-wins indexer would silently mask it. Behaviour must stay byte-identical.
            // `occupantNow` = which agent physically sits on each CP at the start of the tick (the run-scoped
            // occupantNowAgents buffer, refilled here). (Continued in the gate comment below.)
            _occupantNowAgents.Clear();
            foreach (var a in _fleet)
                _occupantNowAgents.Add(a.Position, a);

            // (2) Liveness policy — ClusterFormation phase. The policy detects physical-standoff clusters (the RAG
            //     detector can't see them: each member HOLDS, not waits for, its interval-exclusive reservation) and
            //     hands each to the configured joint resolver. It emits EnterJointResolver (PIBT: release + drive the
            //     member jointly one hop per tick in the Advance phase) and SolveClusterJointly (CBS: release the
            //     members, then solve + reserve the cluster's conflict-free paths atomically). Being !EnRoute after a
            //     release, a member's cell already feeds PhysicalBlockersExcept so the rest of the fleet routes around.
            _agentViews.Clear();
            foreach (var a in _fleet)
                _agentViews.Add(ViewOf(a));
            var clusterFormation = _policy.Evaluate(new LivenessSnapshot(
                _tick, LivenessPhase.ClusterFormation, _scheduleFaithful,
                _agentViews, _parkedCells));

            foreach (var directive in clusterFormation)
                switch (directive)
                {
                    case EnterJointResolver enter:
                        foreach (var id in enter.AgentIds)
                        {
                            var ag = _fleet.Single(a => a.Id == id);
                            await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);
                            ag.PibtActive = true;
                            ag.PibtEpisodeTicksLeft = _pibtEpisodeMaxTicks;
                            ag.PibtHeldTicks = 0;
                            ag.BlockedTicks = 0;
                            _log?.Invoke($"pibt-enter@tick{_tick}: {ag.Id} joins a congestion cluster at {ag.Position} (goal {ag.Goal}).");
                        }
                        break;

                    case SolveClusterJointly solve:
                    {
                        var members = solve.AgentIds.Select(id => _fleet.Single(a => a.Id == id)).ToList();

                        // Release each member's stalled reservation so the view CBS plans against is just the flowing
                        // fleet, then have the cycle solve + reserve the cluster jointly.
                        foreach (var ag in members)
                        {
                            await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);
                            ag.BlockedTicks = 0; // handled this tick — don't re-detect before it can advance
                        }

                        var clusterGoals = members
                            .Select(a => new AgentGoal(a.Id, a.Position, a.EffectiveGoal, a.Priority))
                            .ToList();
                        var clusterCells = members.Select(a => a.Position).ToHashSet(StringComparer.Ordinal);
                        var report = await _cycle.PlanClusterAsync(
                            _roadmapId,
                            clusterGoals,
                            PhysicalBlockersExcept(clusterCells),
                            _cancellationToken).ConfigureAwait(false);

                        var solved = 0;
                        foreach (var res in report.Results)
                        {
                            var ag = _fleet.Single(a => a.Id == res.AgentId);
                            ag.Replans++;
                            if (res is { Reserved: true, Path: not null })
                            {
                                SetEnRouteFromPath(ag, res.Path);
                                solved++;
                            }
                            // else: the member stays pending (released) and re-plans via SIPP next cycle (fallback).
                        }
                        _log?.Invoke($"cbs@tick{_tick}: solved a {members.Count}-agent standoff cluster ({solved} reserved).");
                        break;
                    }
                }

            _claimedNext.Clear();
            foreach (var a in _fleet.Where(a => !(a.EnRoute && !a.Done)))
                _claimedNext.Add(a.Position);

            // Schedule-faithful: resolve up front which agents step this tick, so the committed moves keep every
            // end-position distinct — a follower may take a cell its leader vacates this same tick (back-to-back
            // pipelining), but nobody steps onto a cell a non-moving vehicle holds (a waiting/parked vehicle holds
            // no reservation, so the schedule can't have accounted for it). Null in greedy mode, where the
            // per-agent right-of-way gate below decides instead.
            var scheduledAdvance = _executionMode == FleetExecutionMode.ScheduleFaithful
                ? ResolveScheduleFaithfulAdvances(_fleet, _tick, _parkedCells, _log)
                : null;

            // (2a) Liveness policy — JointDrive phase. The policy computes the joint-resolver (PIBT) cluster's joint
            //     single-hop move and which agents exit the episode this tick (MoveTo / ExitJointResolver). The
            //     executor applies each: a PIBT agent holds no lease, so a move just updates its physical pose (no
            //     ReleaseAsync) and claims the new cell; an exit parks it (goal reached) or disbands it back to SIPP.
            //     Done BEFORE the blocked-streak pass + Advance phase so a cell a PIBT agent parks on this tick is
            //     visible to the gate's parked-ahead net (exactly the executor's old ordering: PIBT-drive → gate).
            _agentViews.Clear();
            foreach (var a in _fleet)
                _agentViews.Add(ViewOf(a, scheduledToAdvance: scheduledAdvance?.Contains(a.Id) ?? false));
            foreach (var directive in _policy.Evaluate(new LivenessSnapshot(
                         _tick, LivenessPhase.JointDrive, _scheduleFaithful,
                         _agentViews,
                         _parkedCells)))
                switch (directive)
                {
                    case MoveTo move:
                    {
                        var ag = _fleet.Single(a => a.Id == move.AgentId);
                        ag.PibtEpisodeTicksLeft--;
                        if (!string.Equals(move.Cell, ag.Position, StringComparison.Ordinal))
                        {
                            ag.Start = move.Cell;     // a PIBT agent holds no lease: move it physically (Position = Start)
                            ag.CpRoute = new[] { move.Cell };
                            ag.Idx = 0;
                            ag.PibtHeldTicks = 0;
                        }
                        else
                        {
                            ag.PibtHeldTicks++;        // forced hold this tick
                        }
                        _claimedNext.Add(ag.Position);  // claim the (new) cell so no non-cluster mover steps onto it
                        break;
                    }

                    case ExitJointResolver exit:
                    {
                        var ag = _fleet.Single(a => a.Id == exit.AgentId);
                        ag.PibtActive = false;
                        // (FMS-V1 R2) A joint-resolver agent that exits on its station's buffer/dock follows the same
                        // station lifecycle as a normal arrival (stage for admission / go in service) instead of
                        // parking. Inert (returns false) without an FmsScenario, so the exit logic below is unchanged.
                        if (await FmsHandleArrivalAsync(ag, ag.Position).ConfigureAwait(false))
                        {
                            // handled: staged at the buffer or now in service (the FMS arm set the state + leases).
                        }
                        else if (string.Equals(ag.Position, ag.Goal, StringComparison.Ordinal))
                        {
                            // Reached the real goal: park (hand back nothing — it held no lease).
                            ag.Done = true;
                            _parkedCells.Add(ag.Position);
                            _flowtimeTicks += _tick;
                        }
                        else
                        {
                            // Disband back to prioritized-SIPP, which re-plans from this pose next cycle.
                            _log?.Invoke($"pibt-exit@tick{_tick}: {ag.Id} leaves PIBT at {ag.Position} ({exit.Reason}), re-planning.");
                        }
                        break;
                    }
                }

            // Up-front blocked-streak bookkeeping (schedule-faithful only). In schedule-faithful mode the advance
            // outcome is resolved deterministically by `scheduledAdvance`, so the en-route blocked streak the policy
            // reads to time the stall-reroute / head-on yield is reproduced here BEFORE consulting it — exactly the
            // inline `BlockedTicks++` / `= 0` the gate used to do, just hoisted so the policy sees the post-update
            // value. The parked-ahead skip uses the LIVE parked set (after PIBT arrivals), matching the gate, so an
            // agent rerouted around a freshly-parked cell never has its streak bumped. (Greedy keeps its own streak
            // sequentially in the gate below.)
            if (_scheduleFaithful)
                foreach (var ag in _fleet.Where(a => a is { EnRoute: true, Done: false }))
                {
                    var atEnd = ag.Idx >= ag.CpRoute.Count - 1;
                    if (atEnd || _parkedCells.Contains(ag.CpRoute[ag.Idx + 1]))
                        continue; // arrival / parked-ahead reroute: streak untouched
                    if (scheduledAdvance!.Contains(ag.Id))
                        ag.BlockedTicks = 0;
                    else if (ag.CpEntryTicks.Count == ag.CpRoute.Count && _tick >= ag.CpEntryTicks[ag.Idx + 1])
                        ag.BlockedTicks++;
                }

            // (2b) Liveness policy — Advance phase. The policy decides the schedule-faithful per-agent stall-reroute /
            //     head-on yield (against the post-PIBT poses + live parked set) plus the head-on diagnostic. The
            //     executor enacts the chosen yields in the gate below; the greedy gate's standoff diagnostic + blocked
            //     streak stay the executor's own mechanism.
            _yieldReason.Clear();
            _agentViews.Clear();
            foreach (var a in _fleet)
            {
                var atEnd = a is { EnRoute: true, Done: false } && a.Idx >= a.CpRoute.Count - 1;
                var nextParked = a is { EnRoute: true, Done: false } && !atEnd
                    && _parkedCells.Contains(a.CpRoute[a.Idx + 1]);
                var schedAdvance = scheduledAdvance?.Contains(a.Id) ?? false;
                var schedToMove = _scheduleFaithful && a is { EnRoute: true, Done: false } && !atEnd
                    && a.CpEntryTicks.Count == a.CpRoute.Count && _tick >= a.CpEntryTicks[a.Idx + 1];
                _agentViews.Add(ViewOf(a, atEnd, nextParked, schedAdvance, schedToMove));
            }
            foreach (var directive in _policy.Evaluate(new LivenessSnapshot(
                         _tick, LivenessPhase.Advance, _scheduleFaithful,
                         _agentViews,
                         _parkedCells)))
                switch (directive)
                {
                    case YieldAndReplan yield:
                        _yieldReason[yield.AgentId] = yield.Reason;
                        break;

                    case Diagnostic diag:
                        _log?.Invoke(diag.Message);
                        break;
                }

            foreach (var ag in _fleet.Where(a => a is { EnRoute: true, Done: false }))
            {
                var fromCp = ag.CpRoute[ag.Idx];
                var atGoalAlready = ag.Idx >= ag.CpRoute.Count - 1;

                if (!atGoalAlready)
                {
                    var toCp = ag.CpRoute[ag.Idx + 1];
                    var occupant = _occupantNowAgents.GetValueOrDefault(toCp);

                    // Permanent obstacle ahead is decided by `parkedCells` (the authoritative set of cells a vehicle
                    // has parked on), NOT by occupantNow[toCp].Done: occupantNow holds live agent references captured
                    // at tick-start, and an agent that vacates toCp this tick and parks elsewhere would make that
                    // stale reference read Done — a false "obstacle". parkedCells only ever holds a cell while a
                    // vehicle truly sits parked on it. This is a gate-time check (against the LIVE set, after PIBT
                    // arrivals + earlier same-tick arrivals park) — the executor's right-of-way safety net, so it
                    // stays mechanism (it must override a stale schedule that granted a step onto a now-parked cell).
                    if (_parkedCells.Contains(toCp))
                    {
                        // Re-route: release this path and rejoin planning from where we stand. `parkedCells` already
                        // lists the obstacle, so the next plan avoids it. A liveness fallback in BOTH modes: the
                        // planner routes around parked cells, but a vehicle can park AFTER this agent was planned.
                        ag.Replans++;
                        var held = ag.AllResources;
                        ag.EnRoute = false;
                        ag.Start = fromCp;
                        ag.CpRoute = new[] { fromCp };
                        ag.CpEntryTicks = Array.Empty<long>();
                        ag.Idx = 0;
                        ag.AllResources = Array.Empty<ResourceRef>();
                        _claimedNext.Add(fromCp);
                        await _cycle.ReleaseAsync(ag.Id, held, _cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // The advance decision is the ONLY thing the execution mode forks.
                    var advance = scheduledAdvance is not null
                        // Schedule-faithful: step iff the up-front resolution granted this agent a move this tick.
                        ? scheduledAdvance.Contains(ag.Id)
                        // Greedy (v0) right-of-way gate: enter only if the next CP is free this tick AND unclaimed
                        // by a prior mover. Conservative — a trailing vehicle waits one tick for the cell ahead.
                        : !_claimedNext.Contains(toCp) && occupant is null;

                    if (!advance)
                    {
                        _claimedNext.Add(fromCp); // hold the current CP (and all leases) this tick.

                        if (_executionMode == FleetExecutionMode.ScheduleFaithful)
                        {
                            // The schedule-faithful stall-reroute / head-on yield is the policy's decision (the
                            // blocked streak was updated up-front; the head-on diagnostic was already emitted). Here
                            // we only enact the chosen yield: drop the reservation and re-plan from this pose — the
                            // planner then routes around the blocker, and releasing our cells lets the rest of the
                            // stalled chain re-plan too. A non-yielding stalled agent just holds (its leases) this tick.
                            if (_yieldReason.TryGetValue(ag.Id, out var sfReason)
                                && (sfReason == YieldAndReplan.StallReason || sfReason == YieldAndReplan.HeadOnReason))
                            {
                                ag.Replans++;
                                var held = ag.AllResources;
                                ag.EnRoute = false;
                                ag.Start = fromCp;
                                ag.CpRoute = new[] { fromCp };
                                ag.CpEntryTicks = Array.Empty<long>();
                                ag.Idx = 0;
                                ag.AllResources = Array.Empty<ResourceRef>();
                                ag.BlockedTicks = 0;
                                await _cycle.ReleaseAsync(ag.Id, held, _cancellationToken).ConfigureAwait(false);
                            }
                            continue;
                        }

                        // Greedy gate: a non-advancing tick is a wait; surface a physical standoff diagnostically.
                        ag.BlockedTicks++;
                        if (_log is not null && ag.BlockedTicks == StandoffLogThreshold)
                        {
                            var occName = occupant?.Id ?? "(higher-priority mover)";
                            var mutualSwap = occupant is { EnRoute: true, Done: false }
                                && occupant.Idx + 1 < occupant.CpRoute.Count
                                && string.Equals(occupant.CpRoute[occupant.Idx + 1], fromCp, StringComparison.Ordinal);
                            _log(
                                $"standoff@tick{_tick}: {ag.Id} stalled {ag.BlockedTicks}+ ticks at {fromCp} wanting {toCp}; " +
                                $"blocked by {occName}" +
                                (mutualSwap ? $" which wants {fromCp} back -> HEAD-ON SWAP {ag.Id}<->{occName}" : string.Empty) +
                                $" (goal {ag.Goal}). " +
                                "Not a RAG cycle: both hold granted reservations, so deadlock detection won't fire.");
                        }
                        continue;
                    }

                    ag.Idx++;
                    ag.BlockedTicks = 0;
                    _claimedNext.Add(toCp);
                    await _cycle.ReleaseAsync(ag.Id,
                    [
                        RoadmapGraph.SiteRef(fromCp),
                        new ResourceRef(ResourceKind.Lane, $"{fromCp}-{toCp}"),
                    ], _cancellationToken).ConfigureAwait(false);
                }

                if (ag.Idx >= ag.CpRoute.Count - 1)
                {
                    var here = ag.CpRoute[ag.Idx];

                    // (FMS-V1 R2) Station arrival: reaching a pre-dock buffer (stage for admission) or an admitted
                    // dock point (go in service) is handled by the FMS arm, which keeps the agent NOT done and NOT
                    // parked. It returns false (and this is inert) without an FmsScenario, so the arrival logic below
                    // is byte-identical when off. Claim the current cell so no mover steps onto it this tick.
                    if (await FmsHandleArrivalAsync(ag, here).ConfigureAwait(false))
                    {
                        _claimedNext.Add(here);
                        continue;
                    }

                    if (!ag.FrontierIsGoal)
                    {
                        // RHCR: reached the rolling-horizon window frontier, not the (effective) goal yet. Drop
                        // the spent window and rejoin planning from here so the next cycle commits the next window
                        // toward EffectiveGoal. Same release-then-replan idiom as reroute-around-parked; the
                        // frontier CP stays claimed this tick so no mover steps onto it. This costs one re-plan
                        // tick per window boundary. An h<w lookahead to hide that cost was REJECTED for v2: the
                        // measured small-window damage is re-plan CHURN, not this pause, so re-planning more often
                        // would worsen it, and the high-density convergence it would broaden is already handled by
                        // the StepAside executor recovery above. See the v2 decision record in the design docs.
                        var held = ag.AllResources;
                        ag.EnRoute = false;
                        ag.Start = here;
                        ag.CpRoute = new[] { here };
                        ag.CpEntryTicks = Array.Empty<long>();
                        ag.Idx = 0;
                        ag.AllResources = Array.Empty<ResourceRef>();
                        _claimedNext.Add(here);
                        await _cycle.ReleaseAsync(ag.Id, held, _cancellationToken).ConfigureAwait(false);
                    }
                    else if (ag.RedirectTarget is not null)
                    {
                        // A relocated parked gatekeeper reached its step-aside site: hold here (keeping the
                        // aside-site lease) — this is NOT the real goal, so the agent is not "done". The
                        // BeforePlanning policy's RestoreGoal directive clears RedirectTarget once its yield
                        // window elapses, and the agent then re-plans back toward its own goal.
                        ag.EnRoute = false;
                        ag.Start = here;
                        _claimedNext.Add(here);
                    }
                    else
                    {
                        // Arrived at the real goal: hand back everything still held (goal CP + any remainder) — no
                        // leak — and mark the goal CP as a parked obstacle so the rest of the fleet routes around it.
                        ag.Done = true;
                        ag.EnRoute = false;
                        _claimedNext.Add(here);
                        _parkedCells.Add(here);
                        _flowtimeTicks += _tick; // this agent reached its goal at this tick
                        await _cycle.ReleaseAsync(ag.Id, ag.AllResources, _cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // (3) Safety: no two agents holding right-of-way (moving or just-arrived) — or being driven by PIBT —
            //     share a CP this tick. PIBT agents hold no lease but physically occupy a cell, so they must be in
            //     the net or a PIBT bug could co-locate undetected.
            var occupied = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ag in _fleet.Where(a => a.EnRoute || a.Done || a.PibtActive).OrderBy(a => a.Id, StringComparer.Ordinal))
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

            // (3b) Safety: no two agents SWAPPED cells this tick (a head-on edge collision — they cross on one
            //      lane in opposite directions). Their end CPs are distinct, so (3) above can't see it. The
            //      schedule-faithful resolver forbids this up front; detect any residual so a run reports
            //      CollisionDetected honestly instead of a false "no collision", and log the offenders.
            foreach (var ag in _fleet.Where(a => a.EnRoute || a.Done || a.PibtActive))
            {
                if (!_posBefore.TryGetValue(ag.Id, out var prev) || string.Equals(prev, ag.Position, StringComparison.Ordinal))
                    continue; // didn't move this tick
                if (occupied.TryGetValue(prev, out var other)
                    && StringComparer.Ordinal.Compare(ag.Id, other) < 0 // count each pair once
                    && _posBefore.TryGetValue(other, out var otherPrev)
                    && string.Equals(otherPrev, ag.Position, StringComparison.Ordinal))
                {
                    _collisions++;
                    _log?.Invoke($"EDGE-SWAP@tick{_tick}: {ag.Id} and {other} crossed lane {prev}<->{ag.Position} (head-on). " +
                        "This is an execution/schedule desync — the resolver should have held one of them.");
                    if (_collision is null)
                    {
                        _status = FleetLoopStatus.CollisionDetected;
                        _collision = new FleetCollisionInfo(_tick, ag.Position, [other, ag.Id]);
                    }
                }
            }

            _maxConcurrent = Math.Max(_maxConcurrent, _fleet.Count(a => a.EnRoute));

            // (4) Record the frame: every agent's position + motion state this tick (incl. a colliding tick).
            _frames.Add(new FleetTickFrame(
                _tick,
                _fleet
                    .OrderBy(a => a.Id, StringComparer.Ordinal)
                    .Select(a => new FleetTickPosition(a.Id, a.Position, a.State))
                    .ToList()));

            // Stop the run at the first collision — physical state past it is meaningless to replay.
            if (_status == FleetLoopStatus.CollisionDetected)
                break;
        }
    }
}
