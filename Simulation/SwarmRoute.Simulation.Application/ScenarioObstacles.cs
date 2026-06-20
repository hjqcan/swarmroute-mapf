namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab — ScenarioBench) Generates the blocked control-point set for a <see cref="ScenarioKind"/> on a
/// given grid. Pure + deterministic. Every preset is designed to leave the free cells <b>connected</b> (so the
/// engine can always plan a route), while the obstacles create the bottlenecks / winding aisles a uniform grid lacks.
/// The blocked cells are simply omitted from the grid graph by <see cref="GridFieldFactory"/>.
/// </summary>
public static class ScenarioObstacles
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlySet<string> For(ScenarioKind kind, int width, int height) => kind switch
    {
        ScenarioKind.Bottleneck => Bottleneck(width, height),
        ScenarioKind.Obstacles => Pillars(width, height),
        _ => None,
    };

    /// <summary>A vertical wall at the middle column, open only over a central band of rows (the corridor). The two
    /// halves stay connected through the gap; with no room for a wall (width &lt; 3) it degrades to open.</summary>
    private static IReadOnlySet<string> Bottleneck(int width, int height)
    {
        if (width < 3 || height < 2)
            return None;

        var mid = width / 2;
        var gap = Math.Max(2, height / 4);          // a passable-but-congested corridor, not a single-cell deadlock
        var gapStart = (height - gap) / 2;

        var blocked = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 0; row < height; row++)
            if (row < gapStart || row >= gapStart + gap)
                blocked.Add(GridFieldFactory.SiteId(row, mid));
        return blocked;
    }

    /// <summary>Single-cell pillars at every (odd row, odd column): the even rows and columns remain as free aisles,
    /// which keeps the whole field connected (any free cell reaches any other along the even lattice).</summary>
    private static IReadOnlySet<string> Pillars(int width, int height)
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 1; row < height; row += 2)
            for (var col = 1; col < width; col += 2)
                blocked.Add(GridFieldFactory.SiteId(row, col));
        return blocked;
    }
}
