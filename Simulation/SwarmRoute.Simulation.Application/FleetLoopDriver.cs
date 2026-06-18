using SwarmRoute.Coordination.Application;
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
public sealed record FleetLoopStats(FleetLoopStatus Status, int Ticks, int Collisions, int Arrived, int Replans);

/// <summary>The full recorded result of a closed-loop run.</summary>
/// <param name="Frames">Tick-by-tick timeline (includes the colliding tick when one occurs).</param>
/// <param name="PerAgentRoute">Per agent: the CP sequence it was actually reserved along.</param>
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
///   <item><description><b>Plan + reserve</b> every idle agent via <see cref="IFleetCoordinationCycle.RunCycleAsync"/>
///     (deterministic priority order). Newly-reserved agents become en route at their start CP.</description></item>
///   <item><description><b>Advance</b> each en-route agent one CP, awaiting <see cref="IFleetCoordinationCycle.ReleaseAsync"/>
///     to hand back the CP+lane it left behind; on arrival it releases all its path resources (no leak).</description></item>
///   <item><description><b>Record</b> a frame for the tick (every agent's CP + motion state).</description></item>
///   <item><description><b>Check safety</b>: if two agents holding right-of-way share a CP on a tick, that is a
///     collision — the run stops with <see cref="FleetLoopStatus.CollisionDetected"/> (the colliding frame is
///     still recorded so a viewer can show exactly where it happened).</description></item>
/// </list>
/// Deterministic given deterministic inputs (no wall-clock / RNG here). This driver is a <b>verifier</b>: it
/// does NOT throw on collision or non-convergence — those are real outcomes it reports via
/// <see cref="FleetLoopResult.Stats"/> so callers (sim API, frontend) can surface them. It throws only on an
/// internal invariant breach (a reserved path that doesn't run start→goal).
/// <para><b>Fidelity note:</b> agents advance one CP per tick (lockstep), independent of the reserved
/// space-time intervals. The reservation table time-separates agents by interval; lockstep execution does not
/// honour that timing, so under density it can surface a collision the time-model considered separated. That is
/// a faithful signal of the v0 gap (placeholder schedule, no execution-time feedback) — to be closed by v1
/// (SIPP + schedule-faithful execution).</para>
/// </summary>
public sealed class FleetLoopDriver
{
    private sealed class RunAgent(FleetAgentSpec spec)
    {
        public string Id { get; } = spec.Id;
        public string Start { get; } = spec.StartSiteId;
        public string Goal { get; } = spec.GoalSiteId;
        public int Priority { get; } = spec.Priority;

        public bool EnRoute { get; set; }
        public bool Done { get; set; }
        public IReadOnlyList<string> CpRoute { get; set; } = Array.Empty<string>();
        public IReadOnlyList<ResourceRef> AllResources { get; set; } = Array.Empty<ResourceRef>();
        public int Idx { get; set; }
        public int Replans { get; set; }

        /// <summary>Where the agent physically sits this tick: current CP if en route/arrived, else its start.</summary>
        public string Position => EnRoute || Done ? CpRoute[Idx] : Start;

        public AgentMotionState State => Done ? AgentMotionState.Arrived
            : EnRoute ? AgentMotionState.Moving
            : AgentMotionState.Waiting;
    }

    /// <summary>
    /// Runs the closed loop, recording a frame per tick, until every agent arrives, a collision is detected, or
    /// the tick budget is exhausted. The fleet is processed in a stable order so a given input always produces
    /// the same timeline.
    /// </summary>
    /// <exception cref="ArgumentNullException">If <paramref name="cycle"/>, <paramref name="graph"/> or <paramref name="agents"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="maxTicks"/> &lt; 1.</exception>
    /// <exception cref="FleetLoopException">Only on an internal invariant breach (reserved path not start→goal).</exception>
    public async Task<FleetLoopResult> RunToCompletionAsync(
        IFleetCoordinationCycle cycle,
        Guid roadmapId,
        RoadmapGraph graph,
        IReadOnlyCollection<FleetAgentSpec> agents,
        int maxTicks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);

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
        var status = FleetLoopStatus.Completed;
        FleetCollisionInfo? collision = null;

        // Record the initial state (tick 0): every agent waiting at its start CP, before any movement. This
        // gives a viewer a frame where the fleet sits at its origins, so playback visibly departs from A (the
        // loop below reserves AND advances an agent within the same tick, so tick 1 is already one CP in).
        frames.Add(new FleetTickFrame(
            0,
            fleet
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => new FleetTickPosition(a.Id, a.Position, a.State))
                .ToList()));

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

            // (1) Plan + reserve every agent that still needs right-of-way.
            var pending = fleet
                .Where(a => !a.Done && !a.EnRoute)
                .Select(a => new AgentGoal(a.Id, a.Start, a.Goal, a.Priority))
                .ToList();

            if (pending.Count > 0)
            {
                var report = await cycle.RunCycleAsync(roadmapId, pending, cancellationToken).ConfigureAwait(false);
                foreach (var r in report.Results)
                {
                    var ag = fleet.Single(a => a.Id == r.AgentId);
                    // Attempts beyond the first are prune-and-replan retries within this cycle.
                    ag.Replans += Math.Max(0, r.Attempts - 1);

                    if (r is { Reserved: true, Path: not null })
                    {
                        ag.EnRoute = true;
                        ag.Idx = 0;
                        ag.CpRoute = r.Path!.Cells
                            .Where(c => c.Resource.Kind == ResourceKind.CP)
                            .Select(c => c.Resource.Id)
                            .ToList();
                        ag.AllResources = r.Path!.Cells.Select(c => c.Resource).Distinct().ToList();

                        if (ag.CpRoute.Count == 0 || ag.CpRoute[0] != ag.Start || ag.CpRoute[^1] != ag.Goal)
                            throw new FleetLoopException(
                                $"Reserved path for '{ag.Id}' does not run {ag.Start}->{ag.Goal} " +
                                $"(got [{string.Join(",", ag.CpRoute)}]).");
                    }
                }
            }

            // (2) Move each en-route agent one CP forward, releasing what it leaves behind.
            foreach (var ag in fleet.Where(a => a is { EnRoute: true, Done: false }).ToList())
            {
                if (ag.Idx < ag.CpRoute.Count - 1)
                {
                    var fromCp = ag.CpRoute[ag.Idx];
                    var toCp = ag.CpRoute[ag.Idx + 1];
                    ag.Idx++;
                    await cycle.ReleaseAsync(ag.Id,
                    [
                        RoadmapGraph.SiteRef(fromCp),
                        new ResourceRef(ResourceKind.Lane, $"{fromCp}-{toCp}"),
                    ], cancellationToken).ConfigureAwait(false);
                }

                if (ag.Idx >= ag.CpRoute.Count - 1)
                {
                    // Arrived: hand back everything still held (goal CP + any remainder) — no leak.
                    ag.Done = true;
                    ag.EnRoute = false;
                    await cycle.ReleaseAsync(ag.Id, ag.AllResources, cancellationToken).ConfigureAwait(false);
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

        var routes = fleet.ToDictionary(
            a => a.Id,
            a => (IReadOnlyList<string>)(a.CpRoute.Count > 0 ? a.CpRoute.ToList() : new List<string> { a.Start }),
            StringComparer.Ordinal);

        var stats = new FleetLoopStats(
            Status: status,
            Ticks: tick,
            Collisions: collisions,
            Arrived: fleet.Count(a => a.Done),
            Replans: fleet.Sum(a => a.Replans));

        return new FleetLoopResult(frames, routes, stats, maxConcurrent, collision);
    }
}

/// <summary>Raised only on an internal invariant breach (a reserved path that does not run start→goal).</summary>
public sealed class FleetLoopException(string message) : Exception(message);
