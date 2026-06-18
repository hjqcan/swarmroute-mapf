using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Coordination.Application.Dispatch;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Simulation.Application;

namespace SwarmRoute.Host;

/// <summary>
/// Wires the autonomous dispatcher demo fleet (Track B). When <c>Dispatcher:Enabled=true</c> the host runs a
/// real lifelong loop with NO database: an in-memory grid roadmap, a seeded vehicle fleet, an order book fed by
/// <c>POST /api/orders</c>, the <see cref="DispatcherService"/> as the coordination goal source, and the
/// <see cref="DispatcherHostedService"/> advancing the fleet — while the already-registered
/// <see cref="FleetCoordinationLoop"/> continuously plans and reserves the goals it produces.
/// <para>
/// Disabled by default so a database-backed deployment keeps the production <c>IRoadmapQueryService</c> and the
/// inert in-memory goal source. This is a self-contained dev/demo composition, not a WMS.
/// </para>
/// </summary>
public static class DispatcherHostExtensions
{
    /// <summary>A stable id for the in-memory demo roadmap the dispatcher fleet operates on.</summary>
    public static readonly Guid DemoRoadmapId = new("d1500000-0000-0000-0000-000000000001");

    public static WebApplicationBuilder AddSwarmRouteDispatcher(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection("Dispatcher");
        if (!section.GetValue("Enabled", false))
            return builder;

        var width = section.GetValue("GridWidth", 8);
        var height = section.GetValue("GridHeight", 8);
        var vehicleCount = section.GetValue("VehicleCount", 4);
        var tickMs = section.GetValue("TickIntervalMs", 500);

        var field = new GridFieldFactory().BuildGrid(width, height);

        // The dispatcher + the coordination cycle both resolve the graph through IRoadmapQueryService; serve the
        // demo roadmap in-memory (replaces the Map context's DB-backed provider for this no-database demo).
        builder.Services.RemoveAll<IRoadmapQueryService>();
        builder.Services.AddSingleton<IRoadmapQueryService>(new InMemoryRoadmapQueryService(DemoRoadmapId, field.Graph));

        // Order book + a fleet seeded across distinct grid cells (row-major).
        builder.Services.AddSingleton<OrderBook>();
        var registry = new VehicleRegistry();
        for (var i = 0; i < Math.Min(vehicleCount, field.Sites.Count); i++)
            registry.Register($"agv-{i + 1}", field.Sites[i].Id);
        builder.Services.AddSingleton(registry);

        builder.Services.Configure<DispatcherOptions>(o => o.TickInterval = TimeSpan.FromMilliseconds(tickMs));

        // The dispatcher IS the coordination goal source — override the inert in-memory default.
        builder.Services.AddSingleton<DispatcherService>(sp => new DispatcherService(
            DemoRoadmapId,
            sp.GetRequiredService<IRoadmapQueryService>(),
            sp.GetRequiredService<OrderBook>(),
            sp.GetRequiredService<VehicleRegistry>(),
            sp.GetRequiredService<ILogger<DispatcherService>>()));
        builder.Services.RemoveAll<ICoordinationGoalSource>();
        builder.Services.AddSingleton<ICoordinationGoalSource>(sp => sp.GetRequiredService<DispatcherService>());

        builder.Services.AddHostedService<DispatcherHostedService>();

        // Live order/vehicle/goal gauges on the fleet meter for the Prometheus exporter (Track C).
        builder.Services.AddHostedService<DispatcherMetrics>();

        return builder;
    }
}
