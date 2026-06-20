using System.Text.Json.Serialization;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// The full result of one simulation run, shaped for a frontend to render the field and replay the run:
/// the grid field, the per-agent plan (start/goal/colour/path), the tick-by-tick timeline, and run stats.
/// </summary>
/// <param name="Continuous">(v3 SIPPwRT) Real-millisecond trajectory replay, present ONLY when the run used the
/// continuous-time executor (<c>Planner=Sippwrt</c>). For every other planner it is <see langword="null"/> and
/// omitted from the JSON entirely (<see cref="JsonIgnoreCondition.WhenWritingNull"/>), so discrete responses stay
/// byte-identical. The discrete <see cref="Timeline"/> is always present (the reservation ribbon needs per-event
/// frames); the frontend uses <see cref="Continuous"/>, when set, for smooth time-based agent motion.</param>
public sealed record SimulationResultDto(
    FieldDto Field,
    IReadOnlyList<AgentDto> Agents,
    TimelineDto Timeline,
    StatsDto Stats,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ContinuousTimelineDto? Continuous = null,
    SimulationMetricsDto? Metrics = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuidanceReportDto? Guidance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TraceEventDto>? Trace = null,
    RobustnessDto? Robustness = null);

/// <summary>
/// (v4 SwarmRoute Lab — Robust Execution) The run's Action-Dependency-Graph robustness summary: how many inter-AGV
/// cell <b>handoffs</b> the plan implies, how many are <b>tight</b> (zero buffer — any delay there collides under
/// naive timestamp execution), the <b>minimum slack</b> (the largest single delay the whole plan absorbs before a
/// collision), and the tightest cells. Derived from the timeline; the bigger the dependency count / the smaller the
/// slack, the more a real deployment needs to follow the dependency graph rather than the clock.
/// </summary>
/// <param name="HandoffDependencies">Inter-AGV cell-handoff dependencies (ADG/TPG type-2 edges) — execution coupling.</param>
/// <param name="TightHandoffs">Handoffs with zero slack (a delay there collides under naive timestamp execution).</param>
/// <param name="MinSlackTicks">The smallest handoff buffer — the largest single delay the plan tolerates naively.</param>
/// <param name="TightestCells">The most delay-brittle control points, tightest first.</param>
public sealed record RobustnessDto(
    int HandoffDependencies, int TightHandoffs, int MinSlackTicks, IReadOnlyList<string> TightestCells);

/// <summary>
/// (v4 SwarmRoute Lab — TraceEvent) One event in a run's standardized trace: a typed transition stamped with the
/// run's clock. <see cref="Kind"/> is <c>Planned</c> (at t0; <see cref="SiteId"/> = start, <see cref="FromSiteId"/> =
/// goal), <c>Moved</c> (a CP hop; <see cref="SiteId"/> = entered cell, <see cref="FromSiteId"/> = left cell), or
/// <c>Arrived</c> (<see cref="SiteId"/> = goal). Present only when the request opted in (<c>EmitTrace</c>), so default
/// responses stay lean + byte-identical. Built post-hoc from the timeline, so it never perturbs the run.
/// </summary>
/// <param name="Tick">The run-clock instant (tick, or fleet-clock ms under the continuous executor).</param>
/// <param name="AgentId">The AGV the event is about.</param>
/// <param name="Kind"><c>Planned</c> | <c>Moved</c> | <c>Arrived</c>.</param>
/// <param name="SiteId">The primary control point (entered cell / goal / start).</param>
/// <param name="FromSiteId">For <c>Moved</c> the cell left; for <c>Planned</c> the goal; else null.</param>
public sealed record TraceEventDto(long Tick, string AgentId, string Kind, string SiteId, string? FromSiteId = null);

/// <summary>
/// (v4 SwarmRoute Lab) Present only on an <c>OptimizeGuidance</c> run: the <see cref="Baseline"/> metrics (the run
/// BEFORE the congestion-fed re-weighting) plus a summary of the applied <c>GuidanceGraph</c>. The result's
/// top-level <see cref="SimulationResultDto.Metrics"/> are the GUIDED run; these are what it is compared against, so
/// the frontend can show "wait / throughput / convergence: baseline → guided". Omitted from the JSON when absent.
/// </summary>
/// <param name="Baseline">The metrics of the unguided baseline pass.</param>
/// <param name="AdjustedLanes">How many lanes the optimizer re-weighted from the baseline congestion.</param>
/// <param name="MaxMultiplier">The heaviest weight multiplier applied (the strength of the steer; 1.0 = none).</param>
public sealed record GuidanceReportDto(SimulationMetricsDto Baseline, int AdjustedLanes, double MaxMultiplier);

// ── (v4 SwarmRoute Lab) Run metrics — the "is it good?" quantification layer ─────────────────────────────────────

/// <summary>
/// (v4 SwarmRoute Lab) Quantitative metrics for one run, derived deterministically from the recorded timeline
/// (positions + motion state per tick) plus the aggregate stats — the same schedule the frontend replays, so the
/// numbers are reproducible for a given request. These turn "looks like it runs" into "here is throughput, travel
/// time, wait, fairness, reliability, and where the bottlenecks are", which is what makes one planner / policy / map
/// comparable to another. Units follow the run's clock: ticks for the discrete executors, fleet-clock milliseconds
/// for the continuous (SIPPwRT) executor.
/// </summary>
/// <param name="AgvCount">Fleet size.</param>
/// <param name="Arrived">AGVs that reached their goal.</param>
/// <param name="CompletionRate">Arrived / fleet size (0..1) — reliability at a glance.</param>
/// <param name="MakespanTicks">The last recorded tick (when the fleet finished, or the budget ran out).</param>
/// <param name="ThroughputPerThousandTicks">Arrivals per 1000 ticks (makespan-normalised) — the headline rate.</param>
/// <param name="TravelTime">Per-arrived-agent time-to-goal distribution (mean + P50/P95/P99 + max).</param>
/// <param name="MeanWaitRatio">Fleet-mean fraction of each agent's run spent not making forward progress (0..1).</param>
/// <param name="TotalWaitTicks">Total agent-ticks spent stationary-not-arrived (pending or gate-blocked).</param>
/// <param name="TotalReplans">Prune-and-replan retries across the run (planning stability / churn).</param>
/// <param name="MaxConcurrent">Peak simultaneously en-route AGVs (parallelism the route network sustained).</param>
/// <param name="Collisions">Detected control-point collisions (0 for a clean run).</param>
/// <param name="Status">Run outcome: <c>Completed</c>, <c>CollisionDetected</c>, or <c>DidNotConverge</c>.</param>
/// <param name="FairnessIndex">Jain's fairness index over the WHOLE fleet's effective-completion times — every agent
/// contributes, an un-arrived one counted at the makespan (1 = perfectly even; →0 = a few agents finish while many
/// starve at the makespan). Fleet-wide so starvation lowers it honestly; outright starvation is read most directly
/// from <see cref="SimulationMetricsDto.CompletionRate"/>.</param>
/// <param name="Heatmap">Per-cell congestion (occupied + waited agent-ticks) for the bottleneck heatmap overlay.</param>
/// <param name="BottleneckSiteIds">The most-congested control points, worst first (a quick bottleneck ranking).</param>
public sealed record SimulationMetricsDto(
    int AgvCount,
    int Arrived,
    double CompletionRate,
    int MakespanTicks,
    double ThroughputPerThousandTicks,
    TravelTimeStatsDto TravelTime,
    double MeanWaitRatio,
    int TotalWaitTicks,
    int TotalReplans,
    int MaxConcurrent,
    int Collisions,
    string Status,
    double FairnessIndex,
    IReadOnlyList<CellCongestionDto> Heatmap,
    IReadOnlyList<string> BottleneckSiteIds);

/// <summary>A time-to-goal distribution (in the run's clock units): mean plus the tail percentiles that matter for
/// SLA ("most agents are fine, but the worst 5% / 1%?") and the maximum (= the makespan among arrivals).</summary>
public sealed record TravelTimeStatsDto(double Mean, int P50, int P95, int P99, int Max);

/// <summary>One control point's congestion over the whole run: how many agent-ticks were spent ON it
/// (<paramref name="OccupiedTicks"/>) and how many of those were stationary-not-arrived (<paramref name="WaitTicks"/>,
/// the contention signal). Carries planar <paramref name="X"/>,<paramref name="Y"/> so the frontend can shade the cell.</summary>
public sealed record CellCongestionDto(string SiteId, double X, double Y, int OccupiedTicks, int WaitTicks);

/// <summary>
/// (v3 SIPPwRT) The continuous-time replay: each agent's real-millisecond control-point arrival schedule. Unlike
/// the discrete <see cref="TimelineDto"/> (one frame per event), this is per-agent and time-stamped, so a player
/// can interpolate each agent's position smoothly at any playhead millisecond.
/// </summary>
/// <param name="DurationMs">The last arrival millisecond across the fleet — the total length of the replay.</param>
/// <param name="Agents">Each agent's timed trajectory, in stable ordinal id order.</param>
public sealed record ContinuousTimelineDto(long DurationMs, IReadOnlyList<AgentTrajectoryDto> Agents);

/// <summary>One agent's continuous trajectory: the control points it reached and the real-ms time it reached each
/// (the first waypoint is its start at t=0). Between consecutive waypoints the agent moves from one to the next.</summary>
public sealed record AgentTrajectoryDto(string AgentId, IReadOnlyList<TrajectoryWaypointDto> Waypoints);

/// <summary>A timed waypoint: the agent is at control point <paramref name="SiteId"/> (planar
/// <paramref name="X"/>,<paramref name="Y"/>) at <paramref name="ArriveMs"/> fleet-clock milliseconds.</summary>
public sealed record TrajectoryWaypointDto(string SiteId, double X, double Y, long ArriveMs);

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
    int FlowtimeTicks = 0);
