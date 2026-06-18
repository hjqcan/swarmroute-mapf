using System.Collections.Generic;

namespace SwarmRoute.Deadlock.Application.Resolution;

/// <summary>
/// Remembers the avoidance point most recently handed to each victim, so the anti-livelock guard in
/// <see cref="AntiLivelockAvoidancePointSelector"/> can avoid choosing the same point twice in a row.
/// Register as a <b>singleton</b> (it must outlive the per-scan scope the selector resolves in).
/// </summary>
public sealed class AvoidancePointHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _lastByVictim = new(StringComparer.Ordinal);

    /// <summary>The avoidance point last chosen for <paramref name="victimAgentId"/>, or null if none yet.</summary>
    public string? Last(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return null;
        lock (_gate)
        {
            return _lastByVictim.GetValueOrDefault(victimAgentId);
        }
    }

    /// <summary>Records <paramref name="siteId"/> as the latest avoidance point chosen for the victim.</summary>
    public void Record(string victimAgentId, string siteId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId) || string.IsNullOrWhiteSpace(siteId))
            return;
        lock (_gate)
        {
            _lastByVictim[victimAgentId] = siteId;
        }
    }
}
