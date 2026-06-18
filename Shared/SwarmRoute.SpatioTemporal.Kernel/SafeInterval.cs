namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// A derived value: a maximal conflict-free window for a given resource (SIPP "safe interval").
/// Computed by TrafficControl and read-only for PathPlanning. Distinct record type from
/// <see cref="SpaceTimeCell"/> so the two never get confused at a contract boundary, even though
/// they carry the same shape.
/// </summary>
/// <param name="Resource">The roadmap resource the safe interval applies to.</param>
/// <param name="Interval">The half-open window during which the resource is free.</param>
public sealed record SafeInterval(ResourceRef Resource, TimeInterval Interval);
