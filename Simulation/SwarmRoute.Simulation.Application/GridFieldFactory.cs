using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Simulation.Application;

/// <summary>Static metadata for one grid control point: its stable id and planar coordinates (X=col, Y=row).</summary>
public sealed record GridSite(string Id, int X, int Y, MapSiteType Type);

/// <summary>The built grid field: the engine-facing <see cref="RoadmapGraph"/> plus the rendering metadata.</summary>
public sealed record GridField(int Width, int Height, RoadmapGraph Graph, IReadOnlyList<GridSite> Sites);

/// <summary>
/// Builds a rectangular grid roadmap of control points connected to their 4-neighbours. Site ids follow the
/// <c>r{row}c{col}</c> convention, positions are <c>MapPosition(X=col, Y=row)</c>, and every site is a
/// <see cref="MapSiteType.WorkSite"/>. Each undirected 4-neighbour adjacency becomes a pair of unit-distance
/// directed <see cref="MapLine"/>s (both directions), mirroring <c>FakeRoadmapQueryService.Graph</c>.
/// </summary>
public sealed class GridFieldFactory
{
    /// <summary>
    /// Builds a <paramref name="width"/>×<paramref name="height"/> grid. Returns the graph and the ordered site
    /// metadata (row-major: r0c0, r0c1, …).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="width"/> or <paramref name="height"/> &lt; 1.</exception>
    public GridField BuildGrid(int width, int height, GuidanceGraph? guidance = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        // (v4 SwarmRoute Lab) Per-lane weight overlay; Identity (the default) leaves every lane at unit distance, so
        // an unguided build is byte-identical.
        var g = guidance ?? GuidanceGraph.Identity;

        var gridSites = new List<GridSite>(width * height);
        var mapSites = new List<MapSite>(width * height);

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var id = SiteId(row, col);
                gridSites.Add(new GridSite(id, X: col, Y: row, MapSiteType.WorkSite));
                mapSites.Add(new MapSite(id, MapSiteType.WorkSite, new MapPosition(x: col, y: row)));
            }
        }

        var lines = new List<MapLine>();
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var here = SiteId(row, col);

                // Connect to the east neighbour (covers all horizontal adjacencies once).
                if (col + 1 < width)
                    AddBidirectional(lines, here, SiteId(row, col + 1), g);

                // Connect to the south neighbour (covers all vertical adjacencies once).
                if (row + 1 < height)
                    AddBidirectional(lines, here, SiteId(row + 1, col), g);
            }
        }

        var graph = RoadmapGraph.Build(mapSites, lines);
        return new GridField(width, height, graph, gridSites);
    }

    /// <summary>The grid site id for (row, col): <c>r{row}c{col}</c>.</summary>
    public static string SiteId(int row, int col) => $"r{row}c{col}";

    private static void AddBidirectional(List<MapLine> lines, string a, string b, GuidanceGraph guidance)
    {
        // Base unit distance × the lane's guidance multiplier (1.0 when unguided → distance 1, byte-identical).
        lines.Add(new MapLine($"{a}-{b}", a, b, distance: guidance.MultiplierFor($"{a}-{b}")));
        lines.Add(new MapLine($"{b}-{a}", b, a, distance: guidance.MultiplierFor($"{b}-{a}")));
    }
}
