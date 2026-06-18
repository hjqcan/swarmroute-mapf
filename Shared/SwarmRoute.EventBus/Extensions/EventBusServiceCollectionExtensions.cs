using Microsoft.Extensions.DependencyInjection;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.EventBus.Extensions;

/// <summary>
/// DI registration for the event bus. v0 registers in-process pieces so the domain/integration-event
/// dispatch pipeline works without a broker; CAP/RabbitMQ/outbox wiring remains a transport upgrade.
/// </summary>
public static class EventBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NetDevPack in-memory domain-event dispatcher and the v0 in-process integration
    /// publisher.
    /// </summary>
    /// <remarks>
    /// TODO (WS4/WS-X): port grukirbs' <c>AddEventBus(WebApplicationBuilder)</c> — add
    /// <c>services.AddCap(...)</c> with in-memory storage/queue when
    /// <c>EventBus:UseInMemory=true</c>, else PostgreSQL outbox + RabbitMQ. Signature is kept on
    /// <see cref="IServiceCollection"/> (rather than <c>WebApplicationBuilder</c>) so non-web hosts and tests
    /// can call it without an ASP.NET Core dependency.
    /// </remarks>
    public static IServiceCollection AddEventBus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. NetDevPack local domain-event dispatcher.
        services.AddScoped<IDomainEventDispatcher, InMemoryDomainEventDispatcher>();

        // 2. Integration-event publisher (in-process v0; CAP-backed transport can replace this).
        services.AddScoped<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();

        return services;
    }
}
