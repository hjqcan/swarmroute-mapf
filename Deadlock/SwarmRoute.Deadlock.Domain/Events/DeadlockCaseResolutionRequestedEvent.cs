using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Domain.Events;

/// <summary>
/// Raised when the Deadlock context has chosen a victim and a resolution strategy and asks the fleet
/// (Coordination / TrafficControl / PathPlanning) to act — e.g. route the victim to an avoidance site
/// and re-plan it. Carries the victim agent id and the suggested avoid target so the consumer does not
/// have to re-derive them.
/// </summary>
public class DeadlockCaseResolutionRequestedEvent : DomainEvent, IIntegrationEvent
{
    public DeadlockCaseResolutionRequestedEvent(
        Guid caseId,
        string victimAgentId,
        ResolutionStrategy strategy,
        string? suggestedAvoidTarget)
        : base(caseId)
    {
        VictimAgentId = victimAgentId;
        Strategy = strategy;
        SuggestedAvoidTarget = suggestedAvoidTarget;
    }

    /// <summary>The agent that should yield.</summary>
    public string VictimAgentId { get; }

    /// <summary>The chosen resolution strategy (v0: <see cref="ResolutionStrategy.SendToAvoidSite"/>).</summary>
    public ResolutionStrategy Strategy { get; }

    /// <summary>
    /// Suggested avoidance target (site id) for the victim, or <see langword="null"/> if the Deadlock
    /// context could not pick one (the consumer/escalation path then decides).
    /// </summary>
    public string? SuggestedAvoidTarget { get; }

    public string EventName => "Deadlock.Case.ResolutionRequested";
    public string Version => "v1";
}
