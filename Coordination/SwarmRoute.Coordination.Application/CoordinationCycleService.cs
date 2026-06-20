using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Cbs;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Liveness.Domain.Detection;
using SwarmRoute.Liveness.Domain.Resolution;

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

    /// <summary>Local CBS solver for the joint-cluster path (v3): stateless, bounded by RHCR, its own SIPP low
    /// level. Used only by <see cref="PlanClusterAsync"/>, which the executor calls for a detected standoff —
    /// the per-agent <see cref="RunCycleAsync"/> path is untouched.</summary>
    private readonly CbsLocalSolver _cbs;

    /// <summary>The joint resolver the autonomous loop applies to a contended standoff via
    /// <see cref="ResolveStandoffsAsync"/> (<see cref="JointResolverKind.None"/> = the feature is off).</summary>
    private readonly JointResolverKind _jointResolver;

    /// <summary>The PIBT joint-step port: computes a cluster's next collision-free joint single hop, which is then
    /// committed atomically through the reservation table. Only used by the PIBT branch of <see cref="ResolveStandoffsAsync"/>.</summary>
    private readonly IJointStepPlanner _jointStep;

    public CoordinationCycleService(
        IRoadmapQueryService roadmaps,
        IReservationQuery reservations,
        IPathPlanner planner,
        ITrafficCoordinatorAppService traffic,
        IFleetClock clock,
        ILogger<CoordinationCycleService> logger,
        IOptions<CoordinationLoopOptions> loopOptions,
        IJointStepPlanner? jointStepPlanner = null)
    {
        _roadmaps = roadmaps ?? throw new ArgumentNullException(nameof(roadmaps));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jointStep = jointStepPlanner ?? new PibtJointStepPlanner();
        var options = (loopOptions ?? throw new ArgumentNullException(nameof(loopOptions))).Value;
        _horizonWindowMs = options.HorizonWindowMs;
        _jointResolver = options.JointResolver;
        // (v3) The cluster joint planner: discrete CBS by default, or continuous-time CCBS (motion-aware interval
        // constraints over a SIPPwRT low level) when the run is continuous — so a cluster solve under the continuous
        // executor returns continuous-time paths it can run. Default (discrete) is byte-identical.
        _cbs = options.Continuous
            ? new CbsLocalSolver(new CbsOptions(TimeHorizonTicks: _horizonWindowMs, Continuous: true), new SippwrtPathPlanner())
            : new CbsLocalSolver(new CbsOptions(TimeHorizonTicks: _horizonWindowMs));
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
                    FailureReason: lastFailure,
                    IntendedNextCell: FirstHopCp(lastPath, goal.FromSiteId));
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
                    FailureReason: null,
                    IntendedNextCell: FirstHopCp(plan.Path, goal.FromSiteId));
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
            FailureReason: lastFailure ?? $"Could not obtain right-of-way ({lastOutcome}).",
            IntendedNextCell: FirstHopCp(lastPath, goal.FromSiteId));
    }

    /// <summary>The first control point on <paramref name="path"/> that differs from <paramref name="fromSiteId"/> — the
    /// agent's actual attempted next cell (the planner produced it while routing around the live reservation view and
    /// the accumulated prune set, so it is reservation/blacklist-aware). Null when the path is null/empty or never
    /// leaves the start (already at goal / dwell-in-place).</summary>
    private static string? FirstHopCp(SpaceTimePath? path, string fromSiteId)
    {
        if (path is null)
            return null;
        foreach (var cell in path.Cells)
            if (cell.Resource.Kind == ResourceKind.CP
                && !string.Equals(cell.Resource.Id, fromSiteId, StringComparison.Ordinal))
                return cell.Resource.Id;
        return null;
    }

    /// <inheritdoc />
    public Task ReleaseAsync(
        string agentId,
        IReadOnlyList<ResourceRef> passedResources,
        CancellationToken cancellationToken = default)
        => _traffic.ReleaseAsync(agentId, passedResources, cancellationToken);

    /// <inheritdoc />
    public async Task<CycleReport> PlanClusterAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> cluster,
        IReadOnlySet<ResourceRef>? blockedResources = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (cluster.Count == 0)
            return CycleReport.Empty;

        // The cluster members have already released their stale reservations (the executor does that before
        // calling), so the view holds only the flowing fleet. Physical blockers without leases (parked/waiting
        // non-members) are carried separately as the same static blacklist RunCycleAsync uses.
        var graph = await _roadmaps.GetGraphAsync(roadmapId, cancellationToken).ConfigureAwait(false);
        var view = _reservations.GetView(roadmapId);
        var releaseTick = _clock.NowMs;

        var agents = cluster
            .Select(g => new CbsAgent(g.AgentId, g.FromSiteId, g.ToSiteId, g.Priority))
            .ToList();

        var solution = _cbs.Solve(graph, agents, view, releaseTick, blockedResources);
        if (!solution.Solved || solution.Paths is null)
        {
            _logger.LogDebug("Cluster CBS did not solve ({Status}): {Reason}", solution.Status, solution.FailureReason);
            return Unreserved(cluster, solution.Status.ToString());
        }

        // Commit the conflict-free paths atomically: reserve in ordinal order; on any non-grant, roll back every
        // grant so far and fall back (the cluster retries next tick). In practice all grant — the paths respect the
        // live view and are mutually conflict-free — but the reservation table stays the authority.
        var granted = new List<(string AgentId, SpaceTimePath Path)>(agents.Count);
        var results = new List<AgentCycleResult>(agents.Count);
        foreach (var (agentId, path) in solution.Paths
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                     .Select(kv => (kv.Key, kv.Value)))
        {
            var outcome = await _traffic.TryReserveAsync(path, agentId, cancellationToken).ConfigureAwait(false);
            if (outcome != AllocationOutcome.Granted)
            {
                foreach (var g in granted)
                    await _traffic.ReleaseAsync(g.AgentId, g.Path.Cells.Select(c => c.Resource).Distinct().ToList(), cancellationToken)
                        .ConfigureAwait(false);
                _logger.LogDebug("Cluster CBS reserve aborted at agent={AgentId} ({Outcome}); rolled back, falling back.", agentId, outcome);
                return Unreserved(cluster, $"reserve-{outcome}");
            }
            granted.Add((agentId, path));
            results.Add(new AgentCycleResult(agentId, Planned: true, Reserved: true, Outcome: outcome, Attempts: 1, Path: path, FailureReason: null));
        }

        return new CycleReport(results);
    }

    /// <summary>The reservation window (fleet-clock ms) a PIBT joint single hop holds — one tick in the discrete
    /// reservation convention; the half-open lease auto-expires once the host clock passes it.</summary>
    private const long JointStepWindowMs = 1;

    /// <inheritdoc />
    public async Task<CycleReport> ResolveStandoffsAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> contended,
        IReadOnlySet<ResourceRef>? blockedResources = null,
        IReadOnlyDictionary<string, string?>? intendedNextCells = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contended);
        if (_jointResolver == JointResolverKind.None || contended.Count < 2)
            return CycleReport.Empty;

        var graph = await _roadmaps.GetGraphAsync(roadmapId, cancellationToken).ConfigureAwait(false);

        // Form physical-standoff clusters among the contended agents: each sits at its current pose and intends its
        // next cell; the union-find links any agent whose intended cell another contended agent physically holds (a
        // head-on swap or a blocking chain). They are all already unreserved, so the trigger threshold is 1 — every
        // mutually-blocking component of size >= 2 is actionable.
        var occupantNow = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in contended)
            occupantNow[g.FromSiteId] = g.AgentId;

        var hopsCache = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> HopsTo(string goal) =>
            hopsCache.TryGetValue(goal, out var d) ? d : hopsCache[goal] = HopDistances.To(graph, goal);

        // Each agent's intended next cell is its ACTUAL attempted/failed next cell from the cycle report (the
        // reservation/blacklist-aware first hop of its last planned path) when the caller plumbed one — that is what it
        // was really blocked on in TrafficControl — falling back to a geometric next-hop toward the goal only when the
        // report did not carry it (e.g. a direct caller that has no report).
        string? IntendedCell(AgentGoal g) =>
            intendedNextCells is not null && intendedNextCells.TryGetValue(g.AgentId, out var cell) && cell is not null
                ? cell
                : NextHopToward(graph, g.FromSiteId, g.ToSiteId, HopsTo);

        var snapshots = contended
            .Select(g => new StuckAgentSnapshot(g.AgentId, IntendedCell(g), 1, true))
            .ToList();
        var clusters = StuckClusterDetector.Assemble(snapshots, occupantNow, triggerThreshold: 1);
        if (clusters.Count == 0)
            return CycleReport.Empty;

        var byId = contended.ToDictionary(g => g.AgentId, StringComparer.Ordinal);
        var results = new List<AgentCycleResult>();
        foreach (var cluster in clusters)
        {
            var members = cluster
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => byId[id])
                .ToList();

            if (_jointResolver == JointResolverKind.Cbs)
            {
                // Solve + reserve the cluster jointly (CCBS when continuous), reusing the executor-facing path. Blacklist
                // the physical cells held by contended agents OUTSIDE this cluster (the SAME set the PIBT branch blocks)
                // and union them into the static blockers, so CBS never plans/reserves through a cluster-external
                // contended agent's cell even when the host caller passes null blockedResources. (The flowing fleet holds
                // leases the view already respects; this covers the unreserved-but-occupying contended non-members.)
                var memberIds = members.Select(m => m.AgentId).ToHashSet(StringComparer.Ordinal);
                var external = new HashSet<ResourceRef>(blockedResources ?? (IReadOnlySet<ResourceRef>)new HashSet<ResourceRef>());
                foreach (var g in contended)
                    if (!memberIds.Contains(g.AgentId))
                        external.Add(RoadmapGraph.SiteRef(g.FromSiteId));
                var report = await PlanClusterAsync(roadmapId, members, external, cancellationToken).ConfigureAwait(false);
                results.AddRange(report.Results);
                _logger.LogDebug("Standoff cluster ({Count}) handed to CBS.", members.Count);
            }
            else // JointResolverKind.Pibt — a single collision-free joint hop, committed atomically through the table.
            {
                results.AddRange(await GrantPibtJointStepAsync(graph, members, contended, HopsTo, cancellationToken).ConfigureAwait(false));
            }
        }

        return new CycleReport(results);
    }

    /// <summary>Computes a cluster's next collision-free joint single hop (PIBT) and commits it atomically through the
    /// reservation table; each member is reserved when the step was granted AND it actually advances this tick.</summary>
    private async Task<IReadOnlyList<AgentCycleResult>> GrantPibtJointStepAsync(
        RoadmapGraph graph,
        IReadOnlyList<AgentGoal> members,
        IReadOnlyCollection<AgentGoal> allContended,
        Func<string, IReadOnlyDictionary<string, int>> hopsTo,
        CancellationToken cancellationToken)
    {
        var memberIds = members.Select(m => m.AgentId).ToHashSet(StringComparer.Ordinal);
        // Cells held by contended agents OUTSIDE this cluster are off-limits this tick. The reserved/flowing fleet
        // holds leases, so TryGrantJointStep rejects any step onto them regardless (the table is the authority); this
        // just lets PIBT avoid the known poses up front.
        var blocked = allContended
            .Where(g => !memberIds.Contains(g.AgentId))
            .Select(g => g.FromSiteId)
            .ToHashSet(StringComparer.Ordinal);

        var views = members
            .Select(m => new PibtAgentView(m.AgentId, m.FromSiteId, m.ToSiteId, m.Priority, HeldTicks: 0))
            .ToList();
        var move = _jointStep.PlanJointStep(views, blocked, graph, hopsTo);

        var moves = members
            .Select(m => new JointStepMove(m.AgentId, m.FromSiteId, move[m.AgentId]))
            .ToList();
        // Pin the window once so the returned path's cells match the leases TryGrantJointStep reserves exactly.
        var nowMs = _clock.NowMs;
        var outcome = await _traffic.TryGrantJointStepAsync(moves, nowMs, JointStepWindowMs, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Standoff cluster ({Count}) PIBT joint step -> {Outcome}.", members.Count, outcome);

        return members
            .Select(m =>
            {
                var to = move[m.AgentId];
                var advanced = !string.Equals(to, m.FromSiteId, StringComparison.Ordinal);
                var reserved = outcome == AllocationOutcome.Granted && advanced;
                return new AgentCycleResult(
                    m.AgentId,
                    Planned: true,
                    Reserved: reserved,
                    Outcome: outcome,
                    Attempts: 1,
                    // Reserved ⇒ Path != null (AgentCycleResult contract): the granted one-hop move as a SpaceTimePath
                    // over the SAME [now, now+step) window / resources TryGrantJointStep reserved (from-CP, traversed
                    // lane, to-CP). A held or non-granted member did not reserve, so it carries no path.
                    Path: reserved ? OneHopPath(m.FromSiteId, to, nowMs, JointStepWindowMs) : null,
                    FailureReason: outcome == AllocationOutcome.Granted ? null : $"joint step {outcome}",
                    IntendedNextCell: advanced ? to : null);
            })
            .ToList();
    }

    /// <summary>Builds the <see cref="SpaceTimePath"/> for one granted joint single hop <paramref name="from"/> →
    /// <paramref name="to"/> over <c>[nowMs, nowMs+stepMs)</c>: the from-CP, the traversed directed lane, and the to-CP,
    /// each over that window — the same resources/window <c>TryGrantJointStep</c> reserved for the move, in the
    /// CP/Lane/CP shape the executor's CP extraction and <c>SetEnRouteFromPath</c> consumers expect.</summary>
    private static SpaceTimePath OneHopPath(string from, string to, long nowMs, long stepMs)
    {
        var window = new TimeInterval(nowMs, nowMs + stepMs);
        return new SpaceTimePath(new[]
        {
            new SpaceTimeCell(RoadmapGraph.SiteRef(from), window),
            new SpaceTimeCell(RoadmapGraph.LaneRef(from, to), window),
            new SpaceTimeCell(RoadmapGraph.SiteRef(to), window),
        });
    }

    /// <summary>The out-neighbour of <paramref name="from"/> strictly closer to <paramref name="goal"/> (least hops,
    /// ties by ordinal id), or null when none (at the goal or boxed). The agent's intended next cell for clustering.</summary>
    private static string? NextHopToward(
        RoadmapGraph graph, string from, string goal, Func<string, IReadOnlyDictionary<string, int>> hopsTo)
    {
        var hops = hopsTo(goal);
        if (!hops.TryGetValue(from, out var fromHops) || fromHops == 0)
            return null;
        string? best = null;
        var bestHops = int.MaxValue;
        foreach (var n in graph.Neighbours(from))
            if (hops.TryGetValue(n, out var h) && h < fromHops
                && (h < bestHops || (h == bestHops && (best is null || string.CompareOrdinal(n, best) < 0))))
            {
                bestHops = h;
                best = n;
            }
        return best;
    }

    /// <summary>A report in which no cluster member obtained a reservation — the executor falls back per agent.</summary>
    private static CycleReport Unreserved(IReadOnlyCollection<AgentGoal> cluster, string reason)
        => new(cluster
            .Select(g => new AgentCycleResult(g.AgentId, Planned: false, Reserved: false, Outcome: null, Attempts: 0, Path: null, FailureReason: reason))
            .ToList());
}
