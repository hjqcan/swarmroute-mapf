/*
 * Pure helpers that turn a SimulationResult into render-ready lookups, and that
 * interpolate agent positions between timeline frames. Shared by FieldCanvas and
 * ReservationRibbon so the field and the ribbon stay perfectly in sync.
 */
import type { AgentDto, Frame, Position, SimulationResult, Site } from '@/types'

export interface SiteLookup {
  byId: Map<string, Site>
}

/** Build a fast id -> Site map for the field. */
export function buildSiteLookup(result: SimulationResult): SiteLookup {
  const byId = new Map<string, Site>()
  for (const s of result.field.sites) byId.set(s.id, s)
  return { byId }
}

/** A single agent's interpolated render position at a given cursor. */
export interface AgentRenderPos {
  agentId: string
  /** Interpolated planar position (grid units; x=col, y=row). */
  x: number
  y: number
  /** Discrete state taken from the nearest (floor) frame. */
  state: Position['state']
  /** The control point id the agent occupies on the floor frame. */
  siteId: string
}

function clampFrameIndex(cursor: number, frameCount: number): number {
  if (frameCount <= 0) return 0
  return Math.max(0, Math.min(frameCount - 1, cursor))
}

/**
 * Interpolate every agent's position at a float `cursor` over the frames.
 *
 * - integer part of cursor = the "from" frame; fraction = blend toward the next frame.
 * - When `snap` is true (reduced motion) we round to the nearest frame and do not blend.
 *
 * Returns a Map keyed by agentId for O(1) lookups during drawing.
 */
export function interpolatePositions(
  result: SimulationResult,
  cursor: number,
  snap: boolean
): Map<string, AgentRenderPos> {
  const frames = result.timeline.frames
  const out = new Map<string, AgentRenderPos>()
  if (frames.length === 0) return out

  if (snap) {
    const idx = clampFrameIndex(Math.round(cursor), frames.length)
    const frame = frames[idx]
    for (const p of frame.positions) {
      out.set(p.agentId, { agentId: p.agentId, x: p.x, y: p.y, state: p.state, siteId: p.siteId })
    }
    return out
  }

  const lo = Math.floor(clampFrameIndex(cursor, frames.length))
  const hi = Math.min(frames.length - 1, lo + 1)
  const t = lo === hi ? 0 : cursor - lo

  const fromFrame: Frame = frames[lo]
  const toFrame: Frame = frames[hi]

  // Index the "to" frame by agent so we can pair positions.
  const toById = new Map<string, Position>()
  for (const p of toFrame.positions) toById.set(p.agentId, p)

  for (const from of fromFrame.positions) {
    const to = toById.get(from.agentId) ?? from
    out.set(from.agentId, {
      agentId: from.agentId,
      x: from.x + (to.x - from.x) * t,
      y: from.y + (to.y - from.y) * t,
      // State is discrete — take the frame we're leaving.
      state: from.state,
      siteId: from.siteId,
    })
  }
  return out
}

/**
 * The real-millisecond playhead at a float frame `cursor`. In a continuous (SIPPwRT) run the event frames are
 * stamped with their fleet-clock millisecond in `frame.tick`, so blending the bracketing frames' ticks turns the
 * frame cursor (which the playback controls and ribbon already drive) into a millisecond playhead for trajectory
 * sampling — no second clock to keep in sync.
 */
export function msAtCursor(result: SimulationResult, cursor: number): number {
  const frames = result.timeline.frames
  if (frames.length === 0) return 0
  const lo = Math.max(0, Math.min(frames.length - 1, Math.floor(cursor)))
  const hi = Math.min(frames.length - 1, lo + 1)
  const frac = lo === hi ? 0 : cursor - lo
  return frames[lo].tick + (frames[hi].tick - frames[lo].tick) * frac
}

/**
 * Normalised trapezoidal motion profile: maps a time fraction f∈[0,1] to a distance fraction s∈[0,1] that
 * accelerates over the first `r`, cruises, then decelerates over the last `r` — zero velocity at both ends, so an
 * AGV eases out of a control point and brakes into the next instead of moving at a constant clip. This is the
 * kinematic feel SIPPwRT's real edge durations earn. Monotonic; s(0)=0, s(1)=1.
 */
export function trapezoidalEase(f: number, r = 0.25): number {
  if (f <= 0) return 0
  if (f >= 1) return 1
  const vPeak = 1 / (1 - r) // peak velocity that makes the area under the trapezoid exactly 1
  if (f < r) return (vPeak * f * f) / (2 * r)
  if (f > 1 - r) {
    const g = 1 - f
    return 1 - (vPeak * g * g) / (2 * r)
  }
  return (vPeak * r) / 2 + vPeak * (f - r)
}

/**
 * (v3 SIPPwRT) Interpolate every agent's position at a real-millisecond `playheadMs` along its continuous
 * trajectory (CP waypoints stamped with arrival ms), easing each hop with {@link trapezoidalEase}. Returns an
 * empty map when the result has no continuous timeline (a discrete planner — the caller uses
 * {@link interpolatePositions} instead). With `snap` (reduced motion) it holds the nearer control point.
 */
export function interpolateContinuous(
  result: SimulationResult,
  playheadMs: number,
  snap: boolean
): Map<string, AgentRenderPos> {
  const out = new Map<string, AgentRenderPos>()
  const continuous = result.continuous
  if (!continuous) return out

  for (const traj of continuous.agents) {
    const w = traj.waypoints
    if (w.length === 0) continue

    // Locate the segment [w[i], w[i+1]] that the playhead falls in (clamped to the ends).
    let i = 0
    while (i < w.length - 1 && playheadMs >= w[i + 1].arriveMs) i++
    const from = w[i]
    const to = w[Math.min(w.length - 1, i + 1)]
    const arrived = i >= w.length - 1 // playhead at/after the final waypoint → parked at goal

    const span = to.arriveMs - from.arriveMs
    const f = arrived || span <= 0 ? 0 : (playheadMs - from.arriveMs) / span
    const s = snap ? (f < 0.5 ? 0 : 1) : trapezoidalEase(f)

    out.set(traj.agentId, {
      agentId: traj.agentId,
      x: from.x + (to.x - from.x) * s,
      y: from.y + (to.y - from.y) * s,
      state: arrived ? 'Arrived' : 'Moving',
      siteId: s < 0.5 ? from.siteId : to.siteId,
    })
  }
  return out
}

/**
 * The displayed (engine) tick at a float cursor. The cursor is a 0-based index over
 * the frames array; the engine numbers ticks in `frame.tick` (which need not equal
 * the index). Always read the label from the frame, never from the cursor.
 */
export function tickAtCursor(result: SimulationResult, cursor: number): number {
  const frames = result.timeline.frames
  if (frames.length === 0) return 0
  const idx = Math.max(0, Math.min(frames.length - 1, Math.round(cursor)))
  return frames[idx].tick
}

/** First and last engine tick numbers in the timeline. */
export function tickRange(result: SimulationResult): { first: number; last: number } {
  const frames = result.timeline.frames
  if (frames.length === 0) return { first: 0, last: 0 }
  return { first: frames[0].tick, last: frames[frames.length - 1].tick }
}

/**
 * The frame-array index of the colliding tick (for a CollisionDetected run), or null.
 * The cursor/ribbon-columns are 0-based frame indices, but `stats.collisionTick` is an
 * engine tick number — this maps between the two so the field flash and the ribbon's
 * red block/marker land on the correct column instead of comparing a tick to an index.
 */
export function collisionFrameIndex(result: SimulationResult): number | null {
  const { status, collisionTick } = result.stats
  if (status !== 'CollisionDetected' || collisionTick == null) return null
  const idx = result.timeline.frames.findIndex((f) => f.tick === collisionTick)
  return idx >= 0 ? idx : null
}

/** Stable ordering of agents by colorIndex then id (for ribbon rows + legend). */
export function sortedAgents(agents: AgentDto[]): AgentDto[] {
  return [...agents].sort((a, b) => a.colorIndex - b.colorIndex || a.id.localeCompare(b.id))
}

/**
 * For the reservation ribbon: the ordered list of control-point ids each agent
 * occupies per tick (rows aligned to `agents`, columns aligned to frames/ticks).
 */
export function occupancyByAgent(result: SimulationResult): Map<string, string[]> {
  const map = new Map<string, string[]>()
  for (const a of result.agents) map.set(a.id, [])
  for (const frame of result.timeline.frames) {
    for (const p of frame.positions) {
      const row = map.get(p.agentId)
      if (row) row.push(p.siteId)
    }
  }
  return map
}
