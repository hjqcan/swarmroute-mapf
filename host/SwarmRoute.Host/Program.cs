using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.EventBus.Extensions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Map.Infra.CrossCutting.IoC;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.Simulation.Application;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;
using DeadlockBootStrapper = SwarmRoute.Deadlock.Infra.CrossCutting.IoC.DeadlockNativeInjectorBootStrapper;

// ─────────────────────────────────────────────────────────────────────────────
// SwarmRoute composition root (single deployable). Wires the four bounded contexts
// into one running system, in grukirbs order (architecture-design.md §7).
//
// Wiring order (ORDER MATTERS):
//   1. AddEventBus()                              — IDomainEventDispatcher + in-process integration publisher
//   2. MapNativeInjectorBootStrapper              — topology, RoadmapGraph read seam (DbContext: MapDatabase)
//   3. PathPlanningNativeInjectorBootStrapper     — IPathPlanner + NullReservationQuery (no DB)
//   4. TrafficControlNativeInjectorBootStrapper   — AFTER Planning so ReservationService overrides
//                                                   IReservationQuery (DbContext: TrafficControlDatabase)
//   5. DeadlockNativeInjectorBootStrapper         — detector/resolver + TryAdd null seams (no DB)
//   6. Host adapters (override defaults)          — Deadlock snapshot → Traffic; IResourceTopology → Map;
//                                                   Deadlock avoid/detour seams (registered AFTER step 5,
//                                                   but seams use TryAdd so the Host wins; topology must be
//                                                   registered for ReservationTable to pick it up)
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
    .AddApplicationPart(typeof(SwarmRoute.Map.Api.Controllers.MapsController).Assembly)
    .AddApplicationPart(typeof(SwarmRoute.TrafficControl.Api.Controllers.TrafficController).Assembly);

builder.Services.AddOpenApi();

// 1. Event bus (in-memory dispatch for dev; CAP + RabbitMQ outbox lands in WS-X).
builder.Services.AddEventBus();

// 2–5. Context composition roots, in dependency order.
MapNativeInjectorBootStrapper.RegisterServices(builder);
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

//    6b. Deadlock seams (Null* were registered via TryAdd by the bootstrapper, so these explicit ones win).
builder.Services.AddScoped<IDeadlockSnapshotProvider, TrafficSnapshotDeadlockAdapter>();
builder.Services.AddScoped<IDetourReservationService, TrafficDetourReservationAdapter>();
//        Avoidance-point selection: Map-backed selector wrapped in the anti-livelock "no repeat point" guard
//        (singleton history remembers the last point per victim across scans).
builder.Services.AddSingleton<AvoidancePointHistory>();
builder.Services.AddScoped<MapAvoidancePointSelector>();
builder.Services.AddScoped<IAvoidancePointSelector>(sp =>
    new AntiLivelockAvoidancePointSelector(
        sp.GetRequiredService<MapAvoidancePointSelector>(),
        sp.GetRequiredService<AvoidancePointHistory>()));
//        Clearance: confirm recovery by re-detecting over a fresh TrafficControl snapshot (replaces the
//        optimistic NullClearanceConfirmer registered by the bootstrapper).
builder.Services.AddScoped<IClearanceConfirmer, SnapshotClearanceConfirmer>();

// 7. Coordination cycle + hosted watchdog loop.
builder.Services.AddCoordination(registerHostedLoop: true);

// 7b. Simulation API — the Host supplies the per-request engine factory because it knows Infra bootstrappers
//     and registration order. The SimulationController lives in this Host assembly and is discovered by
//     AddControllers() automatically.
builder.Services.AddScoped<ISimulationEngineFactory, InMemorySimulationEngineFactory>();
builder.Services.AddSimulation();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();

// 8b. Map (api/maps) + TrafficControl (api/traffic) controllers.
app.MapControllers();

app.Run();

// Exposed so a WebApplicationFactory smoke test can boot the real container without a live database.
public partial class Program;
