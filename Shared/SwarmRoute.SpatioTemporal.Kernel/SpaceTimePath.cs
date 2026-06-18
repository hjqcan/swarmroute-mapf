namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// An ordered sequence of <see cref="SpaceTimeCell"/>s describing one agent's planned route through
/// space and time. Produced by PathPlanning and consumed by TrafficControl's <c>TryReserve</c>.
/// </summary>
/// <param name="Cells">The ordered cells of the path. Order is significant (entry → exit).</param>
public sealed record SpaceTimePath(IReadOnlyList<SpaceTimeCell> Cells);
