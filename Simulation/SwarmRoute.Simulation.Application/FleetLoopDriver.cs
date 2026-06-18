using SwarmRoute.Coordination.Application;
using SwarmRoute.Coordination.Application.Deadlock;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

/// <summary>One agent's navigation request for a closed-loop run.</summary>
/// <param name="Id">Stable agent id.</param>
/// <param name="StartSiteId">Origin control-point id.</param>
/// <param name="GoalSiteId">Destination control-point id.</param>
/// <param name="Priority">Right-of-way order (lower = planned/reserved first); ties broken by id.</param>
public sealed record FleetAgentSpec(string Id, string StartSiteId, string GoalSiteId, int Priority);

/// <summary>The motion state of an agent on one timeline tick.</summary>
public enum AgentMotionState
{
    /// <summary>Pending right-of-way: not yet granted, sitting at its start control point.</summary>
    Waiting,

    /// <summary>En route: holds a reserved path and is at its current control point.</summary>
    Moving,

    /// <summary>Reached its goal and released all its leases.</summary>
    Arrived
}

/// <summary>How a closed-loop run ended.</summary>
public enum FleetLoopStatus
{
    /// <summary>Every agent reached its goal with no collision.</summary>
    Completed,

    /// <summary>Two agents holding right-of-way shared a control point on a tick (engine/executor collision).</summary>
    CollisionDetected,

    /// <summary>The fleet did not all arrive within the tick budget (livelock / deadlock / starvation).</summary>
    DidNotConverge
}

/// <summary>How the executor advances en-route agents through their reserved CP route.</summary>
public enum FleetExecutionMode
{
    /// <summary>
    /// v0 behaviour: advance at most one CP per tick through a conservative right-of-way gate (enter the next CP
    /// only if it is empty this tick). Pairs with the space-only Dijkstra planner, whose intervals are spatial
    /// locks rather than a faithful schedule.
    /// </summary>
    Greedy,

    /// <summary>
    /// v1 behaviour: advance each agent to the next CP exactly at its planned arrival tick (the CP cell's
    /// interval start on the unified <c>HopMs</c> axis). Pairs with the SIPP planner, whose schedule is
    /// interval-exclusive by construction — so honouring it is collision-free (back-to-back following on
    /// touching half-open intervals), and the defensive same-CP safety check stays as a regression net.
    /// </summary>
    ScheduleFaithful
}

/// <summary>One agent's recorded position on one tick.</summary>
public sealed record FleetTickPosition(string AgentId, string SiteId, AgentMotionState State);

/// <summary>One recorded tick of the closed loop: where every agent is.</summary>
public sealed record FleetTickFrame(int Tick, IReadOnlyList<FleetTickPosition> Positions);

/// <summary>Details of the first detected collision (when <see cref="FleetLoopStatus.CollisionDetected"/>).</summary>
public sealed record FleetCollisionInfo(int Tick, string SiteId, IReadOnlyList<string> AgentIds);

/// <summary>Aggregate outcome of a closed-loop run.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Ticks">Ticks executed (one frame each).</param>
/// <param name="Collisions">Detected CP collisions among agents holding right-of-way (0 for a clean run).</param>
/// <param name="Arrived">Agents that reached their goal.</param>
/// <param name="Replans">Total prune-and-replan retries observed across the run.</param>
/// <param name="Redirects">Total deadlock redirects enacted (victims sent to an avoidance site).</param>
/// <param name="Recoveries">Total deadlock recoveries (victims restored to their original goal after the cycle cleared).</param>
/// <param name="FlowtimeTicks">Sum over arrived agents of the tick at which each reached its goal (lower is
/// better throughput). A schedule-faithful run that pipelines tightly accrues less flowtime than a greedy run
/// that holds trailing vehicles a tick per congested cell.</param>
public sealed record FleetLoopStats(
    FleetLoopStatus Status, int Ticks, int Collisions, int Arrived, int Replans, int Redirects = 0, int Recoveries = 0,
    int FlowtimeTicks = 0);

/// <summary>The full recorded result of a closed-loop run.</summary>
/// <param name="Frames">Tick-by-tick timeline (includes the colliding tick when one occurs).</param>
/// <param name="PerAgentRoute">Per agent: the CP trail it actually occupied, with consecutive duplicates collapsed.</param>
/// <param name="Stats">Aggregate stats.</param>
/// <param name="MaxConcurrentEnRoute">Peak number of simultaneously en-route agents (a liveness/parallelism signal).</param>
/// <param name="Collision">The first collision's details, or <see langword="null"/> when none occurred.</param>
public sealed record FleetLoopResult(
    IReadOnlyList<FleetTickFrame> Frames,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PerAgentRoute,
    FleetLoopStats Stats,
    int MaxConcurrentEnRoute,
    FleetCollisionInfo? Collision);

/// <summary>
/// Production form of the validated closed-loop driver (the body previously inlined in
/// <c>ClosedLoopIntegrationTests.RunToCompletionAsync</c>). Given an <see cref="IFleetCoordinationCycle"/>, a
/// roadmap id, its <see cref="RoadmapGraph"/> and a fleet of <see cref="FleetAgentSpec"/>s, it drives the REAL
/// engine and records a tick-by-tick timeline:
/// <list type="number">
///   <item><description><b>Tick clock</b>: advance the fleet clock to the current tick so every interval reserved
///     this cycle is on the same axis the executor moves on (one tick = one CP hop).</description></item>
///   <item><description><b>Plan + reserve</b> every idle agent via <see cref="IFleetCoordinationCycle.RunCycleAsync"/>
///     (deterministic priority order). Newly-reserved agents become en route at their start CP.</description></item>
///   <item><description><b>Advance</b> each en-route agent at most one CP, through a right-of-way gate: it enters
///     the next CP only if no vehicle occupies it this tick (else it waits), awaiting
///     <see cref="IFleetCoordinationCycle.ReleaseAsync"/> to hand back the CP+lane it left behind; on arrival it
///     releases all its path resources (no leak).</description></item>
///   <item><description><b>Record</b> a frame for the tick (every agent's CP + motion state).</description></item>
///   <item><description><b>Check safety</b> (defensive): assert no two agents holding right-of-way share a CP.
///     With the gate this can no longer happen; if it ever did it would be reported via
///     <see cref="FleetLoopStatus.CollisionDetected"/> (the frame is recorded) as a regression signal.</description></item>
/// </list>
/// Deterministic given deterministic inputs (the tick clock removes the wall-clock dependence). This driver is a
/// <b>verifier</b>: it does NOT throw on a standoff — non-convergence is reported via
/// <see cref="FleetLoopResult.Stats"/> (<see cref="FleetLoopStatus.DidNotConverge"/>) so callers (sim API,
/// frontend) can surface it. It throws only on an internal invariant breach (a reserved path that doesn't run
/// start→goal).
/// <para><b>Collision-freedom.</b> Two layers guarantee it: the reservation table coordinates <em>who plans
/// through which CP and when</em> (interval-exclusive leases), and the executor's right-of-way gate is the final
/// stop-and-wait so a vehicle never enters an occupied CP. A pathological standoff therefore degrades to
/// <see cref="FleetLoopStatus.DidNotConverge"/>, never a crash. The gate is conservative (a trailing vehicle
/// waits one tick for the cell ahead to clear); v1's SIPP planner will tighten throughput by routing in time.</para>
/// </summary>
public sealed class FleetLoopDriver
{
    private sealed class RunAgent(FleetAgentSpec spec)
    {
        public string Id { get; } = spec.Id;
        /// <summary>The current planning origin — the original start, or the CP it was re-routed from.</summary>
        public string Start { get; set; } = spec.StartSiteId;
        public string Goal { get; } = spec.GoalSiteId;
        public int Priority { get; } = spec.Priority;

        public bool EnRoute { get; set; }
        public bool Done { get; set; }
        public IReadOnlyList<string> CpRoute { get; set; } = Array.Empty<string>();

        /// <summary>Parallel to <see cref="CpRoute"/>: the planned fleet-clock tick at which the agent is
        /// scheduled to arrive at each CP (the CP cell's interval start). Consumed only in
        /// <see cref="FleetExecutionMode.ScheduleFaithful"/>; left empty under greedy execution and after a
        /// reset (the agent is then not en route, so the executor never reads it until it re-plans).</summary>
        public IReadOnlyList<long> CpEntryTicks { get; set; } = Array.Empty<long>();

        public IReadOnlyList<ResourceRef> AllResources { get; set; } = Array.Empty<ResourceRef>();
        public int Idx { get; set; }
        public int Replans { get; set; }

        /// <summary>Consecutive ticks this en-route agent has failed to advance at the right-of-way gate
        /// (reset to 0 the moment it moves). A high streak is a physical standoff — a head-on swap or a
        /// circular blocking chain the lockstep executor can't resolve — which the diagnostic log surfaces.</summary>
        public int BlockedTicks { get; set; }

        /// <summary>When set, the agent is yielding a deadlock: it is being routed to this avoidance site
        /// instead of its goal, until the case is recovered and the target is cleared.</summary>
        public string? RedirectTarget { get; set; }

        /// <summary>The deadlock case whose redirect this agent is currently enacting.</summary>
        public Guid? RedirectCaseId { get; set; }

        /// <summary>How many distinct deadlock redirects this agent has been given (anti-livelock guard).</summary>
        public int RedirectAttempts { get; set; }

        /// <summary>Best (smallest) graph distance to the ORIGINAL goal observed at a redirect decision; the
        /// anti-livelock guard requires this to strictly decrease across redirects, else it escalates.</summary>
        public long BestDistanceToGoal { get; set; } = long.MaxValue;

        /// <summary>The goal to plan toward this tick: the avoidance site while redirecting, else the real goal.</summary>
        public string EffectiveGoal => RedirectTarget ?? Goal;

        /// <summary>True while the agent is yielding to an avoidance site and physically sitting on it (waiting
        /// for recovery to restore its original goal). Such an agent is not re-planned and is not "done".</summary>
        public bool HoldingAtAvoidSite => RedirectTarget is not null && !EnRoute && !Done && Start == RedirectTarget;

        /// <summary>Where the agent physically sits this tick: current CP if en route/arrived, else its origin.</summary>
        public string Position => EnRoute || Done ? CpRoute[Idx] : Start;

        public AgentMotionState State => Done ? AgentMotionState.Arrived
            : EnRoute ? AgentMotionState.Moving
            : AgentMotionState.Waiting;
    }

    /// <summary>
    /// Runs the closed loop, recording a frame per tick, until every agent arrives or the tick budget is
    /// exhausted. The fleet is processed in a stable order so a given input always produces the same timeline.
    /// </summary>
    /// <param name="advanceClock">Sets the fleet clock to the current tick at the start of each tick (the sim
    /// passes its <see cref="ManualFleetClock"/>'s setter); when null the engine's own clock is left untouched.</param>
    /// <param name="redirects">Optional deadlock-redirect projection. When supplied, the driver enacts the
    /// Deadlock context's resolutions: a victim with an active redirect is re-planned from its current CP to the
    /// avoidance site; a recovered victim is restored to its original goal; an escalated (livelock) victim stops
    /// being redirected. When null, deadlock resolution is inert (back-compat with the plain sim/closed-loop tests).</param>
    /// <param name="recoverTick">Optional per-tick case-recovery pump (bound to
    /// <c>IDeadlockRecoveryService.TryRecoverAllAsync</c> by the host/test). It marks cases recovered once their
    /// cycle has cleared; this driver restores the original goal only after the victim is physically holding at
    /// its avoidance site.</param>
    /// <param name="escalateLivelock">Optional escalation hook (bound to <c>IDeadlockEscalationService.EscalateLivelockAsync</c>):
    /// invoked when the anti-livelock guard fires (a redirect would not strictly reduce the victim's distance to its
    /// original goal, or the attempt cap is hit).</param>
    /// <param name="executionMode">How en-route agents advance: <see cref="FleetExecutionMode.Greedy"/> (v0
    /// right-of-way gate, the default so existing callers/tests keep v0 behaviour) or
    /// <see cref="FleetExecutionMode.ScheduleFaithful"/> (follow the SIPP-planned per-CP arrival ticks).</param>
    /// <exception cref="ArgumentNullException">If <paramref name="cycle"/>, <paramref name="graph"/> or <paramref name="agents"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="maxTicks"/> &lt; 1.</exception>
    /// <exception cref="FleetLoopException">Only on an internal invariant breach (reserved path not start→goal).</exception>
    public async Task<FleetLoopResult> RunToCompletionAsync(
        IFleetCoordinationCycle cycle,
        Guid roadmapId,
        RoadmapGraph graph,
        IReadOnlyCollection<FleetAgentSpec> agents,
        int maxTicks,
        Action<long>? advanceClock = null,
        IFleetRedirectQuery? redirects = null,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>>? recoverTick = null,
        Func<string, CancellationToken, Task>? escalateLivelock = null,
        FleetExecutionMode executionMode = FleetExecutionMode.Greedy,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);

        // Anti-livelock backstop: a single victim may be redirected at most this many times before the run
        // escalates it as a livelock (in addition to the strict distance-decrease guard below).
        const int maxRedirectAttempts = 5;

        // Diagnostic: after this many consecutive ticks blocked at the gate, an en-route agent is reported as a
        // physical standoff (head-on swap / blocking chain) — the case RAG cycle-detection can't see because the
        // agents HOLD their (interval-separated) reservations rather than wait for one another in the table.
        const int standoffLogThreshold = 12;

        // Stable fleet order (priority then ordinal id) so timeline + routes are reproducible.
        var fleet = agents
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new RunAgent(a))
            .ToList();

        var frames = new List<FleetTickFrame>();
        var tick = 0;
        var collisions = 0;
        var maxConcurrent = 0;
        var redirects_ = 0;
        var recoveries = 0;
        var flowtimeTicks = 0;
        var status = FleetLoopStatus.Completed;
        FleetCollisionInfo? collision = null;

        // Control points occupied by parked (arrived) vehicles. Fed to the planner each cycle as obstacles so the
        // rest of the fleet routes AROUND finished agents instead of stalling behind them at the executor gate.
        var parkedCells = new HashSet<string>(StringComparer.Ordinal);

        // Record the initial state (tick 0): every agent waiting at its start CP, before any movement. This
        // gives a viewer a frame where the fleet sits at its origins, so playback visibly departs from A (the
        // loop below reserves AND advances an agent within the same tick, so tick 1 is already one CP in).
        frames.Add(new FleetTickFrame(
            0,
            fleet
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => new FleetTickPosition(a.Id, a.Position, a.State))
                .ToList()));

        // Drops an agent's in-flight reservation and resets it to "pending at its current CP" so the next cycle
        // re-plans it (toward its EffectiveGoal) from where it physically stands. Used to redirect a deadlock
        // victim onto its avoidance route and to restore it to its real goal once recovered.
        async Task YieldAndReplanFromCurrentAsync(RunAgent ag)
        {
            var here = ag.Position;
            if (ag.AllResources.Count > 0)
                await cycle.ReleaseAsync(ag.Id, ag.AllResources, cancellationToken).ConfigureAwait(false);
            ag.EnRoute = false;
            ag.Start = here;
            ag.CpRoute = new[] { here };
            ag.CpEntryTicks = Array.Empty<long>();
            ag.Idx = 0;
            ag.AllResources = Array.Empty<ResourceRef>();
        }

        while (fleet.Any(a => !a.Done))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (tick + 1 > maxTicks)
            {
                // Tick budget exhausted before everyone arrived: report non-convergence (don't throw).
                status = FleetLoopStatus.DidNotConverge;
                break;
            }

            tick++;

            // Advance the fleet clock to this tick BEFORE planning, so every interval reserved this cycle is
            // expressed in tick units (the axis the executor below advances on). This is what couples the
            // reservation table's interval collision-freedom to actual execution.
            advanceClock?.Invoke(tick);

            // (0) Deadlock resolution (only when wired). Recover cleared victims first (so a restored victim
            //     re-plans toward its real goal THIS tick), then enact any newly-requested redirects. This is the
            //     consumer side of Deadlock.Case.ResolutionRequested/Resolved/Escalated: the events populate the
            //     redirect store during the previous cycle's TryReserve; the driver (the v0 execution layer that
            //     actually knows each agent's pose) acts on them here.
            if (redirects is not null)
            {
                // 0a. Pump recovery: drive ConfirmCleared → Recover → Resolved for any open resolution whose
                //     cycle has cleared. Runs BETWEEN ticks (never nested inside a TryReserve publish).
                if (recoverTick is not null)
                    await recoverTick(cancellationToken).ConfigureAwait(false);

                foreach (var ag in fleet.Where(a => a.RedirectTarget is not null && !a.Done))
                {
                    if (!ag.RedirectCaseId.HasValue)
                        continue;

                    var redirectCaseId = ag.RedirectCaseId.GetValueOrDefault();

                    // Recovered means the deadlock case is clear AND the victim has completed its avoidance
                    // command. Do not restore the original goal while it is still travelling to the avoid site.
                    if (redirects.IsRecovered(ag.Id, redirectCaseId))
                    {
                        if (!ag.HoldingAtAvoidSite)
                            continue;

                        recoveries++;
                        ag.RedirectTarget = null;
                        ag.RedirectCaseId = null;
                        if (redirects is IFleetRedirectAcknowledger acknowledger)
                            acknowledger.MarkRedirectCompleted(redirectCaseId, ag.Id);
                        await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);
                        continue;
                    }

                    // Escalated (e.g. livelock / failed automatic resolution): stop yielding immediately and let
                    // the victim re-plan from its current physical position.
                    if (redirects.IsEscalated(ag.Id, redirectCaseId))
                    {
                        ag.RedirectTarget = null;
                        ag.RedirectCaseId = null;
                        await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);
                    }
                }

                // 0b. Enact new redirects requested by the Deadlock context.
                foreach (var intent in redirects.ActiveRedirects)
                {
                    var ag = fleet.FirstOrDefault(a => a.Id == intent.VictimAgentId);
                    if (ag is null || ag.Done)
                        continue;
                    if (ag.RedirectCaseId == intent.CaseId
                        && string.Equals(ag.RedirectTarget, intent.AvoidSiteId, StringComparison.Ordinal))
                        continue; // already enacting this redirect

                    // Anti-livelock: a redirect is allowed only if the victim's distance to its ORIGINAL goal
                    // strictly decreased since the last redirect (net progress), bounded by an attempt cap.
                    // Otherwise escalate as a livelock and stop redirecting it (DoD §6 / WS-Q3).
                    var dist = graph.DistanceTo(ag.Position, ag.Goal) ?? long.MaxValue;
                    var noProgress = ag.RedirectAttempts >= 1 && dist >= ag.BestDistanceToGoal;
                    if (noProgress || ag.RedirectAttempts >= maxRedirectAttempts)
                    {
                        if (escalateLivelock is not null)
                            await escalateLivelock(ag.Id, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    ag.BestDistanceToGoal = Math.Min(ag.BestDistanceToGoal, dist);
                    ag.RedirectAttempts++;
                    redirects_++;
                    ag.RedirectTarget = intent.AvoidSiteId;
                    ag.RedirectCaseId = intent.CaseId;
                    await YieldAndReplanFromCurrentAsync(ag).ConfigureAwait(false);
                }
            }

            // (1) Plan + reserve every agent that still needs right-of-way. A victim holding at its avoidance
            //     site (waiting for recovery) is intentionally NOT re-planned.
            var pending = fleet
                .Where(a => !a.Done && !a.EnRoute && !a.HoldingAtAvoidSite)
                .Select(a => new AgentGoal(a.Id, a.Start, a.EffectiveGoal, a.Priority))
                .ToList();

            if (pending.Count > 0)
            {
                // Feed physical occupancy that is NOT represented by an active lease back into planning.
                // En-route agents still hold reservations, so TrafficControl already protects them. Parked
                // vehicles and waiting agents do not rely on active leases, but they still occupy their CP.
                // The planner exempts each agent's own start/goal, so adding every waiting Position does not
                // prevent that agent from departing its current CP.
                var physicallyBlockedCells = parkedCells
                    .Concat(fleet.Where(a => !a.EnRoute && !a.Done).Select(a => a.Position))
                    .ToHashSet(StringComparer.Ordinal);
                var blocked = physicallyBlockedCells.Count == 0
                    ? null
                    : physicallyBlockedCells.Select(RoadmapGraph.SiteRef).ToHashSet();
                var report = await cycle.RunCycleAsync(roadmapId, pending, blocked, cancellationToken).ConfigureAwait(false);
                foreach (var r in report.Results)
                {
                    var ag = fleet.Single(a => a.Id == r.AgentId);
                    // Attempts beyond the first are prune-and-replan retries within this cycle.
                    ag.Replans += Math.Max(0, r.Attempts - 1);

                    if (r is { Reserved: true, Path: not null })
                    {
                        ag.EnRoute = true;
                        ag.Idx = 0;
                        var cpCells = r.Path!.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).ToList();
                        ag.CpRoute = cpCells.Select(c => c.Resource.Id).ToList();
                        // Schedule-faithful execution reads each CP's planned arrival tick from the same cells.
                        ag.CpEntryTicks = cpCells.Select(c => c.Interval.StartMs).ToList();
                        ag.AllResources = r.Path!.Cells.Select(c => c.Resource).Distinct().ToList();

                        if (ag.CpRoute.Count == 0 || ag.CpRoute[0] != ag.Start || ag.CpRoute[^1] != ag.EffectiveGoal)
                            throw new FleetLoopException(
                                $"Reserved path for '{ag.Id}' does not run {ag.Start}->{ag.EffectiveGoal} " +
                                $"(got [{string.Join(",", ag.CpRoute)}]).");
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
            var occupantNow = fleet.ToDictionary(a => a.Position, a => a, StringComparer.Ordinal);
            var claimedNext = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in fleet.Where(a => !(a.EnRoute && !a.Done)))
                claimedNext.Add(a.Position);

            // Schedule-faithful: resolve up front which agents step this tick, so the committed moves keep every
            // end-position distinct — a follower may take a cell its leader vacates this same tick (back-to-back
            // pipelining), but nobody steps onto a cell a non-moving vehicle holds (a waiting/parked vehicle holds
            // no reservation, so the schedule can't have accounted for it). Null in greedy mode, where the
            // per-agent right-of-way gate below decides instead.
            var scheduledAdvance = executionMode == FleetExecutionMode.ScheduleFaithful
                ? ResolveScheduleFaithfulAdvances(fleet, tick, parkedCells)
                : null;

            foreach (var ag in fleet.Where(a => a is { EnRoute: true, Done: false }))
            {
                var fromCp = ag.CpRoute[ag.Idx];
                var atGoalAlready = ag.Idx >= ag.CpRoute.Count - 1;

                if (!atGoalAlready)
                {
                    var toCp = ag.CpRoute[ag.Idx + 1];
                    var occupant = occupantNow.GetValueOrDefault(toCp);

                    // Permanent obstacle ahead is decided by `parkedCells` (the authoritative set of cells a
                    // vehicle has parked on), NOT by occupantNow[toCp].Done: occupantNow holds live agent
                    // references captured at tick-start, and an agent that vacates toCp this tick and parks
                    // elsewhere would make that stale reference read Done — a false "obstacle". parkedCells only
                    // ever holds a cell while a vehicle truly sits parked on it.
                    if (parkedCells.Contains(toCp))
                    {
                        // Re-route: release this path and rejoin planning from where we stand. `parkedCells`
                        // already lists the obstacle, so the next plan avoids it. A liveness fallback in BOTH
                        // modes: SIPP routes around parked cells at plan time, but a vehicle can park AFTER this
                        // agent was planned.
                        ag.Replans++;
                        var held = ag.AllResources;
                        ag.EnRoute = false;
                        ag.Start = fromCp;
                        ag.CpRoute = new[] { fromCp };
                        ag.CpEntryTicks = Array.Empty<long>();
                        ag.Idx = 0;
                        ag.AllResources = Array.Empty<ResourceRef>();
                        claimedNext.Add(fromCp);
                        await cycle.ReleaseAsync(ag.Id, held, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // The advance decision is the ONLY thing the execution mode forks.
                    var advance = scheduledAdvance is not null
                        // Schedule-faithful: step iff the up-front resolution granted this agent a move this tick.
                        ? scheduledAdvance.Contains(ag.Id)
                        // Greedy (v0) right-of-way gate: enter only if the next CP is free this tick AND unclaimed
                        // by a prior mover. Conservative — a trailing vehicle waits one tick for the cell ahead.
                        : !claimedNext.Contains(toCp) && occupant is null;

                    if (!advance)
                    {
                        claimedNext.Add(fromCp); // hold the current CP (and all leases) this tick.

                        // Standoff diagnostics are a greedy-gate concern only: in schedule-faithful mode a
                        // non-advancing tick is a planned wait, not a physical deadlock the table couldn't see.
                        if (executionMode == FleetExecutionMode.Greedy)
                        {
                            ag.BlockedTicks++;
                            if (log is not null && ag.BlockedTicks == standoffLogThreshold)
                            {
                                var occName = occupant?.Id ?? "(higher-priority mover)";
                                var mutualSwap = occupant is { EnRoute: true, Done: false }
                                    && occupant.Idx + 1 < occupant.CpRoute.Count
                                    && string.Equals(occupant.CpRoute[occupant.Idx + 1], fromCp, StringComparison.Ordinal);
                                log(
                                    $"standoff@tick{tick}: {ag.Id} stalled {ag.BlockedTicks}+ ticks at {fromCp} wanting {toCp}; " +
                                    $"blocked by {occName}" +
                                    (mutualSwap ? $" which wants {fromCp} back -> HEAD-ON SWAP {ag.Id}<->{occName}" : string.Empty) +
                                    $" (goal {ag.Goal}; redirects so far={redirects_}). " +
                                    "Not a RAG cycle: both hold granted reservations, so deadlock detection won't fire.");
                            }
                        }
                        continue;
                    }

                    ag.Idx++;
                    ag.BlockedTicks = 0;
                    claimedNext.Add(toCp);
                    await cycle.ReleaseAsync(ag.Id,
                    [
                        RoadmapGraph.SiteRef(fromCp),
                        new ResourceRef(ResourceKind.Lane, $"{fromCp}-{toCp}"),
                    ], cancellationToken).ConfigureAwait(false);
                }

                if (ag.Idx >= ag.CpRoute.Count - 1)
                {
                    var here = ag.CpRoute[ag.Idx];
                    if (ag.RedirectTarget is not null)
                    {
                        // Reached the avoidance site: hold here (keeping the avoid-site lease) and wait for the
                        // deadlock case to recover — this is NOT the real goal, so the agent is not "done". The
                        // (0) block restores the original goal once the cycle clears.
                        ag.EnRoute = false;
                        ag.Start = here;
                        claimedNext.Add(here);
                    }
                    else
                    {
                        // Arrived at the real goal: hand back everything still held (goal CP + any remainder) — no
                        // leak — and mark the goal CP as a parked obstacle so the rest of the fleet routes around it.
                        ag.Done = true;
                        ag.EnRoute = false;
                        claimedNext.Add(here);
                        parkedCells.Add(here);
                        flowtimeTicks += tick; // this agent reached its goal at this tick
                        await cycle.ReleaseAsync(ag.Id, ag.AllResources, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // (3) Safety: no two agents holding right-of-way (moving or just-arrived) share a CP this tick.
            var occupied = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ag in fleet.Where(a => a.EnRoute || a.Done).OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (occupied.TryGetValue(ag.Position, out var other))
                {
                    collisions++;
                    if (collision is null)
                    {
                        status = FleetLoopStatus.CollisionDetected;
                        collision = new FleetCollisionInfo(tick, ag.Position, [other, ag.Id]);
                    }
                }
                else
                {
                    occupied[ag.Position] = ag.Id;
                }
            }

            maxConcurrent = Math.Max(maxConcurrent, fleet.Count(a => a.EnRoute));

            // (4) Record the frame: every agent's position + motion state this tick (incl. a colliding tick).
            frames.Add(new FleetTickFrame(
                tick,
                fleet
                    .OrderBy(a => a.Id, StringComparer.Ordinal)
                    .Select(a => new FleetTickPosition(a.Id, a.Position, a.State))
                    .ToList()));

            // Stop the run at the first collision — physical state past it is meaningless to replay.
            if (status == FleetLoopStatus.CollisionDetected)
                break;
        }

        // Per-agent route = the actual walked trail (A → … → B), reconstructed from the recorded frames by
        // collapsing consecutive same-CP positions. Using the agent's FINAL reserved leg (a.CpRoute) instead
        // would drop the earlier segments of any agent that re-planned (reroute-around-parked) or was
        // redirected (deadlock yield) mid-journey, leaving the canvas polyline detached from its "A" marker —
        // the "some AGVs have no line" artefact. The trail always begins at the start (frame 0) and ends at the
        // agent's last position (its goal when arrived).
        var routes = fleet.ToDictionary(
            a => a.Id,
            a =>
            {
                var trail = new List<string>();
                foreach (var frame in frames)
                {
                    var sid = frame.Positions.First(p => p.AgentId == a.Id).SiteId;
                    if (trail.Count == 0 || !string.Equals(trail[^1], sid, StringComparison.Ordinal))
                        trail.Add(sid);
                }
                if (trail.Count == 0)
                    trail.Add(a.Start);
                return (IReadOnlyList<string>)trail;
            },
            StringComparer.Ordinal);

        var stats = new FleetLoopStats(
            Status: status,
            Ticks: tick,
            Collisions: collisions,
            Arrived: fleet.Count(a => a.Done),
            Replans: fleet.Sum(a => a.Replans),
            Redirects: redirects_,
            Recoveries: recoveries,
            FlowtimeTicks: flowtimeTicks);

        return new FleetLoopResult(frames, routes, stats, maxConcurrent, collision);
    }

    /// <summary>
    /// Schedule-faithful advance resolution: returns the ids of en-route agents that step to their next CP this
    /// tick. An agent is a <i>candidate</i> when its planned arrival tick for the next CP has come (and the cell
    /// ahead is not a parked vehicle it must re-route around). Candidates are then pruned to a set whose post-move
    /// positions are all distinct: a candidate may follow a leader into the cell the leader vacates this same tick
    /// (back-to-back), but never step onto a cell a non-moving vehicle holds, and two candidates never take the
    /// same cell. Resolution iterates to a fixpoint, so revoking a blocked leader correctly blocks its follower in
    /// the next pass. The SIPP schedule is interval-exclusive, so in normal operation every candidate is granted;
    /// the pruning is a defensive guarantee that keeps execution collision-free even if reality diverges from the
    /// plan (a delayed or re-routed vehicle), with block (3) reporting any residual breach.
    /// </summary>
    private static HashSet<string> ResolveScheduleFaithfulAdvances(
        IReadOnlyList<RunAgent> fleet, long tick, IReadOnlySet<string> parkedCells)
    {
        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ag in fleet)
        {
            if (ag is not { EnRoute: true, Done: false })
                continue;
            if (ag.Idx >= ag.CpRoute.Count - 1)
                continue; // at goal: parks, does not step
            if (ag.CpEntryTicks.Count != ag.CpRoute.Count)
                continue; // no schedule attached (e.g. just reset): do not step
            if (tick < ag.CpEntryTicks[ag.Idx + 1])
                continue; // planned wait this tick

            var to = ag.CpRoute[ag.Idx + 1];
            if (parkedCells.Contains(to))
                continue; // parked vehicle ahead: the main loop re-routes this agent rather than advancing it

            target[ag.Id] = to;
        }

        var granted = new HashSet<string>(target.Keys, StringComparer.Ordinal);
        var ordered = fleet
            .Where(a => target.ContainsKey(a.Id))
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        var changed = true;
        while (changed)
        {
            changed = false;

            // Cells held after this tick by anyone NOT advancing (non-movers + revoked movers keep their CP).
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in fleet)
                if (!granted.Contains(a.Id))
                    blocked.Add(a.Position);

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in ordered)
            {
                if (!granted.Contains(a.Id))
                    continue;
                var to = target[a.Id];
                // Revoke if the target is held by a stayer, or already claimed by a higher-priority mover.
                if (blocked.Contains(to) || !claimed.Add(to))
                {
                    granted.Remove(a.Id);
                    changed = true;
                }
            }
        }

        return granted;
    }
}

/// <summary>Raised only on an internal invariant breach (a reserved path that does not run start→goal).</summary>
public sealed class FleetLoopException(string message) : Exception(message);
