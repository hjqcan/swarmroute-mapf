using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Liveness.Domain.Resolution;

/// <summary>
/// Hop-count distance from every vertex to a goal, via a reverse breadth-first sweep over the graph's directed
/// edges. This is the same heuristic <c>SippPathPlanner.HopDistancesTo</c> computes; it is lifted here (rather
/// than referenced) because <c>Simulation.Application</c> does not — and should not — reference
/// <c>PathPlanning.Domain</c>. It depends only on <see cref="RoadmapGraph"/> (Map.Domain).
/// </summary>
public static class HopDistances
{
    /// <summary>
    /// Returns <c>cell → hops-to-<paramref name="goal"/></c> over directed edges. Vertices with no directed
    /// path to the goal are simply absent (callers treat "absent" as unreachable / worst preference).
    /// </summary>
    public static IReadOnlyDictionary<string, int> To(RoadmapGraph graph, string goal)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(goal);

        // Reverse adjacency: predecessors[w] = { v : v → w is a directed edge }.
        var predecessors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var v in graph.Vertices)
            foreach (var w in graph.Neighbours(v))
                (predecessors.TryGetValue(w, out var list) ? list : predecessors[w] = new List<string>()).Add(v);

        var dist = new Dictionary<string, int>(StringComparer.Ordinal) { [goal] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(goal);
        while (queue.Count > 0)
        {
            var w = queue.Dequeue();
            var d = dist[w];
            if (!predecessors.TryGetValue(w, out var preds))
                continue;
            foreach (var v in preds)
            {
                if (dist.ContainsKey(v))
                    continue;
                dist[v] = d + 1;
                queue.Enqueue(v);
            }
        }

        return dist;
    }
}
