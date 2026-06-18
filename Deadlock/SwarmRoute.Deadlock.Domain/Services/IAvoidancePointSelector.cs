namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// Integration seam (to be fulfilled by Map/TrafficControl wiring): given the victim agent that must
/// yield, choose a concrete avoidance/relay site for it to retreat to so the contended resource is
/// released. In v0 this is the AJR "AvoidSite" concept.
/// <para>
/// This interface is deliberately defined in the Deadlock domain and left without a production
/// implementation here — the real selection needs the road-map graph (avoid sites) and the live
/// reservation view, which arrive at integration. A <c>NullAvoidancePointSelector</c> is provided for
/// standalone builds/tests.
/// </para>
/// </summary>
public interface IAvoidancePointSelector
{
    /// <summary>
    /// Returns the id of an avoidance site the victim can be sent to, or <see langword="null"/> if none
    /// is currently available (the resolver then escalates).
    /// </summary>
    /// <param name="victimAgentId">The agent that must yield.</param>
    /// <param name="excludedSiteIds">Site ids the selector should NOT return (e.g. the point chosen on the
    /// previous attempt — the anti-livelock "don't pick the same point twice in a row" guard). When the only
    /// otherwise-valid candidate is excluded the caller may retry with no exclusion. Null/empty = no exclusion.</param>
    string? SelectAvoidancePoint(string victimAgentId, IReadOnlySet<string>? excludedSiteIds = null);
}
