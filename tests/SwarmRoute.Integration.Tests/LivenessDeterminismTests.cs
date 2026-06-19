using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Golden-master reproducibility lock for the synchronous <c>ILivenessPolicy</c> seam: the same dense scenario
/// run twice through <see cref="SimulationService.RunAsync"/> must produce a byte-identical
/// <see cref="SimulationResultDto"/> timeline — same frame count and, per tick, the same agents at the same cells
/// in the same motion state. A dense 10×8 / 12-AGV seed with <see cref="JointResolverKind.Pibt"/> is chosen so the
/// run actually drives a PIBT episode (the new policy path), not just the trivial collision-free baseline; this
/// asserts that routing every standoff decision through the policy stayed deterministic.
/// </summary>
public sealed class LivenessDeterminismTests
{
    private static SimulationResultDto Run(SimulationRequest request) =>
        new SimulationService(
                new GridFieldFactory(),
                new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(request).GetAwaiter().GetResult();

    [Fact]
    public void Same_dense_pibt_scenario_replays_byte_identically()
    {
        var request = new SimulationRequest(
            Width: 10, Height: 8, AgvCount: 12, Seed: 99940,
            Planner: PlannerKind.Sipp, StepAside: true, JointResolver: JointResolverKind.Pibt);

        var first = Run(request);
        var second = Run(request);

        // Same outcome and frame count …
        Assert.Equal(first.Stats.Status, second.Stats.Status);
        Assert.Equal(first.Stats.Ticks, second.Stats.Ticks);
        Assert.Equal(first.Stats.Arrived, second.Stats.Arrived);
        Assert.Equal(first.Stats.Collisions, second.Stats.Collisions);
        Assert.Equal(first.Timeline.TickCount, second.Timeline.TickCount);
        Assert.Equal(first.Timeline.Frames.Count, second.Timeline.Frames.Count);

        // … and a byte-identical per-tick timeline (every agent's cell + motion state, in stable id order).
        Assert.Equal(SerializeTimeline(first), SerializeTimeline(second));
    }

    private static string SerializeTimeline(SimulationResultDto result)
        => string.Join(";", result.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions
                .OrderBy(p => p.AgentId, StringComparer.Ordinal)
                .Select(p => $"{p.AgentId}@{p.SiteId}/{p.State}"))));
}
