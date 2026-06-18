# ADR-003 — Loop cadence is event-driven + watchdog tick; re-planning is deterministic

- Status: Accepted
- Date: 2026-06-18
- Deciders: TL / Coordination / Algo

## Context

A lifelong MAPF driver needs a defined trigger model and deterministic re-planning, or it livelocks (R6): two
agents endlessly swap who yields, or a victim oscillates between the same avoidance points.

## Decision

**Cadence.** The Coordination loop is **event-driven with a watchdog tick**:
- reacts to new goals, `TrafficControl.Allocation.Contended`, and `Deadlock.Case.ResolutionRequested`;
- a low-frequency `FleetCoordinationLoop` (`IHostedService`) watchdog tick re-checks for stragglers.
Deadlock detection is reactive (triggered by `Allocation.Contended` via a subscriber), never polled in the inner
loop. Recovery (`ConfirmCleared → Recover → Resolved`) is pumped once per execution tick by the driver, outside
any `TryReserve` publish, so it never nests inside contention handling.

**Determinism.** Goals are processed in a stable order: **`Priority` → `HadWaitedTime` (aging) → ordinal
`agentId`** — the same `RightOfWay` tie-break TrafficControl uses, so a given input always serialises the same
way. Aging on `HadWaitedTime` provides anti-starvation (I7).

**Anti-livelock (deadlock resolution).** Two guards:
1. *No repeat point* — `AntiLivelockAvoidancePointSelector` will not hand a victim the same avoidance site twice
   in a row when an alternative exists (it falls back to repeating only if that siding is the sole option).
2. *Strict progress* — the executor only re-redirects a victim if its graph distance to its **original** goal
   strictly decreased since the last redirect, bounded by an attempt cap; otherwise the case is escalated as a
   `Livelock` (`Deadlock.Case.Escalated`) and the victim is no longer redirected (it parks; the run reports
   `DidNotConverge` rather than spinning forever).

## Consequences

- Reproducible runs (no RNG / wall-clock in the loop body; the simulation uses a `ManualFleetClock` whose
  `NowMs == tick` so reserved intervals share the executor's axis).
- v2 will introduce a rolling-horizon (RHCR) window; the cadence contract here is forward-compatible.
- High-density infeasible scenarios surface as `DidNotConverge` (a liveness/throughput limit addressed by v1
  SIPP), never as a collision or a hang.
