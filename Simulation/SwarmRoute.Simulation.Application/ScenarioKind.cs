namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab — ScenarioBench) The map layout for a run. <see cref="Open"/> is the uniform grid (the
/// historical default, byte-identical); the others carve obstacles into it to create the non-uniform topologies a
/// warehouse actually has — where continuous-time, the congestion heatmap, and GuidanceGraph stop being inert and a
/// planner's traffic behaviour is actually exercised. Every preset keeps the free cells connected, so any goal stays
/// reachable.
/// </summary>
public enum ScenarioKind
{
    /// <summary>The open uniform grid — no obstacles (the default; byte-identical to the pre-ScenarioBench field).</summary>
    Open,

    /// <summary>A wall down the middle with a central gap: all cross-traffic must funnel through one narrow corridor —
    /// the classic congestion bottleneck where guidance / CBS / the heatmap show their value.</summary>
    Bottleneck,

    /// <summary>A regular lattice of single-cell pillars (warehouse shelves): free aisles on the even rows/columns,
    /// so routes wind around obstacles and hotspots form at the aisle intersections.</summary>
    Obstacles,
}
