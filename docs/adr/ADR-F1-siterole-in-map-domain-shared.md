# ADR-F1 — `SiteRole` lives in `Map.Domain.Shared`, additive

- Status: Accepted
- Date: 2026-06-19
- Deciders: TL / architecture (FMS-V1)

## Context

The FMS Dispatch layer needs to reason about the operational role of each roadmap site — where vehicles pass
through, queue, dock, service, park and charge. The existing `MapSiteType` enum captures the map-editor's
authoring classification (`CPSite`/`WorkSite`/`RelaySite`/`AvoidSite`/`DockSite`) but not the FMS dispatch
semantics (e.g. a *pre-dock buffer* distinct from a generic buffer, or a *transit-core* waypoint that must stay
endpoint-free).

## Decision

Introduce `enum SiteRole { Transit, Workstation, Parking, Charger, Buffer, PreDockBuffer, DockPoint }` in
**`Map/SwarmRoute.Map.Domain.Shared/Enums/`**, alongside the existing `MapSiteType`. It **complements** rather
than replaces `MapSiteType`: a physical site carries both an authoring type and an FMS role.

The change is **purely additive** — it adds a new enum file only. The `MapSite` aggregate is **not** touched in
this round. When wiring the role onto the aggregate later (Foundations phase), `MapSite` gains an optional
constructor parameter defaulting to `SiteRole.Transit` (backward compatible, aggregate invariants preserved,
EF adds a string column with a default-value migration).

## Consequences

- No existing code path changes; nothing reads `SiteRole` yet.
- The Dispatch context can speak in FMS site roles without depending on the Map application/EF layers.
- Placing it in `Map.Domain.Shared` (not `Dispatch.Domain.Shared`) keeps site taxonomy owned by the Map context
  and avoids a Map→Dispatch reference when the role is later attached to `MapSite`.
