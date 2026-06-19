using SwarmRoute.PathPlanning.Domain.Planners;
using Xunit;

namespace SwarmRoute.PathPlanning.Tests.Sippwrt;

/// <summary>
/// Pins the single float→integer boundary of the continuous-time path: the trapezoidal/triangular closed-form
/// must yield exact, deterministic integer milliseconds, with the branch decided integer-exactly.
/// </summary>
public sealed class EdgeKinematicsTests
{
    private static readonly KinematicProfile P = KinematicProfile.Default; // v_max=1000, a_max=1000 (1 m/s, 1 m/s²)

    // L=3000 ≥ v²/a=1000 → trapezoidal: 2·(1) + (3000−1000)/1000 = 4.0 s.
    [Fact]
    public void Trapezoidal_is_exact() => Assert.Equal(4000, EdgeKinematics.DurationMs(3000, P));

    // L=250 < 1000 → triangular: 2·√(250/1000) = 2·0.5 = 1.0 s.
    [Fact]
    public void Triangular_is_exact() => Assert.Equal(1000, EdgeKinematics.DurationMs(250, P));

    // L=1000 exactly equals v²/a → trapezoidal branch (≥), cruise distance 0: 2.0 s.
    [Fact]
    public void Boundary_is_trapezoidal_with_zero_cruise() => Assert.Equal(2000, EdgeKinematics.DurationMs(1000, P));

    // A longer edge always takes longer (the property that makes continuous-time differ from min-hop discrete).
    [Fact]
    public void Longer_edge_takes_strictly_longer() =>
        Assert.True(EdgeKinematics.DurationMs(6000, P) > EdgeKinematics.DurationMs(3000, P));

    // Degenerate / sub-ms edges clamp to a non-empty interval.
    [Fact]
    public void Degenerate_edge_clamps_to_at_least_one_ms()
    {
        Assert.Equal(1, EdgeKinematics.DurationMs(0, P));
        Assert.True(EdgeKinematics.DurationMs(1, new KinematicProfile(1000, 1_000_000_000)) >= 1);
    }

    // The float→int boundary is stable: identical inputs ⇒ identical output.
    [Fact]
    public void Is_deterministic()
    {
        for (long l = 1; l <= 5000; l += 137)
            Assert.Equal(EdgeKinematics.DurationMs(l, P), EdgeKinematics.DurationMs(l, P));
    }

    [Fact]
    public void Rejects_non_positive_kinematics()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KinematicProfile(0, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KinematicProfile(1000, -1));
    }
}
