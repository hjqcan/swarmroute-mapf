namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// The kind of physical roadmap resource a reservation can target.
/// Mirrors the AGV/AMR topology vocabulary: Control Point (站点), Lane (路段/边),
/// Block (互斥区块) and Zone (更大的分片/区域).
/// </summary>
public enum ResourceKind
{
    /// <summary>A control point / station / node (站点).</summary>
    CP,

    /// <summary>A directed lane / edge / segment between two control points (路段).</summary>
    Lane,

    /// <summary>A mutual-exclusion block grouping several resources (互斥区块).</summary>
    Block,

    /// <summary>A larger spatial zone used for sharding / regional contention (区域).</summary>
    Zone
}

/// <summary>
/// Stable, value-typed reference to a single roadmap resource.
/// This is part of the frozen cross-context Kernel contract — all bounded contexts
/// (Map / PathPlanning / TrafficControl / Deadlock) speak in terms of <see cref="ResourceRef"/>.
/// </summary>
/// <param name="Kind">The category of resource.</param>
/// <param name="Id">The opaque, context-stable identifier of the resource within its kind.</param>
public readonly record struct ResourceRef(ResourceKind Kind, string Id);
