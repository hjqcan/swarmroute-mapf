using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Coordination.Application.Dispatch;
using SwarmRoute.Host.Controllers;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Track B — the autonomous dispatcher skeleton: orders flow in, get assigned to the nearest idle vehicle,
/// demo vehicle poses advance and complete, and the produced goal book is exactly what the real coordination
/// cycle plans + reserves. The demo pose driver is not a reservation-authoritative executor.
/// </summary>
public sealed class DispatcherTests
{
    private static readonly Guid Roadmap = Guid.NewGuid();

    private static (DispatcherService Dispatcher, OrderBook Orders, VehicleRegistry Registry) Build(
        RoadmapGraph graph, params (string Id, string Site)[] vehicles)
    {
        var orders = new OrderBook();
        var registry = new VehicleRegistry();
        foreach (var (id, site) in vehicles)
            registry.Register(id, site);
        var dispatcher = new DispatcherService(
            Roadmap, new FakeRoadmapQueryService(Roadmap, graph), orders, registry,
            NullLogger<DispatcherService>.Instance);
        return (dispatcher, orders, registry);
    }

    private static (OrdersController Controller, OrderBook Orders) BuildController(
        RoadmapGraph graph, Guid? graphRoadmapId = null)
    {
        var orders = new OrderBook();
        var registry = new VehicleRegistry();
        registry.Register("agv-1", "A");

        var services = new ServiceCollection();
        services.AddSingleton(orders);
        services.AddSingleton(new DispatcherService(
            Roadmap,
            new FakeRoadmapQueryService(graphRoadmapId ?? Roadmap, graph),
            orders,
            registry,
            NullLogger<DispatcherService>.Instance));

        return (new OrdersController(services.BuildServiceProvider()), orders);
    }

    [Fact]
    public async Task Order_is_assigned_and_drives_the_goal_book()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B", "C", "D");
        var (dispatcher, orders, registry) = Build(graph, ("agv-1", "A"));
        orders.Submit(new TransportOrder("ord-1", "D"));

        var goals = await dispatcher.DispatchAsync(); // tick 1: assign (movement begins next tick)

        var goal = Assert.Single(goals);
        Assert.Equal("agv-1", goal.AgentId);
        Assert.Equal("A", goal.FromSiteId);             // goal is planned from the vehicle's current pose
        Assert.Equal("D", goal.ToSiteId);
        Assert.Equal("A", registry.Snapshot().Single().SiteId);
        Assert.Single(dispatcher.CurrentGoals);         // CurrentGoals reflects the in-flight assignment

        await dispatcher.DispatchAsync();               // tick 2: advance one CP toward D
        Assert.Equal("B", registry.Snapshot().Single().SiteId);
    }

    [Fact]
    public void Order_ids_are_unique_across_pending_assigned_and_completed_orders()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B");
        var (_, orders, _) = Build(graph, ("agv-1", "A"));

        orders.Submit(new TransportOrder("ord-1", "B"));
        Assert.Throws<DuplicateOrderIdException>(() => orders.Submit(new TransportOrder("ord-1", "B")));

        Assert.True(orders.TryTake("ord-1"));
        Assert.Throws<DuplicateOrderIdException>(() => orders.Submit(new TransportOrder("ord-1", "B")));

        orders.Complete("ord-1");
        Assert.Throws<DuplicateOrderIdException>(() => orders.Submit(new TransportOrder("ord-1", "B")));
    }

    [Fact]
    public void Generated_order_ids_skip_client_supplied_ids()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B");
        var (_, orders, _) = Build(graph, ("agv-1", "A"));

        orders.Submit(new TransportOrder("ord-1", "B"));
        var generated = orders.Submit(new TransportOrder(string.Empty, "B"));

        Assert.Equal("ord-2", generated.Id);
    }

    [Fact]
    public void Pending_orders_with_equal_priority_follow_arrival_order()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B");
        var (_, orders, _) = Build(graph, ("agv-1", "A"));

        orders.Submit(new TransportOrder("ord-10", "B"));
        orders.Submit(new TransportOrder("ord-2", "B"));

        Assert.Equal(new[] { "ord-10", "ord-2" }, orders.Pending().Select(o => o.Id));
    }

    [Fact]
    public async Task Destination_validation_reports_missing_site_and_missing_graph()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B");
        var (dispatcher, _, _) = Build(graph, ("agv-1", "A"));

        Assert.True(await dispatcher.DestinationExistsAsync("B"));
        Assert.False(await dispatcher.DestinationExistsAsync("Z"));

        var orders = new OrderBook();
        var registry = new VehicleRegistry();
        registry.Register("agv-1", "A");
        var missingGraphDispatcher = new DispatcherService(
            Roadmap,
            new FakeRoadmapQueryService(Guid.NewGuid(), graph),
            orders,
            registry,
            NullLogger<DispatcherService>.Instance);

        Assert.Null(await missingGraphDispatcher.DestinationExistsAsync("B"));
    }

    [Fact]
    public async Task Orders_controller_rejects_destination_outside_active_roadmap()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B");
        var (controller, orders) = BuildController(graph);

        var result = await controller.Submit(new OrdersController.OrderRequest("Z"), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal((0, 0, 0), orders.Counts());
    }

    [Fact]
    public async Task Orders_controller_returns_503_when_dispatcher_graph_is_unavailable()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B");
        var (controller, orders) = BuildController(graph, Guid.NewGuid());

        var result = await controller.Submit(new OrdersController.OrderRequest("B"), CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal((0, 0, 0), orders.Counts());
    }

    [Fact]
    public async Task Orders_controller_rejects_duplicate_order_id()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B");
        var (controller, orders) = BuildController(graph);

        var first = await controller.Submit(new OrdersController.OrderRequest("B", Id: "ord-1"), CancellationToken.None);
        var second = await controller.Submit(new OrdersController.OrderRequest("B", Id: "ord-1"), CancellationToken.None);

        Assert.IsType<AcceptedResult>(first);
        var conflict = Assert.IsType<ConflictObjectResult>(second);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal((1, 0, 0), orders.Counts());
    }

    [Fact]
    public async Task Vehicle_reaches_destination_completes_order_and_goes_idle()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B", "C");
        var (dispatcher, orders, registry) = Build(graph, ("agv-1", "A"));
        orders.Submit(new TransportOrder("ord-1", "C"));

        for (var i = 0; i < 4; i++)
            await dispatcher.DispatchAsync();

        var vehicle = registry.Snapshot().Single();
        Assert.Equal("C", vehicle.SiteId);
        Assert.Null(vehicle.OrderId); // idle again
        Assert.Empty(dispatcher.CurrentGoals);
        Assert.Equal((0, 0, 1), orders.Counts());
    }

    [Fact]
    public async Task Nearest_idle_vehicle_takes_the_order()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B", "C", "D", "E");
        var (dispatcher, orders, registry) = Build(graph, ("agv-far", "A"), ("agv-near", "D"));
        orders.Submit(new TransportOrder("ord-1", "E")); // E is one hop from D, four from A

        await dispatcher.DispatchAsync();

        Assert.Equal("ord-1", registry.Snapshot().Single(v => v.Id == "agv-near").OrderId);
        Assert.Null(registry.Snapshot().Single(v => v.Id == "agv-far").OrderId);
    }

    [Fact]
    public async Task Two_vehicles_never_occupy_the_same_cell()
    {
        // Both head down the same chain toward E; the one-vehicle-per-CP gate must keep their poses distinct.
        var graph = FakeRoadmapQueryService.Chain("A", "B", "C", "D", "E");
        var (dispatcher, orders, registry) = Build(graph, ("agv-1", "A"), ("agv-2", "B"));
        orders.Submit(new TransportOrder("o1", "E"));
        orders.Submit(new TransportOrder("o2", "D"));

        for (var tick = 0; tick < 12; tick++)
        {
            await dispatcher.DispatchAsync();
            var poses = registry.Snapshot().Select(v => v.SiteId).ToList();
            Assert.Equal(poses.Count, poses.Distinct().Count());
        }
    }

    [Fact]
    public async Task Coordination_cycle_plans_and_reserves_the_dispatcher_goal_book()
    {
        var graph = FakeRoadmapQueryService.Chain("A", "B", "C", "D");
        var (dispatcher, orders, _) = Build(graph, ("agv-1", "A"));
        orders.Submit(new TransportOrder("ord-1", "D"));
        await dispatcher.DispatchAsync(); // produces a goal (agv-1: B -> D)

        using var host = CoordinationTestHost.Build(graph);
        var report = await host.Cycle.RunCycleAsync(host.RoadmapId, dispatcher.CurrentGoals.ToList());

        Assert.Contains("agv-1", report.ReservedAgentIds);
    }
}
