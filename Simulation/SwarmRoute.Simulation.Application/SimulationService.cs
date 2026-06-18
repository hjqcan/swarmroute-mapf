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

    public SimulationService(
        GridFieldFactory gridFactory,
        FleetLoopDriver loopDriver,
        ISimulationEngineFactory engineFactory)
    {
        _gridFactory = gridFactory ?? throw new ArgumentNullException(nameof(gridFactory));
        _loopDriver = loopDriver ?? throw new ArgumentNullException(nameof(loopDriver));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
    }

    /// <inheritdoc />
    public async Task<SimulationResultDto> RunAsync(SimulationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        // 1. Build the grid field (graph + render metadata).
        var field = _gridFactory.BuildGrid(request.Width, request.Height);

        // 2. Assign distinct starts and distinct goals. Shuffling all cells and taking two disjoint blocks
        //    ([0..k) starts, [k..2k) goals) guarantees every start != its goal AND all starts/goals are
        //    distinct, given the validated invariant Width*Height >= 2*AgvCount.
        var rng = new Random(request.Seed ?? DefaultSeed);
        var shuffled = Shuffle(field.Sites.Select(s => s.Id).ToList(), rng);

        var agentSpecs = new List<FleetAgentSpec>(request.AgvCount);
        for (var i = 0; i < request.AgvCount; i++)
        {
            var start = shuffled[i];
            var goal = shuffled[request.AgvCount + i];
            agentSpecs.Add(new FleetAgentSpec($"agv-{i + 1}", start, goal, Priority: i));
        }

        // 3. Get a fresh REAL engine for THIS request (isolation between concurrent runs).
        await using var engine = _engineFactory.Create(field.Graph);

        // 4. Run the closed loop to completion, recording the timeline. The driver advances the engine's tick
        //    clock each tick so reservation intervals share the executor's axis (no wall-clock drift).
        var maxTicks = MaxTicks(request);
        var loop = await _loopDriver
            .RunToCompletionAsync(
                engine.Cycle, engine.RoadmapId, field.Graph, agentSpecs, maxTicks,
                advanceClock: engine.Clock.SetTick,
                redirects: engine.Redirects,
                recoverTick: engine.RecoverTick,
                escalateLivelock: engine.EscalateLivelock,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // 5. Map to the transport DTO.
        return Map(field, agentSpecs, loop);
    }

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
        FleetLoopResult loop)
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
            .Select((s, i) => new AgentDto(
                s.Id,
                s.StartSiteId,
                s.GoalSiteId,
                ColorIndex: i,
                PathSiteIds: loop.PerAgentRoute.TryGetValue(s.Id, out var route) ? route : new[] { s.StartSiteId }))
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
            loop.Stats.Redirects,
            loop.Stats.Recoveries);

        return new SimulationResultDto(fieldDto, agents, timeline, stats);
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
