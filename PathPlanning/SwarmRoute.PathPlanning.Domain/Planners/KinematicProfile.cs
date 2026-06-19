namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// Kinematic limits for continuous-time (SIPPwRT) planning: top speed and acceleration, in the graph's integer
/// length units (the same "mm" as <c>RoadmapGraph.EdgeWeight = round(Distance·1000)</c>) per second and per
/// second². Supplied to <see cref="SippwrtPathPlanner"/> by construction; deliberately NOT placed on the agent
/// or <c>MapLine</c>, so the discrete planners (Dijkstra/SIPP) and their reservations stay byte-identical and no
/// cross-context schema changes are needed. One fleet-wide profile keeps edge durations a pure function of
/// <c>(topology, profile)</c> — reproducible, with no per-agent state to serialize.
/// </summary>
public sealed record KinematicProfile
{
    /// <summary>Top speed, in graph length units per second (e.g. 1000 = 1 m/s when 1 unit = 1 mm).</summary>
    public long VMaxMmPerS { get; }

    /// <summary>Acceleration (and deceleration) magnitude, in graph length units per second².</summary>
    public long AMaxMmPerS2 { get; }

    public KinematicProfile(long vMaxMmPerS, long aMaxMmPerS2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vMaxMmPerS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aMaxMmPerS2);
        VMaxMmPerS = vMaxMmPerS;
        AMaxMmPerS2 = aMaxMmPerS2;
    }

    /// <summary>1 m/s top speed, 1 m/s² acceleration (in mm units: 1000, 1000).</summary>
    public static KinematicProfile Default { get; } = new(1000, 1000);
}
