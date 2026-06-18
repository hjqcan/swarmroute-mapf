using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmRoute.Coordination.Application.Dispatch;

/// <summary>Tuning knobs for the autonomous <see cref="DispatcherHostedService"/>.</summary>
public sealed class DispatcherOptions
{
    /// <summary>How often the dispatcher advances the fleet and re-assigns idle vehicles. Defaults to 500ms.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Drives the autonomous <see cref="DispatcherService"/> on a <see cref="PeriodicTimer"/>: each tick it advances
/// demo poses one step and re-assigns idle vehicles. Registered only when the host has a dispatcher fleet wired
/// (see the Host composition root); the lifelong <see cref="FleetCoordinationLoop"/> independently plans and
/// reserves the goal book this service maintains, but this demo driver does not treat reservation grants as
/// movement authority. Exceptions in a tick are logged and swallowed so one bad tick never tears down the loop.
/// </summary>
public sealed class DispatcherHostedService : BackgroundService
{
    private readonly DispatcherService _dispatcher;
    private readonly DispatcherOptions _options;
    private readonly ILogger<DispatcherHostedService> _logger;

    public DispatcherHostedService(
        DispatcherService dispatcher,
        IOptions<DispatcherOptions> options,
        ILogger<DispatcherHostedService> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DispatcherHostedService started; tick = {Interval}.", _options.TickInterval);
        using var timer = new PeriodicTimer(_options.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _dispatcher.DispatchAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dispatcher tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }

        _logger.LogInformation("DispatcherHostedService stopped.");
    }
}
