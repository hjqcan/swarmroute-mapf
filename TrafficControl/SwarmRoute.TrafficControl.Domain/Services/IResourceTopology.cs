using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// The slice of Map topology the allocator needs to compute the <em>resource closure</em> of a path —
/// i.e. everything that must be locked/released together with a given resource. In the original engine this
/// information lived on the mutable <c>MapSite</c>/<c>MapLine</c>/<c>MapBlock</c> graph
/// (<c>ParentBlock</c>, <c>InterferenceSites</c>/<c>InterferenceLines</c>, <c>ContainSites</c>/<c>ContainLines</c>).
/// Here it is abstracted so the TrafficControl domain stays pure: the Application layer backs it with the
/// Map context's published topology, and v0 can also run with an <see cref="Empty"/> (no-closure) topology.
/// </summary>
/// <remarks>
/// <para><b>This abstraction is the home of the v0 release-leak fix.</b> The original
/// <c>GraphMap.GeneratePath</c> locked, for each path resource, its <c>ParentBlock</c> (and the pruning step
/// also pulled in the interference closure), but <c>GraphMap.UnlockPath</c> left the ParentBlock /
/// interference release commented out — so blocks and interfered resources leaked forever. By driving both
/// grant <em>and</em> release through the same <see cref="ClosureOf"/>, the two are kept symmetric.</para>
/// </remarks>
public interface IResourceTopology
{
    /// <summary>
    /// Returns the full closure of <paramref name="resource"/>: the resource itself plus its parent block
    /// and its interference set (transitively de-duplicated). Locking/releasing a resource must apply to its
    /// whole closure. Implementations must include <paramref name="resource"/> in the result.
    /// </summary>
    IReadOnlyCollection<ResourceRef> ClosureOf(ResourceRef resource);

    /// <summary>
    /// True when <paramref name="resource"/> is blacklisted for <paramref name="agentId"/> (the agent must
    /// never be routed onto / granted it). Ports <c>MapResource.AGVBlackList.Contains(agvId)</c>.
    /// </summary>
    bool IsBlacklisted(ResourceRef resource, string agentId);

    /// <summary>A topology with no parent blocks, no interference and no blacklist (closure = the resource itself).</summary>
    static IResourceTopology Empty { get; } = new EmptyResourceTopology();

    /// <summary>The trivial no-closure topology used when Map topology is not wired (v0 standalone / tests).</summary>
    private sealed class EmptyResourceTopology : IResourceTopology
    {
        public IReadOnlyCollection<ResourceRef> ClosureOf(ResourceRef resource) => new[] { resource };

        public bool IsBlacklisted(ResourceRef resource, string agentId) => false;
    }
}
