# ADR-002 — Reservation state is in-memory authoritative; EF is snapshot/audit only

- Status: Accepted
- Date: 2026-06-18
- Deciders: TL / TrafficControl

## Context

`ReservationTable` is the fleet's single real-time allocation state and the hottest mutable aggregate: it changes
every control cycle. Routing every mutation through `BaseDbContext.Commit()` → PostgreSQL would not keep up with
the loop, and would make the inner plan↔reserve path depend on the database.

## Decision

- The **authoritative `ReservationTable` is an in-memory singleton** guarded by `StateVersion` (optimistic
  concurrency). The hot path (`TryReserve` / `Release` / conflict + closure checks) takes **zero** EF dependency.
- **EF (`TrafficControlDbContext`) is used only for snapshot + audit**, for crash recovery and forensics — never
  as the per-tick source of truth. A periodic Hangfire job (`ReservationSnapshotJob`) writes
  `ReservationAuditRecord` rows (state version + lease count + a JSONB lease blob); optional restore-on-boot
  (`ReservationTable.RehydrateFrom`, gated by `RunSnapshotRestoreOnStartup`, default off) rebuilds the singleton
  from the latest snapshot.
- Domain/integration events still flow through the normal dispatch + publish pipeline; only the *persistence* of
  table state is special. TrafficControl's `Commit()` semantics therefore differ from other contexts and are
  documented in code.

## Consequences

- The single `ReservationTable` aggregate serialises all fleet allocation through one `StateVersion` — acceptable
  to start; sharding by `MapBlock`/zone is deferred (keep `ResourceLease` independently addressable so sharding is
  later, not a rewrite). See R4.
- Snapshots are eventually-consistent with the in-memory truth (written on a cadence), which is the correct
  trade-off for crash recovery: losing the last few seconds of leases on a crash is fine; the fleet re-plans.
