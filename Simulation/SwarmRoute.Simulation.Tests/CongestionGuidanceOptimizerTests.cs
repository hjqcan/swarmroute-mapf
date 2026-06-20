using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the pure <see cref="CongestionGuidanceOptimizer"/>: it turns a run's congestion heatmap into a
/// <see cref="GuidanceGraph"/> that penalises lanes ENTERING hot cells more than cold ones (so a weight-aware planner
/// steers around the hotspots), and yields the identity overlay when nothing was congested.
/// </summary>
public sealed class CongestionGuidanceOptimizerTests
{
    private static SimulationMetricsDto MetricsWith(params CellCongestionDto[] heatmap) =>
        new(AgvCount: 0, Arrived: 0, CompletionRate: 0, MakespanTicks: 0, ThroughputPerThousandTicks: 0,
            TravelTime: new TravelTimeStatsDto(0, 0, 0, 0, 0), MeanWaitRatio: 0, TotalWaitTicks: 0, TotalReplans: 0,
            MaxConcurrent: 0, Collisions: 0, Status: "Completed", FairnessIndex: 1.0,
            Heatmap: heatmap, BottleneckSiteIds: []);

    [Fact]
    public void Penalises_lanes_into_congested_cells_more_than_cold_ones()
    {
        // r0c1 is the hottest cell (load 10+5=15); r0c0 is cold (load 1).
        var metrics = MetricsWith(
            new CellCongestionDto("r0c1", 1, 0, OccupiedTicks: 10, WaitTicks: 5),
            new CellCongestionDto("r0c0", 0, 0, OccupiedTicks: 1, WaitTicks: 0));
        var field = new GridFieldFactory().BuildGrid(2, 2);

        var g = CongestionGuidanceOptimizer.Derive(metrics, field, strength: 2.0);

        Assert.Equal(3.0, g.MultiplierFor("r0c0-r0c1"), 6);                      // entering the hottest cell: 1 + 2*15/15
        Assert.True(g.MultiplierFor("r0c0-r0c1") > g.MultiplierFor("r0c1-r0c0")); // hotter target ⇒ heavier penalty
        Assert.Equal(3.0, g.MaxMultiplier, 6);
        Assert.True(g.AdjustedLaneCount > 0);
    }

    [Fact]
    public void No_congestion_yields_the_identity_overlay()
    {
        var field = new GridFieldFactory().BuildGrid(2, 2);

        var g = CongestionGuidanceOptimizer.Derive(MetricsWith(), field);

        Assert.Equal(0, g.AdjustedLaneCount);
        Assert.Equal(1.0, g.MaxMultiplier, 6);
        Assert.Equal(1.0, g.MultiplierFor("r0c0-r0c1"), 6);
    }
}
