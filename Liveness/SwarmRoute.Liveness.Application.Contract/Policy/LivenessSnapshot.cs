using SwarmRoute.Dispatch.Domain.Shared;

namespace SwarmRoute.Liveness.Application.Contract.Policy;

/// <summary>
/// Which point in the executor's discrete tick the policy is being consulted at. The physical-standoff
/// decisions are inherently staged — a parked blocker is relocated <em>before</em> the planner re-routes the
/// walled-out agent; a congestion cluster is formed <em>after</em> plan+reserve (so it sees the freshly-planned
/// poses) but <em>before</em> the schedule resolves who advances; the joint-resolver drive and the per-agent
/// stall/head-on/parked-ahead yields are decided <em>after</em> the schedule is resolved. Each phase is consulted
/// at the exact mechanism point its inputs become available, so the relocation is behaviour-preserving by
/// construction. One <see cref="ILivenessPolicy.Evaluate"/> call per phase per tick.
/// </summary>
public enum LivenessPhase
{
    /// <summary>Before plan+reserve: recover gatekeepers whose yield window elapsed, then relocate parked blockers
    /// off a walled-out agent's approach (so the next plan can route it through).</summary>
    BeforePlanning,

    /// <summary>After plan+reserve, before the schedule resolves advances: form physical-standoff clusters and hand
    /// them to the joint resolver (PIBT enter / CBS solve).</summary>
    ClusterFormation,

    /// <summary>After the schedule has resolved which en-route agents step this tick, before the gate runs: drive
    /// the joint-resolver (PIBT) agents one hop each (and decide which exit the episode this tick).</summary>
    JointDrive,

    /// <summary>After the joint-resolver drive (so any cell a joint-resolver agent just parked on is visible): decide
    /// the per-agent schedule-faithful stall-reroute / head-on yield and the head-on diagnostic. (The parked-ahead
    /// reroute stays the executor's gate-time safety net, against the live parked set.)</summary>
    Advance,
}

/// <summary>
/// One agent's physical liveness state this tick — the read-only view the policy reasons over. Built by the
/// executor from its mutable <c>RunAgent</c>; the policy never sees or mutates engine state directly. Some fields
/// are only meaningful in a particular <see cref="LivenessPhase"/> (e.g. <see cref="ScheduledToAdvance"/> is only
/// set in <see cref="LivenessPhase.Advance"/>); the policy reads each field only in the phase that populates it.
/// </summary>
/// <param name="Id">Stable agent id.</param>
/// <param name="Position">The CP the agent physically occupies this tick.</param>
/// <param name="Goal">The agent's real goal CP.</param>
/// <param name="EffectiveGoal">The goal the agent is planning toward this tick: its active redirect avoidance
/// site if it has one, else <see cref="Goal"/>. The joint resolver plans against this.</param>
/// <param name="Priority">Right-of-way order (lower plans first); ties broken by ordinal id.</param>
/// <param name="EnRouteNextCell">The raw next CP of an en-route agent's committed route (regardless of candidacy);
/// null when not en route or already at the route's end. Used by the head-on / parked-ahead / joint-resolver-blocked
/// checks (the actual reserved route) and, together with the agent's effective goal, by the policy to derive its
/// candidacy-gated intended next cell for cluster detection.</param>
/// <param name="EnRoute">True while holding a reserved path; false when waiting or arrived.</param>
/// <param name="Done">True once arrived and released.</param>
/// <param name="InJointResolver">True while the agent is being driven by the joint resolver (PIBT).</param>
/// <param name="HasActiveRedirect">True while the agent is enacting a deadlock redirect (owned by the deadlock
/// machinery — excluded from cluster formation and step-aside).</param>
/// <param name="HoldingAtAvoidSite">True while the agent sits parked on its avoidance site awaiting recovery.</param>
/// <param name="BlockedTicks">Consecutive ticks this en-route agent failed to advance at the gate (the policy's own
/// running streak, echoed back so the policy is a pure function of the snapshot).</param>
/// <param name="StuckTicks">Consecutive ticks this waiting agent failed to obtain a progressing route (walled-out streak).</param>
/// <param name="YieldTicksRemaining">Ticks left in a relocated gatekeeper's step-aside window (0 when not yielding).</param>
/// <param name="PibtHeldTicks">Consecutive ticks a joint-resolver agent has been forced to hold this episode.</param>
/// <param name="PibtEpisodeTicksLeft">Joint-resolver driving ticks left before the agent disbands back to planning.</param>
/// <param name="AtRouteEnd">(Advance phase) True when the agent is at the last CP of its committed route.</param>
/// <param name="NextCellIsParked">(Advance phase) True when the agent's next route CP is occupied by a parked vehicle.</param>
/// <param name="ScheduledToAdvance">(Advance phase) True when the schedule resolved a step for this agent this tick.</param>
/// <param name="ScheduledToMoveThisTick">(Advance phase, schedule-faithful) True when the planned arrival tick for
/// the next CP has come (so a non-advance is a real stall, not a planned wait).</param>
/// <param name="Mobility">How freely the liveness layer may relocate this vehicle when resolving contention. The
/// default <see cref="MobilityClass.Movable"/> preserves the pre-FMS behaviour exactly; a vehicle that is
/// <see cref="MobilityClass.ImmovableUntilServiceComplete"/> (docked and in service) is a hard immovable obstacle
/// and is never relocated, PIBT-driven, CBS-driven, or yielded by the policy.</param>
public readonly record struct AgentLivenessView(
    string Id,
    string Position,
    string Goal,
    string EffectiveGoal,
    int Priority,
    string? EnRouteNextCell,
    bool EnRoute,
    bool Done,
    bool InJointResolver,
    bool HasActiveRedirect,
    bool HoldingAtAvoidSite,
    int BlockedTicks,
    int StuckTicks,
    int YieldTicksRemaining,
    int PibtHeldTicks,
    int PibtEpisodeTicksLeft,
    bool AtRouteEnd = false,
    bool NextCellIsParked = false,
    bool ScheduledToAdvance = false,
    bool ScheduledToMoveThisTick = false,
    MobilityClass Mobility = MobilityClass.Movable);

/// <summary>
/// The whole-fleet physical state for one tick at one <see cref="Phase"/>: every agent's
/// <see cref="AgentLivenessView"/> plus the cells occupied by parked (arrived) vehicles. The roadmap graph and
/// tuning are held by the policy for the run, so the snapshot is purely the per-tick mutable picture.
/// </summary>
/// <param name="Tick">The discrete tick (used only in diagnostics).</param>
/// <param name="Phase">Which mechanism point in the tick the policy is being consulted at.</param>
/// <param name="ScheduleFaithful">True under the schedule-faithful executor (the stall-reroute / head-on yields
/// apply); false under the greedy gate (only the standoff diagnostic applies).</param>
/// <param name="Agents">Every agent's view this tick.</param>
/// <param name="ParkedCells">Cells a finished vehicle is parked on.</param>
public sealed record LivenessSnapshot(
    long Tick,
    LivenessPhase Phase,
    bool ScheduleFaithful,
    IReadOnlyList<AgentLivenessView> Agents,
    IReadOnlySet<string> ParkedCells);
