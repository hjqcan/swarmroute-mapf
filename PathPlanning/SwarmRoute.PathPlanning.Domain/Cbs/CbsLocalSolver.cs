using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Cbs;

/// <summary>
/// Discrete, bounded, local <b>Conflict-Based Search</b> (Sharon et al. 2015). Given a small cluster of agents on
/// a directed roadmap and the rest-of-fleet reservations, it returns conflict-free space-time paths for all of
/// them — or a clean failure. It cracks dense standoffs greedy priority-inheritance (PIBT) cannot (e.g. a head-on
/// that needs one agent to wait in a passing bay while the other passes), because it searches the joint solution
/// space rather than committing greedily.
/// <para>
/// <b>Two levels.</b> High level: a best-first constraint tree on sum-of-costs; each node has a constraint set and
/// one satisfying path per agent; the first inter-agent conflict is split into two children, each adding one
/// constraint to one agent. Low level: <see cref="SippPathPlanner"/> run against a <see cref="CbsConstraintView"/>
/// (external reservations ∪ that agent's constraints) — reused verbatim, so the produced <see cref="SpaceTimePath"/>
/// is axis-consistent and directly reservable. Conflict detection mirrors the reservation table exactly (same-CP
/// overlap = vertex; reversed-lane overlap = head-on edge; touching half-open intervals — "following" — do not
/// conflict).
/// </para>
/// <para>
/// <b>Sound, complete-and-optimal within the bounds; clean failure outside.</b> A <see cref="CbsStatus.Solved"/>
/// result's paths are mutually conflict-free and consistent with external reservations, with minimum sum-of-costs.
/// Beyond the node / cluster-size / horizon budget it returns a failure status (never a wrong or colliding answer),
/// so the caller falls back and the executor's DidNotConverge floor holds. A pure function — no I/O, no clock, no RNG.
/// </para>
/// </summary>
public sealed class CbsLocalSolver
{
    private readonly CbsOptions _options;
    private readonly IPathPlanner _lowLevel;

    /// <param name="options">Tractability bounds (defaults are sensible for congestion-pocket clusters).</param>
    /// <param name="lowLevel">The constrained single-agent search; defaults to a fresh <see cref="SippPathPlanner"/>.</param>
    public CbsLocalSolver(CbsOptions? options = null, IPathPlanner? lowLevel = null)
    {
        _options = options ?? new CbsOptions();
        _lowLevel = lowLevel ?? new SippPathPlanner();
    }

    /// <summary>
    /// Solves the cluster jointly. All agents share the release tick <paramref name="releaseTick"/> (they depart
    /// from their current cells at the same instant). <paramref name="externalView"/> holds the rest of the
    /// fleet's reservations the cluster must not violate.
    /// </summary>
    public CbsResult Solve(
        RoadmapGraph graph,
        IReadOnlyList<CbsAgent> agents,
        IReservationView externalView,
        long releaseTick,
        IReadOnlySet<ResourceRef>? blockedResources = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(externalView);

        if (agents.Count == 0)
            return CbsResult.Success(new Dictionary<string, SpaceTimePath>(StringComparer.Ordinal), 0, 0);
        if (agents.Count > _options.MaxAgents)
            return CbsResult.Failure(CbsStatus.BudgetExceeded, 0, $"cluster of {agents.Count} exceeds MaxAgents={_options.MaxAgents}.");

        // Fix one canonical agent order; every downstream tie-break derives from it.
        var canonical = agents.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        var byId = canonical.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var ids = canonical.Select(a => a.Id).ToList();
        var horizon = _options.TimeHorizonTicks == long.MaxValue
            ? long.MaxValue
            : SaturatingAdd(releaseTick, _options.TimeHorizonTicks);

        var sequence = 0L;

        // Root: each agent planned independently against only the external reservations.
        var rootPaths = new Dictionary<string, SpaceTimePath>(StringComparer.Ordinal);
        var rootCosts = new Dictionary<string, long>(StringComparer.Ordinal);
        var rootReachesGoal = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var a in canonical)
        {
            var plan = LowLevelPlan(graph, a, externalView, Array.Empty<CbsConstraint>(), releaseTick, horizon, blockedResources);
            if (plan is null)
                return CbsResult.Failure(CbsStatus.Infeasible, 0, $"agent '{a.Id}' has no path to '{a.Goal}'.");
            rootPaths[a.Id] = plan.Value.Path;
            rootCosts[a.Id] = plan.Value.Cost;
            rootReachesGoal[a.Id] = plan.Value.ReachesGoal;
        }

        var open = new PriorityQueue<CbsNode, NodeKey>();
        Enqueue(open, new CbsNode(Array.Empty<CbsConstraint>(), rootPaths, rootCosts, rootReachesGoal, Sum(rootCosts), sequence++));

        var nodesExpanded = 0;
        while (open.TryDequeue(out var node, out _))
        {
            nodesExpanded++;
            if (nodesExpanded > _options.HighLevelNodeBudget)
                return CbsResult.Failure(CbsStatus.BudgetExceeded, nodesExpanded, $"node budget {_options.HighLevelNodeBudget} exceeded.");

            var conflict = FindFirstConflict(ids, node.Paths, node.ReachesGoal);
            if (conflict is null)
                return CbsResult.Success(node.Paths, node.SumOfCosts, nodesExpanded);

            foreach (var (agentId, kind, resource) in TwoBranches(conflict))
            {
                if (node.Constraints.Count >= _options.MaxConstraintsPerNode)
                    continue; // depth backstop — skip, do not crash

                var newConstraint = new CbsConstraint(agentId, kind, resource, new TimeInterval(conflict.Tick, conflict.Tick + 1));
                var childConstraints = Append(node.Constraints, newConstraint);

                var replanned = LowLevelPlan(graph, byId[agentId], externalView, childConstraints, releaseTick, horizon, blockedResources);
                if (replanned is null)
                    continue; // this branch is infeasible → prune

                var childPaths = With(node.Paths, agentId, replanned.Value.Path);
                var childCosts = With(node.Costs, agentId, replanned.Value.Cost);
                var childReachesGoal = With(node.ReachesGoal, agentId, replanned.Value.ReachesGoal);
                Enqueue(open, new CbsNode(childConstraints, childPaths, childCosts, childReachesGoal, Sum(childCosts), sequence++));
            }
        }

        return CbsResult.Failure(CbsStatus.NoSolution, nodesExpanded, "constraint tree exhausted; cluster unsolvable within the horizon.");
    }

    // ── Low level ────────────────────────────────────────────────────────────────────────────────────────────

    private (SpaceTimePath Path, long Cost, bool ReachesGoal)? LowLevelPlan(
        RoadmapGraph graph, CbsAgent agent, IReservationView externalView,
        IReadOnlyList<CbsConstraint> constraints, long releaseTick, long horizon,
        IReadOnlySet<ResourceRef>? blockedResources)
    {
        var view = new CbsConstraintView(externalView, constraints.Where(c => string.Equals(c.AgentId, agent.Id, StringComparison.Ordinal)));
        var request = new PlanRequest(
            Guid.Empty,
            agent.Id,
            agent.Start,
            agent.Goal,
            releaseTimeMs: releaseTick,
            blacklistedResources: blockedResources,
            horizonEndMs: horizon);
        var result = _lowLevel.Plan(graph, request, view);
        return result is { Success: true, Path: not null, Cost: not null }
            ? (result.Path, result.Cost.DurationMs, result.ReachesGoal)
            : null;
    }

    // ── Conflict detection (mirrors the reservation table's collision definition) ────────────────────────────

    private static CbsConflict? FindFirstConflict(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, SpaceTimePath> paths,
        IReadOnlyDictionary<string, bool> reachesGoal)
    {
        // Per agent: CP cells and Lane cells. The terminal CP is extended to +∞ only when the low level reached the
        // real goal; an RHCR frontier is just a finite window boundary, not a permanent parked obstacle.
        var cpCells = ids.ToDictionary(id => id, id => CpCells(paths[id], reachesGoal[id]), StringComparer.Ordinal);
        var laneCells = ids.ToDictionary(
            id => id,
            id => paths[id].Cells.Where(c => c.Resource.Kind == ResourceKind.Lane).ToList(),
            StringComparer.Ordinal);

        var conflicts = new List<CbsConflict>();
        for (var i = 0; i < ids.Count; i++)
            for (var j = i + 1; j < ids.Count; j++)
            {
                var a = ids[i];
                var b = ids[j];

                foreach (var ca in cpCells[a])
                    foreach (var cb in cpCells[b])
                        if (ca.Resource.Equals(cb.Resource) && ca.Interval.Overlaps(cb.Interval))
                            conflicts.Add(new CbsConflict(CbsConflictKind.Vertex, a, b, ca.Resource, cb.Resource,
                                Math.Max(ca.Interval.StartMs, cb.Interval.StartMs)));

                foreach (var la in laneCells[a])
                    foreach (var lb in laneCells[b])
                        if (IsReversedLane(la.Resource, lb.Resource) && la.Interval.Overlaps(lb.Interval))
                            conflicts.Add(new CbsConflict(CbsConflictKind.Edge, a, b, la.Resource, lb.Resource,
                                Math.Max(la.Interval.StartMs, lb.Interval.StartMs)));
            }

        // Deterministic "first" conflict: earliest tick, then ordinal agent pair, then Vertex before Edge.
        return conflicts
            .OrderBy(c => c.Tick)
            .ThenBy(c => c.AgentA, StringComparer.Ordinal)
            .ThenBy(c => c.AgentB, StringComparer.Ordinal)
            .ThenBy(c => (int)c.Kind)
            .FirstOrDefault();
    }

    /// <summary>CP cells of a path; extend the terminal CP only when the agent parks at its real goal.</summary>
    private static List<SpaceTimeCell> CpCells(SpaceTimePath path, bool terminalParks)
    {
        var cps = path.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).ToList();
        if (terminalParks && cps.Count > 0)
        {
            var last = cps[^1];
            cps[^1] = new SpaceTimeCell(last.Resource, new TimeInterval(last.Interval.StartMs, long.MaxValue));
        }
        return cps;
    }

    private static IEnumerable<(string AgentId, CbsConstraintKind Kind, ResourceRef Resource)> TwoBranches(CbsConflict conflict)
    {
        var kind = conflict.Kind == CbsConflictKind.Vertex ? CbsConstraintKind.Vertex : CbsConstraintKind.Edge;
        yield return (conflict.AgentA, kind, conflict.ResourceA);
        yield return (conflict.AgentB, kind, conflict.ResourceB);
    }

    /// <summary>The reservation table's reversed-lane rule (split on the first '-'); a head-on edge collision.</summary>
    private static bool IsReversedLane(ResourceRef a, ResourceRef b)
    {
        if (a.Kind != ResourceKind.Lane || b.Kind != ResourceKind.Lane)
            return false;

        var dashA = a.Id.IndexOf('-');
        var dashB = b.Id.IndexOf('-');
        if (dashA <= 0 || dashB <= 0)
            return false;

        return a.Id.AsSpan(0, dashA).SequenceEqual(b.Id.AsSpan(dashB + 1))
            && a.Id.AsSpan(dashA + 1).SequenceEqual(b.Id.AsSpan(0, dashB));
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────────────

    private static void Enqueue(PriorityQueue<CbsNode, NodeKey> open, CbsNode node)
        => open.Enqueue(node, new NodeKey(node.SumOfCosts, node.Constraints.Count, node.Sequence));

    private static long Sum(Dictionary<string, long> costs)
    {
        long total = 0;
        foreach (var c in costs.Values)
            total += c;
        return total;
    }

    private static IReadOnlyList<CbsConstraint> Append(IReadOnlyList<CbsConstraint> list, CbsConstraint item)
    {
        var copy = new List<CbsConstraint>(list.Count + 1);
        copy.AddRange(list);
        copy.Add(item);
        return copy;
    }

    private static Dictionary<string, T> With<T>(IReadOnlyDictionary<string, T> source, string key, T value)
    {
        var copy = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var kv in source)
            copy[kv.Key] = kv.Value;
        copy[key] = value;
        return copy;
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>One CBS constraint-tree node: a constraint set, a satisfying path + cost per agent, and a
    /// monotonic sequence number that makes the open-list ordering a strict total order (deterministic).</summary>
    private sealed record CbsNode(
        IReadOnlyList<CbsConstraint> Constraints,
        IReadOnlyDictionary<string, SpaceTimePath> Paths,
        IReadOnlyDictionary<string, long> Costs,
        IReadOnlyDictionary<string, bool> ReachesGoal,
        long SumOfCosts,
        long Sequence);

    /// <summary>Strict total order on open-list nodes: sum-of-costs, then shallower, then insertion order.</summary>
    private readonly record struct NodeKey(long SumOfCosts, int ConstraintCount, long Sequence) : IComparable<NodeKey>
    {
        public int CompareTo(NodeKey other)
        {
            var c = SumOfCosts.CompareTo(other.SumOfCosts);
            if (c != 0) return c;
            c = ConstraintCount.CompareTo(other.ConstraintCount);
            return c != 0 ? c : Sequence.CompareTo(other.Sequence);
        }
    }
}
