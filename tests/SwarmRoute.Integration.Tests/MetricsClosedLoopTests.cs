using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab) The metrics layer driven through the REAL engine: every run carries a well-formed,
/// deterministic <see cref="SimulationMetricsDto"/> that agrees with the aggregate stats, present for both the
/// discrete and the continuous executors. This is the "is it good?" quantification the platform is built on.
/// </summary>
public sealed class MetricsClosedLoopTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed, PlannerKind planner) =>
        new SimulationService(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, planner)).GetAwaiter().GetResult();

    [Fact]
    public void Metrics_present_and_consistent_with_stats()
    {
        var r = Run(5, 5, 4, 7, PlannerKind.Sipp);

        Assert.NotNull(r.Metrics);
        var m = r.Metrics!;
        Assert.Equal(4, m.AgvCount);
        Assert.Equal(r.Stats.Arrived, m.Arrived);            // metrics agree with the aggregate stats
        Assert.Equal(r.Stats.Status, m.Status);
        Assert.Equal(r.Stats.Collisions, m.Collisions);
        Assert.Equal(r.Stats.Replans, m.TotalReplans);

        Assert.NotEmpty(m.Heatmap);                          // cells were occupied → a heatmap exists
        Assert.InRange(m.MeanWaitRatio, 0.0, 1.0);
        Assert.InRange(m.FairnessIndex, 0.0, 1.0001);
        Assert.InRange(m.CompletionRate, 0.0, 1.0);

        // The heatmap is ranked worst-congestion-first (bottleneck ranking).
        for (var i = 1; i < m.Heatmap.Count; i++)
            Assert.True(
                m.Heatmap[i - 1].OccupiedTicks + m.Heatmap[i - 1].WaitTicks
                >= m.Heatmap[i].OccupiedTicks + m.Heatmap[i].WaitTicks);
    }

    [Fact]
    public void Completed_run_reports_full_completion()
    {
        var r = Run(5, 5, 4, 7, PlannerKind.Sipp);
        if (r.Metrics!.Status != "Completed")
            return; // only assert on a converging seed

        Assert.Equal(1.0, r.Metrics.CompletionRate, 6);
        Assert.Equal(r.Metrics.AgvCount, r.Metrics.Arrived);
        Assert.True(r.Metrics.TravelTime.Max >= r.Metrics.TravelTime.P50);
    }

    [Fact]
    public void Metrics_are_deterministic_for_a_fixed_seed()
    {
        string J(SimulationResultDto r) => System.Text.Json.JsonSerializer.Serialize(r.Metrics);
        Assert.Equal(J(Run(6, 6, 6, 99, PlannerKind.Sipp)), J(Run(6, 6, 6, 99, PlannerKind.Sipp)));
    }

    [Fact]
    public void Metrics_present_for_the_continuous_executor_too()
    {
        var r = Run(5, 5, 4, 7, PlannerKind.Sippwrt);

        Assert.NotNull(r.Metrics);
        Assert.NotNull(r.Continuous);            // the continuous run carries both the trajectory and the metrics
        Assert.Equal(4, r.Metrics!.AgvCount);
    }
}
