using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Tests;

public sealed class MapPositionTests
{
    [Fact]
    public void Empty_is_origin_with_zero_heading()
    {
        Assert.Equal(0, MapPosition.Empty.X);
        Assert.Equal(0, MapPosition.Empty.Y);
        Assert.Equal(0, MapPosition.Empty.Angle);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var a = new MapPosition(1, 2, 90);
        var b = new MapPosition(1, 2, 90);
        var c = new MapPosition(1, 2, 91);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DistanceTo_is_planar_euclidean()
    {
        var a = new MapPosition(0, 0, 0);
        var b = new MapPosition(3, 4, 123); // heading ignored
        Assert.Equal(5.0, a.DistanceTo(b), precision: 9);
    }
}
