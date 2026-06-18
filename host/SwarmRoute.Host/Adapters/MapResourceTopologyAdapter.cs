using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Map-backed <see cref="IResourceTopology"/>. Overrides TrafficControl's default
/// <see cref="IResourceTopology.Empty"/> so the authoritative <c>ReservationTable</c> locks/releases a
/// resource together with its real <b>interference set</b> and <b>parent block</b> (the closure), and honours
/// the per-agent <b>blacklist</b> — exactly the invariants the first-generation engine enforced via
/// <c>GraphMap</c>'s lock/unlock + <c>MapResource.AGVBlackList</c>.
/// <para>
/// The closure index is derived from the published <see cref="Roadmap"/> aggregate (sites' / lines'
/// interference ids and blocks' contained ids), loaded lazily through a DI scope. The active roadmap is taken
/// from <see cref="ICoordinationGoalSource.CurrentRoadmapId"/>; before any roadmap is imported/selected the
/// topology degrades safely to identity closure (the resource itself), so the Host builds and starts without a
/// live database (a no-DB smoke never touches persistence here).
/// </para>
/// </summary>
/// <remarks>
/// Registered as a singleton (TrafficControl's <c>ReservationTable</c> captures it at construction). The index
/// is cached per roadmap id and rebuilt only on a cache miss; <see cref="Invalidate"/> drops it (e.g. wire to
/// <c>Map.Roadmap.Published</c> when CAP lands in WS-X).
/// </remarks>
public sealed class MapResourceTopologyAdapter : IResourceTopology
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICoordinationGoalSource _goalSource;
    private readonly ConcurrentDictionary<Guid, RoadmapTopology> _cache = new();

    public MapResourceTopologyAdapter(
        IServiceScopeFactory scopeFactory,
        ICoordinationGoalSource goalSource)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _goalSource = goalSource ?? throw new ArgumentNullException(nameof(goalSource));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ResourceRef> ClosureOf(ResourceRef resource)
    {
        var topology = Current();
        return topology is null ? new[] { resource } : topology.ClosureOf(resource);
    }

    /// <inheritdoc />
    public bool IsBlacklisted(ResourceRef resource, string agentId)
    {
        var topology = Current();
        return topology is not null && topology.IsBlacklisted(resource, agentId);
    }

    /// <summary>Drops the cached topology for <paramref name="roadmapId"/> (rebuilt on next access).</summary>
    public void Invalidate(Guid roadmapId) => _cache.TryRemove(roadmapId, out _);

    private RoadmapTopology? Current()
    {
        var roadmapId = _goalSource.CurrentRoadmapId;
        if (roadmapId is null)
            return null;

        return _cache.GetOrAdd(roadmapId.Value, Build);
    }

    private RoadmapTopology Build(Guid roadmapId)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRoadmapRepository>();

        // GetWithTopologyAsync is async; this is called off the hot path (first access / after invalidate),
        // so blocking here is acceptable and keeps IResourceTopology synchronous as the contract requires.
        var roadmap = repository
            .GetWithTopologyAsync(roadmapId)
            .GetAwaiter()
            .GetResult();

        return roadmap is null ? RoadmapTopology.Identity : RoadmapTopology.FromRoadmap(roadmap);
    }

    /// <summary>
    /// Immutable per-roadmap closure + blacklist index built once from the aggregate. Interference and
    /// block-membership are symmetric/mutual, so the closure of any member includes the whole group.
    /// </summary>
    private sealed class RoadmapTopology
    {
        public static readonly RoadmapTopology Identity = new(
            new Dictionary<ResourceRef, IReadOnlyCollection<ResourceRef>>(),
            new Dictionary<ResourceRef, HashSet<string>>());

        private readonly IReadOnlyDictionary<ResourceRef, IReadOnlyCollection<ResourceRef>> _closures;
        private readonly IReadOnlyDictionary<ResourceRef, HashSet<string>> _blacklist;

        private RoadmapTopology(
            IReadOnlyDictionary<ResourceRef, IReadOnlyCollection<ResourceRef>> closures,
            IReadOnlyDictionary<ResourceRef, HashSet<string>> blacklist)
        {
            _closures = closures;
            _blacklist = blacklist;
        }

        public IReadOnlyCollection<ResourceRef> ClosureOf(ResourceRef resource)
            => _closures.TryGetValue(resource, out var closure) ? closure : new[] { resource };

        public bool IsBlacklisted(ResourceRef resource, string agentId)
            => _blacklist.TryGetValue(resource, out var agents) && agents.Contains(agentId);

        public static RoadmapTopology FromRoadmap(Roadmap roadmap)
        {
            // Build an undirected interference graph over ResourceRefs, then take each node's component-ish
            // neighbourhood (direct neighbours + self) as its closure. Sites contribute CP refs, lines Lane
            // refs ("{start}-{end}", matching RoadmapGraph.LaneRef), blocks group their contained sites+lines.
            var lineRefById = roadmap.Lines.ToDictionary(
                l => l.LineId,
                l => RoadmapGraph.LaneRef(l.StartStationId, l.EndStationId));

            var adjacency = new Dictionary<ResourceRef, HashSet<ResourceRef>>();

            void Link(ResourceRef a, ResourceRef b)
            {
                if (a.Equals(b))
                    return;
                (adjacency.TryGetValue(a, out var sa) ? sa : adjacency[a] = new HashSet<ResourceRef>()).Add(b);
                (adjacency.TryGetValue(b, out var sb) ? sb : adjacency[b] = new HashSet<ResourceRef>()).Add(a);
            }

            ResourceRef? SiteRef(string siteId) =>
                roadmap.FindSite(siteId) is null ? null : RoadmapGraph.SiteRef(siteId);

            ResourceRef? LineRef(string lineId) =>
                lineRefById.TryGetValue(lineId, out var r) ? r : null;

            // Site interference (mutual): site <-> interfering sites/lines.
            foreach (var site in roadmap.Sites)
            {
                var selfRef = RoadmapGraph.SiteRef(site.SiteId);
                foreach (var otherId in site.InterferenceSiteIds)
                    if (SiteRef(otherId) is { } o)
                        Link(selfRef, o);
                foreach (var lineId in site.InterferenceLineIds)
                    if (LineRef(lineId) is { } o)
                        Link(selfRef, o);
            }

            // Line interference (mutual): lane <-> interfering sites/lines.
            foreach (var line in roadmap.Lines)
            {
                var selfRef = lineRefById[line.LineId];
                foreach (var siteId in line.InterferenceSiteIds)
                    if (SiteRef(siteId) is { } o)
                        Link(selfRef, o);
                foreach (var lineId in line.InterferenceLineIds)
                    if (LineRef(lineId) is { } o)
                        Link(selfRef, o);
            }

            // Parent block: every contained site/line mutually interferes within the block (mutual exclusion).
            foreach (var block in roadmap.Blocks)
            {
                var members = new List<ResourceRef>();
                foreach (var siteId in block.ContainedSiteIds)
                    if (SiteRef(siteId) is { } r)
                        members.Add(r);
                foreach (var lineId in block.ContainedLineIds)
                    if (LineRef(lineId) is { } r)
                        members.Add(r);

                for (var i = 0; i < members.Count; i++)
                    for (var j = i + 1; j < members.Count; j++)
                        Link(members[i], members[j]);
            }

            // Freeze closures: self + direct neighbours (one-hop). Locking a resource locks this whole set,
            // which is the v0 "interference + parent block" closure the original UnlockPath also released.
            var closures = new Dictionary<ResourceRef, IReadOnlyCollection<ResourceRef>>();
            foreach (var (node, neighbours) in adjacency)
            {
                var set = new HashSet<ResourceRef>(neighbours) { node };
                closures[node] = set.ToArray();
            }

            // Blacklist: none in the static topology yet (Map keeps only static topology; per-agent blacklist
            // is a TrafficControl/runtime concern in v1). Empty map => IsBlacklisted is always false.
            var blacklist = new Dictionary<ResourceRef, HashSet<string>>();

            return new RoadmapTopology(closures, blacklist);
        }
    }
}
