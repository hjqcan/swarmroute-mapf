namespace SwarmRoute.TrafficControl.Domain.Shared;

/// <summary>
/// The outcome of a reservation attempt over a candidate <c>SpaceTimePath</c>.
/// This is the public verdict returned by <c>ITrafficCoordinatorAppService.TryReserve</c>.
/// </summary>
/// <remarks>
/// In v0 (whole-path spatial lock ported faithfully) only <see cref="Granted"/> and <see cref="Queued"/>
/// are produced by the allocator; <see cref="Blocked"/> and <see cref="Preempted"/> are part of the frozen
/// contract for v1+ (priority planning / preemptive right-of-way) and are reserved here so consumers
/// (Coordination, Deadlock) need not change shape later.
/// </remarks>
public enum AllocationOutcome
{
    /// <summary>Every resource on the path was free for this agent; leases were created for the whole path.</summary>
    Granted,

    /// <summary>The path conflicts with an existing lease; the request was recorded as contended and queued.</summary>
    Queued,

    /// <summary>The path can never be granted as-is (e.g. a resource is blacklisted for this agent).</summary>
    Blocked,

    /// <summary>A lower-priority holder was (or would be) preempted to grant this request (v1+).</summary>
    Preempted,

    /// <summary>
    /// (v2) Granting/queuing this path would close a wait-for cycle, so it was refused for constructive liveness:
    /// no lease is created and no cycle-closing contended edge is recorded. Distinct from <see cref="Queued"/> —
    /// the agent must NOT wait on this path (waiting would deadlock); it must re-route. The coordination loop's
    /// prune-and-replan reacts exactly as for a denial, routing the agent around the cycle-closing resource.
    /// </summary>
    CycleAverted
}
