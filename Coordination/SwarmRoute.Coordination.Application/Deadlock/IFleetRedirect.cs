using System.Collections.Generic;

namespace SwarmRoute.Coordination.Application.Deadlock;

/// <summary>
/// Read side of the deadlock-redirect projection, consumed by the fleet driver each tick. Tells the driver
/// which victims should currently be heading to an avoidance site, which have been recovered (so their
/// original goal can be restored), and which were escalated as livelocks (so the driver stops redirecting).
/// </summary>
public interface IFleetRedirectQuery
{
    /// <summary>Active redirects (victim → avoid site) the driver should be enacting right now.</summary>
    IReadOnlyCollection<RedirectIntent> ActiveRedirects { get; }

    /// <summary>Looks up the active redirect for <paramref name="victimAgentId"/>, if any.</summary>
    bool TryGetActiveRedirect(string victimAgentId, out RedirectIntent intent);

    /// <summary>True once the case for <paramref name="victimAgentId"/> has been recovered (cycle cleared).</summary>
    bool IsRecovered(string victimAgentId);

    /// <summary>True once the case for <paramref name="victimAgentId"/> was escalated (e.g. livelock).</summary>
    bool IsEscalated(string victimAgentId);
}

/// <summary>
/// Write side of the deadlock-redirect projection, driven by the <see cref="DeadlockResolutionRequestedConsumer"/>
/// in reaction to <c>Deadlock.Case.ResolutionRequested / Resolved / Escalated</c> integration events.
/// </summary>
public interface IFleetRedirectSink
{
    /// <summary>Records (or refreshes) an active redirect for a victim; clears any prior recovered/escalated flag.</summary>
    void PublishRedirect(RedirectIntent intent);

    /// <summary>Marks the victim's case recovered: clears the active redirect and flags it recovered.</summary>
    void MarkRecovered(string victimAgentId);

    /// <summary>Marks the victim's case escalated: clears the active redirect and flags it escalated.</summary>
    void MarkEscalated(string victimAgentId);
}
