namespace SwarmRoute.Coordination.Application.Deadlock;

/// <summary>
/// A Coordination-side projection of a deadlock resolution: agent <see cref="VictimAgentId"/> should be
/// redirected to <see cref="AvoidSiteId"/> to break case <see cref="CaseId"/>. Produced by the
/// <see cref="DeadlockResolutionRequestedConsumer"/> from a <c>Deadlock.Case.ResolutionRequested</c> event;
/// consumed by the fleet driver (the v0 execution layer), which re-plans the victim from its CURRENT
/// control point to the avoid site and restores the victim's original goal once the case is recovered.
/// <para>The original goal is intentionally NOT carried here — the driver already owns each agent's goal
/// and current pose, so the intent only needs (victim → avoid site).</para>
/// </summary>
public sealed record RedirectIntent(Guid CaseId, string VictimAgentId, string AvoidSiteId);
