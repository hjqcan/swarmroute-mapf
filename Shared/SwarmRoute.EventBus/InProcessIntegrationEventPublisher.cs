using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.EventBus;

/// <summary>
/// v0 in-process integration publisher. It dispatches integration-marked domain events to registered
/// <see cref="IIntegrationEventHandler"/> instances within the current DI scope.
/// </summary>
public sealed class InProcessIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly IServiceProvider _services;
    private readonly ILogger<InProcessIntegrationEventPublisher> _logger;

    public InProcessIntegrationEventPublisher(
        IServiceProvider services,
        ILogger<InProcessIntegrationEventPublisher> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task PublishAsync(IEnumerable<Event> domainEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        var events = domainEvents
            .Where(e => e is IIntegrationEvent)
            .ToList();
        if (events.Count == 0)
            return;

        var handlers = _services.GetServices<IIntegrationEventHandler>().ToList();
        if (handlers.Count == 0)
            return;

        foreach (var domainEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var handler in handlers)
            {
                if (!handler.CanHandle(domainEvent))
                    continue;

                try
                {
                    await handler.HandleAsync(domainEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Integration-event handler {Handler} failed for event {EventType}.",
                        handler.GetType().Name,
                        domainEvent.GetType().Name);
                }
            }
        }
    }
}
