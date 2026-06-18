using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;
using NetDevPack.Messaging;

namespace SwarmRoute.TrafficControl.Infra.Data.Context;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> tooling can build a <see cref="TrafficControlDbContext"/> without the
/// application host. Uses a placeholder Npgsql connection string — never used at runtime.
/// </summary>
public sealed class TrafficControlDbContextFactory : IDesignTimeDbContextFactory<TrafficControlDbContext>
{
    public TrafficControlDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TrafficControlDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=swarmroute_trafficcontrol;Username=postgres;Password=postgres")
            .Options;

        return new TrafficControlDbContext(
            options,
            new DesignTimeDomainEventDispatcher(),
            integrationEventPublisher: null,
            NullLogger<TrafficControlDbContext>.Instance);
    }

    /// <summary>No-op <see cref="IDomainEventDispatcher"/> used only by the design-time factory.</summary>
    private sealed class DesignTimeDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<DomainEvent> domainEvents) => Task.CompletedTask;

        public void Dispatch(IEnumerable<DomainEvent> domainEvents) { }
    }
}
