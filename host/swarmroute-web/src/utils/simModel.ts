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
