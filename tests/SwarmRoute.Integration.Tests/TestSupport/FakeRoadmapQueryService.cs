using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Integration.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IRoadmapQueryService"/> backed by a <see cref="RoadmapGraph"/> built directly via
/// <see cref="RoadmapGraph.Build(IEnumerable{MapSite}, IEnumerable{MapLine})"/> — no EF / no Postgres. Lets the
/// integration tests exercise the REAL PathPlanning + TrafficControl + Coordination services against a known
/// topology without a database (the production <c>RoadmapGraphProvider</c> would read the repository).
/// </summary>
public sealed class FakeRoadmapQueryService : IRoadmapQueryService
{
    private readonly Dictionary<Guid, RoadmapGraph> _graphs;

    public FakeRoadmapQueryService(Guid roadmapId, RoadmapGraph graph)
        => _graphs = new Dictionary<Guid, RoadmapGraph> { [roadmapId] = graph };

    public Task<RoadmapGraph> GetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
        => _graphs.TryGetValue(roadmapId, out var g)
            ? Task.FromResult(g)
            : throw new KeyNotFoundException($"Roadmap '{roadmapId}' not found.");

    public Task<RoadmapGraph?> TryGetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
        => Task.FromResult(_graphs.TryGetValue(roadmapId, out var g) ? g : null);

    public void Invalidate(Guid roadmapId) => _graphs.Remove(roadmapId);

    /// <summary>
    /// Builds a simple undirected (bidirectional-edge) chain roadmap over the given ordered site ids:
    /// <c>A-B-C-D</c> becomes edges A↔B, B↔C, C↔D, each of unit distance. Sites are <see cref="MapSiteType.WorkSite"/>.
    /// </summary>
    public static RoadmapGraph Chain(params string[] siteIds)
    {
        var sites = siteIds
            .Select(id => new MapSite(id, MapSiteType.WorkSite, MapPosition.Empty))
            .ToList();

        var lines = new List<MapLine>();
        for (var i = 0; i < siteIds.Length - 1; i++)
        {
            var a = siteIds[i];
            var b = siteIds[i + 1];
            lines.Add(new MapLine($"{a}-{b}", a, b, distance: 1));
            lines.Add(new MapLine($"{b}-{a}", b, a, distance: 1));
        }

        return RoadmapGraph.Build(sites, lines);
    }

    /// <summary>
    /// Builds an arbitrary roadmap from a site list and undirected edges (each becomes a directed lane in both
    /// directions, unit distance). e.g. a "+" intersection: sites W,E,N,S,C0 with edges (W,C0),(C0,E),(N,C0),(C0,S).
    /// </summary>
    public static RoadmapGraph Graph(IReadOnlyList<string> siteIds, params (string A, string B)[] edges)
    {
        var sites = siteIds
            .Select(id => new MapSite(id, MapSiteType.WorkSite, MapPosition.Empty))
            .ToList();

        var lines = new List<MapLine>();
        foreach (var (a, b) in edges)
        {
            lines.Add(new MapLine($"{a}-{b}", a, b, distance: 1));
            lines.Add(new MapLine($"{b}-{a}", b, a, distance: 1));
        }

        return RoadmapGraph.Build(sites, lines);
    }

    /// <summary>
    /// Builds an arbitrary bidirectional roadmap with explicit edge distances.
    /// </summary>
    public static RoadmapGraph WeightedGraph(
        IReadOnlyList<string> siteIds,
        params (string A, string B, double Distance)[] edges)
    {
        var sites = siteIds
            .Select(id => new MapSite(id, MapSiteType.WorkSite, MapPosition.Empty))
            .ToList();

        var lines = new List<MapLine>();
        foreach (var (a, b, distance) in edges)
        {
            lines.Add(new MapLine($"{a}-{b}", a, b, distance));
            lines.Add(new MapLine($"{b}-{a}", b, a, distance));
        }

        return RoadmapGraph.Build(sites, lines);
    }
}
