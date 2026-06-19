using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using SwarmRoute.Coordination.Application;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Host;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Map.Infra.CrossCutting.IoC;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.Simulation.Application;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;
using DeadlockBootStrapper = SwarmRoute.Liveness.Infra.CrossCutting.IoC.DeadlockNativeInjectorBootStrapper;

// ─────────────────────────────────────────────────────────────────────────────
// SwarmRoute composition root (single deployable). Wires the four bounded contexts
// into one running system, in grukirbs order (architecture-design.md §7).
//
// Wiring order (ORDER MATTERS):
//   1. AddEventBus(builder.Configuration)         — IDomainEventDispatcher + in-process or CAP/Rabbit publisher
//   2. MapNativeInjectorBootStrapper              — topology, RoadmapGraph read seam (DbContext: MapDatabase)
//   3. PathPlanningNativeInjectorBootStrapper     — IPathPlanner + NullReservationQuery (no DB)
//   4. TrafficControlNativeInjectorBootStrapper   — AFTER Planning so ReservationService overrides
//                                                   IReservationQuery (DbContext: TrafficControlDatabase)
//   5. DeadlockNativeInjectorBootStrapper         — the RAG cycle detector (post-hoc detection role; no DB)
//   6. Host adapters (override defaults)          — IResourceTopology → Map (topology must be registered for
//                                                   ReservationTable to pick it up)
//   7. AddCoordination() + FleetCoordinationLoop  — the lifelong RHCR driver
//   8. Controllers from Map.Api + TrafficControl.Api
//
// Only Map + TrafficControl register a DbContext, and both guard on a present connection string, so
// `dotnet build` and a no-DB smoke (container build) never connect at startup.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// 8a. MVC controllers — include the Map + TrafficControl Api assemblies as application parts so their
// controllers are discovered (they live outside the Host entry assembly).
builder.Services
    .AddControllers()
    // Bind/emit enums by name (e.g. SimulationRequest.Planner = "Sipp" | "Dijkstra"), not by ordinal.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .AddApplicationPart(typeof(SwarmRoute.Map.Api.Controllers.MapsController).Assembly)
    .AddApplicationPart(typeof(SwarmRoute.TrafficControl.Api.Controllers.TrafficController).Assembly);

builder.Services.AddOpenApi();

// Liveness/readiness endpoint (DoD §8 observability) + the coordination-loop status check.
builder.Services.AddHealthChecks()
    .AddCheck<SwarmRoute.Host.CoordinationHealthCheck>("coordination", tags: ["live"]);

// OpenTelemetry metrics → Prometheus scrape endpoint (/metrics). Subscribes to the fleet meter
// (SwarmRouteMetrics: planning latency, reservation grants/denials/releases, deadlock detect/resolve — all
// emitted by the live coordination loop) plus ASP.NET Core + .NET runtime instrumentation.
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(SwarmRouteMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

// 1. Event bus (in-memory for dev/tests; CAP PostgreSQL outbox + RabbitMQ when EventBus:UseInMemory=false).
builder.Services.AddEventBus(builder.Configuration);

// 2–5. Context composition roots, in dependency order.
MapNativeInjectorBootStrapper.RegisterServices(builder);

// Staged rollout: which planner the autonomous host loop uses. Defaults to the v0 Dijkstra baseline; flip to
// SIPP at the final stage via configuration ("Planning:DefaultPlanner": "Sipp"). Pre-registered so PathPlanning's
// TryAddSingleton<PlannerOptions> defers to it. (The Simulation A/B path overrides this per request.)
builder.Services.AddSingleton(new PlannerOptions
{
    Default = builder.Configuration.GetValue("Planning:DefaultPlanner", PlannerKind.Dijkstra)
});
PathPlanningNativeInjectorBootStrapper.RegisterServices(builder);
TrafficControlNativeInjectorBootStrapper.RegisterServices(builder); // after Planning → IReservationQuery override wins
DeadlockBootStrapper.RegisterServices(builder);

// 6. Host adapters — bridge the contexts the contracts deliberately left open.
//    Coordination's goal source is the single source of "which roadmap is active"; register it up-front so the
//    Map-backed adapters (topology / avoidance) can resolve the current roadmap.
builder.Services.TryAddSingleton<InMemoryCoordinationGoalSource>();
builder.Services.TryAddSingleton<ICoordinationGoalSource>(sp =>
    sp.GetRequiredService<InMemoryCoordinationGoalSource>());

//    6a. IResourceTopology → Map-backed (interference + parent-block closure, blacklist). Registered BEFORE
//        nothing else needs it at resolve time, but the singleton ReservationTable captures it lazily; a plain
//        AddSingleton replaces TrafficControl's IResourceTopology.Empty (last registration wins).
builder.Services.AddSingleton<IResourceTopology, MapResourceTopologyAdapter>();

// 7. Coordination cycle + hosted watchdog loop.
builder.Services.AddCoordination(registerHostedLoop: true);

// 7b. Simulation API — the Host supplies the per-request engine factory because it knows Infra bootstrappers
//     and registration order. The SimulationController lives in this Host assembly and is discovered by
//     AddControllers() automatically.
builder.Services.AddScoped<ISimulationEngineFactory, InMemorySimulationEngineFactory>();
builder.Services.AddSimulation();

// 7b'. Autonomous dispatcher demo fleet (Track B), opt-in via Dispatcher:Enabled. When on, it overrides the
//      DB-backed IRoadmapQueryService + inert goal source with an in-memory grid + dispatch-flow demo: orders
//      become goals for FleetCoordinationLoop, while demo pose advancement remains kinematic and does not treat
//      reservation grants as movement authority. Must run after the Map bootstrapper and the goal-source
//      registration above (it replaces both).
builder.AddSwarmRouteDispatcher();

// 7c. Runtime background jobs — enabled only when TrafficControlDatabase exists.
builder.Services.AddSwarmRouteBackgroundJobs(builder.Configuration);

var app = builder.Build();

await app.RunConfiguredMigrationsAsync();
app.RegisterSwarmRouteRecurringJobs();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();

// 8b. Map (api/maps) + TrafficControl (api/traffic) controllers.
app.MapControllers();
app.MapHealthChecks("/health");

// 8c. Observability surface (Track C): Prometheus scrape endpoint + a human/JSON status snapshot.
app.MapPrometheusScrapingEndpoint(); // GET /metrics
app.MapGet("/status", (IServiceProvider sp) => Results.Ok(SwarmRoute.Host.StatusSnapshot.Build(sp)));

app.Run();

// Exposed so a WebApplicationFactory smoke test can boot the real container without a live database.
public partial class Program;
