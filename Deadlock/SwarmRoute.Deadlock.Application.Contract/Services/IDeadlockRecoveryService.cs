using System.Collections.Generic;

namespace SwarmRoute.Deadlock.Application.Contract.Services;

/// <summary>
/// Drives the recovery half of the deadlock loop. For every open resolution whose circular wait has been
/// confirmed cleared (the victim reached its avoidance site and the contended resource is free again),
/// it advances the avoidance plan <c>ConfirmCleared → Recover → Completed</c>, marks the case
/// <c>Resolved</c> (raising <c>Deadlock.Case.Resolved</c>) and closes the registry entry.
/// <para>Called once per coordination tick by the fleet driver (the v0 execution layer), separately from
/// the contention-triggered scan, so it never nests inside a <c>TryReserve</c> publish.</para>
/// </summary>
public interface IDeadlockRecoveryService
{
    /// <summary>
    /// Attempts recovery for every open resolution. Returns the victim ids whose recovery just completed
    /// (so the caller can restore those agents to their original goals). Empty when nothing cleared.
    /// </summary>
    Task<IReadOnlyCollection<string>> TryRecoverAllAsync(CancellationToken cancellationToken = default);
}
