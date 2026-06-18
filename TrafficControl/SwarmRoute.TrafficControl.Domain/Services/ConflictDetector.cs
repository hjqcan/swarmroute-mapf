using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Shared;
using SwarmRoute.TrafficControl.Domain.ValueObjects;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// Default <see cref="IConflictDetector"/>. Walks the candidate cells against the aggregate's live leases and
/// classifies each clash. Topology (interference / opposing lanes) is consulted through
/// <see cref="IResourceTopology"/> so the detector stays decoupled from Map. Stateless → singleton-safe.
/// </summary>
/// <remarks>
/// Classification (for a clash between candidate cell on resource R over interval I and an incumbent lease by
/// another agent):
/// <list type="bullet">
///   <item><description><b>EdgeSwap</b> — R is a <see cref="ResourceKind.Lane"/> and the incumbent holds the
///   <em>reversed</em> lane (<c>"a-b"</c> vs <c>"b-a"</c>) over an overlapping interval;</description></item>
///   <item><description><b>VertexSame</b> — same resource, overlapping interval, candidate enters no later
///   than the incumbent (head-on / simultaneous occupation);</description></item>
///   <item><description><b>Following</b> — same resource, overlapping interval, candidate enters strictly
///   <em>after</em> the incumbent (trailing into a not-yet-cleared cell);</description></item>
///   <item><description><b>Interference</b> — a member of R's interference closure (other than R itself) is
///   held by another agent over an overlapping interval.</description></item>
/// </list>
/// </remarks>
public sealed class ConflictDetector : IConflictDetector
{
    private readonly IResourceTopology _topology;

    public ConflictDetector(IResourceTopology topology)
        => _topology = topology ?? throw new ArgumentNullException(nameof(topology));

    /// <inheritdoc />
    public IReadOnlyList<Conflict> Detect(ReservationTable table, SpaceTimePath candidate, string agentId)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));

        var conflicts = new List<Conflict>();
        var active = table.ActiveLeases;

        foreach (var cell in candidate.Cells)
        {
            foreach (var lease in active)
            {
                if (string.Equals(lease.AgentId, agentId, StringComparison.Ordinal))
                    continue;
                if (!lease.Interval.Overlaps(cell.Interval))
                    continue;

                if (lease.Resource.Equals(cell.Resource))
                {
                    // Same cell, overlapping time → vertex or following depending on who enters first.
                    var type = cell.Interval.StartMs > lease.Interval.StartMs
                        ? ConflictType.Following
                        : ConflictType.VertexSame;
                    conflicts.Add(new Conflict(type, agentId, lease.AgentId, cell.Resource, lease.Resource));
                }
                else if (IsReversedLane(cell.Resource, lease.Resource))
                {
                    conflicts.Add(new Conflict(ConflictType.EdgeSwap, agentId, lease.AgentId, cell.Resource, lease.Resource));
                }
            }

            // Interference: any closure member (other than the cell itself) held by another agent.
            foreach (var member in _topology.ClosureOf(cell.Resource))
            {
                if (member.Equals(cell.Resource))
                    continue;

                foreach (var lease in active)
                {
                    if (string.Equals(lease.AgentId, agentId, StringComparison.Ordinal))
                        continue;
                    if (lease.Resource.Equals(member) && lease.Interval.Overlaps(cell.Interval))
                        conflicts.Add(new Conflict(ConflictType.Interference, agentId, lease.AgentId, cell.Resource, member));
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// True when two lanes are the same physical edge traversed in opposite directions. Lane ids follow the
    /// original engine's <c>"start-end"</c> convention (see <c>GraphMap.UnlockPath</c>), so the reverse of
    /// <c>"a-b"</c> is <c>"b-a"</c>.
    /// </summary>
    private static bool IsReversedLane(ResourceRef a, ResourceRef b)
    {
        if (a.Kind != ResourceKind.Lane || b.Kind != ResourceKind.Lane)
            return false;

        var dashA = a.Id.IndexOf('-');
        var dashB = b.Id.IndexOf('-');
        if (dashA <= 0 || dashB <= 0)
            return false;

        var aStart = a.Id.AsSpan(0, dashA);
        var aEnd = a.Id.AsSpan(dashA + 1);
        var bStart = b.Id.AsSpan(0, dashB);
        var bEnd = b.Id.AsSpan(dashB + 1);

        return aStart.SequenceEqual(bEnd) && aEnd.SequenceEqual(bStart);
    }
}
