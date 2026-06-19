namespace SwarmRoute.PathPlanning.Domain.Cbs;

/// <summary>
/// One agent in a local CBS (Conflict-Based Search) solve: route <see cref="Id"/> from <see cref="Start"/> to
/// <see cref="Goal"/>. Constructible 1:1 from the executor's congestion-cluster view (id, current cell → Start,
/// effective goal → Goal, priority). A value type so the solver's inputs are immutable and order-stable.
/// </summary>
/// <param name="Id">Stable agent id (ties broken ordinally throughout the solver for determinism).</param>
/// <param name="Start">The agent's start control point.</param>
/// <param name="Goal">The agent's goal control point (or a bounded frontier under a horizon).</param>
/// <param name="Priority">Right-of-way order; carried for parity with the rest of the system (CBS itself orders by id).</param>
public readonly record struct CbsAgent(string Id, string Start, string Goal, int Priority);
