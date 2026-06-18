using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// Default <see cref="IResourceAllocator"/>. Allocation delegates to the aggregate's whole-path
/// <c>TryGrant</c> (which itself applies the blacklist + interference-closure + occupied-by-another filter,
/// keeping the invariant); <see cref="BlockedResources"/> reproduces the pruning set for replanning. The
/// closure is consulted via <see cref="IResourceTopology"/>. Stateless → singleton-safe.
/// </summary>
public sealed class ResourceAllocator : IResourceAllocator
{
    private readonly IResourceTopology _topology;

    public ResourceAllocator(IResourceTopology topology)
        => _topology = topology ?? throw new ArgumentNullException(nameof(topology));

    /// <inheritdoc />
    public AllocationOutcome Allocate(ReservationTable table, SpaceTimePath path, string agentId, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(path);
        return table.TryGrant(path, agentId, priority);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ResourceRef> BlockedResources(ReservationTable table, SpaceTimePath path, string agentId)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));

        var blocked = new HashSet<ResourceRef>();

        foreach (var cell in path.Cells)
        {
            foreach (var member in _topology.ClosureOf(cell.Resource))
            {
                if (_topology.IsBlacklisted(member, agentId))
                {
                    blocked.Add(member);
                    continue;
                }

                if (!table.IsFreeForExcept(member, cell.Interval, agentId))
                    blocked.Add(member);
            }
        }

        return blocked;
    }
}
