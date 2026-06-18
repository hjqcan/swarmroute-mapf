import { useCallback, useEffect, useMemo, useRef } from 'react'
import { useIntl } from 'react-intl'
import { useSimStore } from '@/store/simStore'
import { usePrefersReducedMotion } from '@/hooks/usePrefersReducedMotion'
import { useElementSize } from '@/hooks/useElementSize'
import { COLORS, hueFor, withAlpha } from '@/utils/palette'
import { setupHiDpiCanvas } from '@/utils/canvas'
import { collisionFrameIndex, occupancyByAgent, sortedAgents } from '@/utils/simModel'

const PAD_LEFT = 56 // gutter for the agent labels
const PAD_RIGHT = 14
const PAD_TOP = 22 // tick axis
const PAD_BOTTOM = 10

/**
 * The signature element: a space-time reservation chart. One row per AGV (in its
 * hue), a horizontal tick axis, and per-tick cells showing which control point
 * each AGV occupies. Consecutive same-CP cells read as a "hold" (dimmer); CP
 * changes read as moves (a small connector + brighter block). A warm-amber
 * playhead is synced to the field playback (the shared store cursor), so the
 * field and the ribbon move as one. This is the WHERE + WHEN view that explains
 * why the run is collision-free: no two rows ever fill the same CP in the same
 * column.
 *
 * Interactive: click/drag scrubs the cursor (and pauses), so you can inspect any
 * tick.
 */
export default function ReservationRibbon() {
  const intl = useIntl()
  const [setRef, size] = useElementSize<HTMLDivElement>()
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const reduced = usePrefersReducedMotion()

  const result = useSimStore((s) => s.result)
  const cursor = useSimStore((s) => s.cursor)
  const playing = useSimStore((s) => s.playing)
  const setCursor = useSimStore((s) => s.setCursor)
  const setPlaying = useSimStore((s) => s.setPlaying)

  // Per-result derived data (stable across animation frames).
  const model = useMemo(() => {
    if (!result) return null
    const agents = sortedAgents(result.agents)
    const occ = occupancyByAgent(result)
    // Assign each distinct control-point id a band index for vertical micro-offset
    // within a row, so a CP change is visible even at a glance.
    const cpIndex = new Map<string, number>()
    result.field.sites.forEach((s, i) => cpIndex.set(s.id, i))
    return { agents, occ, cpIndex, tickCount: result.timeline.tickCount }
  }, [result])

  const draw = useCallback(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const { width, height } = size
    const ctx = setupHiDpiCanvas(canvas, width, height)
    if (!ctx) return

    // Panel backdrop.
    ctx.fillStyle = COLORS.panel
    ctx.fillRect(0, 0, width, height)
    if (!result || !model || model.agents.length === 0) return

    const liveCursor = useSimStore.getState().cursor
    const frames = result.timeline.frames
    const cols = Math.max(1, frames.length)
    const rows = model.agents.length

    const plotW = Math.max(1, width - PAD_LEFT - PAD_RIGHT)
    const plotH = Math.max(1, height - PAD_TOP - PAD_BOTTOM)
    const colW = plotW / cols
    const rowH = plotH / rows
    const blockH = Math.min(rowH * 0.62, 18)

    const xForTick = (t: number) => PAD_LEFT + (t + 0.5) * colW
    const yForRow = (r: number) => PAD_TOP + (r + 0.5) * rowH

    /* ---- tick axis: gridlines + sparse labels ---- */
    ctx.font = '500 10px "JetBrains Mono", ui-monospace, monospace'
    ctx.fillStyle = COLORS.textMuted
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    const labelEvery = Math.max(1, Math.ceil(cols / 16))
    for (let t = 0; t < cols; t++) {
      const x = xForTick(t)
      if (t % labelEvery === 0) {
        ctx.strokeStyle = withAlpha(COLORS.hairline, 0.5)
        ctx.lineWidth = 1
        ctx.beginPath()
        ctx.moveTo(x, PAD_TOP)
        ctx.lineTo(x, height - PAD_BOTTOM)
        ctx.stroke()
        ctx.fillStyle = COLORS.textMuted
        // Label with the engine tick from the frame, not the column index.
        ctx.fillText(String(frames[t]?.tick ?? t), x, PAD_TOP - 11)
      }
    }

    // Column index of the colliding tick (cursor/columns are 0-based indices, collisionTick is an engine tick).
    const collisionIdx = collisionFrameIndex(result)
    const collisionAgents = new Set(result.stats.collisionAgentIds ?? [])

    /* ---- one row per AGV ---- */
    model.agents.forEach((agent, ri) => {
      const hue = hueFor(agent.colorIndex)
      const y = yForRow(ri)
      const occ = model.occ.get(agent.id) ?? []

      // Row label (left gutter).
      ctx.fillStyle = hue
      ctx.textAlign = 'right'
      ctx.font = '600 11px "JetBrains Mono", ui-monospace, monospace'
      ctx.fillText(shortId(agent.id), PAD_LEFT - 12, y)
      // Swatch.
      ctx.beginPath()
      ctx.arc(PAD_LEFT - 6, y, 3, 0, Math.PI * 2)
      ctx.fillStyle = hue
      ctx.fill()

      // Faint row baseline.
      ctx.strokeStyle = withAlpha(COLORS.hairline, 0.6)
      ctx.lineWidth = 1
      ctx.beginPath()
      ctx.moveTo(PAD_LEFT, y)
      ctx.lineTo(width - PAD_RIGHT, y)
      ctx.stroke()

      // Per-tick reservation blocks.
      for (let t = 0; t < cols; t++) {
        const cp = occ[t]
        if (cp == null) continue
        const prev = t > 0 ? occ[t - 1] : undefined
        const moved = prev !== undefined && prev !== cp
        const x0 = PAD_LEFT + t * colW
        const cx = xForTick(t)

        // Connector to previous block when the AGV moved to a new CP.
        if (moved) {
          ctx.strokeStyle = withAlpha(hue, 0.5)
          ctx.lineWidth = 1.5
          ctx.beginPath()
          ctx.moveTo(x0 - colW * 0.5, y)
          ctx.lineTo(x0 + colW * 0.5, y)
          ctx.stroke()
        }

        const w = Math.max(2, colW * 0.78)
        const h = blockH
        const isCollision =
          collisionIdx != null && t === collisionIdx && collisionAgents.has(agent.id)
        // Holds (same CP as previous) are dimmer; fresh occupancy is brighter.
        const held = prev === cp
        ctx.fillStyle = isCollision ? COLORS.danger : withAlpha(hue, held ? 0.35 : 0.85)
        roundRect(ctx, cx - w / 2, y - h / 2, w, h, Math.min(3, h / 2))
        ctx.fill()
        if (isCollision) {
          ctx.strokeStyle = COLORS.danger
          ctx.lineWidth = 1.5
          roundRect(ctx, cx - w / 2, y - h / 2, w, h, Math.min(3, h / 2))
          ctx.stroke()
        }
      }
    })

    /* ---- collision tick marker (full-height) ---- */
    if (collisionIdx != null) {
      const x = xForTick(collisionIdx)
      ctx.strokeStyle = withAlpha(COLORS.danger, 0.8)
      ctx.lineWidth = 1.5
      ctx.setLineDash([3, 3])
      ctx.beginPath()
      ctx.moveTo(x, PAD_TOP - 4)
      ctx.lineTo(x, height - PAD_BOTTOM)
      ctx.stroke()
      ctx.setLineDash([])
    }

    /* ---- playhead synced to field playback ---- */
    const headX = xForTick(liveCursor)
    ctx.strokeStyle = COLORS.accent
    ctx.lineWidth = 2
    ctx.beginPath()
    ctx.moveTo(headX, PAD_TOP - 6)
    ctx.lineTo(headX, height - PAD_BOTTOM)
    ctx.stroke()
    // Head cap.
    ctx.beginPath()
    ctx.moveTo(headX - 4, PAD_TOP - 6)
    ctx.lineTo(headX + 4, PAD_TOP - 6)
    ctx.lineTo(headX, PAD_TOP - 1)
    ctx.closePath()
    ctx.fillStyle = COLORS.accent
    ctx.fill()
  }, [result, model, size, reduced])

  // While playing: one persistent RAF loop that sweeps the playhead. draw() reads the
  // LIVE store cursor, so this must NOT depend on `cursor` — depending on it would tear
  // the loop down and reschedule on every advance (~60×/s), cancelling the in-flight
  // draw before it paints and freezing the playhead until playback stops.
  useEffect(() => {
    if (!playing) return
    let raf = requestAnimationFrame(function loop() {
      draw()
      raf = requestAnimationFrame(loop)
    })
    return () => cancelAnimationFrame(raf)
  }, [playing, draw])

  // While paused: repaint once whenever the cursor (scrub), size, result or motion
  // preference changes, so scrubbing moves the playhead.
  useEffect(() => {
    if (playing) return
    draw()
  }, [playing, draw, cursor])

  // Click / drag to scrub.
  const scrubTo = useCallback(
    (clientX: number) => {
      const canvas = canvasRef.current
      if (!canvas || !result) return
      const rect = canvas.getBoundingClientRect()
      const cols = Math.max(1, result.timeline.frames.length)
      const plotW = Math.max(1, rect.width - PAD_LEFT - PAD_RIGHT)
      const colW = plotW / cols
      const rel = clientX - rect.left - PAD_LEFT
      const tick = rel / colW - 0.5
      setPlaying(false)
      setCursor(tick)
    },
    [result, setCursor, setPlaying]
  )

  const onPointerDown = (e: React.PointerEvent) => {
    if (!result) return
    e.currentTarget.setPointerCapture(e.pointerId)
    scrubTo(e.clientX)
  }
  const onPointerMove = (e: React.PointerEvent) => {
    if (!result || e.buttons === 0) return
    scrubTo(e.clientX)
  }

  return (
    <section className="flex h-full flex-col">
      <header className="mb-1.5 flex items-baseline justify-between gap-3">
        <h2 className="font-display text-sm font-semibold tracking-wide text-text-primary">
          {intl.formatMessage({ id: 'ribbon.title' })}
        </h2>
        <p className="truncate text-2xs uppercase tracking-wider text-text-muted">
          {intl.formatMessage({ id: 'ribbon.subtitle' })}
        </p>
      </header>
      <div
        ref={setRef}
        className="relative min-h-0 flex-1 overflow-hidden rounded-lg border border-hairline bg-panel"
      >
        <canvas
          ref={canvasRef}
          className="block h-full w-full cursor-col-resize touch-none"
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          role="slider"
          tabIndex={result ? 0 : -1}
          aria-label={intl.formatMessage({ id: 'ribbon.title' })}
          aria-valuemin={0}
          aria-valuemax={Math.max(0, (result?.timeline.frames.length ?? 1) - 1)}
          aria-valuenow={Math.round(cursor)}
        />
        {!result && (
          <div className="pointer-events-none absolute inset-0 flex items-center justify-center px-6 text-center text-sm text-text-muted">
            {intl.formatMessage({ id: 'ribbon.empty' })}
          </div>
        )}
      </div>
    </section>
  )
}

function roundRect(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number
): void {
  const rr = Math.min(r, w / 2, h / 2)
  ctx.beginPath()
  ctx.moveTo(x + rr, y)
  ctx.arcTo(x + w, y, x + w, y + h, rr)
  ctx.arcTo(x + w, y + h, x, y + h, rr)
  ctx.arcTo(x, y + h, x, y, rr)
  ctx.arcTo(x, y, x + w, y, rr)
  ctx.closePath()
}

function shortId(id: string): string {
  const m = id.match(/(\d+)\s*$/)
  return m ? `#${m[1]}` : id.slice(-3)
}
