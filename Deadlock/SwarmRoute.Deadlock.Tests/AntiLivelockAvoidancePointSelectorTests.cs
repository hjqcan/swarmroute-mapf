using System.Collections.Generic;
using System.Linq;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Deadlock.Domain.Services;

namespace SwarmRoute.Deadlock.Tests;

public class AntiLivelockAvoidancePointSelectorTests
{
    /// <summary>Returns the first of its sites that is not excluded (a stand-in for a real multi-siding map).</summary>
    private sealed class ListAvoidancePointSelector(params string[] sites) : IAvoidancePointSelector
    {
        public string? SelectAvoidancePoint(string victimAgentId, IReadOnlySet<string>? excludedSiteIds = null)
            => sites.FirstOrDefault(s => excludedSiteIds is null || !excludedSiteIds.Contains(s));
    }

    [Fact]
    public void DoesNotPickTheSamePointTwiceInARow_WhenAnAlternativeExists()
    {
        var sut = new AntiLivelockAvoidancePointSelector(
            new ListAvoidancePointSelector("V1", "V2"), new AvoidancePointHistory());

        var first = sut.SelectAvoidancePoint("A");
        var second = sut.SelectAvoidancePoint("A");

        Assert.Equal("V1", first);
        Assert.Equal("V2", second);          // excluded the just-used V1
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FallsBackToRepeating_WhenItIsTheOnlyOption()
    {
        var sut = new AntiLivelockAvoidancePointSelector(
            new ListAvoidancePointSelector("V1"), new AvoidancePointHistory());

        Assert.Equal("V1", sut.SelectAvoidancePoint("A"));
        // Only one siding exists: excluding it would strand the victim, so the guard allows the repeat
        // (the driver's distance/attempt guard catches a genuine livelock instead).
        Assert.Equal("V1", sut.SelectAvoidancePoint("A"));
    }

    [Fact]
    public void TracksHistoryPerVictim_Independently()
    {
        var sut = new AntiLivelockAvoidancePointSelector(
            new ListAvoidancePointSelector("V1", "V2"), new AvoidancePointHistory());

        Assert.Equal("V1", sut.SelectAvoidancePoint("A"));
        Assert.Equal("V1", sut.SelectAvoidancePoint("B")); // B has its own history → still gets V1
        Assert.Equal("V2", sut.SelectAvoidancePoint("A")); // A avoids its own last (V1)
    }
}
