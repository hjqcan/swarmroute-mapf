# Map / Resources (資源・地圖)

*The bounded context that owns the fleet's static roadmap — the durable graph of control points (站点) and directed lanes (路段) that every other context plans, reserves and drives over.*

---

## 1. Purpose & bounded-context responsibility

The Map context is the **single source of truth for the static topology** of a fleet's working area. It owns:

- The **roadmap aggregate** — a named, versioned set of sites, directed lines and mutual-exclusion blocks (`Roadmap` at `Map/SwarmRoute.Map.Domain/Aggregates/Roadmap.cs:23`).
- The **built read model** — an in-memory directed-weighted graph (`RoadmapGraph` at `Map/SwarmRoute.Map.Domain/ValueObjects/RoadmapGraph.cs:23`) that downstream planners run Dijkstra/SIPP over.
- The **import / publish lifecycle** — validate a topology, persist it, and announce it via integration events.
- **Static interference geometry** — which sites/lines physically overlap (`InterferenceCalculator`, `InterferenceSet`).

It explicitly does **NOT** own:

| Concern | Owner |
|---|---|
| Reservations / locks / occupancy (`Locked`/`OccupiedBy`) | **TrafficControl** (reservation table) |
| Path search / agent plans | **PathPlanning** |
| The per-tick coordination loop, goal selection | **Coordination** |
| Per-agent blacklists, runtime resource state | **TrafficControl / Host** (runtime) |
| Wait-for-cycle prevention/detection + physical-standoff policy | **Liveness** |

This split is deliberate and load-bearing. The original first-generation engine fused topology with live occupancy on `MapSite`/`MapResource`; this port keeps only the *static* fields here. `MapSite` (`Map/SwarmRoute.Map.Domain/Entities/MapSite.cs:13`) carries type, pose, enabled flag and interference references — **no** dynamic lock state. Even the ported `MapResourceStatus` enum documents that `Locked`/`Belong` are retained for import fidelity only; the authoritative copy lives in TrafficControl (`Map/SwarmRoute.Map.Domain.Shared/Enums/MapResourceStatus.cs:13`).

The context consolidates **three** duplicated upstream `GraphMap` implementations (`AJR.MAPF.Map`, `AJR.Platform.GraphMapDP`, …) into one aggregate, and fixes two source bugs along the way: the `MapSiteType` duplicate-value collision and the `MapLine.Distince` typo (see §3, §8).

---

## 2. Layers & projects

Standard grukirbs-style DDD onion. Dependencies point **inward**; the API and IoC sit on top.

```
Domain.Shared  ←  Domain  ←  Application.Contract  ←  Application  ←  Infra.CrossCutting.IoC  ←  Api
                    ↑                                       ↑              ↑
                    └──────────── Infra.Data ───────────────┴──────────────┘
```

| Project | Role |
|---|---|
| `SwarmRoute.Map.Domain.Shared` | Leaf: enums (`MapSiteType`, `MapLineType`, `MapResourceStatus`) and `MapErrorCodes` (`MAP-001..008`). No dependencies. |
| `SwarmRoute.Map.Domain` | The model: `Roadmap` aggregate, `MapSite`/`MapLine`/`MapBlock` entities, `MapPosition`/`RoadmapGraph`/`InterferenceSet` value objects, domain services (`IRoadmapGraphFactory`, `IInterferenceCalculator`), `IRoadmapRepository`, integration events. References `SwarmRoute.SpatioTemporal.Kernel`, `SwarmRoute.Domain.Abstractions`, `NetDevPack`, and the vendored `SwarmRoute.Algorithms` graph library. |
| `SwarmRoute.Map.Application.Contract` | The published surface: `IMapAppService`, **`IRoadmapQueryService`** (the cross-context read seam), and transport DTOs. References `Map.Domain` *on purpose* so the returned `RoadmapGraph` VO is visible to consumers (see the comment at `Map/SwarmRoute.Map.Application.Contract/SwarmRoute.Map.Application.Contract.csproj`). |
| `SwarmRoute.Map.Application` | `MapAppService` (import/read/publish/delete), `RoadmapGraphProvider` (cached read seam impl), `RoadmapFactory` (DTO→domain via validating ctors), `MapMappingProfile` (domain→DTO via AutoMapper), and the `RoadmapPublishedCacheInvalidator` event handler. |
| `SwarmRoute.Map.Infra.Data` | `MapDbContext` (EF Core + Npgsql), `RoadmapRepository`, the `InitialCreate` migration, and the design-time `MapDbContextFactory`. |
| `SwarmRoute.Map.Infra.CrossCutting.IoC` | `MapNativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)` — the composition root. |
| `SwarmRoute.Map.Api` | `MapsController` + a thin standalone `Program.cs`. In production the controller is hosted by `SwarmRoute.Host` as an MVC application part. |
| `SwarmRoute.Map.Tests` | xUnit; references only `Map.Domain` (pure unit tests, no DB). |

Note on layering completeness: there is **no `Application.Authorization`, no `HttpApi.Client`, no separate `EntityFrameworkCore` config-assembly** — EF mapping lives directly in `MapDbContext.OnModelCreating`. This is appropriate for a context whose write side is a low-frequency import/publish flow.

---

## 3. Domain model

### Aggregate: `Roadmap`

`Roadmap` (`Map/SwarmRoute.Map.Domain/Aggregates/Roadmap.cs:23`) is the only aggregate root. Sites, lines and blocks are **entities inside the boundary** — only ever mutated through the root. The aggregate id is the EF surrogate `Entity.Id` (Guid); topology ids (`SiteId`, `LineId`, `BlockId`) are *separate* stable string keys used by the graph and by cross-context `ResourceRef`s.

**Invariants** (enforced in the constructor and in `ReplaceTopology`, both routing through `Validate` at `:153`; violations throw `ArgumentException` carrying a `MAP-xxx` code):

1. At least one site (`MAP-001`).
2. Site ids unique (`MAP-002`); line ids unique (`MAP-003`).
3. Every line's `StartStationId`/`EndStationId` resolves to a site — no dangling endpoints (`MAP-004`).
4. Every block's contained site/line ids resolve to members — no dangling members (`MAP-005`).

`StateVersion` is an optimistic-concurrency counter (EF concurrency token) bumped on every edit; `ReplaceTopology` validates the *new* set before clearing the old, so a rejected replace leaves the aggregate untouched (proved by a test). `MarkImported()`/`MarkPublished()` enqueue the integration events (§4). `Rename`, `CheckVersion`, `FindSite`, `FindLine` round out the API.

### Value object: `RoadmapGraph`

`RoadmapGraph` (`Map/SwarmRoute.Map.Domain/ValueObjects/RoadmapGraph.cs:23`) wraps a `DirectedWeightedSparseGraph<string>` from the vendored `SwarmRoute.Algorithms` library. It is an immutable structural-equality VO and **the** in-process read model. Build rules (`Build` at `:37`, mirroring `GraphMap.Init`):

- **Vertices** = ids of **enabled** sites (`s.Enable`). Disabled sites are excluded.
- **Edges** = enabled lines, directed `StartStationId → EndStationId`. A line whose endpoint is not a vertex is skipped defensively (e.g. it pointed at a disabled site).
- **Weight** = `round(Distance * 1000)` (`WeightScale = 1000`, `MidpointRounding.AwayFromZero`). Distance is in metres; the ×1000 keeps the vendored integer-weight Dijkstra exact at millimetre resolution.
- **Zero-length clamp**: the vendored graph treats weight `0` as "no edge", so a degenerate zero-distance line is clamped to weight `1` to keep the edge.

Query surface (all by topology `SiteId`):

| Member | Meaning |
|---|---|
| `VertexCount` / `EdgeCount` / `Vertices` | Graph size & vertex set |
| `HasSite(id)` / `HasLine(from,to)` | Membership |
| `Neighbours(siteId)` | Out-successors (one directed hop); empty if unknown |
| `EdgeWeight(from,to)` | Scaled weight, or `null` |
| `DistanceTo(start,end)` | Dijkstra shortest-path **cost**, or `null` if absent/unreachable (mirrors `GraphMap.DistanceTo`) |
| `ShortestPath(start,end)` | Ordered inclusive site-id list, or `null`; trivial `[start]` when `start == end` |

Kernel-interop helpers map graph elements to the **frozen** `ResourceRef` contract (`Shared/SwarmRoute.SpatioTemporal.Kernel/ResourceRef.cs:30`):

- `SiteRef(siteId)` → `ResourceRef(ResourceKind.CP, siteId)`
- `LaneId(from,to)` → the `"{from}-{to}"` convention
- `LaneRef(from,to)` → `ResourceRef(ResourceKind.Lane, "{from}-{to}")`

These are how TrafficControl / Liveness name the same physical resources Map owns. The raw `Graph` is also exposed for advanced consumers (planners running their own Dijkstra/SIPP).

### Entities & supporting VOs

- **`MapSite`** (`…/Entities/MapSite.cs:17`): `SiteId`, `SiteType`, `Pos` (`MapPosition`), `Enable`, and interference id lists. `SetEnabled`/`SetInterference*` are `internal` (mutation only through the aggregate). `Angle` is a convenience shortcut to `Pos.Angle`.
- **`MapLine`** (`…/Entities/MapLine.cs:16`): directed `StartStationId → EndStationId`, `Distance` (≥ 0, else `MAP-008`), `LineType`, optional Bézier `ControlPos1/2`, interference lists. Port fixes the original `Distince` typo and replaces object navigations with stable string ids.
- **`MapBlock`** (`…/Entities/MapBlock.cs:12`): a mutual-exclusion group — contained site/line ids plus an AABB (`MinPos`/`MaxPos`).
- **`MapPosition`** (`…/ValueObjects/MapPosition.cs:10`): a 2-D pose `(X, Y, Angle°)`; value equality; planar Euclidean `DistanceTo` (heading ignored); `Empty` placeholder. Consolidates the original `MapPos` (X/Y) with the separately-stored per-site angle.
- **`InterferenceSet`** (`…/ValueObjects/InterferenceSet.cs:11`): an immutable **symmetric** id→ids adjacency. `FromPairs` ignores self-pairs and links both directions. Built by `InterferenceCalculator.ComputeSiteInterference` via pairwise footprint overlap.

### Enums (`Domain.Shared`)

- **`MapSiteType`** (`…/Enums/MapSiteType.cs:14`): `CPSite=1, WorkSite=2, RelaySite=3, AvoidSite=4, DockSite=5`. **Bug fix:** the upstream enum had `RelaySite=3` *and* `AvoidSite=3` colliding, with `DockSite=4` overlapping `AvoidSite`'s intended slot. Renumbered contiguous & distinct; a test guards it.
- **`MapLineType`**: `Straight=0`, `Bezier=1`.
- **`MapResourceStatus`**: `Locked/Belong/Unlocked/Unable` — preserved for import fidelity; only `Unlocked`/`Unable` are meaningful to Map (the rest are TrafficControl's).

### Domain services

- **`IRoadmapGraphFactory`** / `RoadmapGraphFactory` (`…/Services/RoadmapGraphFactory.cs:8`): thin abstraction over `RoadmapGraph.Build` (build from collections or from a `Roadmap`), so callers depend on an interface rather than a static factory. Registered singleton (stateless).
- **`IInterferenceCalculator`** / `InterferenceCalculator` (`…/Services/InterferenceCalculator.cs:16`): circle-overlap test `AreInterfering`. **Normalises** the upstream inverted predicate: returns `true` exactly when footprints *partially* overlap — `|rA − rB| < d < rA + rB`. Touching, containment and disjoint are **not** interference (each pinned by a test).

---

## 4. Read seam — how other contexts consume the graph

```
                     (frozen Kernel contract: ResourceRef)
   PathPlanning ──┐
                  ├─▶ IRoadmapQueryService ──▶ RoadmapGraph (VO)   [synchronous, per planning tick]
   Coordination ──┘            ▲
                               │  Map.Roadmap.Published  ──▶  Invalidate(roadmapId)   [out-of-band]
```

**`IRoadmapQueryService`** (`Map/SwarmRoute.Map.Application.Contract/Services/IRoadmapQueryService.cs:14`) is the **frozen cross-context contract** and the only way another context reaches Map's read model. It exposes `GetGraphAsync` / `TryGetGraphAsync` / `Invalidate(roadmapId)` — consumers receive a built `RoadmapGraph` and never see EF entities, the `DbContext`, or the aggregate.

The contract is verified-in-use, not aspirational: `PathPlanning.Application`'s `PathPlanningAppService` resolves the built graph through **this exact interface** (`using SwarmRoute.Map.Application.Contract.Services;`), and Coordination's application layer references Map for the same purpose. The contract documents the intended rhythm: planners read the graph **synchronously every tick** (a hot path), while topology *changes* are pushed **out-of-band** through the `Map.Roadmap.Published` integration event, which invalidates the cache so the next read rebuilds.

**Production implementation — `RoadmapGraphProvider`** (`Map/SwarmRoute.Map.Application/Services/RoadmapGraphProvider.cs:19`): a **singleton** holding a `ConcurrentDictionary<Guid, RoadmapGraph>`. Lock-free reads; on a miss it builds via a *fresh DI scope* (a singleton cannot hold the scoped `IRoadmapRepository`), loads `GetWithTopologyAsync`, and `GetOrAdd`s so concurrent first-access yields one instance. `Invalidate` simply removes the key. The cache is dropped on publish by **`RoadmapPublishedCacheInvalidator`** (`…/Application/Events/RoadmapPublishedCacheInvalidator.cs:11`), an `IDomainEventHandler<MapRoadmapPublishedEvent>`.

The seam is pluggable. The **Simulation** context ships an `InMemoryRoadmapQueryService` implementing the same interface from a pre-built `RoadmapGraph`, and the simulation Host registers it *after* Map so it overrides `RoadmapGraphProvider` for DB-free runs. PathPlanning tests use a `FakeRoadmapQueryService` the same way.

Integration events (`…/Domain/Events/`), both `DomainEvent, IIntegrationEvent`:

| Event | `EventName` / `Version` | Carries | Consumed by |
|---|---|---|---|
| `MapRoadmapImportedEvent` | `Map.Roadmap.Imported` / `v1` | id, name, version, site/line/block counts | observability / bookkeeping |
| `MapRoadmapPublishedEvent` | `Map.Roadmap.Published` / `v1` | id, name, version | cache invalidators (Map's provider; Host's topology adapter) |

> **Note on the runtime closure seam (Host):** in the assembled system the Host binds TrafficControl's `IResourceTopology` to a `MapResourceTopologyAdapter` that reads the `Roadmap` **aggregate directly** (via `IRoadmapRepository`) and derives each resource's lock-closure (interference set + parent block) using `RoadmapGraph.SiteRef`/`LaneRef`. So Map's *domain* (aggregate + `RoadmapGraph` helpers) is consumed beyond the `IRoadmapQueryService` graph seam — but always read-only, and the dynamic state derived from it lives in TrafficControl, not Map.

---

## 5. Persistence

`MapDbContext` (`Map/SwarmRoute.Map.Infra.Data/Context/MapDbContext.cs:24`) is an EF Core 10 / Npgsql unit-of-work over a `BaseDbContext` that dispatches domain/integration events on commit. It persists exactly one aggregate, `Roadmap`, with its three owned child collections:

| Table | Maps | Key / index |
|---|---|---|
| `Roadmaps` | aggregate | PK `Id` (never generated); unique `IX_Roadmaps_Name`; `StateVersion` is the concurrency token |
| `RoadmapSites` | `OwnsMany(Sites)` | shadow `Id` PK + FK `RoadmapId`; unique `(RoadmapId, SiteId)` |
| `RoadmapLines` | `OwnsMany(Lines)` | unique `(RoadmapId, LineId)` |
| `RoadmapBlocks` | `OwnsMany(Blocks)` | unique `(RoadmapId, BlockId)` |

Value-conversion choices: enums → `string` (varchar 32); each `MapPosition` and every string-id list (`Interference*`, `Contained*`) → a **`jsonb`** column via custom `ValueConverter` + `ValueComparer`. `MapPosition`'s private ctor makes it unfit for direct `System.Text.Json`, so a `PositionJson` surrogate record round-trips it (`…/Context/Persistence/PositionJson.cs:9`). Navigations use `PropertyAccessMode.Field` to write the backing `List<>`s through the encapsulated entities. The schema is captured in the `InitialCreate` migration; `MapDbContextFactory` (a design-time `IDesignTimeDbContextFactory`) lets `dotnet ef` build the context without the host (placeholder connection string, no-op event dispatcher).

`RoadmapRepository` (`…/Repositories/RoadmapRepository.cs:10`) extends `BaseRepository<MapDbContext, Roadmap>`. `GetWithTopologyAsync` returns a **tracked** aggregate (owned children load automatically with the owner) for editing; `GetByNameAsync` is `AsNoTracking`.

**Design intent:** persistence is the *snapshot / import store*, not the hot path. The write side is a low-frequency import/publish; the planning-time graph is built **in memory** by `RoadmapGraph.Build` and cached by the provider. Storing the small, read-mostly id-list and pose collections as `jsonb` is a deliberate simplification (no join tables for what is effectively embedded data).

---

## 6. Composition / wiring

`MapNativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)` (`Map/SwarmRoute.Map.Infra.CrossCutting.IoC/MapNativeInjectorBootStrapper.cs:23`) is the composition root:

| Registration | Lifetime | Notes |
|---|---|---|
| `MapDbContext` (Npgsql) | scoped | connection string key `MapDatabase`; **guarded** — if absent, the context is registered without a provider so the Host boots DB-less |
| `IRoadmapRepository` → `RoadmapRepository` | scoped | |
| `IRoadmapGraphFactory` → `RoadmapGraphFactory` | singleton | stateless |
| `IInterferenceCalculator` → `InterferenceCalculator` | singleton | stateless |
| `IMapAppService` → `MapAppService` | scoped | |
| `IRoadmapQueryService` → `RoadmapGraphProvider` | **singleton** | cache must survive across requests |
| `IDomainEventHandler<MapRoadmapPublishedEvent>` → `RoadmapPublishedCacheInvalidator` | scoped | |
| AutoMapper (`MapMappingProfile`) | — | scanned from the Application assembly |

The bootstrapper follows the repo-wide `*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)` convention. `SwarmRoute.Host`'s `Program.cs` calls it (`MapNativeInjectorBootStrapper.RegisterServices(builder)`), adds `MapsController` as an MVC application part, and then layers Host adapters on top (the Map-backed `IResourceTopology` and avoidance selector). Map's own `Program.cs` is the same call plus controllers/OpenAPI for running the Api standalone. The connection-string guard means *only* Map and TrafficControl register a `DbContext`, and both tolerate its absence, so a no-DB smoke test never touches persistence.

---

## 7. Tests

`SwarmRoute.Map.Tests` (xUnit, references `Map.Domain` only — fast, no DB):

- **`RoadmapGraphTests`** — graph build (vertex/edge counts, directedness), `EdgeWeight` = `Distance×1000`, disabled-site exclusion, `DistanceTo` against a hand-computed shortest path **and** cross-checked against an independent `DijkstraShortestPaths` over the same graph, `ShortestPath` ordering, unreachable → `null`, `Neighbours` out-successors, factory ≡ static build (structural equality), and the zero-length→weight-1 clamp. Fixtures use a 4-node "diamond" roadmap (`Builders.DiamondRoadmap`).
- **`RoadmapInvariantTests`** — every aggregate invariant (dangling start/end endpoint, duplicate site/line id, empty site set, dangling block member, empty name), `Rename` version bump, `ReplaceTopology` rollback-on-rejection, and `MarkPublished` raising `Map.Roadmap.Published`.
- **`InterferenceTests`** — overlap / disjoint / containment / touching cases for `AreInterfering`, and symmetric set construction (incl. self-pair rejection).
- **`MapPositionTests`** — `Empty`, value equality, planar Euclidean distance (heading ignored).
- **`MapSiteTypeTests`** — guards the duplicate-value bug fix (all values & names distinct; Avoid/Relay/Dock separated).

Not covered here (by design — they need infrastructure): the EF mapping/round-trip, `RoadmapGraphProvider` caching/invalidation, `MapAppService` orchestration, and the controller. PathPlanning's own test suite exercises the `IRoadmapQueryService` seam end-to-end via its fakes.

---

## 8. v0 status & notes

**Implemented**

- `Roadmap` aggregate with full invariant validation and optimistic versioning.
- `RoadmapGraph` build + Dijkstra distance/path/neighbours, weight scaling, zero-length clamp; `ResourceRef` (CP/Lane) interop helpers.
- Static interference geometry (`InterferenceCalculator`, `InterferenceSet`).
- Application services: import / get / list / publish / delete; graph-summary projection for the API.
- Cached read seam (`RoadmapGraphProvider`) + publish-driven invalidation; consumed by PathPlanning, Coordination, and Host adapters.
- EF Core + Npgsql persistence with an initial migration; `jsonb` for poses & id lists.
- IoC bootstrapper, REST controller (`api/maps`), and Host integration.

**Bug fixes vs. the upstream engine** — `MapSiteType` duplicate-value collision (now distinct), `MapLine.Distince` → `Distance`, and the inverted interference predicate (now `true` ⇔ partial overlap). The three duplicated `GraphMap` implementations are unified into this one aggregate.

**Deferred / notes**

- **No topology *edit* API yet.** `Roadmap.ReplaceTopology` and the per-site mutators exist on the aggregate, but `IMapAppService` only exposes import (create) / publish / delete — there is no update-in-place endpoint. Re-import is the current edit path.
- **Block AABB is stored but not computed/validated** — `MinPos`/`MaxPos` are accepted as given on import; nothing derives them from contained members.
- **Interference is computed but not persisted as a derived set** — `InterferenceCalculator` produces an `InterferenceSet`, but interference is stored only as the raw per-site/line id lists supplied on import; the closure used at runtime is rebuilt by the Host's `MapResourceTopologyAdapter`.
- **Integration-event transport is in-process.** `Program.cs`/Host wire `AddEventBus()` for in-process dispatch; CAP/RabbitMQ is a documented TODO, so `Map.Roadmap.Published` currently invalidates caches within the same process.
- **`MapResourceStatus` is import-fidelity only** — dynamic states are not Map's; TrafficControl is authoritative.
- **No per-agent blacklist in Map** — the Host's topology adapter leaves it empty (a v1 runtime concern).
