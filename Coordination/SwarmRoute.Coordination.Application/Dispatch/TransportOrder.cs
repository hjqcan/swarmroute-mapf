namespace SwarmRoute.Coordination.Application.Dispatch;

/// <summary>
/// A transport order the dispatcher must fulfil: bring some vehicle to <see cref="DestinationSiteId"/>. The
/// OpenTCS analogue of a "transport order" reduced to its essentials for the v1 autonomous-loop skeleton (a
/// single destination; pick-up/drop sequences are a later elaboration).
/// </summary>
/// <param name="Id">Stable order id (assigned by the order book if not supplied).</param>
/// <param name="DestinationSiteId">The control point the assigned vehicle must reach.</param>
/// <param name="Priority">Lower value = dispatched sooner (ties broken by arrival order).</param>
public sealed record TransportOrder(string Id, string DestinationSiteId, int Priority = 0);
