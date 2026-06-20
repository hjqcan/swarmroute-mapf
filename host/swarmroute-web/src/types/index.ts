/*
 * API contract types for the SwarmRoute simulation engine.
 *
 * The backend (POST /api/simulation/run) returns this DTO DIRECTLY as camelCase JSON
 * — it is NOT wrapped in { code, msg, data }. On bad input it returns 400 ProblemDetails.
 * These types mirror SwarmRoute.Simulation.Application.SimulationResultDto.
 */

/** Per-agent motion state on a given tick. */
export type RunState = 'Waiting' | 'Moving' | 'Arrived'

/** Aggregate run outcome — the honest verification verdict. */
export type RunStatus = 'Completed' | 'CollisionDetected' | 'DidNotConverge'

/**
 * Which planner the engine runs.
 * - `Dijkstra` — v0 space-only shortest path; can deadlock in dense fields (two AGVs commit to crossing
 *   routes and stall forever, reported as DidNotConverge).
 * - `Sipp` — v1 Safe-Interval Path Planning; reservation-aware (plans in time), so it routes around
 *   reservation conflicts and reports remaining physical standoffs as DidNotConverge.
 * - `Sippwrt` — v3 continuous-time SIPP: each edge costs its real kinematic traversal duration (not a fixed
 *   tick), executed by the event-driven continuous executor. The result then carries a {@link ContinuousTimeline}
 *   for smooth real-millisecond playback. (On the uniform sim grid every edge is the same length, so the
 *   time-optimal advantage is inert here — the visible difference is smooth, eased motion.)
 */
export type PlannerKind = 'Dijkstra' | 'Sipp' | 'Sippwrt'

/** (v4 SwarmRoute Lab — ScenarioBench) The map layout: `Open` is the uniform grid; `Bottleneck` walls the middle
 *  column except a central corridor; `Obstacles` is a lattice of pillars. Obstacle cells are absent from the field. */
export type ScenarioKind = 'Open' | 'Bottleneck' | 'Obstacles'

/** (v4 SwarmRoute Lab — Dispatcher) How the dispatcher matches AGVs to goals: `Random` (uncorrelated, the default),
 *  `Nearest` (greedy nearest-robot), or `Optimal` (Hungarian min-total-travel matching). */
export type AssignmentPolicy = 'Random' | 'Nearest' | 'Optimal'

/**
 * Which zone-local joint resolver owns a physical standoff cluster (mirrors the backend
 * `SwarmRoute.Liveness.Application.Contract.Policy.JointResolverKind`). Exactly one resolver owns a cluster.
 * - `None` — no joint resolver; clusters are broken only by the cheap per-agent ladder (head-on yield / stall-reroute).
 * - `Pibt` — zone-local PIBT (Priority Inheritance with Backtracking): fast greedy one-hop-per-tick drive.
 * - `Cbs` — zone-local CBS (Conflict-Based Search): complete, cracks the dense standoffs PIBT can't. SIPP-only.
 */
export type JointResolverKind = 'None' | 'Pibt' | 'Cbs'

/** Inputs to one simulation run. */
export interface SimulationRequest {
  width: number
  height: number
  agvCount: number
  seed?: number
  planner?: PlannerKind
  /**
   * Optional rolling-horizon window for SIPP/RHCR. Omit for the backend default: unbounded whole-path planning.
   */
  horizonWindowMs?: number
  /**
   * Optional explicit per-AGV start cells (agent order), to CONTINUE a lifelong run: each AGV keeps its current
   * pose and gets a new goal instead of teleporting to a fresh random layout. Omitted on a fresh run.
   */
  starts?: string[]
  /**
   * Opt-in executor recovery for SIPP goal-blocking cases. Edge-collision safety is independent and always on.
   */
  stepAside?: boolean
  /**
   * Opt-in zone-local joint resolver for physical standoff clusters (SIPP-only; default `None`). One resolver owns
   * a cluster: `Pibt` is the fast greedy priority-inheritance drive (v3); `Cbs` is the complete local Conflict-Based
   * Search (v3) that cracks the swaps/chains greedy PIBT can't (heavier). Edge-collision safety is independent.
   */
  jointResolver?: JointResolverKind
  /**
   * (v4 SwarmRoute Lab) Opt-in 2-pass congestion-fed guidance optimization: run once, re-weight the busiest
   * corridors from the measured congestion, then re-run the SAME fleet on the guided field. The result then carries
   * a {@link GuidanceReport} comparing baseline vs guided. Off by default. Steers weight-aware planners (SIPPwRT,
   * Dijkstra); hop-uniform SIPP is unaffected.
   */
  optimizeGuidance?: boolean
  /** (v4 SwarmRoute Lab — ScenarioBench) The map layout (default `Open` = uniform grid). Obstacle scenarios carve
   *  walls / pillars so the metrics, heatmap, guidance and continuous-time are exercised on a non-uniform field. */
  scenario?: ScenarioKind
  /** (v4 SwarmRoute Lab — Dispatcher) How the dispatcher matches AGVs to goals (default `Random`). `Optimal` is the
   *  Hungarian min-total-travel matching; `Nearest` the greedy nearest-robot heuristic — both cut travel. */
  assignment?: AssignmentPolicy
  /** (v4 SwarmRoute Lab — TraceEvent) Opt-in: emit the standardized event trace on the result for export. Default off. */
  emitTrace?: boolean
  /** (v4 SwarmRoute Lab — Order/Dispatch context) Opt-in: simulate a lifelong order stream over the same field + fleet
   *  (online assignment, stations, battery, SLA) and report its operations KPIs. Default off. */
  simulateOrders?: boolean
}

/** A single control point on the grid at planar (x=col, y=row). */
export interface Site {
  id: string
  x: number
  y: number
  type: string
}

/** A directed lane between two control points. */
export interface Lane {
  id: string
  from: string
  to: string
}

/** The grid field: dimensions plus sites (nodes) and lanes (edges). */
export interface FieldDto {
  width: number
  height: number
  sites: Site[]
  lanes: Lane[]
}

/** One AGV: id, start/goal, stable colour index, the reserved CP sequence (replay path / occupied trail),
 *  and the route it has yet to travel (shortest path from where the trail ends to the goal; [] once arrived). */
export interface AgentDto {
  id: string
  startSiteId: string
  goalSiteId: string
  colorIndex: number
  pathSiteIds: string[]
  remainingSiteIds: string[]
}

/** One agent's position on one tick. */
export interface Position {
  agentId: string
  siteId: string
  x: number
  y: number
  state: RunState
}

/** One tick of the replay: where every agent is. */
export interface Frame {
  tick: number
  positions: Position[]
}

/** The replay timeline: one frame per tick. */
export interface Timeline {
  tickCount: number
  frames: Frame[]
}

/** (v3 SIPPwRT) A timed waypoint: the agent reaches control point `siteId` (planar x,y) at `arriveMs` fleet-clock ms. */
export interface TrajectoryWaypoint {
  siteId: string
  x: number
  y: number
  arriveMs: number
}

/** (v3 SIPPwRT) One agent's continuous trajectory: the control points it reached, each stamped with the real
 *  millisecond it was reached (the first waypoint is its start at t=0). Interpolate between consecutive waypoints
 *  for smooth motion. */
export interface AgentTrajectory {
  agentId: string
  waypoints: TrajectoryWaypoint[]
}

/** (v3 SIPPwRT) Continuous-time replay: per-agent real-millisecond CP arrival schedules. Present ONLY when the run
 *  used the continuous executor (planner = `Sippwrt`); absent (undefined) for every discrete planner, so the
 *  discrete response stays byte-identical. */
export interface ContinuousTimeline {
  durationMs: number
  agents: AgentTrajectory[]
}

/** Aggregate run statistics. */
export interface Stats {
  ticks: number
  collisions: number
  arrived: number
  replans: number
  status: RunStatus
  collisionTick: number | null
  collisionAgentIds: string[] | null
}

/** (v4 SwarmRoute Lab) Time-to-goal distribution in the run's clock units (ticks; ms for the continuous executor). */
export interface TravelTimeStats {
  mean: number
  p50: number
  p95: number
  p99: number
  max: number
}

/** (v4 SwarmRoute Lab) One control point's congestion over the run — the data behind the bottleneck heatmap. */
export interface CellCongestion {
  siteId: string
  x: number
  y: number
  occupiedTicks: number
  waitTicks: number
}

/** (v4 SwarmRoute Lab) Quantitative metrics for one run — throughput, travel-time tail, wait, fairness,
 *  reliability, and the per-cell congestion heatmap. The "is it good?" layer, deterministic for a given request. */
export interface SimulationMetrics {
  agvCount: number
  arrived: number
  completionRate: number
  makespanTicks: number
  throughputPerThousandTicks: number
  travelTime: TravelTimeStats
  meanWaitRatio: number
  totalWaitTicks: number
  totalReplans: number
  maxConcurrent: number
  collisions: number
  status: RunStatus
  fairnessIndex: number
  heatmap: CellCongestion[]
  bottleneckSiteIds: string[]
}

/** The full result of one simulation run. */
export interface SimulationResult {
  field: FieldDto
  agents: AgentDto[]
  timeline: Timeline
  stats: Stats
  /** (v3 SIPPwRT) Present only for the continuous executor (planner = `Sippwrt`); drives smooth time-based
   *  playback on the field. Undefined for discrete planners — the field then animates off the tick timeline. */
  continuous?: ContinuousTimeline
  /** (v4 SwarmRoute Lab) Quantitative run metrics + the per-cell congestion heatmap. Present on every run. */
  metrics?: SimulationMetrics
  /** (v4 SwarmRoute Lab) Present only on an OptimizeGuidance run: the baseline (pre-guidance) metrics + the applied
   *  guidance summary; the top-level {@link metrics} are then the GUIDED run, for a baseline→guided comparison. */
  guidance?: GuidanceReport
  /** (v4 SwarmRoute Lab — TraceEvent) The standardized event log (Planned/Moved/Arrived), present only when the
   *  request opted in (`emitTrace`). Exportable for external analysis. */
  trace?: TraceEvent[]
  /** (v4 SwarmRoute Lab — Robust Execution) The ADG/TPG robustness summary (present on every run). */
  robustness?: Robustness
  /** (v4 SwarmRoute Lab — Robust Execution) The ADG/TPG-following executor what-if: under an injected delay, naive
   *  timestamp replay collides while the dependency-following replay absorbs it. Present only when a cell is shared. */
  delayResilience?: DelayResilience
  /** (v4 SwarmRoute Lab — Order/Dispatch context) The lifelong online-dispatch operations summary (orders releasing
   *  over time, queued + assigned to the fleet with stations/battery/SLA). Present only when the request opted in. */
  orderDispatch?: OrderDispatch
}

/** (v4 SwarmRoute Lab) An OptimizeGuidance run's comparison payload: the unguided baseline metrics + a summary of
 *  the applied edge-weight guidance. The result's top-level `metrics` are the guided run. */
export interface GuidanceReport {
  baseline: SimulationMetrics
  adjustedLanes: number
  maxMultiplier: number
}

/** (v4 SwarmRoute Lab) One planner's result in a portfolio benchmark — the same map / fleet / seed run under each
 *  planner so their unit-free metrics (completion, wait ratio, fairness) are directly comparable. */
export interface BenchmarkEntry {
  planner: PlannerKind
  metrics: SimulationMetrics | null
}

/** (v4 SwarmRoute Lab — TraceEvent) One event in a run's standardized trace: a typed transition stamped with the
 *  run's clock. `kind` is `Planned` (siteId=start, fromSiteId=goal), `Moved` (fromSiteId→siteId hop), or `Arrived`. */
export interface TraceEvent {
  tick: number
  agentId: string
  kind: string
  siteId: string
  fromSiteId?: string
}

/** (v4 SwarmRoute Lab — Robust Execution) The run's Action-Dependency-Graph robustness: inter-AGV cell-handoff
 *  dependencies (execution coupling), how many are tight (zero buffer), the min slack (largest single delay the plan
 *  absorbs before a naive collision), and the most delay-brittle cells. */
export interface Robustness {
  handoffDependencies: number
  tightHandoffs: number
  minSlackTicks: number
  tightestCells: string[]
}

/** (v4 SwarmRoute Lab — Robust Execution) The ADG/TPG-following executor's delay what-if. A delay (just past the
 *  tightest handoff's slack) is injected into the most brittle AGV, then the plan is re-executed two ways: naively by
 *  wall-clock timestamps (collides at tight handoffs) versus following the dependency graph (collision-free, paying
 *  `adgMakespanInflation` extra ticks). The contrast is the case for executing on dependencies, not the clock. */
export interface DelayResilience {
  delayTicks: number
  delayedAgent: string
  naiveCollisions: number
  adgCollisions: number
  adgMakespanInflation: number
  plannedMakespan: number
}

/** (v4 SwarmRoute Lab — Order/Dispatch context) The lifelong online-dispatch operations summary, simulated above the
 *  MAPF layer: a stream of transport orders releasing over time, queued and continuously assigned to the fleet (pickup
 *  → dropoff stations, battery, SLA deadlines). The headline is policy sensitivity — a smarter assignment turns the
 *  backlog over faster, lifting on-time delivery and cutting latency. All times in ms. */
export interface OrderDispatch {
  ordersTotal: number
  ordersCompleted: number
  onTimeRate: number
  meanLatencyMs: number
  p95LatencyMs: number
  makespanMs: number
  fleetUtilization: number
  chargingStops: number
  maxQueueDepth: number
  policy: string
}
