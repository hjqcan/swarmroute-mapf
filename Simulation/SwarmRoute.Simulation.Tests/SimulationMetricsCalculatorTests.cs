using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the pure <see cref="SimulationMetricsCalculator"/>: travel-time distribution, wait (the
/// stationary-not-arrived signal that catches a gate-blocked en-route agent, not just a Waiting flag), completion,
/// throughput, fairness, and the per-cell congestion heatmap — all derived from a hand-built timeline so the exact
/// values are checkable.
/// </summary>
public sealed class SimulationMetricsCalculatorTests
{
    private static readonly IReadOnlyDictionary<string, (double X, double Y)> Pos =
        new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal)
        {
            ["A"] = (0, 0), ["B"] = (1, 0), ["C"] = (2, 0),
        };

    private static FleetTickPosition P(string id, string cell, AgentMotionState s) => new(id, cell, s);

    // agv-1: A→B→C, arrives tick 2. agv-2: B (holds one tick) →C, arrives tick 3.
    private static FleetLoopResult Sample()
    {
        var frames = new List<FleetTickFrame>
        {
            new(0, [P("agv-1", "A", AgentMotionState.Moving), P("agv-2", "B", AgentMotionState.Waiting)]),
            new(1, [P("agv-1", "B", AgentMotionState.Moving), P("agv-2", "B", AgentMotionState.Waiting)]), // agv-2 stalls on B
            new(2, [P("agv-1", "C", AgentMotionState.Arrived), P("agv-2", "C", AgentMotionState.Moving)]),
            new(3, [P("agv-1", "C", AgentMotionState.Arrived), P("agv-2", "C", AgentMotionState.Arrived)]),
        };
        var routes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["agv-1"] = ["A", "B", "C"], ["agv-2"] = ["B", "C"],
        };
        return new FleetLoopResult(frames, routes,
            new FleetLoopStats(FleetLoopStatus.Completed, Ticks: 3, Collisions: 0, Arrived: 2, Replans: 5),
            MaxConcurrentEnRoute: 2, Collision: null);
    }

    private static readonly IReadOnlyList<FleetAgentSpec> Specs =
        [new("agv-1", "A", "C", 0), new("agv-2", "B", "C", 1)];

    [Fact]
    public void Completion_throughput_and_travel_time_match_the_timeline()
    {
        var m = SimulationMetricsCalculator.Compute(Sample(), Specs, Pos);

        Assert.Equal(2, m.AgvCount);
        Assert.Equal(2, m.Arrived);
        Assert.Equal(1.0, m.CompletionRate, 6);
        Assert.Equal("Completed", m.Status);
        Assert.Equal(3, m.MakespanTicks);
        Assert.Equal(2 * 1000.0 / 3, m.ThroughputPerThousandTicks, 4);

        Assert.Equal(2.5, m.TravelTime.Mean, 6); // arrivals at tick 2 and 3
        Assert.Equal(3, m.TravelTime.P95);
        Assert.Equal(3, m.TravelTime.Max);
    }

    [Fact]
    public void Wait_counts_a_stationary_not_arrived_agent()
    {
        var m = SimulationMetricsCalculator.Compute(Sample(), Specs, Pos);

        Assert.Equal(1, m.TotalWaitTicks);                 // agv-2 held B for one tick
        Assert.Equal((0.0 / 2 + 1.0 / 3) / 2, m.MeanWaitRatio, 6); // agv-1: 0/2, agv-2: 1/3
        Assert.Equal(2, m.MaxConcurrent);
        Assert.Equal(5, m.TotalReplans);
    }

    [Fact]
    public void Heatmap_ranks_the_most_congested_cell_and_carries_coordinates()
    {
        var m = SimulationMetricsCalculator.Compute(Sample(), Specs, Pos);

        var c = m.Heatmap.Single(h => h.SiteId == "C");
        Assert.Equal(4, c.OccupiedTicks);                  // agv-1 + agv-2 on C across ticks 2 and 3
        Assert.Equal(2, c.X);
        var b = m.Heatmap.Single(h => h.SiteId == "B");
        Assert.Equal(1, b.WaitTicks);                      // agv-2 stalled on B once
        Assert.Contains("C", m.BottleneckSiteIds);
        Assert.Contains("B", m.BottleneckSiteIds);
    }

    [Fact]
    public void Fairness_is_jain_over_travel_times()
    {
        var m = SimulationMetricsCalculator.Compute(Sample(), Specs, Pos);
        // Jain([2,3]) = 5^2 / (2 * (4+9)) = 25/26
        Assert.Equal(25.0 / 26.0, m.FairnessIndex, 6);
    }

    [Fact]
    public void Empty_run_is_well_formed_not_a_throw()
    {
        var empty = new FleetLoopResult([], new Dictionary<string, IReadOnlyList<string>>(),
            new FleetLoopStats(FleetLoopStatus.DidNotConverge, 0, 0, 0, 0), 0, null);

        var m = SimulationMetricsCalculator.Compute(empty, [], Pos);

        Assert.Equal(0, m.Arrived);
        Assert.Equal(1.0, m.FairnessIndex, 6); // no agents → no unfairness
        Assert.Empty(m.Heatmap);
    }
}
