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
/// releases, then forwards any integration events the aggregate accumulated to the bus before the current DI
/// scope ends.
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
    public async Task<AllocationOutcome> TryReserveAsync(
        SpaceTimePath path,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));

        var outcome = _allocator.Allocate(_table, path, agentId);
        if (outcome == AllocationOutcome.Granted)
            SwarmRouteMetrics.ReservationGrants.Add(1);
        else
            SwarmRouteMetrics.ReservationDenials.Add(1);
        _logger.LogDebug("TryReserve agent={AgentId} cells={Cells} -> {Outcome}", agentId, path.Cells.Count, outcome);
        await DrainAndPublishAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ResourceRef> BlockedResources(SpaceTimePath path, string agentId)
        => _allocator.BlockedResources(_table, path, agentId);

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string agentId,
        IReadOnlyList<ResourceRef> passedResources,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));
        ArgumentNullException.ThrowIfNull(passedResources);

        var freed = _table.ReleaseBehind(agentId, passedResources);
        SwarmRouteMetrics.ReservationReleases.Add(1);
        _logger.LogDebug("Release agent={AgentId} passed={Passed} -> freed={Freed}", agentId, passedResources.Count, freed.Count);
        await DrainAndPublishAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects the integration events the aggregate buffered and publishes them best-effort. Because the
    /// reservation table is in-memory (no DbContext on the hot path) the usual BaseDbContext.Commit dispatch
    /// does not run here, so we drain explicitly.
    /// </summary>
    private async Task DrainAndPublishAsync(CancellationToken cancellationToken)
    {
        var batch = _table.DrainDomainEvents();
        if (batch.Count == 0)
            return;

        if (_publisher is null)
            return;

        try
        {
            await _publisher.PublishAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integration-event publish failed for {Count} event(s).", batch.Count);
        }
    }
}
