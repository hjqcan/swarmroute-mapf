using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Single-roadmap, in-memory <see cref="IRoadmapQueryService"/> backed by a pre-built <see cref="RoadmapGraph"/>
/// — no EF / no Postgres. Used to feed a per-request simulation engine the grid field it was built from
/// (production wiring uses <c>RoadmapGraphProvider</c> over the repository). Mirrors the test fake.
/// </summary>
public sealed class InMemoryRoadmapQueryService : IRoadmapQueryService
{
    private readonly Guid _roadmapId;
    private RoadmapGraph? _graph;

    public InMemoryRoadmapQueryService(Guid roadmapId, RoadmapGraph graph)
    {
        _roadmapId = roadmapId;
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public Task<RoadmapGraph> GetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
        => roadmapId == _roadmapId && _graph is not null
            ? Task.FromResult(_graph)
            : throw new KeyNotFoundException($"Roadmap '{roadmapId}' not found.");

    public Task<RoadmapGraph?> TryGetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
        => Task.FromResult(roadmapId == _roadmapId ? _graph : null);

    public void Invalidate(Guid roadmapId)
    {
        if (roadmapId == _roadmapId)
            _graph = null;
    }
}
