namespace SwarmRoute.Deadlock.Application.Contract.Services;

/// <summary>
/// Escalates an open deadlock resolution as a livelock when the fleet driver detects the victim is making
/// no progress (its distance to the original goal did not strictly decrease across redirect attempts, or
/// the only avoidance point would repeat). Marks the live case <c>Escalated</c> (kind
/// <c>Livelock</c>, raising <c>Deadlock.Case.Escalated</c>) and closes the registry entry so the victim is
/// no longer redirected. The driver-side progress check lives in Coordination/Simulation (where the graph
/// and current pose are known); this service performs the Deadlock-side state change + event.
/// </summary>
public interface IDeadlockEscalationService
{
    /// <summary>
    /// Escalates the open resolution for <paramref name="victimAgentId"/> as a livelock. Returns
    /// <see langword="true"/> if an open resolution was found and escalated; <see langword="false"/> if the
    /// victim had no open resolution (already recovered/closed).
    /// </summary>
    Task<bool> EscalateLivelockAsync(
        string victimAgentId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
