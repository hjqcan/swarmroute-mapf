import { useCallback, useEffect, useMemo, useRef } from 'react'
import { useIntl } from 'react-intl'
import { useSimStore } from '@/store/simStore'
import { usePrefersReducedMotion } from '@/hooks/usePrefersReducedMotion'
import { useElementSize } from '@/hooks/useElementSize'
import { COLORS, hueFor, trailFor, withAlpha } from '@/utils/palette'
import { makeProjector, setupHiDpiCanvas } from '@/utils/canvas'
import {
  buildSiteLookup,
  collisionFrameIndex,
  interpolateContinuous,
  interpolatePositions,
  msAtCursor,
} from '@/utils/simModel'

const MARGIN = 44

/**
 * The hero. A raw HTML5 canvas that draws the grid (sites + faint lanes), each
 * AGV's planned path as a low-alpha polyline in its hue, A/B markers, and AGV
 * markers that animate by interpolating between timeline frames over playback
 * time (driven by the shared store cursor). Resize-aware; flashes the colliding
 * control point + agents red at collisionTick.
 *
 * Rendering model: one `draw()` reads the live store cursor and paints a single
 * frame. While playing we schedule draw() on requestAnimationFrame; while paused
 * we draw once whenever cursor/size/result/motion changes (scrubbing). React is
 * not re-rendered per animation frame — only the canvas repaints.
 */
/** (v4 SwarmRoute Lab) Congestion heat ramp: low load → faint amber, high → solid red. `t` is normalised 0..1. */
function heatColor(t: number): string {
  const u = Math.min(1, Math.max(0, t))
  const g = Math.round(150 * (1 - u) + 40 * u) // amber→red: drop the green channel as load rises
  const alpha = 0.1 + 0.55 * u
  return `rgba(230, ${g}, 40, ${alpha})`
}

export default function FieldCanvas() {
  const intl = useIntl()
  const [setRef, size] = useElementSize<HTMLDivElement>()
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const reduced = usePrefersReducedMotion()

  const result = useSimStore((s) => s.result)
  const cursor = useSimStore((s) => s.cursor)
  const playing = useSimStore((s) => s.playing)
  const hiddenPaths = useSimStore((s) => s.hiddenPaths)
  const showHeatmap = useSimStore((s) => s.showHeatmap)

  const lookup = useMemo(() => (result ? buildSiteLookup(result) : null), [result])

  // A single-frame paint. Reads the live cursor from the store so the RAF loop
  // always paints the current playback position.
  const draw = useCallback(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const { width, height } = size
    const ctx = setupHiDpiCanvas(canvas, width, height)
    if (!ctx) return

    ctx.fillStyle = COLORS.base
    ctx.fillRect(0, 0, width, height)
    if (!result || !lookup) return

    const { field, agents, stats } = result
    const proj = makeProjector(field.width, field.height, width, height, MARGIN)
    const r = Math.max(3, Math.min(9, proj.cell * 0.16))
    const liveCursor = useSimStore.getState().cursor

    // collisionFrameIndex maps the engine's collisionTick to its frame-array index so the
    // flash fires at the right cursor position (comparing the cursor to the raw tick never matched).
    const collisionIdx = collisionFrameIndex(result)
    const collisionAgents = new Set(stats.collisionAgentIds ?? [])
    const atCollision = collisionIdx != null && liveCursor >= collisionIdx - 0.001
    const flashOn = atCollision && (reduced || Math.floor(Date.now() / 350) % 2 === 0)

    /* ---- (v4 SwarmRoute Lab — ScenarioBench) obstacle cells: any grid cell with no control point is a wall ---- */
    {
      const siteIds = new Set(field.sites.map((s) => s.id))
      const wall = proj.cell * 0.96
      for (let row = 0; row < field.height; row++) {
        for (let col = 0; col < field.width; col++) {
          if (siteIds.has(`r${row}c${col}`)) continue
          const px = proj.toX(col)
          const py = proj.toY(row)
          ctx.fillStyle = 'rgba(0, 0, 0, 0.5)'
          ctx.fillRect(px - wall / 2, py - wall / 2, wall, wall)
          ctx.lineWidth = 1
          ctx.strokeStyle = withAlpha(COLORS.hairline, 0.7)
          ctx.strokeRect(px - wall / 2, py - wall / 2, wall, wall)
        }
      }
    }

    /* ---- (v4 SwarmRoute Lab) congestion heatmap: shade each cell by how contested it was over the run.
       Bottlenecks (high occupied + wait agent-ticks) glow red; the network + agents draw on top. ---- */
    if (showHeatmap && result.metrics && result.metrics.heatmap.length > 0) {
      const heat = result.metrics.heatmap
      const maxLoad = Math.max(...heat.map((c) => c.occupiedTicks + c.waitTicks), 1)
      const cellSize = proj.cell * 0.92
      for (const c of heat) {
        const t = (c.occupiedTicks + c.waitTicks) / maxLoad
        if (t <= 0) continue
        ctx.fillStyle = heatColor(t)
        ctx.fillRect(proj.toX(c.x) - cellSize / 2, proj.toY(c.y) - cellSize / 2, cellSize, cellSize)
      }
    }

    /* ---- lanes (faint directed edges) ---- */
    ctx.lineWidth = 1
    ctx.strokeStyle = COLORS.hairline
    for (const lane of field.lanes) {
      const a = lookup.byId.get(lane.from)
      const b = lookup.byId.get(lane.to)
      if (!a || !b) continue
      ctx.beginPath()
      ctx.moveTo(proj.toX(a.x), proj.toY(a.y))
      ctx.lineTo(proj.toX(b.x), proj.toY(b.y))
      ctx.stroke()
    }

    /* ---- planned paths (per-agent polyline in hue, low alpha) ---- */
    ctx.lineJoin = 'round'
    ctx.lineCap = 'round'
    for (const agent of agents) {
      if (hiddenPaths.has(agent.id)) continue
      if (agent.pathSiteIds.length < 2) continue
      ctx.strokeStyle = trailFor(agent.colorIndex)
      ctx.lineWidth = Math.max(2, proj.cell * 0.1)
      ctx.beginPath()
      agent.pathSiteIds.forEach((sid, i) => {
        const s = lookup.byId.get(sid)
        if (!s) return
        const px = proj.toX(s.x)
        const py = proj.toY(s.y)
        if (i === 0) ctx.moveTo(px, py)
        else ctx.lineTo(px, py)
      })
      ctx.stroke()
    }

    /* ---- remaining route to goal (dashed, brighter) ----
       The road each un-arrived AGV has yet to travel: shortest path from where its trail
       ends to the goal. It joins the solid trail seamlessly (shared first point) and makes
       "where is it still trying to go" visible even when the AGV stalled in a standoff.
       Suppressed with the rest of the agent's route by the same visibility toggle. */
    ctx.setLineDash([Math.max(4, proj.cell * 0.2), Math.max(3, proj.cell * 0.16)])
    for (const agent of agents) {
      if (hiddenPaths.has(agent.id)) continue
      if (agent.remainingSiteIds.length < 2) continue
      ctx.strokeStyle = withAlpha(hueFor(agent.colorIndex), 0.85)
      ctx.lineWidth = Math.max(2, proj.cell * 0.1)
      ctx.beginPath()
      agent.remainingSiteIds.forEach((sid, i) => {
        const s = lookup.byId.get(sid)
        if (!s) return
        const px = proj.toX(s.x)
        const py = proj.toY(s.y)
        if (i === 0) ctx.moveTo(px, py)
        else ctx.lineTo(px, py)
      })
      ctx.stroke()
    }
    ctx.setLineDash([])

    /* ---- site nodes ---- */
    for (const s of field.sites) {
      const px = proj.toX(s.x)
      const py = proj.toY(s.y)
      ctx.beginPath()
      ctx.arc(px, py, r * 0.5, 0, Math.PI * 2)
      ctx.fillStyle = COLORS.panel
      ctx.fill()
      ctx.lineWidth = 1
      ctx.strokeStyle = COLORS.hairline
      ctx.stroke()
    }

    /* ---- A (start) and B (goal) markers per agent ---- */
    ctx.font = `600 ${Math.max(9, r * 1.3)}px "Space Grotesk", system-ui, sans-serif`
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    for (const agent of agents) {
      // A hidden agent's whole route is suppressed — its A/B markers go with its path,
      // so isolating one AGV leaves only that AGV's start + goal on the field.
      if (hiddenPaths.has(agent.id)) continue
      const hue = hueFor(agent.colorIndex)
      const start = lookup.byId.get(agent.startSiteId)
      const goal = lookup.byId.get(agent.goalSiteId)
      if (start) {
        const px = proj.toX(start.x)
        const py = proj.toY(start.y)
        ctx.beginPath()
        ctx.arc(px, py, r, 0, Math.PI * 2)
        ctx.fillStyle = withAlpha(hue, 0.18)
        ctx.fill()
        ctx.lineWidth = 1.5
        ctx.strokeStyle = hue
        ctx.stroke()
        ctx.fillStyle = hue
        ctx.fillText('A', px, py + 0.5)
      }
      if (goal) {
        const px = proj.toX(goal.x)
        const py = proj.toY(goal.y)
        // Diamond for the goal so A/B read differently at a glance.
        ctx.save()
        ctx.translate(px, py)
        ctx.rotate(Math.PI / 4)
        ctx.beginPath()
        ctx.rect(-r, -r, r * 2, r * 2)
        ctx.fillStyle = withAlpha(hue, 0.14)
        ctx.fill()
        ctx.lineWidth = 1.5
        ctx.strokeStyle = hue
        ctx.stroke()
        ctx.restore()
        ctx.fillStyle = hue
        ctx.fillText('B', px, py + 0.5)
      }
    }

    /* ---- animated AGV markers ----
       Continuous (SIPPwRT) runs ship a real-millisecond per-agent trajectory: sample it at the playhead ms the
       cursor maps to, so each AGV eases (accelerates/decelerates) along its hop. Discrete runs blend tick frames. */
    const positions = result.continuous
      ? interpolateContinuous(result, msAtCursor(result, liveCursor), reduced)
      : interpolatePositions(result, liveCursor, reduced)
    const markerR = Math.max(5, proj.cell * 0.22)
    for (const agent of agents) {
      const pos = positions.get(agent.id)
      if (!pos) continue
      const hue = hueFor(agent.colorIndex)
      const px = proj.toX(pos.x)
      const py = proj.toY(pos.y)
      const isColliding = flashOn && collisionAgents.has(agent.id)

      if (!reduced && pos.state === 'Moving') {
        ctx.beginPath()
        ctx.arc(px, py, markerR * 1.9, 0, Math.PI * 2)
        ctx.fillStyle = withAlpha(hue, 0.1)
        ctx.fill()
      }

      ctx.beginPath()
      ctx.arc(px, py, markerR, 0, Math.PI * 2)
      ctx.fillStyle = isColliding ? COLORS.danger : hue
      ctx.fill()
      ctx.lineWidth = 2
      ctx.strokeStyle = isColliding ? COLORS.danger : COLORS.base
      ctx.stroke()

      if (pos.state === 'Waiting') {
        ctx.beginPath()
        ctx.arc(px, py, markerR * 0.42, 0, Math.PI * 2)
        ctx.fillStyle = COLORS.base
        ctx.fill()
      }

      ctx.fillStyle = COLORS.textPrimary
      ctx.font = `600 ${Math.max(8, markerR * 0.82)}px "JetBrains Mono", ui-monospace, monospace`
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      ctx.fillText(shortId(agent.id), px, py - markerR - 7)
    }

    /* ---- collision ring on the contended control point ---- */
    if (flashOn && collisionIdx != null) {
      const frame = result.timeline.frames[collisionIdx]
      const involved = frame?.positions.filter((p) => collisionAgents.has(p.agentId)) ?? []
      for (const p of involved) {
        const px = proj.toX(p.x)
        const py = proj.toY(p.y)
        ctx.beginPath()
        ctx.arc(px, py, markerR * 2.4, 0, Math.PI * 2)
        ctx.lineWidth = 2.5
        ctx.strokeStyle = COLORS.danger
        ctx.stroke()
      }
    }
  }, [result, lookup, size, reduced, hiddenPaths, showHeatmap])

  // While playing: one persistent RAF loop that repaints every frame. It reads the
  // LIVE store cursor inside draw(), so it must NOT depend on `cursor` — adding cursor
  // here would tear the loop down and reschedule it on every advance (~60×/s), and the
  // teardown cancels the in-flight draw before it paints, freezing the field on the
  // first frame until playback stops. Depend only on [playing, draw].
  useEffect(() => {
    if (!playing) return
    let raf = requestAnimationFrame(function loop() {
      draw()
      raf = requestAnimationFrame(loop)
    })
    return () => cancelAnimationFrame(raf)
  }, [playing, draw])

  // While paused: repaint once whenever the cursor (scrub), size, result or motion
  // preference changes. If we're parked on/after the collision tick, keep a slow blink
  // alive so the contended control point stays legible while stopped.
  useEffect(() => {
    if (playing) return
    draw()
    const collisionIdx = result ? collisionFrameIndex(result) : null
    const showingCollision = collisionIdx != null && cursor >= collisionIdx - 0.001
    if (!showingCollision || reduced) return
    let raf = requestAnimationFrame(function loop() {
      draw()
      raf = requestAnimationFrame(loop)
    })
    return () => cancelAnimationFrame(raf)
  }, [playing, draw, cursor, result, reduced])

  return (
    <div
      ref={setRef}
      className="relative h-full w-full overflow-hidden rounded-lg border border-hairline bg-base"
    >
      <canvas ref={canvasRef} className="block h-full w-full" aria-hidden="true" />
      {!result && (
        <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center gap-2 text-center">
          <div className="font-display text-lg text-text-primary">
            {intl.formatMessage({ id: 'field.empty.title' })}
          </div>
          <div className="max-w-xs text-sm text-text-muted">
            {intl.formatMessage({ id: 'field.empty.hint' })}
          </div>
        </div>
      )}
    </div>
  )
}

/** Trim a long agent id to something legible on a marker (e.g. "agent-3" -> "3"). */
function shortId(id: string): string {
  const m = id.match(/(\d+)\s*$/)
  return m ? m[1] : id.slice(-2)
}
