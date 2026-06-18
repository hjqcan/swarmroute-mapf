using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetDevPack.Data;
using NetDevPack.Domain;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Infra.Data.Core.Context;

/// <summary>
/// Base <see cref="DbContext"/> with automatic domain-event dispatch, implementing
/// <see cref="IUnitOfWork"/>. Every bounded-context DbContext should derive from this.
/// Ported from grukirbs' <c>BaseDbContext</c>: collect aggregate domain events from the
/// EF ChangeTracker → SaveChanges → dispatch local events via <see cref="IDomainEventDispatcher"/>
/// → publish integration events via the optional <see cref="IIntegrationEventPublisher"/>.
/// </summary>
public abstract class BaseDbContext : DbContext, IUnitOfWork
{
    private readonly IDomainEventDispatcher _domainEventDispatcher;
    private readonly IIntegrationEventPublisher? _integrationEventPublisher;
    private readonly ILogger<BaseDbContext> _logger;

    protected BaseDbContext(
        DbContextOptions options,
        IDomainEventDispatcher domainEventDispatcher,
        IIntegrationEventPublisher? integrationEventPublisher,
        ILogger<BaseDbContext> logger) : base(options)
    {
        _domainEventDispatcher = domainEventDispatcher ?? throw new ArgumentNullException(nameof(domainEventDispatcher));
        _integrationEventPublisher = integrationEventPublisher; // optional: modules without integration events pass null
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Commits the unit of work: persists changes and then dispatches the collected domain events.
    /// </summary>
    public async Task<bool> Commit()
    {
        // 1. Collect domain events from all tracked aggregates.
        var domainEntities = ChangeTracker
            .Entries<Entity>()
            .Where(e => e.Entity.DomainEvents?.Any() == true)
            .Select(e => e.Entity)
            .ToList();

        var domainEvents = domainEntities
            .SelectMany(e => e.DomainEvents!)
            .ToList();

        // 2. Clear events on the aggregates (avoid double dispatch).
        domainEntities.ForEach(e => e.ClearDomainEvents());

        // 3. Persist. (A CAP Outbox interceptor, when configured, writes integration events here.)
        var success = await SaveChangesAsync() > 0;
        if (!success)
        {
            _logger.LogWarning("SaveChanges persisted no rows; skipping domain-event dispatch.");
            return false;
        }

        // 4. Dispatch after the transaction has committed.
        if (domainEvents.Count > 0)
        {
            await DispatchDomainEventsAsync(domainEvents);
        }

        return true;
    }

    /// <summary>
    /// Dispatches collected events: local synchronous handlers + cross-boundary asynchronous publish.
    /// Dispatch failures are logged but never rethrown, so they do not corrupt the business flow.
    /// </summary>
    private async Task DispatchDomainEventsAsync(List<Event> domainEvents)
    {
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();

        // 4.1 Local domain events (synchronous, same bounded context).
        var localDomainEvents = domainEvents.OfType<DomainEvent>().ToList();
        if (localDomainEvents.Count > 0)
        {
            _logger.LogDebug("Dispatching {Count} local domain event(s), CorrelationId: {CorrelationId}",
                localDomainEvents.Count, correlationId);

            try
            {
                await _domainEventDispatcher.DispatchAsync(localDomainEvents);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local domain-event dispatch failed, CorrelationId: {CorrelationId}", correlationId);
            }
        }

        // 4.2 Integration events to the message queue (asynchronous, cross-boundary).
        if (_integrationEventPublisher is null)
        {
            _logger.LogDebug("No IntegrationEventPublisher configured; skipping integration-event publish.");
            return;
        }

        try
        {
            await _integrationEventPublisher.PublishAsync(domainEvents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integration-event publish failed, CorrelationId: {CorrelationId}", correlationId);
        }
    }
}
