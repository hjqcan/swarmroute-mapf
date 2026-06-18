namespace SwarmRoute.PathPlanning.Domain.Shared.Enums;

/// <summary>
/// Identifies which planning algorithm produced (or is to produce) a plan.
/// <para>
/// <see cref="Dijkstra"/> is the v0 space-only baseline. <see cref="Sipp"/> is the v1 Safe-Interval Path
/// Planning implementation; both values are selectable through the stable simulation/API contract.
/// </para>
/// </summary>
public enum PlannerKind
{
    /// <summary>Pruned-Dijkstra single-agent shortest path (v0 baseline, space-only).</summary>
    Dijkstra = 1,

    /// <summary>Safe-Interval Path Planning — reservation-aware, time-conflict-free (v1).</summary>
    Sipp = 2
}
