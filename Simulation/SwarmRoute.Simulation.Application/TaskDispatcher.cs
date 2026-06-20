using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab — Dispatcher) How the dispatcher matches AGVs to goals (the OpenTCS "Dispatcher" job: which
/// vehicle takes which task). <see cref="Random"/> is the historical uncorrelated pairing; the others minimise travel.
/// </summary>
public enum AssignmentPolicy
{
    /// <summary>The uncorrelated pairing (agent i ↔ goal i) — the historical default, byte-identical.</summary>
    Random,

    /// <summary>Greedy nearest-robot dispatch: each AGV (in turn) takes its nearest unclaimed goal. Cheap, the common
    /// real-FMS heuristic; usually much better than random, but not provably optimal.</summary>
    Nearest,

    /// <summary>Optimal assignment: the minimum-total-travel perfect matching of goals to AGVs (Hungarian algorithm
    /// over graph shortest-path distances). Provably ≤ any other assignment's total distance.</summary>
    Optimal,
}

/// <summary>
/// (v4 SwarmRoute Lab — Dispatcher) Matches a fleet's start poses to a goal pool per an <see cref="AssignmentPolicy"/>,
/// returning the goal each AGV should be sent to. A pure, deterministic function of the starts, goals and roadmap
/// (cost = graph shortest-path distance), so a dispatched run is reproducible. This is the first slice of a real
/// Order/Dispatch context: smarter matching cuts total travel and makespan without touching the planner.
/// </summary>
public static class TaskDispatcher
{
    /// <summary>Cost used for an unreachable start→goal pair (never happens on a connected field; a guard).</summary>
    private const long Unreachable = 1_000_000_000L;

    /// <summary>Returns the goal assigned to each AGV (index-aligned with <paramref name="starts"/>): for
    /// <see cref="AssignmentPolicy.Random"/> the input <paramref name="goals"/> order is kept; otherwise the goals are
    /// permuted to minimise travel. <paramref name="goals"/> must have the same count as <paramref name="starts"/>.</summary>
    public static IReadOnlyList<string> Assign(
        IReadOnlyList<string> starts, IReadOnlyList<string> goals, RoadmapGraph graph, AssignmentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(graph);
        if (policy == AssignmentPolicy.Random || starts.Count <= 1)
            return goals;

        var cost = BuildCost(starts, goals, graph);
        var assignment = policy == AssignmentPolicy.Optimal ? Hungarian(cost) : GreedyNearest(cost);
        return Enumerable.Range(0, starts.Count).Select(i => goals[assignment[i]]).ToList();
    }

    private static long[,] BuildCost(IReadOnlyList<string> starts, IReadOnlyList<string> goals, RoadmapGraph graph)
    {
        var n = starts.Count;
        var cost = new long[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                cost[i, j] = graph.DistanceTo(starts[i], goals[j]) ?? Unreachable;
        return cost;
    }

    /// <summary>Each AGV in turn (ordinal) claims its cheapest still-unclaimed goal. Deterministic.</summary>
    private static int[] GreedyNearest(long[,] cost)
    {
        var n = cost.GetLength(0);
        var taken = new bool[n];
        var assignment = new int[n];
        for (var i = 0; i < n; i++)
        {
            var best = -1;
            var bestCost = long.MaxValue;
            for (var j = 0; j < n; j++)
                if (!taken[j] && cost[i, j] < bestCost)
                {
                    bestCost = cost[i, j];
                    best = j;
                }
            taken[best] = true;
            assignment[i] = best;
        }
        return assignment;
    }

    /// <summary>
    /// Hungarian (Kuhn–Munkres) minimum-cost perfect matching, O(n³) with potentials. Returns <c>assignment[i] = j</c>
    /// (row/AGV i ↔ column/goal j) minimising the total cost — provably ≤ any other assignment. Deterministic.
    /// </summary>
    private static int[] Hungarian(long[,] cost)
    {
        var n = cost.GetLength(0);
        const long inf = long.MaxValue / 4;
        var u = new long[n + 1];
        var v = new long[n + 1];
        var p = new int[n + 1];   // p[j] = row matched to column j (1-indexed; 0 = none)
        var way = new int[n + 1];

        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = new long[n + 1];
            var used = new bool[n + 1];
            for (var j = 0; j <= n; j++)
                minv[j] = inf;

            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = -1;
                var delta = inf;
                for (var j = 1; j <= n; j++)
                    if (!used[j])
                    {
                        var cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
                        if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                        if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                    }
                for (var j = 0; j <= n; j++)
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                j0 = j1;
            }
            while (p[j0] != 0);

            do { var j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0 != 0);
        }

        var assignment = new int[n];
        for (var j = 1; j <= n; j++)
            assignment[p[j] - 1] = j - 1;
        return assignment;
    }
}
