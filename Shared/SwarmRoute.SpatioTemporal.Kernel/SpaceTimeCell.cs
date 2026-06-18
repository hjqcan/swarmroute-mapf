namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// A single space-time cell: one roadmap <see cref="ResourceRef"/> occupied over one half-open
/// <see cref="TimeInterval"/>. This is the core object that flows between planning and reservation —
/// the layer the first-generation engine was missing.
/// </summary>
/// <param name="Resource">The roadmap resource this cell occupies.</param>
/// <param name="Interval">The half-open time window of the occupation.</param>
public sealed record SpaceTimeCell(ResourceRef Resource, TimeInterval Interval);
