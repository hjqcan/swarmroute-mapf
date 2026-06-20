using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Per-call run context for <see cref="FleetLoopDriver.RunToCompletionAsync"/>: holds ALL the run-scoped state
/// (the fleet, parked cells, frame log, counters, pooled scratch buffers) as instance fields and exposes the loop
/// body that used to live as local functions inside the driver method as instance methods. A fresh instance is
/// constructed for every call, so the state is naturally per-request — keeping <see cref="FleetLoopDriver"/> itself
/// stateless and the loop concurrency-safe. This is a pure reorganisation of the driver body; behaviour is
/// byte-identical to the previous single-method implementation.
/// </summary>
internal sealed partial class FleetLoopRun
{
    // ── Immutable run inputs (captured once at construction) ──────────────────────────────────────────────────
    private readonly IFleetCoordinationCycle _cycle;
    private readonly Guid _roadmapId;
    private readonly RoadmapGraph _graph;
    private readonly int _maxTicks;
    private readonly Action<long>? _advanceClock;
    private readonly FleetExecutionMode _executionMode;
    private readonly ILivenessPolicy _policy;
    private readonly Action<string>? _log;

    // ── (FMS-V1 R2) Station overlay (null ⇒ every FMS branch below is skipped ⇒ byte-identical) ──────────────────
    /// <summary>The FMS station overlay for this run, or <see langword="null"/> for a plain (non-FMS) run. When
    /// null the dock-admission hold, in-service dock occupancy and post-service parking arms are all inert.</summary>
    private readonly FmsScenario? _fms;

    /// <summary>The dock-admission scheduler consulted per tick to admit an AGV from a pre-dock buffer onto a dock
    /// point. Non-null only when <see cref="_fms"/> defines stations.</summary>
    private readonly IStationScheduler? _stationScheduler;

    /// <summary>Reverse index dock-point CP → its <see cref="StationDefinition"/>, built once from <see cref="_fms"/>.
    /// Empty when no FMS scenario is in play. Used to recognise an arrived-at dock point and recover its service
    /// duration / blocking closure.</summary>
    private readonly IReadOnlyDictionary<string, StationDefinition> _stationByDock;

    /// <summary>Reverse index pre-dock buffer CP → the station it buffers, built once from <see cref="_fms"/>. An AGV
    /// physically standing on one of these (and bound for that station's dock) is a dock-admission candidate.</summary>
    private readonly IReadOnlyDictionary<string, StationDefinition> _stationByBuffer;

    /// <summary>(FMS-V2) Optional parking manager used by the clear-to-parking step to choose a serviced vehicle's
    /// resting slot (nearest free <see cref="SiteRole.Parking"/>, falling back to <see cref="SiteRole.Buffer"/>),
    /// avoiding cells already occupied/parked by other vehicles. <see langword="null"/> ⇒ the inline nearest-Parking
    /// pick (<see cref="NearestRole"/>) is used instead, which is byte-identical to the FMS-V1 behaviour.</summary>
    private readonly IParkingManager? _parkingManager;

    /// <summary>(FMS-V3) Optional lifelong-dispatch runtime: the task dispatcher + backlog + horizon + ledger. When
    /// <see langword="null"/> (the default) the run is NOT lifelong — it ends at "all arrived", AGVs are never
    /// re-tasked, and no lifelong metric is recorded — so behaviour is byte-identical. When supplied the loop runs to
    /// the horizon and re-tasks each AGV that clears to parking.</summary>
    private readonly LifelongRuntime? _lifelong;

    /// <summary>True iff a lifelong runtime is in play (every lifelong arm is otherwise skipped).</summary>
    private bool LifelongActive => _lifelong is not null;

    /// <summary>Set once at the top of <see cref="ExecuteAsync"/> (run-scoped, never mutated thereafter), so the
    /// loop methods can read it without threading it through every signature — exactly as the original local
    /// functions captured the method's <c>cancellationToken</c> parameter.</summary>
    private CancellationToken _cancellationToken;

    /// <summary>True iff <see cref="FleetExecutionMode.ScheduleFaithful"/>; cached once (was a local of the same name).</summary>
    private readonly bool _scheduleFaithful;

    // Greedy gate only: after this many consecutive ticks blocked at the gate, an en-route agent is logged as a
    // physical standoff (head-on swap / blocking chain). Pure executor telemetry — the greedy advance outcome is
    // resolved sequentially in the gate, so this stays mechanism (the policy owns the schedule-faithful yields).
    private const int StandoffLogThreshold = 12;

    /// <summary>Joint-resolver (PIBT) per-episode budget, bounded generously by graph size — the value the executor
    /// uses to initialise the episode counter it owns when applying an EnterJointResolver directive. The policy
    /// computes the same bound to decide the budget-elapsed exit; both are the deterministic 2*max(1,V).</summary>
    private readonly int _pibtEpisodeMaxTicks;

    // ── Mutable run state ─────────────────────────────────────────────────────────────────────────────────────
    private readonly List<RunAgent> _fleet;
    private readonly List<FleetTickFrame> _frames = new();
    private int _tick;
    private int _collisions;
    private int _maxConcurrent;
    private int _flowtimeTicks;
    private FleetLoopStatus _status = FleetLoopStatus.Completed;
    private FleetCollisionInfo? _collision;

    // Control points occupied by parked (arrived) vehicles. Fed to the planner each cycle as obstacles so the
    // rest of the fleet routes AROUND finished agents instead of stalling behind them at the executor gate.
    private readonly HashSet<string> _parkedCells = new(StringComparer.Ordinal);

    // ── RUN-SCOPED scratch buffers (allocation pooling) ────────────────────────────────────────────────────
    // These are reused across every tick/event by Clear()+refill instead of being re-allocated each iteration.
    // They live on this per-run instance (constructed per call), so there is no shared mutable state across the
    // DI-injected driver's concurrent requests. Each is read-then-discarded within the tick that fills it (the
    // snapshot/dictionary is consumed synchronously and not retained), so reuse is safe.
    private readonly Dictionary<string, string> _posBefore = new(StringComparer.Ordinal);     // pose at tick-start (edge-swap net)
    private readonly Dictionary<string, string> _occupantNow = new(StringComparer.Ordinal);   // (continuous) occupancy net; built below
    private readonly Dictionary<string, RunAgent> _occupantNowAgents = new(StringComparer.Ordinal); // (discrete) tick-start CP→agent gate map
    private readonly HashSet<string> _claimedNext = new(StringComparer.Ordinal);              // cells occupied after this tick
    private readonly Dictionary<string, string> _yieldReason = new(StringComparer.Ordinal);   // schedule-faithful per-agent yields
    // One reused per-phase agent-view list. policy.Evaluate is a pure function of the snapshot and never retains
    // the snapshot or its Agents list (confirmed in LivenessPolicy: each phase copies out via ToDictionary/ToList
    // and returns a fresh directive list), so a single buffer refilled per phase is safe.
    private readonly List<AgentLivenessView> _agentViews;

    public FleetLoopRun(
        IFleetCoordinationCycle cycle,
        Guid roadmapId,
        RoadmapGraph graph,
        IReadOnlyCollection<FleetAgentSpec> agents,
        int maxTicks,
        Action<long>? advanceClock,
        FleetExecutionMode executionMode,
        ILivenessPolicy policy,
        Action<string>? log,
        FmsScenario? fms = null,
        IStationScheduler? stationScheduler = null,
        IParkingManager? parkingManager = null,
        LifelongRuntime? lifelong = null)
    {
        _cycle = cycle;
        _roadmapId = roadmapId;
        _graph = graph;
        _maxTicks = maxTicks;
        _advanceClock = advanceClock;
        _executionMode = executionMode;
        _policy = policy;
        _log = log;
        _fms = fms;
        _stationScheduler = stationScheduler;
        _parkingManager = parkingManager;
        _lifelong = lifelong;

        _scheduleFaithful = executionMode == FleetExecutionMode.ScheduleFaithful;
        _pibtEpisodeMaxTicks = 2 * Math.Max(1, graph.VertexCount);

        // (FMS-V1 R2) Build the dock/buffer reverse indexes once. Both stay empty when no FMS scenario is in play, so
        // every station lookup below short-circuits and the run is byte-identical to a non-FMS run.
        var byDock = new Dictionary<string, StationDefinition>(StringComparer.Ordinal);
        var byBuffer = new Dictionary<string, StationDefinition>(StringComparer.Ordinal);
        if (_fms is not null)
            foreach (var station in _fms.Stations)
            {
                byDock.TryAdd(station.DockPoint, station);
                foreach (var buffer in station.PreDockBuffers)
                    byBuffer.TryAdd(buffer, station);
            }
        _stationByDock = byDock;
        _stationByBuffer = byBuffer;

        // Stable fleet order (priority then ordinal id) so timeline + routes are reproducible.
        _fleet = agents
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new RunAgent(a))
            .ToList();

        // (FMS-V1 R2) An AGV whose goal is a station dock point starts the mission heading for the pre-dock buffer:
        // its effective goal is the buffer (so it routes there for admission), MissionState=MovingToPreDockBuffer.
        // The per-tick admission arm then flips its goal to the dock once granted. No-op without an FMS run.
        if (_fms is not null)
            foreach (var ag in _fleet)
                if (_stationByDock.TryGetValue(ag.Goal, out var station))
                {
                    ag.StationId = station.StationId;
                    ag.MissionState = AgvMissionState.MovingToPreDockBuffer;
                    FmsInitStationGoal(ag, station);
                }

        // (FMS-V3) In a lifelong run every AGV starts idle-parked at its start so the dispatcher assigns even the first
        // task uniformly. No-op without a lifelong runtime, so the per-agent setup above stands for a one-shot run.
        LifelongInitFleet();

        _agentViews = new List<AgentLivenessView>(_fleet.Count);
    }

    /// <summary>
    /// Runs the closed loop, recording a frame per tick, until every agent arrives or the tick budget is
    /// exhausted, then assembles the <see cref="FleetLoopResult"/>. This is the body that previously lived inline
    /// in <see cref="FleetLoopDriver.RunToCompletionAsync"/>; behaviour is byte-identical.
    /// </summary>
    public async Task<FleetLoopResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;

        // Record the initial state (tick 0): every agent waiting at its start CP, before any movement. This
        // gives a viewer a frame where the fleet sits at its origins, so playback visibly departs from A (the
        // loop below reserves AND advances an agent within the same tick, so tick 1 is already one CP in).
        _frames.Add(new FleetTickFrame(
            0,
            _fleet
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => new FleetTickPosition(a.Id, a.Position, a.State))
                .ToList()));

        if (_executionMode == FleetExecutionMode.Continuous)
            await ExecuteContinuousAsync().ConfigureAwait(false);
        else
            await ExecuteDiscreteAsync().ConfigureAwait(false);

        return BuildResult();
    }

    /// <summary>Assembles the recorded run into a <see cref="FleetLoopResult"/> (per-agent trail, optional timed
    /// trajectories, aggregate stats) — the common tail both execution modes fall through to.</summary>
    private FleetLoopResult BuildResult()
    {
        // Per-agent route = the actual walked trail (A → … → B), reconstructed from the recorded frames by
        // collapsing consecutive same-CP positions. Using the agent's FINAL reserved leg (a.CpRoute) instead
        // would drop the earlier segments of any agent that re-planned (reroute-around-parked) or was
        // redirected (deadlock yield) mid-journey, leaving the canvas polyline detached from its "A" marker —
        // the "some AGVs have no line" artefact. The trail always begins at the start (frame 0) and ends at the
        // agent's last position (its goal when arrived).
        var routes = _fleet.ToDictionary(
            a => a.Id,
            a =>
            {
                var trail = new List<string>();
                foreach (var frame in _frames)
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

        // (v3 SIPPwRT) Continuous-time trajectory: each agent's CP arrival schedule in real fleet-clock ms,
        // derived from the SAME event frames the discrete trail uses (each frame's Tick is the event's ms in this
        // mode), seeded with the start at t=0. Null under the discrete modes — the result stays byte-identical.
        IReadOnlyList<FleetTimedTrajectory>? timedTrajectories = null;
        if (_executionMode == FleetExecutionMode.Continuous)
        {
            timedTrajectories = _fleet
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a =>
                {
                    // Purely frame-derived: frame[0] is the tick-0 snapshot at every agent's ORIGINAL start, so the
                    // first waypoint is (start, 0). NB do NOT seed from a.Start — a reroute mutates it to the agent's
                    // current pose, which would corrupt both the start cell and the t=0 timestamp.
                    var waypoints = new List<FleetTimedWaypoint>();
                    foreach (var frame in _frames)
                    {
                        var sid = frame.Positions.First(p => p.AgentId == a.Id).SiteId;
                        if (waypoints.Count == 0 || !string.Equals(waypoints[^1].SiteId, sid, StringComparison.Ordinal))
                            waypoints.Add(new FleetTimedWaypoint(sid, frame.Tick));
                    }
                    return new FleetTimedTrajectory(a.Id, waypoints);
                })
                .ToList();
        }

        var stats = new FleetLoopStats(
            Status: _status,
            Ticks: _tick,
            Collisions: _collisions,
            Arrived: _fleet.Count(a => a.Done),
            Replans: _fleet.Sum(a => a.Replans),
            FlowtimeTicks: _flowtimeTicks);

        return new FleetLoopResult(
            _frames, routes, stats, _maxConcurrent, _collision, timedTrajectories, BuildLifelongMetrics());
    }

    // ── Shared helpers (were local functions of RunToCompletionAsync) ─────────────────────────────────────────

    /// <summary>
    /// Drops an agent's in-flight reservation and resets it to "pending at its current CP" so the next cycle
    /// re-plans it (toward its EffectiveGoal) from where it physically stands. Used to redirect a deadlock
    /// victim onto its avoidance route and to restore it to its real goal once recovered.
    /// </summary>
    private async Task YieldAndReplanFromCurrentAsync(RunAgent ag)
    {
        var here = ag.Position;
        if (ag.AllResources.Count > 0)
            await _cycle.ReleaseAsync(ag.Id, ag.AllResources, _cancellationToken).ConfigureAwait(false);
        ag.EnRoute = false;
        ag.Start = here;
        ag.CpRoute = new[] { here };
        ag.CpEntryTicks = Array.Empty<long>();
        ag.Idx = 0;
        ag.AllResources = Array.Empty<ResourceRef>();
        ag.FrontierIsGoal = true; // re-plan toward EffectiveGoal fresh next cycle (block 1 re-derives this).
    }

    /// <summary>
    /// The raw next CP of an en-route agent's committed route (regardless of candidacy), or null when not en
    /// route / already at the route end. Used by the head-on / parked-ahead / joint-resolver-blocked checks and
    /// (with the effective goal) by the policy to derive the candidate-gated intent for cluster detection.
    /// </summary>
    private static string? EnRouteNextCellFor(RunAgent a) =>
        a is { EnRoute: true, Done: false } && a.Idx + 1 < a.CpRoute.Count ? a.CpRoute[a.Idx + 1] : null;

    /// <summary>
    /// Builds the policy's per-agent view for a given phase. The Advance-phase-only fields (scheduledAdvance,
    /// nextCellIsParked, …) default off in the earlier phases (BeforePlanning / ClusterFormation never read them).
    /// </summary>
    private static AgentLivenessView ViewOf(RunAgent a, bool atRouteEnd = false, bool nextCellIsParked = false,
        bool scheduledToAdvance = false, bool scheduledToMoveThisTick = false) =>
        new(
            a.Id, a.Position, a.Goal, a.EffectiveGoal, a.Priority,
            EnRouteNextCellFor(a),
            a.EnRoute, a.Done, a.PibtActive, a.RedirectTarget is not null, a.HoldingAtAvoidSite,
            a.BlockedTicks, a.StuckTicks, a.YieldTicksRemaining, a.PibtHeldTicks, a.PibtEpisodeTicksLeft,
            atRouteEnd, nextCellIsParked, scheduledToAdvance, scheduledToMoveThisTick, a.Mobility);

    /// <summary>
    /// Puts an agent en route on a freshly-reserved path: the CP route plus each CP's planned arrival tick (the
    /// axis the schedule-faithful executor advances on). Shared by block (1)'s plan+reserve and the CBS cluster
    /// solve (block 2-CBS), since both hand the executor a reserved SpaceTimePath in the same shape.
    /// </summary>
    private void SetEnRouteFromPath(RunAgent ag, SpaceTimePath path)
    {
        ag.EnRoute = true;
        ag.Idx = 0;
        ag.StuckTicks = 0;
        var cpCells = path.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).ToList();
        ag.CpRoute = cpCells.Select(c => c.Resource.Id).ToList();
        ag.CpEntryTicks = cpCells.Select(c => c.Interval.StartMs).ToList();
        ag.AllResources = path.Cells.Select(c => c.Resource).Distinct().ToList();
        if (ag.CpRoute.Count == 0 || ag.CpRoute[0] != ag.Start)
            throw new FleetLoopException(
                $"Reserved path for '{ag.Id}' does not start at {ag.Start} (got [{string.Join(",", ag.CpRoute)}]).");
        ag.FrontierIsGoal = string.Equals(ag.CpRoute[^1], ag.EffectiveGoal, StringComparison.Ordinal);
    }

    private IReadOnlySet<ResourceRef>? PhysicalBlockersExcept(IReadOnlySet<string>? excludedCells = null)
    {
        var cells = _parkedCells
            .Concat(_fleet.Where(a => !a.EnRoute && !a.Done).Select(a => a.Position))
            .Where(c => excludedCells is null || !excludedCells.Contains(c))
            .ToHashSet(StringComparer.Ordinal);
        return cells.Count == 0 ? null : cells.Select(RoadmapGraph.SiteRef).ToHashSet();
    }

    private sealed class RunAgent(FleetAgentSpec spec)
    {
        public string Id { get; } = spec.Id;
        /// <summary>The current planning origin — the original start, or the CP it was re-routed from.</summary>
        public string Start { get; set; } = spec.StartSiteId;
        /// <summary>The agent's current goal CP. Get-only in a one-shot run (set once from the spec). In a (FMS-V3)
        /// lifelong run the re-task arm advances it to each successive transport task's goal via
        /// <see cref="RetaskTo"/>; that is the ONLY mutation path, and it never fires without a lifelong runtime, so a
        /// non-lifelong run is byte-identical (the goal is the immutable spec goal exactly as before).</summary>
        public string Goal { get; private set; } = spec.GoalSiteId;
        public int Priority { get; } = spec.Priority;

        /// <summary>(FMS) How freely the liveness layer may relocate this vehicle when resolving contention. The
        /// default <see cref="MobilityClass.Movable"/> keeps behaviour byte-identical; once a vehicle is docked and
        /// <see cref="MobilityClass.ImmovableUntilServiceComplete"/> it is surfaced to the policy as a hard obstacle
        /// (never relocated / PIBT-driven / CBS-driven / yielded). Set by the (next-phase) service-admission arm.</summary>
        public MobilityClass Mobility { get; set; } = MobilityClass.Movable;

        /// <summary>(FMS) The agent's dispatch mission state. <see cref="AgvMissionState.Idle"/> by default so the
        /// pre-FMS run is unchanged; the (next-phase) arrival/dock arm advances it through the service lifecycle.</summary>
        public AgvMissionState MissionState { get; set; } = AgvMissionState.Idle;

        /// <summary>(FMS) Ticks of docked service remaining while <see cref="AgvMissionState.InService"/>; counted
        /// down by the service arm. Zero by default — no service in progress.</summary>
        public int ServiceTicksRemaining { get; set; }

        /// <summary>(FMS-V1 R2) The id of the station this AGV is bound to (its goal is the station's dock point), or
        /// <see langword="null"/> when it is a plain transit agent. Set once at construction; used by the per-tick
        /// dock-admission arm to recover the station and to release its service window on completion.</summary>
        public string? StationId { get; set; }

        public bool EnRoute { get; set; }
        public bool Done { get; set; }

        /// <summary>True when the current committed route ends at the agent's <see cref="EffectiveGoal"/> (a full
        /// plan). False when it is a rolling-horizon (RHCR) window truncated short of the goal — then the arrival
        /// branch re-plans the next window from the frontier instead of parking. Re-derived whenever the agent
        /// becomes en route, and only read while en route, so the default is irrelevant after the first plan.</summary>
        public bool FrontierIsGoal { get; set; } = true;

        public IReadOnlyList<string> CpRoute { get; set; } = Array.Empty<string>();

        /// <summary>Parallel to <see cref="CpRoute"/>: the planned fleet-clock tick at which the agent is
        /// scheduled to arrive at each CP (the CP cell's interval start). Consumed only in
        /// <see cref="FleetExecutionMode.ScheduleFaithful"/>; left empty under greedy execution and after a
        /// reset (the agent is then not en route, so the executor never reads it until it re-plans).</summary>
        public IReadOnlyList<long> CpEntryTicks { get; set; } = Array.Empty<long>();

        public IReadOnlyList<ResourceRef> AllResources { get; set; } = Array.Empty<ResourceRef>();
        public int Idx { get; set; }
        public int Replans { get; set; }

        /// <summary>Consecutive ticks this WAITING agent has failed to obtain a reserved route (reset the moment
        /// it becomes en route). A long streak means it is walled out of its goal — typically by parked vehicles
        /// sitting on the only approach — and triggers the parked-gatekeeper step-aside.</summary>
        public int StuckTicks { get; set; }

        /// <summary>Ticks remaining for which this (un-parked) gatekeeper holds aside to let a walled-out agent
        /// pass; on reaching zero it recovers (re-plans back to its own goal). Zero when not yielding.</summary>
        public int YieldTicksRemaining { get; set; }

        /// <summary>Consecutive ticks this en-route agent has failed to advance at the right-of-way gate
        /// (reset to 0 the moment it moves). A high streak is a physical standoff — a head-on swap or a
        /// circular blocking chain the lockstep executor can't resolve — which the diagnostic log surfaces.</summary>
        public int BlockedTicks { get; set; }

        /// <summary>True while this agent is being driven by zone-local PIBT (v3): it has released its (stalled)
        /// reservation and is moved one hop per tick by the joint resolver until it reaches its goal or the episode
        /// budget elapses. PIBT agents are excluded from normal planning/advancing and handled in block (2).</summary>
        public bool PibtActive { get; set; }

        /// <summary>Consecutive ticks this PIBT agent has been forced to hold inside the current episode (the
        /// anti-livelock key that promotes the longest-waiting agent in the resolver's processing order).</summary>
        public int PibtHeldTicks { get; set; }

        /// <summary>Ticks of PIBT driving left in the current episode before the agent disbands back to SIPP.</summary>
        public int PibtEpisodeTicksLeft { get; set; }

        /// <summary>When set, the agent is a relocated parked gatekeeper stepping aside to an avoidance site
        /// instead of its goal, until its yield window elapses (see the parked-gatekeeper step-aside in the
        /// BeforePlanning policy phase). Drives <see cref="EffectiveGoal"/> and <see cref="HoldingAtAvoidSite"/>.</summary>
        public string? RedirectTarget { get; set; }

        /// <summary>(FMS-V1 R2) A station-lifecycle goal override that takes precedence over both the gatekeeper
        /// redirect and the real goal: the pre-dock buffer while heading there for admission, or the parking slot
        /// after service. <see langword="null"/> for a transit agent (or once admitted to the dock), so a non-FMS run
        /// is byte-identical (<see cref="EffectiveGoal"/> falls through to the existing redirect/goal logic).</summary>
        public string? FmsGoalOverride { get; set; }

        /// <summary>The goal to plan toward this tick: the FMS station-lifecycle override (buffer / parking) if any,
        /// else the avoidance site while stepping aside, else the real goal.</summary>
        public string EffectiveGoal => FmsGoalOverride ?? RedirectTarget ?? Goal;

        /// <summary>(FMS-V1 R2) True while docked and performing the station service — a hard immovable obstacle that
        /// holds the dock lease, is never re-planned, and is counted down by the service arm. False by default.</summary>
        public bool InService => MissionState == AgvMissionState.InService;

        /// <summary>True while the agent is yielding to an avoidance site and physically sitting on it. Such an
        /// agent is not re-planned and is not "done"; the gatekeeper yield-window countdown restores its goal.</summary>
        public bool HoldingAtAvoidSite => RedirectTarget is not null && !EnRoute && !Done && Start == RedirectTarget;

        /// <summary>Where the agent physically sits this tick: current CP if en route/arrived, else its origin.</summary>
        public string Position => EnRoute || Done ? CpRoute[Idx] : Start;

        public AgentMotionState State => Done ? AgentMotionState.Arrived
            : EnRoute ? AgentMotionState.Moving
            : AgentMotionState.Waiting;

        /// <summary>(FMS-V3 lifelong) Re-point a finished, parked AGV at a fresh transport-task goal and wake it up to
        /// go again: clears the parked/done state, plants the new goal + station binding, and resets the navigation
        /// fields to "pending at the current cell" so the next cycle plans toward the new goal. The caller (the lifelong
        /// re-task arm) is responsible for the station-lifecycle goal override (buffer/dock) and the parked-cell set.</summary>
        public void RetaskTo(string goal, string? stationId, AgvMissionState missionState)
        {
            var here = Position;
            Goal = goal;
            StationId = stationId;
            MissionState = missionState;
            Done = false;
            EnRoute = false;
            PibtActive = false;
            Mobility = MobilityClass.Movable;
            RedirectTarget = null;
            FmsGoalOverride = null;
            Start = here;
            CpRoute = new[] { here };
            CpEntryTicks = Array.Empty<long>();
            Idx = 0;
            AllResources = Array.Empty<ResourceRef>();
            ServiceTicksRemaining = 0;
            StuckTicks = 0;
            BlockedTicks = 0;
            YieldTicksRemaining = 0;
            FrontierIsGoal = true;
        }
    }
}
