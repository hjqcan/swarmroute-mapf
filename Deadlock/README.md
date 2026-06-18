# Deadlock Handling (死鎖處理)

*A reactive bounded context that detects circular-wait deadlocks from TrafficControl's reservation contention graph and requests resolution — it never holds reservations or plans paths.*

---

## 1. Purpose & responsibility

The Deadlock context answers one question: **"given who currently *owns* and who currently *waits on* which resources, is the fleet stuck in a circular wait, and if so who should yield?"**

It is a **pure analyser**. Concretely it:

- **Reads** a point-in-time `ResourceAllocationGraphSnapshot` (the *Owns* / *Waits* edges) that TrafficControl produces — it never mutates TrafficControl, never holds its locks, never persists state. The seam comment is explicit: *"TrafficControl produces it; Deadlock consumes it (never mutating, never holding TrafficControl locks)"* (`Shared/SwarmRoute.SpatioTemporal.Kernel/ResourceAllocationGraphSnapshot.cs:4`).
- **Detects** circular waits by building a Resource-Allocation-Graph (RAG) and running cycle detection over it (`SwarmRoute.Deadlock.Domain/Services/RagDeadlockDetector.cs:24`).
- **Decides** a victim + strategy and **requests** resolution by raising integration events; the *fleet* (Coordination / TrafficControl / PathPlanning) reacts. Detour reservation itself is delegated back through a seam to TrafficControl's normal `TryReserve` path so the recovery move can never create a new collision (invariant **I1**).

What it explicitly does **not** do:

- It does **not** hold or grant reservations — that is TrafficControl's job (the resolution detour goes back through `IDetourReservationService` → TrafficControl).
- It does **not** plan paths — PathPlanning owns that.
- It has **no EF/DbContext**: deadlocks are transient, so cases are modelled as aggregates only to use the domain-event channel and optimistic-concurrency convention; nothing is stored (`SwarmRoute.Deadlock.Domain/Aggregates/DeadlockCase.cs:14`).

### Provenance (AJR.MAPF port)

This context is a clean-architecture re-implementation of the original `AJR.MAPF` deadlock subsystem:

| AJR.MAPF original | SwarmRoute Deadlock |
| --- | --- |
| `MapResourceAllocationGraph.GenerateGraph` | `ResourceAllocationGraph.Build()` (`.../ValueObjects/ResourceAllocationGraph.cs:107`) |
| `ConflictDetect.IndependenceDetection.DeadlockDetect` | `IDeadlockDetector` / `RagDeadlockDetector` (`.../Services/RagDeadlockDetector.cs`) |
| `CyclesDetector.CyclicVertices(graph, "agent_")` | reused verbatim (`src/vendor/SwarmRoute.Algorithms/Graphs/CyclesDetector.cs:175`) |
| stubbed `ISolver.Solve()` + `Recover()`, `ConflictSolveStateMachine` | `IDeadlockResolver` + `AvoidancePlan` state machine |
| "go to avoid point" recovery | `ResolutionStrategy.SendToAvoidSite` |

---

## 2. Layers & projects

Six projects, clean-architecture layering (dependencies point inward). Only `Domain` and `Application.Contract` carry a compile-time dependency on the shared Kernel — the context is buildable **standalone** (no compile-time edge to TrafficControl).

| Project | Role | Depends on |
| --- | --- | --- |
| `SwarmRoute.Deadlock.Domain.Shared` | Enums (`DeadlockKind`, `DeadlockCaseStatus`, `ResolutionStrategy`, `AvoidancePlanStep`) + `DeadlockErrorCodes`. | — (nothing) |
| `SwarmRoute.Deadlock.Domain` | RAG value object, cycle detection, aggregates, domain services + integration seams, domain events. | Kernel, `Domain.Abstractions`, `Domain.Shared`, NetDevPack, `SwarmRoute.Algorithms` (graph + `CyclesDetector`) |
| `SwarmRoute.Deadlock.Application.Contract` | `IDeadlockAppService` + DTOs (`DeadlockReportDto`, `DeadlockCycleDto`). | Kernel |
| `SwarmRoute.Deadlock.Application` | `DeadlockAppService` orchestrator, `AllocationContendedSubscriber`, `IDeadlockSnapshotProvider` consumer seam. | `Domain`, `Application.Contract` |
| `SwarmRoute.Deadlock.Infra.CrossCutting.IoC` | `DeadlockNativeInjectorBootStrapper` DI registration. | `Application` |
| `SwarmRoute.Deadlock.Tests` | xUnit unit tests. | `Domain`, `Application` |

Key external types: the graph machinery lives under the `AJR.Platform.Algorithms.*` namespaces but is vendored locally at `src/vendor/SwarmRoute.Algorithms*` (`DirectedSparseGraph<T>`, `CyclesDetector`). The cross-context vocabulary (`ResourceRef`, `ResourceKind`, `ResourceAllocationGraphSnapshot`) is the **frozen** `SwarmRoute.SpatioTemporal.Kernel`.

---

## 3. Domain model

### `ResourceAllocationGraph` (value object) — `.../ValueObjects/ResourceAllocationGraph.cs`

The RAG is an **immutable value object** (`ValueObject`, equality by sorted owns/waits edge sets — `:152`). It adapts the frozen `ResourceAllocationGraphSnapshot` into a directed graph that cycle detection runs over. `FromSnapshot` validates that every agent/resource id is non-blank and trims them (`:71`, `:81`).

It carries two edge families, matching the AJR source's three vertex families (`agent_`, `occupySite_`, `applySite_` — `:39`):

- **Ownership** (held): `occupySite_<resource> → agent_<owner>` — "this resource is held by that agent" (`:140`).
- **Wait-for** (request): `agent_<waiter> → occupySite_<resource>` — "this agent is blocked on that resource" (`:144`).

The crucial modelling decision (faithful to AJR): **both** ownership and wait-for edges pivot on a *single shared* `occupySite_` vertex per resource, so an `agent → resource → agent → resource → …` path can close into a cycle. The `applySite_` marker vertices are added for fidelity/observability but carry **no edges** (`:133`). `ResourceKey` namespaces a resource as `Kind:Id` so a CP and a Lane with the same id are distinct vertices (`:150`).

`Build()` is two-phase (vertices first, then edges) because the vendored `DirectedSparseGraph<T>.AddEdge` silently returns `false` if an endpoint vertex is missing (`src/vendor/SwarmRoute.Algorithms.DataStructures/Graphs/DirectedSparseGraph.cs:167`) — adding all vertices first guarantees every edge lands.

#### ASCII: a 2-agent circular wait

`A` owns `r1` and wants `r2`; `B` owns `r2` and wants `r1`:

```
        owns                       wait
   ┌───────────────┐      ┌────────────────────┐
   ▼               │      ▼                     │
occupySite_CP:r1   │   occupySite_CP:r2         │
   │               │      │                     │
   │ owns          │ wait │ owns          wait  │
   ▼               │      ▼                     │
 agent_A ──────────┘    agent_B ────────────────┘
   (A waits r2)            (B waits r1)

Edges actually built:
  occupySite_CP:r1 → agent_A        (ownership)
  occupySite_CP:r2 → agent_B        (ownership)
  agent_A          → occupySite_CP:r2   (wait-for)
  agent_B          → occupySite_CP:r1   (wait-for)

Cycle through agent vertices:
  agent_A → occupySite_CP:r2 → agent_B → occupySite_CP:r1 → agent_A
```

`CyclesDetector.CyclicVertices(graph, "agent_")` returns `[agent_A, agent_B]`. (Verified by `ResourceAllocationGraphTests.Build_ProducesGraphThatCycleDetectorFlags`.)

### `DeadlockCycle` (value object) — `.../ValueObjects/DeadlockCycle.cs`

The set of agent ids in one circular wait, stored **without** the `agent_` prefix, deduplicated, and **sorted ordinal-ascending** so identity is deterministic regardless of discovery order (`:37`). `FromVertices` strips the `agent_` prefix from RAG vertex names (`:55`). This stable ordering is what makes victim selection reproducible (§6).

### Cycle-detection algorithm — `RagDeadlockDetector` (`.../Services/RagDeadlockDetector.cs`)

Two stages:

1. **Faithful AJR port.** Build the RAG, run `CyclesDetector.CyclicVertices(graph, "agent_")` (`:30`). The vendored detector does a directed DFS with a recursion stack and flags every `agent_` vertex *from which a cycle is reachable* (`src/vendor/.../CyclesDetector.cs:175`). This **over-approximates**: a starving waiter that merely queues *behind* a deadlock is flagged even though it is not part of any mutual wait.

2. **SwarmRoute refinement — partition into genuine cycles** (`PartitionIntoCycles`, `:63`). To make resolution actionable and to satisfy *"two independent cycles must be reported separately"*, the detector builds the **agent-blocking digraph** restricted to cyclic agents — edge `a → b` when `a` waits on a resource `b` owns (`:84`) — and returns its **non-trivial strongly-connected components**: an SCC of size ≥ 2, or a singleton with a self blocking-edge (`:99`). SCCs are found by an iterative (stack-based, no recursion-depth risk) **Tarjan** implementation (`StronglyConnectedComponents`, `:121`), with deterministic ordinal iteration order. A trivial singleton (the starving waiter) is dropped.

The reported cycles are ordered by smallest member agent id (`:46`), so detection is **deterministic across repeated runs** (asserted by `DeadlockDetectorTests.Detection_IsDeterministic_AcrossRepeatedRuns`).

### Aggregates

**`DeadlockCase`** (`.../Aggregates/DeadlockCase.cs`) — aggregate root for one detected deadlock. Lifecycle `Detected → Resolving → Resolved | Escalated`:

- `Detect(cycle)` opens a case in `Detected` and raises `DeadlockCaseDetectedEvent` (`:68`).
- `RequestResolution(victim, strategy, suggestedAvoidTarget?)`: `Detected → Resolving`, raises `DeadlockCaseResolutionRequestedEvent`; rejects a victim not in the cycle (`:105`, `:115`).
- `MarkResolved()`: `Resolving → Resolved`, raises `DeadlockCaseResolvedEvent` (`:130`).
- `Escalate(reason?)`: `Detected/Resolving → Escalated`, idempotent (`:146`).
- Carries `StateVersion` (checked-increment optimistic concurrency) per house convention (`:160`).

**`AvoidancePlan`** (`.../Aggregates/AvoidancePlan.cs`) — the forward-only recovery state machine (ports AJR `ISolver.Solve+Recover` / `ConflictSolveStateMachine`):

```
SelectVictim → SelectAvoidancePoint → ReserveDetour → DispatchToAvoid → ConfirmCleared → Recover → Completed
                                                                                                  ↘ Aborted (terminal failure)
```

Each `RecordX`/`AdvanceX` enforces the expected current step (`Expect`, `:152`) and bumps `StateVersion`. The aggregate is transport-agnostic: it records *what* was decided (victim, avoid site) and *how far* recovery progressed; the side effects are performed by the resolver via the seams and fed back in.

---

## 4. Reactive flow (event-driven)

The whole context is triggered by **one** inbound integration event and emits **three** outbound ones, all over the in-process event bus.

```
TrafficControl.ReservationTable
    │  request queued behind a held resource (or a stale request aged)
    │  AddDomainEvent(new AllocationContendedEvent(...))      [ReservationTable.cs:168 / :429]
    ▼
"TrafficControl.Allocation.Contended"  (IIntegrationEvent, v1)
    │  in-process bus → handler.CanHandle / HandleAsync
    ▼
AllocationContendedSubscriber.HandleAsync          [Application/Subscribers/AllocationContendedSubscriber.cs:42]
    │  (1) re-entrancy guard (AsyncLocal ScanDepth)  ── §see note
    │  (2) snapshot = IDeadlockSnapshotProvider.GetSnapshotAsync()
    ▼
DeadlockAppService.ScanAsync(snapshot)             [Application/Services/DeadlockAppService.cs:48]
    │  cycles = IDeadlockDetector.Detect(snapshot)
    │  for each cycle:
    │     case = DeadlockCase.Detect(cycle)         ── raises Deadlock.Case.Detected
    │     IDeadlockResolver.SolveAsync(case)        ── raises Deadlock.Case.ResolutionRequested
    │                                                   (and Resolved on recovery, or Escalate)
    │  drain case.DomainEvents → IIntegrationEventPublisher.PublishAsync(...)
    ▼
Published integration events (consumed by Coordination / TrafficControl / PathPlanning):
    • "Deadlock.Case.Detected"             (DeadlockCaseDetectedEvent,            v1)
    • "Deadlock.Case.ResolutionRequested"  (DeadlockCaseResolutionRequestedEvent, v1)  → victim + suggested avoid target
    • "Deadlock.Case.Resolved"             (DeadlockCaseResolvedEvent,            v1)
```

**Actual event types & names** (all `DomainEvent, IIntegrationEvent`, `Version = "v1"`):

| Direction | `EventName` | Type | Payload |
| --- | --- | --- | --- |
| **in** | `TrafficControl.Allocation.Contended` | `AllocationContendedEvent` (`TrafficControl/.../Events/AllocationContendedEvent.cs`) | reservationTableId, agentId, contendedRequestCount |
| out | `Deadlock.Case.Detected` | `DeadlockCaseDetectedEvent` (`.../Events/DeadlockCaseDetectedEvent.cs`) | caseId, kind, agentIds |
| out | `Deadlock.Case.ResolutionRequested` | `DeadlockCaseResolutionRequestedEvent` | caseId, victimAgentId, strategy, suggestedAvoidTarget |
| out | `Deadlock.Case.Resolved` | `DeadlockCaseResolvedEvent` | caseId, victimAgentId |

**The subscriber** (`AllocationContendedSubscriber.cs:19`) implements `IIntegrationEventHandler`. `CanHandle` matches strictly on `EventName == "TrafficControl.Allocation.Contended"` (`:37`). **v0 ignores the payload** — any contention triggers a full re-scan (`:57` remark).

**Re-entrancy guard.** A detour reservation can itself publish `Allocation.Contended` synchronously through the in-process bus; an `AsyncLocal<int> ScanDepth` short-circuits a nested scan to `DeadlockReportDto.Empty` so a resolution-in-flight cannot recursively open a second case (`AllocationContendedSubscriber.cs:23`, `:61`). This is the single most subtle piece of the flow and is covered by a dedicated test (§8).

**The "Commit" role.** Because the context has no DbContext, `DeadlockAppService` plays the role `BaseDbContext.Commit()` plays elsewhere: after running the cases it drains `Entity.DomainEvents` from every case and hands the integration-flagged subset to `IIntegrationEventPublisher.PublishAsync` itself (`DeadlockAppService.cs:79`).

---

## 5. Snapshot seam

`IDeadlockSnapshotProvider` (`.../Application/Abstractions/IDeadlockSnapshotProvider.cs:14`) is the **consumer-side** seam — Deadlock declares it so it stays buildable without a compile-time dependency on TrafficControl:

```csharp
Task<ResourceAllocationGraphSnapshot> GetSnapshotAsync(CancellationToken ct = default);
```

- **Standalone default:** `NullDeadlockSnapshotProvider` returns an empty (healthy) snapshot `new ResourceAllocationGraphSnapshot([], [])` (`:25`).
- **Integration adapter (Host):** `TrafficSnapshotDeadlockAdapter` (`host/SwarmRoute.Host/Adapters/TrafficSnapshotDeadlockAdapter.cs`) bridges the async Deadlock seam to TrafficControl's **authoritative, synchronous** `ITrafficControlSnapshotProvider.GetSnapshot()` (`TrafficControl/.../Services/ITrafficControlSnapshotProvider.cs:11`), wrapping it in `Task.FromResult`. TrafficControl is the single writer; Deadlock just reads. There, `Owns` = one edge per active lease, `Waits` = one edge per queued/contended request.
- **Test adapter:** `AllocationContendedSubscriberTests.EmptySnapshotProvider` is the in-test stand-in returning an empty snapshot.

The shape both sides agree on is the frozen Kernel record `ResourceAllocationGraphSnapshot(Owns, Waits)` where each edge is a `(string AgentId, ResourceRef Resource)`.

---

## 6. Resolution

Resolution is orchestrated by `IDeadlockResolver` → `AvoidanceDeadlockResolver` (`.../Services/AvoidanceDeadlockResolver.cs`), which walks an `AvoidancePlan` through the AJR avoidance/recovery state machine.

**`SolveAsync(case)`** (`:40`):

1. **SelectVictim** — `IVictimSelector` → `DeterministicVictimSelector`. Heuristic: operate on the smallest cycle; within it pick the **lexicographically-smallest (ordinal) agent id**, which is simply `cycle.AgentIds[0]` since the cycle is pre-sorted (`DeterministicVictimSelector.cs:30`). Deterministic by design — *the same deadlock always nominates the same victim*, which is what prevents **livelock** (R6).
2. **SelectAvoidancePoint** — `IAvoidancePointSelector.SelectAvoidancePoint(victim)`.
3. `case.RequestResolution(victim, SendToAvoidSite, avoidSite)` — raised **unconditionally** so Coordination always learns the intended victim, *even if* the detour later fails (`:59`).
4. If no avoid site → `plan.Abort` + `case.Escalate` (`:61`).
5. **ReserveDetour** — `IDetourReservationService.TryReserveDetourAsync(victim, avoidSite)`; on denial → abort + escalate (`:72`).
6. **DispatchToAvoid** — record dispatched; plan is now at `ConfirmCleared`.

**`Recover(case, plan)`** (`:88`): only from `ConfirmCleared`; checks `IClearanceConfirmer.IsCleared(victim)`; on success walks `ConfirmCleared → Recover → Completed` and `case.MarkResolved()` (raising `Deadlock.Case.Resolved`).

### Strategies

`ResolutionStrategy` (`Domain.Shared/Enums/ResolutionStrategy.cs`): **`SendToAvoidSite` (v0 baseline, implemented)**; `Preempt` and `Requeue` are declared but *reserved for later evolution* (not used in v0).

### Integration seams — what is stubbed vs implemented

Three seams are declared in the Deadlock **domain** but deliberately left without production implementations there (TrafficControl/Map own the real work). Each ships a `Null*` default:

| Seam | Standalone default (in this context) | Host adapter (integrated) |
| --- | --- | --- |
| `IAvoidancePointSelector` | `NullAvoidancePointSelector` → always `null` → resolver escalates (`NullIntegrationSeams.cs:9`) | `MapAvoidancePointSelector` — picks a free `AvoidSite`/`RelaySite` from the active roadmap, respecting topology closure + blacklist, ordinal-deterministic (`host/.../Adapters/MapAvoidancePointSelector.cs`) |
| `IDetourReservationService` | `NullDetourReservationService` → always `false` (`:20`) | `TrafficDetourReservationAdapter` — reserves the avoid site via TrafficControl's `ITrafficCoordinatorAppService.TryReserveAsync` for a bounded 60 s window, so the detour respects every lease and cannot collide (`host/.../Adapters/TrafficDetourReservationAdapter.cs`) |
| `IClearanceConfirmer` | `NullClearanceConfirmer` → optimistic `true` (`:35`) | *(not yet overridden in Host — still optimistic in v0)* |

So in a **standalone** build, `SolveAsync` always escalates (no avoid site) — but the victim/strategy and the `ResolutionRequested` event are *still produced*, which is the intended "Deadlock analyses, the fleet acts" contract. The detour adapter's own comment flags that v0 is only a bounded destination hold, not a full path-to-avoid reservation, because the victim's current pose belongs to a not-yet-present fleet-state/dispatch integration.

---

## 7. Composition / wiring

`DeadlockNativeInjectorBootStrapper.RegisterServices(...)` (`.../Infra.CrossCutting.IoC/DeadlockNativeInjectorBootStrapper.cs`) follows the house `*NativeInjectorBootStrapper` convention with both a `WebApplicationBuilder` overload (`:31`) and a web-agnostic `IServiceCollection` overload (`:42`). `RegisterCore` (`:49`):

- **Domain (always concrete):** `IDeadlockDetector → RagDeadlockDetector`, `IVictimSelector → DeterministicVictimSelector`, `IDeadlockResolver → AvoidanceDeadlockResolver` (`AddScoped`).
- **Integration seams via `TryAdd`:** `IAvoidancePointSelector`, `IDetourReservationService`, `IClearanceConfirmer`, `IDeadlockSnapshotProvider` get their `Null*` defaults — so the context is fully resolvable standalone, **and** the Host can override each simply by registering a real adapter (the explicit registration wins because the bootstrapper used `TryAdd`).
- **Application:** `IDeadlockAppService → DeadlockAppService`; `AllocationContendedSubscriber` registered concretely *and* as `IIntegrationEventHandler` via a factory delegate (`:65`) so the in-process bus discovers it through `GetServices<IIntegrationEventHandler>()`.
- **Intentionally NOT registered here:** `IIntegrationEventPublisher` — owned by the EventBus/Host wiring (`:27`).

**Host order matters** (`host/SwarmRoute.Host/Program.cs`): `AddEventBus()` (step 1, supplies the in-process publisher + dispatcher) → `DeadlockBootStrapper.RegisterServices` (step 5, the `TryAdd` Null seams) → step 6b explicitly registers `TrafficSnapshotDeadlockAdapter`, `MapAvoidancePointSelector`, `TrafficDetourReservationAdapter` *after* the bootstrapper so they override the nulls (`Program.cs:69-71`).

The in-process publisher (`Shared/SwarmRoute.EventBus/InProcessIntegrationEventPublisher.cs`) filters to `IIntegrationEvent`, fetches all `IIntegrationEventHandler`s in scope, and dispatches via `CanHandle`/`HandleAsync` — swallowing/logging handler exceptions so one failing subscriber cannot break the publish loop (`:53`). A CAP/RabbitMQ host can later bind the same handler to the same event name with no application change.

---

## 8. Tests (`SwarmRoute.Deadlock.Tests`, xUnit)

Pure in-memory tests — no host, no DB. A fluent `SnapshotBuilder` fabricates RAG snapshots (`SnapshotBuilder.cs`, incl. `SnapshotBuilder.Cycle(n)` for a canonical n-agent ring); `Fakes.cs` provides a `CapturingIntegrationEventPublisher` plus "integrated" fakes (`FixedAvoidancePointSelector`, `AlwaysGrantDetourReservationService`, `StubClearanceConfirmer`).

| File | Covers |
| --- | --- |
| `ResourceAllocationGraphTests` | RAG build: agent/`occupySite`/`applySite` vertices + ownership/wait edges; resource key includes `Kind`; value equality is by edge sets (order-independent) and distinguishes Owns from Waits; that the built graph is what `CyclesDetector` flags. |
| `DeadlockDetectorTests` | 2/3/4-agent cycles flag all members; acyclic snapshot → none; everyone-holds-nobody-waits → none; a self-cycle (own+wait same resource) is flagged deterministically; **two independent cycles reported separately**; an extra non-cyclic waiter (`Z`) is excluded; **determinism across repeated runs**; null snapshot throws. |
| `VictimSelectionTests` | Smallest-ordinal-id victim, stable regardless of input order, ordinal (not numeric/length) comparison (`"10" < "2"`), repeated calls identical. |
| `DeadlockCaseTests` | Lifecycle: `Detect` raises `Deadlock.Case.Detected`; `RequestResolution` → `Resolving` + event, rejects victim-not-in-cycle; `MarkResolved` from `Resolving` (and throws from `Detected`); `Escalate` idempotent; empty cycle rejected. |
| `AvoidancePlanTests` | Starts at `SelectVictim`/version 1; full happy path to `Completed` bumping version each step; out-of-order transition throws; blank avoid site rejected; `Abort` records reason; abort-when-terminal is a no-op. |
| `DeadlockResolverTests` | With integrated seams → victim `A` dispatched to avoid site, case `Resolving`; `Recover` completes + resolves; recover when not cleared does nothing; **no avoid site → escalate (victim still chosen)**; **detour denied → escalate**. |
| `DeadlockAppServiceTests` | Healthy snapshot → empty report, nothing published; 2-agent cycle → opens case, picks victim, publishes both `Detected` + `ResolutionRequested`; two independent cycles → two reported; **without integrated seams the victim is still reported via `ResolutionRequested`** (escalation path). |
| `AllocationContendedSubscriberTests` | The **re-entrancy guard**: a reservation that re-publishes `Allocation.Contended` mid-scan does *not* trigger a nested scan (`ScanCount == 1`). |

---

## 9. v0 status & v1 roadmap

### Implemented in v0

- RAG construction faithful to `AJR.MAPF.MapResourceAllocationGraph`, reusing the vendored `CyclesDetector.CyclicVertices`.
- **Cyclic** deadlock detection with SCC-based partitioning into independent, deterministically-ordered cycles (over-approximation correctly pruned).
- Deterministic victim selection (livelock-free).
- Full `DeadlockCase` + `AvoidancePlan` lifecycles with optimistic-concurrency versioning.
- Reactive trigger (`AllocationContended` → scan) with a re-entrancy guard, and the three outbound integration events.
- Real Host adapters for the snapshot read, avoid-point selection, and detour reservation (going through TrafficControl's `TryReserve` — invariant I1 honoured).

### Deferred / stubbed (v1+)

- **`DeadlockKind.Livelock`** — declared but *not detected* by RAG cycle detection in v0 (`DeadlockKind.cs:14`). Detecting "moving but no net progress" needs temporal/progress signals, not a static RAG.
- **`ResolutionStrategy.Preempt` / `Requeue`** — declared, unused; only `SendToAvoidSite` is wired.
- **`IClearanceConfirmer`** — still the optimistic `NullClearanceConfirmer` even in the Host; real confirmation should re-snapshot / re-detect that the cycle actually cleared.
- **Detour completeness** — `TrafficDetourReservationAdapter` reserves only a bounded *destination hold*, not the full path-to-avoid-site, pending the fleet-state/dispatch integration (victim pose).
- **Persistence** — none; cases are transient by design. If audit/history is ever required, the aggregates are already event-sourcing-friendly.
- **Transport** — in-process event bus only; the handler is shaped so a CAP/RabbitMQ binding needs no application change.

---

### Cross-context dependencies (summary)

- **Consumes (in):** `TrafficControl.Allocation.Contended` (`AllocationContendedEvent`) — the trigger; `ITrafficControlSnapshotProvider.GetSnapshot()` — the RAG read, via `TrafficSnapshotDeadlockAdapter`; `ITrafficCoordinatorAppService.TryReserveAsync` — the detour write, via `TrafficDetourReservationAdapter`; Map's roadmap (`IRoadmapRepository` + `IResourceTopology`, `MapSiteType.AvoidSite/RelaySite`) — avoid-point selection, via `MapAvoidancePointSelector`.
- **Produces (out):** `Deadlock.Case.Detected`, `Deadlock.Case.ResolutionRequested`, `Deadlock.Case.Resolved` — consumed by Coordination / TrafficControl / PathPlanning.
- **Shared kernel:** `SwarmRoute.SpatioTemporal.Kernel` (`ResourceRef`, `ResourceKind`, `ResourceAllocationGraphSnapshot`) and `SwarmRoute.Domain.Abstractions.EventBus` (`IIntegrationEvent`, `IIntegrationEventHandler`, `IIntegrationEventPublisher`).
