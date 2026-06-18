using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Map.Domain.Events;

/// <summary>
/// Integration event raised when a roadmap topology is imported / (re)persisted. Carries summary counts
/// for observability and downstream bookkeeping.
/// </summary>
public sealed class MapRoadmapImportedEvent : DomainEvent, IIntegrationEvent
{
    public MapRoadmapImportedEvent(
        Guid roadmapId,
        string roadmapName,
        long stateVersion,
        int siteCount,
        int lineCount,
        int blockCount)
        : base(roadmapId)
    {
        RoadmapId = roadmapId;
        RoadmapName = roadmapName;
        StateVersion = stateVersion;
        SiteCount = siteCount;
        LineCount = lineCount;
        BlockCount = blockCount;
    }

    /// <summary>The imported roadmap's aggregate id.</summary>
    public Guid RoadmapId { get; }

    /// <summary>The imported roadmap's name.</summary>
    public string RoadmapName { get; }

    /// <summary>The imported roadmap's optimistic-concurrency version.</summary>
    public long StateVersion { get; }

    /// <summary>Number of sites in the imported roadmap.</summary>
    public int SiteCount { get; }

    /// <summary>Number of lines in the imported roadmap.</summary>
    public int LineCount { get; }

    /// <summary>Number of blocks in the imported roadmap.</summary>
    public int BlockCount { get; }

    /// <inheritdoc />
    public string EventName => "Map.Roadmap.Imported";

    /// <inheritdoc />
    public string Version => "v1";
}
