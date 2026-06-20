using Microsoft.Extensions.Logging;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Liveness.Application.Policy;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Default <see cref="ISimulationService"/>. Builds a grid field, assigns each AGV a distinct start and a
/// distinct goal with a seeded RNG (reproducible), obtains a fresh per-request in-memory engine from
/// <see cref="ISimulationEngineFactory"/> (so concurrent runs never share the singleton
/// <c>ReservationTable</c>), runs the <see cref="FleetLoopDriver"/> to completion, and maps the result to a
/// <see cref="SimulationResultDto"/>.
/// </summary>
public sealed class SimulationService : ISimulationService
{
    /// <summary>Fixed RNG seed used when the request omits one — keeps a seedless request reproducible.</summary>
    public const int DefaultSeed = 1469;

    private readonly GridFieldFactory _gridFactory;
    private readonly FleetLoopDriver _loopDriver;
    private readonly ISimulationEngineFactory _engineFactory;
    private readonly ILogger<SimulationService> _logger;

    public SimulationService(
        GridFieldFactory gridFactory,
        FleetLoopDriver loopDriver,
        ISimulationEngineFactory engineFactory,
        ILogger<SimulationService> logger)
    {
        _gridFactory = gridFactory ?? throw new ArgumentNullException(nameof(gridFactory));
        _loopDriver = loopDriver ?? throw new ArgumentNullException(nameof(loopDriver));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SimulationResultDto> RunAsync(SimulationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        // 1. Build the grid field (graph + render metadata). (v4 ScenarioBench) Carve the scenario's obstacles into
        //    the grid; the free cells (field.Sites) are what the fleet plans over and is assigned starts/goals from.
        var obstacles = ScenarioObstacles.For(request.Scenario, request.Width, request.Height);
        var field = _gridFactory.BuildGrid(request.Width, request.Height, obstacles: obstacles);
        if (field.Sites.Count < 2 * request.AgvCount)
            throw new ArgumentException(
                $"Scenario '{request.Scenario}' leaves {field.Sites.Count} free cell(s), but {2 * request.AgvCount} are " +
                $"needed for {request.AgvCount} AGVs (a distinct start + goal each). Use fewer AGVs or a larger / opener field.",
                nameof(request));

        // 2. Assign per-agent starts + distinct goals.
        //    - Continuation run (request.Starts supplied & valid): keep each AGV where it is and give it a NEW
        //      goal, so a lifelong loop re-plans from the current pose instead of teleporting. Goals are drawn
        //      from cells NOT occupied by a start, so every goal is distinct and != its own start.
        //    - Fresh run: shuffle all cells into two disjoint blocks ([0..k) starts, [k..2k) goals).
        //    Both rely on the validated invariant Width*Height >= 2*AgvCount.
        var rng = new Random(request.Seed ?? DefaultSeed);
        var allCells = field.Sites.Select(s => s.Id).ToList();
        var agentSpecs = new List<FleetAgentSpec>(request.AgvCount);

        var continuationStarts = ValidContinuationStarts(request, field);
        if (continuationStarts is not null)
        {
            var startSet = continuationStarts.ToHashSet(StringComparer.Ordinal);
            var goalPool = Shuffle(allCells.Where(c => !startSet.Contains(c)).ToList(), rng);
            for (var i = 0; i < request.AgvCount; i++)
                agentSpecs.Add(new FleetAgentSpec($"agv-{i + 1}", continuationStarts[i], goalPool[i], Priority: i));
        }
        else
        {
            var shuffled = Shuffle(allCells, rng);
            for (var i = 0; i < request.AgvCount; i++)
                agentSpecs.Add(new FleetAgentSpec($"agv-{i + 1}", shuffled[i], shuffled[request.AgvCount + i], Priority: i));
        }

        // 3-4. Run the closed loop on the (unguided) field.
        var loop = await RunLoopAsync(field, agentSpecs, request, cancellationToken).ConfigureAwait(false);

        // 5. (v4 SwarmRoute Lab) Optional congestion-fed guidance optimization: re-weight the busiest corridors from
        //    this run's measured congestion, then re-run the SAME fleet on the guided field and return the guided run
        //    alongside the baseline metrics for comparison. Off by default → single pass, byte-identical.
        if (!request.OptimizeGuidance)
            return Map(field, agentSpecs, loop);

        var baselineMetrics = SimulationMetricsCalculator.Compute(loop, agentSpecs, PositionById(field));
        var guidance = CongestionGuidanceOptimizer.Derive(baselineMetrics, field);
        var guidedField = _gridFactory.BuildGrid(request.Width, request.Height, guidance, obstacles);
        var guidedLoop = await RunLoopAsync(guidedField, agentSpecs, request, cancellationToken).ConfigureAwait(false);
        return Map(guidedField, agentSpecs, guidedLoop,
            new GuidanceReportDto(baselineMetrics, guidance.AdjustedLaneCount, guidance.MaxMultiplier));
    }

    /// <summary>
    /// Runs one closed-loop pass for a given <paramref name="field"/> + fleet — the engine / executor-mode / policy
    /// wiring — logging a reproducible request on non-convergence. Extracted so a guidance-optimization run can drive
    /// a second pass over a re-weighted field with the same fleet.
    /// </summary>
    private async Task<FleetLoopResult> RunLoopAsync(
        GridField field, IReadOnlyList<FleetAgentSpec> agentSpecs, SimulationRequest request, CancellationToken cancellationToken)
    {
        // A fresh REAL engine for THIS pass (isolation between concurrent runs), planning with the requested planner
        // and rolling-horizon window. SIPPwRT → continuous executor; SIPP → schedule-faithful; Dijkstra → greedy gate.
        await using var engine = _engineFactory.Create(field.Graph, request.Planner, request.HorizonWindowMs, request.PreventDeadlockCycles);

        var executionMode = request.Planner switch
        {
            PlannerKind.Sippwrt => FleetExecutionMode.Continuous,
            PlannerKind.Sipp => FleetExecutionMode.ScheduleFaithful,
            _ => FleetExecutionMode.Greedy,
        };

        var policy = new LivenessPolicy(
            field.Graph,
            new LivenessOptions { JointResolver = request.JointResolver, StepAside = request.StepAside });

        var loop = await _loopDriver
            .RunToCompletionAsync(
                engine.Cycle, engine.RoadmapId, field.Graph, agentSpecs, MaxTicks(request),
                advanceClock: engine.Clock.SetTick,
                executionMode: executionMode,
                policy: policy,
                log: msg => _logger.LogWarning("[standoff] {Detail}", msg),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // On non-convergence, log the EXACT reproducing request (incl. per-agent starts) — paste into
        // POST /api/simulation/run to replay deterministically.
        if (loop.Stats.Status == FleetLoopStatus.DidNotConverge)
        {
            var starts = "[\"" + string.Join("\",\"", agentSpecs.Select(a => a.StartSiteId)) + "\"]";
            var repro = $"{{\"width\":{request.Width},\"height\":{request.Height},\"agvCount\":{request.AgvCount}," +
                        $"\"seed\":{request.Seed?.ToString() ?? "null"},\"planner\":\"{request.Planner}\"," +
                        $"\"horizonWindowMs\":{request.HorizonWindowMs}," +
                        $"\"stepAside\":{(request.StepAside ? "true" : "false")}," +
                        $"\"preventDeadlockCycles\":{(request.PreventDeadlockCycles ? "true" : "false")}," +
                        $"\"jointResolver\":\"{request.JointResolver}\",\"optimizeGuidance\":{(request.OptimizeGuidance ? "true" : "false")},\"starts\":{starts}}}";
            _logger.LogWarning("[did-not-converge] arrived {Arrived}/{Total} in {Ticks} ticks. Repro: {Repro}",
                loop.Stats.Arrived, request.AgvCount, loop.Stats.Ticks, repro);
        }

        return loop;
    }

    private static IReadOnlyDictionary<string, (double X, double Y)> PositionById(GridField field)
        => field.Sites.ToDictionary(s => s.Id, s => (X: (double)s.X, Y: (double)s.Y), StringComparer.Ordinal);

    private static void Validate(SimulationRequest request)
    {
        if (request.Width < 1)
            throw new ArgumentException($"Width must be >= 1 (was {request.Width}).", nameof(request));
        if (request.Height < 1)
            throw new ArgumentException($"Height must be >= 1 (was {request.Height}).", nameof(request));
        if (request.AgvCount < 1)
            throw new ArgumentException($"AgvCount must be >= 1 (was {request.AgvCount}).", nameof(request));

        var capacity = (long)request.Width * request.Height;
        var required = 2L * request.AgvCount;
        if (capacity < required)
            throw new ArgumentException(
                $"Grid is too small for the fleet: Width*Height ({request.Width}x{request.Height} = {capacity}) " +
                $"must be >= 2*AgvCount ({required}) so every AGV gets a distinct start and a distinct goal.",
                nameof(request));

        // CBS returns time-axis paths only a reservation-aware executor can run, so it requires SIPP (discrete CBS,
        // schedule-faithful executor) or SIPPwRT (continuous CCBS, continuous executor). PIBT is the fast greedy
        // discrete tick-stepper; the continuous event loop has no per-tick drive, so PIBT pairs with SIPP only and
        // the continuous executor uses CCBS (JointResolver=Cbs) for joint standoff resolution.
        if (request.JointResolver == JointResolverKind.Cbs
            && request.Planner != PlannerKind.Sipp && request.Planner != PlannerKind.Sippwrt)
            throw new ArgumentException(
                "JointResolver=Cbs requires Planner=Sipp (discrete CBS) or Planner=Sippwrt (continuous CCBS), " +
                "because CBS returns time-axis paths a reservation-aware executor must run.",
                nameof(request));
        if (request.JointResolver == JointResolverKind.Pibt && request.Planner == PlannerKind.Sippwrt)
            throw new ArgumentException(
                "JointResolver=Pibt is not supported with Planner=Sippwrt; the continuous-time executor resolves " +
                "physical standoffs with CCBS (JointResolver=Cbs).",
                nameof(request));
    }

    /// <summary>
    /// Returns the request's continuation starts when usable for this field (exactly one in-graph, distinct cell
    /// per AGV), else <see langword="null"/> to fall back to a fresh random layout. Lets a lifelong loop keep AGVs
    /// at their current poses across runs without the caller having to pre-validate against the grid.
    /// </summary>
    private static IReadOnlyList<string>? ValidContinuationStarts(SimulationRequest request, GridField field)
    {
        var starts = request.Starts;
        if (starts is null || starts.Count != request.AgvCount)
            return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in starts)
            if (string.IsNullOrWhiteSpace(cell) || !field.Graph.HasSite(cell) || !seen.Add(cell))
                return null;

        return starts;
    }

    /// <summary>
    /// A generous upper bound on ticks. Even if the whole-path reservation serialises agents one-at-a-time and
    /// the executor's right-of-way gate makes trailing vehicles wait a tick per congested cell, each agent
    /// traverses at most <c>Width+Height</c> CPs; <c>(Width+Height)*(AgvCount+1)*2</c> plus slack leaves ample
    /// room to converge. A run that still hits this bound is reported as <c>DidNotConverge</c> (a real standoff),
    /// never silently truncated.
    /// </summary>
    private static int MaxTicks(SimulationRequest request)
        => ((request.Width + request.Height) * (request.AgvCount + 1) * 2) + 100;

    private static SimulationResultDto Map(
        GridField field,
        IReadOnlyList<FleetAgentSpec> specs,
        FleetLoopResult loop,
        GuidanceReportDto? guidance = null)
    {
        var positionById = field.Sites.ToDictionary(s => s.Id, s => (X: (double)s.X, Y: (double)s.Y), StringComparer.Ordinal);

        var sites = field.Sites
            .Select(s => new SiteDto(s.Id, s.X, s.Y, s.Type.ToString()))
            .ToList();

        var lanes = new List<LaneDto>();
        foreach (var v in field.Graph.Vertices)
            foreach (var n in field.Graph.Neighbours(v))
                lanes.Add(new LaneDto(RoadmapGraphLaneId(v, n), v, n));

        var fieldDto = new FieldDto(field.Width, field.Height, sites, lanes);

        var agents = specs
            .Select((s, i) =>
            {
                var trail = loop.PerAgentRoute.TryGetValue(s.Id, out var route) ? route : new[] { s.StartSiteId };

                // The route still to be travelled: shortest roadmap path from where the occupied trail ends to
                // the goal. For an arrived AGV the trail already ends at the goal so this is empty; for one that
                // stalled short (a standoff / DidNotConverge) it is the road ahead the frontend draws.
                var lastCp = trail.Count > 0 ? trail[^1] : s.StartSiteId;
                IReadOnlyList<string> remaining = Array.Empty<string>();
                if (!string.Equals(lastCp, s.GoalSiteId, StringComparison.Ordinal))
                {
                    var forward = field.Graph.ShortestPath(lastCp, s.GoalSiteId);
                    if (forward is { Count: > 1 })
                        remaining = forward;
                }

                return new AgentDto(s.Id, s.StartSiteId, s.GoalSiteId, ColorIndex: i, PathSiteIds: trail, RemainingSiteIds: remaining);
            })
            .ToList();

        var frames = loop.Frames
            .Select(f => new FrameDto(
                f.Tick,
                f.Positions
                    .Select(p =>
                    {
                        var pos = positionById.TryGetValue(p.SiteId, out var xy) ? xy : (X: 0d, Y: 0d);
                        return new PositionDto(p.AgentId, p.SiteId, pos.X, pos.Y, p.State.ToString());
                    })
                    .ToList()))
            .ToList();

        var timeline = new TimelineDto(loop.Frames.Count, frames);

        var stats = new StatsDto(
            loop.Stats.Ticks,
            loop.Stats.Collisions,
            loop.Stats.Arrived,
            loop.Stats.Replans,
            loop.Stats.Status.ToString(),
            loop.Collision?.Tick,
            loop.Collision?.AgentIds,
            loop.Stats.FlowtimeTicks);

        // (v3 SIPPwRT) When the run used the continuous executor, attach the real-ms trajectory replay (CP
        // arrivals + render coords). Null for every discrete planner → omitted from the JSON (byte-identical).
        ContinuousTimelineDto? continuous = null;
        if (loop.TimedTrajectories is { Count: > 0 } timed)
        {
            var trajectories = timed
                .Select(t => new AgentTrajectoryDto(
                    t.AgentId,
                    t.Waypoints
                        .Select(w =>
                        {
                            var pos = positionById.TryGetValue(w.SiteId, out var xy) ? xy : (X: 0d, Y: 0d);
                            return new TrajectoryWaypointDto(w.SiteId, pos.X, pos.Y, w.ArriveMs);
                        })
                        .ToList()))
                .ToList();
            var durationMs = trajectories.Max(t => t.Waypoints.Count == 0 ? 0L : t.Waypoints[^1].ArriveMs);
            continuous = new ContinuousTimelineDto(durationMs, trajectories);
        }

        // (v4 SwarmRoute Lab) Quantify the run — throughput, travel-time tail, wait, fairness, reliability, and the
        // per-cell congestion heatmap — deterministically from the same timeline the frontend replays.
        var metrics = SimulationMetricsCalculator.Compute(loop, specs, positionById);

        return new SimulationResultDto(fieldDto, agents, timeline, stats, continuous, metrics, guidance);
    }

    private static string RoadmapGraphLaneId(string from, string to) => $"{from}-{to}";

    /// <summary>Deterministic Fisher–Yates shuffle driven by the seeded RNG (in-place on a copy).</summary>
    private static List<string> Shuffle(List<string> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }
}
