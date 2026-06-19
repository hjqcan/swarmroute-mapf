namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// Continuous-time traversal duration of one edge under a trapezoidal (accelerate → cruise → decelerate) motion
/// profile, stop-to-stop (zero speed at each control point). This is <b>the single floating-point → integer
/// boundary</b> of the entire continuous-time path: the irrational closed-form is evaluated in <see cref="double"/>
/// and collapsed to integer milliseconds by one fixed rounding (<see cref="MidpointRounding.AwayFromZero"/>,
/// matching <c>RoadmapGraph.Build</c>'s <c>round(Distance·1000)</c>), so every value that reaches the
/// scheduling / reservation / conflict path is a deterministic <see cref="long"/>. The trapezoidal-vs-triangular
/// branch is decided in exact <see cref="decimal"/> so no float rounding can pick a different branch across
/// machines.
/// </summary>
public static class EdgeKinematics
{
    /// <summary>
    /// Integer-millisecond stop-to-stop traversal time of an edge of integer length <paramref name="lengthUnits"/>
    /// under <paramref name="profile"/>. Trapezoidal when the edge is long enough to reach <c>v_max</c>
    /// (<c>L ≥ v_max²/a_max</c>), else triangular (peak speed below <c>v_max</c>). Always ≥ 1 (a non-degenerate
    /// interval, mirroring the weight clamp in <c>RoadmapGraph.Build</c>).
    /// </summary>
    public static long DurationMs(long lengthUnits, KinematicProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (lengthUnits <= 0)
            return 1; // degenerate edge → minimal non-empty interval

        long vmax = profile.VMaxMmPerS;
        long amax = profile.AMaxMmPerS2;

        // Trapezoidal iff the edge reaches cruise: L ≥ v_max²/a_max  ⇔  L·a_max ≥ v_max².
        // Decided in exact decimal (no float deciding the branch, no long overflow) → machine-independent.
        var trapezoidal = (decimal)lengthUnits * amax >= (decimal)vmax * vmax;

        double seconds;
        if (trapezoidal)
        {
            // accel + decel ramps (each v_max/a_max) plus cruise over the remaining (L − v_max²/a_max).
            var tRamps = 2.0 * vmax / amax;
            var cruiseDist = lengthUnits - (double)vmax * vmax / amax;
            seconds = tRamps + cruiseDist / vmax;
        }
        else
        {
            // Triangular: peak speed sqrt(L·a_max); total time 2·sqrt(L/a_max).
            seconds = 2.0 * Math.Sqrt((double)lengthUnits / amax);
        }

        return Math.Max(1, (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero));
    }
}
