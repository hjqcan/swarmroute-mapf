using SwarmRoute.PathPlanning.Domain.Shared.Enums;

namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// Selects which registered <see cref="IPathPlanner"/> the <see cref="SelectablePathPlanner"/> dispatches to.
/// <para>
/// One instance is registered per composition root, so selection is per-container: the Host binds
/// <see cref="Default"/> from configuration (staged rollout — <see cref="PlannerKind.Dijkstra"/> until the
/// final flip to <see cref="PlannerKind.Sipp"/>), while each isolated simulation engine sets it to the
/// per-request planner for A/B comparison. The frozen coordination cycle never needs to know which planner is
/// active — it just calls <see cref="IPathPlanner.Plan"/>.
/// </para>
/// </summary>
public sealed class PlannerOptions
{
    /// <summary>The planner the dispatcher uses. Defaults to the v0 baseline so the rollout starts on Dijkstra.</summary>
    public PlannerKind Default { get; set; } = PlannerKind.Dijkstra;
}
