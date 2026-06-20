using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Application.Contract;

/// <summary>
/// The FMS-V2/V3 parking-and-relocation helper seam: picks where an idle vehicle should rest, and which parked
/// vehicles must yield to free a walled agent's path (停車與重定位管理器).
/// <para>
/// Both operations are pure, deterministic reads over a <see cref="RoadmapGraph"/> snapshot plus the current
/// occupancy and site-role maps; nothing is reserved or mutated. They are the dispatch-side inputs the executor
/// turns into movement.
/// </para>
/// </summary>
public interface IParkingManager
{
    /// <summary>
    /// Finds the nearest reachable resting site for a vehicle at <paramref name="currentSite"/>: the closest
    /// free <see cref="SiteRole.Parking"/> by <see cref="RoadmapGraph.ShortestPath"/> distance, falling back to
    /// the closest free <see cref="SiteRole.Buffer"/> when no parking is reachable.
    /// </summary>
    /// <param name="currentSite">The vehicle's current site (the search origin).</param>
    /// <param name="graph">The roadmap distances are measured on.</param>
    /// <param name="occupiedOrParked">Site ids already taken by other vehicles; never returned as a target.</param>
    /// <param name="siteRoles">The FMS role of each site, used to find parkings and buffers.</param>
    /// <returns>The chosen resting site id, or <see langword="null"/> when none is reachable and free.</returns>
    string? AssignParking(
        string currentSite,
        RoadmapGraph graph,
        IReadOnlySet<string> occupiedOrParked,
        IReadOnlyDictionary<string, SiteRole> siteRoles);

    /// <summary>
    /// For a vehicle "walled in" behind parked vehicles on its way to <paramref name="goalSite"/>, returns the
    /// greedy set of relocations that frees its shortest path: each parked blocker on the path paired with the
    /// nearest free buffer it can yield to.
    /// </summary>
    /// <param name="walledAgentSite">The blocked vehicle's current site (path origin).</param>
    /// <param name="goalSite">The blocked vehicle's goal site (path destination).</param>
    /// <param name="graph">The roadmap the path and buffer distances are computed on.</param>
    /// <param name="parkedBySite">Map of occupied site id → the parked vehicle id resting there.</param>
    /// <param name="occupiedOrParked">All taken site ids; relocation targets are chosen outside this set.</param>
    /// <param name="siteRoles">The FMS role of each site, used to find yield buffers.</param>
    /// <returns>
    /// An ordered, deterministic list of <see cref="ParkingRelocation"/>s — empty when the path is clear, when
    /// no path exists, or when no blocker can be given a free buffer.
    /// </returns>
    IReadOnlyList<ParkingRelocation> FindRelocationsForWalledAgent(
        string walledAgentSite,
        string goalSite,
        RoadmapGraph graph,
        IReadOnlyDictionary<string, string> parkedBySite,
        IReadOnlySet<string> occupiedOrParked,
        IReadOnlyDictionary<string, SiteRole> siteRoles);
}
