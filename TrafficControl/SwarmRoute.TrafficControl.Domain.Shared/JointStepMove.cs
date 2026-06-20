namespace SwarmRoute.TrafficControl.Domain.Shared;

/// <summary>
/// One agent's single-hop intent within a <b>joint step</b>: it moves from <see cref="FromSiteId"/> to
/// <see cref="ToSiteId"/> this tick (a "hold" when the two are equal). Produced by the zone-local joint resolver
/// (PIBT) and consumed by <c>ReservationTable.TryGrantJointStep</c> (via the traffic write seam), which reserves
/// each mover's destination control point — and the lane it traverses, when it actually moves — over the one-step
/// window, atomically for the whole batch. Making the reservation table the single authority for the step is what
/// seals cross-tick soundness when the resolver is driven from the autonomous host loop. Lives in Domain.Shared so
/// the write-seam contract and the aggregate share one type.
/// </summary>
/// <param name="AgentId">The moving agent.</param>
/// <param name="FromSiteId">The control point the agent currently holds.</param>
/// <param name="ToSiteId">The control point the agent enters this tick (equal to <see cref="FromSiteId"/> to hold).</param>
public readonly record struct JointStepMove(string AgentId, string FromSiteId, string ToSiteId);
