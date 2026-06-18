namespace SwarmRoute.TrafficControl.Domain.Shared;

/// <summary>
/// Stable error / reason codes raised by the TrafficControl domain. Strings are deliberately
/// <c>BoundedContext.Area.Reason</c>-shaped so they are greppable and safe to surface across the wire.
/// </summary>
public static class TrafficControlErrorCodes
{
    /// <summary>A candidate path was empty (no cells to reserve).</summary>
    public const string EmptyPath = "TrafficControl.Reservation.EmptyPath";

    /// <summary>An agent id was null/empty where one was required.</summary>
    public const string MissingAgentId = "TrafficControl.Reservation.MissingAgentId";

    /// <summary>A resource on the path is already held by another agent over an overlapping interval.</summary>
    public const string ResourceContended = "TrafficControl.Reservation.ResourceContended";

    /// <summary>A resource on the path is blacklisted for the requesting agent.</summary>
    public const string ResourceBlacklisted = "TrafficControl.Reservation.ResourceBlacklisted";

    /// <summary>Attempt to create two conflicting leases (same resource, overlapping interval, different agents).</summary>
    public const string ConflictingLease = "TrafficControl.Reservation.ConflictingLease";

    /// <summary>A lease state transition was not permitted from the current state.</summary>
    public const string InvalidLeaseTransition = "TrafficControl.Lease.InvalidTransition";
}
