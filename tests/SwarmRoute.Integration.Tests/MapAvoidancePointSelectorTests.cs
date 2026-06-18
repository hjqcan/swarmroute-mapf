using Microsoft.Extensions.DependencyInjection;
using NetDevPack.Data;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Integration.Tests;

public sealed class MapAvoidancePointSelectorTests
{
    [Fact]
    public void SelectAvoidancePoint_SkipsCandidateWhenClosureMemberIsOccupied()
    {
        var roadmap = new Roadmap(
            Guid.NewGuid(),
            "avoidance",
            [
                Site("A1", MapSiteType.AvoidSite),
                Site("A2", MapSiteType.AvoidSite),
            ],
            []);
        var block = new ResourceRef(ResourceKind.Block, "B-avoid");
        var topology = new FixedTopology(new Dictionary<ResourceRef, IReadOnlyCollection<ResourceRef>>
        {
            [RoadmapGraph.SiteRef("A1")] = [RoadmapGraph.SiteRef("A1"), block],
            [RoadmapGraph.SiteRef("A2")] = [RoadmapGraph.SiteRef("A2")],
        });
        var snapshot = new ResourceAllocationGraphSnapshot(
            [("other", block)],
            []);

        var services = new ServiceCollection();
        services.AddSingleton<IRoadmapRepository>(new RoadmapRepositoryStub(roadmap));
        using var provider = services.BuildServiceProvider();

        var selector = new MapAvoidancePointSelector(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedGoalSource(roadmap.Id),
            new SnapshotProvider(snapshot),
            topology);

        Assert.Equal("A2", selector.SelectAvoidancePoint("victim"));
    }

    private static MapSite Site(string id, MapSiteType type)
        => new(id, type, MapPosition.Empty);

    private sealed class FixedGoalSource(Guid roadmapId) : ICoordinationGoalSource
    {
        public Guid? CurrentRoadmapId { get; } = roadmapId;

        public IReadOnlyCollection<AgentGoal> CurrentGoals => [];
    }

    private sealed class SnapshotProvider(ResourceAllocationGraphSnapshot snapshot) : ITrafficControlSnapshotProvider
    {
        public ResourceAllocationGraphSnapshot GetSnapshot() => snapshot;
    }

    private sealed class FixedTopology(
        IReadOnlyDictionary<ResourceRef, IReadOnlyCollection<ResourceRef>> closures) : IResourceTopology
    {
        public IReadOnlyCollection<ResourceRef> ClosureOf(ResourceRef resource)
            => closures.TryGetValue(resource, out var closure) ? closure : [resource];

        public bool IsBlacklisted(ResourceRef resource, string agentId) => false;
    }

    private sealed class RoadmapRepositoryStub(Roadmap roadmap) : IRoadmapRepository
    {
        public IUnitOfWork UnitOfWork { get; } = new UnitOfWorkStub();

        public Task<Roadmap?> GetWithTopologyAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(id == roadmap.Id ? roadmap : null);

        public Task<Roadmap?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(name, roadmap.Name, StringComparison.Ordinal) ? roadmap : null);

        public Task<Roadmap?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(id == roadmap.Id ? roadmap : null);

        public Task<IReadOnlyCollection<Roadmap>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Roadmap>>([roadmap]);

        public IQueryable<Roadmap> GetQueryable()
            => new[] { roadmap }.AsQueryable();

        public void Add(Roadmap model) { }

        public void Update(Roadmap model) { }

        public void Remove(Roadmap model) { }

        public Task RemoveByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class UnitOfWorkStub : IUnitOfWork
    {
        public Task<bool> Commit() => Task.FromResult(true);
    }
}
