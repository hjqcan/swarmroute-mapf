using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab — Order/Dispatch context) The lifelong dispatch summary driven through the REAL service: opt-in
/// (omitted unless requested → byte-identical), clears its order stream, and — over a contended backlog — shows the
/// online-assignment payoff: Optimal delivers more orders on-time and at lower latency than the uncorrelated pairing.
/// </summary>
public sealed class OrderDispatchClosedLoopTests
{
    private static OrderDispatchReportDto? Dispatch(int seed, AssignmentPolicy policy, bool simulateOrders = true) =>
        new SimulationService(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(new SimulationRequest(10, 8, 6, seed, PlannerKind.Sipp, Assignment: policy, SimulateOrders: simulateOrders))
            .GetAwaiter().GetResult()
            .OrderDispatch;

    [Fact]
    public void Off_by_default_is_omitted()
    {
        Assert.Null(Dispatch(7, AssignmentPolicy.Optimal, simulateOrders: false));
    }

    [Fact]
    public void Present_and_clears_the_stream_when_opted_in()
    {
        var d = Dispatch(7, AssignmentPolicy.Nearest);

        Assert.NotNull(d);
        Assert.True(d!.OrdersTotal > 0);
        Assert.Equal(d.OrdersTotal, d.OrdersCompleted); // a connected field delivers every order
        Assert.InRange(d.OnTimeRate, 0, 1);
        Assert.InRange(d.FleetUtilization, 0, 1);
        Assert.True(d.MaxQueueDepth > 1, "the order stream should outpace the fleet ⇒ a real backlog");
        Assert.Equal("Nearest", d.Policy);
    }

    [Fact]
    public void Optimal_assignment_beats_random_over_the_lifelong_stream()
    {
        var random = Dispatch(7, AssignmentPolicy.Random)!;
        var optimal = Dispatch(7, AssignmentPolicy.Optimal)!;

        Assert.True(optimal.OnTimeRate >= random.OnTimeRate,
            $"optimal on-time {optimal.OnTimeRate:F2} should be ≥ random {random.OnTimeRate:F2}");
        Assert.True(optimal.MeanLatencyMs <= random.MeanLatencyMs,
            $"optimal mean latency {optimal.MeanLatencyMs} should be ≤ random {random.MeanLatencyMs}");
    }
}
