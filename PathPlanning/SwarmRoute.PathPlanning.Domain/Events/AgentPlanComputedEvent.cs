using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.PathPlanning.Domain.Events;

/// <summary>
/// Integration event raised when an <c>AgentPlan</c> successfully (re)computes a route. Carries the ordered
/// site sequence and summary cost for observability and downstream coordination.
/// </summary>
public sealed class AgentPlanComputedEvent : DomainEvent, IIntegrationEvent
{
    public AgentPlanComputedEvent(
        Guid agentPlanId,
        Guid roadmapId,
        string agentId,
        string fromSiteId,
        string toSiteId,
        long stateVersion,
        IReadOnlyList<string> siteSequence,
        long distanceUnits,
        int hopCount,
        long durationMs)
        : base(agentPlanId)
    {
        AgentPlanId = agentPlanId;
        RoadmapId = roadmapId;
        AgentId = agentId;
        FromSiteId = fromSiteId;
        ToSiteId = toSiteId;
        StateVersion = stateVersion;
        SiteSequence = siteSequence;
        DistanceUnits = distanceUnits;
        HopCount = hopCount;
        DurationMs = durationMs;
    }

    /// <summary>The plan aggregate's id.</summary>
    public Guid AgentPlanId { get; }

    /// <summary>The roadmap the plan was computed against.</summary>
    public Guid RoadmapId { get; }

    /// <summary>The agent (vehicle) the plan is for.</summary>
    public string AgentId { get; }

    /// <summary>The start site.</summary>
    public string FromSiteId { get; }

    /// <summary>The goal site.</summary>
    public string ToSiteId { get; }

    /// <summary>The plan's optimistic-concurrency version after the computation.</summary>
    public long StateVersion { get; }

    /// <summary>The ordered site ids of the computed route (inclusive of both ends).</summary>
    public IReadOnlyList<string> SiteSequence { get; }

    /// <summary>Cumulative scaled edge weight along the route.</summary>
    public long DistanceUnits { get; }

    /// <summary>Number of hops (edges) traversed.</summary>
    public int HopCount { get; }

    /// <summary>Planned travel duration in fleet-clock milliseconds.</summary>
    public long DurationMs { get; }

    /// <inheritdoc />
    public string EventName => "PathPlanning.AgentPlan.Computed";

    /// <inheritdoc />
    public string Version => "v1";
}
