using NetDevPack.Domain;

namespace SwarmRoute.Map.Domain.ValueObjects;

/// <summary>
/// A 2D pose on the roadmap plane: planar position (<see cref="X"/>, <see cref="Y"/>) plus heading
/// <see cref="Angle"/> (degrees). Consolidates the original <c>MapPos</c> (X/Y only) with the per-site
/// <c>Angle</c> that the original engine stored separately.
/// </summary>
public sealed class MapPosition : ValueObject
{
    // EF Core
    private MapPosition()
    {
        X = 0;
        Y = 0;
        Angle = 0;
    }

    /// <summary>Creates a pose at (<paramref name="x"/>, <paramref name="y"/>) with heading <paramref name="angle"/>.</summary>
    public MapPosition(double x, double y, double angle = 0)
    {
        X = x;
        Y = y;
        Angle = angle;
    }

    /// <summary>The all-zero pose, used as a default / placeholder.</summary>
    public static MapPosition Empty { get; } = new(0, 0, 0);

    /// <summary>Planar X coordinate.</summary>
    public double X { get; private set; }

    /// <summary>Planar Y coordinate.</summary>
    public double Y { get; private set; }

    /// <summary>Heading angle in degrees.</summary>
    public double Angle { get; private set; }

    /// <summary>Euclidean (planar) distance to <paramref name="other"/>, ignoring heading.</summary>
    public double DistanceTo(MapPosition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return X;
        yield return Y;
        yield return Angle;
    }
}
