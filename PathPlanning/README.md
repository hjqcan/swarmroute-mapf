# Path Planning (路徑規劃)

*Owns single-agent route computation: given a built roadmap graph and a reservation view, it produces one agent's collision-aware `SpaceTimePath` (or a typed failure).*

---

## 1. Purpose & responsibility

This bounded context answers exactly one question: **"route agent _X_ from site _A_ to site _B_ on roadmap _R_, departing no earlier than _t_ — give me the space-time path or tell me why not."** It is a pure compute context — no persistence, no DbContext, no background loop (`PathPlanningNativeInjectorBootStrapper.cs:16-18`).

It **owns**:

- The single-agent planner strategy seam `IPathPlanner` (`Planners/IPathPlanner.cs:13`) and its shipped implementations: `DijkstraPathPlanner` (v0 baseline) and `SippPathPlanner` (v1 safe-interval planner).
- The request/result vocabulary: `PlanRequest`, `PlanResult`, `PlanCost`, `WaitAction` (`ValueObjects/`).
- The `AgentPlan` aggregate (`Aggregates/AgentPlan.cs:21`) that models the lifecycle of one vehicle's current plan and raises integration events.
- **The declaration of the reservation read seam** `IReservationQuery` (`Reservations/IReservationQuery.cs:23`) — declared *here* by PathPlanning, but implemented by TrafficControl (the frozen cross-context contract; see §5).

It explicitly does **not** own:

- **The reservation table.** PathPlanning only *reads* an `IReservationView`; TrafficControl owns the authoritative table and the write seam (`TryReserve`/`Release`) (`IReservationQuery.cs:14-21`, Kernel `IReservationView.cs:8-11`).
- **The multi-agent loop.** Sequencing many agents, priority ordering, prune-and-replan, and committing reservations live in the **Coordination** context's `CoordinationCycleService` (see §5). PathPlanning plans *one* agent per call and is stateless.
- **The roadmap graph.** The graph is built and cached by the **Map** context; PathPlanning consumes it read-only as `RoadmapGraph`.

The lineage is explicit in the code: `DijkstraPathPlanner` ports the first-generation engine's `AJR.MAPF.XCBS.CBS.SearchPath` — which ran `new DijkstraShortestPaths(graph, start).ShortestPathTo(end)` and returned a site sequence or `null` — and **lifts that flat sequence into a time-aware `SpaceTimePath`** (`DijkstraPathPlanner.cs:8-15`). The space-time layer is the dimension the v0 engine lacked (Kernel `SpaceTimeCell.cs:5-7`).

---

## 2. Layers & projects

Standard grukirbs/DDD onion. Six projects:

| Project | Role | Depends on |
|---|---|---|
| `SwarmRoute.PathPlanning.Domain.Shared` | Leaf primitives: `PlannerKind`, `PlanStatus` enums, `PathPlanningErrorCodes` (`PP-001`…`PP-005`). | *(nothing)* — pure leaf. |
| `SwarmRoute.PathPlanning.Domain` | The core: `IPathPlanner` + `SelectablePathPlanner` + `DijkstraPathPlanner` + `SippPathPlanner`, the value objects, `AgentPlan` aggregate + events, and the `IReservationQuery`/`NullReservationQuery`/`AlwaysFreeReservationView` reservation seam. | Kernel, `Domain.Abstractions` (event bus), NetDevPack (`ValueObject`/`Entity`/`DomainEvent`), the vendored `SwarmRoute.Algorithms`, **`Map.Domain`** (for `RoadmapGraph`), Domain.Shared. |
| `SwarmRoute.PathPlanning.Application.Contract` | Transport surface: `IPathPlanningAppService` + `PlanResultDto`. | Kernel only. |
| `SwarmRoute.PathPlanning.Application` | `PathPlanningAppService` orchestration + the AutoMapper `PathPlanningMappingProfile`. | Domain, Application.Contract, **`Map.Application.Contract`** (for `IRoadmapQueryService`), AutoMapper. |
| `SwarmRoute.PathPlanning.Infra.CrossCutting.IoC` | The composition root `PathPlanningNativeInjectorBootStrapper`. | Application; `FrameworkReference Microsoft.AspNetCore.App` (for `WebApplicationBuilder`). |
| `SwarmRoute.PathPlanning.Tests` | xUnit tests (see §7). | Domain, Application, Application.Contract, IoC. |

Key dependency note: the **Domain** layer references `Map.Domain` directly (`SwarmRoute.PathPlanning.Domain.csproj`) because the planner operates on the concrete `RoadmapGraph` value object — that read model is shared, not duplicated. The vendored `SwarmRoute.Algorithms` reference is transitive plumbing for the same `DijkstraShortestPaths` the Map graph wraps.

---

## 3. The planners — Dijkstra and SIPP

`Plan(RoadmapGraph graph, PlanRequest request, IReservationView reservations)` (`DijkstraPathPlanner.cs:36`). The body is two phases — **(a) find a site sequence**, then **(b) lift it into a timeline** (§4).

The registered `IPathPlanner` is now `SelectablePathPlanner`: it dispatches each call to `DijkstraPathPlanner`
or `SippPathPlanner` according to `PlannerOptions.Default`. The interface did not change for v1; only the
strategy behind it did.

### Shortest path (pruned Dijkstra)

`ShortestPath` (`DijkstraPathPlanner.cs:57-100`) is a hand-rolled, blacklist-aware Dijkstra over the directed-weighted graph — *not* a delegation to `RoadmapGraph.ShortestPath`. It is re-implemented locally precisely because v0 needs to **prune blacklisted transitions mid-search**, which the Map graph's plain `DijkstraShortestPaths` wrapper cannot do.

- **State**: `distances` (`Dictionary<string,long>`, ordinal), `previous` for reconstruction, and a `PriorityQueue<string,long>` keyed by cumulative distance (`:64-70`). The classic lazy-deletion guard (`currentDistance > knownDistance → continue`) handles stale queue entries (`:74-75`).
- **Edge weights**: `graph.EdgeWeight(current, next) ?? 1`, clamped to `>= 1` (`:85-87`). Weights are `round(Distance_metres × 1000)` — the Map graph's `WeightScale = 1000d` (`Map .../RoadmapGraph.cs:25`, `:54`). So a 1.0 m lane = weight `1000`.
- **Blacklist pruning** — `IsBlacklistedTransition` (`:102-110`): a neighbour `next` is skipped if it is a blacklisted **CP** (by raw id *or* `SiteRef`), **except** the agent's own start/goal (never pruned, or the goal becomes unreachable by construction); and the transition is skipped if the directed **Lane** `from→next` is blacklisted (`RoadmapGraph.LaneRef`). This is the v0 avoidance mechanism (§5).
- **Termination**: returns `Reconstruct(...)` (`:112-127`, walk `previous` from goal back to start, reverse) on dequeuing the goal; returns `null` when the queue drains.
- **Trivial case**: `start == goal` short-circuits to `[start]` (`:61-62`).

Neighbour expansion is ordered `OrderBy(id, StringComparer.Ordinal)` (`:80`) — see **determinism** below.

### Failure branches

Ported from `SearchPath` returning `null`, but split into two *distinct, actionable* reasons (`:42-51`):

| Condition | Result | Code |
|---|---|---|
| `!graph.HasSite(FromSiteId)` or `!graph.HasSite(ToSiteId)` | `PlanResult.Failed(...)` | `PP-002` `UnknownSite` |
| `ShortestPath` returns `null`/empty (unreachable / endpoint blocked) | `PlanResult.Failed(...)` | `PP-003` `NoRoute` |

The unknown-endpoint check is *defensive and deliberate* — `ShortestPath` would itself return `null` for an absent vertex, but the comment at `:42-43` notes the reason should not be conflated with genuine "no route". `null` arguments throw `ArgumentNullException` up front (`:38-40`); `reservations` is null-checked even though v0 never reads it (`:40` — "read but treated as always-free in v0").

### Determinism (ordinal tie-break)

The planner is fully deterministic given a fixed graph + request. Two ordinal tie-breaks guarantee a stable single result among equal-cost paths:

1. Neighbour iteration order is `StringComparer.Ordinal` (`:80`).
2. The relaxation rule `candidate >= best → continue` (`:90-91`) keeps the **first** (ordinally-earliest) equal-cost predecessor rather than overwriting on ties.

This matters because the whole fleet pipeline relies on reproducible runs — Coordination orders goals deterministically and `FleetLoopDriver` advertises "deterministic given deterministic inputs" (`CoordinationCycleService.cs:25-31`, `FleetLoopDriver.cs:89`). A non-deterministic planner would break that guarantee.

---

## 4. Space-time timeline — `BuildTimeline`

`BuildTimeline(graph, sites, releaseTimeMs)` (`DijkstraPathPlanner.cs:134-178`) is where the flat site list becomes a Kernel `SpaceTimePath` (`= IReadOnlyList<SpaceTimeCell>`, Kernel `SpaceTimePath.cs:8`). Each `SpaceTimeCell` is one `ResourceRef` occupied over one half-open `TimeInterval` (Kernel `SpaceTimeCell.cs:10`).

### The CP + Lane cell-per-hop model

For each hop `site[i] → site[i+1]` the planner emits **two cells sharing the same interval** (`:153-167`):

```
hop i: weight = EdgeWeight(site[i], site[i+1])  (clamped >= 1)
       traversal = [cursor, cursor + weight)
       cell  ⟨CP:   site[i]      , traversal⟩      ← the control point
       cell  ⟨Lane: site[i]-[i+1], traversal⟩      ← the directed lane
       cursor += weight
```

```
sites:   A ───────► B ───────► C ───────► D
ms:    rel        rel+w0     +w0+w1    +…+w2   (+GoalDwellMs)
cells: CP:A          CP:B       CP:C      CP:D
       Lane:A-B      Lane:B-C   Lane:C-D
       [rel,+w0) ... contiguous, half-open, non-overlapping ...
```

**Why CP and Lane deliberately overlap the same window** (`DijkstraPathPlanner.cs:18-22`): while traversing a segment the vehicle physically occupies *both* the lane and (conservatively) the control point it is heading along. v0 reserves both for safety — it is the simplest sound over-approximation. The resource vocabulary (`ResourceKind.CP` / `.Lane`, Kernel `ResourceRef.cs:8-21`) is fixed so SIPP can reuse the same resources while changing *when* they are occupied.

The CP-only projection is what downstream consumers read back as the route: `AgentPlan.ExtractSiteSequence` and the mapping profile both filter `Resource.Kind == CP` (`AgentPlan.cs:174-178`, `PathPlanningMappingProfile.cs:27-33`).

### Goal dwell & the terminal cell

A `TimeInterval` requires `Start <= End` (Kernel `TimeInterval.cs:25-26`), and a degenerate `[t,t)` cell would be zero-duration. So the terminal (goal) site gets a **unit dwell**: `[cursor, cursor + GoalDwellMs)` where `GoalDwellMs = 1` (`DijkstraPathPlanner.cs:33`, `:170-173`). This is "purely so the produced `SpaceTimePath` type is well-formed in v0" (`:30-32`) — it is *not* a model of real dwell time. The `start == goal` single-site plan is the same shape: one `[rel, rel+GoalDwellMs)` CP cell, zero cost (`:142-148`).

### `releaseTimeMs`

The timeline's origin. The first hop starts exactly at `request.ReleaseTimeMs` (`:150`, `cursor = releaseTimeMs`); the test asserts cell `[0]` starts there (`DijkstraPathPlannerTests.cs:161`). `PlanRequest` validates `ReleaseTimeMs >= 0` (`PlanRequest.cs:45-46`, `PP-004`). Coordination passes one `cycleReleaseTimeMs = _clock.NowMs` shared by every agent in a cycle (`CoordinationCycleService.cs:79-80`).

### `PlanCost`

`new PlanCost(totalDistance, hopCount, durationMs)` (`:175-177`): `DistanceUnits` = Σ scaled edge weights (cross-checked against `RoadmapGraph.DistanceTo` in tests), `HopCount` = `sites.Count - 1`, `DurationMs` = `cursor + GoalDwellMs - releaseTimeMs`. In v0 `DurationMs == DistanceUnits + GoalDwellMs` because **edge weight is used directly as a proxy duration** (`PlanCost.cs:6-10`) — there is no speed model yet.

### Intervals are fleet-clock units — and why the Simulation uses a *tick* clock

Every interval is in **fleet-clock milliseconds** against the single monotonic clock `IFleetClock` (Kernel `IFleetClock.cs:7-11`, `TimeInterval.cs:4`). Half-open `[Start,End)` semantics mean a vehicle may exit a resource exactly as the next enters — touching endpoints do not overlap (`TimeInterval.cs:5-8`, `:39`), which is why the contiguous timeline is collision-clean (`DijkstraPathPlannerTests.cs:173-176`).

There is a subtlety the system resolves outside this context, worth recording here because it is *why the planner's interval units matter*:

- **Production** maps the fleet clock to **wall-clock milliseconds** (`SystemFleetClock`). But a coordination cycle runs sub-millisecond, so two reservations the table considers "time-separated" can land on the **same CP on the same execution step** — the reservation axis and the execution axis are decoupled (`ManualFleetClock.cs:11-16`).
- **Simulation** therefore drives a **discrete tick clock**, `ManualFleetClock`, advancing it to the current integer tick *before each planning cycle* (`ManualFleetClock.cs:18-25`; driven at `FleetLoopDriver.cs:189-192`). With one tick = one CP hop, **the planned intervals == execution ticks**, so the reservation table's interval-exclusive leases become a real collision-freedom guarantee at execution time (`FleetLoopDriver.cs:75-98`).

PathPlanning is agnostic to *which* clock is in play — it just emits intervals on whatever `releaseTimeMs` origin it is handed. The tick-vs-wall-clock decision belongs to **Coordination/Simulation**; this context simply guarantees the intervals are monotonic, contiguous and non-overlapping on that axis.

---

## 5. Reservation awareness

### v0 baseline vs v1 SIPP

`IPathPlanner.Plan` takes an `IReservationView` (`IPathPlanner.cs:21-26`), so every call site already passes one — the seam is wired end-to-end. But **v0's planner does not search safe intervals**. The stub view `AlwaysFreeReservationView` reports `IsFree → true` and a single maximal `[0, long.MaxValue)` free interval for any resource (`AlwaysFreeReservationView.cs:14-27`); `DijkstraPathPlanner` never even calls it (`DijkstraPathPlanner.cs:40` — "read but treated as always-free"). `WaitAction` (`ValueObjects/WaitAction.cs:16`) — the dual of a move, what a reservation-aware planner inserts to let another vehicle pass — is defined now but **never emitted** by v0 ("its timeline is move-only", `WaitAction.cs:11-14`).

`SippPathPlanner` is the v1 implementation of that same seam. It materialises each CP and Lane resource's
`FreeIntervals`, searches `(site, safe-interval)` states with an A* priority, and emits a `SpaceTimePath` whose
CP dwell may be longer than one hop when the agent must wait for a busy CP or lane to clear. A lane conflict is
detected through the view, including reversed-lane conflicts supplied by TrafficControl's snapshot view.

### Avoidance is via the request blacklist (CP/Lane)

So how does v0 avoid contention at all? Through `PlanRequest`'s **blacklist**, enforced by pruning the Dijkstra search (§3, `DijkstraPathPlanner.cs:102-110`). `PlanRequest` carries two views of it (`PlanRequest.cs:21-22, 94-104`):

- `BlacklistedResources` — the canonical `HashSet<ResourceRef>` over CP *and* Lane.
- `BlacklistedSiteIds` — a CP-only convenience view; site ids are normalized into `ResourceRef(CP, id)` and vice-versa (`PlanRequest.cs:60-76`).

### The prune-and-replan contract with Coordination

This is the heart of the v0 design and is owned by **`CoordinationCycleService`** (`Coordination/.../CoordinationCycleService.cs`), not by PathPlanning. The loop per agent (`:99-198`):

1. Read the current view (`_reservations.GetView`), build a `PlanRequest` with the running `pruned` set as `blacklistedResources` (`:120-128`).
2. `_planner.Plan(...)`. On failure → report contended (`:130-145`).
3. `_traffic.TryReserveAsync(path, ...)`. On `Granted` → done (`:150-166`).
4. On `Denied`/`Queued`/`Blocked` → ask `_traffic.BlockedResources(path, ...)` for the *concrete* CP/Lane resources that actually blocked this path, **add them to `pruned`** (never the agent's own start/goal, `:170-178`), and **replan** — bounded by `MaxReplanAttempts = 8` (`:40`, `:118`).
5. If nothing new could be pruned, replanning would be a no-op → stop (`:184-186`).

Each replan **strictly shrinks the search space** (more blacklisted resources), so the inner loop provably terminates — it either routes around the contention or fails with no-route, to be retried next tick once a holder releases (`:25-35`). v1 did not change this loop body: SIPP still respects the blacklist, and additionally routes around the reservation view in time.

The static-obstacle seed is the same mechanism: `FleetLoopDriver` feeds parked (arrived) vehicles' CPs as `blockedResources` so the rest of the fleet routes around them (`FleetLoopDriver.cs:202-205`, `CoordinationCycleService.cs:108-112`).

---

## 6. Composition / wiring

`PathPlanningNativeInjectorBootStrapper.RegisterServices` (`Infra.CrossCutting.IoC/...:33-52`), with a `WebApplicationBuilder` overload (`:22-27`) per the grukirbs `*NativeInjectorBootStrapper` convention so the Host wires every context uniformly:

```csharp
services.AddSingleton<DijkstraPathPlanner>();
services.AddSingleton<SippPathPlanner>();
services.TryAddSingleton<PlannerOptions>();              // default planner, overrideable per container
services.AddSingleton<IPathPlanner, SelectablePathPlanner>();
services.AddSingleton<IReservationQuery, NullReservationQuery>();
services.AddScoped<IPathPlanningAppService, PathPlanningAppService>();
services.AddLogging();
services.AddAutoMapper(_ => { }, typeof(PathPlanningMappingProfile).Assembly);
```

No DbContext / repository / unit of work — PathPlanning is a pure compute context (`:16-18`).

**The `NullReservationQuery` registration is the override seam.** It is the v0 default so PathPlanning builds and runs **standalone** with no TrafficControl present (`NullReservationQuery.cs:5-9`). When TrafficControl is in the composition, *its* bootstrapper registers `IReservationQuery → ReservationService` (`TrafficControl/.../TrafficControlNativeInjectorBootStrapper.cs:88`), which — registered after PathPlanning's in the Host — **wins** for `GetRequiredService` and supplies the live reservation-table-backed view. The planner contract is unchanged either way (`IReservationQuery.cs:19-22`).

### Application orchestration

`PathPlanningAppService.PlanForAsync` (`Application/Services/PathPlanningAppService.cs:59-90`):

1. Build + validate `PlanRequest` (release time 0 in v0) (`:67`).
2. `_roadmapQuery.GetGraphAsync` — Map read seam; throws `KeyNotFoundException` for an unknown roadmap (`:70`).
3. `_reservationQuery.GetView` — standalone mode returns the always-free stub; the Host/Simulation composition is overridden by TrafficControl's live reservation snapshot (`:73`).
4. `_planner.Plan(...)` — dispatched to Dijkstra or SIPP by `SelectablePathPlanner` (`:75`).
5. Wrap the outcome in a fresh `AgentPlan` aggregate (which raises `Computed`/`Failed`) and dispatch events (`:78-87`).
6. `_mapper.Map<PlanResultDto>` (`:89`).

Because this context has no `BaseDbContext.Commit()` to flush aggregate events, the `IDomainEventDispatcher` and `IIntegrationEventPublisher` are resolved **optionally** and invoked directly; when neither is registered (unit tests / standalone) the events are collected on the aggregate and simply not dispatched (`:26-31`, `:96-111`).

### `AgentPlan` aggregate & events

`AgentPlan` (`Aggregates/AgentPlan.cs:21`) models one vehicle's current plan as a proper event-raising aggregate even though it is never persisted — "the coordinator owns an `AgentPlan` per vehicle and re-plans it across ticks" (`:10-19`). `Apply(result)` sets `Computed`/`Failed` state and raises `AgentPlanComputedEvent` (carries the CP sequence + cost) or `AgentPlanFailedEvent` (`:143-171`). `Replan(...)` and `Invalidate(...)` bump a `StateVersion` (optimistic concurrency) and re-raise (`:104-141`). Both events are `IIntegrationEvent`s named `PathPlanning.AgentPlan.Computed` / `.Failed`, version `v1` (`Events/AgentPlanComputedEvent.cs:68-71`, `Events/AgentPlanFailedEvent.cs:53-56`).

---

## 7. Tests — `SwarmRoute.PathPlanning.Tests`

xUnit; three suites + two test-support fakes.

- **`DijkstraPathPlannerTests`** — the planner's correctness (`DijkstraPathPlannerTests.cs`):
  - Shortest-path selection: linear chain, diamond (picks cheaper branch both ways), cross-checked against `RoadmapGraph.DistanceTo` (`:41-90`).
  - Directedness respected — reverse is unreachable (`:106-115`).
  - Failure branches: `PP-003` unreachable, `PP-002` unknown start/goal (`:92-129`).
  - `start == goal` → single non-degenerate cell, zero cost (`:131-144`).
  - **Timeline well-formedness**: 4 CP + 3 Lane cells, first starts at release, CP intervals contiguous + strictly advancing + non-overlapping, move durations == scaled weights `1000/2000/3000`, lanes `A-B/B-C/C-D` (`:146-200`).
  - **Blacklist pruning**: a blacklisted intermediate CP *and* a blacklisted Lane each force the alternate branch (`:202-229`).
  - Null-argument guards (`:231-240`).
- **`AgentPlanTests`** — aggregate behaviour: construction raises the right event, `Replan`/`Invalidate` bump version + raise, id/reason guards (`AgentPlanTests.cs`).
- **`SippPathPlannerTests`** — v1 safe-interval correctness: ordinary routing, deterministic tie-breaks,
  unknown/unreachable failures, waiting for busy CPs and lanes, detouring around permanently blocked CPs, and
  the unified one-hop tick axis.
- **`PathPlanningAppServiceTests`** — end-to-end through the **real** `PathPlanningNativeInjectorBootStrapper` (real planner + `NullReservationQuery` + AutoMapper + app service), with only the Map seam swapped for `FakeRoadmapQueryService` (`PathPlanningAppServiceTests.cs:20-31`): ordered site sequence + cost on success, `PP-003` failure DTO, `KeyNotFoundException` for unknown roadmap, empty-agent-id validation.
- **`TestSupport/`** — `RoadmapGraphBuilder` (fluent builder over the *real* `RoadmapGraph.Build`, distance→`round(d×1000)` weight), `FakeRoadmapQueryService`, and `FakeReservationView` for safe-interval tests.

---

## 8. v0/v1 status

| | **v0 — shipped** | **v1 — shipped** |
|---|---|---|
| `PlannerKind` | `Dijkstra = 1` | `Sipp = 2` |
| Algorithm | Pruned single-agent Dijkstra, **space-only** | SIPP — Safe-Interval Path Planning, **reservation-aware in time** |
| Reservation view | Accepted but **never read**; `AlwaysFreeReservationView` | Searched: `FreeIntervals` / `IsFree` drive safe-interval expansion |
| Contention avoidance | Request **CP/Lane blacklist** + Coordination prune-and-replan | Above **plus** routing around the view in time (waits) |
| Waits | Not emitted by Dijkstra (move-only timeline) | Represented as longer CP dwell intervals in the emitted `SpaceTimePath` |
| CP/Lane cells | Share one interval sized by edge weight | Unit-hop timeline on `TimeAxis.HopMs`, aligned with schedule-faithful execution |

The seam held: **`IPathPlanner` is unchanged across v0→v1** (`IPathPlanner.cs`), and the Coordination loop body
is unchanged. v1 is an additive refinement of *when* resources are taken, on top of v0's already-correct *what*.

---

## Cross-context dependencies (summary)

- **Kernel** (`Shared/SwarmRoute.SpatioTemporal.Kernel`) — frozen vocabulary: `SpaceTimePath`/`SpaceTimeCell`/`TimeInterval`/`ResourceRef`/`SafeInterval`, the read seam `IReservationView`, and `IFleetClock`. PathPlanning produces `SpaceTimePath`; declares `IReservationQuery` returning `IReservationView`.
- **Map** (`Map.Domain` + `Map.Application.Contract`) — consumes the `RoadmapGraph` value object (`HasSite`/`Neighbours`/`EdgeWeight`/`SiteRef`/`LaneRef`, `WeightScale = 1000`) read-only via `IRoadmapQueryService.GetGraphAsync`.
- **TrafficControl** — *implements* PathPlanning's `IReservationQuery` as `ReservationService` and **overrides** the `NullReservationQuery` DI registration (`TrafficControlNativeInjectorBootStrapper.cs:88`). Owns the reservation write seam (`TryReserve`/`Release`), which is **not** part of PathPlanning.
- **Coordination** — *consumes* `IPathPlanner` directly; owns the multi-agent rolling-horizon loop and the **prune-and-replan** contract (`CoordinationCycleService`) that drives v0 contention avoidance via the request blacklist.
- **Simulation** — drives the closed loop and supplies the discrete `ManualFleetClock` (tick = CP hop) that makes planned intervals == execution ticks (`FleetLoopDriver`).
