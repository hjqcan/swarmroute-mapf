using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Infra.Data.Context.Persistence;

/// <summary>
/// Plain JSON-serialisable surrogate for <see cref="MapPosition"/> (whose private ctor / setters make it
/// unsuitable for direct <c>System.Text.Json</c> round-tripping). Used by the EF value converters.
/// </summary>
internal sealed record PositionJson(double X, double Y, double Angle)
{
    public static PositionJson From(MapPosition pos) => new(pos.X, pos.Y, pos.Angle);

    public MapPosition ToDomain() => new(X, Y, Angle);
}
