using System.Collections.Generic;
using System.Linq;
using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Domain.Events;

/// <summary>
/// Raised when a circular wait has been detected and a <c>DeadlockCase</c> opened. Published as an
/// integration event (CAP) so the rest of the fleet can react.
/// </summary>
public class DeadlockCaseDetectedEvent : DomainEvent, IIntegrationEvent
{
    public DeadlockCaseDetectedEvent(
        Guid caseId,
        DeadlockKind kind,
        IReadOnlyList<string> agentIds)
        : base(caseId)
    {
        Kind = kind;
        AgentIds = agentIds?.ToArray() ?? [];
    }

    /// <summary>The kind of deadlock (v0: <see cref="DeadlockKind.Cyclic"/>).</summary>
    public DeadlockKind Kind { get; }

    /// <summary>The agents participating in the circular wait.</summary>
    public IReadOnlyList<string> AgentIds { get; }

    public string EventName => "Deadlock.Case.Detected";
    public string Version => "v1";
}
