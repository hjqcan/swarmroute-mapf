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

    /// <summary>
    /// True once <paramref name="caseId"/> for <paramref name="victimAgentId"/> has been recovered (cycle cleared).
    /// </summary>
    bool IsRecovered(string victimAgentId, Guid caseId);

    /// <summary>True once <paramref name="caseId"/> for <paramref name="victimAgentId"/> was escalated.</summary>
    bool IsEscalated(string victimAgentId, Guid caseId);
}

/// <summary>
/// Driver-side acknowledgement for a redirect command that has reached its physical terminal point. Recovery
/// events can arrive before that happens, so active commands are cleared only after the driver consumes them.
/// </summary>
public interface IFleetRedirectAcknowledger
{
    /// <summary>Clears the active redirect once the victim has physically completed the avoidance command.</summary>
    void MarkRedirectCompleted(Guid caseId, string victimAgentId);
}

/// <summary>
/// Write side of the deadlock-redirect projection, driven by the <see cref="DeadlockResolutionRequestedConsumer"/>
/// in reaction to <c>Deadlock.Case.ResolutionRequested / Resolved / Escalated</c> integration events.
/// </summary>
public interface IFleetRedirectSink
{
    /// <summary>Records (or refreshes) an active redirect for a victim; clears any prior recovered/escalated flag.</summary>
    void PublishRedirect(RedirectIntent intent);

    /// <summary>
    /// Marks the victim's case recovered. This does not clear the active redirect; the driver clears it after the
    /// victim physically reaches the avoidance site and restores its original goal.
    /// </summary>
    void MarkRecovered(Guid caseId, string victimAgentId);

    /// <summary>Marks the victim's case escalated: clears the matching active redirect and flags it escalated.</summary>
    void MarkEscalated(Guid caseId, string victimAgentId);
}
