using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Liveness.Domain.Resolution;

/// <summary>
/// Port over a one-tick <b>joint step</b> for a congestion cluster: given the cluster's agents, the cells the rest
/// of the fleet holds or claims this tick, the roadmap and a hop-distance oracle, it decides every cluster agent's
/// NEXT control point at once — collision-free among the cluster by construction (vertex-distinct, no head-on lane
/// swap). It is the host-seam abstraction the autonomous loop drives the zone-local resolver through: the loop asks
/// for the joint step and then atomically commits it via the reservation table's
/// <c>TryGrantJointStep</c> (the table as the single authority), so the executor-anchored mechanism
/// (<see cref="PibtZoneResolver"/>) and the host loop share one seam. <see cref="PibtJointStepPlanner"/> is the
/// default implementation; the signature mirrors <see cref="PibtZoneResolver.Resolve"/> verbatim.
/// </summary>
public interface IJointStepPlanner
{
    /// <summary>
    /// Computes <c>agentId → next cell</c> for every cluster agent (a value equal to the agent's current cell means
    /// "hold this tick"). Deterministic for a given input, never throws, and never produces a vertex or head-on
    /// collision within the cluster. <paramref name="blockedCells"/> are the rest of the fleet's positions and
    /// claimed next cells (immovable this tick); <paramref name="hopsToGoal"/> maps a goal to its (cell → hops)
    /// oracle (the caller memoizes it across the episode).
    /// </summary>
    IReadOnlyDictionary<string, string> PlanJointStep(
        IReadOnlyList<PibtAgentView> cluster,
        IReadOnlySet<string> blockedCells,
        RoadmapGraph graph,
        Func<string, IReadOnlyDictionary<string, int>> hopsToGoal);
}
