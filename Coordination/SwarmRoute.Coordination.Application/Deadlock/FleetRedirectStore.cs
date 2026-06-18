using System.Collections.Generic;
using System.Linq;

namespace SwarmRoute.Coordination.Application.Deadlock;

/// <summary>
/// Thread-safe in-memory store backing both <see cref="IFleetRedirectQuery"/> (read, for the driver) and
/// <see cref="IFleetRedirectSink"/> (write, for the consumer). Register as a <b>singleton</b> so the
/// scoped consumer (which fires synchronously inside the contention publish) and the driver (which reads
/// between ticks) share one instance.
/// </summary>
public sealed class FleetRedirectStore : IFleetRedirectQuery, IFleetRedirectSink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RedirectIntent> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recovered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _escalated = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void PublishRedirect(RedirectIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        lock (_gate)
        {
            _active[intent.VictimAgentId] = intent;
            _recovered.Remove(intent.VictimAgentId);
            _escalated.Remove(intent.VictimAgentId);
        }
    }

    /// <inheritdoc />
    public void MarkRecovered(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return;
        lock (_gate)
        {
            _active.Remove(victimAgentId);
            _recovered.Add(victimAgentId);
        }
    }

    /// <inheritdoc />
    public void MarkEscalated(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return;
        lock (_gate)
        {
            _active.Remove(victimAgentId);
            _escalated.Add(victimAgentId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<RedirectIntent> ActiveRedirects
    {
        get { lock (_gate) { return _active.Values.ToList(); } }
    }

    /// <inheritdoc />
    public bool TryGetActiveRedirect(string victimAgentId, out RedirectIntent intent)
    {
        intent = null!;
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return false;
        lock (_gate)
        {
            return _active.TryGetValue(victimAgentId, out intent!);
        }
    }

    /// <inheritdoc />
    public bool IsRecovered(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return false;
        lock (_gate) { return _recovered.Contains(victimAgentId); }
    }

    /// <inheritdoc />
    public bool IsEscalated(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return false;
        lock (_gate) { return _escalated.Contains(victimAgentId); }
    }
}
