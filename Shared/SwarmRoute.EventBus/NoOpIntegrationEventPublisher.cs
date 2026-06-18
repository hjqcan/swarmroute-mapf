using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.EventBus;

/// <summary>
/// No-op <see cref="IIntegrationEventPublisher"/> placeholder for v0/dev wiring.
/// </summary>
/// <remarks>
/// TODO (WS4/WS-X): replace with a CAP-backed publisher that converts integration-flagged
/// <see cref="Event"/>s to DTOs and publishes them to the bus (PostgreSQL outbox + RabbitMQ),
/// mirroring grukirbs' <c>CapIntegrationEventPublisher</c>. Kept as a no-op so the dispatch
/// pipeline (<see cref="SwarmRoute.Infra.Data.Core"/>'s BaseDbContext) compiles and runs end-to-end
/// without the messaging stack present.
/// </remarks>
public sealed class NoOpIntegrationEventPublisher : IIntegrationEventPublisher
{
    public Task PublishAsync(IEnumerable<Event> domainEvents, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
