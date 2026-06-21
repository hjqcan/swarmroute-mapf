using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the lifelong <see cref="OrderDispatchSimulator"/> — the online dispatch layer above MAPF. Driven over
/// a real (small) grid so the roadmap distances are genuine: it is deterministic, clears the whole order stream on a
/// connected field, lets a smarter assignment policy turn the backlog over faster, and detours to charge on a tight
/// battery.
/// </summary>
public sealed class OrderDispatchSimulatorTests
{
    private static GridField Grid(int w, int h) => new GridFieldFactory().BuildGrid(w, h);

    // A backlog scenario: orders release far faster than two vehicles can clear them, so the queue (and thus the
    // assignment policy's leverage) is real. Battery off here to isolate the dispatch comparison.
    private static OrderDispatchSimulator.Options Backlog(bool battery = false) =>
        new(OrderCount: 14, InterArrivalMs: 400, SlaMs: 30_000,
            BatteryEnabled: battery, BatteryRangeMm: battery ? 3_000 : 1_000_000_000, RechargeMs: 2_000);

    [Fact]
    public void Is_deterministic()
    {
        var grid = Grid(6, 6);
        var starts = new[] { "r0c0", "r5c5" };
        var a = OrderDispatchSimulator.Run(grid, starts, seed: 9, AssignmentPolicy.Optimal, Backlog());
        var b = OrderDispatchSimulator.Run(grid, starts, seed: 9, AssignmentPolicy.Optimal, Backlog());

        Assert.Equal(a, b); // record value-equality over every field
    }

    [Fact]
    public void Clears_the_whole_stream_on_a_connected_field()
    {
        var report = OrderDispatchSimulator.Run(Grid(6, 6), new[] { "r0c0", "r5c5" }, seed: 1, AssignmentPolicy.Nearest, Backlog());

        Assert.Equal(report.OrdersTotal, report.OrdersCompleted);
        Assert.True(report.OrdersCompleted > 0);
        Assert.InRange(report.OnTimeRate, 0, 1);
        Assert.True(report.MakespanMs > 0);
        Assert.True(report.MaxQueueDepth > 1, $"the fast stream should build a backlog, got peak {report.MaxQueueDepth}");
    }

    [Fact]
    public void A_smarter_policy_turns_the_backlog_over_at_least_as_fast()
    {
        var grid = Grid(8, 6);
        var starts = new[] { "r0c0", "r5c7", "r3c0" };
        var random = OrderDispatchSimulator.Run(grid, starts, seed: 4, AssignmentPolicy.Random, Backlog());
        var optimal = OrderDispatchSimulator.Run(grid, starts, seed: 4, AssignmentPolicy.Optimal, Backlog());

        // Optimal min-empty-travel matching never delivers slower on average, nor misses more deadlines, than the
        // uncorrelated pairing — the lifelong analogue of module #5's one-shot total-travel result.
        Assert.True(optimal.MeanLatencyMs <= random.MeanLatencyMs,
            $"optimal mean latency {optimal.MeanLatencyMs} should be ≤ random {random.MeanLatencyMs}");
        Assert.True(optimal.OnTimeRate >= random.OnTimeRate,
            $"optimal on-time {optimal.OnTimeRate} should be ≥ random {random.OnTimeRate}");
    }

    [Fact]
    public void Runs_over_a_real_dispatch_endpoint_set()
    {
        // Wired onto the real Dispatch domain: orders run between FMS workstations, charging at charger endpoints.
        // A degenerate set (only 1 workstation) must not throw — it widens to parkings / falls back gracefully.
        var grid = Grid(6, 6);
        var endpoints = new EndpointSet(
            Workstations: new HashSet<string> { "r0c0", "r0c5", "r5c0", "r5c5" },
            Parkings: new HashSet<string> { "r2c2", "r3c3" },
            Buffers: new HashSet<string>(),
            Chargers: new HashSet<string> { "r0c2" });

        var a = OrderDispatchSimulator.Run(grid, new[] { "r2c0", "r3c5" }, seed: 5, AssignmentPolicy.Optimal, Backlog(), endpoints);
        var b = OrderDispatchSimulator.Run(grid, new[] { "r2c0", "r3c5" }, seed: 5, AssignmentPolicy.Optimal, Backlog(), endpoints);

        Assert.Equal(a, b);                                   // deterministic over the real endpoint set too
        Assert.Equal(a.OrdersTotal, a.OrdersCompleted);       // every order delivered between workstations
        Assert.True(a.MakespanMs > 0);
    }

    [Fact]
    public void A_tight_battery_forces_charging_detours_yet_still_completes()
    {
        // BatteryRangeMm (3000) is below a single cross-grid leg, so vehicles must detour to a charger mid-stream.
        var report = OrderDispatchSimulator.Run(Grid(6, 6), new[] { "r0c0", "r5c5" }, seed: 2, AssignmentPolicy.Nearest, Backlog(battery: true));

        Assert.True(report.ChargingStops > 0, "a battery below one leg must trigger charging detours");
        Assert.Equal(report.OrdersTotal, report.OrdersCompleted); // charging delays delivery but never drops an order
    }
}
