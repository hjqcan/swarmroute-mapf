using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.PathPlanning.Application.Contract.Services;
using SwarmRoute.PathPlanning.Application.Mapping;
using SwarmRoute.PathPlanning.Application.Services;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Reservations;

namespace SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;

/// <summary>
/// Composition root for the PathPlanning bounded context. Registers the planner strategy, the v0
/// reservation-query stub, the application service and the AutoMapper profile. Mirrors the grukirbs
/// <c>*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)</c> convention so the Host can wire
/// every context uniformly.
/// <para>
/// PathPlanning is a pure compute context — no DbContext / repository / unit of work is registered.
/// </para>
/// </summary>
public static class PathPlanningNativeInjectorBootStrapper
{
    public static WebApplicationBuilder RegisterServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RegisterServices(builder.Services);
        return builder;
    }

    /// <summary>
    /// Web-agnostic overload: registers the PathPlanning services on a bare
    /// <see cref="IServiceCollection"/> (used by non-web hosts and integration tests).
    /// </summary>
    public static IServiceCollection RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Domain - planner strategies, both stateless → singletons. v0 Dijkstra (space-only shortest path) and
        // v1 SIPP (reservation-aware, time-conflict-free) are both registered; the SelectablePathPlanner
        // dispatcher routes IPathPlanner.Plan to whichever PlannerOptions.Default names. PlannerOptions is
        // TryAdd so a caller that builds an isolated container (e.g. a per-request simulation engine) can
        // pre-register its own default and win.
        services.AddSingleton<DijkstraPathPlanner>();
        services.AddSingleton<SippPathPlanner>();
        services.TryAddSingleton<PlannerOptions>();
        services.AddSingleton<IPathPlanner, SelectablePathPlanner>();

        // Read seam to TrafficControl. v0 default = always-free stub; TrafficControl overrides this
        // registration with its reservation-table-backed query once WS4 lands.
        services.AddSingleton<IReservationQuery, NullReservationQuery>();

        // Application - service.
        services.AddScoped<IPathPlanningAppService, PathPlanningAppService>();

        // AutoMapper profiles for this context.
        services.AddLogging();
        services.AddAutoMapper(_ => { }, typeof(PathPlanningMappingProfile).Assembly);

        return services;
    }
}
