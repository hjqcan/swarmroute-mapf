using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Domain.Shared.Events;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Domain.Events;

/// <summary>
/// Raised when a <c>DeadlockCase</c> has been broken and the victim recovered toward its goal. Lets
/// subscribers clear any deadlock-related back-pressure/alarms.
/// </summary>
public class DeadlockCaseResolvedEvent : DomainEvent, IIntegrationEvent, IDeadlockResolved
{
    public DeadlockCaseResolvedEvent(Guid caseId, string victimAgentId)
        : base(caseId)
    {
        VictimAgentId = victimAgentId;
    }

    /// <inheritdoc />
    public Guid CaseId => AggregateId;

    /// <summary>The agent that yielded to break the cycle.</summary>
    public string VictimAgentId { get; }

    public string EventName => "Deadlock.Case.Resolved";
    public string Version => "v1";
}
