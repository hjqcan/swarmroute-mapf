import { create } from 'zustand'
import { devtools } from 'zustand/middleware'
import { runSimulation } from '@/api/simulation'
import { HttpError } from '@/api/client'
import type { SimulationRequest, SimulationResult } from '@/types'

export type PlaybackSpeed = 0.5 | 1 | 2 | 4

/** Default form parameters for a run. Defaults to the SIPP planner: it is reservation-aware and reports dense
 *  physical standoffs honestly when they do not converge (switchable in the control rail). */
export const DEFAULT_PARAMS: SimulationRequest = {
  width: 10,
  height: 8,
  agvCount: 6,
  planner: 'Sipp',
  // Omit horizonWindowMs for the backend's unbounded whole-path SIPP default. The control rail sets a finite
  // value only when RHCR mode is selected.
  // Enable executor recovery for parked goal blockers; edge-collision safety is independent and always on.
  stepAside: true,
  // Enable v3 zone-local PIBT so high-density physical standoffs converge (toggle off in the rail to A/B).
  usePibt: true,
}

export interface SimState {
  /* ---- request form ---- */
  params: SimulationRequest
  setParam: <K extends keyof SimulationRequest>(key: K, value: SimulationRequest[K]) => void
  randomizeSeed: () => void

  /* ---- run lifecycle ---- */
  result: SimulationResult | null
  loading: boolean
  error: string | null
  /** Runs a simulation. An optional override is merged onto the form params (used by the auto-loop to pass the
   *  current AGV positions as `starts` so a continued run re-plans from where they are). */
  run: (override?: Partial<SimulationRequest>) => Promise<void>

  /* ---- auto-loop: when a run finishes playing, pick a new seed and run again, forever ---- */
  autoLoop: boolean
  setAutoLoop: (autoLoop: boolean) => void

  /* ---- per-agent route visibility on the field (ids whose route is hidden) ---- */
  hiddenPaths: Set<string>
  /** Toggle one agent's route on the field (its planned-path polyline + A/B markers). */
  togglePath: (agentId: string) => void

  /* ---- playback (a "frame cursor" is a float index into timeline.frames) ---- */
  playing: boolean
  speed: PlaybackSpeed
  /** Float cursor over frames; integer part = frame index, fraction = interpolation t. */
  cursor: number
  setPlaying: (playing: boolean) => void
  togglePlaying: () => void
  setSpeed: (speed: PlaybackSpeed) => void
  setCursor: (cursor: number) => void
  /** Advance the cursor by dt seconds at the current speed (called by the RAF loop). */
  advance: (dtSeconds: number) => void
}

/** Frames advance at this many ticks per real second at 1× speed. */
const TICKS_PER_SECOND = 2

/** How long to rest on a finished run before auto-looping to a fresh seed (ms). */
const LOOP_PAUSE_MS = 900

/** Pending auto-loop continuation timer. A manual run cancels it so a click is never raced by the loop. */
let pendingLoopTimer: ReturnType<typeof setTimeout> | null = null

function frameCount(result: SimulationResult | null): number {
  return result?.timeline.frames.length ?? 0
}

/**
 * The final-frame position of each AGV in agent-index order (agv-1 … agv-N), used to CONTINUE a lifelong run:
 * the next run keeps these as starts and re-plans new goals, so AGVs carry on from where they are instead of
 * teleporting. Returns null when the timeline is empty or any agent's position is missing (→ fresh layout).
 */
function finalPositions(result: SimulationResult | null, agvCount: number): string[] | null {
  const frames = result?.timeline.frames
  if (!frames || frames.length === 0) return null
  const byId = new Map(frames[frames.length - 1].positions.map((p) => [p.agentId, p.siteId]))
  const out: string[] = []
  for (let i = 1; i <= agvCount; i++) {
    const pos = byId.get(`agv-${i}`)
    if (pos == null) return null
    out.push(pos)
  }
  return out
}

export const useSimStore = create<SimState>()(
  devtools(
    (set, get) => ({
      params: { ...DEFAULT_PARAMS },
      setParam: (key, value) => set((s) => ({ params: { ...s.params, [key]: value } })),
      randomizeSeed: () =>
        set((s) => ({ params: { ...s.params, seed: Math.floor(Math.random() * 100_000) } })),

      result: null,
      loading: false,
      error: null,
      run: async (override) => {
        // Cancel any pending auto-loop continuation so a (manual) run is never overwritten by a stale loop tick.
        if (pendingLoopTimer != null) {
          clearTimeout(pendingLoopTimer)
          pendingLoopTimer = null
        }
        const params = { ...get().params, ...override }
        // Log the EXACT request (incl. continuation `starts`) — the only thing that reproduces a run. With the
        // auto-loop a run is seed + starts, so the seed field alone can't replay it; copy this JSON to reproduce.
        // eslint-disable-next-line no-console
        console.info('[swarmroute] run request (copy to reproduce):', JSON.stringify(params))
        set({ loading: true, error: null })
        try {
          const result = await runSimulation(params)
          set({
            result,
            loading: false,
            error: null,
            cursor: 0,
            // hiddenPaths is intentionally preserved across runs: a re-run keeps each
            // AGV's shown/hidden choice so the operator stays focused on the same AGVs.
            // Auto-play a successful run so the verification is immediately visible.
            playing: result.timeline.frames.length > 1,
          })
        } catch (err) {
          const message =
            err instanceof HttpError
              ? err.detail
                ? `${err.message}：${err.detail}`
                : err.message
              : (err as Error)?.message || '运行失败 / Run failed'
          set({ loading: false, error: message, playing: false })
        }
      },

      hiddenPaths: new Set<string>(),
      togglePath: (agentId) =>
        set((s) => {
          // New Set on every toggle so subscribers (the canvas) see a fresh reference and repaint.
          const next = new Set(s.hiddenPaths)
          if (next.has(agentId)) next.delete(agentId)
          else next.add(agentId)
          return { hiddenPaths: next }
        }),

      autoLoop: false,
      setAutoLoop: (autoLoop) => set({ autoLoop }),

      playing: false,
      speed: 1,
      cursor: 0,
      setPlaying: (playing) => set({ playing }),
      togglePlaying: () => set((s) => ({ playing: !s.playing })),
      setSpeed: (speed) => set({ speed }),
      setCursor: (cursor) => {
        const max = Math.max(0, frameCount(get().result) - 1)
        set({ cursor: Math.max(0, Math.min(max, cursor)) })
      },
      advance: (dtSeconds) => {
        const { result, speed, cursor, playing } = get()
        if (!playing || !result) return
        const max = frameCount(result) - 1
        if (max <= 0) return
        const next = cursor + dtSeconds * TICKS_PER_SECOND * speed
        if (next >= max) {
          // Reached the end: settle on the final frame and pause.
          set({ cursor: max, playing: false })
          // Auto-loop: rest briefly on the finished run, then CONTINUE — keep each AGV at its current pose and
          // give it a new goal (new seed) instead of teleporting to a fresh random layout. The seed input is
          // bound to params.seed, so randomizeSeed() updates the UI. The toggle is re-checked at fire time so
          // turning it off (or a run already in flight) cleanly ends the loop.
          if (get().autoLoop) {
            pendingLoopTimer = setTimeout(() => {
              pendingLoopTimer = null
              const s = get()
              if (!s.autoLoop || s.loading) return
              const starts = finalPositions(s.result, s.params.agvCount)
              s.randomizeSeed()
              void s.run(starts ? { starts } : undefined)
            }, LOOP_PAUSE_MS)
          }
        } else {
          set({ cursor: next })
        }
      },
    }),
    { name: 'simStore' }
  )
)
