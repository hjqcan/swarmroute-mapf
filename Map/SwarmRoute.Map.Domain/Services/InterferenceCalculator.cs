using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Domain.Services;

/// <summary>
/// Default radius-overlap interference calculator. Ported from
/// <c>AJR.Platform.GraphMapDP.InterferenceResourceDetection.SiteInterferenceSiteDetection</c>.
/// <para>
/// The original returned <c>false</c> when the centre distance <c>dp</c> satisfied
/// <c>|r1 - r2| &lt; dp &lt; r1 + r2</c> (i.e. the circles partially overlap) and <c>true</c> otherwise — a
/// confusingly inverted predicate. Here the boolean is normalised: <see cref="AreInterfering"/> returns
/// <c>true</c> exactly when the footprints partially overlap.
/// </para>
/// </summary>
public sealed class InterferenceCalculator : IInterferenceCalculator
{
    /// <inheritdoc />
    public bool AreInterfering(MapPosition a, double radiusA, MapPosition b, double radiusB)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));

        // Partial overlap: circles cross each other's boundary. Touching/containment/disjoint are NOT interference.
        return distance > Math.Abs(radiusA - radiusB) && distance < radiusA + radiusB;
    }

    /// <inheritdoc />
    public InterferenceSet ComputeSiteInterference(IReadOnlyCollection<MapSite> sites, double footprintRadius)
    {
        ArgumentNullException.ThrowIfNull(sites);

        var list = sites.ToList();
        var pairs = new List<(string, string)>();

        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                if (AreInterfering(list[i].Pos, footprintRadius, list[j].Pos, footprintRadius))
                    pairs.Add((list[i].SiteId, list[j].SiteId));
            }
        }

        return InterferenceSet.FromPairs(pairs);
    }
}
