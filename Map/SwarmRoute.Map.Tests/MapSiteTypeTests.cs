using SwarmRoute.Map.Domain.Shared.Enums;

namespace SwarmRoute.Map.Tests;

public sealed class MapSiteTypeTests
{
    [Fact]
    public void All_enum_values_are_distinct()
    {
        // The original AJR enum had RelaySite = 3 and AvoidSite = 3 (duplicate), with DockSite = 4
        // colliding. This guards the fix: every member maps to a unique underlying value.
        var values = Enum.GetValues<MapSiteType>().Select(v => (int)v).ToArray();
        var distinct = values.Distinct().ToArray();

        Assert.Equal(values.Length, distinct.Length);
    }

    [Fact]
    public void Avoid_and_relay_and_dock_are_separated()
    {
        Assert.NotEqual((int)MapSiteType.RelaySite, (int)MapSiteType.AvoidSite);
        Assert.NotEqual((int)MapSiteType.AvoidSite, (int)MapSiteType.DockSite);
        Assert.NotEqual((int)MapSiteType.RelaySite, (int)MapSiteType.DockSite);
    }

    [Fact]
    public void Names_round_trip_distinctly()
    {
        var names = Enum.GetNames<MapSiteType>();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Contains("AvoidSite", names);
        Assert.Contains("DockSite", names);
    }
}
