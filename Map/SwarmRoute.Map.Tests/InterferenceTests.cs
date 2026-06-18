using SwarmRoute.Map.Domain.Services;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Tests;

public sealed class InterferenceTests
{
    private readonly IInterferenceCalculator _calc = new InterferenceCalculator();

    [Fact]
    public void Overlapping_circles_interfere()
    {
        // Centres 1 apart, each radius 1 → |r1-r2|=0 < 1 < r1+r2=2 → partial overlap.
        var a = new MapPosition(0, 0);
        var b = new MapPosition(1, 0);
        Assert.True(_calc.AreInterfering(a, 1.0, b, 1.0));
    }

    [Fact]
    public void Disjoint_circles_do_not_interfere()
    {
        // Centres 5 apart, radii 1 each → 5 > 2 → disjoint.
        var a = new MapPosition(0, 0);
        var b = new MapPosition(5, 0);
        Assert.False(_calc.AreInterfering(a, 1.0, b, 1.0));
    }

    [Fact]
    public void Fully_contained_circle_does_not_interfere()
    {
        // Same centre, radii 1 and 3 → distance 0, |r1-r2|=2 → 0 is NOT > 2 → containment, not partial overlap.
        var a = new MapPosition(0, 0);
        Assert.False(_calc.AreInterfering(a, 1.0, a, 3.0));
    }

    [Fact]
    public void Touching_circles_do_not_interfere()
    {
        // Centres exactly r1+r2 apart → boundary touch, strict inequality excludes it.
        var a = new MapPosition(0, 0);
        var b = new MapPosition(2, 0);
        Assert.False(_calc.AreInterfering(a, 1.0, b, 1.0));
    }

    [Fact]
    public void ComputeSiteInterference_builds_symmetric_set_for_close_sites()
    {
        var sites = new[]
        {
            Builders.Site("A", 0, 0),
            Builders.Site("B", 1, 0),   // 1 from A → overlaps with radius 1
            Builders.Site("C", 10, 0)   // far away → no overlap
        };

        var set = _calc.ComputeSiteInterference(sites, footprintRadius: 1.0);

        Assert.True(set.Interfere("A", "B"));
        Assert.True(set.Interfere("B", "A")); // symmetric
        Assert.False(set.Interfere("A", "C"));
        Assert.Contains("B", set.InterferingWith("A"));
        Assert.DoesNotContain("C", set.InterferingWith("A"));
    }

    [Fact]
    public void InterferenceSet_from_pairs_is_symmetric_and_ignores_self()
    {
        var set = InterferenceSet.FromPairs(new[] { ("A", "B"), ("X", "X") });

        Assert.True(set.Interfere("A", "B"));
        Assert.True(set.Interfere("B", "A"));
        Assert.Empty(set.InterferingWith("X")); // self-pair ignored
    }
}
