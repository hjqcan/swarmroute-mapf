using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.PathPlanning.Tests.TestSupport;

/// <summary>
/// Tiny fluent helper to hand-build a <see cref="RoadmapGraph"/> from sites and directed lines, reusing the
/// Map context's real <see cref="RoadmapGraph.Build(IEnumerable{MapSite}, IEnumerable{MapLine})"/> (the same
/// vertex/edge construction the production graph uses). Distances are in metres; the resulting edge weight is
/// <c>round(distance * 1000)</c>, so a distance of <c>1.0</c> yields weight <c>1000</c>.
/// </summary>
internal sealed class RoadmapGraphBuilder
{
    private readonly Dictionary<string, MapSite> _sites = new(StringComparer.Ordinal);
    private readonly List<MapLine> _lines = new();
    private int _lineSeq;

    /// <summary>Adds a site (idempotent by id), defaulting to an enabled relay site at the origin.</summary>
    public RoadmapGraphBuilder Site(string siteId, bool enable = true)
    {
        _sites[siteId] = new MapSite(siteId, MapSiteType.RelaySite, MapPosition.Empty, enable);
        return this;
    }

    /// <summary>
    /// Adds a directed line <paramref name="from"/> → <paramref name="to"/> of the given metre
    /// <paramref name="distance"/> (default 1.0 → weight 1000). Endpoint sites are auto-created if absent.
    /// </summary>
    public RoadmapGraphBuilder Edge(string from, string to, double distance = 1.0)
    {
        if (!_sites.ContainsKey(from)) Site(from);
        if (!_sites.ContainsKey(to)) Site(to);

        _lines.Add(new MapLine($"L{_lineSeq++}", from, to, distance));
        return this;
    }

    /// <summary>Builds the immutable <see cref="RoadmapGraph"/>.</summary>
    public RoadmapGraph Build() => RoadmapGraph.Build(_sites.Values, _lines);
}
