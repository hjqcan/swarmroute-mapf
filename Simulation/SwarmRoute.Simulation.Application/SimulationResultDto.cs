namespace SwarmRoute.Simulation.Application;

/// <summary>
/// The full result of one simulation run, shaped for a frontend to render the field and replay the run:
/// the grid field, the per-agent plan (start/goal/colour/path), the tick-by-tick timeline, and run stats.
/// </summary>
public sealed record SimulationResultDto(
    FieldDto Field,
    IReadOnlyList<AgentDto> Agents,
    TimelineDto Timeline,
    StatsDto Stats);

/// <summary>The grid field: its dimensions plus the sites (control points) and lanes (directed edges).</summary>
public sealed record FieldDto(
    int Width,
    int Height,
    IReadOnlyList<SiteDto> Sites,
    IReadOnlyList<LaneDto> Lanes);

/// <summary>A single control point on the grid at planar (<paramref name="X"/>=col, <paramref name="Y"/>=row).</summary>
public sealed record SiteDto(string Id, double X, double Y, string Type);

/// <summary>A directed lane between two control points.</summary>
public sealed record LaneDto(string Id, string From, string To);

/// <summary>
/// One AGV: its id, start/goal control points, a stable colour index for the frontend palette, the CP
/// sequence it was actually reserved along (its replay path / occupied trail), and the route it has yet to
/// travel.
/// </summary>
/// <param name="PathSiteIds">The CP trail the AGV actually occupied (history) — for an arrived AGV this ends at the goal.</param>
/// <param name="RemainingSiteIds">
/// The route still to be travelled: the shortest roadmap path from where <paramref name="PathSiteIds"/> ends to
/// the goal (inclusive of both ends, so it joins the trail seamlessly). Empty once the AGV has arrived; for an
/// AGV that stalled short of its goal (a standoff / <c>DidNotConverge</c>) it is the road ahead — what the
/// frontend draws so "where is it still trying to go" stays visible.
/// </param>
public sealed record AgentDto(
    string Id,
    string StartSiteId,
    string GoalSiteId,
    int ColorIndex,
    IReadOnlyList<string> PathSiteIds,
    IReadOnlyList<string> RemainingSiteIds);

/// <summary>The replay timeline: a frame per tick, each holding every agent's position on that tick.</summary>
public sealed record TimelineDto(int TickCount, IReadOnlyList<FrameDto> Frames);

/// <summary>One tick of the replay: where every agent is.</summary>
public sealed record FrameDto(int Tick, IReadOnlyList<PositionDto> Positions);

/// <summary>
/// One agent's position on one tick: the control point it occupies, its planar (X,Y), and its motion state
/// (<c>Waiting</c> = not yet granted right-of-way, sitting at its start; <c>Moving</c> = en route; <c>Arrived</c>).
/// </summary>
public sealed record PositionDto(string AgentId, string SiteId, double X, double Y, string State);

/// <summary>Aggregate run statistics.</summary>
/// <param name="Ticks">Number of ticks the loop ran (1 frame each).</param>
/// <param name="Collisions">Count of detected CP collisions (0 for a clean run).</param>
/// <param name="Arrived">Number of AGVs that reached their goal.</param>
/// <param name="Replans">Total prune-and-replan retries observed across the run.</param>
/// <param name="Status">Run outcome: <c>Completed</c>, <c>CollisionDetected</c>, or <c>DidNotConverge</c>.</param>
/// <param name="CollisionTick">Tick of the first collision (when <c>Status == CollisionDetected</c>), else null.</param>
/// <param name="CollisionAgentIds">The agents involved in the first collision, else null.</param>
/// <param name="Redirects">Total deadlock redirects enacted by the driver.</param>
/// <param name="Recoveries">Total deadlock recoveries accepted by the driver.</param>
/// <param name="FlowtimeTicks">Sum over arrived AGVs of the tick each reached its goal (a throughput signal;
/// lower is tighter pipelining).</param>
public sealed record StatsDto(
    int Ticks,
    int Collisions,
    int Arrived,
    int Replans,
    string Status,
    int? CollisionTick,
    IReadOnlyList<string>? CollisionAgentIds,
    int Redirects = 0,
    int Recoveries = 0,
    int FlowtimeTicks = 0);
