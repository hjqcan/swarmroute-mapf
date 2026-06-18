using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetDevPack.Messaging;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Application.Events;
using SwarmRoute.Map.Application.Mapping;
using SwarmRoute.Map.Application.Services;
using SwarmRoute.Map.Domain.Events;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Domain.Services;
using SwarmRoute.Map.Infra.Data.Context;
using SwarmRoute.Map.Infra.Data.Repositories;

namespace SwarmRoute.Map.Infra.CrossCutting.IoC;

/// <summary>
/// Composition root for the Map bounded context. Registers the DbContext, repository, domain/application
/// services, the cached <see cref="IRoadmapQueryService"/> read seam and the publish-event cache invalidator.
/// Mirrors the grukirbs <c>*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)</c> convention.
/// </summary>
public static class MapNativeInjectorBootStrapper
{
    /// <summary>Connection-string key used for the Map database.</summary>
    public const string ConnectionStringName = "MapDatabase";

    public static WebApplicationBuilder RegisterServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Infra - Data (PostgreSQL / Npgsql). The connection string may be absent at design/dev time.
        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        builder.Services.AddDbContext<MapDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
                options.UseNpgsql(connectionString);
        });

        builder.Services.AddScoped<IRoadmapRepository, RoadmapRepository>();

        // Domain - Services
        builder.Services.AddSingleton<IRoadmapGraphFactory, RoadmapGraphFactory>();
        builder.Services.AddSingleton<IInterferenceCalculator, InterferenceCalculator>();

        // Application - Services
        builder.Services.AddScoped<IMapAppService, MapAppService>();

        // Read seam: cached RoadmapGraph provider (singleton so the cache survives across requests).
        builder.Services.AddSingleton<IRoadmapQueryService, RoadmapGraphProvider>();

        // Local domain-event handler: invalidate the cached graph on publish.
        builder.Services.AddScoped<IDomainEventHandler<MapRoadmapPublishedEvent>, RoadmapPublishedCacheInvalidator>();

        // AutoMapper profiles for this context.
        builder.Services.AddAutoMapper(_ => { }, typeof(MapMappingProfile).Assembly);

        return builder;
    }
}
