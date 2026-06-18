using DotNetCore.CAP;
using Microsoft.Extensions.Logging;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.EventBus;

/// <summary>CAP-backed integration publisher using CAP storage as the durable send outbox.</summary>
public sealed class CapIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly ICapPublisher _publisher;
    private readonly ILogger<CapIntegrationEventPublisher> _logger;

    public CapIntegrationEventPublisher(ICapPublisher publisher, ILogger<CapIntegrationEventPublisher> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task PublishAsync(IEnumerable<Event> domainEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = ResolveMetadata(domainEvent);
            if (metadata is null)
                continue;

            await _publisher
                .PublishAsync(metadata.Value.EventName, domainEvent, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Published integration event {EventName} ({EventType}) via CAP.",
                metadata.Value.EventName,
                domainEvent.GetType().Name);
        }
    }

    private static (string EventName, string Version)? ResolveMetadata(Event domainEvent)
    {
        if (domainEvent is IIntegrationEvent integrationEvent)
            return (integrationEvent.EventName, integrationEvent.Version);

        var attribute = domainEvent.GetType()
            .GetCustomAttributes(typeof(IntegrationEventAttribute), inherit: false)
            .OfType<IntegrationEventAttribute>()
            .SingleOrDefault();

        return attribute is null ? null : (attribute.EventName, attribute.Version);
    }
}
