import { create } from 'zustand'
import { devtools } from 'zustand/middleware'
import { runSimulation } from '@/api/simulation'
import { HttpError } from '@/api/client'
import type { SimulationRequest, SimulationResult } from '@/types'

export type PlaybackSpeed = 0.5 | 1 | 2 | 4

/** Default form parameters for a run. */
export const DEFAULT_PARAMS: SimulationRequest = {
  width: 10,
  height: 8,
  agvCount: 6,
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
  run: () => Promise<void>

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

function frameCount(result: SimulationResult | null): number {
  return result?.timeline.frames.length ?? 0
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
      run: async () => {
        const { params } = get()
        set({ loading: true, error: null })
        try {
          const result = await runSimulation(params)
          set({
            result,
            loading: false,
            error: null,
            cursor: 0,
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
        } else {
          set({ cursor: next })
        }
      },
    }),
    { name: 'simStore' }
  )
)
