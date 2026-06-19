# Traffic Control (交通管制)

*Owns right-of-way for the fleet: who may occupy which roadmap resource over which time interval — grant, deny, release. At grant time it consults Liveness's `IWouldCloseCycleDetector` so a lease that would close a wait-for cycle is refused. It does not plan paths and does not run the fleet loop.*

---

## 1. Purpose & responsibility

Traffic Control is the bounded context that arbitrates **space-time occupancy** of the roadmap. Its single source of truth is one in-memory aggregate, `ReservationTable` (`SwarmRoute.TrafficControl.Domain/Aggregates/ReservationTable.cs:29`), holding the live set of **interval leases**: `(resource × time interval × agent)` triples. Around it sit stateless domain services that answer three questions:

- **May this whole path be reserved for this agent right now?** → `IResourceAllocator.Allocate` / `ReservationTable.TryGrant` (`ReservationTable.cs:129`).
- **What is free, and when?** → `IReservationCalendar` / `FreeIntervals` / `IsFree` (`ReservationTable.cs:271`, `:323`), exposed read-only to PathPlanning as an `IReservationView`.
- **Who is blocking whom?** → `ITrafficControlSnapshotProvider` builds the `ResourceAllocationGraphSnapshot` (Owns/Waits edges) Liveness's `RagCycleDetector` runs cycle detection over.

It is the DDD successor to the original engine's mutable `GraphMap` (`_sites/_lines/_blocks` status + `_agvPathDic`), re-expressed from a binary `Locked/Unlocked` flag to a genuine time-interval lease (`ReservationTable.cs:11-16`). Two responsibilities it deliberately does **not** own: path search (PathPlanning) and the control/execution loop (Coordination + Simulation). Traffic Control only says *yes/queued/blocked* and *released*.

### Core design decisions (the "why")

| Decision | Rationale | Where |
|---|---|---|
| **Interval leases, not simple locks** | A lock answers "is it held?"; a lease answers "held by whom, for which window, in which lifecycle state". This is the time axis the v0 engine lacked, and the data SIPP needs at v1 without a model change. | `ResourceLease.cs:7-17`, `TimeInterval.cs` |
| **Single-writer singleton aggregate** | One fleet, one clock, one authoritative table → no distributed lock, no merge conflicts. Registered `AddSingleton` (invariant I5). | `TrafficControlNativeInjectorBootStrapper.cs:73-74` |
| **Hot path in-memory; EF only for audit** | Reservation grant/release happens thousands of times per plan; a DB round-trip would dominate. EF persists snapshot/audit rows off the hot path (ADR-002 / R2). | `ReservationAuditRecord.cs:3-9`, `TrafficControlDbContext.cs:11-16` |
| **Topology closure abstracted out of Domain** | The grant/release "what else must move with this resource" set (parent block + interference) is Map knowledge; abstracting it via `IResourceTopology` keeps `TrafficControl.Domain` free of any Map dependency. | `IResourceTopology.cs:5-19` |
| **Contended requests are RAG "Waits" edges** | Recording denials as `ReservationRequest`s exposes the wait-for graph (the snapshot Liveness's `RagCycleDetector` reads) and lets the escalation job age waiters → no starvation. | `ReservationTable.cs:39`, `ReservationRequest.cs:13-17` |
| **Grant-time cycle prevention via an injected port** | A wait-for cycle that never forms needs no recovery. `TryGrant` consults an injected `IWouldCloseCycleDetector` (implemented by Liveness's `RagCycleDetector`) and refuses a grant that would close a cycle — opt-in per run via the request's `PreventDeadlockCycles` flag; off = a Null detector = byte-identical baseline. | `IWouldCloseCycleDetector.cs`, `ReservationTable.cs` |

---

## 2. Layers & projects

Standard grukirbs/NetDevPack onion. Dependencies point inward; the Domain references only the Kernel and abstractions.

```
SwarmRoute.TrafficControl.Domain.Shared      enums + error codes, zero deps
        ▲
SwarmRoute.TrafficControl.Domain             aggregate, value objects, domain services, state machine
   refs: SpatioTemporal.Kernel, Domain.Abstractions, StateMachine.Core, NetDevPack, Domain.Shared
        ▲
SwarmRoute.TrafficControl.Application.Contract   frozen cross-context seams + DTOs
   refs: SpatioTemporal.Kernel, Domain.Shared
        ▲
SwarmRoute.TrafficControl.Application         app services, SystemFleetClock, topology adapter, subscriber
   refs: Domain, Application.Contract, PathPlanning.Domain (to implement IReservationQuery)
        ▲                                  ▲                                  ▲
Infra.Data (EF audit)        Infra.BackgroundJobs (Hangfire)        Infra.CrossCutting.IoC (composition)
        ▲                                  ▲                                  ▲
                              SwarmRoute.TrafficControl.Api (operator HTTP)
```

| Project | Role | Key dependencies |
|---|---|---|
| **Domain.Shared** | `AllocationOutcome`, `ConflictType`, `LeaseState`, `TrafficControlErrorCodes`. No project refs. | — |
| **Domain** | `ReservationTable` aggregate; value objects (`ResourceLease`, `ReservationRequest`, `Conflict`, `RightOfWay`); domain services (`ResourceAllocator`, `ConflictDetector`, `ReservationCalendar`) + their interfaces; `IResourceTopology`; `IWouldCloseCycleDetector` (+ `NullWouldCloseCycleDetector`, the grant-time prevention port Liveness implements); the lease `TrafficControlStateMachine` + guards. | `SpatioTemporal.Kernel`, `StateMachine.Core`, `NetDevPack` |
| **Application.Contract** | The three frozen seams: `ITrafficCoordinatorAppService` (write), `ITrafficControlSnapshotProvider` (read — the RAG snapshot Liveness's detector consumes), `ITrafficControlOperatorAppService` (operator); DTOs. | `SpatioTemporal.Kernel` |
| **Application** | `TrafficCoordinatorAppService`, `TrafficControlSnapshotProvider`, `TrafficControlOperatorAppService`, `ReservationService` (implements PathPlanning's `IReservationQuery`), `SystemFleetClock`, `DictionaryResourceTopology`, `ReplanTriggerSubscriber`. | `Domain`, `Application.Contract`, **`PathPlanning.Domain`** |
| **Infra.Data** | `TrafficControlDbContext` + `ReservationAuditRecord` — **snapshot/audit only**. EF Core + Npgsql. | `Domain`, `Infra.Data.Core` |
| **Infra.BackgroundJobs** | `LeaseExpirySweepJob`, `StaleRequestEscalationJob` (Hangfire recurring). | `Application`, `Hangfire.Core` |
| **Infra.CrossCutting.IoC** | `TrafficControlNativeInjectorBootStrapper` — composition root. | the three Infra/App projects |
| **Api** | `TrafficController` — operator HTTP endpoints (occupancy, allocation-graph, unlock). Hot-path reserve/release is **not** exposed over HTTP. | `Application.Contract`, IoC, `EventBus` |

All projects target **net10.0 / `LangVersion=latest` (C# 14)**, nullable + implicit usings enabled (`Directory.Build.props`, each `.csproj`). No central package management by team policy.

---

## 3. Domain model

### 3.1 `ReservationTable` aggregate — the authoritative live state

A `sealed` `Entity, IAggregateRoot` guarded by one `lock (_sync)` (single-writer; all mutators and snapshot reads take it). It keeps a **dual index** (`ReservationTable.cs:33-37`):

- `_byResource : Dictionary<ResourceRef, List<ResourceLease>>` — kept **sorted by `Interval.StartMs`** so free-interval math and conflict checks are local to a resource's bucket.
- `_byAgent : Dictionary<string, List<ResourceLease>>` — so release and RAG-snapshot are `O(agent's leases)`.
- `_contended : List<ReservationRequest>` — the queued requests, i.e. the "Waits" edges.

**The invariant (I):** no two **conflicting** leases coexist — *same (or closure/reversed-lane) resource, overlapping interval, different agents*. Same-agent overlapping/touching windows on one resource are **merged**, not duplicated. Every mutator preserves this and calls `Touch()` (`ReservationTable.cs:677`), which bumps `StateVersion` (optimistic concurrency) and stamps `StateChangedAtUtc`.

The invariant is enforced at the lowest level by `Insert` (`ReservationTable.cs:467-518`): before adding a lease it scans `LeasesConflictingWith(resource)` and **throws** `TrafficControlErrorCodes.ConflictingLease` if a different agent overlaps (`:470-477`). Insert is also where same-agent merging happens — overlapping/touching windows for the same agent+resource collapse into one union lease, and an exact-cover duplicate is a no-op returning `false` (`:493-517`). This is what makes `TryGrant` idempotent for an unchanged re-reservation.

**Conflict relation** (`ReservationTable.cs:613-614`): two resources conflict when they are `Equals` **or** `IsReversedLane` (`"a-b"` vs `"b-a"`, lane-kind only — `:622-638`). `LeasesConflictingWith` (`:601-611`) walks every held resource that conflicts with the query resource, which is how a request for lane `A-B` correctly sees an incumbent on `B-A`.

#### Key operations

| Method | Contract | Notes |
|---|---|---|
| `TryGrant(path, agentId, priority)` | whole-path lock → `Granted` / `Queued` / `Blocked` | `:129` — see §5 |
| `ReleaseBehind(agentId, passedResources)` | free leases on passed resources **+ their closure**; returns freed; partial | `:206` — the `UnlockPath` leak fix |
| `ReleaseAll(agentId)` | free every lease + drop the agent's contended requests | `:239` — abort/arrival |
| `FreeIntervals(resource)` | maximal safe intervals over `[0, long.MaxValue)` | `:271` — complement of lease union |
| `IsFree(resource, interval)` | agent-agnostic: any overlap by anyone? | `:323` — view semantics |
| `IsFreeForExcept(resource, interval, agentId)` | free ignoring the agent's own leases | `:336` — used by allocator pruning |
| `Refresh(nowMs)` | evict fully-elapsed leases; prune expired contention | `:354` — sweep-job safety net |
| `RecordContention` / `ReplaceContended` / `EscalateStaleRequests` | manage the Waits set + aging | `:375`, `:386`, `:405` |
| `DrainDomainEvents()` | atomically copy+clear buffered events under the lock | `:88` — app layer publishes |
| `CreateSnapshotView()` | immutable `IReservationView` using the same closure semantics | `:106` — handed to the planner |

#### Free-interval math

`FreeIntervals` (`ReservationTable.cs:271-317`) sweeps the resource's conflicting leases sorted by start, merging overlaps to find gaps, and emits a `SafeInterval` for each gap plus a tail `[cursor, long.MaxValue)`. Because intervals are **half-open** (`TimeInterval.Overlaps` is `Start < otherEnd && otherStart < End`, `TimeInterval.cs:39`), touching leases (`[0,100)` then `[100,200)`) do **not** merge the gap away and do **not** conflict — a vehicle may exit a cell exactly as the next enters. `IReservationCalendar.EarliestFreeStart` (`ReservationCalendar.cs:27-44`) walks these intervals for the first one that fits a requested duration — the seed of SIPP's "earliest arrival" step at v1.

### 3.2 Value objects

- **`ResourceLease`** (`ResourceLease.cs:18`) — immutable `(Resource, AgentId, Interval, State)`; equality by all four. Successor to `MapResource.OccupiedBy + Status`. `ConflictsWith` (`:50`), `WithState` (`:59`), `HasExpiredAt(nowMs)` (`:62`).
- **`ReservationRequest`** (`ReservationRequest.cs:18`) — a contended request: ports `AJR.MAPF.Map.ResourceRequest` (`AgentId`, `Resource`, `RequestTime`, `EstimateTime`, `HadWaitedTime`) and adds the v0+ `Requested` `TimeInterval` and an explicit `Priority`. `AgedBy(seconds)` (`:91`) and `MergedWith` (`:97`) keep repeated waits as a single edge.
- **`Conflict`** (`Conflict.cs:16`) — a detected `(Type, AgentA, AgentB, ResourceA, ResourceB)`. For vertex/following the two resources are equal; for edge-swap they are the opposing lanes; for interference the interfering pair.
- **`RightOfWay`** (`RightOfWay.cs:16`) — the deterministic tie-break rule: **Priority desc → HadWaitedTime desc → AgentId ordinal**. The third tier guarantees a *total, stable* order (no coin-flips), which is what keeps the loop free of live-lock (two agents must never repeatedly yield to each other). Stateless singleton (`Default`).

### 3.3 Resource kinds & closure

`ResourceRef(ResourceKind Kind, string Id)` is the frozen Kernel contract (`ResourceRef.cs:30`). Kinds: **CP** (control point/站点), **Lane** (directed edge/路段), **Block** (mutual-exclusion区块), **Zone** (region/区域).

The **closure** of a resource is "everything that must be locked/released together with it" — the resource itself + its parent block + its interference set — modelled by `IResourceTopology.ClosureOf` (`IResourceTopology.cs:20-27`, must include the resource itself) and `IsBlacklisted` (ports `MapResource.AGVBlackList`). Two implementations:

- `IResourceTopology.Empty` (`IResourceTopology.cs:39-44`) — identity closure, no blacklist; the v0 default and test baseline.
- `DictionaryResourceTopology` (`DictionaryResourceTopology.cs:19`) — data-driven, with a fluent `Builder`. Lives in **Application**, not Domain, so the Domain stays Map-free. The Host populates it (in practice via `MapResourceTopologyAdapter`) from Map's published interference/contained-site/blacklist data.

> **The release-leak fix.** The original `GraphMap.GeneratePath` locked each path resource *plus its `ParentBlock`* (and pruning pulled in the interference closure), but `GraphMap.UnlockPath` left the ParentBlock/interference release **commented out**, so blocks and interfered resources leaked forever. Here, **both** grant (`ExpandClosure`, `ReservationTable.cs:439-453`) and release (`ReleaseBehind`, `:217-223`) drive through the *same* `ClosureOf`, so the two are symmetric by construction. The regression is pinned by `ReleaseBehind_frees_parent_block_and_interference_closure_no_leak` (`ReservationTableTests.cs:118`).

---

## 4. Conflict taxonomy

`ConflictDetector` (`ConflictDetector.cs:27`) is stateless (singleton-safe) and classifies each clash between a candidate cell (resource `R`, interval `I`, agent `A`) and an incumbent lease held by a **different** agent over an overlapping interval. It consults `IResourceTopology` for interference so it stays decoupled from Map.

```
                 same resource?                       reversed lane?            closure member held?
candidate cell ──────┬───────────────────────┐     ┌──────────────┐          ┌────────────────────┐
                     │                        │     │              │          │                    │
        enters ≤ incumbent        enters > incumbent  "a-b" vs "b-a"      member ≠ R held by other
              │                        │                  │                        │
         VertexSame                Following           EdgeSwap                Interference
```

| Type | Predicate | Meaning | Origin | Code |
|---|---|---|---|---|
| **VertexSame** | same resource, overlap, candidate `StartMs ≤` incumbent `StartMs` | head-on / simultaneous occupation of one cell | MAPF vertex conflict | `ConflictDetector.cs:54-60` |
| **Following** | same resource, overlap, candidate `StartMs >` incumbent `StartMs` | trailing into a not-yet-cleared cell | MAPF following conflict | `ConflictDetector.cs:57-59` |
| **EdgeSwap** | both Lane, ids are reverses (`"a-b"`/`"b-a"`), overlap | two AGVs swap places on one physical edge | MAPF edge/swap conflict | `ConflictDetector.cs:62-65` |
| **Interference** | a closure member of `R` (≠ `R`) held by another over overlap | mutually-interfering resources occupied at once | AGV interference sites/lines | `ConflictDetector.cs:69-81` |

**Reversed-lane semantics.** Lane ids follow the engine's `"start-end"` convention, so the reverse of `"a-b"` is `"b-a"`. `IsReversedLane` (`ConflictDetector.cs:92-108`, mirrored in the aggregate at `ReservationTable.cs:622-638`) splits on the first `-` and checks `aStart==bEnd && aEnd==bStart`. This is applied **everywhere a resource is matched** — granting, `FreeIntervals`, `IsFreeForExcept`, and the snapshot — so an opposing-lane reservation is treated as the same physical edge. Crucially, the recorded Waits edge points at the **incumbent's** lane id, not the requester's (`ReservationTable.cs:155-160`; pinned by `Reversed_lane_overlap_is_queued...` `ReservationTableTests.cs:101` and `Reversed_lane_contention_waits_on_the_blocking_owned_lane` `SnapshotProviderTests.cs:70`).

> `ConflictDetector` is the *classifier* used by the lease state machine's `NoConflictGuard` and available for diagnostics; the *grant* path (`TryGrant`) does its own faster free/blacklist check rather than calling the detector. They share the same conflict relation, so they agree.

---

## 5. Allocation flow

### TryReserve (whole-path, all-or-nothing)

`ITrafficCoordinatorAppService.TryReserveAsync` → `ResourceAllocator.Allocate` → `ReservationTable.TryGrant` (`TrafficCoordinatorAppService.cs:37`, `ResourceAllocator.cs:21`, `ReservationTable.cs:129`). The flow inside `TryGrant`:

```
TryGrant(path, agent, priority)
  1. empty path?                         → Blocked
  2. ExpandClosure(path)                 → list of (member, interval) cells (parent block + interference)
  3. for each cell:
       blacklisted(member, agent)?       → record contended, mark blacklisted
       another agent overlaps (closure)? → record contended, mark contended  (Waits edge points at the blocker)
       [prevention on] would these Waits edges close a wait-for cycle?
                                         → _cycleDetector.WouldCloseCycle ⇒ refuse (mark contended)
  4. blacklisted → Blocked ;  contended → Queued
        (emit ReservationDenied + AllocationContended, create NO lease)
  5. all free → Insert() every closure cell  (Reserved)            ← whole-path lock
        drop this agent's stale Waits + prune now-satisfied waits
        emit ReservationGranted
        → Granted
```

This is the faithful port of the original whole-path lock: a path is granted **only if every resource (closure-expanded) is free for this agent**; otherwise *nothing* is locked and the request is queued (`WholePath_grant_then_crossing_path_is_queued`, `ReservationTableTests.cs:58`). Outcomes (`AllocationOutcome.cs`): **Granted**, **Queued** (contended; recorded as Waits), **Blocked** (blacklisted — never grantable as-is). `Preempted` is reserved for v1 and never produced today.

A successful grant also **prunes the agent's stale wait edges** (`ReservationTable.cs:182-183`) so the RAG reflects current contention, not retry history (`Successful_retry_removes_the_agents_stale_wait_edges`, `ReservationTableTests.cs:87`).

**Grant-time cycle prevention.** When the injected `IWouldCloseCycleDetector` is the real one (opt-in per run via the request's `PreventDeadlockCycles` flag), step 3 also asks Liveness's `RagCycleDetector.WouldCloseCycle(currentRag, agent, candidateWaitEdges)` (`ReservationTable.cs:194`): if granting would close a wait-for cycle, the request is refused (treated as contended) so the planner re-routes and the circular wait **never forms**. Off (the `NullWouldCloseCycleDetector` default) ⇒ byte-identical baseline. This replaced the former reactive *detect-cycle → request-resolution → redirect-a-victim* path entirely.

### Release-behind (monotonic, as the agent passes)

As an agent drives past resources, Coordination calls `ReleaseAsync(agentId, passedResources)` → `ReleaseBehind` (`TrafficCoordinatorAppService.cs:57`, `ReservationTable.cs:206`). It expands each passed resource to its closure, frees the agent's matching leases, prunes contended requests that are now satisfiable, and emits `ReservationReleasedEvent(partial: true)`. It only ever releases the *past* (monotonic; invariant I6) — the agent keeps its hold on cells ahead.

### ReleaseAll (arrival / abort)

`ReleaseAll(agentId)` (`ReservationTable.cs:239`) frees every lease the agent holds **and** drops its contended requests (it no longer waits on anything), emitting `ReservationReleasedEvent(partial: false)`.

### BlockedResources (prune-and-replan)

`ResourceAllocator.BlockedResources` (`ResourceAllocator.cs:29-45`) answers "which candidate-path resources should the planner delete and re-search?" It detects blockage across the full closure (`CellIsBlocked` checks blacklist + `IsFreeForExcept` for every closure member, `:47-59`) but returns **only planner-prunable CP/Lane resources** (`IsPlannerPrunable`, `:61-62`) — a block/interference resource is invisible to the planner, so the conflict is mapped *back* to the CP/Lane the planner can actually prune. Pinned by `BlockedResources_maps_block_contention_back_to_candidate_cp/lane` (`ResourceAllocatorTests.cs:11`, `:30`).

---

## 6. The fleet clock & time axis

Every `TimeInterval` is expressed against one monotonic **fleet clock**, `IFleetClock.NowMs` (Kernel, `IFleetClock.cs:7-11`). Two implementations exist, and the difference is load-bearing:

- **`SystemFleetClock`** (`Application/Services/SystemFleetClock.cs:9`) — production default: fleet time = `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`. Registered `AddSingleton<IFleetClock>` (`TrafficControlNativeInjectorBootStrapper.cs:71`). One shared instance gives the whole fleet one wall-clock axis. It drives `LeaseExpirySweepJob` (`LeaseExpirySweepJob.cs:31`).

- **`ManualFleetClock`** (Simulation, `Simulation/.../ManualFleetClock.cs:18`) — a discrete, externally-advanced clock. The `FleetLoopDriver` sets it to the current integer **tick** before each planning cycle (`SetTick`, `:24`), so every reserved interval lands on the *same* axis the executor advances on (one tick = one control-point hop).

> **Why the axis matters for collision-freedom.** The reservation table's interval-based guarantee ("no two conflicting leases overlap") is only a *real* guarantee at execution time if reservations and execution share a time axis. Under the wall-clock `SystemFleetClock`, control cycles run sub-millisecond, so two reservations the table considers *time-separated* could be executed on the same control point on the same tick — the model is correct but the axis is decoupled. The simulation therefore **overrides `IFleetClock` with the tick clock** (documented at `ManualFleetClock.cs:11-17`), removing the mismatch. *(Cross-references: Coordination drives the loop; Simulation owns the tick clock. Traffic Control owns only the production `SystemFleetClock` and consumes whichever `IFleetClock` the Host registers.)*

---

## 7. Events & integration

### Domain/integration events

All four extend NetDevPack `DomainEvent` and implement `IIntegrationEvent` (versioned `"v1"`). The aggregate buffers them; the app layer drains and publishes via `DrainAndPublishAsync` because the in-memory hot path never hits `BaseDbContext.Commit` (`TrafficCoordinatorAppService.cs:71-93`).

| Event | Raised when | `EventName` |
|---|---|---|
| `ReservationGrantedEvent` | whole-path grant created leases | `TrafficControl.Reservation.Granted` |
| `ReservationDeniedEvent` | grant queued or blocked (carries the outcome) | `TrafficControl.Reservation.Denied` |
| `ReservationReleasedEvent` | leases freed (`partial` = release-behind vs release-all) | `TrafficControl.Reservation.Released` |
| `AllocationContendedEvent` | a request became contended / was aged (carries Waits count) | `TrafficControl.Allocation.Contended` |

### Integration

- **→ Liveness (grant-time prevention, the live coupling).** Traffic Control does **not** publish a reactive deadlock-resolution event. Instead it depends on Liveness's cycle check *synchronously, inside the grant*: `ReservationTable` is constructed with an `IWouldCloseCycleDetector` (Liveness's `RagCycleDetector`), and `TryGrant` calls `WouldCloseCycle` before recording a contended wait edge (`ReservationTable.cs:194`, §5). A grant that would close a wait-for cycle is refused so the planner re-routes. This is opt-in per run (`PreventDeadlockCycles`) and defaults to the `NullWouldCloseCycleDetector` (off). *(The former `AllocationContendedSubscriber` reactive flow — pull a snapshot, run cycle detection, request a victim redirect — has been removed; that whole detect → resolve → redirect → recover path no longer exists.)*
- **`ITrafficControlSnapshotProvider` (read seam).** The `ResourceAllocationGraphSnapshot` it builds maps **active leases → Owns** and **contended requests → Waits** (`TrafficControlSnapshotProvider.cs:25-36`). It is the RAG that Liveness's `RagCycleDetector` runs over (grant-time prevention + post-hoc detection) and that the operator HTTP / observability surfaces expose — never held under a Traffic Control lock.
- **→ Coordination (stub).** `ReplanTriggerSubscriber` (`Application/Subscribers/ReplanTriggerSubscriber.cs:14`) is the v0 placeholder that will, at integration, carry a `ReservationDenied` payload into Coordination's replan queue. It compiles and is testable standalone; only the CAP transport binding is `TODO(integration)`.

### Anti-starvation / escalation (no live-lock, invariant I7)

Contended requests carry `HadWaitedTime`. `StaleRequestEscalationJob.Escalate` (`StaleRequestEscalationJob.cs:32`) → `ReservationTable.EscalateStaleRequests` (`ReservationTable.cs:405-433`) ages every outstanding request by `agingSeconds` (default 1), so a long-waiter's `RightOfWay` tie-break eventually beats fresher, equal-priority contenders. Each pass also prunes now-satisfiable requests and raises one `AllocationContendedEvent` (subject = longest-waiter) as an aging/diagnostic signal. Pinned by `StaleRequestEscalationJob_ages_contended_requests_and_emits_event` (`CalendarAndJobsTests.cs:48`).

### Lease lifecycle state machine

`TrafficControlStateMachine` (`Domain/StateMachine/TrafficControlStateMachine.cs:18`) models one lease over `LeaseState`: `Requested →(Grant)→ Reserved →(Enter)→ InTransit →(Pass)→ Releasing →(Release)→ Free`. The `Grant` transition is gated by composable guards that express the original "can I lock this?" predicate declaratively: `ResourceAvailableGuard` (no other agent overlaps), `NoConflictGuard` (wraps `IConflictDetector`), `NotBlacklistedGuard` (`Domain/StateMachine/Guards.cs`). The aggregate remains the authority on the table-wide invariant; this machine guards a single lease's transitions and is the v1 hook for per-lease lifecycle.

---

## 8. Persistence

There is **no EF on the reservation hot path.** The authoritative state is the in-memory singleton aggregate. `Infra.Data` exists only for snapshot/audit (ADR-002 / R2):

- `ReservationAuditRecord` (`Infra.Data/Entities/ReservationAuditRecord.cs:10`) — a deliberately **flat** row (not a mapped aggregate, so the hot path takes no EF dependency): `Id`, `ReservationTableId`, `StateVersion`, `AgentId`, `Action` (`Granted`/`Queued`/`Released`…), `LeaseCount`, optional `LeasesJson` (full snapshot), `CreatedAtUtc`.
- `TrafficControlDbContext` (`Infra.Data/Context/TrafficControlDbContext.cs:21`) — derives from `BaseDbContext` (for the standard UoW/event plumbing) but maps only `ReservationAudits`; `LeasesJson` is `jsonb`, indexed on `ReservationTableId` and `CreatedAtUtc`. `OnModelCreating` `Ignore<Event>()` so domain events are never persisted. Migration `20260618070926_InitialCreate` creates the single `ReservationAudits` table.
- `TrafficControlDbContextFactory` — design-time only, placeholder Npgsql connection string for `dotnet ef`.

The connection string (`"TrafficControlDatabase"`) may be **absent** at dev/design time — the bootstrapper registers the context but only calls `UseNpgsql` when the string is present (`TrafficControlNativeInjectorBootStrapper.cs:42-47`), so the context (and the whole context) runs without a database.

---

## 9. Composition / wiring

`TrafficControlNativeInjectorBootStrapper.RegisterServices` (`Infra.CrossCutting.IoC/TrafficControlNativeInjectorBootStrapper.cs:37`) is the composition root, with both a `WebApplicationBuilder` overload (wires the audit DbContext) and a bare `IServiceCollection` overload for non-web hosts/tests. `RegisterCore` (`:64-96`) registers:

- `IResourceTopology.Empty` (singleton) — the v0 identity-closure default; the **Host overrides** it with `MapResourceTopologyAdapter` (last registration wins, `host/.../Program.cs:66`).
- `IFleetClock → SystemFleetClock` (singleton).
- **`ReservationTable` as a process-wide singleton** (`:74`) — the single writer.
- Stateless domain services as singletons: `IResourceAllocator`, `IReservationCalendar`, `IConflictDetector`.
- App services: `ITrafficCoordinatorAppService` and `ITrafficControlOperatorAppService` **scoped** (so per-request event publishing works), `ITrafficControlSnapshotProvider` singleton.
- **The key override:** `IReservationQuery → ReservationService` (`:88`). PathPlanning declares `IReservationQuery` and ships a `NullReservationQuery` (always-free stub). Because the Host calls PathPlanning's bootstrapper **first** and Traffic Control's **after**, this later registration wins and the planner reads the live reservation table (`ReservationService.cs:7-13`, `Program.cs:11-17`, host comment `Program.cs:53`). `ReservationService.GetView` hands back a point-in-time `CreateSnapshotView()` so SIPP-style search reads real safe intervals without ever mutating the table.
- `ReplanTriggerSubscriber` and the two Hangfire jobs (singletons; scheduling wired in the Host).

The Host also routes Coordination's detour reservations back through this context's write seam via `TrafficDetourReservationAdapter → ITrafficCoordinatorAppService.TryReserveAsync` (`host/.../Adapters/TrafficDetourReservationAdapter.cs`).

---

## 10. Tests

`SwarmRoute.TrafficControl.Tests` (xUnit) covers the context end to end via in-memory aggregates (no DB), with shared builders in `TestHelpers.cs` (`Cp/Lane/Block`, `CpPath`, `ClosureTopology`).

| File | What it pins |
|---|---|
| `ReservationTableTests.cs` | The invariant (conflicting grant → Queued, only one lease); disjoint/touching windows allowed; whole-path all-or-nothing; reversed-lane queued against the incumbent lane; **release-no-leak regression** (closure freed); `ReleaseAll` clears leases+waits; `FreeIntervals`/`IsFree`/`IsFreeForExcept` incl. reversed lane; `StateVersion` bump; `Refresh` eviction + contention pruning; idempotent `RecordContention`; blacklist → Blocked; same-agent merge + exact-duplicate idempotency. |
| `ConflictDetectorTests.cs` | All four classifications (VertexSame, Following, EdgeSwap, Interference-via-closure); no conflict when time-separated; no self-conflict with own leases. |
| `ResourceAllocatorTests.cs` | `BlockedResources` maps block contention back to the candidate CP / Lane (not the block). |
| `TrafficCoordinatorAppServiceTests.cs` | Grant → crossing queued → release-behind unblocks; release frees full closure through the seam; `ManualUnlock` drains+publishes `ReservationReleasedEvent`. |
| `SnapshotProviderTests.cs` | Owns/Waits mapping; empty table; released lease drops from Owns; resource-kind preserved; reversed-lane Waits points at the blocking owned lane. |
| `ReservationServiceTests.cs` | `ReservationService` is an `IReservationQuery`; the view is a stable snapshot (old view unchanged after a later grant); reversed-lane + closure semantics match the writer. |
| `CalendarAndJobsTests.cs` | `EarliestFreeStart` finds the first fitting window; `LeaseExpirySweepJob` evicts expired; `StaleRequestEscalationJob` ages waits + emits `AllocationContendedEvent`. |
| `RightOfWayTests.cs` | Priority → wait-time → ordinal-id ordering; totality/antisymmetry; deterministic `Winner` regardless of arg order. |
| `TrafficControlStateMachineTests.cs` | Happy-path `Requested→Free`; invalid transition fails without state change; `Grant` blocked by `ResourceAvailable`/`NotBlacklisted` guards; succeeds under `NoConflict`. |

---

## 11. v0 status & v1 roadmap

**v0 (today).** Faithful port of the engine's whole-path spatial lock, but on a genuine interval model:

- `TryGrant` reserves the **whole path timeline at once** (≈ the original whole-path lock); the allocator strategy is "all-or-nothing whole-path".
- Only `Granted` / `Queued` / `Blocked` are produced; `Preempted` is reserved.
- One global `ReservationTable` (one fleet, one clock); `roadmapId` is accepted for contract shape but the same table is snapshotted regardless (`ReservationService.cs:14-18`).
- Closure defaults to identity unless the Host wires a Map-backed topology.

**Why it is already SIPP-ready.** Nothing about the model is a binary lock: leases are `(resource × interval)`, the calendar already computes `FreeIntervals` / `EarliestFreeStart`, and the read seam (`IReservationView`) hands the planner exactly the *safe intervals per resource* SIPP consumes. Swapping in safe-interval allocation at v1 is a **strategy change behind `IResourceAllocator`/`IReservationCalendar`, not a model change** to the aggregate (stated at `ReservationTable.cs:24-27`, `IReservationCalendar.cs:7-11`).

**v1 roadmap.**
- **Safe-interval (SIPP) allocation** — replace whole-path locking with per-cell safe-interval reservation behind the same `IResourceAllocator` interface; the planner already reads `FreeIntervals`/`EarliestFreeStart`.
- **Priority planning / preemptive right-of-way** — produce `AllocationOutcome.Preempted` using the `RightOfWay` rule already in place.
- **Per-lease lifecycle** — drive `Reserved → InTransit → Releasing` via `TrafficControlStateMachine` as execution telemetry arrives (today release is coarse-grained `ReleaseBehind`).
- **Persistence beyond audit** — periodic full snapshots (`LeasesJson`) for crash recovery; the schema already supports it.
