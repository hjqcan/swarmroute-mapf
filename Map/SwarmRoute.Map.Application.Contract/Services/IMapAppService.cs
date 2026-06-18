using SwarmRoute.Map.Application.Contract.Dtos;

namespace SwarmRoute.Map.Application.Contract.Services;

/// <summary>
/// Application service for managing roadmap topologies: import (create), read, list and delete.
/// Persists through the roadmap repository + unit of work and raises Map integration events.
/// </summary>
public interface IMapAppService
{
    /// <summary>
    /// Imports a new roadmap topology, validating all aggregate invariants, persisting it and raising
    /// <c>Map.Roadmap.Imported</c> (and <c>Map.Roadmap.Published</c> when requested).
    /// </summary>
    /// <returns>The persisted roadmap as a <see cref="RoadmapDto"/>.</returns>
    Task<RoadmapDto> ImportAsync(ImportRoadmapRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the full topology of a roadmap, or <c>null</c> if not found.</summary>
    Task<RoadmapDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns all roadmaps' topologies.</summary>
    Task<IReadOnlyList<RoadmapDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a serialisable summary of the built graph for a roadmap, or <c>null</c> if not found.</summary>
    Task<RoadmapGraphSummaryDto?> GetGraphSummaryAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-publishes an existing roadmap (raising <c>Map.Roadmap.Published</c> to invalidate downstream caches).
    /// Returns <c>false</c> if the roadmap does not exist.
    /// </summary>
    Task<bool> PublishAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes a roadmap. Returns <c>false</c> if it does not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
