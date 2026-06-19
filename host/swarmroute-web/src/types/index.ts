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
 */
export type PlannerKind = 'Dijkstra' | 'Sipp'

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
   * Opt-in zone-local PIBT (v3): when a cluster of AGVs is physically stuck, resolve that zone with Priority
   * Inheritance with Backtracking so high-density standoffs converge. SIPP-only; edge-collision safety unchanged.
   */
  usePibt?: boolean
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

/** The full result of one simulation run. */
export interface SimulationResult {
  field: FieldDto
  agents: AgentDto[]
  timeline: Timeline
  stats: Stats
}
