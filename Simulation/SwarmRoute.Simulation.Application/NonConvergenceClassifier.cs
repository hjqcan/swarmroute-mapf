using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (FMS-V2) Post-hoc diagnostics for a <see cref="FleetLoopStatus.DidNotConverge"/> run: classifies every
/// not-arrived AGV into a single <see cref="NonConvergenceReason"/> (未收斂分類器), purely by reading the recorded
/// timeline + the roadmap. It never perturbs the run and is invoked ONLY on a non-converged result, so a converged
/// run stays byte-identical.
/// <para>
/// <b>Per-agent rule</b> (checked in this order, first match wins):
/// </para>
/// <list type="number">
///   <item><see cref="NonConvergenceReason.NoWellFormedEndpointPath"/> — the goal is not a graph vertex, or there is
///     no path to it even on the EMPTY graph (independent of any parked vehicle).</item>
///   <item><see cref="NonConvergenceReason.ParkedGoalBlocker"/> — the goal IS reachable on the empty graph but
///     UNREACHABLE once every parked (finished) vehicle's cell is removed: a parked vehicle walls off the only
///     approach (the permanent goal-blocking the WarehouseWellFormed fix targets).</item>
///   <item><see cref="NonConvergenceReason.ParkingSaturation"/> — the agent's task is done (it sits on a
///     workstation/dock it was bound to and is no longer moving toward it) but no free parking/buffer slot is
///     reachable to clear to.</item>
///   <item><see cref="NonConvergenceReason.LiveStandoffUnresolved"/> — the goal is reachable on the live
///     (parked-minus) graph, yet the agent sat motionless on one cell for a long unbroken stretch at the end of the
///     run (a physical standoff the executor never broke).</item>
///   <item><see cref="NonConvergenceReason.TickBudgetExceeded"/> — none of the above: the goal is reachable and the
///     agent was still making or able to make progress; the run simply ran out of ticks.</item>
/// </list>
/// </summary>
internal static class NonConvergenceClassifier
{
    /// <summary>A motionless stretch at least this many ticks long (at the run's end) marks a live standoff rather
    /// than a mere budget overrun. Deterministic and conservative — a brief end-of-run wait is not a standoff.</summary>
    private const int StandoffTailTicks = 12;

    /// <summary>
    /// Classifies a non-converged run. Returns <see langword="null"/> for a CONVERGED run (or one with no recorded
    /// frames), so the caller attaches the diagnostic only when there is something to report (byte-identical
    /// otherwise). For a non-converged run it returns the per-agent reasons (only the not-arrived agents) and the
    /// dominant reason (the most frequent, ties broken by enum order for determinism).
    /// </summary>
    /// <param name="loop">The recorded closed-loop result.</param>
    /// <param name="specs">The fleet's start/goal specs (goal per agent).</param>
    /// <param name="graph">The roadmap reachability is computed on.</param>
    /// <param name="siteRoles">(FMS) The per-site FMS role map, used to detect parking-saturation; may be empty
    /// (a non-FMS run), in which case the parking-saturation branch never fires.</param>
    public static NonConvergenceReport? Classify(
        FleetLoopResult loop,
        IReadOnlyList<FleetAgentSpec> specs,
        RoadmapGraph graph,
        IReadOnlyDictionary<string, SiteRole>? siteRoles = null)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(graph);

        if (loop.Stats.Status != FleetLoopStatus.DidNotConverge || loop.Frames.Count == 0)
            return null;

        siteRoles ??= EmptyRoles;
        var goalById = specs.ToDictionary(s => s.Id, s => s.GoalSiteId, StringComparer.Ordinal);

        var last = loop.Frames[^1];

        // Cells a parked (arrived/done) vehicle occupies at the end — the obstacles the live graph removes. An agent
        // is "arrived" when its final motion state is Arrived; its final cell is then a permanent parked obstacle.
        var parkedCells = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in last.Positions)
            if (p.State == AgentMotionState.Arrived)
                parkedCells.Add(p.SiteId);

        var perAgent = new Dictionary<string, NonConvergenceReason>(StringComparer.Ordinal);
        foreach (var p in last.Positions)
        {
            if (p.State == AgentMotionState.Arrived)
                continue; // arrived agents are not stranded
            if (!goalById.TryGetValue(p.AgentId, out var goal))
                continue;

            perAgent[p.AgentId] = ClassifyAgent(p.AgentId, p.SiteId, goal, graph, parkedCells, siteRoles, loop.Frames);
        }

        if (perAgent.Count == 0)
            return null; // status says non-converged but every agent ended Arrived — nothing stranded to explain

        // Dominant reason = the most frequent; ties broken by the enum's natural order (lowest value) so it is
        // deterministic. (ParkedGoalBlocker outranks the higher-valued reasons on a tie, which is the conservative
        // choice for the M-F2 regression check — it surfaces goal-blocking rather than hiding it under a tie.)
        var dominant = perAgent.Values
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First().Key;

        return new NonConvergenceReport(dominant, perAgent);
    }

    /// <summary>Classifies one not-arrived agent against the first-match-wins rule documented on the type.</summary>
    private static NonConvergenceReason ClassifyAgent(
        string agentId,
        string here,
        string goal,
        RoadmapGraph graph,
        IReadOnlySet<string> parkedCells,
        IReadOnlyDictionary<string, SiteRole> siteRoles,
        IReadOnlyList<FleetTickFrame> frames)
    {
        // (1) Structurally impossible: the goal is not a vertex, or unreachable even on the EMPTY graph.
        if (!graph.HasSite(goal) || graph.ShortestPath(here, goal) is null)
            return NonConvergenceReason.NoWellFormedEndpointPath;

        // (2) Reachable on the empty graph, but is the only approach walled off by PARKED vehicles? Recompute the
        //     path avoiding parked cells (excluding the agent's own cell + the goal cell themselves, which the agent
        //     legitimately occupies / is heading to). Unreachable now ⇒ a parked vehicle is the goal blocker.
        var blockers = parkedCells
            .Where(c => !string.Equals(c, here, StringComparison.Ordinal)
                && !string.Equals(c, goal, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (blockers.Count > 0 && !ReachableAvoiding(graph, here, goal, blockers))
            return NonConvergenceReason.ParkedGoalBlocker;

        // (3) Parking saturation: the agent's task is done — it sits on a workstation/dock endpoint and is no longer
        //     advancing toward its goal — but no free parking/buffer slot is reachable to clear to. Only meaningful
        //     when the run carries FMS roles; with an empty role map this never fires.
        if (siteRoles.Count > 0
            && IsServicedAndStuck(here, goal, siteRoles)
            && NoFreeRestingSiteReachable(graph, here, parkedCells, siteRoles))
            return NonConvergenceReason.ParkingSaturation;

        // (4) Goal reachable on the live graph, but the agent sat motionless for a long tail ⇒ a live standoff the
        //     executor never resolved.
        if (MotionlessTailTicks(frames, agentId) >= StandoffTailTicks)
            return NonConvergenceReason.LiveStandoffUnresolved;

        // (5) Otherwise the goal is reachable and the agent was (or could be) progressing — just out of ticks.
        return NonConvergenceReason.TickBudgetExceeded;
    }

    /// <summary>True when a path from <paramref name="from"/> to <paramref name="to"/> exists in the undirected
    /// projection of <paramref name="graph"/> while avoiding every cell in <paramref name="blocked"/> (BFS over
    /// out-edges and reverse edges, since the grid carries both lane directions; <paramref name="from"/> and
    /// <paramref name="to"/> are assumed already excluded from <paramref name="blocked"/>).</summary>
    private static bool ReachableAvoiding(RoadmapGraph graph, string from, string to, IReadOnlySet<string> blocked)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return true;

        var visited = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in UndirectedNeighbours(graph, current))
            {
                if (blocked.Contains(next) || !visited.Add(next))
                    continue;
                if (string.Equals(next, to, StringComparison.Ordinal))
                    return true;
                queue.Enqueue(next);
            }
        }

        return false;
    }

    /// <summary>The undirected neighbours of <paramref name="site"/>: out-neighbours plus vertices that list it as an
    /// out-neighbour (the grid is bidirectional, so this is direction-robust). Ordinal-sorted for determinism.</summary>
    private static IEnumerable<string> UndirectedNeighbours(RoadmapGraph graph, string site)
    {
        var neighbours = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var successor in graph.Neighbours(site))
            neighbours.Add(successor);
        foreach (var vertex in graph.Vertices)
        {
            if (string.Equals(vertex, site, StringComparison.Ordinal))
                continue;
            foreach (var successor in graph.Neighbours(vertex))
                if (string.Equals(successor, site, StringComparison.Ordinal))
                {
                    neighbours.Add(vertex);
                    break;
                }
        }

        return neighbours;
    }

    /// <summary>True when the agent's task looks DONE: it currently sits on its own goal AND that goal is a
    /// workstation or dock endpoint (a service site it has reached and now only needs to clear to parking from).</summary>
    private static bool IsServicedAndStuck(
        string here, string goal,
        IReadOnlyDictionary<string, SiteRole> siteRoles)
    {
        if (!string.Equals(here, goal, StringComparison.Ordinal))
            return false;
        var role = siteRoles.TryGetValue(here, out var r) ? r : SiteRole.Transit;
        return role is SiteRole.Workstation or SiteRole.DockPoint;
    }

    /// <summary>True when NO free parking (then buffer) slot is reachable from <paramref name="here"/> on the
    /// graph-minus-parked: every resting site is either occupied by a parked vehicle or unreachable.</summary>
    private static bool NoFreeRestingSiteReachable(
        RoadmapGraph graph, string here,
        IReadOnlySet<string> parkedCells,
        IReadOnlyDictionary<string, SiteRole> siteRoles)
    {
        var blockers = parkedCells
            .Where(c => !string.Equals(c, here, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (site, role) in siteRoles)
        {
            if (role is not (SiteRole.Parking or SiteRole.Buffer))
                continue;
            if (parkedCells.Contains(site) || string.Equals(site, here, StringComparison.Ordinal))
                continue; // taken or where we already stand
            if (!graph.HasSite(site))
                continue;
            if (ReachableAvoiding(graph, here, site, blockers))
                return false; // a free resting site IS reachable ⇒ not saturated
        }

        return true;
    }

    /// <summary>The length (in ticks) of the agent's MOTIONLESS tail: how many consecutive final frames it sat on
    /// the same cell. A long tail with a reachable goal is the live-standoff signal.</summary>
    private static int MotionlessTailTicks(IReadOnlyList<FleetTickFrame> frames, string agentId)
    {
        var tail = 0;
        string? last = null;
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            var pos = frames[i].Positions.FirstOrDefault(p => string.Equals(p.AgentId, agentId, StringComparison.Ordinal));
            if (pos is null)
                break;
            if (last is null)
            {
                last = pos.SiteId;
                tail = 1;
                continue;
            }
            if (!string.Equals(pos.SiteId, last, StringComparison.Ordinal))
                break;
            tail++;
        }

        return tail;
    }

    private static readonly IReadOnlyDictionary<string, SiteRole> EmptyRoles =
        new Dictionary<string, SiteRole>(StringComparer.Ordinal);
}

/// <summary>
/// (FMS-V2) The classified outcome of a non-converged run: the dominant reason across all not-arrived AGVs plus the
/// per-agent breakdown. Produced only on a <see cref="FleetLoopStatus.DidNotConverge"/> run.
/// </summary>
/// <param name="DominantReason">The most frequent per-agent reason (ties broken by enum order, deterministically).</param>
/// <param name="PerAgentReasons">Each not-arrived agent's classified reason.</param>
internal sealed record NonConvergenceReport(
    NonConvergenceReason DominantReason,
    IReadOnlyDictionary<string, NonConvergenceReason> PerAgentReasons);
