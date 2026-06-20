namespace SwarmRoute.Liveness.Domain.Resolution;

/// <summary>
/// An immutable snapshot of one congestion-cluster agent handed to <see cref="PibtZoneResolver"/>. Keeping the
/// resolver on this read-only view (rather than the driver's mutable run-state) makes it a pure function — and
/// lets the v3 host-seam lift it verbatim behind an <c>IJointStepPlanner</c> port.
/// </summary>
/// <param name="Id">Stable agent id.</param>
/// <param name="Cell">The control point the agent physically sits on this tick.</param>
/// <param name="Goal">The agent's effective goal (real goal or active redirect target).</param>
/// <param name="Priority">Right-of-way order (lower = higher priority); ties broken by ordinal id.</param>
/// <param name="HeldTicks">Consecutive ticks the agent has been forced to hold inside the current PIBT episode;
/// the most-waited agent is promoted in the processing order (anti-livelock), so this is part of the
/// deterministic ordering key, not wall-clock state.</param>
public readonly record struct PibtAgentView(string Id, string Cell, string Goal, int Priority, int HeldTicks);
