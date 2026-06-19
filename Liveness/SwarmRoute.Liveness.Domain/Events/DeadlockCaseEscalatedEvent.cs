using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Deadlock.Domain.Shared.Events;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Domain.Events;

/// <summary>
/// Raised when a <c>DeadlockCase</c> is escalated because automatic resolution failed to make progress —
/// in v0 this is the <b>livelock</b> guard firing: the victim was redirected but its distance to its
/// original goal did not strictly decrease (or the same avoidance point would be chosen again), so the
/// fleet stops retrying that victim and surfaces the case for higher-level handling. Lets Coordination /
/// the driver stop redirecting the victim instead of oscillating forever (DoD §6 / WS-Q3 anti-livelock).
/// </summary>
public class DeadlockCaseEscalatedEvent : DomainEvent, IIntegrationEvent, IDeadlockEscalated
{
    public DeadlockCaseEscalatedEvent(
        Guid caseId,
        string victimAgentId,
        DeadlockKind kind,
        string? reason)
        : base(caseId)
    {
        VictimAgentId = victimAgentId;
        Kind = kind;
        Reason = reason;
    }

    /// <inheritdoc />
    public Guid CaseId => AggregateId;

    /// <summary>The agent whose resolution was abandoned.</summary>
    public string VictimAgentId { get; }

    /// <summary>The kind the case was classified as on escalation (v0 livelock guard: <see cref="DeadlockKind.Livelock"/>).</summary>
    public DeadlockKind Kind { get; }

    /// <summary>Why the case escalated (error code / reason), if any.</summary>
    public string? Reason { get; }

    public string EventName => "Deadlock.Case.Escalated";
    public string Version => "v1";
}
