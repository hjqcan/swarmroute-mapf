# ADR-F4 — `ScenarioMode` is a 3rd orthogonal axis, separate from `ScenarioKind` and `AssignmentPolicy`

- Status: Accepted
- Date: 2026-06-19
- Deciders: TL / architecture (FMS-V1)

## Context

The simulation already has two independent scenario axes:

| Axis | Type | Controls | Status |
|---|---|---|---|
| Map layout | `ScenarioKind { Open, Bottleneck, Obstacles }` | obstacles / walls / shelves | existing |
| Endpoint assignment | `AssignmentPolicy { Random, … }` | how start/goal are drawn | existing |

FMS adds a *task-lifecycle / arrival-semantics* concern (clear-to-parking vs disappear, lifelong task streams,
per-mode acceptance criteria). The temptation is to overload `ScenarioKind` or `AssignmentPolicy`; that would
conflate orthogonal concerns and break the regression lock on those existing enums.

## Decision

Introduce **`ScenarioMode { RandomStress, WarehouseWellFormed, LifelongDispatch }`** as a **third, orthogonal**
axis (it lives in `Simulation.Application`, owned by the Simulation squad — *not* created in this Contracts
round). It does **not** modify `ScenarioKind` or `AssignmentPolicy`.

A `WellFormedEndpointGenerator` is realised as a new `AssignmentPolicy.WellFormed` value, *orchestrated by*
`ScenarioMode`; an `ArrivalPolicy` (`Disappear` / `PermanentPark` / `ClearToParking`) controls post-arrival
behaviour. Stress testing can use `Disappear`; realistic FMS runs use `ClearToParking`.

This ADR records the axis decision only; the V1 "Contracts" round does **not** touch `Simulation/`.

## Consequences

- The three axes compose freely (layout × assignment × lifecycle), so existing scenarios keep their exact
  behaviour when `ScenarioMode` defaults to the stress mode.
- Per-mode metrics and acceptance criteria attach to `ScenarioMode`, keeping "high completion-rate" and "stress"
  evaluations cleanly separated.
- No existing enum gains a member that would shift existing serialized/selected values.
