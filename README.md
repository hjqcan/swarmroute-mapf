# SwarmRoute MAPF — 多機路徑規劃系統

> A multi-robot (AGV) **lifelong path-planning & traffic-control** engine, built as a Domain-Driven set of
> bounded contexts on **.NET 10 / C# 14**, with an in-memory closed-loop simulator and a web visualizer.
> Robots travel from A to B across a shared roadmap **without ever colliding** — the whole system exists to
> make that guarantee, and to let you watch it happen.

SwarmRoute is a clean-architecture re-platforming of an early MAPF prototype (`third-party/AJR.MAPF`) onto the
DDD conventions of a reference codebase (`third-party/grukirbs`). The fleet-control problem is decomposed into
**four domains** the industry treats as separate concerns, plus an orchestration loop and an observable
simulation:

| 領域 / Context | What it owns | README |
|---|---|---|
| **Map — 資源 / 地圖** | The static roadmap: control points (CP), directed lanes, mutual-exclusion blocks; the in-memory `RoadmapGraph` read model. | [Map/README.md](Map/README.md) |
| **Path Planning — 路徑規劃** | Single-agent route search over the graph; lifts a route into a space-time path. | [PathPlanning/README.md](PathPlanning/README.md) |
| **Traffic Control — 交通管制** | Right-of-way: interval-based space-time reservations (who may occupy which resource, when); grant / deny / release. | [TrafficControl/README.md](TrafficControl/README.md) |
| **Deadlock Handling — 死鎖處理** | Reactive circular-wait detection from the reservation contention graph; requests resolution (detour / reroute). | [Deadlock/README.md](Deadlock/README.md) |
| **Coordination — 協調** | The RHCR rolling-horizon control loop that orchestrates the four domains into one fleet tick. | [Coordination/README.md](Coordination/README.md) |
| **Simulation — 模擬** | DB-free closed-loop **executor + verifier** and the HTTP replay API that drives the whole stack. | [Simulation/README.md](Simulation/README.md) |
| **SwarmRoute Web — 前端** | React/Vite "dispatcher's console" that runs scenarios and replays them on a canvas. | [host/swarmroute-web/README.md](host/swarmroute-web/README.md) |

Design background and the team plan live in [`docs/architecture-design.md`](docs/architecture-design.md) and
[`docs/team-implementation-plan.md`](docs/team-implementation-plan.md).

---

## Architecture

Clean architecture, one-way dependencies. Each context is a vertical slice
(`Domain.Shared → Domain → Application.Contract → Application → Infra.Data → Infra.CrossCutting.IoC → Api`)
and contexts talk **only** through `Application.Contract` interfaces and the shared **Kernel** — never across
each other's domain internals.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ swarmroute-web   React 19 / Vite — dispatcher's console, HTML5-canvas replay   │
└─────────────────────────────────┬──────────────────────────────────────────────┘
                                   │  POST /api/simulation/run   (Vite dev-proxy)
┌─────────────────────────────────▼──────────────────────────────────────────────┐
│ Host   ASP.NET Core — composition root, SimulationController, integration       │
│        adapters (topology / detour / deadlock-snapshot / in-memory engine)      │
└─────────────────────────────────┬──────────────────────────────────────────────┘
┌─────────────────────────────────▼──────────────────────────────────────────────┐
│ Simulation 模擬   FleetLoopDriver (tick executor + right-of-way gate),           │
│                   ManualFleetClock, per-request in-memory engine, replay DTO    │
└─────────────────────────────────┬──────────────────────────────────────────────┘
                                   │  RunCycleAsync / ReleaseAsync
┌─────────────────────────────────▼──────────────────────────────────────────────┐
│ Coordination 協調   RHCR rolling-horizon cycle — orchestrates the four domains   │
└────┬──────────────────────┬───────────────────────┬─────────────────────────────┘
     │ IRoadmapQueryService  │ IPathPlanner           │ ITrafficCoordinatorAppService
     │                       │ IReservationQuery      │           │  AllocationContended
┌────▼─────────┐   ┌─────────▼────────┐   ┌───────────▼──────┐    │   ┌───────────────┐
│ Map 資源/地圖 │   │ PathPlanning      │   │ TrafficControl   │────┼──►│ Deadlock 死鎖  │
│ RoadmapGraph │   │ 路徑規劃 (Dijkstra)│   │ 交通管制 (預約表) │◄───┼───│ 死鎖處理 (RAG)  │
└──────────────┘   └──────────────────┘   └──────────────────┘  snapshot └───────────────┘
       └───────────────────────┴───────────────────────┴────────────────┬───────────┘
┌─────────────────────────────────────────────────────────────────────────▼──────────┐
│ Shared / Kernel   SpatioTemporal.Kernel (ResourceRef, SpaceTimePath, TimeInterval,   │
│   IFleetClock, IReservationView, SafeInterval), EventBus, Domain.Abstractions,       │
│   Infra.Data.Core, StateMachine.Core, vendored graph algorithms, NetDevPack          │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

Key seams (the only ways contexts couple):

- **`IRoadmapQueryService`** (Map) → the `RoadmapGraph` read model consumed by PathPlanning, Coordination, Simulation.
- **`IPathPlanner`** (PathPlanning) → single-agent planning, called by Coordination.
- **`IReservationQuery`** — *declared* by PathPlanning (`NullReservationQuery` default), *implemented* by TrafficControl (`ReservationService`), which overrides the registration at composition time.
- **`ITrafficCoordinatorAppService`** (TrafficControl) → `TryReserve` / `Release` / `BlockedResources`, the write seam Coordination drives.
- **`AllocationContended`** integration event (TrafficControl → Deadlock) + a reservation **snapshot** read seam back the other way.
- **`IFleetClock`** (Kernel) → the time axis every reservation interval is expressed against.

---

## How the closed loop works

**One coordination cycle** (`CoordinationCycleService.RunCycleAsync`, the RHCR rolling horizon) — agents are
processed in a deterministic priority order; each one plans, tries to take right-of-way, and on denial prunes
the contended resources and re-plans within a bounded budget:

```
for each agent goal (ascending Priority, then ordinal id):
   graph  = Map.GetGraph(roadmap)
   view   = TrafficControl.ReservationView
   path   = PathPlanning.Plan(graph, request, view)        # Dijkstra, honours blacklist
   outcome= TrafficControl.TryReserve(path, agent)          # whole-path interval lock
   if Queued/Blocked:  prune the blocking CP/Lane → re-plan (bounded)   # = "wait / detour"
```

**The executor** (`FleetLoopDriver`, in the Simulation context) turns cycles into motion, one tick at a time,
and is the component that makes the run **observable and collision-free**:

```
tick 0 : record initial frame (every AGV waiting at its start)
each tick t:
   clock ← t                          # tick-driven ManualFleetClock (see below)
   plan + reserve idle agents          # via RunCycleAsync, parked vehicles passed as obstacles
   for each en-route agent (priority order):
       next = the agent's next CP
       if next is occupied this tick → WAIT   (right-of-way gate)
            └ if the blocker is a *parked* (arrived) vehicle → DROP reservation & re-route
       else → advance one CP, release the CP+lane behind
       on arrival → release all leases, mark the goal cell a parked obstacle
   record a frame
```

### The collision-freedom guarantee

Two independent layers, so a same-cell collision is impossible *by construction*:

1. **Reservation layer** — TrafficControl coordinates *who plans through which control point, and when*, via
   interval-exclusive leases.
2. **Executor right-of-way gate** — a vehicle never enters a control point another vehicle occupies this tick;
   it waits, or re-routes around a parked vehicle.

A genuine standoff therefore degrades to an honest **`DidNotConverge`** status — never a crash, never a
collision.

> **Why a tick clock matters.** Production's `SystemFleetClock` reports wall-clock milliseconds. Because cycles
> run sub-millisecond, two reservations the table considered time-separated could land on the same control
> point on the same *tick* — the reservation time axis and the execution axis were uncorrelated. The simulation
> injects a **`ManualFleetClock`** (one tick = one CP hop), putting reservations and execution on one axis and
> making the interval guarantee real and the run reproducible. This is the keystone of the closed loop — see
> [Simulation/README.md](Simulation/README.md).

---

## Tech stack

- **.NET 10.0 / C# 14**, EF Core 10, Npgsql/PostgreSQL (snapshot/audit only — the hot path is in-memory),
  CAP + Hangfire (integration events / jobs), AutoMapper, **NetDevPack** base classes (`Entity`,
  `IAggregateRoot`, `ValueObject`, `DomainEvent`, `IUnitOfWork`), vendored graph algorithms.
- **Frontend:** React 19 + TypeScript (strict) + Vite 7 + Tailwind 3 + Ant Design 6 (themed) + Zustand 5 +
  react-intl (zh-CN / en) + raw HTML5 Canvas.

## Repository layout

```
Map/  PathPlanning/  TrafficControl/  Deadlock/   # the four domains (each: per-context README + DDD layers)
Coordination/                                     # RHCR orchestration loop
Simulation/                                       # closed-loop executor + in-memory engine + replay DTOs
Shared/                                           # Kernel, EventBus, Domain.Abstractions, Infra.Data.Core, StateMachine.Core
host/SwarmRoute.Host/                             # ASP.NET Core composition root + Simulation HTTP API + adapters
host/swarmroute-web/                              # React/Vite simulator & visualizer
tests/                                            # SwarmRoute.Integration.Tests (closed-loop + cycle integration)
docs/                                             # architecture design + team plan
src/vendor/                                       # vendored graph data-structures/algorithms
third-party/                                      # reference material: AJR.MAPF (prototype) + grukirbs (DDD reference)
lib/                                              # NetDevPack
SwarmRoute.Mapf.sln
```

Each domain also has unit tests (`SwarmRoute.<Context>.Tests`); the cross-context behaviour is covered by
`tests/SwarmRoute.Integration.Tests`.

---

## Getting started

### Prerequisites
- .NET 10 SDK
- Node.js + npm (for the frontend)
- **No database required** — the simulation API and the integration tests run entirely in-memory. (PostgreSQL
  is only needed for the EF-backed Map import/snapshot paths, which the simulator does not touch.)

### Build & test
```bash
dotnet build                       # whole solution
dotnet test                        # full suite (Map, PathPlanning, TrafficControl, Deadlock, Integration)
```

### Run the backend (no DB)
```bash
ASPNETCORE_URLS=http://localhost:5062 dotnet run --project host/SwarmRoute.Host
```

Drive a simulation directly:
```bash
curl -s -XPOST localhost:5062/api/simulation/run \
  -H 'content-type: application/json' \
  -d '{"width":16,"height":16,"agvCount":12}'
# → { field, agents, timeline{frames[{tick, positions[…]}]}, stats{status, collisions, arrived, replans, …} }
```
`stats.status` is `Completed` | `CollisionDetected` | `DidNotConverge`. (`CollisionDetected` is a regression
indicator — the engine is collision-free by construction; dense, infeasible packings report `DidNotConverge`.)

### Run the web simulator
```bash
cd host/swarmroute-web
npm install
npm run dev            # http://localhost:5173  (Vite proxies /api → http://localhost:5062, no CORS)
```
Set a field size + AGV count, press **运行 (Run)**, and watch each AGV travel A→B along its planned path while
the **space-time reservation ribbon** shows why no two ever share a control point.

---

## Status & roadmap

**v0 (current).** End-to-end closed loop, fully green, collision-free by construction:
- Dijkstra shortest-path planning + blacklist-driven prune-and-replan.
- Whole-path, interval-based reservations (SIPP-ready model).
- Reactive deadlock detection over the resource-allocation graph.
- Tick-synchronous executor with a right-of-way gate and parked-vehicle rerouting.
- In-memory simulation API + canvas replay frontend.

Verified across a density sweep (10×10, 16×16, 20×20 grids; up to dozens of AGVs; many seeds): **zero
collisions everywhere**; typical densities complete; only genuinely over-packed fields report `DidNotConverge`.

**v1 (planned).** Swap the planner for **SIPP** (safe-interval path planning) behind the unchanged
`IPathPlanner` seam, and move to schedule-faithful execution — coordinating crossings *in time* for higher
throughput, with the same contexts, contracts, and loop body.

---

## Provenance

- `third-party/AJR.MAPF` — the original MAPF prototype (the real logic lived in `AJR.Platform.Minimal`); its
  CBS "couldn't lock the path → wait / replan" behaviour is re-expressed here as the Coordination
  prune-and-replan loop, and its `GraphMap` lock/unlock as TrafficControl's interval leases.
- `third-party/grukirbs` — the DDD / clean-architecture reference whose layering and NetDevPack conventions
  this codebase mirrors.
