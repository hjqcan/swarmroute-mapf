using NetDevPack.Messaging;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.Events;

namespace SwarmRoute.Map.Application.Events;

/// <summary>
/// Local domain-event handler that invalidates the cached <c>RoadmapGraph</c> when a roadmap is published,
/// so the next read rebuilds from the new topology. Wired to <see cref="MapRoadmapPublishedEvent"/>.
/// </summary>
public sealed class RoadmapPublishedCacheInvalidator : IDomainEventHandler<MapRoadmapPublishedEvent>
{
    private readonly IRoadmapQueryService _queryService;

    public RoadmapPublishedCacheInvalidator(IRoadmapQueryService queryService)
        => _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));

    public Task Handle(MapRoadmapPublishedEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _queryService.Invalidate(@event.RoadmapId);
        return Task.CompletedTask;
    }
}
