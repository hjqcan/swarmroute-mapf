using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using SwarmRoute.Map.Infra.Data.Context;
using SwarmRoute.TrafficControl.Infra.BackgroundJobs;
using SwarmRoute.TrafficControl.Infra.Data.Context;

namespace SwarmRoute.Host;

internal static class SwarmRouteHostStartupExtensions
{
    private const string TrafficControlConnectionStringName = "TrafficControlDatabase";
    private const string LeaseExpirySweepJobId = "traffic-control:lease-expiry-sweep";
    private const string StaleRequestEscalationJobId = "traffic-control:stale-request-escalation";

    public static IServiceCollection AddSwarmRouteBackgroundJobs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(TrafficControlConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            return services;

        services.AddHangfire(config =>
            config.UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
        services.AddHangfireServer();

        return services;
    }

    public static async Task RunConfiguredMigrationsAsync(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration["RunMigrationsOnStartup"], defaultValue: false))
            return;

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MapDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<TrafficControlDbContext>().Database.MigrateAsync();
    }

    public static void RegisterSwarmRouteRecurringJobs(this WebApplication app)
    {
        if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString(TrafficControlConnectionStringName)))
            return;

        var manager = app.Services.GetRequiredService<IRecurringJobManager>();

        var leaseSweep = ReadSchedule(app.Configuration, "LeaseExpirySweep");
        if (leaseSweep.Enabled)
            manager.AddOrUpdate<LeaseExpirySweepJob>(
                LeaseExpirySweepJobId,
                job => job.Sweep(),
                leaseSweep.CronExpression);
        else
            manager.RemoveIfExists(LeaseExpirySweepJobId);

        var staleEscalation = ReadSchedule(app.Configuration, "StaleRequestEscalation");
        if (staleEscalation.Enabled)
            manager.AddOrUpdate<StaleRequestEscalationJob>(
                StaleRequestEscalationJobId,
                job => job.RunAsync(CancellationToken.None),
                staleEscalation.CronExpression);
        else
            manager.RemoveIfExists(StaleRequestEscalationJobId);
    }

    private static (bool Enabled, string CronExpression) ReadSchedule(IConfiguration configuration, string sectionName)
    {
        var prefix = $"BackgroundJobs:{sectionName}";
        return (
            IsEnabled(configuration[$"{prefix}:Enabled"], defaultValue: true),
            string.IsNullOrWhiteSpace(configuration[$"{prefix}:CronExpression"])
                ? Cron.Minutely()
                : configuration[$"{prefix}:CronExpression"]!);
    }

    private static bool IsEnabled(string? configured, bool defaultValue)
        => string.IsNullOrWhiteSpace(configured) ? defaultValue : bool.Parse(configured);
}
