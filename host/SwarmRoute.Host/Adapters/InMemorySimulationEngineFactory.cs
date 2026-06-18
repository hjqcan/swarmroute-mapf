using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.Simulation.Application;
using SwarmRoute.SpatioTemporal.Kernel;
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

        // Drive reservation timing off the discrete simulation tick (not wall-clock): the driver advances this
        // clock each tick so reserved intervals live on the same axis the executor moves on. Replaces the
        // default SystemFleetClock registered by TrafficControl.
        var clock = new ManualFleetClock();
        services.RemoveAll<IFleetClock>();
        services.AddSingleton<IFleetClock>(clock);

        var provider = services.BuildServiceProvider();
        return new Engine(provider, roadmapId, provider.GetRequiredService<IFleetCoordinationCycle>(), clock);
    }

    private sealed class Engine(
        ServiceProvider provider,
        Guid roadmapId,
        IFleetCoordinationCycle cycle,
        ManualFleetClock clock)
        : ISimulationEngine
    {
        public Guid RoadmapId { get; } = roadmapId;

        public IFleetCoordinationCycle Cycle { get; } = cycle;

        public ManualFleetClock Clock { get; } = clock;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
