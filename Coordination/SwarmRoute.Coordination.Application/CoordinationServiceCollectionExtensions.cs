using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SwarmRoute.Liveness.Domain.Resolution;

namespace SwarmRoute.Coordination.Application;

/// <summary>
/// Composition-root helpers for the Coordination context. Registers the testable cycle body
/// (<see cref="IFleetCoordinationCycle"/>), the goal source, and (optionally) the hosted
/// <see cref="FleetCoordinationLoop"/>. Assumes the four contexts' own bootstrappers have already registered
/// <c>IRoadmapQueryService</c>, <c>IPathPlanner</c>, <c>IReservationQuery</c> and
/// <c>ITrafficCoordinatorAppService</c>.
/// </summary>
public static class CoordinationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the coordination cycle service and goal source. Does NOT register the hosted loop — the Host
    /// adds it via <c>AddHostedService&lt;FleetCoordinationLoop&gt;()</c> (or call
    /// <see cref="AddCoordination(IServiceCollection, bool, Action{CoordinationLoopOptions})"/> to do both).
    /// </summary>
    public static IServiceCollection AddCoordination(this IServiceCollection services)
        => services.AddCoordination(registerHostedLoop: false);

    /// <summary>
    /// Registers the coordination cycle service + goal source, and optionally the hosted
    /// <see cref="FleetCoordinationLoop"/> watchdog. The loop is registered as a singleton so on-demand
    /// callers and the hosted lifecycle share one instance.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="registerHostedLoop">When true, also wires <see cref="FleetCoordinationLoop"/> as a hosted service.</param>
    /// <param name="configure">Optional options configuration (tick interval, watchdog enable).</param>
    public static IServiceCollection AddCoordination(
        this IServiceCollection services,
        bool registerHostedLoop,
        Action<CoordinationLoopOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IFleetCoordinationCycle, CoordinationCycleService>();

        // The PIBT joint-step port for the autonomous loop's JointResolver=Pibt path (the host-seam wrapper over the
        // pure zone-local resolver). Default; a host may register its own before calling this.
        services.TryAddSingleton<IJointStepPlanner, PibtJointStepPlanner>();

        // Default in-memory goal book; the Host may register its own ICoordinationGoalSource before this.
        services.TryAddSingleton<InMemoryCoordinationGoalSource>();
        services.TryAddSingleton<ICoordinationGoalSource>(sp =>
            sp.GetRequiredService<InMemoryCoordinationGoalSource>());

        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<CoordinationLoopOptions>();

        if (registerHostedLoop)
        {
            services.TryAddSingleton<FleetCoordinationLoop>();
            services.AddHostedService(sp => sp.GetRequiredService<FleetCoordinationLoop>());
        }

        return services;
    }
}
