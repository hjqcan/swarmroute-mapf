namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab — Robust Execution) The <b>ADG/TPG-following executor</b>, run as a self-contained what-if over
/// a completed run's plan (it never touches the live executor). It injects a delay into the most delay-brittle AGV
/// and re-executes the plan two ways:
/// <list type="bullet">
///   <item><b>Naive timestamp-following</b>: everyone keeps their planned clock times; the delayed AGV simply shifts.
///     At a tight cell-handoff this puts two AGVs on the same control point at once — a collision.</item>
///   <item><b>ADG-following</b>: each AGV enters a control point only once its <i>dependencies</i> are met — its own
///     previous hop AND every other AGV that the plan had use that cell first. Delay propagates along the dependency
///     graph instead of causing a conflict, so it stays <b>collision-free by construction</b>, at the cost of some
///     makespan.</item>
/// </list>
/// The contrast (naive collides, ADG absorbs) is exactly why a real deployment follows the dependency graph rather
/// than wall-clock timestamps. A pure, deterministic function of the recorded timeline.
/// </summary>
public static class AdgExecutor
{
    private readonly record struct Ev(string Agent, int Step);

    public static DelayResilienceDto? Simulate(FleetLoopResult loop)
    {
        ArgumentNullException.ThrowIfNull(loop);

        // 1. Per-AGV ordered hops: (cell, plannedEnter), with the exit = the next hop's enter (last cell → end+1).
        var lastTick = loop.Frames.Count > 0 ? loop.Frames[^1].Tick : 0;
        var hops = new Dictionary<string, List<(string Cell, long Enter)>>(StringComparer.Ordinal);
        var open = new Dictionary<string, (string Cell, long Enter)>(StringComparer.Ordinal);
        foreach (var frame in loop.Frames)
            foreach (var p in frame.Positions)
            {
                if (open.TryGetValue(p.AgentId, out var o) && string.Equals(o.Cell, p.SiteId, StringComparison.Ordinal))
                    continue;
                if (!hops.TryGetValue(p.AgentId, out var list))
                    hops[p.AgentId] = list = new List<(string, long)>();
                list.Add((p.SiteId, frame.Tick));
                open[p.AgentId] = (p.SiteId, frame.Tick);
            }

        long PlannedEnter(Ev e) => hops[e.Agent][e.Step].Enter;
        long PlannedExit(Ev e) => e.Step + 1 < hops[e.Agent].Count ? hops[e.Agent][e.Step + 1].Enter : lastTick + 1;

        // 2. Cell-handoff dependencies (ADG type-2): per cell, order the AGVs that visited it; a later AGV's entry
        //    depends on the earlier AGV's NEXT hop (it leaving the cell). Also find the tightest handoff to perturb.
        var deps = new Dictionary<Ev, List<Ev>>();          // lateEntry → [earlyLeave]
        var occByCell = new Dictionary<string, List<Ev>>(StringComparer.Ordinal);
        foreach (var (agent, list) in hops)
            for (var step = 0; step < list.Count; step++)
            {
                if (!occByCell.TryGetValue(list[step].Cell, out var occ))
                    occByCell[list[step].Cell] = occ = new List<Ev>();
                occ.Add(new Ev(agent, step));
            }

        Ev? tightEarly = null, tightLate = null;
        var tightSlack = long.MaxValue;
        foreach (var (_, occ) in occByCell)
        {
            if (occ.Count < 2) continue;
            occ.Sort((a, b) => PlannedEnter(a).CompareTo(PlannedEnter(b)));
            for (var i = 1; i < occ.Count; i++)
            {
                var early = occ[i - 1];
                var late = occ[i];
                if (early.Step + 1 >= hops[early.Agent].Count)
                    continue; // the early AGV parks here and never leaves → not a real handoff
                var leave = new Ev(early.Agent, early.Step + 1);
                if (!deps.TryGetValue(late, out var d))
                    deps[late] = d = new List<Ev>();
                d.Add(leave);

                var slack = PlannedEnter(late) - PlannedExit(early);
                if (slack < tightSlack)
                {
                    tightSlack = slack;
                    tightEarly = early;
                    tightLate = late;
                }
            }
        }

        if (tightEarly is null)
            return null; // no shared cells → nothing to perturb

        // 3. Perturb: delay the early AGV of the tightest handoff by just past its slack, so the naive plan breaks.
        var delayAgent = tightEarly.Value.Agent;
        var delay = (int)Math.Min(int.MaxValue, Math.Max(1, tightSlack + 1));
        _ = tightLate;
        bool Delayed(Ev e) => string.Equals(e.Agent, delayAgent, StringComparison.Ordinal);

        // Count cell occupancy overlaps (two AGVs on the same control point at once) over all occupant pairs per cell.
        int CountCollisions(Func<Ev, long> enter, Func<Ev, long> exit)
        {
            var collisions = 0;
            foreach (var (_, occ) in occByCell)
                for (var i = 0; i < occ.Count; i++)
                    for (var j = i + 1; j < occ.Count; j++)
                        if (enter(occ[i]) < exit(occ[j]) && enter(occ[j]) < exit(occ[i]))
                            collisions++;
            return collisions;
        }

        // 4. NAIVE: shift the delayed AGV's whole timeline by `delay`; everyone else keeps their planned clock times.
        var naiveCollisions = CountCollisions(
            e => PlannedEnter(e) + (Delayed(e) ? delay : 0),
            e => PlannedExit(e) + (Delayed(e) ? delay : 0));

        // 5. ADG-following: each entry's actual time is the longest path of its constraints — its own previous hop
        //    (shifted by the planned travel) AND every handoff dependency (it waits for the AGV that used the cell
        //    first to leave). Solved by memoized recursion so it is robust to frame ordering; the visiting guard keeps
        //    a degenerate cycle (possible only in a collision-recorded DidNotConverge plan) from recursing forever.
        var actual = new Dictionary<Ev, long>();
        var visiting = new HashSet<Ev>();
        long Resolve(Ev e)
        {
            if (actual.TryGetValue(e, out var cached))
                return cached;
            if (!visiting.Add(e))
                return PlannedEnter(e) + (Delayed(e) ? delay : 0); // cycle guard — fall back to the planned time
            var prev = new Ev(e.Agent, e.Step - 1);
            long t = e.Step == 0
                ? PlannedEnter(e) + (Delayed(e) ? delay : 0)
                : Resolve(prev) + Math.Max(0, PlannedEnter(e) - PlannedEnter(prev)); // never travel backwards in time
            if (deps.TryGetValue(e, out var ds))
                foreach (var d in ds)
                    t = Math.Max(t, Resolve(d)); // wait until the AGV that used this cell first has left it
            visiting.Remove(e);
            actual[e] = t;
            return t;
        }
        foreach (var (agent, list) in hops)
            for (var step = 0; step < list.Count; step++)
                Resolve(new Ev(agent, step));

        long ActualEnter(Ev e) => actual[e];
        long ActualExit(Ev e) => e.Step + 1 < hops[e.Agent].Count
            ? actual[new Ev(e.Agent, e.Step + 1)]                 // leaves when its next hop begins
            : actual[e] + (PlannedExit(e) - PlannedEnter(e));     // terminal: preserve the planned dwell
        // Self-check: the dependency-following schedule must be collision-free. This is a real recomputation, not a
        // hardcoded 0 — if the handoff dependencies were built wrong, this would surface it (and the tests catch it).
        var adgCollisions = CountCollisions(ActualEnter, ActualExit);

        long Makespan(Func<Ev, long> enter) => hops.Max(kv => enter(new Ev(kv.Key, kv.Value.Count - 1)));
        var plannedMakespan = Makespan(PlannedEnter);
        var adgMakespan = Makespan(ActualEnter);

        return new DelayResilienceDto(
            DelayTicks: delay,
            DelayedAgent: delayAgent,
            NaiveCollisions: naiveCollisions,
            AdgCollisions: adgCollisions,
            AdgMakespanInflation: (int)Math.Min(int.MaxValue, Math.Max(0, adgMakespan - plannedMakespan)),
            PlannedMakespan: (int)Math.Min(int.MaxValue, plannedMakespan));
    }
}
