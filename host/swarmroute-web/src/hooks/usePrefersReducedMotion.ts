import { useEffect, useState } from 'react'

/**
 * Tracks the user's `prefers-reduced-motion` setting (live). When true, the app
 * snaps to discrete frames and drops ambient motion (quality-floor requirement).
 */
export function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return false
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches
  })

  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return
    const mql = window.matchMedia('(prefers-reduced-motion: reduce)')
    const onChange = () => setReduced(mql.matches)
    mql.addEventListener('change', onChange)
    return () => mql.removeEventListener('change', onChange)
  }, [])

  return reduced
}
