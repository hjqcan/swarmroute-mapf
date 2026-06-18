using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// A single-agent path planner: given the roadmap graph, a request and the current reservation view, it
/// computes a space-time path (or reports failure). This is the strategy seam along the v0 → v3 roadmap —
/// v0 supplies <see cref="DijkstraPathPlanner"/> (space-only shortest path); v1 swaps in a SIPP planner that
/// actually consults <paramref name="reservations"/>. The interface is unchanged across that evolution.
/// </summary>
public interface IPathPlanner
{
    /// <summary>
    /// Plans a route for <paramref name="request"/> against <paramref name="graph"/>, consulting
    /// <paramref name="reservations"/> for conflict-free windows.
    /// </summary>
    /// <param name="graph">The built roadmap graph to search.</param>
    /// <param name="request">The agent / start / goal / release-time request.</param>
    /// <param name="reservations">
    /// The current read-only reservation view. v0's planner reads it but treats everything as free; v1's SIPP
    /// planner uses it to find safe intervals.
    /// </param>
    /// <returns>A <see cref="PlanResult"/> — success with a path + cost, or failure with a reason.</returns>
    PlanResult Plan(RoadmapGraph graph, PlanRequest request, IReservationView reservations);
}
