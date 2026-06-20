using Microsoft.Extensions.Logging;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain.Endpoints;
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

        // (FMS-V1 R2) Opt-in station demo: ignore the random grid layout and run a fixed FMS scenario (e.g. M-F1)
        // with the station executor honouring dock admission / in-service occupancy / post-service parking. Off
        // (null) ⇒ falls through to the normal run below ⇒ byte-identical.
        if (request.StationScenario is { } stationScenario)
            return await RunStationScenarioAsync(stationScenario, request, cancellationToken).ConfigureAwait(false);

        // (FMS-V3) Opt-in lifelong dispatch: a continuous-operation warehouse over a larger, sparser grid driven by the
        // runtime task dispatcher, bounded by a horizon. Requires BOTH ScenarioMode.LifelongDispatch AND a positive
        // LifelongHorizonTicks; with the mode set but no horizon it falls back to a one-shot WarehouseWellFormed pass
        // (so selecting the mode alone is byte-identical to that scenario). Default mode (RandomStress) skips this.
        if (request.ScenarioMode == ScenarioMode.LifelongDispatch && request.LifelongHorizonTicks is > 0)
            return await RunLifelongDispatchAsync(request, cancellationToken).ConfigureAwait(false);

        // (FMS-V2) Opt-in well-formed warehouse: carve a parking/workstation endpoint ring around a connected transit
        // core, draw task goals from workstations, and clear serviced AGVs to real parking. ScenarioMode defaults to
        // RandomStress => this is skipped => byte-identical. (LifelongDispatch with no horizon also runs the one-shot
        // warehouse here — the mode-without-horizon fallback above falls through to this branch.)
        if (request.ScenarioMode is ScenarioMode.WarehouseWellFormed or ScenarioMode.LifelongDispatch)
            return await RunWarehouseWellFormedAsync(request, cancellationToken).ConfigureAwait(false);

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

        // Per-agent starts + a pool of distinct goals (one per agent); the dispatcher then MATCHES goals to AGVs by the
        // configured policy. Random keeps the shuffled pairing (byte-identical); Nearest/Optimal cut total travel.
        IReadOnlyList<string> starts;
        IReadOnlyList<string> goalPool;
        var continuationStarts = ValidContinuationStarts(request, field);
        if (continuationStarts is not null)
        {
            var startSet = continuationStarts.ToHashSet(StringComparer.Ordinal);
            starts = continuationStarts;
            goalPool = Shuffle(allCells.Where(c => !startSet.Contains(c)).ToList(), rng).Take(request.AgvCount).ToList();
        }
        else
        {
            var shuffled = Shuffle(allCells, rng);
            starts = shuffled.Take(request.AgvCount).ToList();
            goalPool = shuffled.Skip(request.AgvCount).Take(request.AgvCount).ToList();
        }

        var goals = TaskDispatcher.Assign(starts, goalPool, field.Graph, request.Assignment);
        for (var i = 0; i < request.AgvCount; i++)
            agentSpecs.Add(new FleetAgentSpec($"agv-{i + 1}", starts[i], goals[i], Priority: i));

        // 3-4. Run the closed loop on the (unguided) field.
        var loop = await RunLoopAsync(field, agentSpecs, request, cancellationToken).ConfigureAwait(false);

        // (v4 SwarmRoute Lab — Order/Dispatch context) Opt-in lifelong dispatch simulation over the SAME field + fleet:
        // a stream of transport orders releasing over time, queued and continuously assigned by the chosen policy (with
        // stations, battery and SLA). The dispatch geography is drawn from the REAL Dispatch domain — the
        // WellFormedEndpointGenerator's FMS endpoint partition (orders between workstations, charging at charger/parking
        // endpoints), the same policy the warehouse scenarios use. A self-contained operations-layer analysis (it reads
        // the endpoint model, not the live DispatcherService); off by default → null → omitted.
        var orderDispatch = request.SimulateOrders
            ? OrderDispatchSimulator.Run(
                field, starts, request.Seed ?? DefaultSeed, request.Assignment,
                OrderDispatchSimulator.Options.Derive(field, request.AgvCount),
                new WellFormedEndpointGenerator().BuildEndpoints(field.Graph, request.AgvCount, request.Seed ?? DefaultSeed))
            : null;

        // 5. (v4 SwarmRoute Lab) Optional congestion-fed guidance optimization: re-weight the busiest corridors from
        //    this run's measured congestion, then re-run the SAME fleet on the guided field and return the guided run
        //    alongside the baseline metrics for comparison. Off by default → single pass, byte-identical.
        if (!request.OptimizeGuidance)
            return Map(field, agentSpecs, loop, emitTrace: request.EmitTrace, orderDispatch: orderDispatch);

        var baselineMetrics = SimulationMetricsCalculator.Compute(loop, agentSpecs, PositionById(field));
        var guidance = CongestionGuidanceOptimizer.Derive(baselineMetrics, field);
        var guidedField = _gridFactory.BuildGrid(request.Width, request.Height, guidance, obstacles);
        var guidedLoop = await RunLoopAsync(guidedField, agentSpecs, request, cancellationToken).ConfigureAwait(false);
        return Map(guidedField, agentSpecs, guidedLoop,
            new GuidanceReportDto(baselineMetrics, guidance.AdjustedLaneCount, guidance.MaxMultiplier),
            emitTrace: request.EmitTrace, orderDispatch: orderDispatch);
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

    /// <summary>
    /// (FMS-V1 R2) Runs an opt-in fixed FMS station demo end-to-end: builds the scenario (grid + fleet + station
    /// overlay + catalog), creates a per-request engine WITH the catalog so the dock-admission scheduler shares the
    /// run's reservation system, and drives the SIPP schedule-faithful discrete executor with the FMS overlay so it
    /// honours stations (buffer admission hold, in-service dock occupancy, post-service relocation to parking). The
    /// only built-in layout is <see cref="StationScenarioKind.MF1"/>.
    /// </summary>
    private async Task<SimulationResultDto> RunStationScenarioAsync(
        StationScenarioKind kind, SimulationRequest request, CancellationToken cancellationToken)
    {
        var scenario = kind switch
        {
            StationScenarioKind.MF1 => MF1ScenarioBuilder.Build(_gridFactory, transitAgvCount: Math.Max(2, request.AgvCount)),
            _ => throw new ArgumentOutOfRangeException(nameof(request),
                $"Unknown station scenario '{kind}'."),
        };

        // The engine is created WITH the catalog, so it wires the dock-admission scheduler over this run's reservation
        // table; the executor consults it per tick. The station demo runs the SIPP planner + schedule-faithful
        // executor: SIPP's timeline is on the unified HopMs==1 tick axis (one hop = one tick), so the dock-admission
        // service window — built in raw ticks ([currentTick, currentTick+serviceMs)) — shares that exact axis and
        // correctly conflicts with transit AGVs' control-point leases on the station's blocking closure. (Dijkstra
        // scales intervals by ×1000 edge weight, off the tick axis, so its leases would not line up with the window.)
        await using var engine = _engineFactory.Create(
            scenario.Field.Graph, PlannerKind.Sipp, request.HorizonWindowMs, request.PreventDeadlockCycles,
            stationCatalog: scenario.Catalog);

        var policy = new LivenessPolicy(
            scenario.Field.Graph,
            new LivenessOptions { JointResolver = JointResolverKind.None, StepAside = request.StepAside });

        // Tick budget: corridor travel for every AGV plus the full service window plus the dock AGV's wait-then-dock-
        // then-park trip — generous so a converging run is never truncated (non-convergence is reported, not hidden).
        var maxTicks = ((scenario.Field.Width + scenario.Field.Height) * (scenario.Agents.Count + 2) * 2)
            + (int)Math.Min(int.MaxValue / 2, MF1ScenarioBuilder.ServiceDurationMs) + 200;

        var loop = await _loopDriver
            .RunToCompletionAsync(
                engine.Cycle, engine.RoadmapId, scenario.Field.Graph, scenario.Agents, maxTicks,
                advanceClock: engine.Clock.SetTick,
                executionMode: FleetExecutionMode.ScheduleFaithful,
                policy: policy,
                log: msg => _logger.LogInformation("[fms] {Detail}", msg),
                fms: scenario.Fms,
                stationScheduler: engine.StationScheduler,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return Map(scenario.Field, scenario.Agents, loop, emitTrace: request.EmitTrace, fms: scenario.Fms);
    }

    /// <summary>
    /// (FMS-V2) Runs an opt-in well-formed warehouse scenario end-to-end. <see cref="WarehouseScenarioBuilder"/>
    /// carves a parking/workstation endpoint partition out of the request's grid (keeping the transit core connected
    /// with egress), draws each station AGV's task goal from a workstation and the rest from safe endpoints/cells, and
    /// the station executor drives the service-then-clear-to-parking lifecycle with an <see cref="IParkingManager"/>
    /// picking each serviced AGV's resting slot. Uses SIPP + the schedule-faithful executor (the M-F1 finding: the
    /// service window lives on the unified HopMs==1 tick axis SIPP plans on). On non-convergence the result carries the
    /// per-agent <see cref="NonConvergenceReason"/> diagnostics.
    /// </summary>
    private async Task<SimulationResultDto> RunWarehouseWellFormedAsync(
        SimulationRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var scenario = WarehouseScenarioBuilder.Build(
            _gridFactory, request.Width, request.Height, request.AgvCount, request.Seed ?? DefaultSeed);

        // The engine is created WITH the catalog so the dock-admission scheduler shares this run's reservation table.
        // SIPP planner + schedule-faithful executor: the service window is built in raw ticks on the same HopMs==1 axis
        // SIPP plans on, so it conflicts correctly with transit leases (Dijkstra's x1000 weight is off that axis).
        await using var engine = _engineFactory.Create(
            scenario.Field.Graph, PlannerKind.Sipp, request.HorizonWindowMs, request.PreventDeadlockCycles,
            stationCatalog: scenario.Catalog);

        var policy = new LivenessPolicy(
            scenario.Field.Graph,
            new LivenessOptions { JointResolver = request.JointResolver, StepAside = request.StepAside });

        var loop = await _loopDriver
            .RunToCompletionAsync(
                engine.Cycle, engine.RoadmapId, scenario.Field.Graph, scenario.Agents, MaxTicks(request),
                advanceClock: engine.Clock.SetTick,
                executionMode: FleetExecutionMode.ScheduleFaithful,
                policy: policy,
                log: msg => _logger.LogInformation("[warehouse] {Detail}", msg),
                fms: scenario.Fms,
                stationScheduler: engine.StationScheduler,
                parkingManager: new ParkingManager(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return Map(scenario.Field, scenario.Agents, loop, emitTrace: request.EmitTrace, fms: scenario.Fms);
    }

    /// <summary>The number of transport tasks a lifelong run generates: roughly one task per AGV per
    /// <see cref="TaskTicksPerJob"/> ticks of horizon, so the backlog grows and drains realistically. Clamped to at
    /// least one per AGV.</summary>
    private static int LifelongTaskCount(SimulationRequest request, long horizon)
        => Math.Max(request.AgvCount, (int)Math.Min(int.MaxValue, request.AgvCount * Math.Max(1, horizon / TaskTicksPerJob)));

    /// <summary>Nominal ticks one transport job (drive→service→clear→re-task) takes on a sparse grid — the task-count
    /// pacing constant, so a horizon releases roughly as many tasks as the fleet can serve (a backlog that grows then
    /// drains, rather than an unservable flood).</summary>
    private const long TaskTicksPerJob = 18;

    /// <summary>
    /// (FMS-V3) Runs an opt-in <b>lifelong dispatch</b> scenario end-to-end. <see cref="LifelongScenarioBuilder"/> carves a
    /// large safe workstation/parking endpoint set out of the request's grid and a STREAM of transport tasks; the runtime
    /// <see cref="ITaskDispatcher"/> hands each idle AGV its next task, and the executor drives the service-then-clear-to-
    /// parking-then-re-task loop to the horizon. Uses SIPP + the schedule-faithful executor (the M-F1 finding) and an
    /// <see cref="IParkingManager"/> for resting slots. The dock-admission scheduler is wired with cost-based admission
    /// when the request opts in. The result carries the additive <see cref="LifelongMetricsDto"/>.
    /// </summary>
    private async Task<SimulationResultDto> RunLifelongDispatchAsync(
        SimulationRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var horizon = request.LifelongHorizonTicks!.Value;
        var taskCount = LifelongTaskCount(request, horizon);
        var scenario = LifelongScenarioBuilder.Build(
            _gridFactory, request.Width, request.Height, request.AgvCount, taskCount, horizon,
            request.Seed ?? DefaultSeed);

        // The engine is created WITH the catalog so the dock-admission scheduler shares this run's reservation table,
        // optionally with cost-based admission (a long-service blocking station then weighs let-pass vs go-first). SIPP
        // planner + schedule-faithful executor — the service window lives on the same HopMs==1 tick axis SIPP plans on.
        await using var engine = _engineFactory.Create(
            scenario.Field.Graph, PlannerKind.Sipp, request.HorizonWindowMs, request.PreventDeadlockCycles,
            stationCatalog: scenario.Catalog,
            costBasedAdmission: request.LifelongCostBasedAdmission,
            fleetPlan: scenario.FleetPlan);

        // A lifelong run accumulates contention forever (re-tasked AGVs converge on docks, parked AGVs cluster), so it
        // NEEDS the executor's standoff-recovery levers on — unlike the one-shot warehouse, leaving them off lets a
        // transient cluster stall the whole loop mid-run. Default StepAside on and use local CBS to crack dense
        // standoffs (CBS pairs with the SIPP planner here); an explicit request value still overrides. These are
        // sim/executor-scoped liveness levers (production has no executor), exactly as in the other scenarios.
        var jointResolver = request.JointResolver == JointResolverKind.None
            ? JointResolverKind.Cbs
            : request.JointResolver;
        var policy = new LivenessPolicy(
            scenario.Field.Graph,
            new LivenessOptions { JointResolver = jointResolver, StepAside = true });

        // The lifelong runtime: the deterministic priority dispatcher over the generated task stream + the dock index.
        // The loop runs to exactly the horizon (the driver's maxTicks IS the horizon), re-tasking on each clear-to-park.
        var lifelong = new LifelongRuntime(
            new PriorityTaskDispatcher(), scenario.Tasks, scenario.StationByDock, scenario.Field.Graph, horizon);

        var loop = await _loopDriver
            .RunToCompletionAsync(
                engine.Cycle, engine.RoadmapId, scenario.Field.Graph, scenario.Agents,
                maxTicks: (int)Math.Min(int.MaxValue, horizon),
                advanceClock: engine.Clock.SetTick,
                executionMode: FleetExecutionMode.ScheduleFaithful,
                policy: policy,
                log: msg => _logger.LogInformation("[lifelong] {Detail}", msg),
                fms: scenario.Fms,
                stationScheduler: engine.StationScheduler,
                parkingManager: new ParkingManager(),
                lifelong: lifelong,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return Map(scenario.Field, scenario.Agents, loop, emitTrace: request.EmitTrace, fms: scenario.Fms);
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
        GuidanceReportDto? guidance = null,
        bool emitTrace = false,
        OrderDispatchReportDto? orderDispatch = null,
        FmsScenario? fms = null)
    {
        var positionById = field.Sites.ToDictionary(s => s.Id, s => (X: (double)s.X, Y: (double)s.Y), StringComparer.Ordinal);

        // (FMS) The active scenario's per-site role map (dock / parking / buffer / workstation …), or empty for a
        // non-FMS run ⇒ every site's Role below is null ⇒ omitted from the JSON ⇒ byte-identical.
        var siteRoles = fms?.SiteRoles;
        var sites = field.Sites
            .Select(s => new SiteDto(
                s.Id, s.X, s.Y, s.Type.ToString(),
                Role: siteRoles is not null && siteRoles.TryGetValue(s.Id, out var role) ? role.ToString() : null))
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
                        // (FMS) p.Mission is non-null ONLY on an FMS run (the frame-builder gates on the scenario),
                        // so a non-FMS frame stays null ⇒ omitted from the JSON ⇒ byte-identical.
                        return new PositionDto(
                            p.AgentId, p.SiteId, pos.X, pos.Y, p.State.ToString(), p.Mission?.ToString());
                    })
                    .ToList()))
            .ToList();

        var timeline = new TimelineDto(loop.Frames.Count, frames);

        // (FMS-V2) On a DidNotConverge run, classify each stranded AGV (ParkedGoalBlocker / ParkingSaturation /
        // LiveStandoffUnresolved / TickBudgetExceeded / …) from the timeline + roadmap. Null for a converged /
        // collision run ⇒ omitted from the JSON ⇒ byte-identical. FMS site roles (when present) enable the
        // parking-saturation classification; a non-FMS run passes none and that branch never fires.
        var nonConvergence = ToDto(NonConvergenceClassifier.Classify(loop, specs, field.Graph, fms?.SiteRoles));

        var stats = new StatsDto(
            loop.Stats.Ticks,
            loop.Stats.Collisions,
            loop.Stats.Arrived,
            loop.Stats.Replans,
            loop.Stats.Status.ToString(),
            loop.Collision?.Tick,
            loop.Collision?.AgentIds,
            loop.Stats.FlowtimeTicks,
            nonConvergence);

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

        // (v4 SwarmRoute Lab — TraceEvent) Opt-in standardized event trace, derived from the timeline (Planned / Moved
        // / Arrived). Null when not requested → omitted from the JSON, byte-identical.
        var trace = emitTrace ? TraceEventBuilder.Build(loop, specs) : null;

        // (v4 SwarmRoute Lab — Robust Execution) The Action-Dependency-Graph robustness summary, derived from the
        // timeline: cell-handoff dependencies + how much delay the plan absorbs before a naive collision.
        var robustness = RobustnessAnalyzer.Compute(loop);

        // (v4 SwarmRoute Lab — Robust Execution) The ADG-following executor what-if: inject a delay into the most
        // brittle AGV and re-execute naively (collides) vs following the dependency graph (absorbs it, collision-free).
        var delayResilience = AdgExecutor.Simulate(loop);

        return new SimulationResultDto(
            fieldDto, agents, timeline, stats, continuous, metrics, guidance, trace, robustness, delayResilience,
            orderDispatch,
            // (FMS-V3) Continuous-operation metrics — non-null ONLY on a lifelong run (the loop carried a lifelong
            // runtime). Null for every other run ⇒ omitted from the JSON ⇒ byte-identical.
            loop.Lifelong);
    }

    private static string RoadmapGraphLaneId(string from, string to) => $"{from}-{to}";

    /// <summary>(FMS-V2) Maps the internal non-convergence classification to its DTO (reason enums as stable strings),
    /// or <see langword="null"/> when there is nothing to report (a converged run) ⇒ omitted from the JSON.</summary>
    private static NonConvergenceDto? ToDto(NonConvergenceReport? report)
        => report is null
            ? null
            : new NonConvergenceDto(
                report.DominantReason.ToString(),
                report.PerAgentReasons.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal));

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
