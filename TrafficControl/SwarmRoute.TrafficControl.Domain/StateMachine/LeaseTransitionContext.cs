using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Domain.StateMachine;

/// <summary>
/// The context object passed to lease-state guards / transitions: which resource, over which interval, for
/// which agent, against which live <see cref="ReservationTable"/>. Lets the guards consult availability,
/// conflicts and the blacklist without the state machine itself depending on those services directly.
/// </summary>
/// <param name="Table">The live reservation table the guard evaluates against.</param>
/// <param name="Resource">The resource the lease targets.</param>
/// <param name="Interval">The half-open window the lease covers.</param>
/// <param name="AgentId">The agent the lease belongs to.</param>
public sealed record LeaseTransitionContext(
    ReservationTable Table,
    ResourceRef Resource,
    TimeInterval Interval,
    string AgentId);
