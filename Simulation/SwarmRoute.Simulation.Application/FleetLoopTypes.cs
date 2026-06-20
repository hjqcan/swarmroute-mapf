using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Liveness.Application.Contract.Policy;
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
    ScheduleFaithful,

    /// <summary>
    /// v3 SIPPwRT: continuous-time, event-driven execution. Instead of one CP per integer tick, the clock jumps to
    /// the next real-millisecond CP-arrival (the union of all agents' planned <c>CpEntryTicks</c>) and advances
    /// whichever agents arrive then. Pairs with the SIPPwRT planner (real kinematic edge durations); honouring its
    /// interval-exclusive schedule is collision-free by construction, exactly as ScheduleFaithful relies on.
    /// </summary>
    Continuous
}

/// <summary>One agent's recorded position on one tick.</summary>
/// <param name="Mission">(FMS) The agent's dispatch mission state on this tick — populated ONLY on an FMS run (an
/// <see cref="FmsScenario"/> is active), else <see langword="null"/> so a non-FMS run is byte-identical (a non-FMS
/// agent's <c>MissionState</c> defaults to <see cref="AgvMissionState.Idle"/> for ALL agents, so it must NOT be
/// blindly serialized — it is gated on the FMS run).</param>
public sealed record FleetTickPosition(
    string AgentId, string SiteId, AgentMotionState State, AgvMissionState? Mission = null);

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
/// <param name="FlowtimeTicks">Sum over arrived agents of the tick at which each reached its goal (lower is
/// better throughput). A schedule-faithful run that pipelines tightly accrues less flowtime than a greedy run
/// that holds trailing vehicles a tick per congested cell.</param>
public sealed record FleetLoopStats(
    FleetLoopStatus Status, int Ticks, int Collisions, int Arrived, int Replans,
    int FlowtimeTicks = 0);

/// <summary>The full recorded result of a closed-loop run.</summary>
/// <param name="Frames">Tick-by-tick timeline (includes the colliding tick when one occurs).</param>
/// <param name="PerAgentRoute">Per agent: the CP trail it actually occupied, with consecutive duplicates collapsed.</param>
/// <param name="Stats">Aggregate stats.</param>
/// <param name="MaxConcurrentEnRoute">Peak number of simultaneously en-route agents (a liveness/parallelism signal).</param>
/// <param name="Collision">The first collision's details, or <see langword="null"/> when none occurred.</param>
/// <param name="TimedTrajectories">(v3 SIPPwRT) Per-agent real-millisecond CP arrival schedule — populated ONLY
/// under <see cref="FleetExecutionMode.Continuous"/>, else <see langword="null"/> (the discrete modes leave it
/// untouched). Derived from the recorded event frames, so it always matches the discrete <see cref="Frames"/>.</param>
/// <param name="Lifelong">(FMS-V3) Continuous-operation metrics — populated ONLY on a lifelong-dispatch run (the
/// driver was handed a lifelong runtime), else <see langword="null"/>. Carries throughput / backlog wait / queue depth
/// / parking saturation derived from the run's task ledger.</param>
public sealed record FleetLoopResult(
    IReadOnlyList<FleetTickFrame> Frames,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PerAgentRoute,
    FleetLoopStats Stats,
    int MaxConcurrentEnRoute,
    FleetCollisionInfo? Collision,
    IReadOnlyList<FleetTimedTrajectory>? TimedTrajectories = null,
    LifelongMetricsDto? Lifelong = null);

/// <summary>(v3 SIPPwRT) One agent's continuous-time trajectory: the CPs it reached and the fleet-clock
/// millisecond it reached each (the first waypoint is its start at t=0).</summary>
public sealed record FleetTimedTrajectory(string AgentId, IReadOnlyList<FleetTimedWaypoint> Waypoints);

/// <summary>(v3 SIPPwRT) A timed waypoint: control point <paramref name="SiteId"/> reached at
/// <paramref name="ArriveMs"/> fleet-clock milliseconds.</summary>
public sealed record FleetTimedWaypoint(string SiteId, long ArriveMs);

/// <summary>Raised only on an internal invariant breach (a reserved path that does not start at the agent's current CP).</summary>
public sealed class FleetLoopException(string message) : Exception(message);
