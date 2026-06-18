namespace SwarmRoute.PathPlanning.Domain.Shared.Enums;

/// <summary>
/// Lifecycle status of an <c>AgentPlan</c>.
/// </summary>
public enum PlanStatus
{
    /// <summary>A valid space-time path was computed and is current.</summary>
    Computed = 1,

    /// <summary>Planning failed (e.g. goal unreachable / endpoint blocked); the plan carries no path.</summary>
    Failed = 2,

    /// <summary>The plan was superseded by a newer plan or invalidated (e.g. topology change, conflict).</summary>
    Superseded = 3
}
