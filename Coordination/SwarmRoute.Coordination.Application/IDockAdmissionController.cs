using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Coordination.Application;

/// <summary>
/// The FMS goal-filtering admission seam: a per-cycle hook that, before the coordination loop plans, rewrites the
/// agent goals headed for a station dock point according to the dock-admission verdict and reports which roadmap
/// resources the planner must route everyone else around (停靠准入閘門).
/// <para>
/// <b>Why the port lives here.</b> Admission filtering deals in <see cref="AgentGoal"/>, which lives in this
/// (Coordination) context, while the admission <em>policy</em> (the station scheduler / calendar) lives in the
/// Dispatch context. Per the FMS dependency-cycle rule the port is therefore declared here and implemented in
/// Dispatch (Dispatch → Coordination, one-way): Coordination must not reference Dispatch's application services.
/// </para>
/// <para>
/// <b>Opt-in / byte-identical.</b> When no FMS implementation is registered the loop binds the
/// <see cref="PassThroughDockAdmissionController"/>, which returns the goals unchanged and an empty blocked set, so
/// the coordination cycle is byte-identical to its pre-FMS behaviour. The real Dispatch implementation feeds its
/// rewritten goals and blocked closure into the loop's existing <c>blockedResources</c> path (ADR-F3) — it appends
/// behaviour, it never changes a frozen signature.
/// </para>
/// </summary>
public interface IDockAdmissionController
{
    /// <summary>
    /// Evaluates the cycle's <paramref name="goals"/> against the FMS dock-admission policy and returns the
    /// (possibly rewritten) goals to plan plus the roadmap resources to treat as blocked this cycle.
    /// <para>
    /// Implementations must be deterministic and total: every input goal maps to exactly one output goal (rewritten
    /// or unchanged), preserving the input order, so the caller can feed the result straight into a coordination
    /// cycle without disturbing its stable priority ordering.
    /// </para>
    /// </summary>
    /// <param name="roadmapId">The roadmap the cycle runs on.</param>
    /// <param name="goals">The agent goals about to be planned this cycle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DockAdmissionResult"/>: the admitted (possibly buffer-rewritten) goals, and the set of resources
    /// the planner must route other agents around (the denied stations' blocking closures). The pass-through
    /// implementation returns <paramref name="goals"/> unchanged with an empty blocked set.
    /// </returns>
    Task<DockAdmissionResult> EvaluateAdmissionAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> goals,
        CancellationToken ct = default);
}

/// <summary>
/// The outcome of one <see cref="IDockAdmissionController.EvaluateAdmissionAsync"/> pass (停靠准入結果).
/// </summary>
/// <param name="AdmittedGoals">
/// The goals to plan this cycle, in the same order as the input. A goal whose dock admission was denied is rewritten
/// to hold its vehicle at the station's pre-dock buffer; every other goal (granted, or not bound to a station) is
/// the original instance.
/// </param>
/// <param name="BlockedResources">
/// The roadmap resources to treat as unavailable this cycle — the union of the blocking closures of the stations
/// whose admission was denied — so the planner routes the rest of the fleet around the contended docks. Empty when
/// nothing was denied.
/// </param>
public sealed record DockAdmissionResult(
    IReadOnlyCollection<AgentGoal> AdmittedGoals,
    IReadOnlySet<ResourceRef> BlockedResources)
{
    /// <summary>The goals to plan this cycle, in input order; never <see langword="null"/>.</summary>
    public IReadOnlyCollection<AgentGoal> AdmittedGoals { get; } =
        AdmittedGoals ?? throw new ArgumentNullException(nameof(AdmittedGoals));

    /// <summary>The resources to route other agents around this cycle; never <see langword="null"/>.</summary>
    public IReadOnlySet<ResourceRef> BlockedResources { get; } =
        BlockedResources ?? throw new ArgumentNullException(nameof(BlockedResources));
}

/// <summary>
/// The inert default <see cref="IDockAdmissionController"/>: it admits every goal unchanged and blocks nothing
/// (穿透式停靠准入閘門). Bound by the coordination loop whenever no FMS dock-admission implementation is registered, so
/// the cycle stays byte-identical to its pre-FMS behaviour.
/// </summary>
public sealed class PassThroughDockAdmissionController : IDockAdmissionController
{
    private static readonly IReadOnlySet<ResourceRef> NoBlocked = new HashSet<ResourceRef>();

    /// <inheritdoc />
    public Task<DockAdmissionResult> EvaluateAdmissionAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> goals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goals);
        return Task.FromResult(new DockAdmissionResult(goals, NoBlocked));
    }
}
