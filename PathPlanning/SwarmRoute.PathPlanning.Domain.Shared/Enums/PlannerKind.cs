namespace SwarmRoute.PathPlanning.Domain.Shared.Enums;

/// <summary>
/// Identifies which planning algorithm produced (or is to produce) a plan.
/// <para>
/// v0 ships only <see cref="Dijkstra"/> (space-only shortest path). <see cref="Sipp"/> (Safe-Interval
/// Path Planning, reservation-aware) is the v1 successor; the enum reserves the slot so the contract
/// is stable across the evolution.
/// </para>
/// </summary>
public enum PlannerKind
{
    /// <summary>Pruned-Dijkstra single-agent shortest path (v0 baseline, space-only).</summary>
    Dijkstra = 1,

    /// <summary>Safe-Interval Path Planning — reservation-aware, time-conflict-free (v1).</summary>
    Sipp = 2
}
