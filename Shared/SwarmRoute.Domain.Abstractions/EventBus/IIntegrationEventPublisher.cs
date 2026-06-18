using NetDevPack.Messaging;

namespace SwarmRoute.Domain.Abstractions.EventBus;

/// <summary>
/// Publishes domain events that are flagged as integration events (via <see cref="IIntegrationEvent"/>
/// or <see cref="IntegrationEventAttribute"/>) to the message queue (e.g. RabbitMQ via CAP).
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Publishes the integration-flagged subset of <paramref name="domainEvents"/> to the bus.
    /// </summary>
    Task PublishAsync(IEnumerable<Event> domainEvents, CancellationToken cancellationToken = default);
}
