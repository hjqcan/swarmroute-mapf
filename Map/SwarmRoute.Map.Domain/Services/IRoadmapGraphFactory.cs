using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Domain.Services;

/// <summary>
/// Domain service that builds a <see cref="RoadmapGraph"/> from roadmap topology. Encapsulates the
/// <c>GraphMap.Init</c> graph-construction rules so callers depend on an abstraction rather than the VO's
/// static factory.
/// </summary>
public interface IRoadmapGraphFactory
{
    /// <summary>Builds a graph directly from site and line collections.</summary>
    RoadmapGraph Build(IReadOnlyCollection<MapSite> sites, IReadOnlyCollection<MapLine> lines);

    /// <summary>Builds a graph from a <see cref="Roadmap"/> aggregate.</summary>
    RoadmapGraph Build(Roadmap roadmap);
}
