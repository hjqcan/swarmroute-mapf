using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.Coordination.Application;

/// <summary>
/// Default <see cref="IFleetCoordinationCycle"/>. Wires the four contexts into one rolling-horizon cycle:
/// <list type="number">
///   <item><description>Map: <see cref="IRoadmapQueryService.GetGraphAsync"/> (cached, in-process).</description></item>
///   <item><description>TrafficControl: <see cref="IReservationQuery.GetView"/> for the current reservation view.</description></item>
///   <item><description>PathPlanning: <see cref="IPathPlanner.Plan"/> over the graph + view.</description></item>
///   <item><description>TrafficControl: <see cref="ITrafficCoordinatorAppService.TryReserveAsync"/> — on
///     <see cref="AllocationOutcome.Queued"/>/<see cref="AllocationOutcome.Blocked"/> the contended CP/Lane
///     resources are pruned (fed back as the planner's blacklist) and the agent is re-planned, bounded by
///     <see cref="MaxReplanAttempts"/>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Determinism / no livelock (R6, ADR-003).</b> Goals are processed in ascending
/// <see cref="AgentGoal.Priority"/> then ordinal agent id; each agent's reservation is committed before the
/// next agent plans (so the table serialises them the same way every run). Re-planning is bounded and strictly
/// shrinks the search space (the contended resources are blacklisted), so the inner loop always terminates:
/// it either finds a route that avoids the contention, or planning fails (no route) and the agent is reported
/// contended for this cycle — to be retried next tick once a holder releases.</para>
/// <para><b>v0 note.</b> The <see cref="DijkstraPathPlanner"/> does not yet consult safe intervals in the
/// reservation view, so immediate retry avoidance is expressed via the planner's CP/Lane blacklist. When the
/// SIPP planner lands at v1 it will additionally route around the view in time; this loop body does not
/// change.</para>
/// </remarks>
public sealed class CoordinationCycleService : IFleetCoordinationCycle
{
    /// <summary>Maximum plan→reserve attempts per agent per cycle (1 initial + up to this-1 prune-and-replans).</summary>
    public const int MaxReplanAttempts = 8;

    private readonly IRoadmapQueryService _roadmaps;
    private readonly IReservationQuery _reservations;
    private readonly IPathPlanner _planner;
    private readonly ITrafficCoordinatorAppService _traffic;
    private readonly IFleetClock _clock;
    private readonly ILogger<CoordinationCycleService> _logger;

    /// <summary>The rolling-horizon (RHCR) window in fleet-clock ms; <see cref="long.MaxValue"/> = unbounded.</summary>
    private readonly long _horizonWindowMs;

    public CoordinationCycleService(
        IRoadmapQueryService roadmaps,
        IReservationQuery reservations,
        IPathPlanner planner,
        ITrafficCoordinatorAppService traffic,
        IFleetClock clock,
        ILogger<CoordinationCycleService> logger,
        IOptions<CoordinationLoopOptions> loopOptions)
    {
        _roadmaps = roadmaps ?? throw new ArgumentNullException(nameof(roadmaps));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _horizonWindowMs = (loopOptions ?? throw new ArgumentNullException(nameof(loopOptions))).Value.HorizonWindowMs;
    }

    /// <summary>The rolling-horizon arrival ceiling for a cycle releasing at <paramref name="releaseTimeMs"/>:
    /// <c>release + window</c>, saturating to <see cref="long.MaxValue"/> (= unbounded) on overflow or when the
    /// window is unbounded.</summary>
    private long HorizonEndFor(long releaseTimeMs)
        => _horizonWindowMs == long.MaxValue || releaseTimeMs > long.MaxValue - _horizonWindowMs
            ? long.MaxValue
            : releaseTimeMs + _horizonWindowMs;

    /// <inheritdoc />
    public async Task<CycleReport> RunCycleAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> goals,
        IReadOnlySet<ResourceRef>? blockedResources = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);
        if (goals.Count == 0)
            return CycleReport.Empty;

        // Map: the roadmap graph for this horizon (cached, in-process).
        var graph = await _roadmaps.GetGraphAsync(roadmapId, cancellationToken).ConfigureAwait(false);

        // One rolling-horizon timestamp per cycle; every planned interval is expressed on this fleet clock.
        var cycleReleaseTimeMs = _clock.NowMs;

        // Deterministic priority order: lower Priority first, then ordinal agent id.
        var ordered = goals
            .OrderBy(g => g.Priority)
            .ThenBy(g => g.AgentId, StringComparer.Ordinal)
            .ToList();

        var results = new List<AgentCycleResult>(ordered.Count);
        foreach (var goal in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await PlanAndReserveAsync(roadmapId, graph, goal, cycleReleaseTimeMs, blockedResources, cancellationToken)
                .ConfigureAwait(false));
        }

        return new CycleReport(results);
    }

    private async Task<AgentCycleResult> PlanAndReserveAsync(
        Guid roadmapId,
        RoadmapGraph graph,
        AgentGoal goal,
        long releaseTimeMs,
        IReadOnlySet<ResourceRef>? blockedResources,
        CancellationToken cancellationToken)
    {
        // TrafficControl: current reservation view (re-read each attempt so the planner sees the latest state).
        // Seed the prune set with the statically-blocked resources (parked vehicles) so every plan this cycle
        // routes around them — the agent's own start/goal are never blocked (the planner skips those).
        var pruned = blockedResources is { Count: > 0 }
            ? new HashSet<ResourceRef>(blockedResources)
            : new HashSet<ResourceRef>();

        SpaceTimePath? lastPath = null;
        string? lastFailure = null;
        AllocationOutcome? lastOutcome = null;

        for (var attempt = 1; attempt <= MaxReplanAttempts; attempt++)
        {
            var view = _reservations.GetView(roadmapId);
            var request = new PlanRequest(
                roadmapId,
                goal.AgentId,
                goal.FromSiteId,
                goal.ToSiteId,
                releaseTimeMs: releaseTimeMs,
                blacklistedResources: pruned.Count == 0 ? null : pruned,
                horizonEndMs: HorizonEndFor(releaseTimeMs));

            var plan = _planner.Plan(graph, request, view);
            if (!plan.Success || plan.Path is null)
            {
                // No route (possibly because every alternative was pruned) — report contended/unplannable.
                lastFailure = plan.FailureReason ?? "Planning failed.";
                _logger.LogDebug(
                    "Cycle agent={AgentId} attempt={Attempt} plan failed: {Reason}",
                    goal.AgentId, attempt, lastFailure);
                return new AgentCycleResult(
                    goal.AgentId,
                    Planned: lastPath is not null,
                    Reserved: false,
                    Outcome: lastOutcome,
                    Attempts: attempt,
                    Path: lastPath,
                    FailureReason: lastFailure);
            }

            lastPath = plan.Path;

            // TrafficControl: try to take right-of-way for the whole path.
            var outcome = await _traffic.TryReserveAsync(plan.Path, goal.AgentId, cancellationToken)
                .ConfigureAwait(false);
            lastOutcome = outcome;
            if (outcome == AllocationOutcome.Granted)
            {
                _logger.LogDebug(
                    "Cycle agent={AgentId} attempt={Attempt} reserved ({Cells} cells).",
                    goal.AgentId, attempt, plan.Path.Cells.Count);
                return new AgentCycleResult(
                    goal.AgentId,
                    Planned: true,
                    Reserved: true,
                    Outcome: outcome,
                    Attempts: attempt,
                    Path: plan.Path,
                    FailureReason: null);
            }

            // Denied/Queued/Blocked: prune only the concrete resources that are actually blocking this path.
            var before = pruned.Count;
            foreach (var resource in _traffic.BlockedResources(plan.Path, goal.AgentId))
            {
                // Never prune the agent's own start/goal — that would make the goal unreachable by construction.
                if (resource.Kind == ResourceKind.CP
                    && (string.Equals(resource.Id, goal.FromSiteId, StringComparison.Ordinal) ||
                        string.Equals(resource.Id, goal.ToSiteId, StringComparison.Ordinal)))
                    continue;
                pruned.Add(resource);
            }

            _logger.LogDebug(
                "Cycle agent={AgentId} attempt={Attempt} -> {Outcome}; pruned {PrunedCount} resource(s), replanning.",
                goal.AgentId, attempt, outcome, pruned.Count);

            // If we couldn't add anything new to prune, replanning would be a no-op → stop now (contended).
            if (pruned.Count == before)
                break;
        }

        // Exhausted retries (or nothing left to prune): contended for this cycle, retried next tick.
        return new AgentCycleResult(
            goal.AgentId,
            Planned: lastPath is not null,
            Reserved: false,
            Outcome: lastOutcome,
            Attempts: MaxReplanAttempts,
            Path: lastPath,
            FailureReason: lastFailure ?? $"Could not obtain right-of-way ({lastOutcome}).");
    }

    /// <inheritdoc />
    public Task ReleaseAsync(
        string agentId,
        IReadOnlyList<ResourceRef> passedResources,
        CancellationToken cancellationToken = default)
        => _traffic.ReleaseAsync(agentId, passedResources, cancellationToken);
}
