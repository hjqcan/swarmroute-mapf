import { useEffect, useRef } from 'react'
import { useSimStore } from '@/store/simStore'

/**
 * Drives playback: a single requestAnimationFrame loop that, while playing,
 * advances the store's frame cursor by real elapsed time. Mount this once
 * (in the page) — FieldCanvas and ReservationRibbon both read `cursor`, so they
 * animate from one shared clock and stay in lockstep.
 *
 * When reduced motion is requested the caller passes snap=true; we still tick the
 * cursor forward (so the run plays), the renderers just don't blend between frames.
 */
export function usePlaybackLoop(): void {
  const playing = useSimStore((s) => s.playing)
  const advance = useSimStore((s) => s.advance)
  const rafRef = useRef<number | null>(null)
  const lastRef = useRef<number | null>(null)

  useEffect(() => {
    if (!playing) {
      lastRef.current = null
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current)
      rafRef.current = null
      return
    }

    const tick = (now: number) => {
      const last = lastRef.current
      lastRef.current = now
      if (last != null) {
        // Clamp dt to avoid a huge jump after a tab is backgrounded.
        const dt = Math.min(0.1, (now - last) / 1000)
        advance(dt)
      }
      rafRef.current = requestAnimationFrame(tick)
    }

    rafRef.current = requestAnimationFrame(tick)
    return () => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current)
      rafRef.current = null
      lastRef.current = null
    }
  }, [playing, advance])
}
