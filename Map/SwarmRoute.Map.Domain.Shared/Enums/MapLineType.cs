namespace SwarmRoute.Map.Domain.Shared.Enums;

/// <summary>
/// Geometry type of a roadmap line/segment. Ported from <c>AJR.Platform.GraphMapDP.MapLineType</c>.
/// </summary>
public enum MapLineType
{
    /// <summary>A straight line between its two endpoint sites.</summary>
    Straight = 0,

    /// <summary>A Bézier curve defined by the endpoint sites plus up to two control points.</summary>
    Bezier = 1
}
