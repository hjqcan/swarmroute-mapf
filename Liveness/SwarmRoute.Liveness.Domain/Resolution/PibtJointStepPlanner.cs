using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Liveness.Domain.Resolution;

/// <summary>
/// The default <see cref="IJointStepPlanner"/>: wraps the pure <see cref="PibtZoneResolver"/> verbatim, so the
/// autonomous host-loop seam and the simulation executor compute the <em>same</em> zone-local PIBT joint step.
/// Stateless and deterministic — all per-episode memory (the hop oracle) is supplied by the caller — so it carries
/// every guarantee of the resolver: vertex-distinct targets, no 2-cycle swap, and a graceful all-hold floor.
/// </summary>
public sealed class PibtJointStepPlanner : IJointStepPlanner
{
    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> PlanJointStep(
        IReadOnlyList<PibtAgentView> cluster,
        IReadOnlySet<string> blockedCells,
        RoadmapGraph graph,
        Func<string, IReadOnlyDictionary<string, int>> hopsToGoal)
        => PibtZoneResolver.Resolve(cluster, blockedCells, graph, hopsToGoal);
}
