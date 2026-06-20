using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for ScenarioBench obstacle maps: the presets carve the expected cells, the obstacle cells are absent
/// from the built grid, and — crucially — the free cells stay <b>connected</b> so the engine can always plan a route
/// (no stranded goals). <see cref="ScenarioKind.Open"/> leaves a full grid.
/// </summary>
public sealed class ScenarioBenchTests
{
    [Fact]
    public void Open_scenario_has_no_obstacles_and_a_full_grid()
    {
        Assert.Empty(ScenarioObstacles.For(ScenarioKind.Open, 6, 6));
        Assert.Equal(36, new GridFieldFactory().BuildGrid(6, 6).Sites.Count);
    }

    [Fact]
    public void Bottleneck_walls_the_middle_column_except_a_central_gap()
    {
        var obstacles = ScenarioObstacles.For(ScenarioKind.Bottleneck, 8, 8); // mid col 4; gap = 2 rows (3,4)

        Assert.Contains(GridFieldFactory.SiteId(0, 4), obstacles);           // walled above the gap
        Assert.Contains(GridFieldFactory.SiteId(7, 4), obstacles);           // walled below the gap
        Assert.DoesNotContain(GridFieldFactory.SiteId(3, 4), obstacles);     // the corridor is open
        Assert.DoesNotContain(GridFieldFactory.SiteId(4, 4), obstacles);
        Assert.DoesNotContain(GridFieldFactory.SiteId(0, 3), obstacles);     // only the middle column is walled
    }

    [Theory]
    [InlineData(ScenarioKind.Bottleneck)]
    [InlineData(ScenarioKind.Obstacles)]
    public void Obstacle_cells_are_absent_and_the_free_cells_stay_connected(ScenarioKind kind)
    {
        const int w = 10, h = 8;
        var obstacles = ScenarioObstacles.For(kind, w, h);
        Assert.NotEmpty(obstacles);

        var field = new GridFieldFactory().BuildGrid(w, h, obstacles: obstacles);
        Assert.Equal(w * h - obstacles.Count, field.Sites.Count);

        var siteIds = field.Sites.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(obstacles, o => Assert.DoesNotContain(o, siteIds));        // no obstacle is a control point

        // Connectivity: every free cell is reachable from the first one — so no start/goal can ever be stranded.
        var first = field.Sites[0].Id;
        Assert.All(
            field.Sites.Where(s => !string.Equals(s.Id, first, StringComparison.Ordinal)),
            s => Assert.NotNull(field.Graph.ShortestPath(first, s.Id)));
    }
}
