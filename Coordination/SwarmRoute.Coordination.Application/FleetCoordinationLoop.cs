using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmRoute.Liveness.Application.Contract.Policy;

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

    /// <summary>
    /// The joint resolver the autonomous loop applies to a physical standoff left contended after a cycle
    /// (<see cref="JointResolverKind.None"/> = none, the loop just retries the contended agents next tick;
    /// <see cref="JointResolverKind.Cbs"/> = solve each mutually-blocking cluster jointly via CBS/CCBS and reserve
    /// atomically; <see cref="JointResolverKind.Pibt"/> = grant each cluster's next joint single-hop atomically
    /// through the reservation table). Default <see cref="JointResolverKind.None"/> = byte-identical to the plain
    /// plan+reserve loop (the post-cycle standoff pass is skipped entirely).
    /// </summary>
    public JointResolverKind JointResolver { get; set; } = JointResolverKind.None;
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

    /// <summary>
    /// The inert dock-admission fallback bound when no FMS <see cref="IDockAdmissionController"/> is registered.
    /// Stateless and thread-safe, so a single shared instance is sufficient — it admits every goal unchanged and
    /// blocks nothing, keeping the cycle byte-identical to its pre-FMS behaviour.
    /// </summary>
    private static readonly IDockAdmissionController PassThroughDockAdmission =
        new PassThroughDockAdmissionController();

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

            // (FMS-V1 R2) Dock-admission gate. Filter this cycle's goals through the FMS controller before planning:
            // a goal denied dock admission is rewritten to hold its vehicle at the station's pre-dock buffer, and the
            // denied stations' blocking closures are reported so the planner routes the rest of the fleet around the
            // contended docks (ADR-F3). The result feeds the loop's existing blockedResources path. Opt-in /
            // byte-identical: when no FMS implementation is registered we bind the inert PassThrough, which returns the
            // goals unchanged and an empty blocked set, so the cycle is identical to its pre-FMS behaviour.
            var admission = scope.ServiceProvider.GetService<IDockAdmissionController>()
                ?? PassThroughDockAdmission;
            var (admittedGoals, blockedResources) = await EvaluateAdmissionAsync(admission, roadmapId.Value, goals, cancellationToken)
                .ConfigureAwait(false);

            var report = await cycle.RunCycleAsync(roadmapId.Value, admittedGoals, blockedResources, cancellationToken).ConfigureAwait(false);

            // (v3) Joint standoff resolution. Hand the agents left contended after the per-agent cycle to the
            // configured joint resolver: CBS/CCBS solves each mutually-blocking cluster jointly and reserves it
            // atomically; PIBT grants each cluster's next joint single-hop through the reservation table (the table
            // as the single authority). Opt-in — when JointResolver=None this whole pass is skipped and the loop is
            // byte-identical to the plain plan+reserve cycle.
            if (_options.JointResolver != JointResolverKind.None)
            {
                var contended = report.ContendedAgentIds.ToHashSet(StringComparer.Ordinal);
                if (contended.Count >= 2)
                {
                    // Cluster on the admitted goals (the goals the cycle actually planned), so a buffer-held goal is
                    // standoff-resolved toward its held destination, not its original dock. With PassThrough admission
                    // admittedGoals is the same instance as goals, so this is byte-identical.
                    var clusterGoals = admittedGoals.Where(g => contended.Contains(g.AgentId)).ToList();
                    // Carry each contended agent's actual attempted next cell (the reservation/blacklist-aware first hop
                    // of its planned path) from the cycle report into the resolver, so it clusters on what the agents
                    // really blocked on rather than a reservation-blind geometric hop.
                    var intendedNextCells = report.Results
                        .Where(r => contended.Contains(r.AgentId))
                        .ToDictionary(r => r.AgentId, r => r.IntendedNextCell, StringComparer.Ordinal);
                    var resolved = await cycle.ResolveStandoffsAsync(roadmapId.Value, clusterGoals, intendedNextCells: intendedNextCells, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (resolved.Results.Count > 0)
                        report = MergeResolved(report, resolved);
                }
            }

            return report;
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

    /// <summary>
    /// Runs the dock-admission pass and returns the (possibly buffer-rewritten) goals to plan plus the resources to
    /// treat as blocked this cycle. The pass-through controller returns the goals unchanged with an empty blocked set
    /// (byte-identical). A null verdict, or one that does not cover every input goal, degrades safely to the original
    /// goals with no blocked resources, so a misbehaving FMS controller can never strand the loop or drop an agent.
    /// </summary>
    private static async Task<(IReadOnlyCollection<AgentGoal> AdmittedGoals, IReadOnlySet<SwarmRoute.SpatioTemporal.Kernel.ResourceRef>? BlockedResources)> EvaluateAdmissionAsync(
        IDockAdmissionController admission,
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> goals,
        CancellationToken cancellationToken)
    {
        var result = await admission.EvaluateAdmissionAsync(roadmapId, goals, cancellationToken).ConfigureAwait(false);
        if (result is null || result.AdmittedGoals.Count != goals.Count)
            return (goals, null);

        var blocked = result.BlockedResources.Count > 0 ? result.BlockedResources : null;
        return (result.AdmittedGoals, blocked);
    }

    /// <summary>Overlays the joint-resolver outcomes onto the base cycle report, preserving its deterministic order.</summary>
    private static CycleReport MergeResolved(CycleReport baseReport, CycleReport resolved)
    {
        var overlay = resolved.Results.ToDictionary(r => r.AgentId, StringComparer.Ordinal);
        var merged = baseReport.Results
            .Select(r => overlay.TryGetValue(r.AgentId, out var o) ? o : r)
            .ToList();
        return new CycleReport(merged);
    }
}
