using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Domain.Services;

/// <summary>Default <see cref="IRoadmapGraphFactory"/> delegating to <see cref="RoadmapGraph.Build"/>.</summary>
public sealed class RoadmapGraphFactory : IRoadmapGraphFactory
{
    /// <inheritdoc />
    public RoadmapGraph Build(IReadOnlyCollection<MapSite> sites, IReadOnlyCollection<MapLine> lines)
        => RoadmapGraph.Build(sites, lines);

    /// <inheritdoc />
    public RoadmapGraph Build(Roadmap roadmap)
    {
        ArgumentNullException.ThrowIfNull(roadmap);
        return RoadmapGraph.Build(roadmap.Sites, roadmap.Lines);
    }
}
