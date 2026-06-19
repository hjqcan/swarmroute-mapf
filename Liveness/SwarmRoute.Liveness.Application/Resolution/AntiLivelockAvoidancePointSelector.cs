using System.Collections.Generic;
using SwarmRoute.Deadlock.Domain.Services;

namespace SwarmRoute.Deadlock.Application.Resolution;

/// <summary>
/// Decorates a real <see cref="IAvoidancePointSelector"/> with the anti-livelock "don't pick the same
/// avoidance point twice in a row" guard (DoD §6 / WS-Q3): it excludes the point last chosen for the victim
/// and, only if that leaves no candidate, falls back to allowing the repeat (a single-siding topology must
/// still be usable — the driver's distance/attempt guard catches a genuine livelock). The point actually
/// returned is recorded in <see cref="AvoidancePointHistory"/> for next time.
/// <para>Wrap the concrete selector and register THIS as <c>IAvoidancePointSelector</c>; keep
/// <see cref="AvoidancePointHistory"/> a singleton so the "last point" survives across scans.</para>
/// </summary>
public sealed class AntiLivelockAvoidancePointSelector : IAvoidancePointSelector
{
    private readonly IAvoidancePointSelector _inner;
    private readonly AvoidancePointHistory _history;

    public AntiLivelockAvoidancePointSelector(IAvoidancePointSelector inner, AvoidancePointHistory history)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    /// <inheritdoc />
    public string? SelectAvoidancePoint(string victimAgentId, IReadOnlySet<string>? excludedSiteIds = null)
    {
        var last = _history.Last(victimAgentId);

        // Merge the caller's exclusions (if any) with "the point we used last time for this victim".
        IReadOnlySet<string>? excluded = excludedSiteIds;
        if (last is not null)
        {
            var merged = excludedSiteIds is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(excludedSiteIds, StringComparer.Ordinal);
            merged.Add(last);
            excluded = merged;
        }

        // Prefer a point that isn't the one we just used; if excluding leaves nothing, fall back to the
        // caller's exclusions only (allow the repeat rather than fabricate "no avoid site").
        var chosen = _inner.SelectAvoidancePoint(victimAgentId, excluded)
                     ?? _inner.SelectAvoidancePoint(victimAgentId, excludedSiteIds);

        if (chosen is not null)
            _history.Record(victimAgentId, chosen);

        return chosen;
    }
}
