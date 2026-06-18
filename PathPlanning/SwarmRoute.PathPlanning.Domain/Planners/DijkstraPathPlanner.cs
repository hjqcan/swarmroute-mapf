using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Shared;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// The v0 baseline planner: pruned-Dijkstra single-agent shortest path. Ports
/// <c>AJR.MAPF.XCBS.CBS.SearchPath</c>, which ran
/// <c>new DijkstraShortestPaths(graph, start).ShortestPathTo(end)</c> and returned the site sequence (or
/// <c>null</c> when the endpoint was blocked / no route existed). Here that exact computation is reused via
/// <see cref="RoadmapGraph.ShortestPath(string, string)"/> (the Map context already wraps the same vendored
/// <c>DijkstraShortestPaths</c>), and the result is lifted into a Kernel <see cref="SpaceTimePath"/>.
/// </summary>
/// <remarks>
/// <para><b>Failure branches (ported from <c>SearchPath</c> returning <c>null</c>):</b> unknown start/goal
/// vertex, or no route from start to goal → <see cref="PlanResult.Failed(string)"/>.</para>
/// <para><b>Timeline:</b> each graph edge contributes a directed Lane cell for the traversal window, and each
/// site contributes a CP cell. CP and Lane cells for the same movement window intentionally overlap: the v0
/// model reserves both the point and the segment for safety, while SIPP can later refine exact enter/exit
/// timing without changing the resource vocabulary.</para>
/// <para><b>Reservation awareness:</b> v0 accepts the <see cref="IReservationView"/> (so the call site is
/// wired) but does not search safe intervals yet. It does enforce the request's blacklist over CP and Lane
/// resources. v1's SIPP planner will additionally search the view in time.</para>
/// </remarks>
public sealed class DijkstraPathPlanner : IPathPlanner
{
    /// <summary>
    /// Dwell assigned to the terminal (goal) cell so its half-open interval is non-degenerate, in fleet-clock
    /// milliseconds. Purely so the produced <see cref="SpaceTimePath"/> type is well-formed in v0.
    /// </summary>
    public const long GoalDwellMs = 1;

    /// <inheritdoc />
    public PlanResult Plan(RoadmapGraph graph, PlanRequest request, IReservationView reservations)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reservations); // read but treated as always-free in v0.

        // Defensive: surface unknown endpoints as a distinct, actionable failure (ShortestPath also returns
        // null for these, but the reason should not be conflated with genuine "no route").
        if (!graph.HasSite(request.FromSiteId))
            return PlanResult.Failed($"[{PathPlanningErrorCodes.UnknownSite}] Start site '{request.FromSiteId}' is not in roadmap '{request.RoadmapId}'.");
        if (!graph.HasSite(request.ToSiteId))
            return PlanResult.Failed($"[{PathPlanningErrorCodes.UnknownSite}] Goal site '{request.ToSiteId}' is not in roadmap '{request.RoadmapId}'.");

        var sites = ShortestPath(graph, request);
        if (sites is null || sites.Count == 0)
            return PlanResult.Failed($"[{PathPlanningErrorCodes.NoRoute}] No route from '{request.FromSiteId}' to '{request.ToSiteId}' in roadmap '{request.RoadmapId}'.");

        var (path, cost) = BuildTimeline(graph, sites, request.ReleaseTimeMs);
        return PlanResult.Succeeded(path, cost);
    }

    private static IReadOnlyList<string>? ShortestPath(RoadmapGraph graph, PlanRequest request)
    {
        var start = request.FromSiteId;
        var goal = request.ToSiteId;
        if (string.Equals(start, goal, StringComparison.Ordinal))
            return [start];

        var distances = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [start] = 0
        };
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, long>();
        queue.Enqueue(start, 0);

        while (queue.TryDequeue(out var current, out var currentDistance))
        {
            if (distances.TryGetValue(current, out var knownDistance) && currentDistance > knownDistance)
                continue;

            if (string.Equals(current, goal, StringComparison.Ordinal))
                return Reconstruct(previous, start, goal);

            foreach (var next in graph.Neighbours(current).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (IsBlacklistedTransition(request, current, next))
                    continue;

                var weight = graph.EdgeWeight(current, next) ?? 1;
                if (weight <= 0)
                    weight = 1;

                var candidate = currentDistance + weight;
                if (distances.TryGetValue(next, out var best) && candidate >= best)
                    continue;

                distances[next] = candidate;
                previous[next] = current;
                queue.Enqueue(next, candidate);
            }
        }

        return null;
    }

    private static bool IsBlacklistedTransition(PlanRequest request, string fromSiteId, string toSiteId)
    {
        if (!string.Equals(toSiteId, request.FromSiteId, StringComparison.Ordinal)
            && !string.Equals(toSiteId, request.ToSiteId, StringComparison.Ordinal)
            && (request.IsBlacklisted(toSiteId) || request.IsBlacklisted(RoadmapGraph.SiteRef(toSiteId))))
            return true;

        return request.IsBlacklisted(RoadmapGraph.LaneRef(fromSiteId, toSiteId));
    }

    private static IReadOnlyList<string> Reconstruct(
        IReadOnlyDictionary<string, string> previous,
        string start,
        string goal)
    {
        var path = new List<string> { goal };
        var current = goal;
        while (!string.Equals(current, start, StringComparison.Ordinal))
        {
            current = previous[current];
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Lifts an ordered site sequence into a <see cref="SpaceTimePath"/> and its <see cref="PlanCost"/>,
    /// using cumulative scaled edge weight as a proxy duration from <paramref name="releaseTimeMs"/>. The CP
    /// and Lane cells for one traversal share the same interval by design.
    /// </summary>
    private static (SpaceTimePath Path, PlanCost Cost) BuildTimeline(
        RoadmapGraph graph,
        IReadOnlyList<string> sites,
        long releaseTimeMs)
    {
        var cells = new List<SpaceTimeCell>(sites.Count);

        // Single-site plan (start == goal): one non-degenerate dwell cell, zero cost.
        if (sites.Count == 1)
        {
            cells.Add(new SpaceTimeCell(
                RoadmapGraph.SiteRef(sites[0]),
                new TimeInterval(releaseTimeMs, releaseTimeMs + GoalDwellMs)));
            return (new SpaceTimePath(cells), PlanCost.Zero);
        }

        var cursor = releaseTimeMs;
        long totalDistance = 0;

        for (var i = 0; i < sites.Count - 1; i++)
        {
            // Weight of the edge leaving site i; clamp to >= 1 so the timeline strictly advances even if an
            // edge weight is somehow non-positive (RoadmapGraph already clamps zero-length lines to 1).
            var weight = graph.EdgeWeight(sites[i], sites[i + 1]) ?? 1;
            if (weight <= 0)
                weight = 1;

            var next = cursor + weight;
            var traversal = new TimeInterval(cursor, next);
            cells.Add(new SpaceTimeCell(RoadmapGraph.SiteRef(sites[i]), traversal));
            cells.Add(new SpaceTimeCell(RoadmapGraph.LaneRef(sites[i], sites[i + 1]), traversal));

            totalDistance += weight;
            cursor = next;
        }

        // Terminal (goal) cell: a unit dwell so the half-open interval is non-degenerate.
        cells.Add(new SpaceTimeCell(
            RoadmapGraph.SiteRef(sites[^1]),
            new TimeInterval(cursor, cursor + GoalDwellMs)));

        var hopCount = sites.Count - 1;
        var durationMs = cursor + GoalDwellMs - releaseTimeMs;
        return (new SpaceTimePath(cells), new PlanCost(totalDistance, hopCount, durationMs));
    }
}
