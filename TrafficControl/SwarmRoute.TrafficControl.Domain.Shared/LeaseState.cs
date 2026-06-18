namespace SwarmRoute.TrafficControl.Domain.Shared;

/// <summary>
/// Lifecycle state of a single <c>ResourceLease</c>. The time-interval successor to the original
/// engine's <c>MapResource.Status</c> (Locked/Unlocked) + <c>OccupiedBy</c>.
/// Drives <c>TrafficControlStateMachine</c>: <c>Requested → Reserved → InTransit → Releasing → Free</c>.
/// </summary>
public enum LeaseState
{
    /// <summary>The lease has been requested but not yet granted (resource not held).</summary>
    Requested,

    /// <summary>The lease is granted and the resource is held for the agent ahead of arrival.</summary>
    Reserved,

    /// <summary>The agent is physically traversing / occupying the resource.</summary>
    InTransit,

    /// <summary>The agent has passed and the lease is being released.</summary>
    Releasing,

    /// <summary>The lease has been released; the resource is free.</summary>
    Free
}
