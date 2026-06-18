using System.Collections.Generic;
using System.Linq;

namespace SwarmRoute.Coordination.Application.Deadlock;

/// <summary>
/// Thread-safe in-memory store backing both <see cref="IFleetRedirectQuery"/> (read, for the driver) and
/// <see cref="IFleetRedirectSink"/> (write, for the consumer). Register as a <b>singleton</b> so the
/// scoped consumer (which fires synchronously inside the contention publish) and the driver (which reads
/// between ticks) share one instance.
/// </summary>
public sealed class FleetRedirectStore : IFleetRedirectQuery, IFleetRedirectSink, IFleetRedirectAcknowledger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RedirectIntent> _active = new(StringComparer.Ordinal);
    private readonly HashSet<(string VictimAgentId, Guid CaseId)> _recovered = [];
    private readonly HashSet<(string VictimAgentId, Guid CaseId)> _escalated = [];

    /// <inheritdoc />
    public void PublishRedirect(RedirectIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        lock (_gate)
        {
            _active[intent.VictimAgentId] = intent;
            _recovered.Remove((intent.VictimAgentId, intent.CaseId));
            _escalated.Remove((intent.VictimAgentId, intent.CaseId));
        }
    }

    /// <inheritdoc />
    public void MarkRecovered(Guid caseId, string victimAgentId)
    {
        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(victimAgentId))
            return;
        var victim = victimAgentId.Trim();
        lock (_gate)
        {
            _recovered.Add((victim, caseId));
        }
    }

    /// <inheritdoc />
    public void MarkEscalated(Guid caseId, string victimAgentId)
    {
        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(victimAgentId))
            return;
        var victim = victimAgentId.Trim();
        lock (_gate)
        {
            RemoveActiveIfCaseMatches(victim, caseId);
            _escalated.Add((victim, caseId));
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
    public bool IsRecovered(string victimAgentId, Guid caseId)
    {
        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(victimAgentId))
            return false;
        lock (_gate) { return _recovered.Contains((victimAgentId.Trim(), caseId)); }
    }

    /// <inheritdoc />
    public bool IsEscalated(string victimAgentId, Guid caseId)
    {
        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(victimAgentId))
            return false;
        lock (_gate) { return _escalated.Contains((victimAgentId.Trim(), caseId)); }
    }

    /// <inheritdoc />
    public void MarkRedirectCompleted(Guid caseId, string victimAgentId)
    {
        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(victimAgentId))
            return;
        lock (_gate)
        {
            RemoveActiveIfCaseMatches(victimAgentId.Trim(), caseId);
        }
    }

    private void RemoveActiveIfCaseMatches(string victimAgentId, Guid caseId)
    {
        if (_active.TryGetValue(victimAgentId, out var active)
            && active.CaseId == caseId)
        {
            _active.Remove(victimAgentId);
        }
    }
}
