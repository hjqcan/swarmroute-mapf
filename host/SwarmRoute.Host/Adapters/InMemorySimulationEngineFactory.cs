using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Coordination.Application.Deadlock;
using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Deadlock.Infra.CrossCutting.IoC;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.Simulation.Application;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Host-side factory for Simulation's per-request in-memory engine. This is deliberately outside
/// Simulation.Application because it knows concrete Infra bootstrappers and DI registration order.
/// </summary>
public sealed class InMemorySimulationEngineFactory : ISimulationEngineFactory
{
    public ISimulationEngine Create(RoadmapGraph graph, PlannerKind planner = PlannerKind.Dijkstra)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var roadmapId = Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddEventBus();
        services.AddSingleton<IRoadmapQueryService>(new InMemoryRoadmapQueryService(roadmapId, graph));

        // Select the planner for THIS run. Pre-registered before the PathPlanning bootstrapper, whose
        // TryAddSingleton<PlannerOptions> then defers to this instance — so the isolated container's
        // SelectablePathPlanner dispatches to Dijkstra or SIPP per request, with no shared global state.
        services.AddSingleton(new PlannerOptions { Default = planner });
        PathPlanningNativeInjectorBootStrapper.RegisterServices(services);
        TrafficControlNativeInjectorBootStrapper.RegisterServices(services);
        DeadlockNativeInjectorBootStrapper.RegisterServices(services);

        services.AddScoped<IDeadlockSnapshotProvider, TrafficSnapshotDeadlockAdapter>();
        services.AddScoped<IDetourReservationService, TrafficDetourReservationAdapter>();
        services.AddScoped<IClearanceConfirmer, SnapshotClearanceConfirmer>();
        services.AddScoped<IAvoidancePointSelector>(sp =>
            new GraphAvoidancePointSelector(
                graph,
                sp.GetRequiredService<ITrafficControlSnapshotProvider>()));

        services.AddCoordination();

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
            provider.GetRequiredService<IFleetRedirectQuery>(),
            provider.GetRequiredService<IDeadlockRecoveryService>(),
            provider.GetRequiredService<IDeadlockEscalationService>());
    }

    private sealed class Engine(
        ServiceProvider provider,
        Guid roadmapId,
        IFleetCoordinationCycle cycle,
        ManualFleetClock clock,
        IFleetRedirectQuery redirects,
        IDeadlockRecoveryService recovery,
        IDeadlockEscalationService escalation)
        : ISimulationEngine
    {
        public Guid RoadmapId { get; } = roadmapId;

        public IFleetCoordinationCycle Cycle { get; } = cycle;

        public ManualFleetClock Clock { get; } = clock;

        public IFleetRedirectQuery Redirects { get; } = redirects;

        public Func<CancellationToken, Task<IReadOnlyCollection<string>>> RecoverTick { get; } =
            recovery.TryRecoverAllAsync;

        public Func<string, CancellationToken, Task> EscalateLivelock { get; } =
            async (victimAgentId, cancellationToken) =>
                await escalation
                    .EscalateLivelockAsync(
                        victimAgentId,
                        "Simulation.Driver.Livelock",
                        cancellationToken)
                    .ConfigureAwait(false);

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class GraphAvoidancePointSelector(
        RoadmapGraph graph,
        ITrafficControlSnapshotProvider snapshots) : IAvoidancePointSelector
    {
        public string? SelectAvoidancePoint(string victimAgentId, IReadOnlySet<string>? excludedSiteIds = null)
        {
            if (string.IsNullOrWhiteSpace(victimAgentId))
                return null;

            var occupiedSites = snapshots.GetSnapshot().Owns
                .Where(o => o.Resource.Kind == ResourceKind.CP)
                .Select(o => o.Resource.Id)
                .ToHashSet(StringComparer.Ordinal);

            return graph.Vertices
                .Where(site => excludedSiteIds is null || !excludedSiteIds.Contains(site))
                .Where(site => !occupiedSites.Contains(site))
                .OrderBy(site => site, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }
}
