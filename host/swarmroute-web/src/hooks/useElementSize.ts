import { useCallback, useLayoutEffect, useRef, useState } from 'react'

export interface Size {
  width: number
  height: number
}

/**
 * Resize-aware element measurement via ResizeObserver. Returns a ref-setter to
 * attach to the element plus its current content-box size in CSS pixels.
 */
export function useElementSize<T extends HTMLElement>(): [(node: T | null) => void, Size] {
  const [size, setSize] = useState<Size>({ width: 0, height: 0 })
  const observerRef = useRef<ResizeObserver | null>(null)

  const setRef = useCallback((node: T | null) => {
    observerRef.current?.disconnect()
    if (!node) return
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (!entry) return
      const { width, height } = entry.contentRect
      setSize({ width, height })
    })
    observer.observe(node)
    observerRef.current = observer
    // Seed an initial measurement synchronously.
    const rect = node.getBoundingClientRect()
    setSize({ width: rect.width, height: rect.height })
  }, [])

  useLayoutEffect(() => () => observerRef.current?.disconnect(), [])

  return [setRef, size]
}
