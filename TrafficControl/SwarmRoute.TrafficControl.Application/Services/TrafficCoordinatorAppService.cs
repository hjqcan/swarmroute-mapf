using Microsoft.Extensions.Logging;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Application.Services;

/// <summary>
/// The write seam implementation (<see cref="ITrafficCoordinatorAppService"/>). Drives the singleton,
/// authoritative <see cref="ReservationTable"/> through the <see cref="IResourceAllocator"/> for grants and
/// releases, then forwards any integration events the aggregate accumulated to the bus (best-effort, so the
/// synchronous hot path is never blocked on the broker).
/// </summary>
public sealed class TrafficCoordinatorAppService : ITrafficCoordinatorAppService
{
    private readonly ReservationTable _table;
    private readonly IResourceAllocator _allocator;
    private readonly IIntegrationEventPublisher? _publisher;
    private readonly ILogger<TrafficCoordinatorAppService> _logger;

    public TrafficCoordinatorAppService(
        ReservationTable table,
        IResourceAllocator allocator,
        ILogger<TrafficCoordinatorAppService> logger,
        IIntegrationEventPublisher? publisher = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publisher = publisher; // optional: dev / tests may run without a bus
    }

    /// <inheritdoc />
    public AllocationOutcome TryReserve(SpaceTimePath path, string agentId)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));

        var outcome = _allocator.Allocate(_table, path, agentId);
        _logger.LogDebug("TryReserve agent={AgentId} cells={Cells} -> {Outcome}", agentId, path.Cells.Count, outcome);
        DrainAndPublish();
        return outcome;
    }

    /// <inheritdoc />
    public void Release(string agentId, IReadOnlyList<ResourceRef> passedResources)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));
        ArgumentNullException.ThrowIfNull(passedResources);

        var freed = _table.ReleaseBehind(agentId, passedResources);
        _logger.LogDebug("Release agent={AgentId} passed={Passed} -> freed={Freed}", agentId, passedResources.Count, freed.Count);
        DrainAndPublish();
    }

    /// <summary>
    /// Collects the integration events the aggregate buffered and publishes them best-effort. Because the
    /// reservation table is in-memory (no DbContext on the hot path) the usual BaseDbContext.Commit dispatch
    /// does not run here, so we drain explicitly.
    /// </summary>
    private void DrainAndPublish()
    {
        var events = _table.DomainEvents;
        if (events is null || events.Count == 0)
            return;

        var batch = events.ToList();
        _table.ClearDomainEvents();

        if (_publisher is null)
            return;

        // Fire-and-forget: cross-context notifications (e.g. Allocation.Contended → Deadlock) must not
        // block the control loop on the broker. Failures are swallowed/logged, never thrown.
        _ = Task.Run(async () =>
        {
            try
            {
                await _publisher.PublishAsync(batch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Best-effort integration-event publish failed for {Count} event(s).", batch.Count);
            }
        });
    }
}
