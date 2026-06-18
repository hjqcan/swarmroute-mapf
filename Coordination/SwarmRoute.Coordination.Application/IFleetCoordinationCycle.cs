namespace SwarmRoute.Coordination.Application;

/// <summary>
/// The testable body of the lifelong fleet-coordination loop (RHCR-style rolling-horizon re-planning). One
/// <see cref="RunCycleAsync"/> call is one rolling-horizon tick: for each agent goal (processed in a stable
/// priority order) it gets the roadmap graph + the current reservation view, plans a path, then tries to take
/// right-of-way; when control is denied it prunes the contended resources and re-plans within a bounded retry
/// budget — the clean-architecture replacement for the AJR <c>CBS</c> "couldn't lock the path → wait / replan"
/// behaviour. <see cref="Release"/> is the incremental, monotonic resource hand-back as agents drive past.
/// </summary>
public interface IFleetCoordinationCycle
{
    /// <summary>
    /// Runs one coordination cycle over <paramref name="goals"/> on roadmap <paramref name="roadmapId"/> and
    /// returns a per-agent <see cref="CycleReport"/> (planned? reserved? outcome). Deterministic: goals are
    /// processed in ascending <see cref="AgentGoal.Priority"/> then ordinal agent id, so the same inputs always
    /// serialize the same way.
    /// </summary>
    Task<CycleReport> RunCycleAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> goals,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the leases <paramref name="agentId"/> holds on the resources it has driven past (pass-through to
    /// TrafficControl's <c>Release</c>; incremental + monotonic — only the past, invariant I6). Resource ids are
    /// the site/lane/block ids; each is released together with its parent-block + interference closure.
    /// </summary>
    void Release(string agentId, IReadOnlyList<SwarmRoute.SpatioTemporal.Kernel.ResourceRef> passedResources);
}
