using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;

namespace SwarmRoute.Integration.Tests.TestSupport;

/// <summary>
/// Builds a DI container wiring the REAL PathPlanning + TrafficControl + Coordination services (via their
/// <see cref="IServiceCollection"/> bootstrapper overloads + <c>AddEventBus()</c>) over an in-memory,
/// graph-backed <see cref="IRoadmapQueryService"/> — no Postgres, no web host. This is the
/// integration-under-test surface for M1 (topology → path) and M2 (multi-agent serialisation).
/// </summary>
public sealed class CoordinationTestHost : IDisposable
{
    public Guid RoadmapId { get; }
    public ServiceProvider Services { get; }

    /// <summary>The built roadmap graph this host serves (also handy for driving <c>FleetLoopDriver</c> directly).</summary>
    public RoadmapGraph Graph { get; }

    private CoordinationTestHost(Guid roadmapId, RoadmapGraph graph, ServiceProvider services)
    {
        RoadmapId = roadmapId;
        Graph = graph;
        Services = services;
    }

    public static CoordinationTestHost Build(
        RoadmapGraph graph,
        IFleetClock? clock = null,
        IResourceTopology? topology = null,
        PlannerKind planner = PlannerKind.Dijkstra)
    {
        var roadmapId = Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddLogging();

        // 1. Event bus (real in-memory integration dispatch).
        services.AddEventBus();

        // 2. Map read seam — supplied by a graph-backed fake (production uses RoadmapGraphProvider + EF).
        services.AddSingleton<IRoadmapQueryService>(new FakeRoadmapQueryService(roadmapId, graph));

        // 3. PathPlanning (SelectablePathPlanner + NullReservationQuery). Pre-register the planner choice so the
        //    bootstrapper's TryAddSingleton<PlannerOptions> defers to it (Dijkstra baseline vs SIPP for A/B).
        services.AddSingleton(new PlannerOptions { Default = planner });
        PathPlanningNativeInjectorBootStrapper.RegisterServices(services);

        // 4. TrafficControl AFTER PathPlanning so ReservationService overrides IReservationQuery.
        TrafficControlNativeInjectorBootStrapper.RegisterServices(services);
        if (topology is not null)
        {
            services.RemoveAll<IResourceTopology>();
            services.AddSingleton(topology);
        }

        if (clock is not null)
        {
            services.RemoveAll<IFleetClock>();
            services.AddSingleton(clock);
        }

        // 5. Coordination cycle (no hosted loop — tests drive RunCycleAsync directly).
        services.AddCoordination();

        return new CoordinationTestHost(roadmapId, graph, services.BuildServiceProvider());
    }

    public IFleetCoordinationCycle Cycle => Services.GetRequiredService<IFleetCoordinationCycle>();

    /// <summary>The write seam used to seed/clear reservations (and to drive contention-triggered scans).</summary>
    public ITrafficCoordinatorAppService Traffic => Services.GetRequiredService<ITrafficCoordinatorAppService>();

    public void Dispose() => Services.Dispose();
}
