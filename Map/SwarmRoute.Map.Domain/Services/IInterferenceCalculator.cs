using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Domain.Services;

/// <summary>
/// Domain service that detects footprint interference (radius overlap) between roadmap resources.
/// Ported from <c>AJR.Platform.GraphMapDP.InterferenceResourceDetection</c>.
/// </summary>
public interface IInterferenceCalculator
{
    /// <summary>
    /// True when two circular footprints centred at <paramref name="a"/> / <paramref name="b"/> with radii
    /// <paramref name="radiusA"/> / <paramref name="radiusB"/> overlap (partial overlap — neither fully contains
    /// the other and they are not disjoint). Mirrors the original detection logic.
    /// </summary>
    bool AreInterfering(MapPosition a, double radiusA, MapPosition b, double radiusB);

    /// <summary>
    /// Computes a symmetric <see cref="InterferenceSet"/> over the given sites by pairwise footprint overlap,
    /// using a single shared <paramref name="footprintRadius"/> for every site.
    /// </summary>
    InterferenceSet ComputeSiteInterference(IReadOnlyCollection<MapSite> sites, double footprintRadius);
}
