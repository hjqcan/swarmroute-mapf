using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.PathPlanning.Tests.TestSupport;

/// <summary>
/// In-memory fake <see cref="IRoadmapQueryService"/> for end-to-end PathPlanning tests: returns a single
/// pre-built <see cref="RoadmapGraph"/> for a known roadmap id, mimicking the Map context's read seam
/// (including its <see cref="KeyNotFoundException"/> contract for unknown ids).
/// </summary>
internal sealed class FakeRoadmapQueryService : IRoadmapQueryService
{
    private readonly Guid _roadmapId;
    private readonly RoadmapGraph _graph;

    public FakeRoadmapQueryService(Guid roadmapId, RoadmapGraph graph)
    {
        _roadmapId = roadmapId;
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public Task<RoadmapGraph> GetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
    {
        var graph = roadmapId == _roadmapId
            ? _graph
            : throw new KeyNotFoundException($"Roadmap '{roadmapId}' was not found.");
        return Task.FromResult(graph);
    }

    public Task<RoadmapGraph?> TryGetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
        => Task.FromResult(roadmapId == _roadmapId ? _graph : null);

    public void Invalidate(Guid roadmapId)
    {
        // no-op for the fake
    }
}
