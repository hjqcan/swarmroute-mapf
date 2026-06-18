using SwarmRoute.Deadlock.Domain.Aggregates;

namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// Drives the deadlock-avoidance/recovery state machine for a case — the clean-architecture
/// replacement for the stubbed AJR <c>ISolver.Solve()</c> / <c>Recover()</c>.
/// <para>
/// <see cref="SolveAsync"/> picks a victim, selects an avoidance point, reserves a detour and dispatches the
/// victim to the avoid site (the AJR <c>Solve</c>). <see cref="Recover"/> confirms the cycle cleared and
/// restores the victim's original navigation (the AJR <c>Recover</c>). Both mutate the supplied
/// <see cref="AvoidancePlan"/> aggregate and the <see cref="DeadlockCase"/> lifecycle.
/// </para>
/// </summary>
public interface IDeadlockResolver
{
    /// <summary>
    /// Builds an <see cref="AvoidancePlan"/> for <paramref name="deadlockCase"/> and runs it through
    /// <c>SelectVictim → SelectAvoidancePoint → ReserveDetour → DispatchToAvoid</c>. The case transitions
    /// to <c>Resolving</c> (raising <c>Deadlock.Case.ResolutionRequested</c>) when a victim/strategy is
    /// chosen, or to <c>Escalated</c> if no avoidance site / detour is available. Returns the plan.
    /// </summary>
    Task<AvoidancePlan> SolveAsync(
        DeadlockCase deadlockCase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="plan"/> through <c>ConfirmCleared → Recover → Completed</c> and marks
    /// <paramref name="deadlockCase"/> <c>Resolved</c> (raising <c>Deadlock.Case.Resolved</c>). Returns
    /// <see langword="true"/> if recovery completed.
    /// </summary>
    bool Recover(DeadlockCase deadlockCase, AvoidancePlan plan);
}
