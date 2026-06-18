using Microsoft.Extensions.DependencyInjection;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.EventBus.Extensions;

/// <summary>
/// DI registration for the event bus. Stub for v0 — registers the in-process pieces so the
/// domain-event dispatch pipeline works; the CAP/RabbitMQ/outbox wiring is a documented TODO.
/// </summary>
public static class EventBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NetDevPack in-memory domain-event dispatcher and a placeholder integration
    /// publisher.
    /// </summary>
    /// <remarks>
    /// TODO (WS4/WS-X): port grukirbs' <c>AddEventBus(WebApplicationBuilder)</c> — add
    /// <c>services.AddCap(...)</c> with in-memory storage/queue when
    /// <c>EventBus:UseInMemory=true</c>, else PostgreSQL outbox + RabbitMQ, and swap
    /// <see cref="NoOpIntegrationEventPublisher"/> for a CAP-backed publisher. Signature is kept on
    /// <see cref="IServiceCollection"/> (rather than <c>WebApplicationBuilder</c>) so non-web hosts
    /// and tests can call it without an ASP.NET Core dependency.
    /// </remarks>
    public static IServiceCollection AddEventBus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. NetDevPack local domain-event dispatcher.
        services.AddScoped<IDomainEventDispatcher, InMemoryDomainEventDispatcher>();

        // 2. Integration-event publisher (placeholder until CAP is wired in).
        services.AddScoped<IIntegrationEventPublisher, NoOpIntegrationEventPublisher>();

        return services;
    }
}
