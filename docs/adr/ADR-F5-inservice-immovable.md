# ADR-F5 — `InService` ⇒ `MobilityClass.ImmovableUntilServiceComplete`, never relocated (Round 2)

- Status: Accepted (design); enforcement deferred to Round 2
- Date: 2026-06-19
- Deciders: TL / architecture (FMS-V1)

## Context

The Liveness layer resolves contention by relocating vehicles (cluster formation, advance, parked relocation). A
vehicle that is **docked and performing a station service** must not be relocated mid-service: moving it would
abandon work, and physically it holds the dock point and (for a blocking station) its closure for the whole
window. The relocation machinery needs an unambiguous, shared signal to skip such vehicles.

## Decision

A vehicle in `AgvMissionState.InService` carries `MobilityClass.ImmovableUntilServiceComplete`. Per ADR-F2 its
service window is already a long lease over the dock-point CP + blocking closure, so the executor treats an
`InService` vehicle as a **hard obstacle + long lease**.

Round 2 enforces this: the `ParkedRelocationSelector` and the `LivenessPolicy` (ClusterFormation / Advance) skip
any vehicle whose `MobilityClass` is `ImmovableUntilServiceComplete` (or `Faulted`). Only `Movable` and
`MovableWithCost` vehicles are relocation candidates.

The V1 "Contracts" round delivers only the `MobilityClass` / `AgvMissionState` enums; it does **not** modify
`Liveness/`, `Simulation/`, or any relocation code.

## Consequences

- `MobilityClass` becomes the single relocation-eligibility gate, decoupling Liveness from FMS mission details.
- In-service vehicles are guaranteed undisturbed for the service window, which the reservation/closure model
  already backs.
- `Faulted` rides the same gate (also never relocated, routed around), so disabled vehicles get correct treatment
  for free.
