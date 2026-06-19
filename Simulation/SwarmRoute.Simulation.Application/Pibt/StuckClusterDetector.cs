namespace SwarmRoute.Simulation.Application.Pibt;

/// <summary>An immutable snapshot of one en-route agent used to detect congestion clusters.</summary>
/// <param name="Id">Stable agent id.</param>
/// <param name="NextCell">The control point the agent is trying to enter this tick (its current route's next CP),
/// or <see langword="null"/> when it has none (e.g. already at the frontier/goal).</param>
/// <param name="BlockedTicks">Consecutive ticks the agent has failed to advance at the gate. An agent at or above
/// the trigger threshold is a "standoff seed".</param>
/// <param name="EnRoute">Whether the agent currently holds a reserved route (only en-route agents jam at the gate).</param>
/// <param name="Done">Whether the agent has reached its goal (parked; immovable, never a cluster member).</param>
public readonly record struct StuckAgentSnapshot(string Id, string? NextCell, int BlockedTicks, bool EnRoute, bool Done);

/// <summary>
/// Promotes the executor's existing physical-standoff diagnostic into actionable congestion clusters. A cluster
/// is a connected component of <b>mutually-blocking</b> agents — head-on swaps and circular blocking chains both
/// fall into one component — that contains at least one agent stuck for <c>triggerThreshold</c>+ ticks. Only such
/// clusters are handed to <see cref="PibtZoneResolver"/>; the rest of the fleet keeps running prioritized-SIPP.
/// <para>
/// A pure function. An agent is a <i>candidate</i> only when it is en-route, not done, and cannot advance this
/// tick (its next cell is physically occupied); it is linked to whichever candidate sits on that next cell (its
/// blocker). Singletons are dropped: a lone agent queued behind a vehicle that is itself free to move is not a
/// deadlock and will clear on its own — so a real cluster has size ≥ 2.
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
    /// <param name="triggerThreshold">A component is actionable only if a member's <c>BlockedTicks</c> reaches this.</param>
    public static IReadOnlyList<IReadOnlySet<string>> Assemble(
        IReadOnlyList<StuckAgentSnapshot> fleet,
        IReadOnlyDictionary<string, string> occupantNow,
        int triggerThreshold)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(occupantNow);

        // Candidates: en-route, not done, and blocked this tick (next cell occupied → cannot advance).
        var candidates = new Dictionary<string, StuckAgentSnapshot>(StringComparer.Ordinal);
        foreach (var a in fleet)
            if (a.EnRoute && !a.Done && a.NextCell is not null && occupantNow.ContainsKey(a.NextCell))
                candidates[a.Id] = a;

        // Union-find: link each candidate to the candidate that occupies its next cell (its blocker).
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
            var blocker = occupantNow[a.NextCell!];
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
            if (a.BlockedTicks >= triggerThreshold)
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
