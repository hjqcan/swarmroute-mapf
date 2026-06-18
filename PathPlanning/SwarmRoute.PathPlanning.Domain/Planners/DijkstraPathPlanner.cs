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
/// <para><b>Timeline (v0 is space-only):</b> the path is given a well-formed, strictly monotonic,
/// non-overlapping timeline by treating each edge's scaled weight as a proxy duration. Starting at
/// <see cref="PlanRequest.ReleaseTimeMs"/>, cell <c>i</c> occupies <c>[t_i, t_{i+1})</c> where the increment
/// is the weight of the edge leaving site <c>i</c>; the final (goal) cell is given a unit-length dwell so the
/// half-open interval is non-degenerate. There is NO time-conflict logic yet — that is v1 SIPP.</para>
/// <para><b>Reservation awareness:</b> v0 accepts the <see cref="IReservationView"/> (so the call site is
/// wired) but treats every resource as free — it does not consult the view. v1's SIPP planner will.</para>
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

        // Port of CBS.SearchPath: Dijkstra shortest path; null => endpoint blocked / no route.
        var sites = graph.ShortestPath(request.FromSiteId, request.ToSiteId);
        if (sites is null || sites.Count == 0)
            return PlanResult.Failed($"[{PathPlanningErrorCodes.NoRoute}] No route from '{request.FromSiteId}' to '{request.ToSiteId}' in roadmap '{request.RoadmapId}'.");

        var (path, cost) = BuildTimeline(graph, sites, request.ReleaseTimeMs);
        return PlanResult.Succeeded(path, cost);
    }

    /// <summary>
    /// Lifts an ordered site sequence into a monotonic, non-overlapping <see cref="SpaceTimePath"/> and its
    /// <see cref="PlanCost"/>, using cumulative scaled edge weight as a proxy duration from
    /// <paramref name="releaseTimeMs"/>.
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
            cells.Add(new SpaceTimeCell(RoadmapGraph.SiteRef(sites[i]), new TimeInterval(cursor, next)));

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
