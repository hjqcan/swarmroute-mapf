using SwarmRoute.Liveness.Domain.Detection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Liveness.Infra.CrossCutting.IoC;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.Simulation.Application;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Host-side factory for Simulation's per-request in-memory engine. This is deliberately outside
/// Simulation.Application because it knows concrete Infra bootstrappers and DI registration order.
/// </summary>
public sealed class InMemorySimulationEngineFactory : ISimulationEngineFactory
{
    public ISimulationEngine Create(RoadmapGraph graph, PlannerKind planner = PlannerKind.Dijkstra, long horizonWindowMs = long.MaxValue, bool preventCycles = false)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var roadmapId = Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddEventBus();
        services.AddSingleton<IRoadmapQueryService>(new InMemoryRoadmapQueryService(roadmapId, graph));

        // Grant-time deadlock prevention (v2, WouldCloseCycle). Pre-registered before the TrafficControl
        // bootstrapper so its TryAddSingleton<IWouldCloseCycleDetector> Null default defers to this — turning
        // prevention ON for THIS run (default off = the Null detector = byte-identical v0/v1). Per-request and
        // contained to this isolated container, exactly like the planner / horizon switches.
        if (preventCycles)
            services.AddSingleton<IWouldCloseCycleDetector, RagCycleDetector>();

        // Select the planner for THIS run. Pre-registered before the PathPlanning bootstrapper, whose
        // TryAddSingleton<PlannerOptions> then defers to this instance — so the isolated container's
        // SelectablePathPlanner dispatches to Dijkstra or SIPP per request, with no shared global state.
        services.AddSingleton(new PlannerOptions { Default = planner });
        PathPlanningNativeInjectorBootStrapper.RegisterServices(services);
        TrafficControlNativeInjectorBootStrapper.RegisterServices(services);
        DeadlockNativeInjectorBootStrapper.RegisterServices(services);

        services.AddCoordination();

        // RHCR (v2): bound this run's planning horizon. The CoordinationCycleService reads HorizonWindowMs from
        // CoordinationLoopOptions and stamps each PlanRequest; long.MaxValue (default) = unbounded whole-path,
        // byte-identical to v1. Per-request and contained to this isolated container, like PlannerOptions above.
        // (v3) Continuous: the SIPPwRT planner pairs with the continuous executor, so its cluster joint solve must be
        // CCBS (continuous CBS over a SIPPwRT low level) rather than discrete CBS. Off for every discrete planner.
        services.Configure<CoordinationLoopOptions>(o =>
        {
            o.HorizonWindowMs = horizonWindowMs;
            o.Continuous = planner == PlannerKind.Sippwrt;
        });

        // Drive reservation timing off the discrete simulation tick (not wall-clock): the driver advances this
        // clock each tick so reserved intervals live on the same axis the executor moves on. Replaces the
        // default SystemFleetClock registered by TrafficControl.
        var clock = new ManualFleetClock();
        services.RemoveAll<IFleetClock>();
        services.AddSingleton<IFleetClock>(clock);

        var provider = services.BuildServiceProvider();
        return new Engine(
            provider,
            roadmapId,
            provider.GetRequiredService<IFleetCoordinationCycle>(),
            clock);
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
