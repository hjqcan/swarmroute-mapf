using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.Coordination.Application;

/// <summary>
/// The deterministic per-agent outcome of one coordination cycle, mirroring the RHCR loop step
/// "plan → request control → (denied? prune &amp; replan)". This is the <b>testable</b> result the loop body
/// returns so callers (and tests) can assert what was planned and reserved without observing side effects.
/// </summary>
/// <param name="AgentId">The agent this result is for.</param>
/// <param name="Planned">True when a path was successfully computed for the agent.</param>
/// <param name="Reserved">True when the planned path was granted right-of-way by TrafficControl.</param>
/// <param name="Outcome">
/// The final allocation outcome from TrafficControl, or <see langword="null"/> when planning failed (so no
/// reservation was attempted).
/// </param>
/// <param name="Attempts">How many plan→reserve attempts were made (1 + the number of prune-and-replan retries).</param>
/// <param name="Path">The path that was reserved (when <see cref="Reserved"/>), else the last planned path (may be null).</param>
/// <param name="FailureReason">A human-readable reason when the agent was neither planned nor reserved.</param>
public sealed record AgentCycleResult(
    string AgentId,
    bool Planned,
    bool Reserved,
    AllocationOutcome? Outcome,
    int Attempts,
    SpaceTimePath? Path,
    string? FailureReason);

/// <summary>
/// The result of running one full coordination cycle over a set of agent goals: the per-agent results in the
/// deterministic order they were processed.
/// </summary>
public sealed record CycleReport(IReadOnlyList<AgentCycleResult> Results)
{
    /// <summary>An empty cycle (no goals processed).</summary>
    public static CycleReport Empty { get; } = new(Array.Empty<AgentCycleResult>());

    /// <summary>Agents whose path was reserved (granted right-of-way) this cycle.</summary>
    public IReadOnlyList<string> ReservedAgentIds =>
        Results.Where(r => r.Reserved).Select(r => r.AgentId).ToList();

    /// <summary>Agents that were planned but could not get control (denied/queued/blocked).</summary>
    public IReadOnlyList<string> ContendedAgentIds =>
        Results.Where(r => r.Planned && !r.Reserved).Select(r => r.AgentId).ToList();

    /// <summary>Agents whose planning failed (no route / unknown site).</summary>
    public IReadOnlyList<string> UnplannableAgentIds =>
        Results.Where(r => !r.Planned).Select(r => r.AgentId).ToList();
}
