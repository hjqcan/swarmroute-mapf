using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Cbs;

/// <summary>How a local CBS solve ended.</summary>
public enum CbsStatus
{
    /// <summary>Conflict-free paths found for all agents (within budget + horizon).</summary>
    Solved,

    /// <summary>The constraint tree was exhausted within the horizon — the cluster is provably unsolvable there.</summary>
    NoSolution,

    /// <summary>A node / cluster-size / constraint-depth budget was hit before a solution — clean fallback.</summary>
    BudgetExceeded,

    /// <summary>An agent has no path at all even unconstrained (goal unreachable / start blocked).</summary>
    Infeasible
}

/// <summary>
/// The outcome of <see cref="CbsLocalSolver.Solve"/>. <see cref="Paths"/> is non-null only when
/// <see cref="Status"/> is <see cref="CbsStatus.Solved"/>; every other status is a clean failure the caller
/// treats identically (fall back, preserving the never-crash / never-collide floor).
/// </summary>
public sealed record CbsResult(
    CbsStatus Status,
    IReadOnlyDictionary<string, SpaceTimePath>? Paths,
    long SumOfCosts,
    int NodesExpanded,
    string? FailureReason)
{
    /// <summary>True when conflict-free paths were found for all agents.</summary>
    public bool Solved => Status == CbsStatus.Solved;

    public static CbsResult Success(IReadOnlyDictionary<string, SpaceTimePath> paths, long sumOfCosts, int nodesExpanded)
        => new(CbsStatus.Solved, paths, sumOfCosts, nodesExpanded, null);

    public static CbsResult Failure(CbsStatus status, int nodesExpanded, string reason)
        => new(status, null, 0, nodesExpanded, reason);
}
