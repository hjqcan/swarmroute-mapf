namespace SwarmRoute.Deadlock.Domain.Shared.Enums;

/// <summary>
/// Lifecycle state of a <c>DeadlockCase</c>: <c>Detected → Resolving → Resolved | Escalated</c>.
/// </summary>
public enum DeadlockCaseStatus
{
    /// <summary>The circular wait has been detected; no resolution has started yet.</summary>
    Detected = 0,

    /// <summary>A resolution strategy has been chosen and is in progress (victim being routed away).</summary>
    Resolving = 1,

    /// <summary>The deadlock was successfully broken and the victim recovered toward its goal.</summary>
    Resolved = 2,

    /// <summary>Automatic resolution failed (e.g. no avoidance site / detour); handed off for escalation.</summary>
    Escalated = 3,
}
