# Simulation (模擬 — 閉環執行器與記憶體模擬 API)

*Where the abstract planning engine becomes an observable, verifiable closed loop: a database-free, per-request in-memory simulation that drives the REAL Map + PathPlanning + TrafficControl + Coordination stack to completion, records a tick-by-tick timeline, and proves it is collision-free.*

---

## 1. Purpose

The other contexts answer *"can a fleet be planned and reserved without conflict?"* in the abstract. The Simulation context answers it **concretely and observably**: it stands up the actual engine over an in-memory roadmap, runs a multi-AGV scenario from starts to goals, and emits a frame-per-tick replay that a frontend renders. There is **no database** — no EF, no Postgres — so the whole thing runs in a unit-test or behind one HTTP call.

Two properties make this more than a demo:

- **It reuses the real services.** The engine is wired from the production composition roots — `PathPlanningNativeInjectorBootStrapper.RegisterServices`, `TrafficControlNativeInjectorBootStrapper.RegisterServices`, `services.AddCoordination()` (`InMemorySimulationEngineFactory.cs:31-33`). The only swaps are an in-memory roadmap source and a tick-driven clock. So a green simulation is evidence the *actual* planner/reservation/coordination code is collision-free, not a toy reimplementation.
- **It is a verifier, not just a runner.** `FleetLoopDriver` (the executor) records every tick, runs a defensive same-CP safety check, and honestly reports a standoff as `DidNotConverge` instead of crashing or silently truncating. The closed-loop body it contains was lifted out of `ClosedLoopIntegrationTests` into production code so the API and the tests drive the *same* verified loop.

Two assemblies complete the story; both live outside `Simulation.Application` because they know concrete Infra/Host wiring:
- `host/SwarmRoute.Host/Adapters/InMemorySimulationEngineFactory.cs` — builds the per-request engine.
- `host/SwarmRoute.Host/Controllers/SimulationController.cs` — the `POST /api/simulation/run` endpoint.

---

## 2. Per-request engine — isolation by construction

`InMemorySimulationEngineFactory.Create(RoadmapGraph)` (`InMemorySimulationEngineFactory.cs:20-44`) builds a **fresh `ServiceCollection` and `ServiceProvider` per call**:

```csharp
var roadmapId = Guid.NewGuid();
var services = new ServiceCollection();
services.AddLogging();
services.AddEventBus();
services.AddSingleton<IRoadmapQueryService>(new InMemoryRoadmapQueryService(roadmapId, graph));
PathPlanningNativeInjectorBootStrapper.RegisterServices(services);
TrafficControlNativeInjectorBootStrapper.RegisterServices(services);
services.AddCoordination();
// ... swap the clock (below) ...
var provider = services.BuildServiceProvider();
return new Engine(provider, roadmapId, provider.GetRequiredService<IFleetCoordinationCycle>(), clock);
```

Why a whole container per request? Because the authoritative `ReservationTable` is a **process-wide singleton** in production (`TrafficControlNativeInjectorBootStrapper.cs:74`, commented *"the in-memory authoritative reservation state: a process-wide SINGLETON"*). If two concurrent simulations shared one provider they would share that table and one run's leases would block or corrupt the other's. A private provider per run gives each simulation its **own `ReservationTable`, `IFleetCoordinationCycle`, planner and clock** — concurrent `POST /run` calls are fully isolated. `ISimulationEngine : IAsyncDisposable` (`ISimulationEngineFactory.cs:17`) and the `await using` in the service (`SimulationService.cs:53`) tear the provider down when the run ends.

**The roadmap source.** `InMemoryRoadmapQueryService` (`InMemoryRoadmapQueryService.cs`) is a single-roadmap `IRoadmapQueryService` backed by the pre-built `RoadmapGraph`; `GetGraphAsync(id)` returns it for the one registered `roadmapId` and throws `KeyNotFoundException` otherwise. It mirrors the test fake and stands in for production's repository-backed `RoadmapGraphProvider`.

**The clock swap — the crux.** TrafficControl's bootstrapper registers `SystemFleetClock` as the default `IFleetClock`. The factory removes it and substitutes a `ManualFleetClock` (`InMemorySimulationEngineFactory.cs:38-40`):

```csharp
var clock = new ManualFleetClock();
services.RemoveAll<IFleetClock>();
services.AddSingleton<IFleetClock>(clock);
```

The same `clock` instance is handed to the `Engine` so the driver can advance it. This is the registration that couples reservation time to execution time — see §3/§4.

---

## 3. `FleetLoopDriver` — the closed-loop executor (the core)

`FleetLoopDriver.RunToCompletionAsync(...)` (`FleetLoopDriver.cs:134-355`) is the heart of this context. Given the engine's `IFleetCoordinationCycle`, the `roadmapId`, the `RoadmapGraph`, a fleet of `FleetAgentSpec`, a `maxTicks` budget and an `advanceClock` setter, it drives the real engine and records a `FleetLoopResult`.

It keeps per-agent run state in a private `RunAgent` (`FleetLoopDriver.cs:102-123`): `Start` (the *current* planning origin — mutated on re-route), `Goal`, `Priority`, `EnRoute`/`Done` flags, the reserved `CpRoute`, the held `AllResources`, the route index `Idx`, and a `Replans` counter. The fleet is sorted into a **stable order** — `OrderBy(Priority).ThenBy(Id, Ordinal)` (`FleetLoopDriver.cs:149-153`) — so a given input always produces the same timeline.

### The tick loop

```
                       ┌──────────────────────────────────────────────────────────────┐
  tick-0 frame         │  record frame 0: every agent Waiting at its start CP           │
  (all Waiting)        └──────────────────────────────────────────────────────────────┘
                                              │
        ┌─────────────────────────────────────▼─────────────────────────── while any agent !Done ─┐
        │  if tick+1 > maxTicks  ──► status = DidNotConverge; break                                 │
        │  tick++                                                                                   │
        │                                                                                           │
        │  (0) advanceClock(tick)        ManualFleetClock.NowMs = tick   ← reservation axis = tick   │
        │                                                                                           │
        │  (1) PLAN + RESERVE pending (idle) agents                                                 │
        │        cycle.RunCycleAsync(roadmapId, pending, blocked: parkedCells-as-SiteRefs, ct)      │
        │        for each reserved result: EnRoute=true, Idx=0, CpRoute = path's CP cells           │
        │        (assert CpRoute runs Start→Goal, else throw FleetLoopException)                    │
        │                                                                                           │
        │  (2) ADVANCE each en-route agent at most ONE CP — through the RIGHT-OF-WAY GATE:           │
        │        occupantNow  = who physically sits on each CP at tick start                        │
        │        claimedNext  = CPs held after this tick (seed: every NON-mover keeps its CP)        │
        │        for each mover (priority order):                                                   │
        │           toCp = next CP                                                                   │
        │           ├─ occupant on toCp is Done (parked)? ─► RE-ROUTE: release path, Start=fromCp,   │
        │           │                                         rejoin planning next cycle             │
        │           ├─ toCp in claimedNext OR occupied now? ─► WAIT: hold fromCp (keep all leases)   │
        │           └─ else ─► Idx++, claim toCp, ReleaseAsync(fromCp-CP, fromCp→toCp-Lane)          │
        │        on reaching last CP ─► Done, parkedCells.Add(goal), ReleaseAsync(all held)          │
        │                                                                                           │
        │  (3) SAFETY (defensive): no two right-of-way holders share a CP                            │
        │        if they do ─► Collisions++, status = CollisionDetected (record the frame, break)    │
        │                                                                                           │
        │  (4) RECORD frame: every agent's CP + AgentMotionState this tick                          │
        └───────────────────────────────────────────────────────────────────────────────────────────┘
```

**(tick 0) Initial frame.** Before the loop, a frame for tick 0 is recorded with every agent `Waiting` at its start CP (`FleetLoopDriver.cs:169-174`). This gives the viewer a "fleet at origins" frame, because the loop *reserves and advances within the same tick* — so tick 1 is already one CP in.

**(0) Advance the tick clock.** At the very start of each tick, `advanceClock?.Invoke(tick)` sets the fleet clock to the integer tick (`FleetLoopDriver.cs:192`; the sim passes `engine.Clock.SetTick` per `SimulationService.cs:61`). This happens **before** planning so every `TimeInterval` reserved this cycle is expressed in *tick units* — the same axis the executor moves on (one tick = one CP hop). This is the single most important design decision in the loop; §4 explains why.

**(1) Plan + reserve pending agents.** Every agent that is `!Done && !EnRoute` becomes an `AgentGoal(Id, Start, Goal, Priority)` (`FleetLoopDriver.cs:195-198`) and the batch is handed to `cycle.RunCycleAsync(roadmapId, pending, blocked, ct)` (`FleetLoopDriver.cs:205`). Crucially, **`parkedCells` is passed as the `blockedResources` set** — `parkedCells.Select(RoadmapGraph.SiteRef).ToHashSet()` (`FleetLoopDriver.cs:202-204`) — so the planner routes the rest of the fleet *around* agents that have already finished and are sitting on their goal CPs. For each reserved result the driver flips the agent en route, extracts its `CpRoute` (the `ResourceKind.CP` cells of the returned `SpaceTimePath`, `FleetLoopDriver.cs:216-219`), caches `AllResources` for later release, and accumulates `Replans += max(0, Attempts-1)` (attempts beyond the first are the cycle's internal prune-and-replan retries). It then asserts the reserved path actually runs `Start → Goal`, throwing `FleetLoopException` only if that internal invariant is violated (`FleetLoopDriver.cs:222-225`).

**(2) The right-of-way gate — the final collision guarantee.** This is the executor's stop-and-wait. Two structures are built each tick (`FleetLoopDriver.cs:247-250`):
- `occupantNow` — which agent physically sits on each CP at the *start* of the tick (pending agents on their origin, en-route on their current CP, arrived on their goal).
- `claimedNext` — the CPs that will be occupied *after* the tick. It is **seeded with every non-mover's position**, so a mover can never step onto a cell a waiting or parked agent holds.

Then, for each en-route agent (in the stable priority order), looking at the next CP `toCp` (`FleetLoopDriver.cs:252-294`):

1. **Parked blocker → re-route.** If `occupant.Done` (a finished vehicle is permanently parked on `toCp`), waiting would never clear. So the agent **drops its reservation** (`ReleaseAsync(held)`), sets `EnRoute=false`, `Start = fromCp`, resets its route to `[fromCp]`, and rejoins planning next cycle (`FleetLoopDriver.cs:263-278`). Because the parked cell is already in `parkedCells`, the next plan routes around it. `Replans++`.
2. **Transient blocker → wait.** If `toCp` is already in `claimedNext` (a higher-priority mover took it) **or** still occupied now, the agent **holds position**: it adds `fromCp` to `claimedNext` and keeps all its leases (`FleetLoopDriver.cs:281-285`). It retries next tick.
3. **Clear → advance one CP.** Otherwise `Idx++`, claim `toCp`, and `ReleaseAsync` the cell+lane just vacated — the `fromCp` CP and the `fromCp→toCp` `Lane` (`FleetLoopDriver.cs:287-293`) — handing them back to the reservation table so trailing/crossing agents can use them.

On reaching the last CP the agent becomes `Done`, its goal CP is added to `parkedCells`, and **all still-held resources are released** (no lease leak) (`FleetLoopDriver.cs:296-305`).

**(3) Defensive safety check.** After moving, the driver asserts no two right-of-way holders (en-route or just-arrived) occupy the same CP (`FleetLoopDriver.cs:309-325`). With the gate in place this *cannot* happen; if it ever did it is recorded as `FleetLoopStatus.CollisionDetected` with a `FleetCollisionInfo(tick, siteId, [a,b])` and the run stops — a regression signal, not normal flow.

**(4) Record the frame** with every agent's `(AgentId, SiteId, AgentMotionState)` (`FleetLoopDriver.cs:330-335`), track `MaxConcurrentEnRoute` as a parallelism signal, and loop.

The driver is **deterministic** given deterministic inputs (the tick clock removes wall-clock dependence) and is a **verifier**: it throws only on the internal `Start→Goal` invariant breach, never on a standoff.

---

## 4. Collision-freedom & liveness

### Two layers guarantee no collision

1. **Interval reservations (who plans through where, and when).** Inside `RunCycleAsync`, `CoordinationCycleService` (`Coordination/.../CoordinationCycleService.cs:37`) plans each agent against the current reservation view and reserves its path as interval-exclusive leases in the singleton `ReservationTable`. This coordinates the fleet at *plan time*: two agents are not granted overlapping `[start,end)` intervals on the same resource.
2. **The executor right-of-way gate (the final stop-and-wait).** Reservations alone are only as safe as the time axis they live on. The gate in step (2) is the hard guarantee at *execution time*: a vehicle physically enters the next CP **only if it is empty this tick and unclaimed by a prior mover** — otherwise it waits. A same-CP collision is therefore impossible *by construction*, independent of any reservation subtlety.

A pathological standoff (mutual blocking the gate cannot resolve) degrades to **`DidNotConverge`** — never a crash, never a collision.

### Why the tick clock matters (the wall-clock root cause)

The gate guarantees safety, but the whole point of the simulation is to verify the *reservation layer*. That layer is only meaningful if reserved intervals and executed ticks live on **one axis**. Production's `SystemFleetClock` reports `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` (`TrafficControl/.../SystemFleetClock.cs:12`) — wall-clock milliseconds. A simulation runs **many cycles sub-millisecond**, so two reservations the table considers time-separated can land on the same CP on the same tick: the reservation axis (ms) and the execution axis (ticks) are *decoupled*. `ManualFleetClock` fixes this (`ManualFleetClock.cs`): the driver sets `NowMs = tick` before each cycle (`ManualFleetClock.cs:24`), so one tick of execution equals one unit of reservation time. This is exactly what makes the reservation table's interval collision-freedom a *real* guarantee at execution time, and what makes the run reproducible.

### Liveness & the throughput trade-off

- **Honest non-convergence.** When the grid is dense, whole-path reservation can serialise agents and the conservative gate makes trailing vehicles wait. If the fleet still hasn't all arrived within `maxTicks`, the loop sets `DidNotConverge` and breaks (`FleetLoopDriver.cs:180-185`) rather than colliding or truncating silently. Dense instances thus *report* the limitation truthfully.
- **Parked-vehicle rerouting** (step (2).1 + `parkedCells`) is the liveness escape valve: a finished agent on a goal CP becomes a planner obstacle, so the rest of the fleet routes around it instead of deadlocking behind it forever.
- **The gate is conservative.** A trailing vehicle waits one tick for the cell ahead to clear, trading a little throughput for a hard no-collision guarantee. v1's SIPP planner (§8) will recover that throughput by routing in time rather than stop-and-wait.

`FleetLoopStatus` (`FleetLoopDriver.cs:28-38`): `Completed` (all arrived, no collision), `CollisionDetected` (regression signal — should never occur), `DidNotConverge` (tick budget exhausted: livelock / deadlock / starvation).

---

## 5. `SimulationService` — scenario generation & DTO mapping

`SimulationService.RunAsync` (`SimulationService.cs:30-66`) orchestrates one run:

1. **Validate** (`SimulationService.cs:68-84`): `Width ≥ 1`, `Height ≥ 1`, `AgvCount ≥ 1`, and the key invariant **`Width*Height ≥ 2*AgvCount`** — the grid must hold a *distinct start AND a distinct goal* for every AGV. A violation throws `ArgumentException` (→ HTTP 400).
2. **Build the grid** via `GridFieldFactory.BuildGrid(w, h)` (§ below).
3. **Seed distinct starts/goals** with a seeded `Random(request.Seed ?? DefaultSeed)` where `DefaultSeed = 1469` keeps a seedless request reproducible (`SimulationService.cs:13, 41`). It Fisher–Yates **shuffles** all site ids (`Shuffle`, `SimulationService.cs:152-160`) and takes two disjoint blocks — `shuffled[i]` as start, `shuffled[AgvCount + i]` as goal (`SimulationService.cs:45-50`). Two disjoint slices guarantee *every start ≠ its goal* **and** all starts/goals mutually distinct, given the validated capacity invariant. Agent `i` gets id `agv-{i+1}` and `Priority: i`.
4. **Get a fresh engine** for this request: `await using var engine = _engineFactory.Create(field.Graph)` (`SimulationService.cs:53`) — isolation per §2.
5. **Run the loop** with `maxTicks = ((Width+Height) * (AgvCount+1) * 2) + 100` (`SimulationService.cs:93-94`) — a generous bound (each agent traverses at most `Width+Height` CPs; the factor leaves slack for serialisation and gate waits) — passing `advanceClock: engine.Clock.SetTick` (`SimulationService.cs:58-62`).
6. **Map** the `FleetLoopResult` to `SimulationResultDto` (`Map`, `SimulationService.cs:96-147`): sites → `SiteDto`, every directed `RoadmapGraph` edge (`Vertices` × `Neighbours`) → `LaneDto`, per-agent reserved route → `AgentDto.PathSiteIds` (with a `ColorIndex` for the palette), each frame → `FrameDto`/`PositionDto` (attaching planar X/Y per CP), and `FleetLoopStats` → `StatsDto`.

`GridFieldFactory.BuildGrid(w, h)` (`GridFieldFactory.cs:26-63`) builds a rectangular roadmap of `WorkSite` control points, ids on the `r{row}c{col}` convention (`GridFieldFactory.cs:66`), positioned at `MapPosition(X=col, Y=row)`. Each undirected 4-neighbour adjacency becomes a **pair of unit-distance directed `MapLine`s** (both directions, `AddBidirectional`, `GridFieldFactory.cs:68-72`), then `RoadmapGraph.Build(mapSites, lines)`. It returns a `GridField(Width, Height, Graph, Sites)` — the engine-facing graph plus the render metadata.

**Registration** (`SimulationServiceCollectionExtensions.cs:14-23`): `AddSimulation()` registers `GridFieldFactory` and `FleetLoopDriver` as singletons (both stateless) and `ISimulationService → SimulationService` scoped. The Host supplies `ISimulationEngineFactory → InMemorySimulationEngineFactory` (scoped) and calls `AddSimulation()` (`Program.cs:79-80`). Application deliberately does **not** know how the engine is wired — that composition belongs to Host/Infra.

---

## 6. HTTP contract

**Endpoint** (`SimulationController.cs:21-35`): `POST /api/simulation/run`. On `ArgumentException` it returns `400` with `ProblemDetails`; otherwise `200` with the result. Responses are serialised by the Host's default `AddControllers()` (`Program.cs:41`) — no custom `JsonNamingPolicy` is set, so System.Text.Json's default **camelCase** applies. The frontend replays the returned timeline.

**Request** — `SimulationRequest` (`SimulationRequest.cs:15`):
```jsonc
{ "width": 8, "height": 8, "agvCount": 4, "seed": 1469 }   // seed optional → DefaultSeed
```

**Response** — `SimulationResultDto` (`SimulationResultDto.cs`), camelCase JSON:
```jsonc
{
  "field": {                       // FieldDto
    "width": 8, "height": 8,
    "sites": [ { "id": "r0c0", "x": 0, "y": 0, "type": "WorkSite" }, ... ],   // SiteDto[]
    "lanes": [ { "id": "r0c0-r0c1", "from": "r0c0", "to": "r0c1" }, ... ]      // LaneDto[] (directed)
  },
  "agents": [                      // AgentDto[]
    { "id": "agv-1", "startSiteId": "r0c0", "goalSiteId": "r7c7",
      "colorIndex": 0, "pathSiteIds": ["r0c0", "r0c1", ...] }                  // reserved CP route
  ],
  "timeline": {                    // TimelineDto — what the frontend replays
    "tickCount": 31,
    "frames": [                    // FrameDto[] — one per tick (incl. tick 0 and any colliding tick)
      { "tick": 0, "positions": [
          { "agentId": "agv-1", "siteId": "r0c0", "x": 0, "y": 0, "state": "Waiting" }, ...  // PositionDto
      ] }, ...
    ]
  },
  "stats": {                       // StatsDto
    "ticks": 30, "collisions": 0, "arrived": 4, "replans": 2,
    "status": "Completed",         // "Completed" | "CollisionDetected" | "DidNotConverge"
    "collisionTick": null,         // set only when status == CollisionDetected
    "collisionAgentIds": null      // the agents involved, else null
  }
}
```

`PositionDto.State` is the stringified `AgentMotionState`: `Waiting` (not yet granted right-of-way, sitting at start), `Moving` (en route), `Arrived` (`SimulationResultDto.cs:43-47`). The frontend animates by stepping `timeline.frames` and drawing each `position` at `(x,y)` coloured by the agent's `colorIndex`.

---

## 7. Tests

The closed-loop integration tests live in `tests/SwarmRoute.Integration.Tests/ClosedLoopIntegrationTests.cs` and exercise this exact driver — they call `FleetLoopDriver.RunToCompletionAsync` **directly** (the production method was extracted from these tests), wiring the **real** coordination host with a `ManualFleetClock` (`advanceClock: clock.SetTick`) over a real `RoadmapGraph` (`FakeRoadmapQueryService` chains/intersections, and `GridFieldFactory` grids). Three scenarios, each asserting `Stats.Collisions == 0`, `Stats.Arrived == fleet.Count`, and **no lease leak** (`AssertNoLeasesLeak`):

- `ClosedLoop_IndependentAgents_AllReachGoals_InParallel_NoCollision_NoLeak` — disjoint corridors; also asserts `MaxConcurrentEnRoute >= 2` (genuine parallelism).
- `ClosedLoop_IntersectionCrossing_SerialisedThroughCentre_BothReachGoals_NoCollision_NoLeak` — a shared `+` intersection serialised through the centre.
- `ClosedLoop_FourAgents_PerimeterRotation_AllReachGoals_NoCollision_NoLeak` — four agents rotate a quarter-turn around a 4×4 perimeter; asserts concurrency.

All three are designed to converge inside their tick budgets, so they assert `Completed`/zero collisions. The `DidNotConverge` path and the `CollisionDetected` regression signal are first-class outcomes of the driver (§4) surfaced through `StatsDto` rather than thrown.

---

## 8. v0 status & v1 roadmap

**v0 (today).** Lockstep, discrete-tick execution: one tick = one CP hop, whole-path interval reservation via the real `CoordinationCycleService`, and a **conservative right-of-way gate** (a trailing vehicle waits a tick for the cell ahead) plus parked-vehicle rerouting. This is provably collision-free and reproducible (tick clock), and it reports dense-instance limits honestly as `DidNotConverge`. The cost is throughput: serialisation + stop-and-wait leave parallelism on the table.

**v1.** Replace whole-path reservation + stop-and-wait with **SIPP (Safe-Interval Path Planning)** and **schedule-faithful execution**: plan in *space-time* against the reservation intervals so agents are routed through free intervals rather than blocked at a cell, and execute the resulting schedule on the tick axis the `ManualFleetClock` already provides. This tightens throughput (more concurrent movers, fewer waits) while keeping the same two-layer collision-freedom guarantee and the same DTO/replay contract.

---

## Cross-context dependencies

| Depends on | What this context uses | Key types (`path:line`) |
|---|---|---|
| **SpatioTemporal.Kernel** (Shared) | The reservation time axis + resource refs | `IFleetClock` (`Shared/.../IFleetClock.cs:7`, `NowMs:10`), `ResourceRef`/`ResourceKind` (`Shared/.../ResourceRef.cs:30, 8`; `CP`, `Lane`), `TimeInterval` `[StartMs,EndMs)` (`Shared/.../TimeInterval.cs:11`), `SpaceTimePath` |
| **Map.Domain** | The roadmap graph + grid sites/lines | `RoadmapGraph.Build` (`Map/.../RoadmapGraph.cs:37`), `.SiteRef` (`:132`), `.Vertices` (`:75`), `.Neighbours` (`:87`); `MapSite`/`MapLine`/`MapPosition`/`MapSiteType.WorkSite` |
| **Map.Application.Contract** | The roadmap source seam (swapped in-memory) | `IRoadmapQueryService` — implemented by `InMemoryRoadmapQueryService` |
| **Coordination.Application** | The plan+reserve+release cycle (the engine) | `IFleetCoordinationCycle.RunCycleAsync` / `.ReleaseAsync` (`Coordination/.../IFleetCoordinationCycle.cs:22, 33`), `AgentGoal` (`:AgentGoal.cs:9`), `CycleReport`/`AgentCycleResult` (`Results`, `Reserved`, `Path`, `Attempts`), `CoordinationCycleService` (`:CoordinationCycleService.cs:37`), `AddCoordination` (`:CoordinationServiceCollectionExtensions.cs:39`) |
| **PathPlanning** (via DI) | Planner registered into the per-request container | `PathPlanningNativeInjectorBootStrapper.RegisterServices` (Host factory) |
| **TrafficControl** (via DI) | The singleton `ReservationTable` + default clock (replaced) | `TrafficControlNativeInjectorBootStrapper.RegisterServices`, `ReservationTable` singleton (`TrafficControl/.../TrafficControlNativeInjectorBootStrapper.cs:74`), `SystemFleetClock` (`TrafficControl/.../SystemFleetClock.cs:12`) |
| **EventBus** | In-memory dispatch inside the engine | `AddEventBus()` |
| **Host** (downstream) | Composes the engine + exposes the HTTP API | `InMemorySimulationEngineFactory` (`host/.../Adapters/InMemorySimulationEngineFactory.cs`), `SimulationController` (`host/.../Controllers/SimulationController.cs`), registration (`host/.../Program.cs:79-80`) |

*Project references: `SwarmRoute.Simulation.Application.csproj` references only Kernel, Map.Domain, Map.Application.Contract and Coordination.Application. PathPlanning/TrafficControl Infra are pulled in transitively through the **Host** factory (which calls their bootstrappers), keeping the Application assembly free of Infra/EF dependencies.*
