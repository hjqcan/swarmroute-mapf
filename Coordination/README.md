# Coordination (協調)

*The fleet's rolling-horizon control loop — the orchestrator that composes Map + PathPlanning + TrafficControl into one RHCR-style "plan → reserve → prune-and-replan" tick, and ticks it for the life of the fleet.*

---

## 1. Purpose & responsibility

Coordination is the **orchestrator** of the system: it is where the multi-robot path-planning problem is actually *solved* online, one rolling-horizon tick at a time. It owns **no domain state of its own** — no roadmap graph, no reservation table, no planner. It holds *references to other contexts' contracts* and sequences calls across them:

1. ask **Map** for the roadmap graph,
2. ask **TrafficControl** for the current reservation view,
3. ask **PathPlanning** to plan a single-agent path over that graph + view,
4. ask **TrafficControl** to take right-of-way for the whole path, and
5. on denial, prune the contended resources and replan — bounded.

This is the clean-architecture descendant of the first-generation engine's CBS "couldn't lock the path → wait / replan" behaviour (`IFleetCoordinationCycle`, `Coordination/SwarmRoute.Coordination.Application/IFleetCoordinationCycle.cs:3-10`). The contract is split deliberately into a **testable cycle body** (`IFleetCoordinationCycle`) and a **lifelong hosted driver** (`FleetCoordinationLoop`), so the control logic can be asserted without a timer, a host, or wall-clock time.

The whole context is one application-layer service plus its DTOs and IoC bootstrapper. It is pure orchestration: every piece of MAPF *mechanism* (search, reservation, topology) lives behind a contract in another context.

---

## 2. Layers & projects

There is exactly **one project**: `SwarmRoute.Coordination.Application` (`Coordination/SwarmRoute.Coordination.Application/`). There is **no `Coordination.Domain`** — confirmed: Coordination has no invariants or aggregates of its own; its "domain" is the choreography itself, and the rules it enforces (determinism, bounded retry) are properties of the *loop*, not of any entity. This is application-layer orchestration in the strict DDD sense.

| File | Role |
|---|---|
| `IFleetCoordinationCycle.cs` | The testable loop-body contract: `RunCycleAsync` (one tick) + `ReleaseAsync` (resource hand-back). |
| `CoordinationCycleService.cs` | The default implementation — wires the four contexts into one cycle (`:37`). |
| `AgentGoal.cs` | One agent's goal for a cycle: `(AgentId, FromSiteId, ToSiteId, Priority)` (`:9`). |
| `CycleReport.cs` | `AgentCycleResult` (per-agent outcome) + `CycleReport` (the cycle's result, with `ReservedAgentIds` / `ContendedAgentIds` / `UnplannableAgentIds` projections). |
| `FleetCoordinationLoop.cs` | The hosted `BackgroundService` watchdog + `CoordinationLoopOptions`. |
| `ICoordinationGoalSource.cs` | The goal-book seam + `InMemoryCoordinationGoalSource` default. |
| `CoordinationServiceCollectionExtensions.cs` | Composition root: `AddCoordination(...)`. |

The `.csproj` references the contract + domain assemblies of Map, PathPlanning, TrafficControl, plus the Kernel. It does **not** reference the Liveness context at all: Coordination never invokes liveness handling from this loop — grant-time cycle **prevention** lives inside TrafficControl's `ReservationTable.TryGrant` (which consults Liveness's `IWouldCloseCycleDetector`), and physical-standoff **resolution** lives in the Simulation executor (`FleetLoopDriver`, which consults `ILivenessPolicy`). It pulls in only `Microsoft.Extensions.*` abstractions (Hosting, DI, Logging, Options) — no concrete infrastructure.

---

## 3. The cycle — `RunCycleAsync`

One `RunCycleAsync` call is **one rolling-horizon tick** over a set of goals (`CoordinationCycleService.cs:66`). It is the heart of the context.

**Per-cycle setup** (`:72-97`):

- Empty goal set → `CycleReport.Empty` short-circuit (`:73-74`).
- **Map**: read the graph once for the whole cycle — `_roadmaps.GetGraphAsync(roadmapId, …)` (`:77`); cached and in-process.
- **One timestamp for the whole cycle**: `cycleReleaseTimeMs = _clock.NowMs`, read **once** (`:80`). Every interval planned this cycle is expressed on this single fleet-clock instant, so the cycle's reservations all sit on the same time axis. The executor injects a tick-driven clock (see §7) so intervals line up with execution ticks.
- **Deterministic order**: goals are sorted by ascending `Priority`, then ordinal `AgentId` (`OrderBy(g => g.Priority).ThenBy(g => g.AgentId, StringComparer.Ordinal)`, `:83-86`). Lower `Priority` = higher right-of-way = planned/reserved first.
- Goals are then processed **sequentially**, each one's reservation committed before the next plans (`:89-94`) — this is prioritised planning: earlier agents' leases are visible to later agents.

**Per-agent inner loop** — `PlanAndReserveAsync` (`:99-198`), the bounded plan→reserve→prune→replan loop:

```
pruned ← blockedResources (the parked/obstacle cells), or ∅          (:110-112)
for attempt = 1 .. MaxReplanAttempts (= 8):                          (:118, :40)
    view    ← TrafficControl.GetView(roadmapId)        # re-read each attempt   (:120)
    request ← PlanRequest(roadmap, agent, from, to,
                          releaseTimeMs = cycle ts,
                          blacklistedResources = pruned)             (:121-127)
    plan    ← PathPlanning.Plan(graph, request, view)                (:129)
    if not plan.Success:                                             (:130)
        return UNPLANNABLE  (no route — maybe every alt was pruned)  (:131-145)
    outcome ← TrafficControl.TryReserveAsync(plan.Path, agent)       (:150)
    if outcome == Granted:
        return RESERVED  ✔                                           (:153-166)
    # Denied / Queued / Blocked:
    for r in TrafficControl.BlockedResources(plan.Path, agent):      (:170)
        if r is the agent's own start/goal CP:  skip                 (:173-176)
        pruned.add(r)                                                (:177)
    if pruned didn't grow:  break   # replanning would be a no-op    (:185-186)
# fell out of the loop:
return CONTENDED  (planned but no right-of-way; retried next tick)   (:190-197)
```

Salient design points, all in code:

- **`MaxReplanAttempts = 8`** (`:40`) — "1 initial + up to 7 prune-and-replans". The budget bounds the inner loop.
- **The view is re-read every attempt** (`:120`) so each replan sees the latest reservation state (earlier agents this cycle already committed).
- **Pruning is surgical, not whole-path.** Only the *concrete blocking* CP/Lane resources reported by `TrafficControl.BlockedResources(path, agent)` are added to the blacklist (`:170-177`) — not the entire failed path. This is what test `M2_Retry_PrunesOnlyBlockedResource_AndKeepsSharedPrefix` pins: a detour that shares the failed path's prefix must stay reachable.
- **Closure → prunable cell projection.** TrafficControl detects block/interference-closure conflicts internally but hands back only *planner-prunable* CP/Lane resources (`ITrafficCoordinatorAppService.BlockedResources`, contract at `TrafficControl/.../ITrafficCoordinatorAppService.cs:30-35`). So a `Block:Z` closure conflict comes back to Coordination as the candidate CP to delete — see `M2_Retry_ProjectsClosureBlockConflict_ToPlannerPrunableCell`.
- **Start/goal are never pruned** (`:173-176`) — pruning an agent's own endpoint would make the goal unreachable by construction.
- **No-op guard** (`:185-186`): if an attempt adds nothing new to `pruned`, the next plan would be identical, so the loop stops early and reports the agent contended.

**`blockedResources` (the parked-vehicle parameter).** `RunCycleAsync` takes an optional `IReadOnlySet<ResourceRef>? blockedResources` (`IFleetCoordinationCycle.cs:19-26`, impl `:69`). It **seeds the prune set** for *every* agent in the cycle (`:110-112`), so every plan routes around those cells — e.g. control points occupied by parked/completed vehicles. The effect: the fleet flows *past* finished agents instead of stalling behind them. (The executor populates this from arrived vehicles' goal cells — see §7.)

**The result.** Each agent yields an `AgentCycleResult(AgentId, Planned, Reserved, Outcome, Attempts, Path, FailureReason)` (`CycleReport.cs:21-28`). `Attempts` counts plan→reserve tries (1 + replans), which the executor uses as its replan metric. The cycle returns a `CycleReport` whose `Results` are in the deterministic processing order, with convenience projections `ReservedAgentIds` / `ContendedAgentIds` / `UnplannableAgentIds` (`CycleReport.cs:39-49`).

**`ReleaseAsync`** (`:201-205`) is a thin pass-through to `TrafficControl.ReleaseAsync` — the incremental, monotonic hand-back of leases an agent has driven past (only the past, invariant I6; each resource released with its parent-block + interference closure). Coordination adds no logic here; it just exposes the write seam to its caller.

---

## 4. The lifelong loop — `FleetCoordinationLoop`

`RunCycleAsync` is **one** tick. `FleetCoordinationLoop` (`FleetCoordinationLoop.cs:36`) is the **lifelong online-MAPF driver** that ticks it forever — the OpenTCS "Dispatcher" / RHCR rolling-horizon scheduler. It is an `IHostedService` (`BackgroundService`):

- On a `PeriodicTimer` watchdog at `CoordinationLoopOptions.TickInterval` (default 1s, `:14`), it loops `WaitForNextTickAsync` → `RunOnceAsync` (`:67-73`), cancellation-safe on shutdown.
- `RunOnceAsync` (`:88-110`) reads the **current goal book** from `ICoordinationGoalSource` (`CurrentRoadmapId`, `CurrentGoals`); when idle (no roadmap / no goals) it is a **safe no-op** returning `CycleReport.Empty` (`:92-93`). Otherwise it opens a **DI scope per tick**, resolves `IFleetCoordinationCycle`, and runs exactly one cycle (`:97-99`).
- **Resilience**: a cycle-internal exception is logged and swallowed (`:105-109`) so one bad tick never tears down the lifelong loop; only `OperationCanceledException` propagates.
- `EnableWatchdog = false` (`:21`) turns the timer off so the loop is **on-demand only** (tests / hosts that drive ticks explicitly call `RunOnceAsync`).

**How it differs from one cycle:** the loop adds *time, a goal source, and fault isolation*. The cycle is a pure function of `(roadmap, goals, view, clock)` → `CycleReport`; the loop supplies *when* (the watchdog tick), *what* (the live goal book), and *keep-alive* (swallow-and-continue). Liveness handling is **not** part of this inner loop: wait-for-cycle **prevention** happens at grant time inside TrafficControl (Liveness's `IWouldCloseCycleDetector`, consulted by `ReservationTable.TryGrant`), and physical-standoff **resolution** is the Simulation executor's job (`ILivenessPolicy`, consulted by `FleetLoopDriver`). Coordination just plans → reserves → prunes-and-replans (`:28-30`).

`ICoordinationGoalSource` (`ICoordinationGoalSource.cs:9-16`) is the seam between the loop and the order book: `CurrentRoadmapId` + a `CurrentGoals` snapshot. The default `InMemoryCoordinationGoalSource` (`:22-60`) is a thread-safe mutable book the Host (or a test) feeds via `Set`/`Clear`; with nothing set the loop idles. Keeping it separate from the loop is what lets the goal book be swapped (real dispatcher) without touching control logic.

**Composition** (`CoordinationServiceCollectionExtensions.cs`): `AddCoordination()` registers the cycle (`AddScoped<IFleetCoordinationCycle, CoordinationCycleService>`, `:39`) and the default goal source (`TryAddSingleton`, `:42-44`) but **not** the hosted loop. `AddCoordination(registerHostedLoop: true, …)` additionally wires `FleetCoordinationLoop` as a singleton hosted service (`:51-55`) so on-demand callers and the hosted lifecycle share one instance. The production Host calls `AddCoordination(registerHostedLoop: true)` (`host/SwarmRoute.Host/Program.cs:74`); the integration tests call plain `AddCoordination()` and drive `RunCycleAsync` directly. The bootstrapper assumes the four other contexts' bootstrappers have already registered their contracts (`:9-13`).

---

## 5. Clean-architecture seams (the dependency map)

Coordination depends on other contexts **only** via their `Application.Contract` interfaces + the shared Kernel — never on a concrete implementation, EF, or a broker. This is the load-bearing seam. The constructor (`CoordinationCycleService.cs:49-63`) takes exactly five collaborators:

| Dependency (interface) | Owning context | Used for |
|---|---|---|
| `IRoadmapQueryService.GetGraphAsync` | **Map** (`Map.Application.Contract.Services`) | The roadmap graph for the horizon (cached, in-process). `CoordinationCycleService.cs:77` |
| `IReservationQuery.GetView` | **PathPlanning** (declared) / **TrafficControl** (implemented) | The read-only reservation view the planner searches against, re-read per attempt. `:120` |
| `IPathPlanner.Plan` | **PathPlanning** (`PathPlanning.Domain.Planners`) | Single-agent space-time path over graph + view. `:129` |
| `ITrafficCoordinatorAppService.TryReserveAsync` | **TrafficControl** (`TrafficControl.Application.Contract.Services`) | Take whole-path right-of-way; returns `AllocationOutcome`. `:150` |
| `ITrafficCoordinatorAppService.BlockedResources` | **TrafficControl** | The planner-prunable resources actually blocking this path. `:170` |
| `ITrafficCoordinatorAppService.ReleaseAsync` | **TrafficControl** | Monotonic lease hand-back (pass-through). `:205` |
| `IFleetClock.NowMs` | **Kernel** (`SpatioTemporal.Kernel`) | The single per-cycle release timestamp. `:80` |

Note the deliberate **read/write seam split**: `IReservationQuery` (a *read* view) is **declared by PathPlanning** so the planner builds standalone against a `NullReservationQuery`, and **implemented by TrafficControl**, which overrides the registration with its reservation-table-backed view (`IReservationQuery.cs:5-22`). The *write* seam (`TryReserve`/`Release`) lives in `TrafficControl.Application.Contract` (`ITrafficCoordinatorAppService.cs:6-11`). Coordination is the one place that holds both ends — it reads via PathPlanning's view and writes via TrafficControl's coordinator. All currencies are Kernel value types (`ResourceRef`, `TimeInterval`, `SpaceTimePath`).

---

## 6. Determinism & liveness (no livelock)

This is the property the context exists to guarantee — captured in the XML docs as **R6 / ADR-003** (`CoordinationCycleService.cs:25-35`, `AgentGoal.cs:3-8`).

**Determinism.** Goals are processed in a *stable total order* — ascending `Priority`, then ordinal `AgentId` (`:83-86`). Because each agent's reservation is **committed before the next agent plans** (the sequential `foreach`, `:89-94`, plus per-attempt view re-read), the reservation table serialises the fleet **the same way on every run** for the same inputs. There is no non-determinism from ordering or concurrency inside a cycle. (Tests `M2_TwoAgents_…` pin this: the priority-0 agent wins the shared corridor, the priority-1 agent is `Queued`.)

**Bounded replanning ⇒ the inner loop always terminates.** Each prune-and-replan strictly *shrinks the search space*: the contended resources are added to the blacklist (`:170-177`) and never removed within the cycle, so the planner is solving a monotonically more-constrained problem each attempt. The loop exits via one of three terminating conditions:

1. **Granted** → reserved, done (`:153-166`);
2. **No route** → `plan.Success == false` (possibly because every alternative got pruned) → reported `UNPLANNABLE` (`:130-145`);
3. **Nothing new to prune** → the no-op guard breaks early (`:185-186`); or the `MaxReplanAttempts` budget is hit (`:118`) → reported **contended**.

So a cycle can **never** spin forever, and a denial degrades to a *reported* contended/unplannable outcome rather than a hang.

**Denial → wait → retry-next-tick.** Within a tick, a contended agent does **not** busy-wait — it is simply *not granted* this cycle and returned as contended (`:190-197`). Liveness comes from the *outer* loop: a holder releases the contended resource (executor `ReleaseAsync` as it drives past), and the **next** tick re-reads a now-freer view and the agent is granted (exactly the shape of test `M2_…_ThenSecondGrantedAfterRelease`: release the corridor, re-run the cycle, the queued agent is now `Granted`). Rolling horizon = denial is cheap and transient, retried on the next clock tick — not resolved by spinning inside one tick. Combined with the static `blockedResources` route-around for *permanent* obstacles (parked vehicles, §3), the fleet does not livelock behind either transient contention or finished agents.

---

## 7. Relationship to the executor (Simulation context)

Coordination decides **who may reserve which path and when**; it does **not** move vehicles or advance time. That is the **executor's** job — `FleetLoopDriver` in the **Simulation** context (`Simulation/SwarmRoute.Simulation.Application/FleetLoopDriver.cs`). The two compose cleanly:

- The executor **owns the tick and the clock.** Each tick it advances the fleet clock *before* planning (`advanceClock?.Invoke(tick)`, `FleetLoopDriver.cs:192`; the sim passes `ManualFleetClock.SetTick`, wired at `SimulationService.cs:61`). This is what makes the per-cycle `_clock.NowMs` (§3) land on the execution tick axis — coupling interval collision-freedom to actual motion.
- It **calls `RunCycleAsync`** for every idle agent, in deterministic priority order, and reads back the reserved paths (`FleetLoopDriver.cs:205-227`). Newly-reserved agents become en-route.
- It applies an **execution-time right-of-way gate** — "if a vehicle occupies the next CP, wait" — the final stop-and-wait that makes a same-CP collision impossible *by construction*, on top of the reservation table (`FleetLoopDriver.cs:230-294`).
- It **calls `ReleaseAsync`** incrementally as each agent leaves a CP/lane, and again on arrival to hand back the whole path (no leak) (`:289-293`, `:304`).
- It **feeds `blockedResources` back in**: arrived vehicles' goal cells go into a `parkedCells` set passed as the next cycle's `blockedResources` (`:202-205`, `:303`), closing the parked-vehicle route-around loop.
- A pathological standoff degrades to `FleetLoopStatus.DidNotConverge` (a *reported* outcome), never a crash (`:180-185`).

The full executor is documented in the **Simulation** context — this README only notes the seam. The key takeaway: **Coordination = planner/reservation brain (one tick is pure); Simulation's `FleetLoopDriver` = the clock-advancing, collision-gating executor that drives those ticks.**

---

## 8. Tests

Coordination's behaviour is verified by **integration tests** in `SwarmRoute.Integration.Tests` (no Postgres, no broker — the contexts' real DI bootstrappers over an in-memory graph-backed `IRoadmapQueryService`, assembled by `tests/SwarmRoute.Integration.Tests/TestSupport/CoordinationTestHost.cs`).

`tests/SwarmRoute.Integration.Tests/CoordinationCycleIntegrationTests.cs`:

| Test | What it pins |
|---|---|
| `M1_SingleAgent_PlansShortestPath_AndReservesGranted` | Topology → REAL planner path → REAL reservation `Granted`; path visits `A,B,C,D` in order. |
| `Cycle_UsesFleetClockReleaseTime_ForNewReservations` | Every reserved cell's interval starts ≥ the injected `IFleetClock.NowMs` — the per-cycle timestamp (§3). |
| `M2_TwoAgents_SharingCorridor_AreSerialised_ThenSecondGrantedAfterRelease` | Head-on corridor: priority-0 `Granted`, priority-1 `Queued`; after `ReleaseAsync` + re-run, the second is `Granted`. Determinism + denial→retry-next-tick. |
| `M2_Retry_PrunesOnlyBlockedResource_AndKeepsSharedPrefix` | Surgical pruning: the detour sharing the failed path's prefix is reserved (`Attempts ≥ 2`), the blocked lane `B-D` is avoided. |
| `M2_Retry_ProjectsClosureBlockConflict_ToPlannerPrunableCell` | A `Block:Z` *closure* conflict is projected to the prunable CP `B`; retry reroutes through `A-C-D`. |

The closed-loop end-to-end run (the executor driving many ticks to completion) is covered by `ClosedLoopIntegrationTests.cs` against `FleetLoopDriver` (Simulation context). Grant-time wait-for-cycle prevention — the only coupling between this loop's reservations and Liveness — is covered separately by `CyclePreventionIntegrationTests.cs` / `WouldCloseCycleDetectorTests.cs` (Liveness's `RagCycleDetector` screening `TryGrant`); there is no longer a reactive deadlock-scan test, because that flow was removed.

---

## 9. v0 status & v1 roadmap

**v0 (current).** The cycle runs against `DijkstraPathPlanner` — **space-only shortest path**. The planner is *handed* the reservation view but treats everything as free (`IPathPlanner.cs:7-11`, `CoordinationCycleService.cs:32-35`). So immediate, same-tick conflict avoidance is expressed entirely through the **CP/Lane blacklist** (whole-path lock via `TryReserve`, then prune-and-replan around the contended resources). `AllocationOutcome` produces `Granted` / `Queued` in v0; `Blocked` / `Preempted` are reserved in the frozen contract for v1+ (`AllocationOutcome.cs:7-12`). This faithfully ports the first-generation engine's "lock the whole path; on failure, blacklist and replan" loop into clean architecture.

**v1 (planned).** Swap `DijkstraPathPlanner` for a **SIPP** (Safe-Interval Path Planning) planner that *actually consults the reservation view in time*, routing around occupied intervals during search. The seam is already shaped for it: `IPathPlanner.Plan(graph, request, view)` is unchanged across v0→v1 (`IPathPlanner.cs:8-11`), and **this loop body does not change** — Coordination still plans → reserves → prunes-and-replans; SIPP simply makes the first plan time-aware so fewer attempts contend. The XML docs call this out explicitly (`CoordinationCycleService.cs:32-35`). Later versions extend toward priority planning / preemptive right-of-way (`Preempted`), again without reshaping the cycle.

---

### One cycle at a glance

```
                RunCycleAsync(roadmap, goals, blockedResources)
                                  │
          graph ← Map.GetGraphAsync(roadmap)          (once / cycle)
          ts    ← clock.NowMs                          (once / cycle)
          order goals by (Priority, AgentId)           (deterministic)
                                  │
          ┌────────── for each goal, in order ─────────┐
          │  pruned ← blockedResources                 │
          │  ┌── attempt 1..8 ──────────────────────┐  │
          │  │ view ← TrafficControl.GetView         │  │
          │  │ plan ← PathPlanning.Plan(graph,req,view)│ │
          │  │ if !plan: → UNPLANNABLE ──────────────┼──┼──▶ result
          │  │ outcome ← TrafficControl.TryReserve    │  │
          │  │ if Granted: → RESERVED ───────────────┼──┼──▶ result
          │  │ else: prune blocking resources; replan │  │
          │  │ if nothing new pruned: break           │  │
          │  └── (budget hit / no-op) → CONTENDED ────┼──┼──▶ result
          └────────────────────────────────────────────┘
                                  │
                          CycleReport(results)
```
