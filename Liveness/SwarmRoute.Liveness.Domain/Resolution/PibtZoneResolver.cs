using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Liveness.Domain.Resolution;

/// <summary>
/// PIBT (Priority Inheritance with Backtracking, Okumura et al.) restricted to one congestion cluster, for one
/// tick. Given the cluster's agents, the cells occupied/claimed by the rest of the fleet this tick, the roadmap
/// and a hop-distance oracle, it decides every cluster agent's NEXT control point at once — collision-free among
/// the cluster by construction (no two land on the same cell; no head-on lane swap), and never throwing.
/// <para>
/// This is the principled, deadlock-aware generalization of the executor's ad-hoc stall-reroute / step-aside
/// band-aids: a stuck pocket is shuffled one hop at a time until the standoff dissolves, after which each agent
/// re-plans back to prioritized-SIPP. It is a <b>pure function</b> (no engine, no I/O, no mutable driver state),
/// so it is unit-testable in isolation and the v3 host-seam wraps it behind an <c>IJointStepPlanner</c> port unchanged.
/// </para>
/// <para><b>Guarantees.</b> Every decided target is claimed by exactly one agent (vertex-distinct); a 2-cycle
/// swap is refused (longer rotations a→b→c→a are allowed and are collision-free); the highest-priority agent
/// always gets its most-preferred reachable cell, so at least one agent makes progress whenever any move exists.
/// When the whole pocket is gridlocked, every agent holds — the caller advances no one that tick and the
/// driver's <c>DidNotConverge</c> tick budget remains the ultimate backstop. PIBT can only help; it can never
/// produce a collision or a crash.</para>
/// </summary>
public static class PibtZoneResolver
{
    /// <summary>
    /// Computes the cluster's joint single-hop move for this tick.
    /// </summary>
    /// <param name="cluster">The congestion cluster's agents (each with its current cell + effective goal).</param>
    /// <param name="blockedCells">Cells occupied or being entered this tick by agents OUTSIDE the cluster
    /// (immovable obstacles): the rest of the fleet's current positions plus their claimed next cells. A cluster
    /// agent is never allowed to move onto one of these, which is what keeps PIBT collision-free against the
    /// flowing fleet within the same tick.</param>
    /// <param name="graph">The roadmap (directed). Out-neighbours define the only legal one-hop moves, so lane
    /// directionality is honoured for free; staying put needs no edge.</param>
    /// <param name="hopsToGoal">Goal → (cell → hop-distance). Supplied by the caller so it can memoize across the
    /// episode; <see cref="HopDistances.To"/> is the standard implementation.</param>
    /// <returns><c>agentId → next cell</c> for every cluster agent (a value equal to the agent's current cell
    /// means "hold this tick"). Deterministic for a given input.</returns>
    public static IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyList<PibtAgentView> cluster,
        IReadOnlySet<string> blockedCells,
        RoadmapGraph graph,
        Func<string, IReadOnlyDictionary<string, int>> hopsToGoal)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(blockedCells);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(hopsToGoal);

        // Where each cluster member currently sits, and which member sits on a given cell (for inheritance).
        var cellOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var goalOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var occupant = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in cluster)
        {
            cellOf[a.Id] = a.Cell;
            goalOf[a.Id] = a.Goal;
            occupant[a.Cell] = a.Id;
        }

        // Processing order: most-waited first (anti-livelock), then static priority, then ordinal id. A pure
        // function of recorded state, so the result is deterministic.
        var ordered = cluster
            .OrderByDescending(a => a.HeldTicks)
            .ThenBy(a => a.Priority)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => a.Id)
            .ToList();

        var next = new Dictionary<string, string>(StringComparer.Ordinal); // decided moves (agent → next cell)
        var taken = new HashSet<string>(StringComparer.Ordinal);           // cells claimed by a decided agent

        foreach (var id in ordered)
            if (!next.ContainsKey(id))
                Step(id, callerCell: null, depth: 0);

        return next;

        // Returns true when `ai` found a valid next cell (committing it in `next`); false only for a PUSHED agent
        // that cannot vacate, signalling its caller to backtrack. A non-pushed agent always succeeds because
        // "stay" is among its candidates and its current cell is never taken when it is processed top-level.
        bool Step(string ai, string? callerCell, int depth)
        {
            // Defensive bound: each push targets a distinct still-undecided agent, so recursion depth cannot
            // exceed the cluster size; this guard only caps a hypothetical logic defect (cf. SIPP's MaxExpansions).
            if (depth > cluster.Count)
                return false;

            var from = cellOf[ai];
            var hops = hopsToGoal(goalOf[ai]);

            foreach (var v in OrderedCandidates(from, hops))
            {
                if (taken.Contains(v))
                    continue;                                   // already claimed by a decided agent
                if (v != from && blockedCells.Contains(v))
                    continue;                                   // a non-cluster agent holds/claims this cell
                if (callerCell is not null && v == callerCell)
                    continue;                                   // would swap with the agent that pushed me

                next[ai] = v;                                   // tentatively claim v
                taken.Add(v);

                if (occupant.TryGetValue(v, out var bj) && bj != ai && !next.ContainsKey(bj))
                {
                    // Someone sits on v and hasn't moved yet → priority inheritance: push them off v. Passing
                    // `from` as their caller forbids them from swapping straight back into my cell.
                    if (Step(bj, callerCell: from, depth + 1))
                        return true;

                    next.Remove(ai);                            // push failed → backtrack, try my next candidate
                    taken.Remove(v);
                    continue;
                }

                return true;                                    // v is free, or held by an agent already vacating
            }

            return false;                                       // pushed agent with nowhere to go
        }

        // Candidates = {stay} ∪ out-neighbours, ranked by hop-distance to goal, then cheaper lane, then ordinal id
        // — a total order, so ties are broken deterministically. Staying sorts by the current cell's own distance
        // (weight 0), so a strictly-improving neighbour is always preferred and the agent never needlessly retreats.
        IEnumerable<string> OrderedCandidates(string from, IReadOnlyDictionary<string, int> hops)
        {
            var candidates = new List<string>(graph.Neighbours(from)) { from };
            return candidates
                .OrderBy(w => hops.TryGetValue(w, out var d) ? d : int.MaxValue)
                .ThenBy(w => string.Equals(w, from, StringComparison.Ordinal) ? 0L : graph.EdgeWeight(from, w) ?? long.MaxValue)
                .ThenBy(w => w, StringComparer.Ordinal);
        }
    }
}
