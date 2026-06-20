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

/** Trail colour for an agent (hue at ~32% alpha — visible as a distinct planned-path line, still soft). */
export function trailFor(colorIndex: number): string {
  return withAlpha(hueFor(colorIndex), 0.32)
}

/*
 * (FMS) Site-role render colours, keyed by the backend `SiteRole` name. The canvas draws each role distinctly
 * (workstation = filled square, parking = 'P' tile, buffers = hollow tile, dock = bold ringed, charger = '⚡',
 * transit = plain node) using these structural hues — they read as facility geometry, not as agent hues.
 */
export const SITE_ROLE_COLORS: Record<string, string> = {
  Workstation: '#7C9CFF', // a cool indigo — a place where work happens
  Parking: '#5CE1A8', // calm green — a safe resting slot
  Charger: '#F4D35E', // amber/yellow — energy
  Buffer: '#6B7A93', // muted gray — staging
  PreDockBuffer: '#34D6E8', // cyan — the gate just upstream of a dock
  DockPoint: '#E85AAD', // magenta — the service point itself
  Transit: '#25324A', // hairline-ish — plain through-traffic node
}

/** (FMS) The render colour for a site role, or the transit/hairline default when unknown. */
export function siteRoleColor(role: string | undefined): string {
  return (role && SITE_ROLE_COLORS[role]) || SITE_ROLE_COLORS.Transit
}

/*
 * (FMS) Mission-state agent colours, keyed by the backend `AgvMissionState` name. An FMS run colours each AGV by what
 * its mission is doing rather than by its hue: in-service is locked red, docking/waiting amber, moving-to/parked gray,
 * and the normal motion-state colour (the agent hue) is used for everything else (return null → caller falls back).
 */
const MISSION_COLORS: Partial<Record<string, string>> = {
  InService: '#FF5C5C', // locked / occupying a dock — danger red
  Docking: '#FFB020', // transitioning onto the dock — amber
  WaitingDockAdmission: '#FFB020', // held at the buffer awaiting admission — amber
  MovingToParking: '#6B7A93', // clearing out of the way — gray
  IdleParked: '#6B7A93', // resting — gray
  Faulted: '#FF5C5C', // disabled — danger red
}

/** (FMS) The mission-state colour for an AGV, or `null` when the mission carries no override (the caller then uses the
 *  agent's normal motion-state colour). Undefined mission (non-FMS run) returns null too. */
export function missionColor(mission: string | undefined): string | null {
  if (!mission) return null
  return MISSION_COLORS[mission] ?? null
}
