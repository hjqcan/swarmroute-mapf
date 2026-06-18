namespace SwarmRoute.Map.Domain.Shared.Enums;

/// <summary>
/// Static lifecycle/availability status of a roadmap resource as far as the Map context is concerned.
/// <para>
/// Ported from the original <c>AJR.MAPF.Map.MapResourceStaus</c> (note: original spelling "Staus").
/// In the v0 architecture the <em>dynamic</em> occupancy states (<see cref="Locked"/> / <see cref="Belong"/>)
/// are owned by the TrafficControl context's reservation table; Map only retains the static topology
/// (<see cref="Enums.MapResourceStatus.Unlocked"/> = available, <see cref="Unable"/> = disabled). The full
/// enum is preserved here for fidelity with the source and for mapping/import compatibility.
/// </para>
/// </summary>
public enum MapResourceStatus
{
    /// <summary>Resource is locked (reserved). Dynamic state — authoritative copy lives in TrafficControl.</summary>
    Locked = 1,

    /// <summary>Resource is occupied/owned by a vehicle. Dynamic state — authoritative copy lives in TrafficControl.</summary>
    Belong = 2,

    /// <summary>Resource is unlocked and available (the default static state).</summary>
    Unlocked = 3,

    /// <summary>Resource is permanently unavailable (excluded from the graph).</summary>
    Unable = 4
}
