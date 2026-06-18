using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Composition-root helper for the Simulation context. Registers the <see cref="ISimulationService"/> and its
/// stateless collaborators (<see cref="GridFieldFactory"/>, <see cref="FleetLoopDriver"/>). The simulation
/// builds its own per-request engine, so this adds nothing else to the Host's container.
/// </summary>
public static class SimulationServiceCollectionExtensions
{
    /// <summary>Registers the Simulation API services.</summary>
    public static IServiceCollection AddSimulation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<GridFieldFactory>();
        services.TryAddSingleton<FleetLoopDriver>();
        services.TryAddScoped<ISimulationService, SimulationService>();

        return services;
    }
}
