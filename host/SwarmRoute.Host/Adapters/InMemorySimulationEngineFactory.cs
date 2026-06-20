using SwarmRoute.Liveness.Domain.Detection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;
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
    public ISimulationEngine Create(
        RoadmapGraph graph,
        PlannerKind planner = PlannerKind.Dijkstra,
        long horizonWindowMs = long.MaxValue,
        bool preventCycles = false,
        IStationCatalog? stationCatalog = null,
        bool costBasedAdmission = false,
        IFleetPlanProvider? fleetPlan = null)
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

        // (FMS-V1 R2) When the run supplies a station catalog, wire the Dispatch dock-admission scheduler + its
        // service-window calendar into THIS isolated container. The calendar plans service windows (dock CP + blocking
        // closure leases) through the same ITrafficCoordinatorAppService → singleton ReservationTable the fleet plans
        // on, so the executor's per-tick admission is coordinated with transit reservations. Registered as singletons
        // to match the per-request engine's single-run lifetime (one logical scope), composing the singleton
        // allocator/table. Omitted entirely when no catalog ⇒ ISimulationEngine.StationScheduler stays null ⇒
        // byte-identical to a non-FMS run.
        if (stationCatalog is not null)
        {
            services.AddSingleton(stationCatalog);
            services.AddSingleton<IStationResourceCalendar, StationResourceCalendar>();

            if (costBasedAdmission)
            {
                // (FMS-V3) Cost-based admission: give the scheduler the traffic-impact analyzer (over this run's
                // graph), the optional fleet-plan priority snapshot, and the cost policy, so a blocking station weighs
                // let-pass vs go-first numerically. The scheduler is constructed explicitly (not via DI activation) so
                // the appended optional ctor params are supplied; off (the default) ⇒ the plain calendar+catalog
                // scheduler below ⇒ byte-identical.
                services.AddSingleton<ITrafficImpactAnalyzer>(new TrafficImpactAnalyzer(graph));
                if (fleetPlan is not null)
                    services.AddSingleton(fleetPlan);
                services.AddSingleton<ICostBasedAdmissionPolicy>(new CostBasedAdmissionPolicy());
                services.AddSingleton<IStationScheduler>(sp => new StationScheduler(
                    sp.GetRequiredService<IStationResourceCalendar>(),
                    sp.GetRequiredService<IStationCatalog>(),
                    sp.GetRequiredService<ITrafficImpactAnalyzer>(),
                    sp.GetService<IFleetPlanProvider>(),
                    sp.GetRequiredService<ICostBasedAdmissionPolicy>()));
            }
            else
            {
                services.AddSingleton<IStationScheduler, StationScheduler>();
            }
        }

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
            clock,
            // Null unless a catalog was supplied above (the scheduler is then registered in this container).
            provider.GetService<IStationScheduler>());
    }

    private sealed class Engine(
        ServiceProvider provider,
        Guid roadmapId,
        IFleetCoordinationCycle cycle,
        ManualFleetClock clock,
        IStationScheduler? stationScheduler)
        : ISimulationEngine
    {
        public Guid RoadmapId { get; } = roadmapId;

        public IFleetCoordinationCycle Cycle { get; } = cycle;

        public ManualFleetClock Clock { get; } = clock;

        public IStationScheduler? StationScheduler { get; } = stationScheduler;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
