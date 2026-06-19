using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Shared;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// The v3 planner: <b>SIPPwRT / TP-SIPPwRT</b> — Safe-Interval Path Planning over <b>continuous time</b> with
/// kinematics. It is structurally the same <c>(vertex, safe-interval)</c> A* as <see cref="SippPathPlanner"/>,
/// but a hop no longer takes a fixed <c>HopMs</c> tick: each edge takes its <b>real traversal time</b>
/// <c>EdgeKinematics.DurationMs(EdgeWeight(v,w), profile)</c> (a trapezoidal accel→cruise→decel motion under the
/// supplied <see cref="KinematicProfile"/>). The produced <see cref="SpaceTimePath"/> carries real-millisecond
/// cell intervals — and because the Kernel <see cref="TimeInterval"/> / <c>ReservationTable</c> are already
/// interval-granularity-agnostic, those intervals are reserved by <c>TryReserve</c> unchanged.
/// <para>
/// <b>Determinism.</b> All scheduling arithmetic is integer milliseconds; the only floating-point lives inside
/// <see cref="EdgeKinematics.DurationMs"/> and is collapsed to a deterministic <see cref="long"/> there. The
/// neighbour order, safe-interval indexing, closed-set early-exit and tie-breaks are copied verbatim from SIPP, so
/// identical inputs yield a byte-identical path. On a <i>uniform</i> graph (all edges equal length) SIPPwRT
/// returns the same site sequence as SIPP (only the interval widths scale); on a non-uniform graph it prefers the
/// time-optimal route, which a min-hop discrete planner cannot see.
/// </para>
/// </summary>
/// <remarks>
/// SIPP is <c>sealed</c> with a private nested search, so this is a deliberate clone-and-adapt rather than a
/// subclass — keeping <see cref="SippPathPlanner"/> frozen/byte-identical. Implements the frozen
/// <see cref="IPathPlanner.Plan"/> seam unchanged. Stateless → singleton-safe (the profile is immutable).
/// </remarks>
public sealed class SippwrtPathPlanner : IPathPlanner
{
    /// <summary>Minimum non-degenerate dwell at the terminal (goal/frontier) cell, in ms.</summary>
    public const long GoalDwellMs = TimeAxis.HopMs;

    private readonly KinematicProfile _profile;

    public SippwrtPathPlanner(KinematicProfile? profile = null) => _profile = profile ?? KinematicProfile.Default;

    /// <summary>A SIPP search state: a vertex paired with the index of one of that vertex's safe intervals.</summary>
    private readonly record struct State(string Vertex, int IntervalIndex);

    /// <summary>One search outcome: the state to reconstruct from, plus whether it reached the goal or a frontier.</summary>
    private readonly record struct SearchResult(State State, bool ReachesGoal);

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

        // Trivial plan: already at the goal. One non-degenerate dwell cell, zero cost (mirrors Dijkstra/SIPP).
        if (string.Equals(start, goal, StringComparison.Ordinal))
        {
            var dwell = new SpaceTimeCell(RoadmapGraph.SiteRef(start), new TimeInterval(release, release + GoalDwellMs));
            return PlanResult.Succeeded(new SpaceTimePath([dwell]), PlanCost.Zero);
        }

        // Time-admissible, consistent heuristic: minimum traversal TIME to the goal (zero waits) over directed
        // edges, in ms. A lower bound on the true remaining cost (waits only add). Unreachable start ⇒ no route.
        var minTimeToGoal = MinTimeToGoal(graph, goal, _profile);
        if (!minTimeToGoal.ContainsKey(start))
            return PlanResult.Failed($"[{PathPlanningErrorCodes.NoRoute}] No route from '{start}' to '{goal}' in roadmap '{request.RoadmapId}'.");

        var search = new Search(graph, request, reservations, minTimeToGoal, _profile);
        var result = search.Run();
        if (result is null)
            return PlanResult.Failed($"[{PathPlanningErrorCodes.NoRoute}] No conflict-free route from '{start}' to '{goal}' in roadmap '{request.RoadmapId}'.");

        return search.Reconstruct(result.Value.State, result.Value.ReachesGoal);
    }

    /// <summary>
    /// Minimum traversal TIME (ms, zero waits) from every vertex to <paramref name="goal"/>: a reverse Dijkstra
    /// over edge durations (the continuous-time analogue of SIPP's hop-count reverse-BFS). Vertices with no
    /// directed path to the goal are absent. Deterministic — the resulting distances are unique regardless of
    /// heap pop order.
    /// </summary>
    private static Dictionary<string, long> MinTimeToGoal(RoadmapGraph graph, string goal, KinematicProfile profile)
    {
        // Reverse adjacency carrying the original edge's duration: predecessors[w] = { (v, dur(v→w)) }.
        var predecessors = new Dictionary<string, List<(string V, long Dur)>>(StringComparer.Ordinal);
        foreach (var v in graph.Vertices)
            foreach (var w in graph.Neighbours(v))
            {
                var dur = EdgeKinematics.DurationMs(graph.EdgeWeight(v, w) ?? 1, profile);
                (predecessors.TryGetValue(w, out var list) ? list : predecessors[w] = new List<(string, long)>()).Add((v, dur));
            }

        var dist = new Dictionary<string, long>(StringComparer.Ordinal) { [goal] = 0 };
        var open = new PriorityQueue<string, long>();
        open.Enqueue(goal, 0);
        while (open.TryDequeue(out var u, out var du))
        {
            if (du > dist.GetValueOrDefault(u, long.MaxValue))
                continue; // stale duplicate
            if (!predecessors.TryGetValue(u, out var preds))
                continue;
            foreach (var (v, dur) in preds)
            {
                var nd = du + dur;
                if (nd < dist.GetValueOrDefault(v, long.MaxValue))
                {
                    dist[v] = nd;
                    open.Enqueue(v, nd);
                }
            }
        }

        return dist;
    }

    /// <summary>One SIPPwRT A* search, holding the per-resource safe-interval + per-edge duration caches.</summary>
    private sealed class Search
    {
        // A generous backstop against a pathological loop; the (vertex × safe-interval) state space is finite.
        private const int MaxExpansions = 1_000_000;

        private readonly RoadmapGraph _graph;
        private readonly PlanRequest _request;
        private readonly IReservationView _view;
        private readonly Dictionary<string, long> _minTimeToGoal;
        private readonly KinematicProfile _profile;
        private readonly long _horizonEndMs;

        // Best windowed frontier seen so far (closest to the true goal by min-TIME-to-goal, then arrival, then id).
        private State? _bestFrontier;
        private long _bestFrontierTime;
        private long _bestFrontierArrival;

        // Per-resource safe intervals + per-edge durations, materialised once and reused across expansions.
        private readonly Dictionary<string, List<TimeInterval>> _cpIntervals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TimeInterval>> _laneIntervals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _edgeDur = new(StringComparer.Ordinal);

        private readonly Dictionary<State, long> _arrival = new();
        private readonly Dictionary<State, (State Prev, long Depart)> _cameFrom = new();

        public Search(RoadmapGraph graph, PlanRequest request, IReservationView view, Dictionary<string, long> minTimeToGoal, KinematicProfile profile)
        {
            _graph = graph;
            _request = request;
            _view = view;
            _minTimeToGoal = minTimeToGoal;
            _profile = profile;
            _horizonEndMs = request.HorizonEndMs;
        }

        public SearchResult? Run()
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
                    continue;
                if (++expansions > MaxExpansions)
                    return null;

                var g = _arrival[state];

                if (string.Equals(state.Vertex, _request.ToSiteId, StringComparison.Ordinal)
                    && CanDwellAt(state, g))
                    return new SearchResult(state, ReachesGoal: true);

                ConsiderFrontier(state, g);

                Expand(state, g, open, closed);
            }

            if (_horizonEndMs == long.MaxValue || _bestFrontier is null)
                return null;
            return new SearchResult(_bestFrontier.Value, ReachesGoal: false);
        }

        /// <summary>Records the incumbent windowed frontier; tie-breaks by (min-time-to-goal, arrival, ordinal id).</summary>
        private void ConsiderFrontier(State state, long arrival)
        {
            if (!_minTimeToGoal.TryGetValue(state.Vertex, out var t))
                return;
            if (!CanDwellAt(state, arrival))
                return;

            var better = _bestFrontier is null
                || t < _bestFrontierTime
                || (t == _bestFrontierTime && arrival < _bestFrontierArrival)
                || (t == _bestFrontierTime && arrival == _bestFrontierArrival
                    && string.CompareOrdinal(state.Vertex, _bestFrontier.Value.Vertex) < 0);
            if (!better)
                return;
            _bestFrontier = state;
            _bestFrontierTime = t;
            _bestFrontierArrival = arrival;
        }

        private bool CanDwellAt(State state, long arrival)
        {
            var interval = CpIntervals(state.Vertex)[state.IntervalIndex];
            return interval.Contains(arrival)
                && (interval.EndMs == long.MaxValue || arrival <= interval.EndMs - GoalDwellMs);
        }

        private void Expand(State state, long g, PriorityQueue<State, long> open, HashSet<State> closed)
        {
            var v = state.Vertex;
            var iv = CpIntervals(v)[state.IntervalIndex];

            foreach (var w in _graph.Neighbours(v).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (IsBlacklistedTransition(_request, v, w))
                    continue;

                var laneId = RoadmapGraph.LaneId(v, w);
                var laneRef = new ResourceRef(ResourceKind.Lane, laneId);
                var dur = EdgeDurationMs(v, w, laneId);
                var wIntervals = CpIntervals(w);

                for (var jw = 0; jw < wIntervals.Count; jw++)
                {
                    var succ = new State(w, jw);
                    if (closed.Contains(succ))
                        continue;

                    var arrival = EarliestArrival(g, iv, wIntervals[jw], laneRef, laneId, dur);
                    if (arrival is null)
                        continue;
                    if (arrival.Value > _horizonEndMs)
                        continue;

                    var depart = arrival.Value - dur;
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
        /// Earliest arrival at the destination interval honouring: departure ≥ current arrival; the held CP cell
        /// <c>[g, depart+dur)</c> fitting the source safe interval; the arrival landing inside the destination
        /// interval with room for the minimum dwell; and the lane free for the <c>dur</c>-long traversal window.
        /// </summary>
        private long? EarliestArrival(long g, TimeInterval iv, TimeInterval jw, ResourceRef laneRef, string laneId, long dur)
        {
            //   d >= g                              — cannot leave before arriving at v
            //   d >= jw.Start - dur                 — arrival d+dur must reach the destination interval
            //   d <= iv.End - dur                   — the move-out [d, d+dur) must fit v's safe interval
            //   d <= jw.End - dur - GoalDwellMs     — arrival a=d+dur must hold the minimum dwell [a, a+GoalDwellMs) in jw
            //   (SIPPwRT separates the TRAVERSAL time `dur` from the minimum DWELL `GoalDwellMs`; SIPP conflated
            //    both as HopMs, so its `jw.End - 2*HopMs` becomes `jw.End - dur - GoalDwellMs` here.)
            var dMin = Math.Max(g, jw.StartMs - dur);
            var dMaxByV = iv.EndMs == long.MaxValue ? long.MaxValue : iv.EndMs - dur;
            var dMaxByW = jw.EndMs == long.MaxValue ? long.MaxValue : jw.EndMs - dur - GoalDwellMs;
            var dMax = Math.Min(dMaxByV, dMaxByW);
            if (dMin > dMax)
                return null;

            var depart = EarliestFreeDeparture(laneRef, laneId, dMin, dMax, dur);
            return depart is null ? null : depart.Value + dur;
        }

        /// <summary>Earliest <c>d</c> in <c>[dMin, dMax]</c> with the lane free for the whole <c>[d, d+dur)</c> traversal.</summary>
        private long? EarliestFreeDeparture(ResourceRef laneRef, string laneId, long dMin, long dMax, long dur)
        {
            foreach (var free in LaneIntervals(laneRef, laneId))
            {
                var hi = free.EndMs == long.MaxValue ? long.MaxValue : free.EndMs - dur;
                var lo = Math.Max(free.StartMs, dMin);
                var capped = Math.Min(hi, dMax);
                if (lo <= capped)
                    return lo;
            }

            return null;
        }

        /// <summary>A* priority: earliest arrival plus the admissible min-time-to-goal heuristic (saturating).</summary>
        private long Priority(string vertex, long arrival)
        {
            var h = _minTimeToGoal.TryGetValue(vertex, out var t) ? t : long.MaxValue - arrival;
            return arrival > long.MaxValue - h ? long.MaxValue : arrival + h;
        }

        private long EdgeDurationMs(string from, string to, string laneId)
        {
            if (_edgeDur.TryGetValue(laneId, out var cached))
                return cached;
            var dur = EdgeKinematics.DurationMs(_graph.EdgeWeight(from, to) ?? 1, _profile);
            _edgeDur[laneId] = dur;
            return dur;
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

        private static bool IsBlacklistedTransition(PlanRequest request, string fromSiteId, string toSiteId)
        {
            if (!string.Equals(toSiteId, request.FromSiteId, StringComparison.Ordinal)
                && !string.Equals(toSiteId, request.ToSiteId, StringComparison.Ordinal)
                && (request.IsBlacklisted(toSiteId) || request.IsBlacklisted(RoadmapGraph.SiteRef(toSiteId))))
                return true;

            return request.IsBlacklisted(RoadmapGraph.LaneRef(fromSiteId, toSiteId));
        }

        /// <summary>Walks the came-from chain into a <see cref="SpaceTimePath"/> with real-ms (kinematic) cell widths.</summary>
        public PlanResult Reconstruct(State terminalState, bool reachesGoal)
        {
            var nodes = new List<(string Vertex, long Arrival)>();
            var departs = new List<long>();

            var cursor = terminalState;
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
                var dur = EdgeDurationMs(from, to, RoadmapGraph.LaneId(from, to));

                // CP held from arrival until the move into the next CP completes (covers any planned wait); the
                // directed Lane is held for the real (kinematic) traversal window [depart, depart+dur).
                cells.Add(new SpaceTimeCell(RoadmapGraph.SiteRef(from), new TimeInterval(arrival, depart + dur)));
                cells.Add(new SpaceTimeCell(new ResourceRef(ResourceKind.Lane, RoadmapGraph.LaneId(from, to)), new TimeInterval(depart, depart + dur)));

                totalDistance += _graph.EdgeWeight(from, to) ?? 1;
            }

            var terminalArrival = nodes[^1].Arrival;
            var terminalInterval = new TimeInterval(terminalArrival, terminalArrival + GoalDwellMs);
            cells.Add(new SpaceTimeCell(RoadmapGraph.SiteRef(nodes[^1].Vertex), terminalInterval));

            var hopCount = nodes.Count - 1;
            var durationMs = terminalArrival + GoalDwellMs - _request.ReleaseTimeMs;
            return PlanResult.Succeeded(new SpaceTimePath(cells), new PlanCost(totalDistance, hopCount, durationMs), reachesGoal);
        }
    }
}
