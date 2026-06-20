using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// Shared test helpers for building small <see cref="RoadmapGraph"/> snapshots from terse edge lists, for the
/// FMS-V2 Dispatch services that reason about transit-core connectivity (traffic impact, parking, endpoints).
/// </summary>
internal static class RoadmapGraphFixtures
{
    /// <summary>
    /// Builds a graph whose vertices are <paramref name="sites"/> and whose directed edges are the given
    /// <c>"from-to"</c> pairs, each with unit distance. The Dispatch connectivity helpers use the undirected
    /// projection, so a single direction per edge still connects both endpoints.
    /// </summary>
    public static RoadmapGraph Directed(IEnumerable<string> sites, params (string From, string To)[] edges)
    {
        var siteEntities = sites
            .Select(id => new MapSite(id, MapSiteType.CPSite, MapPosition.Empty))
            .ToArray();

        var lineEntities = edges
            .Select(e => new MapLine($"{e.From}-{e.To}", e.From, e.To, 1.0))
            .ToArray();

        return RoadmapGraph.Build(siteEntities, lineEntities);
    }

    /// <summary>
    /// A 5-vertex "barbell" graph: two triangles {A,B,C} and {D,E} joined by a single bridge edge C–D. Removing C
    /// (or D) disconnects the two halves — handy for transit-core / articulation-point assertions.
    /// </summary>
    public static RoadmapGraph Barbell()
        => Directed(
            ["A", "B", "C", "D", "E"],
            ("A", "B"), ("B", "C"), ("C", "A"), // triangle 1
            ("C", "D"),                          // bridge
            ("D", "E"), ("E", "D"));             // edge 2 (both directions)

    /// <summary>
    /// A 4-vertex cycle A→B→C→D→A. Every vertex has degree 2 in the undirected projection, so removing any single
    /// vertex leaves the rest connected — there are no articulation points and a bypass always exists.
    /// </summary>
    public static RoadmapGraph Ring()
        => Directed(["A", "B", "C", "D"], ("A", "B"), ("B", "C"), ("C", "D"), ("D", "A"));
}
