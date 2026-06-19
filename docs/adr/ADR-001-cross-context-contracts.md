# ADR-001 — Cross-context contracts are frozen in the SpatioTemporal Kernel

> _Superseded: the reactive deadlock-resolution flow described here was replaced by the `Liveness` context (grant-time prevention + a synchronous standoff-resolution policy). See `Liveness/README.md`._

- Status: Accepted
- Date: 2026-06-18
- Deciders: TL / architecture

## Context

MAPF couples planning, reservation and deadlock handling tightly. If each bounded context defined its own
spatio-temporal vocabulary, the four contexts (Map / PathPlanning / TrafficControl / Deadlock) plus
Coordination would drift and the seams would become chatty/anemic. Parallel squads also need a stable set of
signatures to develop against from sprint 1.

## Decision

A single shared kernel, **`Shared/SwarmRoute.SpatioTemporal.Kernel`**, defines the cross-context vocabulary as
pure types (no behaviour, no state): `ResourceRef`, `TimeInterval` (half-open `[start,end)`), `SpaceTimeCell`,
`SpaceTimePath`, `SafeInterval`, `IReservationView`, `ResourceAllocationGraphSnapshot`, `IFleetClock`.

The bounded-context seams are frozen interfaces, owned as follows:

| Contract | Lives in | Producer → Consumer |
|---|---|---|
| `IRoadmapQueryService` (returns cached `RoadmapGraph`) | `Map.Application.Contract` | Map → Planning/Coordination |
| `IReservationQuery` / `IReservationView` | Kernel + `PathPlanning.Domain` | TrafficControl → PathPlanning |
| `ITrafficCoordinatorAppService.TryReserve/Release` → `AllocationOutcome` | `TrafficControl.Application.Contract` | Coordination → TrafficControl |
| `ITrafficControlSnapshotProvider` / `ResourceAllocationGraphSnapshot` | Kernel + TrafficControl | TrafficControl → Deadlock |
| Integration events `Map.Roadmap.Published`, `TrafficControl.Allocation.Contended`, `Deadlock.Case.ResolutionRequested/Resolved/Escalated` | EventBus contract | publishers → subscribers |

**Rule of thumb:** within one planning cycle = in-process interface; crossing a cycle / triggering another
subsystem's cadence = CAP integration event. This keeps the inner plan↔reserve loop free of message-bus latency
while preserving a microservice-ready event seam.

Cross-context event payloads cross as versioned DTOs via `IIntegrationDtoConverter` (not raw domain events), so
the wire contract is versioned independently of the domain model.

## Consequences

- v1 (SIPP) has been delivered as a strategy swap behind `IPathPlanner` + `SafeIntervals()` — the contracts did
  not change.
- Coordination consumes `Deadlock.Case.ResolutionRequested` to redirect the victim; Deadlock stays a pure
  analyser (see ADR-003).
- A contract change requires a new ADR + notice to all squads.
