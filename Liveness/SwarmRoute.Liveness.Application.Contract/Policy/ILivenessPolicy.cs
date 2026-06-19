namespace SwarmRoute.Liveness.Application.Contract.Policy;

/// <summary>
/// The single owner of liveness detection + resolution policy. Consulted once per <see cref="LivenessPhase"/> per
/// executor tick (each phase at the mechanism point its inputs become available): it observes the fleet's physical
/// state, classifies every stall (head-on swap, blocking chain / congestion cluster, parked-sealed goal),
/// and returns the cheapest safe resolution as a list of <see cref="LivenessDirective"/>s for the executor to apply.
/// (Termination is bounded by the executor's tick budget, the PIBT episode budget, and the step-aside yield window —
/// there is no separate livelock-escalation guard.)
/// <para>
/// Pure and synchronous: a deterministic function of <see cref="LivenessSnapshot"/> (plus the policy's own per-run
/// working memory — the en-route stall streaks it maintains, and the hop-distance cache). It never performs I/O,
/// never mutates engine state, and never plans/reserves — those are the executor's mechanism. The roadmap graph and
/// <see cref="LivenessOptions"/> are bound for the run at construction, so they are not snapshot fields.
/// </para>
/// </summary>
public interface ILivenessPolicy
{
    /// <summary>Classify this phase's stalls and emit the directives that resolve them (empty when nothing applies).</summary>
    IReadOnlyList<LivenessDirective> Evaluate(LivenessSnapshot snapshot);

    /// <summary>
    /// The joint resolver this policy is configured with (<see cref="JointResolverKind.None"/> when none). Lets the
    /// continuous-time executor decide whether to attempt a joint standoff solve before declaring non-convergence,
    /// without re-deriving the choice. Default <see cref="JointResolverKind.None"/>.
    /// </summary>
    JointResolverKind JointResolver => JointResolverKind.None;
}
