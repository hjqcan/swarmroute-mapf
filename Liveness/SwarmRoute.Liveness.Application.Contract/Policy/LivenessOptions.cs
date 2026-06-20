namespace SwarmRoute.Liveness.Application.Contract.Policy;

/// <summary>
/// Which joint resolver owns a physical standoff cluster (the user-selectable v3 lever; was the
/// <c>UsePibt</c>/<c>UseCbs</c> request bools). Exactly one resolver owns a cluster.
/// </summary>
public enum JointResolverKind
{
    /// <summary>No joint resolver — clusters are broken only by the cheap per-agent ladder (yield / stall-reroute).</summary>
    None = 0,

    /// <summary>Zone-local PIBT: greedy one-hop-per-tick priority-inheritance drive (fast).</summary>
    Pibt = 1,

    /// <summary>Zone-local CBS: complete/optimal conflict-based search over the cluster (cracks dense standoffs PIBT can't).</summary>
    Cbs = 2,
}

/// <summary>
/// Tuning for the liveness policy: the user-facing levers plus the standoff thresholds (formerly the
/// <c>const</c> locals scattered in <c>FleetLoopDriver</c>). One value object per run, carried by the policy.
/// Defaults reproduce today's behaviour exactly.
/// </summary>
public sealed record LivenessOptions
{
    /// <summary>The joint resolver for physical standoff clusters. Default <see cref="JointResolverKind.None"/>.</summary>
    public JointResolverKind JointResolver { get; init; } = JointResolverKind.None;

    /// <summary>Relocate a parked vehicle that walls a waiting agent out of its goal (Push-and-Swap-style). Default off.</summary>
    public bool StepAside { get; init; }

    /// <summary>Consecutive blocked ticks before an en-route standoff is logged as a diagnostic.</summary>
    public int StandoffLogThreshold { get; init; } = 12;

    /// <summary>Schedule-faithful: blocked ticks past a CP's planned entry before an agent drops its lease and re-plans.</summary>
    public int StallRerouteThreshold { get; init; } = 12;

    /// <summary>Schedule-faithful: the lower-priority agent in a head-on swap yields after this few ticks (fast).</summary>
    public int HeadOnYieldThreshold { get; init; } = 3;

    /// <summary>A waiting agent unplannable for this many ticks is treated as walled out (triggers step-aside).</summary>
    public int GatekeeperUnblockThreshold { get; init; } = 10;

    /// <summary>Ticks a relocated parked gatekeeper holds aside before it re-parks at its goal. Under the default
    /// (countdown) mode this is the exact, forced hold; under <see cref="PersistentRelocation"/> it is the MINIMUM
    /// hold (the gatekeeper additionally waits until the corridor it freed is no longer needed).</summary>
    public int GatekeeperYieldWindow { get; init; } = 20;

    /// <summary>
    /// Clearance-driven step-aside (opt-in, additive; requires <see cref="StepAside"/>). When off (the default), a
    /// relocated parked gatekeeper re-parks at its own goal the moment its fixed <see cref="GatekeeperYieldWindow"/>
    /// countdown elapses — which can re-seal the corridor while the walled-out agent is still squeezing through it.
    /// When on, <see cref="GatekeeperYieldWindow"/> becomes a <em>minimum</em> hold rather than a forced return: the
    /// gatekeeper keeps holding aside past the window until the corridor it vacated (its own goal cell) is no longer
    /// on any live agent's approach — i.e. the walled agent has advanced past it or reached goal — and only then
    /// returns. The "still needed" test is a pure function of the snapshot the policy already receives (every live
    /// agent's pose + effective goal over the bound roadmap), so no new engine state or seam is introduced.
    /// <para>Default <see langword="false"/> ⇒ the <see cref="RestoreGoal"/> firing condition is byte-identical to
    /// the fixed-countdown behaviour, so every existing step-aside test and closed-loop run is unchanged.</para>
    /// </summary>
    public bool PersistentRelocation { get; init; }

    /// <summary>Blocked ticks before a congestion cluster is handed to the joint resolver (below the stall/gatekeeper thresholds).</summary>
    public int JointResolverTriggerThreshold { get; init; } = 8;

    /// <summary>A joint-resolver agent forced to hold this many consecutive ticks is handed back to prioritized planning.</summary>
    public int JointResolverHeldExitThreshold { get; init; } = 12;
}
