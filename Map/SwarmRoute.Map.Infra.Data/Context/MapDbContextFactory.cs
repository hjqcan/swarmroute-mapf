using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;
using NetDevPack.Messaging;

namespace SwarmRoute.Map.Infra.Data.Context;

/// <summary>
/// Design-time factory enabling <c>dotnet ef</c> tooling (migrations) to construct a <see cref="MapDbContext"/>
/// without the full application host. Uses a placeholder Npgsql connection string — never used at runtime.
/// </summary>
public sealed class MapDbContextFactory : IDesignTimeDbContextFactory<MapDbContext>
{
    public MapDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MapDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=swarmroute_map;Username=postgres;Password=postgres")
            .Options;

        // Design-time event dispatch is a no-op; migrations never raise domain events.
        return new MapDbContext(
            options,
            new DesignTimeDomainEventDispatcher(),
            integrationEventPublisher: null,
            NullLogger<MapDbContext>.Instance);
    }

    /// <summary>No-op <see cref="IDomainEventDispatcher"/> used only by the design-time factory.</summary>
    private sealed class DesignTimeDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<DomainEvent> domainEvents) => Task.CompletedTask;

        public void Dispatch(IEnumerable<DomainEvent> domainEvents) { }
    }
}
