namespace SwarmRoute.Liveness.Domain.Detection;

/// <summary>An immutable snapshot of one agent used to detect physical standoff clusters. It captures the agent's
/// INTENT (the cell it wants to enter next) and a unified stuckness counter — deliberately independent of the
/// agent's reservation state. A standoff is a physical fact about poses and intents, so it must be detected the
/// same whether a member is en route (holding a reservation, blocked at the gate) or walled-out pending (a
/// re-plan that can only "wait in place" because every forward move is sealed). Keying off the reservation flag
/// instead would miss a swap/chain the moment one member drops to pending — the joint resolver would then never
/// see it.</summary>
/// <param name="Id">Stable agent id.</param>
/// <param name="IntendedNextCell">The control point the agent wants to enter next: an en-route agent's next route
/// CP, or a pending agent's next hop toward its goal. <see langword="null"/> when it has none (at its goal or
/// rolling-horizon frontier, no progressing move, or it is not an active candidate).</param>
/// <param name="StuckTicks">Consecutive ticks the agent has failed to make forward progress — blocked at the gate
/// while en route, or unable to reserve a progressing route while pending. A member at or above the trigger
/// threshold seeds an actionable cluster.</param>
/// <param name="IsCandidate">Whether the agent is eligible to join a standoff cluster: actively seeking its goal
/// and not already owned by another resolver (i.e. not done, not PIBT-driven, not redirecting or holding aside).</param>
public readonly record struct StuckAgentSnapshot(string Id, string? IntendedNextCell, int StuckTicks, bool IsCandidate);

/// <summary>
/// Promotes the executor's physical-standoff signal into actionable congestion clusters. A cluster is a connected
/// component of <b>mutually-obstructing</b> agents — head-on swaps and circular blocking chains both fall into one
/// component — that contains at least one agent stuck for <c>triggerThreshold</c>+ ticks. Only such clusters are
/// handed to the joint resolver (<see cref="PibtZoneResolver"/> / local CBS); the rest of the fleet keeps running
/// prioritized-SIPP.
/// <para>
/// A pure function over poses and intents. An agent is a <i>candidate</i> only when it is an active goal-seeker
/// (<see cref="StuckAgentSnapshot.IsCandidate"/>) whose intended next cell is physically occupied this tick (so it
/// cannot advance); it is linked to whichever candidate occupies that cell (its blocker). Candidacy is
/// reservation-state-agnostic: a walled-out pending agent obstructs — and is obstructed — exactly like a blocked
/// en-route one, so both join the same standoff. A blocker that is NOT a candidate (a parked/finished vehicle on a
/// goal approach) is intentionally left unlinked: that is a re-task-the-parked-vehicle problem for the step-aside
/// mechanism, not a live standoff for the joint resolver. Singletons are dropped: a lone agent queued behind a
/// vehicle that is itself free to move is not a deadlock and clears on its own — so a real cluster has size ≥ 2.
/// </para>
/// </summary>
public static class StuckClusterDetector
{
    /// <summary>
    /// Returns the actionable stuck clusters, each as the set of agent ids in it, ordered deterministically by
    /// the component's smallest id. Empty when nothing is stuck.
    /// </summary>
    /// <param name="fleet">Current per-agent snapshots.</param>
    /// <param name="occupantNow">Cell → the id of the agent physically on it this tick.</param>
    /// <param name="triggerThreshold">A component is actionable only if a member's <c>StuckTicks</c> reaches this.</param>
    public static IReadOnlyList<IReadOnlySet<string>> Assemble(
        IReadOnlyList<StuckAgentSnapshot> fleet,
        IReadOnlyDictionary<string, string> occupantNow,
        int triggerThreshold)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(occupantNow);

        // Candidates: an active goal-seeker whose intended next cell is physically occupied this tick (→ cannot
        // advance). Reservation state is irrelevant — pending and en-route stuck agents are both candidates.
        var candidates = new Dictionary<string, StuckAgentSnapshot>(StringComparer.Ordinal);
        foreach (var a in fleet)
            if (a.IsCandidate && a.IntendedNextCell is not null && occupantNow.ContainsKey(a.IntendedNextCell))
                candidates[a.Id] = a;

        // Union-find: link each candidate to the candidate that occupies its intended next cell (its blocker).
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in candidates.Keys)
            parent[id] = id;

        string Find(string x)
        {
            while (!string.Equals(parent[x], x, StringComparison.Ordinal))
                x = parent[x] = parent[parent[x]];
            return x;
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (!string.Equals(ra, rb, StringComparison.Ordinal))
                parent[ra] = rb;
        }

        foreach (var a in candidates.Values)
        {
            var blocker = occupantNow[a.IntendedNextCell!];
            if (candidates.ContainsKey(blocker) && !string.Equals(blocker, a.Id, StringComparison.Ordinal))
                Union(a.Id, blocker);
        }

        // Group by component root; keep those that contain a seed and have ≥ 2 members.
        var groups = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var seededRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in candidates.Values)
        {
            var root = Find(a.Id);
            (groups.TryGetValue(root, out var set) ? set : groups[root] = new SortedSet<string>(StringComparer.Ordinal)).Add(a.Id);
            if (a.StuckTicks >= triggerThreshold)
                seededRoots.Add(root);
        }

        return groups
            .Where(kv => kv.Value.Count >= 2 && seededRoots.Contains(kv.Key))
            .Select(kv => kv.Value)
            .OrderBy(set => set.Min, StringComparer.Ordinal)
            .Select(set => (IReadOnlySet<string>)new HashSet<string>(set, StringComparer.Ordinal))
            .ToList();
    }
}
