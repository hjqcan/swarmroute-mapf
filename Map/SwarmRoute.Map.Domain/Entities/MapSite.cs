using NetDevPack.Domain;
using SwarmRoute.Map.Domain.Shared;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Domain.Entities;

/// <summary>
/// A roadmap site / station (站点). An entity WITHIN the <see cref="Aggregates.Roadmap"/> aggregate —
/// it is not an aggregate root and is only ever mutated through its owning <see cref="Aggregates.Roadmap"/>.
/// <para>
/// Ported from <c>AJR.MAPF.Map.MapSite</c> / <c>AJR.Platform.GraphMapDP.MapSite</c>, keeping only the
/// <em>static</em> topology (type, pose, enabled flag, interference references). The dynamic occupancy
/// state (<c>Locked</c>/<c>OccupiedBy</c>) deliberately does NOT live here — TrafficControl owns it.
/// </para>
/// </summary>
public class MapSite : Entity
{
    private readonly List<string> _interferenceSiteIds = new();
    private readonly List<string> _interferenceLineIds = new();

    // EF Core
    private MapSite()
    {
        SiteId = string.Empty;
        Pos = MapPosition.Empty;
    }

    /// <summary>
    /// Creates a site. <paramref name="siteId"/> is the topology-stable key (e.g. "A", "S1") used by lines
    /// and the graph; it is distinct from the EF surrogate <see cref="Entity.Id"/>.
    /// </summary>
    /// <param name="siteRole">
    /// FMS dispatch role of the site (additive per ADR-F1). Defaults to <see cref="Enums.SiteRole.Transit"/>
    /// so all existing call-sites and persisted rows behave exactly as before.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="siteId"/> is null/whitespace.</exception>
    public MapSite(
        string siteId,
        MapSiteType siteType,
        MapPosition pos,
        bool enable = true,
        IEnumerable<string>? interferenceSiteIds = null,
        IEnumerable<string>? interferenceLineIds = null,
        SiteRole siteRole = SiteRole.Transit)
    {
        if (string.IsNullOrWhiteSpace(siteId))
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Site id must not be empty.", nameof(siteId));

        SiteId = siteId.Trim();
        SiteType = siteType;
        Pos = pos ?? MapPosition.Empty;
        Enable = enable;
        SiteRole = siteRole;

        if (interferenceSiteIds is not null)
            _interferenceSiteIds.AddRange(interferenceSiteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
        if (interferenceLineIds is not null)
            _interferenceLineIds.AddRange(interferenceLineIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
    }

    /// <summary>Topology-stable site identifier (vertex id in the graph).</summary>
    public string SiteId { get; private set; }

    /// <summary>Functional classification of the site.</summary>
    public MapSiteType SiteType { get; private set; }

    /// <summary>
    /// FMS dispatch role of the site (additive; complements <see cref="SiteType"/>). Defaults to
    /// <see cref="Enums.SiteRole.Transit"/>.
    /// </summary>
    public SiteRole SiteRole { get; private set; }

    /// <summary>Pose (position + heading) of the site.</summary>
    public MapPosition Pos { get; private set; }

    /// <summary>Convenience accessor for the site heading (degrees).</summary>
    public double Angle => Pos.Angle;

    /// <summary>Whether the site participates in the graph (disabled sites are excluded from vertices).</summary>
    public bool Enable { get; private set; }

    /// <summary>Ids of sites whose footprint interferes with this site's footprint.</summary>
    public IReadOnlyCollection<string> InterferenceSiteIds => _interferenceSiteIds.AsReadOnly();

    /// <summary>Ids of lines whose footprint interferes with this site's footprint.</summary>
    public IReadOnlyCollection<string> InterferenceLineIds => _interferenceLineIds.AsReadOnly();

    /// <summary>Enables or disables the site (does not increment aggregate version; callers go through Roadmap).</summary>
    internal void SetEnabled(bool enable) => Enable = enable;

    /// <summary>Replaces the interference site-id set.</summary>
    internal void SetInterferenceSites(IEnumerable<string> siteIds)
    {
        _interferenceSiteIds.Clear();
        _interferenceSiteIds.AddRange(siteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
    }

    /// <summary>Replaces the interference line-id set.</summary>
    internal void SetInterferenceLines(IEnumerable<string> lineIds)
    {
        _interferenceLineIds.Clear();
        _interferenceLineIds.AddRange(lineIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
    }
}
