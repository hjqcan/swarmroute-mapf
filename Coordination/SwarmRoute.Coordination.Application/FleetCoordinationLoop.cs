using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmRoute.Coordination.Application;

/// <summary>
/// Tuning knobs for <see cref="FleetCoordinationLoop"/>.
/// </summary>
public sealed class CoordinationLoopOptions
{
    /// <summary>The watchdog tick interval driving periodic re-planning. Defaults to 1s.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When false the background watchdog timer does not run (the loop is only driven on demand via
    /// <see cref="FleetCoordinationLoop.RunOnceAsync"/>). Useful for tests / hosts that drive the cycle
    /// explicitly. Defaults to true.
    /// </summary>
    public bool EnableWatchdog { get; set; } = true;

    /// <summary>
    /// The rolling-horizon (RHCR) window in fleet-clock milliseconds. Each cycle stamps every plan request with
    /// <c>HorizonEndMs = now + HorizonWindowMs</c>, so the planner only commits the next <c>HorizonWindowMs</c>
    /// ticks of each route and the agent re-plans the following window on arrival at the frontier. Defaults to
    /// <see cref="long.MaxValue"/> = unbounded (RHCR off = whole-path planning, byte-identical to v0/v1).
    /// </summary>
    public long HorizonWindowMs { get; set; } = long.MaxValue;

    /// <summary>
    /// Continuous-time (CCBS) mode for the cluster joint planner: when true, <c>CoordinationCycleService</c>'s
    /// <c>PlanClusterAsync</c> solves with continuous-time CBS (motion-aware interval constraints over a SIPPwRT low
    /// level) instead of discrete CBS, so a cluster solve under the continuous executor returns continuous-time paths.
    /// Set per-run by the engine factory for the SIPPwRT planner. Default false = discrete CBS, byte-identical.
    /// </summary>
    public bool Continuous { get; set; }
}

/// <summary>
/// The lifelong online-MAPF driver (OpenTCS "Dispatcher"): an <see cref="IHostedService"/> that ticks the
/// <see cref="IFleetCoordinationCycle"/> on a <see cref="PeriodicTimer"/> watchdog and on demand. Each tick it
/// reads the current goals from <see cref="ICoordinationGoalSource"/> and runs one cycle; when there are no
/// goals it is a safe no-op. Per architecture-design §7, deadlock handling is reactive (subscriber/job driven),
/// not polled in this inner loop.
/// </summary>
/// <remarks>
/// The cycle body is resolved from a DI scope per tick (the planner/traffic services are singletons, but the
/// scope keeps the loop honest if a scoped collaborator is introduced later). Exceptions in a tick are logged
/// and swallowed so one bad cycle never tears down the lifelong loop.
/// </remarks>
public sealed class FleetCoordinationLoop : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICoordinationGoalSource _goalSource;
    private readonly CoordinationLoopOptions _options;
    private readonly ILogger<FleetCoordinationLoop> _logger;

    public FleetCoordinationLoop(
        IServiceScopeFactory scopeFactory,
        ICoordinationGoalSource goalSource,
        IOptions<CoordinationLoopOptions> options,
        ILogger<FleetCoordinationLoop> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _goalSource = goalSource ?? throw new ArgumentNullException(nameof(goalSource));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableWatchdog)
        {
            _logger.LogInformation("FleetCoordinationLoop watchdog disabled; cycle is on-demand only.");
            return;
        }

        _logger.LogInformation(
            "FleetCoordinationLoop started; watchdog tick = {Interval}.", _options.TickInterval);

        using var timer = new PeriodicTimer(_options.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }

        _logger.LogInformation("FleetCoordinationLoop stopped.");
    }

    /// <summary>
    /// Runs exactly one coordination cycle for the current goal book. Safe no-op when idle (no roadmap /
    /// no goals). Returns the cycle report (or <see cref="CycleReport.Empty"/> when idle) so on-demand callers
    /// and tests can inspect the outcome. Never throws for cycle-internal failures — they are logged.
    /// </summary>
    public async Task<CycleReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var roadmapId = _goalSource.CurrentRoadmapId;
        var goals = _goalSource.CurrentGoals;
        if (roadmapId is null || goals.Count == 0)
            return CycleReport.Empty;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cycle = scope.ServiceProvider.GetRequiredService<IFleetCoordinationCycle>();
            return await cycle.RunCycleAsync(roadmapId.Value, goals, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordination cycle failed for roadmap {RoadmapId}.", roadmapId);
            return CycleReport.Empty;
        }
    }
}
