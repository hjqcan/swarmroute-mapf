using NetDevPack.Domain;

namespace SwarmRoute.PathPlanning.Domain.ValueObjects;

/// <summary>
/// The cost of a computed plan. v0 measures cost as the cumulative scaled edge weight along the route
/// (<see cref="DistanceUnits"/>, matching <c>RoadmapGraph.DistanceTo</c> / <c>round(Distance * 1000)</c>),
/// together with the number of hops (<see cref="HopCount"/>) and the planned travel duration in milliseconds
/// (<see cref="DurationMs"/>, which in v0 equals <see cref="DistanceUnits"/> since the timeline uses edge
/// weight as a proxy duration).
/// </summary>
public sealed class PlanCost : ValueObject
{
    /// <summary>
    /// Creates a plan cost.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any component is negative.</exception>
    public PlanCost(long distanceUnits, int hopCount, long durationMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distanceUnits);
        ArgumentOutOfRangeException.ThrowIfNegative(hopCount);
        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);

        DistanceUnits = distanceUnits;
        HopCount = hopCount;
        DurationMs = durationMs;
    }

    /// <summary>The zero cost (used for a trivial start == goal plan).</summary>
    public static PlanCost Zero { get; } = new(0, 0, 0);

    /// <summary>Cumulative scaled edge weight along the route (same units as <c>RoadmapGraph.DistanceTo</c>).</summary>
    public long DistanceUnits { get; }

    /// <summary>Number of edges (hops) traversed; a single-site plan has 0.</summary>
    public int HopCount { get; }

    /// <summary>Planned travel duration in fleet-clock milliseconds.</summary>
    public long DurationMs { get; }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return DistanceUnits;
        yield return HopCount;
        yield return DurationMs;
    }
}
