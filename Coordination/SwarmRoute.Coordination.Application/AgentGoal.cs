namespace SwarmRoute.Coordination.Application;

/// <summary>
/// A single agent's navigation goal for one coordination cycle: route <see cref="AgentId"/> from
/// <see cref="FromSiteId"/> to <see cref="ToSiteId"/>. <see cref="Priority"/> drives the deterministic
/// processing order (lower value = higher priority = planned/reserved first), tie-broken by agent id, so a
/// cycle is reproducible (no livelock from non-deterministic ordering — ADR-003 / R6).
/// </summary>
public sealed record AgentGoal(string AgentId, string FromSiteId, string ToSiteId, int Priority = 0);
