import { post } from './client'
import type { SimulationRequest, SimulationResult } from '@/types'

/**
 * Run a multi-AGV simulation on the backend engine and return the full result
 * (field + per-agent plans + tick-by-tick timeline + stats).
 *
 * The endpoint returns the DTO directly as camelCase JSON, so `post` already
 * yields the typed `SimulationResult` — no { code, msg, data } unwrap.
 */
export function runSimulation(req: SimulationRequest): Promise<SimulationResult> {
  return post<SimulationResult>('/simulation/run', req)
}
