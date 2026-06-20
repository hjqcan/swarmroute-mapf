namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab — Robust Execution) Derives the run's <b>Action Dependency Graph</b> structure and its
/// delay-robustness from the recorded timeline. For every control point shared by more than one AGV the plan implies
/// a <b>handoff</b> dependency: the later AGV may enter only after the earlier one has left (a TPG/ADG type-2 edge).
/// The <b>slack</b> of a handoff is the planned buffer between them; the tightest handoff (minimum slack) is the
/// largest single delay the plan can absorb before two AGVs would collide under naive timestamp execution — which is
/// exactly why a real deployment follows the dependency graph rather than the clock. A pure, deterministic function of
/// the frames (no engine instrumentation).
/// <para>
/// This builds + measures the dependency graph. A full ADG/TPG-<i>following</i> executor (that absorbs arbitrary
/// delays collision-free by waiting on dependencies instead of timestamps) is the larger follow-up — deliberately not
/// claimed here.
/// </para>
/// </summary>
public static class RobustnessAnalyzer
{
    private const int TopTightest = 6;

    public static RobustnessDto Compute(FleetLoopResult loop)
    {
        ArgumentNullException.ThrowIfNull(loop);

        // Per-agent occupancy intervals on each cell: [enter, exit), exit = the tick it leaves for its next cell (or
        // one past the last frame for the cell it ends on). Built from consecutive same-cell frames.
        var intervalsByCell = new Dictionary<string, List<(long Enter, long Exit)>>(StringComparer.Ordinal);
        var lastTick = loop.Frames.Count > 0 ? loop.Frames[^1].Tick : 0;

        var current = new Dictionary<string, (string Cell, long Enter)>(StringComparer.Ordinal);
        foreach (var frame in loop.Frames)
            foreach (var p in frame.Positions)
            {
                if (current.TryGetValue(p.AgentId, out var open))
                {
                    if (string.Equals(open.Cell, p.SiteId, StringComparison.Ordinal))
                        continue; // still on the same cell
                    Add(intervalsByCell, open.Cell, open.Enter, frame.Tick); // closed: left at this frame's tick
                }
                current[p.AgentId] = (p.SiteId, frame.Tick);
            }
        // Close the final (terminal) interval for every agent — occupied through the end of the run.
        foreach (var (_, open) in current)
            Add(intervalsByCell, open.Cell, open.Enter, lastTick + 1);

        // Each shared cell yields (k-1) handoff dependencies; the slack between consecutive holders is the buffer.
        var dependencies = 0;
        var tightHandoffs = 0;
        var minSlack = long.MaxValue;
        var slackByCell = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var (cell, intervals) in intervalsByCell)
        {
            if (intervals.Count < 2)
                continue;
            intervals.Sort((a, b) => a.Enter.CompareTo(b.Enter));
            for (var i = 1; i < intervals.Count; i++)
            {
                dependencies++;
                var slack = Math.Max(0, intervals[i].Enter - intervals[i - 1].Exit);
                if (slack == 0)
                    tightHandoffs++;
                if (slack < minSlack)
                    minSlack = slack;
                if (!slackByCell.TryGetValue(cell, out var s) || slack < s)
                    slackByCell[cell] = slack;
            }
        }

        var tightest = slackByCell
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(TopTightest)
            .Select(kv => kv.Key)
            .ToList();

        return new RobustnessDto(
            HandoffDependencies: dependencies,
            TightHandoffs: tightHandoffs,
            MinSlackTicks: dependencies == 0 ? 0 : (int)Math.Min(minSlack, int.MaxValue),
            TightestCells: tightest);
    }

    private static void Add(Dictionary<string, List<(long, long)>> map, string cell, long enter, long exit)
    {
        if (!map.TryGetValue(cell, out var list))
            map[cell] = list = new List<(long, long)>();
        list.Add((enter, exit));
    }
}
