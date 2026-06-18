using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.EventBus.Extensions;

/// <summary>
/// DI registration for the event bus. The default overload registers in-process pieces so tests and DB-less
/// development work without a broker; the configuration overload can switch the integration publisher to
/// CAP PostgreSQL storage + RabbitMQ transport.
/// </summary>
public static class EventBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NetDevPack in-memory domain-event dispatcher and the v0 in-process integration
    /// publisher.
    /// </summary>
    /// <remarks>
    /// Signature is kept on <see cref="IServiceCollection"/> (rather than <c>WebApplicationBuilder</c>) so
    /// non-web hosts and tests can call it without an ASP.NET Core dependency.
    /// </remarks>
    public static IServiceCollection AddEventBus(this IServiceCollection services)
        => services.AddEventBus(configuration: null);

    /// <summary>
    /// Registers the local domain dispatcher and either the in-process integration publisher or the CAP-backed
    /// PostgreSQL/RabbitMQ publisher, based on <c>EventBus:UseInMemory</c>.
    /// </summary>
    public static IServiceCollection AddEventBus(this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. NetDevPack local domain-event dispatcher.
        services.AddScoped<IDomainEventDispatcher, InMemoryDomainEventDispatcher>();

        var useInMemory = configuration is null || IsInMemoryEventBus(configuration);
        if (useInMemory)
        {
            services.AddScoped<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
            return services;
        }

        ArgumentNullException.ThrowIfNull(configuration);
        var storageConnectionString = configuration.GetConnectionString("TrafficControlDatabase");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
            storageConnectionString = configuration.GetConnectionString("MapDatabase");
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            throw new InvalidOperationException(
                "EventBus:UseInMemory=false requires ConnectionStrings:TrafficControlDatabase or " +
                "ConnectionStrings:MapDatabase so CAP has durable PostgreSQL storage.");
        }

        services.AddCap(options =>
        {
            options.UsePostgreSql(postgres =>
            {
                postgres.ConnectionString = storageConnectionString;
                postgres.Schema = configuration["EventBus:PostgreSql:Schema"] ?? "cap";
            });

            options.UseRabbitMQ(rabbit =>
            {
                rabbit.HostName = configuration["EventBus:RabbitMq:Host"] ?? "localhost";
                rabbit.UserName = configuration["EventBus:RabbitMq:Username"] ?? "guest";
                rabbit.Password = configuration["EventBus:RabbitMq:Password"] ?? "guest";

                var virtualHost = configuration["EventBus:RabbitMq:VirtualHost"];
                if (!string.IsNullOrWhiteSpace(virtualHost))
                    rabbit.VirtualHost = virtualHost;

                if (int.TryParse(configuration["EventBus:RabbitMq:Port"], out var port))
                    rabbit.Port = port;

                var exchangeName = configuration["EventBus:RabbitMq:ExchangeName"];
                if (!string.IsNullOrWhiteSpace(exchangeName))
                    rabbit.ExchangeName = exchangeName;
            });
        });

        services.AddScoped<IIntegrationEventPublisher, CapIntegrationEventPublisher>();

        return services;
    }

    private static bool IsInMemoryEventBus(IConfiguration configuration)
    {
        var configured = configuration["EventBus:UseInMemory"];
        return string.IsNullOrWhiteSpace(configured) || bool.Parse(configured);
    }
}
