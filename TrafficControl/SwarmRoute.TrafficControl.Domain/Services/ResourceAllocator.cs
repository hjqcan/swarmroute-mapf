using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// Default <see cref="IResourceAllocator"/>. Allocation delegates to the aggregate's whole-path
/// <c>TryGrant</c> (which itself applies the blacklist + interference-closure + occupied-by-another filter,
/// keeping the invariant); <see cref="BlockedResources"/> maps those closure conflicts back to candidate path
/// CP/Lane resources that the planner can actually prune. Stateless → singleton-safe.
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
            if (IsPlannerPrunable(cell.Resource) && CellIsBlocked(table, cell, agentId))
                blocked.Add(cell.Resource);
        }

        return blocked;
    }

    private bool CellIsBlocked(ReservationTable table, SpaceTimeCell cell, string agentId)
    {
        foreach (var member in _topology.ClosureOf(cell.Resource))
        {
            if (_topology.IsBlacklisted(member, agentId))
                return true;

            if (!table.IsFreeForExcept(member, cell.Interval, agentId))
                return true;
        }

        return false;
    }

    private static bool IsPlannerPrunable(ResourceRef resource)
        => resource.Kind is ResourceKind.CP or ResourceKind.Lane;
}
