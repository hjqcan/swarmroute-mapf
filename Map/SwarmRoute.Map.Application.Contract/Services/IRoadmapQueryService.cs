using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Application.Contract.Services;

/// <summary>
/// In-process read seam exposing the built <see cref="RoadmapGraph"/> to other bounded contexts
/// (PathPlanning, Coordination). This is one of the frozen cross-context contracts: a planning cycle
/// reads the graph synchronously every tick, while topology changes are pushed out-of-band via the
/// <c>Map.Roadmap.Published</c> integration event (which invalidates the cached graph).
/// </summary>
/// <remarks>
/// Implementations cache the built graph per roadmap and rebuild lazily on cache miss / after publish.
/// </remarks>
public interface IRoadmapQueryService
{
    /// <summary>
    /// Returns the built <see cref="RoadmapGraph"/> for the given roadmap, building and caching it on first
    /// access. Throws <see cref="KeyNotFoundException"/> if the roadmap does not exist.
    /// </summary>
    Task<RoadmapGraph> GetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to return the built graph; the result is <c>null</c> when the roadmap does not exist.
    /// </summary>
    Task<RoadmapGraph?> TryGetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates any cached graph for the given roadmap (e.g. in response to <c>Map.Roadmap.Published</c>).
    /// </summary>
    void Invalidate(Guid roadmapId);
}
