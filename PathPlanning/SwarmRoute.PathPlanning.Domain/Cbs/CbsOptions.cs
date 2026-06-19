namespace SwarmRoute.PathPlanning.Domain.Cbs;

/// <summary>
/// Bounds that keep a local CBS solve tractable and guarantee termination. All limits are deterministic COUNTS or
/// TICKS — never wall-clock — so a solve is reproducible. Exceeding any bound returns a clean failure
/// (<see cref="CbsStatus.BudgetExceeded"/>), never a partial or colliding answer.
/// </summary>
/// <param name="MaxAgents">Hard cap on cluster size; above it the solver declines immediately (the exponential base).</param>
/// <param name="HighLevelNodeBudget">Maximum constraint-tree nodes expanded before declining. The primary knob.</param>
/// <param name="TimeHorizonTicks">RHCR-style local frontier: each low-level search is bounded to this many ticks
/// from the release tick (via SIPP's horizon). <see cref="long.MaxValue"/> = plan all the way to goal.</param>
/// <param name="MaxConstraintsPerNode">Depth backstop on a pathological constraint chain.</param>
/// <param name="Continuous">CCBS mode (v3): a child constraint forbids the OTHER agent's whole occupation
/// interval on the conflicting resource (motion-aware, paired with the continuous-time SIPPwRT low level) instead
/// of a single discrete tick. Default <see langword="false"/> = discrete CBS, byte-identical to v3.</param>
public sealed record CbsOptions(
    int MaxAgents = 8,
    int HighLevelNodeBudget = 1000,
    long TimeHorizonTicks = long.MaxValue,
    int MaxConstraintsPerNode = 4096,
    bool Continuous = false);
