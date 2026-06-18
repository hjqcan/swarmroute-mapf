using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Coordination.Application.Dispatch;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;

namespace SwarmRoute.Host;

/// <summary>
/// Builds the <c>GET /status</c> snapshot (Track C): the active roadmap + planner mode, how many goals the
/// lifelong loop is driving, the reservation-table lease count, and — when the autonomous dispatcher fleet is
/// enabled — the order-book counts and per-vehicle poses. Everything is resolved best-effort so the endpoint
/// works in every wiring (DB-backed or in-memory, dispatcher on or off).
/// </summary>
public static class StatusSnapshot
{
    public static object Build(IServiceProvider sp)
    {
        var goals = sp.GetService<ICoordinationGoalSource>();
        var planner = sp.GetService<PlannerOptions>();
        var snapshot = sp.GetService<ITrafficControlSnapshotProvider>();
        var orders = sp.GetService<OrderBook>();
        var vehicles = sp.GetService<VehicleRegistry>();

        return new
        {
            planner = (planner?.Default)?.ToString() ?? "Dijkstra",
            activeRoadmap = goals?.CurrentRoadmapId,
            activeGoals = goals?.CurrentGoals.Count ?? 0,
            reservationLeases = TryLeaseCount(snapshot),
            dispatcher = orders is null
                ? null
                : new
                {
                    orders = ToOrderCounts(orders),
                    vehicles = vehicles?.Snapshot()
                        .Select(v => new { v.Id, site = v.SiteId, order = v.OrderId, goal = v.GoalSiteId })
                        .ToList(),
                },
        };
    }

    private static object ToOrderCounts(OrderBook orders)
    {
        var (pending, assigned, completed) = orders.Counts();
        return new { pending, assigned, completed, completedTotal = orders.CompletedTotal };
    }

    private static int? TryLeaseCount(ITrafficControlSnapshotProvider? snapshot)
    {
        try { return snapshot?.GetSnapshot().Owns.Count; }
        catch { return null; }
    }
}

/// <summary>
/// Liveness check for the lifelong coordination loop: the host is up and the goal source is responsive. Reports
/// the active-goal / pending-order / lease counts as health data so a probe surfaces what the fleet is doing.
/// </summary>
public sealed class CoordinationHealthCheck(
    ICoordinationGoalSource goals,
    IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["activeRoadmap"] = goals.CurrentRoadmapId?.ToString() ?? "(idle)",
            ["activeGoals"] = goals.CurrentGoals.Count,
        };
        if (services.GetService(typeof(OrderBook)) is OrderBook orders)
        {
            var (pending, assigned, completed) = orders.Counts();
            data["ordersPending"] = pending;
            data["ordersAssigned"] = assigned;
            data["ordersCompleted"] = completed;
            data["ordersCompletedTotal"] = orders.CompletedTotal;
        }

        return Task.FromResult(HealthCheckResult.Healthy("Coordination loop is live.", data));
    }
}

/// <summary>
/// Registers observable gauges for the autonomous dispatcher on the fleet meter (<see cref="SwarmRouteMetrics"/>),
/// so the Prometheus exporter exposes live order/vehicle/goal counts. Hosted so the gauges live for the app's
/// lifetime; only wired when the dispatcher fleet is enabled.
/// </summary>
public sealed class DispatcherMetrics : IHostedService, IDisposable
{
    private readonly Meter _meter = new(SwarmRouteMetrics.MeterName);

    public DispatcherMetrics(OrderBook orders, VehicleRegistry vehicles, ICoordinationGoalSource goals)
    {
        _meter.CreateObservableGauge("swarmroute_orders_pending", () => orders.Counts().Pending,
            description: "Transport orders waiting for a vehicle.");
        _meter.CreateObservableCounter("swarmroute_orders_completed_total", () => orders.CompletedTotal,
            description: "Transport orders completed since this order book started.");
        _meter.CreateObservableGauge("swarmroute_vehicles_busy", () => vehicles.Snapshot().Count(v => v.OrderId is not null),
            description: "Vehicles currently fulfilling an order.");
        _meter.CreateObservableGauge("swarmroute_coordination_active_goals", () => goals.CurrentGoals.Count,
            description: "Goals the lifelong coordination loop is planning/reserving.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();
}
