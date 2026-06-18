/*
 * Canvas helpers shared by FieldCanvas and ReservationRibbon: HiDPI sizing and a
 * grid -> pixel projector that maps site (x=col, y=row) into the drawable area.
 */

/**
 * Size a canvas's backing store for the device pixel ratio and return the 2D
 * context already scaled so all drawing can use CSS pixels. Returns null if the
 * element has no usable size yet.
 */
export function setupHiDpiCanvas(
  canvas: HTMLCanvasElement,
  cssWidth: number,
  cssHeight: number
): CanvasRenderingContext2D | null {
  if (cssWidth <= 0 || cssHeight <= 0) return null
  const dpr = Math.max(1, Math.min(3, window.devicePixelRatio || 1))
  const bw = Math.round(cssWidth * dpr)
  const bh = Math.round(cssHeight * dpr)
  if (canvas.width !== bw) canvas.width = bw
  if (canvas.height !== bh) canvas.height = bh
  const ctx = canvas.getContext('2d')
  if (!ctx) return null
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.clearRect(0, 0, cssWidth, cssHeight)
  return ctx
}

export interface GridProjector {
  toX: (col: number) => number
  toY: (row: number) => number
  /** Pixel spacing between adjacent grid columns/rows (min of the two). */
  cell: number
}

/**
 * Build a projector that fits a `cols × rows` grid (0-indexed control points)
 * into `width × height` CSS pixels with uniform spacing and a margin, centred.
 */
export function makeProjector(
  cols: number,
  rows: number,
  width: number,
  height: number,
  margin: number
): GridProjector {
  const usableW = Math.max(1, width - margin * 2)
  const usableH = Math.max(1, height - margin * 2)
  // Spacing between control points (n points => n-1 gaps; guard the 1-point case).
  const gapX = cols > 1 ? usableW / (cols - 1) : 0
  const gapY = rows > 1 ? usableH / (rows - 1) : 0
  const cell = Math.max(2, Math.min(gapX || usableH, gapY || usableW))

  // Centre the grid within the usable area.
  const gridW = cols > 1 ? cell * (cols - 1) : 0
  const gridH = rows > 1 ? cell * (rows - 1) : 0
  const offsetX = margin + (usableW - gridW) / 2
  const offsetY = margin + (usableH - gridH) / 2

  return {
    toX: (col: number) => offsetX + col * cell,
    toY: (row: number) => offsetY + row * cell,
    cell,
  }
}
