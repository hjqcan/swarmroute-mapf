using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Tests;

/// <summary>Small helpers for constructing roadmap fixtures in tests.</summary>
internal static class Builders
{
    public static MapSite Site(string id, double x = 0, double y = 0, bool enable = true)
        => new(id, MapSiteType.RelaySite, new MapPosition(x, y), enable);

    public static MapLine Line(string id, string from, string to, double distance)
        => new(id, from, to, distance, MapLineType.Straight);

    /// <summary>
    /// A small diamond roadmap:
    /// <code>
    ///        B
    ///      /(2) \(2)
    ///   A           D
    ///      \(5) /(1)
    ///        C
    /// </code>
    /// Directed edges: A→B(2), A→C(5), B→D(2), C→D(1). Shortest A→D = A→B→D = 4 (scaled 4000).
    /// </summary>
    public static Roadmap DiamondRoadmap()
    {
        var sites = new[]
        {
            Site("A", 0, 0),
            Site("B", 1, 1),
            Site("C", 1, -1),
            Site("D", 2, 0)
        };

        var lines = new[]
        {
            Line("A-B", "A", "B", 2.0),
            Line("A-C", "A", "C", 5.0),
            Line("B-D", "B", "D", 2.0),
            Line("C-D", "C", "D", 1.0)
        };

        return new Roadmap(Guid.NewGuid(), "diamond", sites, lines);
    }
}
