using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;

namespace SwarmRoute.Dispatch.Infra.CrossCutting.IoC;

/// <summary>
/// Registers the Dispatch bounded context's services into the host container, following the grukirbs
/// <c>*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)</c> convention.
/// <para>
/// The Foundations phase wires the station service-window calendar (<see cref="IStationResourceCalendar"/> →
/// <see cref="StationResourceCalendar"/>) and the dock-admission scheduler (<see cref="IStationScheduler"/> →
/// <see cref="StationScheduler"/>). The calendar composes the frozen TrafficControl
/// <c>ITrafficCoordinatorAppService</c> seam, and the scheduler additionally needs an
/// <see cref="IStationCatalog"/> (the fleet's station definitions) — the Host or a scenario loader supplies that.
/// Every FMS lever is opt-in / additive: these registrations are scoped to a host that calls this bootstrapper and
/// do not alter any existing code path. This round it is intentionally <b>not</b> wired into the Host.
/// </para>
/// </summary>
public static class DispatchNativeInjectorBootStrapper
{
    /// <summary>
    /// Web host overload. Registers the Dispatch context's services (none yet) and returns the builder for chaining.
    /// </summary>
    public static WebApplicationBuilder RegisterServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RegisterCore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Web-agnostic overload used by non-web hosts and integration tests.
    /// </summary>
    public static IServiceCollection RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        RegisterCore(services);
        return services;
    }

    private static void RegisterCore(IServiceCollection services)
    {
        // The service-window calendar and the dock-admission scheduler. SCOPED to match the frozen
        // ITrafficCoordinatorAppService the calendar composes (itself scoped in TrafficControl's bootstrapper),
        // avoiding a captive-dependency capture of a scoped seam in a singleton. The calendar's in-memory window
        // ledger backs only the cheap CanReserveServiceWindow pre-check; the authoritative free/contended verdict
        // is the singleton reservation table's, so a per-scope ledger is sound.
        services.AddScoped<IStationResourceCalendar, StationResourceCalendar>();
        services.AddScoped<IStationScheduler, StationScheduler>();

        // NOTE: StationScheduler additionally depends on IStationCatalog (the fleet's station definitions). It is a
        // data-bearing, scenario-specific dependency, so it is intentionally NOT registered here — the Host or a
        // scenario loader supplies it (e.g. services.AddSingleton<IStationCatalog>(new InMemoryStationCatalog(...))).
    }
}
