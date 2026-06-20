# Refactor: `Deadlock` context → `Liveness` (detect + resolve in one synchronous policy)

## Context

The `Deadlock` bounded context is internally clean but **solves the wrong problem**. It only classifies RAG *resource-cycles* — agents **waiting** in the reservation table for a resource another holds. But the dense-field failures we actually hit (e.g. seed 99940/8888, the 24-AGV runs) are **physical standoffs**: agents that each **hold** interval-exclusive reservations yet physically block one another (head-on swap, blocking chain, parked-sealed goal). The RAG detector is structurally blind to these — `FleetLoopDriver` literally logs *"Not a RAG cycle … deadlock detection won't fire"* (`redirects=0` on every such run).

Consequently **all real resolution leaked into `FleetLoopDriver`** as six ad-hoc tiers (head-on yield, stall-reroute, step-aside, PIBT, CBS-trigger, anti-livelock guard), while the whole Deadlock context — detection, `DeadlockCase`/`AvoidancePlan` aggregates, recovery/escalation services, registry, integration events, and the Coordination `FleetRedirectStore` choreography — is consumed **only by the synchronous simulation executor**. Verified: the autonomous `FleetCoordinationLoop` never touches deadlock recovery/redirects, so the entire async/CAP deadlock path is vestigial.

**Goal:** reframe/rename `Deadlock` → **`Liveness`**: one context that owns the *full* taxonomy of liveness failures (resource-cycle, swap, chain, parked-seal, livelock, starvation) via a **pure synchronous policy** (`detect → decide → emit directives`), with `FleetLoopDriver` reduced to thin mechanism (advance agents, apply directives). Not in production; no back-compat; rename freely; **zero tech debt**.

**Decisions taken (user-confirmed):**
1. **Full synchronous collapse.** Delete the async event choreography + `DeadlockCase`/`AvoidancePlan` aggregates + recovery/registry/escalation services + the cross-context integration events + the Coordination `FleetRedirectStore`. Recovery becomes "re-evaluate on fresh state each tick → threat gone ⇒ restore goal."
2. **Keep the manual OFF/PIBT/CBS toggle** (the v3 A/B lever). PIBT moves into the Liveness context; CBS stays in PathPlanning (it's a planner). The policy invokes both as mechanism, selected by `LivenessOptions.JointResolver`.

---

## Target architecture

### The context
`Liveness/` (rename of `Deadlock/`), projects: `SwarmRoute.Liveness.Domain.Shared`, `.Domain`, `.Application.Contract`, `.Application`, `.Infra.CrossCutting.IoC`, `.Tests`.

Single responsibility: **observe the fleet → classify every stall → decide the cheapest safe resolution (escalating) → emit directives.** It never moves, reserves, or plans — those are delegated mechanisms.

### The seam (the heart of the design)
```
// Liveness.Application.Contract — pure, frozen-kernel + Map.Domain vocabulary only
public interface ILivenessPolicy
{
    // Called once per tick, synchronously, AFTER plan+reserve and BEFORE advance.
    LivenessDirectives Evaluate(in LivenessSnapshot snapshot);
}
```
- **`LivenessSnapshot`** (built by the executor each tick): per-agent physical pose + `IntendedNextCell` + en-route/parked flags + the `parkedCells` set + the roadmap graph. This is exactly the data `BuildStuckSnapshots()` already computes today, promoted to a first-class read-only input. (No RAG snapshot needed — physical stall detection *subsumes* the resource-cycle symptom: a resource cycle manifests as a stuck cluster, which the cluster detector already catches.)
- **`LivenessDirectives`**: a flat, pooled list of typed commands the executor applies. Complete vocabulary (each maps 1:1 to an existing mutation block in today's driver): `YieldAndReplan{agent}`, `RelocateParked{blocker, dest, window}`, `EnterJointResolver{agents}`, `MoveTo{agent, cell}`, `ExitJointResolver{agent}`, `SolveClusterJointly{agents}`, `EscalateLivelock{agent}`, `Diagnostic{text}`. The two old "victim"/"avoid-site" concepts collapse into one model.
- **Policy state**: `LivenessPolicy` is pure per call but holds **per-run working memory** keyed by agent — stall streaks, redirect attempts + `BestDistanceToGoal` (the anti-livelock guard), PIBT episode budget, and the run-lifetime hops-cache. This replaces the scattered `RunAgent` liveness fields and the deleted `IActiveResolutionRegistry`.

### Strategy ladder (inside `LivenessPolicy.Evaluate`)
Cheapest → escalate, deterministic (`Priority → ordinal id`, ADR-003): head-on swap → lower-priority `YieldAndReplan`; stuck cluster → `EnterJointResolver`/`SolveClusterJointly` per `LivenessOptions.JointResolver`; parked-sealed goal (if `StepAside`) → `RelocateParked` (Push-and-Swap-style); no net progress / attempt cap → `EscalateLivelock`. The escalation terminal simply stops redirecting; the run still reports `DidNotConverge` honestly.

### What stays / moves / dies
- **Kept & deduped:** `RagCycleDetector` (merge of `RagDeadlockDetector` + host `RagWouldCloseCycleDetector` into one class in `Liveness.Domain.Detection`) — retained **only** for opt-in grant-time prevention (`IWouldCloseCycleDetector` consumed by TrafficControl `ReservationTable.TryGrant`). `ResourceAllocationGraph` + vendored `CyclesDetector` stay under it.
- **Moved (reused verbatim — pure functions):** `PibtZoneResolver`, `PibtAgentView`, `HopDistances`, `StuckClusterDetector` (→ `StandoffClusterDetector`) from `Simulation/.../Pibt/` → `Liveness.Domain`. `CbsLocalSolver` **stays** in `PathPlanning.Domain/Cbs/`, invoked via the existing `IFleetCoordinationCycle.PlanClusterAsync`.
- **Extracted from `FleetLoopDriver` → `Liveness.Domain.Resolution` (pure):** `ParkedRelocationSelector` (from `FindParkedRelocation`/`NearestFreeCellOffPath`/`ReachableAvoiding`) and `AntiLivelockGuard` (from the `RedirectAttempts`/`BestDistanceToGoal`/escalate block). Thresholds (the `const` locals) → `LivenessOptions`.
- **Deleted (subsumed by the physical policy + grant-time prevention):** `DeadlockCase`, `AvoidancePlan`, `DeadlockAppService`, `DeadlockRecoveryService`, `DeadlockEscalationService`, `IActiveResolutionRegistry` + impl, `AllocationContendedSubscriber`, the four seams (`IAvoidancePointSelector`/`IDetourReservationService`/`IClearanceConfirmer`/`IDeadlockSnapshotProvider`) + Null impls + host adapters, `AntiLivelockAvoidancePointSelector`, the `DeadlockEventContracts` (`IDeadlockResolutionRequested/Resolved/Escalated`), the Coordination `Deadlock/` folder (`FleetRedirectStore`, `IFleetRedirect*`, `RedirectIntent`, `DeadlockResolutionRequestedConsumer`), and `ISimulationEngine.{Redirects,RecoverTick,EscalateLivelock}`.

### `FleetLoopDriver` after (≈1450 → ≈400 lines)
Keeps pure mechanism only: `RunAgent` physical state; the right-of-way gate / `ResolveScheduleFaithfulAdvances` / continuous loop; occupancy bookkeeping (`occupantNow`/`claimedNext`/`parkedCells`); safety nets; frame recording + stats; the small apply-methods (`YieldAndReplanFromCurrentAsync`, `ApplyRelocate`, `ApplyMoveTo`, `SetEnRouteFromPath`) and the reservation I/O (`RunCycleAsync`/`ReleaseAsync`/`PlanClusterAsync`). Its signature collapses `redirects`/`recoverTick`/`escalateLivelock`/`stepAside`/`usePibt`/`useCbs` → a single `ILivenessPolicy policy`.

Per-tick flow: `clock.SetTick` → `cycle.RunCycleAsync(pending, blocked=parkedCells)` → `BuildLivenessSnapshot()` → `policy.Evaluate()` → `ApplyDirectives()` (the lone async hop is CBS `PlanClusterAsync` under `SolveClusterJointly`) → advance → safety → record.

### API + frontend ripple
- `SimulationRequest`: drop `StepAside`/`PreventDeadlockCycles`/`UsePibt`/`UseCbs`; add `JointResolver` (`None|Pibt|Cbs` enum), `PreventCycles` (bool), `StepAside` (bool). `SimulationService` maps these → `LivenessOptions` and selects `FleetExecutionMode` as today.
- Frontend (`host/swarmroute-web`): the existing 对峙求解器 OFF/PIBT/CBS `Segmented` (`ControlRail.tsx`) rewires from `usePibt`/`useCbs` → one `jointResolver` field in `types/index.ts` + `simStore.ts` (`DEFAULT_PARAMS`); `stepAside` stays. Preserves the A/B toggle.

---

## Critical files

- **Split/thin:** `Simulation/SwarmRoute.Simulation.Application/FleetLoopDriver.cs` (source of every standoff block being moved), `ISimulationEngineFactory.cs`.
- **Merge:** `Deadlock/SwarmRoute.Deadlock.Domain/Services/RagDeadlockDetector.cs` + `host/SwarmRoute.Host/Adapters/RagWouldCloseCycleDetector.cs` → one `Liveness.Domain.Detection.RagCycleDetector`.
- **Move:** `Simulation/SwarmRoute.Simulation.Application/Pibt/*` → `Liveness.Domain` (Detection/Resolution).
- **New:** `Liveness.Application.Contract/{ILivenessPolicy,LivenessSnapshot,LivenessDirectives,LivenessOptions}.cs`, `Liveness.Application/LivenessPolicy.cs`, `Liveness.Domain.Resolution/{ParkedRelocationSelector,AntiLivelockGuard}.cs`.
- **Delete:** Coordination `SwarmRoute.Coordination.Application/Deadlock/*`; the Deadlock aggregates/services/seams/events listed above; host deadlock seam adapters; `Program.cs`/`InMemorySimulationEngineFactory.cs` deadlock wiring (keep only the prevention detector + `LivenessPolicy` registration).
- **API/UI:** `Simulation/.../SimulationRequest.cs`, `SimulationService.cs`; `host/swarmroute-web/src/{types/index.ts,store/simStore.ts,components/ControlRail.tsx}`.

Reuse (do not reinvent): `RoadmapGraph.ShortestPath`/`DistanceTo` (`Map/.../ValueObjects/RoadmapGraph.cs`); `PibtZoneResolver.Resolve`, `StuckClusterDetector.Assemble`, `HopDistances`; `CbsLocalSolver` via `IFleetCoordinationCycle.PlanClusterAsync`; `ResourceAllocationGraph.FromSnapshot` + vendored `CyclesDetector.CyclicVertices`.

---

## Phased sequence (each phase builds + all tests green before the next)

- **P0 — Rename** `Deadlock`→`Liveness` (5 projects, namespaces, `.sln`, all `ProjectReference`s, `SwarmRoute.Deadlock.Tests`→`.Liveness.Tests`). Pure rename, behavior identical.
- **P1 — Merge RAG detectors** into `Liveness.Domain.Detection.RagCycleDetector` (implements `IWouldCloseCycleDetector`; keep a `Detect` for unit tests). Delete host `RagWouldCloseCycleDetector`; repoint TrafficControl prevention registration. Merge `DeadlockDetectorTests` + `WouldCloseCycleDetectorTests` → `RagCycleDetectorTests`.
- **P2 — Move PIBT + cluster detection** to `Liveness.Domain`; add `Map.Domain` ref to `Liveness.Domain` and a temp `Liveness.Domain` ref from `Simulation.Application` (driver still calls them). Move the two unit-test files.
- **P3 — Introduce the policy** (`Contract` types + `LivenessPolicy`) and extract `ParkedRelocationSelector` + `AntiLivelockGuard`. Reproduce today's decisions exactly. Add pure `LivenessPolicyTests` (swap, parked-seal, cluster, anti-livelock). Not yet wired — behavior unchanged.
- **P4 — Cutover (the one behavioral change):** driver builds snapshot → `policy.Evaluate` → apply directives; collapse driver params to `ILivenessPolicy`; `SimulationRequest` 4 bools → `JointResolver`+`PreventCycles`+`StepAside`; `SimulationService` → `LivenessOptions`. The closed-loop sim suite is the oracle — identical tick counts.
- **P5 — Delete the dead async machinery** (aggregates, recovery/registry/escalation, seams, events, Coordination `Deadlock/`, engine redirect delegates, host adapters) and the distributed deadlock integration tests that exercised them.
- **P6 — Frontend ripple** (`jointResolver` field; rewire OFF/PIBT/CBS `Segmented`; drop `usePibt`/`useCbs`). `npm run typecheck` + `lint` green.
- **P7 — Efficiency pass:** per-tick buffer pooling + pooled `LivenessDirectives`; move hops-cache into the policy as run-lifetime memo; add a golden-master determinism test.

---

## Verification

- **Per phase:** `dotnet build` clean + full suite green (currently 216: Map 33, PathPlanning 34, TrafficControl 54, Deadlock→Liveness 60, Integration 35). P0–P3 are behavior-preserving by construction.
- **P4 oracle (behavioral equivalence):** the closed-loop integration tests that drive `SimulationService`/`FleetLoopDriver` end-to-end (Sipp/Pibt/Cbs closed-loop, sealed-goal relocation, walled-lone-survivor, swap-standoff, physical-occupancy, horizon) must pass with **identical** Completed/Arrived/tick counts before vs after.
- **New pure tests:** `LivenessPolicyTests` proves head-on swap → one `YieldAndReplan`/`EnterJointResolver` per `JointResolver`; parked-seal → one off-corridor `RelocateParked`; no-progress → `EscalateLivelock` — all without an engine or bus (milliseconds, deterministic).
- **End-to-end (live app):** rebuild backend; via the proxy confirm seed 99940 SIPP → `Completed 12/12`; the 对峙求解器 toggle OFF/PIBT/CBS still changes outcomes; `remainingSiteIds` forward-route still renders. Re-run the seed batch (Dijkstra vs SIPP vs SIPP+PIBT/CBS) to confirm no convergence regression.
- **Determinism:** golden-master test runs a fixed dense scenario twice → byte-identical `Frames` (locks ADR-003 through the new seam).
