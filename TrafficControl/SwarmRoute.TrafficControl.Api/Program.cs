using SwarmRoute.EventBus.Extensions;
using SwarmRoute.TrafficControl.Infra.CrossCutting.IoC;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// In-process domain-event dispatch (CAP/RabbitMQ wiring is a documented TODO in the EventBus stub).
builder.Services.AddEventBus();

// TrafficControl bounded context: singleton in-memory ReservationTable, allocator/conflict services,
// reserve/release + snapshot seams, snapshot/audit DbContext and Hangfire jobs. This registration also
// overrides PathPlanning's IReservationQuery (NullReservationQuery) with the live ReservationService.
TrafficControlNativeInjectorBootStrapper.RegisterServices(builder);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
