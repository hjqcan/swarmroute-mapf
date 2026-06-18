using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.TrafficControl.Application.Topology;

/// <summary>
/// A data-driven <see cref="IResourceTopology"/> backed by plain dictionaries: a per-resource closure map
/// (resource → its parent block + interference members) and a per-agent blacklist. The Host populates it from
/// the Map context's published topology (each <c>MapSite</c>/<c>MapLine</c>'s <c>InterferenceSiteIds</c> /
/// <c>InterferenceLineIds</c>, each block's <c>ContainedSiteIds</c>/<c>ContainedLineIds</c>, and the
/// <c>AGVBlackList</c>). Kept here (Application) rather than in the Domain so TrafficControl.Domain stays free
/// of any Map dependency; v0 may run with <see cref="IResourceTopology.Empty"/> instead.
/// </summary>
/// <remarks>
/// The closure for a resource always includes the resource itself (the original engine locked a site/line
/// <em>and</em> its parent block and interference set together). This is the symmetric closure used by both
/// grant and release, which is what fixes the <c>UnlockPath</c> leak.
/// </remarks>
public sealed class DictionaryResourceTopology : IResourceTopology
{
    private readonly IReadOnlyDictionary<ResourceRef, IReadOnlyList<ResourceRef>> _closure;
    private readonly IReadOnlyDictionary<ResourceRef, IReadOnlySet<string>> _blacklist;

    public DictionaryResourceTopology(
        IReadOnlyDictionary<ResourceRef, IReadOnlyList<ResourceRef>>? closure = null,
        IReadOnlyDictionary<ResourceRef, IReadOnlySet<string>>? blacklist = null)
    {
        _closure = closure ?? new Dictionary<ResourceRef, IReadOnlyList<ResourceRef>>();
        _blacklist = blacklist ?? new Dictionary<ResourceRef, IReadOnlySet<string>>();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ResourceRef> ClosureOf(ResourceRef resource)
    {
        if (!_closure.TryGetValue(resource, out var members) || members.Count == 0)
            return new[] { resource };

        // Always include the resource itself, de-duplicated.
        var set = new HashSet<ResourceRef>(members) { resource };
        return set;
    }

    /// <inheritdoc />
    public bool IsBlacklisted(ResourceRef resource, string agentId)
        => _blacklist.TryGetValue(resource, out var agents) && agents.Contains(agentId);

    /// <summary>
    /// Fluent builder accumulating closure / blacklist relations, e.g. while walking the Map topology.
    /// </summary>
    public sealed class Builder
    {
        private readonly Dictionary<ResourceRef, HashSet<ResourceRef>> _closure = new();
        private readonly Dictionary<ResourceRef, HashSet<string>> _blacklist = new();

        /// <summary>Adds <paramref name="member"/> to <paramref name="resource"/>'s closure (and vice-versa is not implied).</summary>
        public Builder WithClosureMember(ResourceRef resource, ResourceRef member)
        {
            if (!_closure.TryGetValue(resource, out var set))
                _closure[resource] = set = new HashSet<ResourceRef>();
            set.Add(member);
            return this;
        }

        /// <summary>Maps <paramref name="resource"/> to its whole closure set at once.</summary>
        public Builder WithClosure(ResourceRef resource, IEnumerable<ResourceRef> members)
        {
            if (!_closure.TryGetValue(resource, out var set))
                _closure[resource] = set = new HashSet<ResourceRef>();
            foreach (var m in members) set.Add(m);
            return this;
        }

        /// <summary>Blacklists <paramref name="resource"/> for <paramref name="agentId"/>.</summary>
        public Builder WithBlacklist(ResourceRef resource, string agentId)
        {
            if (!_blacklist.TryGetValue(resource, out var set))
                _blacklist[resource] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(agentId);
            return this;
        }

        /// <summary>Materialises the immutable topology.</summary>
        public DictionaryResourceTopology Build()
        {
            var closure = _closure.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<ResourceRef>)kv.Value.ToList());
            var blacklist = _blacklist.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlySet<string>)kv.Value);
            return new DictionaryResourceTopology(closure, blacklist);
        }
    }
}
