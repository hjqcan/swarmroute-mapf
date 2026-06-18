using SwarmRoute.EventBus.Extensions;

// Composition root (single deployable). v0 shell — squads (WS3) flesh out the wiring.
//
// Target wiring order (mirrors grukirbs Host, per architecture-design.md §7):
//   1. builder.Services.AddEventBus();                        // in-memory CAP for dev; PG outbox + RabbitMQ in prod
//   2. each context's *NativeInjectorBootStrapper.RegisterServices(builder);   // Map, PathPlanning, TrafficControl, Deadlock
//   3. Hangfire (background jobs)
//   4. builder.Services.AddHostedService<FleetCoordinationLoop>();             // Coordination.Application
//
// Only Map + TrafficControl register a DbContext.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// In-process domain-event dispatch is available today; CAP/RabbitMQ wiring is a TODO in
// SwarmRoute.EventBus (see EventBusServiceCollectionExtensions).
builder.Services.AddEventBus();

// TODO (WS3): call each context's RegisterServices(...) and AddHostedService<FleetCoordinationLoop>().

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
