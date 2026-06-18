namespace SwarmRoute.TrafficControl.Domain.StateMachine;

/// <summary>
/// The triggers that drive a lease through <c>LeaseState</c>:
/// <c>Requested →(Grant)→ Reserved →(Enter)→ InTransit →(Pass)→ Releasing →(Free)→ Free</c>.
/// </summary>
public enum LeaseTrigger
{
    /// <summary>The reservation was granted: <c>Requested → Reserved</c>. Guarded by availability / no-conflict / not-blacklisted.</summary>
    Grant,

    /// <summary>The agent physically entered the resource: <c>Reserved → InTransit</c>.</summary>
    Enter,

    /// <summary>The agent passed the resource: <c>InTransit → Releasing</c>.</summary>
    Pass,

    /// <summary>The lease was released: <c>Releasing → Free</c>.</summary>
    Release
}
