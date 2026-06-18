using Microsoft.EntityFrameworkCore;
using SwarmRoute.Infra.Data.Core.Repositories;
using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Infra.Data.Context;

namespace SwarmRoute.Map.Infra.Data.Repositories;

/// <summary>EF Core repository for the <see cref="Roadmap"/> aggregate.</summary>
public sealed class RoadmapRepository : BaseRepository<MapDbContext, Roadmap>, IRoadmapRepository
{
    public RoadmapRepository(MapDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<Roadmap?> GetWithTopologyAsync(Guid id, CancellationToken cancellationToken = default)
        // Owned collections (Sites/Lines/Blocks) are loaded automatically with the owner. Tracked so the
        // aggregate can be mutated and re-persisted.
        => await Db.Roadmaps.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<Roadmap?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        return await Db.Roadmaps.AsNoTracking().FirstOrDefaultAsync(r => r.Name == trimmed, cancellationToken);
    }
}
