# ADR-F3 — Dock admission inserts at `FleetCoordinationLoop` via the existing `blockedResources` param (Round 2)

- Status: Accepted (design); implementation deferred to Round 2
- Date: 2026-06-19
- Deciders: TL / architecture (FMS-V1)

## Context

Once the station scheduler can grant/deny service windows, the fleet must actually *honour* a denial by keeping
the transit core open and not routing vehicles into a station that is closed for service. We need an insertion
point that does this **without** changing the frozen coordination seam
(`IFleetCoordinationCycle`/`ITrafficCoordinatorAppService`/`AllocationOutcome`) and **without** altering existing
behaviour when the FMS levers are off.

## Decision

`FleetCoordinationLoop.RunOnceAsync` will, **before** `RunCycleAsync`, consult an `IDockAdmissionController`
(introduced in Round 2) and feed any closed-station resources through the loop's **existing `blockedResources`
parameter**. No new parameter is added to the frozen cycle contract; admission rides the channel already there.

When admission is **off** (the default), the controller returns `(original goals, empty blocked)`, so the loop is
**byte-identical** to today — every FMS lever is opt-in / additive.

This ADR records the *design*; the V1 "Contracts" round delivers only the contracts (`IStationScheduler`,
`IStationResourceCalendar`) and does **not** modify `Coordination/` or `FleetCoordinationLoop`.

## Consequences

- The frozen Coordination seam is untouched; the change is a *consumer* of an existing parameter.
- The goal-filtering `IDockAdmissionController` (which references `AgentGoal`) is a Round 2 contract; keeping it
  out of V1 avoids a Coordination↔Dispatch reference cycle (see the note on `IStationScheduler`).
- Off-by-default guarantees the regression lock (discrete/continuous executor byte-identical behaviour) holds.
