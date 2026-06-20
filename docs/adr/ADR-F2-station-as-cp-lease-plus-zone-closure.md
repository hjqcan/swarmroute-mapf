# ADR-F2 — A station is a CP lease + a Zone `blockingClosure`; NO `ResourceKind.Station`, Kernel stays frozen

- Status: Accepted
- Date: 2026-06-19
- Deciders: TL / architecture (FMS-V1)

## Context

An FMS station service is a *long* occupation: a vehicle holds the dock point for the whole service window and,
depending on the station, also severs or degrades the surrounding transit topology. The naive modelling move is
to add a new `ResourceKind.Station` to the `SpatioTemporal.Kernel`. But the Kernel
(`ResourceRef`/`ResourceKind{CP,Lane,Block,Zone}`/`TimeInterval`/`SpaceTimePath`/`IReservationView`) is the
**frozen cross-context contract** (ADR-001); changing it forces every bounded context to re-validate.

## Decision

Model a station with the **existing** Kernel vocabulary, with **zero Kernel/domain changes**:

- the **dock point** is a long lease over one `ResourceRef(ResourceKind.CP, dockPointId)`, and
- the **blocking closure** is a long lease over the station's `BlockingClosure` — an `IReadOnlySet<ResourceRef>`
  (typically `ResourceKind.Zone` / the severed transit core), held for the same `[start, start+serviceMs)`
  window.

Both leases are granted through the existing `ReservationTable` grant + closure machinery (exploration confirmed
**zero domain changes** are required). `StationDefinition` carries `DockPoint` + `BlockingClosure`; the
`IStationResourceCalendar` seam expresses the window as a single half-open `TimeInterval` and grants it only when
the whole closure is free.

`ResourceKind.Station` is explicitly recorded as a **deferred fallback** — to be added only if a future calendar /
observability need demands an explicit type. It is a pure-append, low-risk change, and is **not** adopted in
V1–V3.

## Consequences

- The Kernel and `ReservationTable` stay frozen; the FMS station feature is additive on top of them.
- A `HardBlocking` station's `BlockingClosure` spans the severed transit core; a `NonBlocking` station's closure
  is effectively just the dock point. The blocking semantics live in data (the closure set), not in new code.
- Station leasing reuses the same conflict/closure-free guarantees the reservation table already provides — no
  parallel reservation path.
