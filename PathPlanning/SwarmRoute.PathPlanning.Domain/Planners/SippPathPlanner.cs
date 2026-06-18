using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Shared;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// The v1 planner: Safe-Interval Path Planning (Phillips &amp; Likhachev, 2011), reservation-aware and
/// time-conflict-free. Where <see cref="DijkstraPathPlanner"/> searches space only and treats the reservation
/// view as always-free, SIPP searches <em>space-time</em>: it reads the <see cref="IReservationView"/> for each
/// control point's and lane's maximal free ("safe") intervals and finds the earliest-arrival route that never
/// overlaps another agent's lease, inserting waits where needed to let higher-priority traffic pass.
/// <para>
/// The planner builds its timeline on the unified <see cref="TimeAxis.HopMs"/> axis (one hop = one tick), so a
/// hop cell is the half-open interval <c>[t, t + HopMs)</c> and the produced <see cref="SpaceTimePath"/> lines
/// up one-for-one with the schedule-faithful executor's per-tick advance. The resource vocabulary is identical
/// to v0 — a CP cell plus a directed Lane cell per hop — so TrafficControl's <c>TryReserve</c>, the allocator
/// and the executor's CP extraction are unchanged. With no reservations to dodge, a SIPP plan reduces to the
/// same shape Dijkstra would produce (at unit hop spacing); with reservations, a wait simply lengthens the held
/// control point's dwell interval.
/// </para>
/// <para>
/// <b>Directional (edge-swap) safety</b> is delegated to the view: a head-on swap A→B vs B→A is rejected because
/// the view's <see cref="IReservationView.IsFree"/> / <see cref="IReservationView.FreeIntervals"/> already treat
/// a directed lane and its reverse as conflicting. SIPP only has to query the lane resource for the traversal
/// window.
/// </para>
/// </summary>
/// <remarks>
/// Implements the frozen <see cref="IPathPlanner.Plan"/> seam unchanged. Failure branches mirror Dijkstra:
/// unknown start/goal → <see cref="PathPlanningErrorCodes.UnknownSite"/>; unreachable / fully-blocked →
/// <see cref="PathPlanningErrorCodes.NoRoute"/>. Stateless → singleton-safe.
/// </remarks>
public sealed class SippPathPlanner : IPathPlanner
{
    /// <summary>Move duration of a single hop, in fleet-clock ms (the unified v1 axis: one tick per hop).</summary>
    public const long HopMs = TimeAxis.HopMs;

    /// <summary>Dwell assigned to the terminal (goal) cell so its half-open interval is non-degenerate.</summary>
    public const long GoalDwellMs = TimeAxis.HopMs;

    /// <summary>A SIPP search state: a vertex paired with the index of one of that vertex's safe intervals.</summary>
    private readonly record struct State(string Vertex, int IntervalIndex);

    /// <inheritdoc />
    public PlanResult Plan(RoadmapGraph graph, PlanRequest request, IReservationView reservations)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reservations);

        if (!graph.HasSite(request.FromSiteId))
            return PlanResult.Failed($"[{PathPlanningErrorCodes.UnknownSite}] Start site '{request.FromSiteId}' is not in roadmap '{request.RoadmapId}'.");
        if (!graph.HasSite(request.ToSiteId))
            return PlanResult.Failed($"[{PathPlanningErrorCodes.UnknownSite}] Goal site '{request.ToSiteId}' is not in roadmap '{request.RoadmapId}'.");

        var start = request.FromSiteId;
        var goal = request.ToSiteId;
        var release = request.ReleaseTimeMs;

        // Trivial plan: already at the goal. One non-degenerate dwell cell, zero cost (mirrors Dijkstra).
        if (string.Equals(start, goal, StringComparison.Ordinal))
        {
            var dwell = new SpaceTimeCell(RoadmapGraph.SiteRef(start), new TimeInterval(release, release + GoalDwellMs));
            return PlanResult.Succeeded(new SpaceTimePath([dwell]), PlanCost.Zero);
        }

        // Admissible, consistent heuristic: hop-count to the goal over the (un-blacklisted) graph, scaled by the
        // per-hop tick cost. Unreachable start ⇒ no route (matches Dijkstra's null path).
        var hopsToGoal = HopDistancesTo(graph, goal);
        if (!hopsToGoal.ContainsKey(start))
            return PlanResult.Failed($"[{PathPlanningErrorCodes.NoRoute}] No route from '{start}' to '{goal}' in roadmap '{request.RoadmapId}'.");

        var search = new Search(graph, request, reservations, hopsToGoal);
        var goalState = search.Run();
        if (goalState is null)
            return PlanResult.Failed($"[{PathPlanningErrorCodes.NoRoute}] No conflict-free route from '{start}' to '{goal}' in roadmap '{request.RoadmapId}'.");

        return search.Reconstruct(goalState.Value);
    }

    /// <summary>
    /// Hop-count distance from every vertex to <paramref name="goal"/> (a reverse breadth-first sweep over the
    /// graph's directed edges). Vertices with no directed path to the goal are simply absent from the map.
    /// </summary>
    private static Dictionary<string, int> HopDistancesTo(RoadmapGraph graph, string goal)
    {
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

    /// <summary>One SIPP A* search, holding the per-resource safe-interval caches for this plan.</summary>
    private sealed class Search
    {
        // A generous backstop against a pathological loop; the (vertex × safe-interval) state space is finite,
        // so a correct search never approaches this — it only bounds a hypothetical defect.
        private const int MaxExpansions = 1_000_000;

        private readonly RoadmapGraph _graph;
        private readonly PlanRequest _request;
        private readonly IReservationView _view;
        private readonly Dictionary<string, int> _hopsToGoal;

        // Per-resource safe intervals, materialised once and reused across expansions.
        private readonly Dictionary<string, List<TimeInterval>> _cpIntervals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TimeInterval>> _laneIntervals = new(StringComparer.Ordinal);

        // Search bookkeeping.
        private readonly Dictionary<State, long> _arrival = new();
        private readonly Dictionary<State, (State Prev, long Depart)> _cameFrom = new();

        public Search(RoadmapGraph graph, PlanRequest request, IReservationView view, Dictionary<string, int> hopsToGoal)
        {
            _graph = graph;
            _request = request;
            _view = view;
            _hopsToGoal = hopsToGoal;
        }

        /// <summary>Runs the search; returns the reached goal state, or null when no conflict-free route exists.</summary>
        public State? Run()
        {
            var start = _request.FromSiteId;
            var release = _request.ReleaseTimeMs;

            var startIntervals = CpIntervals(start);
            var startIdx = IndexContaining(startIntervals, release);
            if (startIdx < 0)
                return null; // the start CP is reserved by another agent at the release instant.

            var startState = new State(start, startIdx);
            _arrival[startState] = release;

            var open = new PriorityQueue<State, long>();
            open.Enqueue(startState, Priority(start, release));
            var closed = new HashSet<State>();

            var expansions = 0;
            while (open.TryDequeue(out var state, out _))
            {
                if (!closed.Add(state))
                    continue; // stale duplicate (already expanded with its optimal arrival).
                if (++expansions > MaxExpansions)
                    return null;

                var g = _arrival[state];

                // Goal test: parked at the goal in an open-ended (∞) safe interval — a lifelong park that never
                // collides with a future reservation.
                if (string.Equals(state.Vertex, _request.ToSiteId, StringComparison.Ordinal)
                    && CpIntervals(state.Vertex)[state.IntervalIndex].EndMs == long.MaxValue)
                    return state;

                Expand(state, g, open, closed);
            }

            return null;
        }

        private void Expand(State state, long g, PriorityQueue<State, long> open, HashSet<State> closed)
        {
            var v = state.Vertex;
            var iv = CpIntervals(v)[state.IntervalIndex];

            // Deterministic neighbour order so a given input always yields the same route.
            foreach (var w in _graph.Neighbours(v).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (IsBlacklistedTransition(_request, v, w))
                    continue;

                var laneId = RoadmapGraph.LaneId(v, w);
                var laneRef = new ResourceRef(ResourceKind.Lane, laneId);
                var wIntervals = CpIntervals(w);

                for (var jw = 0; jw < wIntervals.Count; jw++)
                {
                    var succ = new State(w, jw);
                    if (closed.Contains(succ))
                        continue;

                    var arrival = EarliestArrival(g, iv, wIntervals[jw], laneRef, laneId);
                    if (arrival is null)
                        continue;

                    var depart = arrival.Value - HopMs;
                    if (arrival.Value < _arrival.GetValueOrDefault(succ, long.MaxValue))
                    {
                        _arrival[succ] = arrival.Value;
                        _cameFrom[succ] = (state, depart);
                        open.Enqueue(succ, Priority(w, arrival.Value));
                    }
                }
            }
        }

        /// <summary>
        /// Earliest arrival time at the destination's safe interval, honouring: departure no earlier than the
        /// current arrival <paramref name="g"/>; the held CP cell <c>[g, depart+Hop)</c> fitting the current safe
        /// interval <paramref name="iv"/>; the arrival landing inside the destination interval
        /// <paramref name="jw"/>; and the lane being free for the traversal window. Returns null when no feasible
        /// departure exists into this destination interval.
        /// </summary>
        private long? EarliestArrival(long g, TimeInterval iv, TimeInterval jw, ResourceRef laneRef, string laneId)
        {
            // Departure window [dMin, dMax]:
            //   d >= g                          — cannot leave before arriving at v
            //   d >= jw.Start - Hop             — arrival d+Hop must reach the destination interval
            //   d <= iv.End - Hop               — the move-out [d, d+Hop) must fit v's safe interval
            //   d <= jw.End - 2*Hop             — arrival d+Hop must satisfy [d+Hop, d+2Hop) ⊆ jw
            var dMin = Math.Max(g, jw.StartMs - HopMs);
            var dMaxByV = iv.EndMs == long.MaxValue ? long.MaxValue : iv.EndMs - HopMs;
            var dMaxByW = jw.EndMs == long.MaxValue ? long.MaxValue : jw.EndMs - 2 * HopMs;
            var dMax = Math.Min(dMaxByV, dMaxByW);
            if (dMin > dMax)
                return null;

            var depart = EarliestFreeDeparture(laneRef, laneId, dMin, dMax);
            return depart is null ? null : depart.Value + HopMs;
        }

        /// <summary>
        /// Earliest instant <c>d</c> in <c>[dMin, dMax]</c> at which the lane is free for the whole traversal
        /// window <c>[d, d+Hop)</c>, computed from the lane's safe intervals (which already exclude windows where
        /// the reversed lane is held). Null when the lane is busy for the entire window.
        /// </summary>
        private long? EarliestFreeDeparture(ResourceRef laneRef, string laneId, long dMin, long dMax)
        {
            foreach (var free in LaneIntervals(laneRef, laneId))
            {
                // Feasible departures inside this free window keep [d, d+Hop) ⊆ free: d ∈ [free.Start, free.End-Hop].
                var hi = free.EndMs == long.MaxValue ? long.MaxValue : free.EndMs - HopMs;
                var lo = Math.Max(free.StartMs, dMin);
                var capped = Math.Min(hi, dMax);
                if (lo <= capped)
                    return lo; // intervals are start-ordered, so the first fit is the earliest overall.
            }

            return null;
        }

        /// <summary>A* priority: earliest arrival plus the admissible hop-count-to-goal heuristic (saturating).</summary>
        private long Priority(string vertex, long arrival)
        {
            var h = _hopsToGoal.TryGetValue(vertex, out var hops) ? hops * HopMs : long.MaxValue - arrival;
            return arrival > long.MaxValue - h ? long.MaxValue : arrival + h;
        }

        private List<TimeInterval> CpIntervals(string siteId)
        {
            if (_cpIntervals.TryGetValue(siteId, out var cached))
                return cached;
            var list = Materialise(RoadmapGraph.SiteRef(siteId));
            _cpIntervals[siteId] = list;
            return list;
        }

        private List<TimeInterval> LaneIntervals(ResourceRef laneRef, string laneId)
        {
            if (_laneIntervals.TryGetValue(laneId, out var cached))
                return cached;
            var list = Materialise(laneRef);
            _laneIntervals[laneId] = list;
            return list;
        }

        private List<TimeInterval> Materialise(ResourceRef resource)
            => _view.FreeIntervals(resource)
                .Select(si => si.Interval)
                .OrderBy(i => i.StartMs)
                .ToList();

        private static int IndexContaining(List<TimeInterval> intervals, long t)
        {
            for (var i = 0; i < intervals.Count; i++)
                if (intervals[i].Contains(t))
                    return i;
            return -1;
        }

        /// <summary>Mirrors <see cref="DijkstraPathPlanner"/>'s blacklist semantics (start/goal are never pruned).</summary>
        private static bool IsBlacklistedTransition(PlanRequest request, string fromSiteId, string toSiteId)
        {
            if (!string.Equals(toSiteId, request.FromSiteId, StringComparison.Ordinal)
                && !string.Equals(toSiteId, request.ToSiteId, StringComparison.Ordinal)
                && (request.IsBlacklisted(toSiteId) || request.IsBlacklisted(RoadmapGraph.SiteRef(toSiteId))))
                return true;

            return request.IsBlacklisted(RoadmapGraph.LaneRef(fromSiteId, toSiteId));
        }

        /// <summary>Walks the came-from chain into a <see cref="SpaceTimePath"/> + <see cref="PlanCost"/>.</summary>
        public PlanResult Reconstruct(State goalState)
        {
            // Walk back to the start, collecting (vertex, arrival) nodes and the departure used on each edge.
            var nodes = new List<(string Vertex, long Arrival)>();
            var departs = new List<long>(); // departs[i] = departure time on edge nodes[i] → nodes[i+1]

            var cursor = goalState;
            while (true)
            {
                nodes.Add((cursor.Vertex, _arrival[cursor]));
                if (!_cameFrom.TryGetValue(cursor, out var link))
                    break;
                departs.Add(link.Depart);
                cursor = link.Prev;
            }

            nodes.Reverse();
            departs.Reverse();

            var cells = new List<SpaceTimeCell>(nodes.Count * 2);
            long totalDistance = 0;

            for (var i = 0; i < nodes.Count - 1; i++)
            {
                var from = nodes[i].Vertex;
                var to = nodes[i + 1].Vertex;
                var arrival = nodes[i].Arrival;
                var depart = departs[i];

                // CP held from arrival until the move into the next CP completes (covers any planned wait); the
                // directed Lane is held for the one-tick traversal window. Mirrors Dijkstra's CP/Lane overlap.
                cells.Add(new SpaceTimeCell(RoadmapGraph.SiteRef(from), new TimeInterval(arrival, depart + HopMs)));
                cells.Add(new SpaceTimeCell(new ResourceRef(ResourceKind.Lane, RoadmapGraph.LaneId(from, to)), new TimeInterval(depart, depart + HopMs)));

                totalDistance += _graph.EdgeWeight(from, to) ?? HopMs;
            }

            // Terminal (goal) cell: a unit dwell so the half-open interval is non-degenerate.
            var goalArrival = nodes[^1].Arrival;
            cells.Add(new SpaceTimeCell(RoadmapGraph.SiteRef(nodes[^1].Vertex), new TimeInterval(goalArrival, goalArrival + GoalDwellMs)));

            var hopCount = nodes.Count - 1;
            var durationMs = goalArrival + GoalDwellMs - _request.ReleaseTimeMs;
            return PlanResult.Succeeded(new SpaceTimePath(cells), new PlanCost(totalDistance, hopCount, durationMs));
        }
    }
}
