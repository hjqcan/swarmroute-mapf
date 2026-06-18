using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Application.Services;

/// <summary>
/// Builds the immutable <see cref="ResourceAllocationGraphSnapshot"/> the Deadlock context consumes, from the
/// live singleton <see cref="ReservationTable"/>:
/// <list type="bullet">
///   <item><description><c>Owns</c> = one <c>(AgentId, ResourceId)</c> edge per active lease (held resource);</description></item>
///   <item><description><c>Waits</c> = one <c>(AgentId, ResourceId)</c> edge per queued / contended request.</description></item>
/// </list>
/// The aggregate's accessors return stable copies, so the snapshot is internally consistent and Deadlock never
/// holds a TrafficControl lock.
/// </summary>
public sealed class TrafficControlSnapshotProvider : ITrafficControlSnapshotProvider
{
    private readonly ReservationTable _table;

    public TrafficControlSnapshotProvider(ReservationTable table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <inheritdoc />
    public ResourceAllocationGraphSnapshot GetSnapshot()
    {
        var owns = _table.ActiveLeases
            .Select(l => (l.AgentId, l.Resource.Id))
            .ToList();

        var waits = _table.ContendedRequests
            .Select(r => (r.AgentId, r.ResourceId))
            .ToList();

        return new ResourceAllocationGraphSnapshot(owns, waits);
    }
}
