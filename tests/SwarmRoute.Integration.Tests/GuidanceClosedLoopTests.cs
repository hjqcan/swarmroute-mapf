using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab) The congestion-fed GuidanceGraph optimizer driven through the REAL engine: a baseline run's
/// measured congestion is turned into per-lane weight penalties, the SAME fleet is re-run on the re-weighted field,
/// and both are returned for comparison. Contract: (1) <b>off = byte-identical</b> single pass, no guidance report;
/// (2) <b>on</b> attaches the baseline + re-weights the hot corridors and the guided pass stays collision-free;
/// (3) <b>value</b> — it raises total throughput on a congested SIPPwRT config (a weight-aware planner);
/// (4) deterministic.
/// </summary>
public sealed class GuidanceClosedLoopTests
{
    private static SimulationResultDto Run(int w, int h, int agv, int seed, PlannerKind p, bool optimize) =>
        new SimulationService(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(w, h, agv, seed, p, OptimizeGuidance: optimize)).GetAwaiter().GetResult();

    private static string Timeline(SimulationResultDto r) =>
        string.Join(";", r.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions.OrderBy(p => p.AgentId, StringComparer.Ordinal).Select(p => $"{p.AgentId}@{p.SiteId}"))));

    [Fact]
    public void Off_is_a_single_pass_byte_identical_to_a_plain_run()
    {
        var off = Run(6, 6, 6, 7, PlannerKind.Sipp, optimize: false);
        var plain = Run(6, 6, 6, 7, PlannerKind.Sipp, optimize: false);

        Assert.Null(off.Guidance);                 // no second pass, no guidance report
        Assert.Equal(Timeline(plain), Timeline(off));
    }

    [Fact]
    public void On_attaches_baseline_metrics_and_reweights_congested_lanes()
    {
        var r = Run(10, 8, 18, 4, PlannerKind.Sippwrt, optimize: true);

        Assert.NotNull(r.Guidance);
        Assert.NotNull(r.Metrics);
        Assert.NotNull(r.Guidance!.Baseline);                       // the unguided run, for comparison
        Assert.True(r.Guidance.AdjustedLanes > 0, "a congested run should re-weight at least one lane");
        Assert.True(r.Guidance.MaxMultiplier > 1.0, "congested corridors should be penalised");
        Assert.Equal(0, r.Metrics!.Collisions);                    // the guided pass is collision-free
        Assert.NotEqual("CollisionDetected", r.Metrics.Status);
    }

    [Fact]
    public void Guidance_raises_total_throughput_on_a_congested_sippwrt_config()
    {
        int baseline = 0, guided = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var r = Run(10, 8, 18, seed, PlannerKind.Sippwrt, optimize: true);
            baseline += r.Guidance!.Baseline.Arrived;
            guided += r.Metrics!.Arrived;
            Assert.Equal(0, r.Metrics.Collisions);                 // never trades convergence for a collision
        }

        Assert.True(guided > baseline,
            $"congestion-fed guidance should raise total arrivals (baseline {baseline} → guided {guided}).");
    }

    [Fact]
    public void Guidance_run_is_deterministic()
    {
        var a = Run(10, 8, 18, 4, PlannerKind.Sippwrt, optimize: true);
        var b = Run(10, 8, 18, 4, PlannerKind.Sippwrt, optimize: true);

        Assert.Equal(a.Metrics!.Arrived, b.Metrics!.Arrived);
        Assert.Equal(a.Guidance!.AdjustedLanes, b.Guidance!.AdjustedLanes);
        Assert.Equal(Timeline(a), Timeline(b));
    }
}
