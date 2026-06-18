using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Application.Subscribers;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Infra.BackgroundJobs;
using SwarmRoute.TrafficControl.Infra.Data.Context;

namespace SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;

/// <summary>
/// Composition root for the TrafficControl bounded context. Registers the <b>singleton in-memory
/// authoritative</b> <see cref="ReservationTable"/>, the domain services, the application services, the
/// snapshot/audit DbContext and the Hangfire jobs. Mirrors the grukirbs
/// <c>*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)</c> convention so the Host can wire
/// every context uniformly.
/// </summary>
/// <remarks>
/// <para><b>Key override:</b> this registers <c>IReservationQuery → ReservationService</c>, which overrides
/// PathPlanning's default <c>NullReservationQuery</c> (always-free stub). Because the Host calls PathPlanning's
/// bootstrapper first and TrafficControl's after, the last registration wins and the planner reads the live
/// reservation table.</para>
/// <para>The reservation hot path is entirely in-memory; the <see cref="TrafficControlDbContext"/> is for
/// snapshot/audit only (ADR-002).</para>
/// </remarks>
public static class TrafficControlNativeInjectorBootStrapper
{
    /// <summary>Connection-string key used for the (snapshot/audit) TrafficControl database.</summary>
    public const string ConnectionStringName = "TrafficControlDatabase";

    public static WebApplicationBuilder RegisterServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Infra - Data (snapshot/audit only). The connection string may be absent at design/dev time.
        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        builder.Services.AddDbContext<TrafficControlDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
                options.UseNpgsql(connectionString);
        });

        RegisterCore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Web-agnostic overload registering the TrafficControl services (minus the DbContext wiring) on a bare
    /// <see cref="IServiceCollection"/> — used by non-web hosts and integration tests.
    /// </summary>
    public static IServiceCollection RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RegisterCore(services);
        return services;
    }

    private static void RegisterCore(IServiceCollection services)
    {
        // Topology closure source (parent block + interference + blacklist). v0 default = identity closure;
        // the Host may replace this with a Map-backed DictionaryResourceTopology.
        services.AddSingleton(IResourceTopology.Empty);

        // The fleet clock (single monotonic clock all intervals are measured against).
        services.AddSingleton<IFleetClock, SystemFleetClock>();

        // *** The in-memory authoritative reservation state: a process-wide SINGLETON. ***
        services.AddSingleton(sp => new ReservationTable(sp.GetRequiredService<IResourceTopology>()));

        // Domain services (stateless → singletons).
        services.AddSingleton<IResourceAllocator, ResourceAllocator>();
        services.AddSingleton<IReservationCalendar, ReservationCalendar>();
        services.AddSingleton<IConflictDetector, ConflictDetector>();

        // Application services.
        services.AddSingleton<ITrafficCoordinatorAppService, TrafficCoordinatorAppService>();
        services.AddSingleton<ITrafficControlSnapshotProvider, TrafficControlSnapshotProvider>();
        services.AddSingleton<ITrafficControlOperatorAppService, TrafficControlOperatorAppService>();

        // *** Override PathPlanning's NullReservationQuery with the live reservation-table-backed query. ***
        // (Host registers PathPlanning first; this later registration wins.)
        services.AddSingleton<IReservationQuery, ReservationService>();

        // Replan-trigger subscriber (CAP binding wired at integration).
        services.AddSingleton<ReplanTriggerSubscriber>();

        // Hangfire jobs (scheduling/recurring registration wired in the Host).
        services.AddSingleton<LeaseExpirySweepJob>();
        services.AddSingleton<StaleRequestEscalationJob>();
    }
}
