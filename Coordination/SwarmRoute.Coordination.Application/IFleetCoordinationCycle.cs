namespace SwarmRoute.Coordination.Application;

/// <summary>
/// The testable body of the lifelong fleet-coordination loop (RHCR-style rolling-horizon re-planning). One
/// <see cref="RunCycleAsync"/> call is one rolling-horizon tick: for each agent goal (processed in a stable
/// priority order) it gets the roadmap graph + the current reservation view, plans a path, then tries to take
/// right-of-way; when control is denied it prunes the contended resources and re-plans within a bounded retry
/// budget — the clean-architecture replacement for the AJR <c>CBS</c> "couldn't lock the path → wait / replan"
/// behaviour. <see cref="ReleaseAsync"/> is the incremental, monotonic resource hand-back as agents drive past.
/// </summary>
public interface IFleetCoordinationCycle
{
    /// <summary>
    /// Runs one coordination cycle over <paramref name="goals"/> on roadmap <paramref name="roadmapId"/> and
    /// returns a per-agent <see cref="CycleReport"/> (planned? reserved? outcome). Deterministic: goals are
    /// processed in ascending <see cref="AgentGoal.Priority"/> then ordinal agent id, so the same inputs always
    /// serialize the same way.
    /// </summary>
    /// <param name="blockedResources">Resources to treat as unavailable this cycle — e.g. control points
    /// physically occupied by parked/completed vehicles or waiting vehicles whose occupancy is not represented
    /// by an active lease. Every plan routes around them (added to the planner's blacklist), while the planner
    /// still exempts each agent's own start/goal. Null = none.</param>
    Task<CycleReport> RunCycleAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> goals,
        IReadOnlySet<SwarmRoute.SpatioTemporal.Kernel.ResourceRef>? blockedResources = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the leases <paramref name="agentId"/> holds on the resources it has driven past (pass-through to
    /// TrafficControl's <c>Release</c>; incremental + monotonic — only the past, invariant I6). Resource ids are
    /// the site/lane/block ids; each is released together with its parent-block + interference closure.
    /// </summary>
    Task ReleaseAsync(
        string agentId,
        IReadOnlyList<SwarmRoute.SpatioTemporal.Kernel.ResourceRef> passedResources,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans a congestion <paramref name="cluster"/> JOINTLY with local CBS (Conflict-Based Search) and reserves
    /// the resulting conflict-free paths atomically (all-or-nothing). The executor calls this when it detects a
    /// physical standoff the per-agent <see cref="RunCycleAsync"/> cannot untangle — agents holding individually
    /// conflict-free reservations that nonetheless block each other. Returns a <see cref="CycleReport"/> shaped
    /// exactly like <see cref="RunCycleAsync"/>'s (each member's reserved path, or all-unreserved when no joint
    /// solution was found / committed, so the caller falls back). The default is a no-op
    /// (<see cref="CycleReport.Empty"/>), keeping the feature inert unless the real cycle implements it.
    /// </summary>
    Task<CycleReport> PlanClusterAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> cluster,
        IReadOnlySet<SwarmRoute.SpatioTemporal.Kernel.ResourceRef>? blockedResources = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CycleReport.Empty);

    /// <summary>
    /// Resolves the physical standoffs among the <paramref name="contended"/> agents (those left unreserved after a
    /// cycle) with the loop's configured joint resolver. Mutually-blocking agents — head-on swaps and circular
    /// blocking chains — are grouped into clusters; each cluster is either solved jointly by CBS/CCBS and reserved
    /// atomically (as <see cref="PlanClusterAsync"/> does), or advanced one joint single-hop committed atomically
    /// through the reservation table (PIBT via <c>TryGrantJointStep</c>, the table as the single authority). Returns a
    /// <see cref="CycleReport"/> for the agents it acted on, which the loop overlays onto the cycle report. The
    /// default is a no-op (<see cref="CycleReport.Empty"/>), so the seam stays inert unless a real cycle implements it.
    /// </summary>
    /// <param name="intendedNextCells">Optional per-agent <c>agentId → the cell it actually tried to enter next</c>
    /// (the reservation/blacklist-aware first hop of its last planned path, from the cycle report). The standoff
    /// clusterer keys off these so it links the agents genuinely blocking each other; when an agent is absent (or the
    /// map is null) the resolver falls back to a geometric next-hop toward its goal.</param>
    Task<CycleReport> ResolveStandoffsAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> contended,
        IReadOnlySet<SwarmRoute.SpatioTemporal.Kernel.ResourceRef>? blockedResources = null,
        IReadOnlyDictionary<string, string?>? intendedNextCells = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CycleReport.Empty);
}
