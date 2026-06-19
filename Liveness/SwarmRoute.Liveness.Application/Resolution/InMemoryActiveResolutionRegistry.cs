using System.Collections.Generic;
using System.Linq;
using SwarmRoute.Deadlock.Domain.Aggregates;

namespace SwarmRoute.Deadlock.Application.Resolution;

/// <summary>
/// Thread-safe in-memory <see cref="IActiveResolutionRegistry"/>. Register as a <b>singleton</b> so the
/// live case/plan survive across the publish scope that opens them and the later tick that recovers them.
/// </summary>
public sealed class InMemoryActiveResolutionRegistry : IActiveResolutionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveResolution> _byVictim = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool Open(DeadlockCase @case, AvoidancePlan plan)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(plan);

        var victim = plan.VictimAgentId;
        lock (_gate)
        {
            if (_byVictim.ContainsKey(victim))
                return false;

            _byVictim[victim] = new ActiveResolution(victim, @case, plan);
            return true;
        }
    }

    /// <inheritdoc />
    public bool HasOpen(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return false;
        lock (_gate)
        {
            return _byVictim.ContainsKey(victimAgentId.Trim());
        }
    }

    /// <inheritdoc />
    public bool HasOpenForAny(IEnumerable<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        lock (_gate)
        {
            foreach (var id in agentIds)
            {
                if (!string.IsNullOrWhiteSpace(id) && _byVictim.ContainsKey(id.Trim()))
                    return true;
            }
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryGet(string victimAgentId, out ActiveResolution resolution)
    {
        resolution = null!;
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return false;
        lock (_gate)
        {
            return _byVictim.TryGetValue(victimAgentId.Trim(), out resolution!);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ActiveResolution> SnapshotOpen()
    {
        lock (_gate)
        {
            return _byVictim.Values.ToList();
        }
    }

    /// <inheritdoc />
    public void Close(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return;
        lock (_gate)
        {
            _byVictim.Remove(victimAgentId.Trim());
        }
    }
}
