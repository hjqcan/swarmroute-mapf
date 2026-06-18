using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Map.Domain.Events;

/// <summary>
/// Integration event raised when a roadmap version is published as the active topology. Subscribers
/// (PathPlanning / Coordination) invalidate their cached <c>RoadmapGraph</c> on receipt.
/// </summary>
public sealed class MapRoadmapPublishedEvent : DomainEvent, IIntegrationEvent
{
    public MapRoadmapPublishedEvent(Guid roadmapId, string roadmapName, long stateVersion)
        : base(roadmapId)
    {
        RoadmapId = roadmapId;
        RoadmapName = roadmapName;
        StateVersion = stateVersion;
    }

    /// <summary>The published roadmap's aggregate id.</summary>
    public Guid RoadmapId { get; }

    /// <summary>The published roadmap's name.</summary>
    public string RoadmapName { get; }

    /// <summary>The published roadmap's optimistic-concurrency version.</summary>
    public long StateVersion { get; }

    /// <inheritdoc />
    public string EventName => "Map.Roadmap.Published";

    /// <inheritdoc />
    public string Version => "v1";
}
