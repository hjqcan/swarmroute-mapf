using SwarmRoute.Domain.Abstractions.Repositories;
using SwarmRoute.Map.Domain.Aggregates;

namespace SwarmRoute.Map.Domain.Repositories;

/// <summary>
/// Repository for the <see cref="Roadmap"/> aggregate root. Surfaces the unit of work via
/// <see cref="IBaseRepository{T}"/> plus roadmap-specific lookups.
/// </summary>
public interface IRoadmapRepository : IBaseRepository<Roadmap>
{
    /// <summary>Loads a roadmap with its full topology graph (sites/lines/blocks) for tracking and editing.</summary>
    Task<Roadmap?> GetWithTopologyAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Finds a roadmap by its (unique) name, or <c>null</c>.</summary>
    Task<Roadmap?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
}
