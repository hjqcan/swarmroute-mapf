using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Coordination.Application;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
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

    private CoordinationTestHost(Guid roadmapId, ServiceProvider services)
    {
        RoadmapId = roadmapId;
        Services = services;
    }

    public static CoordinationTestHost Build(RoadmapGraph graph)
    {
        var roadmapId = Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddLogging();

        // 1. Event bus (real in-memory dispatch + NoOp integration publisher).
        services.AddEventBus();

        // 2. Map read seam — supplied by a graph-backed fake (production uses RoadmapGraphProvider + EF).
        services.AddSingleton<IRoadmapQueryService>(new FakeRoadmapQueryService(roadmapId, graph));

        // 3. PathPlanning (IPathPlanner + NullReservationQuery).
        PathPlanningNativeInjectorBootStrapper.RegisterServices(services);

        // 4. TrafficControl AFTER PathPlanning so ReservationService overrides IReservationQuery.
        TrafficControlNativeInjectorBootStrapper.RegisterServices(services);

        // 5. Coordination cycle (no hosted loop — tests drive RunCycleAsync directly).
        services.AddCoordination();

        return new CoordinationTestHost(roadmapId, services.BuildServiceProvider());
    }

    public IFleetCoordinationCycle Cycle => Services.GetRequiredService<IFleetCoordinationCycle>();

    public void Dispose() => Services.Dispose();
}
