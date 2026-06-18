/*
 * Dispatcher's console palette helpers.
 * Single source of truth for the agent hue ring + the structural colours used by
 * the canvas + ribbon (raw Canvas2D can't read CSS vars, so we mirror them here).
 */

/** Structural palette — mirrors the CSS vars / Tailwind theme. */
export const COLORS = {
  base: '#0B1220',
  panel: '#141C2B',
  hairline: '#25324A',
  textPrimary: '#C7D2E2',
  textMuted: '#6B7A93',
  accent: '#FFB020',
  danger: '#FF5C5C',
} as const

/*
 * Agent hue ring — assigned by `colorIndex % length`. The first 10 are the
 * specified palette; the remaining are tasteful extensions (same dispatcher
 * register: warm/cool alternation, no neon clashes) so up to 24 AGVs each read
 * as a distinct hue.
 */
export const AGENT_HUES: string[] = [
  '#FFB020',
  '#34D6E8',
  '#E85AAD',
  '#9BE870',
  '#7C9CFF',
  '#FF8A5C',
  '#C792EA',
  '#5CE1A8',
  '#F4D35E',
  '#6BD3FF',
  // tasteful extension to cover up to 24
  '#FF6FB5',
  '#8AE0C4',
  '#FFD27A',
  '#A0B4FF',
  '#5FD0C0',
  '#E0A6FF',
  '#FFA873',
  '#7CE0E8',
  '#B6E87A',
  '#FF9AC2',
  '#9FE0FF',
  '#D7B36B',
  '#86E89C',
  '#C0A0FF',
]

/** Solid hue for a given agent colour index. */
export function hueFor(colorIndex: number): string {
  const n = AGENT_HUES.length
  const i = ((colorIndex % n) + n) % n
  return AGENT_HUES[i]
}

/**
 * Hue with an alpha channel (0..1). Accepts a #RRGGBB hue and appends an 8-bit
 * alpha suffix — handy for trails (~0.18) and soft fills.
 */
export function withAlpha(hex: string, alpha: number): string {
  const a = Math.round(Math.max(0, Math.min(1, alpha)) * 255)
  const suffix = a.toString(16).padStart(2, '0')
  return `${hex}${suffix}`
}

/** Trail colour for an agent (hue at ~18% alpha). */
export function trailFor(colorIndex: number): string {
  return withAlpha(hueFor(colorIndex), 0.18)
}
