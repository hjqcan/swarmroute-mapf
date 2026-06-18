using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Coordination.Application;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.Simulation.Application;
using SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Host-side factory for Simulation's per-request in-memory engine. This is deliberately outside
/// Simulation.Application because it knows concrete Infra bootstrappers and DI registration order.
/// </summary>
public sealed class InMemorySimulationEngineFactory : ISimulationEngineFactory
{
    public ISimulationEngine Create(RoadmapGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var roadmapId = Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddEventBus();
        services.AddSingleton<IRoadmapQueryService>(new InMemoryRoadmapQueryService(roadmapId, graph));

        PathPlanningNativeInjectorBootStrapper.RegisterServices(services);
        TrafficControlNativeInjectorBootStrapper.RegisterServices(services);
        services.AddCoordination();

        var provider = services.BuildServiceProvider();
        return new Engine(provider, roadmapId, provider.GetRequiredService<IFleetCoordinationCycle>());
    }

    private sealed class Engine(ServiceProvider provider, Guid roadmapId, IFleetCoordinationCycle cycle)
        : ISimulationEngine
    {
        public Guid RoadmapId { get; } = roadmapId;

        public IFleetCoordinationCycle Cycle { get; } = cycle;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
