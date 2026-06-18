using AutoMapper;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.PathPlanning.Application.Contract.Dtos;
using SwarmRoute.PathPlanning.Application.Contract.Services;
using SwarmRoute.PathPlanning.Domain.Aggregates;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Domain.ValueObjects;

namespace SwarmRoute.PathPlanning.Application.Services;

/// <summary>
/// Default <see cref="IPathPlanningAppService"/>. Orchestrates a single planning request:
/// <list type="number">
///   <item>resolves the built <c>RoadmapGraph</c> via the Map context's <see cref="IRoadmapQueryService"/>;</item>
///   <item>obtains the current reservation view via <see cref="IReservationQuery"/>
///         (the v0 <see cref="NullReservationQuery"/> hands out an always-free view);</item>
///   <item>runs the configured <see cref="IPathPlanner"/> (v0: <c>DijkstraPathPlanner</c>);</item>
///   <item>builds an <see cref="AgentPlan"/> aggregate from the result (which raises
///         <c>PathPlanning.AgentPlan.Computed</c>/<c>.Failed</c>), then dispatches those events;</item>
///   <item>maps the aggregate to a <see cref="PlanResultDto"/>.</item>
/// </list>
/// <para>
/// Because PathPlanning is a non-persisted compute context (no EF / unit of work), there is no
/// <c>BaseDbContext.Commit()</c> to flush aggregate events. The event dispatcher and integration publisher are
/// therefore resolved <em>optionally</em> and invoked directly here; when neither is registered (e.g. unit
/// tests, standalone), the events are simply collected on the aggregate and not dispatched.
/// </para>
/// </summary>
public sealed class PathPlanningAppService : IPathPlanningAppService
{
    private readonly IRoadmapQueryService _roadmapQuery;
    private readonly IReservationQuery _reservationQuery;
    private readonly IPathPlanner _planner;
    private readonly IMapper _mapper;
    private readonly IDomainEventDispatcher? _domainEventDispatcher;
    private readonly IIntegrationEventPublisher? _integrationEventPublisher;

    public PathPlanningAppService(
        IRoadmapQueryService roadmapQuery,
        IReservationQuery reservationQuery,
        IPathPlanner planner,
        IMapper mapper,
        IDomainEventDispatcher? domainEventDispatcher = null,
        IIntegrationEventPublisher? integrationEventPublisher = null)
    {
        _roadmapQuery = roadmapQuery ?? throw new ArgumentNullException(nameof(roadmapQuery));
        _reservationQuery = reservationQuery ?? throw new ArgumentNullException(nameof(reservationQuery));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _domainEventDispatcher = domainEventDispatcher;
        _integrationEventPublisher = integrationEventPublisher;
    }

    /// <inheritdoc />
    public async Task<PlanResultDto> PlanForAsync(
        Guid roadmapId,
        string agentId,
        string fromSiteId,
        string toSiteId,
        CancellationToken cancellationToken = default)
    {
        // Validates inputs (throws ArgumentException on empty ids). Release time is 0 in v0.
        var request = new PlanRequest(roadmapId, agentId, fromSiteId, toSiteId, releaseTimeMs: 0);

        // Map read seam: throws KeyNotFoundException when the roadmap does not exist.
        var graph = await _roadmapQuery.GetGraphAsync(roadmapId, cancellationToken);

        // TrafficControl read seam (always-free in v0).
        var reservations = _reservationQuery.GetView(roadmapId);

        var result = _planner.Plan(graph, request, reservations);

        // Model the outcome as an aggregate so the transition is explicit and raises the plan event.
        var plan = new AgentPlan(
            Guid.NewGuid(),
            roadmapId,
            request.AgentId,
            request.FromSiteId,
            request.ToSiteId,
            result,
            PlannerKind.Dijkstra);

        await DispatchEventsAsync(plan);

        return _mapper.Map<PlanResultDto>(plan);
    }

    /// <summary>
    /// Dispatches the aggregate's collected domain events to local handlers and publishes the
    /// integration-flagged subset, then clears them. No-ops when the event bus is not registered.
    /// </summary>
    private async Task DispatchEventsAsync(AgentPlan plan)
    {
        var events = plan.DomainEvents;
        if (events is null || events.Count == 0)
            return;

        var snapshot = events.ToList();

        if (_domainEventDispatcher is not null)
            await _domainEventDispatcher.DispatchAsync(snapshot.OfType<DomainEvent>());

        if (_integrationEventPublisher is not null)
            await _integrationEventPublisher.PublishAsync(snapshot);

        plan.ClearDomainEvents();
    }
}
