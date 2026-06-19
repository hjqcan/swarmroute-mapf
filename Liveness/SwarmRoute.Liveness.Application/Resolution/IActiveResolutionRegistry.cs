using System.Collections.Generic;
using SwarmRoute.Deadlock.Domain.Aggregates;

namespace SwarmRoute.Deadlock.Application.Resolution;

/// <summary>
/// One open deadlock resolution: the live <see cref="DeadlockCase"/> + its <see cref="AvoidancePlan"/>,
/// keyed by the victim agent. The aggregates are the SAME instances the resolver produced (not copies),
/// so <c>IDeadlockResolver.Recover</c> can drive them to completion.
/// </summary>
public sealed record ActiveResolution(string VictimAgentId, DeadlockCase Case, AvoidancePlan Plan);

/// <summary>
/// Process-wide store of in-flight deadlock resolutions. Because the Deadlock context persists no EF
/// state (deadlocks are transient), <see cref="Services.DeadlockAppService"/> would otherwise discard the
/// <see cref="DeadlockCase"/>/<see cref="AvoidancePlan"/> right after a scan — leaving nothing for the
/// recovery step (<c>ConfirmCleared → Recover → Resolved</c>) to act on. This registry keeps the live
/// aggregates alive between the scan that opens a resolution and the later tick that recovers it.
/// <para>Implementations MUST be a thread-safe singleton (the in-process bus fans events out
/// synchronously across scopes; the registry is the cross-scope state).</para>
/// </summary>
public interface IActiveResolutionRegistry
{
    /// <summary>
    /// Stores the live <paramref name="case"/> + <paramref name="plan"/> for <c>plan.VictimAgentId</c>.
    /// Idempotent per victim: returns <see langword="false"/> (no-op) if that victim already has an open
    /// resolution, so a re-detected cycle does not open a duplicate case / re-emit
    /// <c>Deadlock.Case.ResolutionRequested</c>.
    /// </summary>
    bool Open(DeadlockCase @case, AvoidancePlan plan);

    /// <summary>True if <paramref name="victimAgentId"/> currently has an open (un-closed) resolution.</summary>
    bool HasOpen(string victimAgentId);

    /// <summary>True if ANY of <paramref name="agentIds"/> is currently an open victim.</summary>
    bool HasOpenForAny(IEnumerable<string> agentIds);

    /// <summary>Looks up the open resolution for <paramref name="victimAgentId"/>.</summary>
    bool TryGet(string victimAgentId, out ActiveResolution resolution);

    /// <summary>A point-in-time snapshot of all open resolutions (for the recovery driver to iterate).</summary>
    IReadOnlyList<ActiveResolution> SnapshotOpen();

    /// <summary>Removes the open resolution for <paramref name="victimAgentId"/> (after recover/escalate). Idempotent.</summary>
    void Close(string victimAgentId);
}
