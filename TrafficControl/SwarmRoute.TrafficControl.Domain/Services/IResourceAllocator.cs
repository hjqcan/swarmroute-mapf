using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// The grant / pruning policy: decides whether a whole candidate path can be reserved for an agent and, if so,
/// drives the aggregate to create the leases. Ports <c>GraphMap.GeneratePath</c>'s reservation half — the
/// pruned-Dijkstra resource filter (skip resources locked by another agent or blacklisted, expanded across the
/// interference closure) plus the whole-path lock. The v0 strategy is "all-or-nothing whole-path"; v1 swaps in
/// a SIPP-aware allocator behind this same interface.
/// </summary>
public interface IResourceAllocator
{
    /// <summary>
    /// Attempts to allocate the whole <paramref name="path"/> to <paramref name="agentId"/> on
    /// <paramref name="table"/> at the given <paramref name="priority"/>, returning the outcome. On
    /// <see cref="AllocationOutcome.Granted"/> the table now holds the leases; otherwise a contended request
    /// was recorded.
    /// </summary>
    AllocationOutcome Allocate(ReservationTable table, SpaceTimePath path, string agentId, int priority = 0);

    /// <summary>
    /// The set of resources that pruning would remove for <paramref name="agentId"/>: those held by another
    /// agent over the path's window or blacklisted, plus their interference closure. This is exactly the
    /// vertex/edge set the planner would delete from its graph copy, surfaced for replanning.
    /// </summary>
    IReadOnlyCollection<ResourceRef> BlockedResources(ReservationTable table, SpaceTimePath path, string agentId);
}
