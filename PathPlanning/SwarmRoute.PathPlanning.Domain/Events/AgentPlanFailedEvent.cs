using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.PathPlanning.Domain.Events;

/// <summary>
/// Integration event raised when an <c>AgentPlan</c> fails to compute a route (goal unreachable / endpoint
/// blocked / unknown site). Carries the reason for diagnostics and downstream reaction.
/// </summary>
public sealed class AgentPlanFailedEvent : DomainEvent, IIntegrationEvent
{
    public AgentPlanFailedEvent(
        Guid agentPlanId,
        Guid roadmapId,
        string agentId,
        string fromSiteId,
        string toSiteId,
        long stateVersion,
        string reason)
        : base(agentPlanId)
    {
        AgentPlanId = agentPlanId;
        RoadmapId = roadmapId;
        AgentId = agentId;
        FromSiteId = fromSiteId;
        ToSiteId = toSiteId;
        StateVersion = stateVersion;
        Reason = reason;
    }

    /// <summary>The plan aggregate's id.</summary>
    public Guid AgentPlanId { get; }

    /// <summary>The roadmap the plan was attempted against.</summary>
    public Guid RoadmapId { get; }

    /// <summary>The agent (vehicle) the plan is for.</summary>
    public string AgentId { get; }

    /// <summary>The start site.</summary>
    public string FromSiteId { get; }

    /// <summary>The goal site.</summary>
    public string ToSiteId { get; }

    /// <summary>The plan's optimistic-concurrency version after the failure.</summary>
    public long StateVersion { get; }

    /// <summary>Human-readable failure reason (carries a <c>PP-xxx</c> error code).</summary>
    public string Reason { get; }

    /// <inheritdoc />
    public string EventName => "PathPlanning.AgentPlan.Failed";

    /// <inheritdoc />
    public string Version => "v1";
}
