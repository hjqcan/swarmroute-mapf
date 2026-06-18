using NetDevPack.Messaging;

namespace SwarmRoute.Domain.Abstractions.EventBus;

/// <summary>
/// In-process integration-event consumer used by the v0 event bus. Real CAP/RabbitMQ wiring can bind the same
/// handlers behind transport-specific attributes later; the application code should not need to change.
/// </summary>
public interface IIntegrationEventHandler
{
    /// <summary>True when this handler consumes <paramref name="domainEvent"/>.</summary>
    bool CanHandle(Event domainEvent);

    /// <summary>Handles <paramref name="domainEvent"/>.</summary>
    Task HandleAsync(Event domainEvent, CancellationToken cancellationToken = default);
}
