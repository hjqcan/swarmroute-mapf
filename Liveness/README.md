# Liveness (活性)

*A bounded context that keeps the fleet making progress. It does this two ways, both decision-only: it
**prevents** circular waits at reservation grant time (a RAG cycle check TrafficControl consults), and it owns the
synchronous, phase-based **`ILivenessPolicy`** that resolves the **physical** standoffs the reservation table
cannot see (head-on swaps, blocking chains, parked-sealed goals). It never holds a reservation, never plans a
path, and never mutates engine state — it only decides.*

> English · 简体中文版本: [README.zh-CN.md](README.zh-CN.md)

---

## 1. Purpose & responsibility

A reservation table grants interval-exclusive cell/lane leases, so it makes time-disjoint plans collision-free.
But two failure modes remain that a reservation table alone cannot fix:

1. **Circular wait at grant time.** Agent A holds r1 and requests r2; B holds r2 and requests r1. Each lease is
   individually legal, but the *wait-for* graph has closed a cycle and neither will ever advance.
2. **Physical standoff at execution time.** Two agents hold time-disjoint reservations yet are physically nose to
   nose in a corridor (a head-on swap), or form a blocking chain / rotation, or a *finished* vehicle parks on the
   only approach to another agent's goal. The reservation table sees no conflict — the agents simply never move.

Liveness owns the policy for both, and **only** the policy:

- **Prevention (constructive).** `RagCycleDetector` implements TrafficControl's `IWouldCloseCycleDetector`: before
  a lease is granted, it asks "would this new wait edge close a wait-for cycle?" and, if so, the grant is refused so
  the planner re-routes — the circular wait never forms. The same class also implements `IDeadlockDetector.Detect`
  for post-hoc analysis (partition the live RAG into genuine cycles).
- **Physical-standoff resolution (reactive, decision-only).** `ILivenessPolicy.Evaluate` is consulted by the
  executor once per phase per tick. It observes the fleet's physical state and returns the cheapest safe resolution
  as a list of `LivenessDirective`s. The executor performs the mechanism (drop a lease, move a pose, call the
  cluster planner); the policy never does.

What it explicitly does **not** do:

- It does **not** hold or grant reservations — that is TrafficControl's job. Prevention is a *veto* on a grant;
  resolution emits *directives* the executor enacts through its normal reserve/plan paths.
- It does **not** plan paths — PathPlanning owns that. A `YieldAndReplan` / `SolveClusterJointly` directive asks the
  *executor* to re-plan or call the cluster planner.
- It is **pure and synchronous**: `Evaluate` is a deterministic function of the `LivenessSnapshot` plus the policy's
  own per-run working memory (the en-route stall streaks and a hop-distance cache). No I/O, no engine mutation.

### History (what this context used to be)

This context began as a port of the `AJR.MAPF` *reactive* deadlock subsystem — an event-driven flow
(`AllocationContended` → scan → `DeadlockCase`/`AvoidancePlan` aggregates → resolve/recover/escalate over the
integration bus, with `IAvoidancePointSelector` / `IDetourReservationService` / `IClearanceConfirmer` seams). **That
whole reactive flow has been removed.** Prevention front-runs it (a cycle that never forms needs no recovery), and
physical standoffs are now resolved synchronously inside the executor loop through `ILivenessPolicy`. The RAG +
`CyclesDetector` machinery survives because both surviving roles (prevention and detection) are exactly cycle
detection. If you are looking for `DeadlockAppService`, `AvoidanceDeadlockResolver`, the avoid-point/detour/clearance
seams, or the `Deadlock.Case.*` integration events — they are gone.

---

## 2. Layers & projects

Clean-architecture layering (dependencies point inward). The context is buildable **standalone** — no compile-time
edge to TrafficControl (it declares the prevention interface it implements via TrafficControl's *Domain*, the only
shared edge being the frozen Kernel).

| Project | Role | Key types |
| --- | --- | --- |
| `SwarmRoute.Liveness.Domain.Shared` | Error codes. | `DeadlockErrorCodes` |
| `SwarmRoute.Liveness.Domain` | The cycle-detection primitive + the pure resolution algorithms. | `Detection/RagCycleDetector`, `Detection/StuckClusterDetector`, `Resolution/PibtZoneResolver`, `Resolution/ParkedRelocationSelector`, `Resolution/HopDistances`, `Resolution/PibtAgentView`, `ValueObjects/ResourceAllocationGraph`, `ValueObjects/DeadlockCycle`, `Services/IDeadlockDetector` |
| `SwarmRoute.Liveness.Application.Contract` | The policy **seam**: interface, snapshot, directives, options. | `Policy/ILivenessPolicy`, `Policy/LivenessSnapshot` (+ `LivenessPhase`, `AgentLivenessView`), `Policy/LivenessDirective` (9 variants), `Policy/LivenessOptions` (+ `JointResolverKind`), `Policy/NoOpLivenessPolicy` |
| `SwarmRoute.Liveness.Application` | The concrete phase-based policy. | `Policy/LivenessPolicy` |
| `SwarmRoute.Liveness.Infra.CrossCutting.IoC` | DI registration. | `DeadlockNativeInjectorBootStrapper` |
| `SwarmRoute.Liveness.Tests` | xUnit unit tests. | — |

Key external types: the graph machinery (`DirectedSparseGraph<T>`, `CyclesDetector`) is vendored at
`src/vendor/SwarmRoute.Algorithms*`. The cross-context vocabulary (`ResourceRef`, `ResourceKind`,
`ResourceAllocationGraphSnapshot`) is the **frozen** `SwarmRoute.SpatioTemporal.Kernel`. The roadmap graph the
resolution algorithms walk is PathPlanning's `RoadmapGraph`.

---

## 3. Grant-time cycle prevention — `RagCycleDetector`

`RagCycleDetector` (`SwarmRoute.Liveness.Domain/Detection/RagCycleDetector.cs:23`) implements **both** surviving
liveness roles on byte-identical cycle semantics:

```csharp
public sealed class RagCycleDetector : IDeadlockDetector, IWouldCloseCycleDetector
```

### Prevention — `IWouldCloseCycleDetector.WouldCloseCycle`

`IWouldCloseCycleDetector` is declared by TrafficControl
(`TrafficControl/SwarmRoute.TrafficControl.Domain/Services/IWouldCloseCycleDetector.cs`); `ReservationTable` is
constructed with one and calls it inside `TryGrant` **before** recording a contended wait edge:

```csharp
bool WouldCloseCycle(
    ResourceAllocationGraphSnapshot currentEdges,
    string candidateAgentId,
    IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges);
```

The detector builds the *hypothetical* RAG = the current owns/waits edges **plus** the candidate's would-be wait
edges, runs `CyclesDetector.CyclicVertices(graph, "agent_")`, and returns whether the candidate now lies on a cycle.
True ⇒ the grant is refused and the planner re-routes, so the circular wait never forms. This is opt-in per run
(`SimulationRequest.PreventDeadlockCycles`); off = the Null detector = byte-identical baseline.

### The RAG (`ResourceAllocationGraph` value object)

`ResourceAllocationGraph` (`.../ValueObjects/ResourceAllocationGraph.cs`) adapts the frozen
`ResourceAllocationGraphSnapshot` into a directed graph cycle detection runs over. Three vertex families
(`agent_`, `occupySite_`, `applySite_`), two edge families:

- **Ownership** (held): `occupySite_<resource> → agent_<owner>`.
- **Wait-for** (request): `agent_<waiter> → occupySite_<resource>`.

Both pivot on a *single shared* `occupySite_` vertex per resource, so an `agent → resource → agent → resource → …`
path can close into a cycle. `ResourceKey` namespaces a resource as `Kind:Id`, so a CP and a Lane with the same id
are distinct vertices.

#### ASCII: a 2-agent circular wait

`A` owns `r1` and wants `r2`; `B` owns `r2` and wants `r1`:

```
Edges built:
  occupySite_CP:r1 → agent_A        (ownership)
  occupySite_CP:r2 → agent_B        (ownership)
  agent_A          → occupySite_CP:r2   (wait-for)
  agent_B          → occupySite_CP:r1   (wait-for)

Cycle through agent vertices:
  agent_A → occupySite_CP:r2 → agent_B → occupySite_CP:r1 → agent_A
```

`CyclesDetector.CyclicVertices(graph, "agent_")` flags `[agent_A, agent_B]`.

### Detection — `IDeadlockDetector.Detect`

For post-hoc analysis, `Detect(snapshot)` runs the same cycle detection, then refines the result: the vendored
detector *over-approximates* (a waiter merely queued behind a deadlock is flagged), so the detector builds the
**agent-blocking digraph** restricted to cyclic agents and returns its **non-trivial strongly-connected components**
(an SCC of size ≥ 2, or a singleton with a self blocking-edge) via an iterative **Tarjan** implementation. Cycles are
returned ordered by smallest member id, so detection is deterministic. `DeadlockCycle` (`.../ValueObjects/`) is the
result value object — the agent ids in one cycle, deduplicated and sorted ordinal-ascending.

---

## 4. The synchronous liveness policy

### The seam — `ILivenessPolicy`

`ILivenessPolicy` (`.../Application.Contract/Policy/ILivenessPolicy.cs`) is the single owner of physical-standoff
policy. One method:

```csharp
IReadOnlyList<LivenessDirective> Evaluate(LivenessSnapshot snapshot);
```

Pure and synchronous (§1). The roadmap graph and `LivenessOptions` are bound for the run at construction, so they
are not snapshot fields. `NoOpLivenessPolicy.Instance` is the always-empty default (used when the executor is given
no policy).

### What the policy sees — `LivenessSnapshot`

```csharp
public sealed record LivenessSnapshot(
    long Tick,                                 // diagnostics only
    LivenessPhase Phase,                       // which mechanism point this consult is
    bool ScheduleFaithful,                     // true under the schedule-faithful (SIPP) executor
    IReadOnlyList<AgentLivenessView> Agents,   // every agent's physical view this tick
    IReadOnlySet<string> ParkedCells);         // cells a finished vehicle is parked on
```

`AgentLivenessView` is the read-only per-agent view (`Position`, `Goal`, `EffectiveGoal`, `Priority`,
`EnRouteNextCell`, the streaks `BlockedTicks` / `StuckTicks` / `PibtHeldTicks`, the joint-resolver flags, and the
`Advance`-phase-only fields `AtRouteEnd` / `NextCellIsParked` / `ScheduledToAdvance` / `ScheduledToMoveThisTick`).
The executor builds it from its mutable `RunAgent`; the policy never sees or mutates engine state directly.

### What the policy returns — `LivenessDirective`

Every directive maps 1:1 to an existing executor mutation (`.../Policy/LivenessDirective.cs`):

| Directive | The executor does |
| --- | --- |
| `YieldAndReplan(AgentId, Reason)` | drop the agent's lease and re-plan from its current pose. `Reason` is `head-on-yield` or `stall-reroute`. |
| `EnterJointResolver(AgentIds)` | release the agents' stalled leases and begin a PIBT episode. |
| `MoveTo(AgentId, Cell)` | move one PIBT agent one hop to `Cell` this tick (hold when `Cell` == current). |
| `ExitJointResolver(AgentId, Reason)` | end the agent's PIBT episode (goal reached / held too long / budget elapsed) → it re-plans normally. |
| `SolveClusterJointly(AgentIds)` | call the cluster (CBS) planner over the cluster and reserve the conflict-free result atomically. |
| `RelocateParked(BlockerId, Dest, YieldWindow, WalledAgentId)` | step a parked blocker aside to `Dest` for `YieldWindow` ticks so the walled-out agent's goal approach opens. |
| `RestoreGoal(AgentId)` | a relocated gatekeeper's yield window elapsed → let it re-plan back to its own goal. |
| `EscalateLivelock(AgentId, Reason)` | anti-livelock terminal: stop trying to relocate/resolve this agent. |
| `Diagnostic(Message)` | forward a human-facing standoff message to the log sink. |

### Why phases — `LivenessPhase`

Physical-standoff decisions are inherently *staged*: a parked blocker must be relocated **before** the planner
re-routes the walled-out agent; a congestion cluster is formed **after** plan+reserve (so it sees the freshly-planned
poses) but **before** the schedule resolves who advances; the joint-resolver drive and the per-agent yields are
decided **after** the schedule is resolved. So the executor consults the policy **once per phase per tick**, each at
the exact mechanism point its inputs become available — which makes the extraction from the executor's old inlined
logic behaviour-preserving by construction.

| Phase | When | The policy decides |
| --- | --- | --- |
| `BeforePlanning` | before plan+reserve | recover gatekeepers whose yield window elapsed (`RestoreGoal`), then relocate parked blockers off a walled-out approach (`RelocateParked`, when `StepAside` is on). |
| `ClusterFormation` | after plan+reserve, before advances resolve | form physical-standoff clusters and hand them to the joint resolver — `EnterJointResolver` (PIBT) or `SolveClusterJointly` (CBS). No-op when `JointResolver == None`. |
| `JointDrive` | after advances resolve, before the gate | drive each PIBT agent one hop (`MoveTo`) and decide which agents exit the episode (`ExitJointResolver`). |
| `Advance` | after the joint drive, before the gate | the schedule-faithful per-agent `stall-reroute` / `head-on-yield` (`YieldAndReplan` + a head-on `Diagnostic`). No-op unless `ScheduleFaithful`. |

`LivenessPolicy` (`.../Application/Policy/LivenessPolicy.cs`) dispatches `Evaluate` on `snapshot.Phase` to one
`EvaluateBeforePlanning` / `EvaluateClusterFormation` / `EvaluateJointDrive` / `EvaluateAdvance` method, holding a
memoized reverse-BFS hop-distance cache (`HopDistances.To`, one per goal) as the greedy next-hop heuristic.

---

## 5. Joint resolvers (PIBT / CBS) and cluster detection

The joint resolver is the cluster owner for a physical standoff — exactly one per cluster, selected by
`JointResolverKind` (`.../Policy/LivenessOptions.cs`):

```csharp
public enum JointResolverKind { None = 0, Pibt = 1, Cbs = 2 }
```

> This single enum is the user-facing lever; it was previously two mutually-exclusive `UsePibt` / `UseCbs` request
> bools. The simulation HTTP request now carries one `JointResolver` field, mapped onto `LivenessOptions.JointResolver`.

### Cluster detection — `StuckClusterDetector`

`StuckClusterDetector.Assemble` (`.../Detection/StuckClusterDetector.cs`) is a static, reservation-agnostic
detector. From each agent's INTENDED next cell + pose + a unified stuckness counter (`StuckAgentSnapshot`), it
union-finds candidates (active goal-seekers whose intended cell is physically occupied) to the occupant blocking
them, and returns the components of size ≥ 2 that contain a member at/over the trigger threshold — ordered by
smallest id (deterministic). Keying off *intent + pose* (not the en-route reservation flag) is what lets a head-on
swap whose member has dropped to a pending/walled-out state still be seen as a cluster.

### PIBT — `PibtZoneResolver`

For `JointResolverKind.Pibt`, `ClusterFormation` emits an `EnterJointResolver` per cluster member, and `JointDrive`
calls `PibtZoneResolver.Resolve` (`.../Resolution/PibtZoneResolver.cs`):

```csharp
public static IReadOnlyDictionary<string, string> Resolve(
    IReadOnlyList<PibtAgentView> cluster,
    IReadOnlySet<string> blockedCells,
    RoadmapGraph graph,
    Func<string, IReadOnlyDictionary<string, int>> hopsToGoal);
```

It plans one joint hop for the cluster by **Priority Inheritance with Backtracking**: process agents most-waited
first (anti-livelock), then static priority, then ordinal id; each agent tries its out-neighbours + stay ordered by
hop-distance to goal, tentatively claims a target, and recursively pushes a lower-priority occupant out of the way,
backtracking on failure. Guarantees: vertex-distinct next cells, no immediate 2-cycle swaps, and the
highest-priority agent gets its best reachable cell. Cells held by non-cluster agents are passed as `blockedCells`
(immovable). The episode ends per agent (`ExitJointResolver`) when it reaches goal, is held too long
(`JointResolverHeldExitThreshold`), or its drive budget elapses — handing it back to prioritized SIPP.

### CBS — `SolveClusterJointly`

For `JointResolverKind.Cbs`, `ClusterFormation` emits one `SolveClusterJointly` per multi-member cluster. The
*executor* (not the policy) releases the members' leases and calls its cluster planner (a complete/optimal local
Conflict-Based Search reusing SIPP as the constrained low level, honoring the rolling-horizon window through it),
then reserves the conflict-free result atomically and resumes schedule-faithful execution. CBS cracks the dense
swaps/chains greedy PIBT cannot, at higher cost. CBS therefore **requires the SIPP planner** (it returns time-axis
paths the schedule-faithful executor must run) — `SimulationService.Validate` enforces this.

---

## 6. How the executor consumes the policy

The consumer is `FleetLoopDriver` in the Simulation context
(`Simulation/SwarmRoute.Simulation.Application/FleetLoopDriver.cs`). It takes an optional `ILivenessPolicy` (default
`NoOpLivenessPolicy.Instance`) and consults it exactly **four times per tick** — one `Evaluate` per `LivenessPhase`,
each at its mechanism point — and applies the returned directives by mutating its own `RunAgent` state:

```
per tick:
  ── BeforePlanning ──  RestoreGoal → clear redirect;  RelocateParked → park blocker aside, reset walled streak
  (plan + reserve)
  ── ClusterFormation ──  EnterJointResolver → release leases, begin PIBT;  SolveClusterJointly → release + cluster-plan + reserve
  (schedule resolves which en-route agents advance)
  ── JointDrive ──  MoveTo → step one PIBT hop;  ExitJointResolver → park / disband back to pending
  ── Advance ──  YieldAndReplan(stall-reroute|head-on-yield) → release + re-plan at the gate;  Diagnostic → log
  (right-of-way gate steps the granted agents)
```

By construction the executor is a mechanical invoker of directives: the policy owns *all* the standoff
decision/PIBT/cluster logic. The driver's project reference to Liveness is `Application.Contract` (the seam) +
`Application` (the concrete `LivenessPolicy`); it no longer references `Liveness.Domain` — the PIBT/cluster code is
the policy's concern.

`SimulationService` (`Simulation/.../SimulationService.cs`) builds the policy per run from the request:
`new LivenessPolicy(field.Graph, new LivenessOptions { JointResolver = request.JointResolver, StepAside = request.StepAside })`.

---

## 7. Composition / wiring

`DeadlockNativeInjectorBootStrapper.RegisterServices(...)`
(`.../Infra.CrossCutting.IoC/DeadlockNativeInjectorBootStrapper.cs`) registers only the surviving primitive:

```csharp
services.AddScoped<IDeadlockDetector, RagCycleDetector>();   // post-hoc detection role
```

The **prevention** role (`IWouldCloseCycleDetector`) is **not** wired here — it is opt-in per run, wired by the
simulation engine factory. `InMemorySimulationEngineFactory`
(`host/SwarmRoute.Host/Adapters/InMemorySimulationEngineFactory.cs`) pre-registers
`AddSingleton<IWouldCloseCycleDetector, RagCycleDetector>()` **before** the TrafficControl bootstrapper, *only when*
`PreventDeadlockCycles` is on, so TrafficControl's `TryAddSingleton` Null default defers to it — turning prevention
on for that isolated, per-request container. (The former host adapter `RagWouldCloseCycleDetector.cs` is deleted;
the Liveness-domain `RagCycleDetector` now serves the role directly.)

The `ILivenessPolicy` is **not** registered in DI at all: it is sim/executor-scoped, constructed per run by
`SimulationService` because it binds the run's roadmap graph + options. Production has no executor loop, so it has
no `ILivenessPolicy`.

---

## 8. Tests

**`SwarmRoute.Liveness.Tests`** (pure, in-memory):

| File | Covers |
| --- | --- |
| `DeadlockDetectorTests` | `RagCycleDetector.Detect`: 2/3/4-agent cycles, acyclic → none, self-cycle, two independent cycles reported separately, an extra non-cyclic waiter excluded, determinism across runs. |
| `ResourceAllocationGraphTests` | RAG build (agent/`occupySite`/`applySite` vertices + ownership/wait edges; key includes `Kind`; value equality by edge sets) and that the built graph is what `CyclesDetector` flags. |
| `LivenessPolicyTests` | the pure `LivenessPolicy` at each phase: head-on yield, cluster formation + PIBT entry, parked step-aside, gatekeeper recovery, schedule-faithful stall-reroute. Hand-built `LivenessSnapshot` in → asserted directives out. |

**`SwarmRoute.Simulation.Tests`** (liveness-related):

| File | Covers |
| --- | --- |
| `PibtZoneResolverTests` | pure `PibtZoneResolver`: head-on resolution, rotations, priority inheritance, deterministic ordering, gridlock floors, lane directionality, the blocked-cell gate. |
| `StuckClusterDetectorTests` | `StuckClusterDetector.Assemble`: standoff component isolation, free-agent / singleton exclusion, threshold triggering. |

**`SwarmRoute.Integration.Tests`** (end-to-end through the REAL engine, exercising the policy seam): `PibtClosedLoopTests`,
`CbsClosedLoopTests` (incl. the `CBS requires Planner=Sipp` validation), `SwapStandoffDetectionTests`,
`WalledLoneSurvivorTests`, `PibtHeldExitTests`, and `LivenessDeterminismTests` (a fixed dense PIBT scenario run
twice is byte-identical — reproducibility through the policy seam).

---

### Cross-context dependencies (summary)

- **Implements (for TrafficControl):** `IWouldCloseCycleDetector` — the grant-time veto, consulted inside
  `ReservationTable.TryGrant`.
- **Implements (for the executor):** `ILivenessPolicy` — the synchronous physical-standoff policy, consulted by
  `FleetLoopDriver` once per phase per tick.
- **Consumes:** the frozen Kernel `ResourceAllocationGraphSnapshot` (the RAG read), PathPlanning's `RoadmapGraph`
  (the surface the resolution algorithms walk).
- **Shared kernel:** `SwarmRoute.SpatioTemporal.Kernel` (`ResourceRef`, `ResourceKind`,
  `ResourceAllocationGraphSnapshot`).
