using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The deterministic <see cref="IParkingManager"/>: nearest-reachable resting-site selection and greedy
/// path-clearing relocations over a <see cref="RoadmapGraph"/> snapshot (停車與重定位管理器).
/// <para>
/// Distance is the graph's shortest-path cost (<see cref="RoadmapGraph.DistanceTo"/>); ties break on the
/// ordinal-least site id so selection is reproducible. Nothing is reserved or mutated — both methods are pure
/// reads of the supplied occupancy and role maps.
/// </para>
/// </summary>
public sealed class ParkingManager : IParkingManager
{
    /// <inheritdoc />
    public string? AssignParking(
        string currentSite,
        RoadmapGraph graph,
        IReadOnlySet<string> occupiedOrParked,
        IReadOnlyDictionary<string, SiteRole> siteRoles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSite);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(occupiedOrParked);
        ArgumentNullException.ThrowIfNull(siteRoles);

        // Prefer a parking slot; only if none is reachable and free fall back to a staging buffer.
        return NearestByRole(currentSite, graph, occupiedOrParked, siteRoles, SiteRole.Parking)
            ?? NearestByRole(currentSite, graph, occupiedOrParked, siteRoles, SiteRole.Buffer);
    }

    /// <inheritdoc />
    public IReadOnlyList<ParkingRelocation> FindRelocationsForWalledAgent(
        string walledAgentSite,
        string goalSite,
        RoadmapGraph graph,
        IReadOnlyDictionary<string, string> parkedBySite,
        IReadOnlySet<string> occupiedOrParked,
        IReadOnlyDictionary<string, SiteRole> siteRoles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walledAgentSite);
        ArgumentException.ThrowIfNullOrWhiteSpace(goalSite);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(parkedBySite);
        ArgumentNullException.ThrowIfNull(occupiedOrParked);
        ArgumentNullException.ThrowIfNull(siteRoles);

        var path = graph.ShortestPath(walledAgentSite, goalSite);
        if (path is null || path.Count == 0)
            return [];

        var relocations = new List<ParkingRelocation>();

        // As blockers are reassigned we claim their target buffers so two blockers never pick the same slot, and
        // we free the blocker's old site (it is leaving it) so a later blocker may legitimately target it.
        var claimed = new HashSet<string>(occupiedOrParked, StringComparer.Ordinal);

        foreach (var site in path)
        {
            // The walled agent itself sits on the path origin; never relocate "itself".
            if (string.Equals(site, walledAgentSite, StringComparison.Ordinal))
                continue;

            if (!parkedBySite.TryGetValue(site, out var parkedAgent) || string.IsNullOrWhiteSpace(parkedAgent))
                continue;

            // The blocker is about to vacate `site`, so it is a candidate target for a later blocker.
            claimed.Remove(site);

            var buffer = NearestYieldBuffer(site, graph, claimed, siteRoles, goalSite, path);
            if (buffer is null)
            {
                // No free buffer for this blocker; restore its occupancy and skip it (greedy, best-effort).
                claimed.Add(site);
                continue;
            }

            claimed.Add(buffer);
            relocations.Add(new ParkingRelocation(parkedAgent, site, buffer));
        }

        return relocations;
    }

    /// <summary>
    /// The nearest free site of <paramref name="role"/> reachable from <paramref name="origin"/> by shortest-path
    /// cost, excluding the origin itself and any occupied site; ties break ordinal-least. <see langword="null"/>
    /// when none qualifies.
    /// </summary>
    private static string? NearestByRole(
        string origin,
        RoadmapGraph graph,
        IReadOnlySet<string> occupied,
        IReadOnlyDictionary<string, SiteRole> siteRoles,
        SiteRole role)
    {
        string? best = null;
        long bestDistance = long.MaxValue;

        // Deterministic scan: candidates in ordinal order, strict-less on distance keeps the first (ordinal-least)
        // of any tie.
        foreach (var site in siteRoles.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (siteRoles[site] != role)
                continue;
            if (string.Equals(site, origin, StringComparison.Ordinal) || occupied.Contains(site))
                continue;
            if (!graph.HasSite(site))
                continue;

            var distance = graph.DistanceTo(origin, site);
            if (distance is not { } d || d >= bestDistance)
                continue;

            bestDistance = d;
            best = site;
        }

        return best;
    }

    /// <summary>
    /// The nearest free buffer (then parking) a blocker at <paramref name="blockerSite"/> can yield to: closest by
    /// shortest-path cost, not occupied, not on the walled agent's <paramref name="path"/>, and not the
    /// <paramref name="goalSite"/>. Buffers are preferred; a parking slot is accepted only when no buffer is free.
    /// </summary>
    private static string? NearestYieldBuffer(
        string blockerSite,
        RoadmapGraph graph,
        IReadOnlySet<string> occupied,
        IReadOnlyDictionary<string, SiteRole> siteRoles,
        string goalSite,
        IReadOnlyList<string> path)
    {
        var offPath = new HashSet<string>(occupied, StringComparer.Ordinal) { goalSite };
        foreach (var site in path)
            offPath.Add(site);

        return NearestByRole(blockerSite, graph, offPath, siteRoles, SiteRole.Buffer)
            ?? NearestByRole(blockerSite, graph, offPath, siteRoles, SiteRole.Parking);
    }
}
