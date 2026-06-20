using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Inputs to one in-memory simulation run. A <paramref name="Width"/>×<paramref name="Height"/> grid field is
/// built, <paramref name="AgvCount"/> AGVs are each assigned a distinct start and a distinct goal, and the REAL
/// engine (PathPlanning + TrafficControl + Coordination) drives them to completion collision-free.
/// </summary>
/// <param name="Width">Grid width (columns), measured in control points. Must be ≥ 1.</param>
/// <param name="Height">Grid height (rows), measured in control points. Must be ≥ 1.</param>
/// <param name="AgvCount">Number of AGVs. <c>Width*Height</c> must be ≥ <c>2*AgvCount</c> so distinct starts/goals fit.</param>
/// <param name="Seed">
/// Optional seed for the start/goal assignment RNG. When omitted a fixed default is used so a given request is
/// reproducible.
/// </param>
/// <param name="Planner">
/// Which planner to run for this request: <see cref="PlannerKind.Dijkstra"/> (v0 baseline, paired with greedy
/// execution) or <see cref="PlannerKind.Sipp"/> (v1 reservation-aware planner, paired with schedule-faithful
/// execution). Defaults to <see cref="PlannerKind.Dijkstra"/> so the same seed can be A/B-compared by flipping
/// only this field. (Frontend toggle is deferred — this is the backend contract.)
/// </param>
/// <param name="Starts">
/// Optional explicit per-AGV start cells (one per AGV, in agent order), used to <b>continue</b> a lifelong run:
/// each AGV keeps its current pose and is given a NEW goal, instead of teleporting to a fresh random layout.
/// When omitted (or invalid — wrong count, an unknown cell, or duplicates) a fresh random start/goal layout is
/// used. Goals are always drawn from cells not occupied by a start, so each goal is distinct and ≠ its own start.
/// </param>
/// <param name="HorizonWindowMs">
/// The rolling-horizon (RHCR, v2) window in fleet-clock ticks: SIPP commits only the next <c>HorizonWindowMs</c>
/// ticks of each route and the agent re-plans the following window at the frontier (bounds reservation lifetime,
/// so no agent locks a long corridor — the high-density convergence lever). Defaults to <see cref="long.MaxValue"/>
/// = unbounded (whole-path planning, byte-identical to v1). A/B by holding <see cref="Planner"/>=<see cref="PlannerKind.Sipp"/>
/// and flipping only this field. Ignored under <see cref="PlannerKind.Dijkstra"/> (space-only planner).
/// </param>
/// <param name="StepAside">
/// Opt-in executor recovery for physical goal-blocking in schedule-faithful (SIPP) runs (default off): a vehicle
/// parked on the only approach to a walled-out agent's goal is routed aside for a window, then re-parks.
/// Head-on edge-swap safety is independent and always on: the executor refuses the swap and the blocked
/// lower-priority agent releases its old plan before re-planning from its current CP, rather than being moved
/// outside TrafficControl.
/// </param>
/// <param name="PreventDeadlockCycles">
/// Opt-in grant-time deadlock prevention (v2 WouldCloseCycle; default off). When on, a reservation that would
/// close a wait-for cycle is refused at <c>TryReserve</c> (the planner re-routes), so the circular wait never
/// forms — constructive liveness that front-runs the reactive detect/redirect path. Off = the Null detector =
/// byte-identical v1, so the same seed A/B-compares prevention by flipping only this field. Independent of
/// <see cref="StepAside"/> (executor recovery) and <see cref="HorizonWindowMs"/> (rolling horizon).
/// </param>
/// <param name="JointResolver">
/// Opt-in zone-local joint resolver for physical standoffs in schedule-faithful (SIPP) runs (v3; default
/// <see cref="JointResolverKind.None"/>). A "physical standoff" is a cluster of agents that each hold
/// interval-exclusive reservations yet physically block one another (head-on swaps / circular chains the
/// reservation table cannot see). Exactly one resolver owns a cluster:
/// <list type="bullet">
/// <item><see cref="JointResolverKind.None"/> — no joint resolver; clusters are broken only by the cheap per-agent
/// ladder (head-on yield / stall-reroute). Byte-identical to v2 (no cluster is ever entered or solved).</item>
/// <item><see cref="JointResolverKind.Pibt"/> — a detected cluster is driven jointly one hop at a time (Priority
/// Inheritance with Backtracking) until the jam dissolves, after which each agent re-plans back to
/// prioritized-SIPP. A fast greedy resolver that engages a few ticks before the stall-reroute band-aid and
/// supersedes it inside its cluster.</item>
/// <item><see cref="JointResolverKind.Cbs"/> — a detected cluster is solved JOINTLY by a complete/optimal local
/// CBS (Conflict-Based Search) over its agents (reusing SIPP as the constrained low level); the conflict-free
/// paths are reserved atomically and the cluster resumes normal schedule-faithful execution. CBS cracks the dense
/// standoffs greedy priority-inheritance cannot (at higher cost). <b>Requires <see cref="PlannerKind.Sipp"/></b>
/// because CBS returns time-axis SIPP paths the schedule-faithful executor must run.</item>
/// </list>
/// Independent of <see cref="StepAside"/> and <see cref="HorizonWindowMs"/> (CBS honors the rolling-horizon window
/// through its SIPP low level). Like every standoff lever it is sim/executor-scoped (production has no executor).
/// </param>
public sealed record SimulationRequest(
    int Width,
    int Height,
    int AgvCount,
    int? Seed = null,
    PlannerKind Planner = PlannerKind.Dijkstra,
    IReadOnlyList<string>? Starts = null,
    long HorizonWindowMs = long.MaxValue,
    bool StepAside = false,
    bool PreventDeadlockCycles = false,
    JointResolverKind JointResolver = JointResolverKind.None,
    bool OptimizeGuidance = false,
    ScenarioKind Scenario = ScenarioKind.Open);
