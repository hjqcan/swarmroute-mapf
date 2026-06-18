using SwarmRoute.Deadlock.Domain.Aggregates;
using SwarmRoute.Deadlock.Domain.Shared;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// Default <see cref="IDeadlockResolver"/> implementing the AJR avoidance/recovery state machine on top
/// of the <see cref="AvoidancePlan"/> aggregate.
/// <para>
/// Victim selection is delegated to <see cref="IVictimSelector"/> (deterministic: smallest cycle,
/// smallest-id tie-break). Avoidance-point selection and detour reservation are delegated to the
/// integration seams (<see cref="IAvoidancePointSelector"/> / <see cref="IDetourReservationService"/>);
/// in a standalone build those are the <c>Null*</c> implementations, so <see cref="SolveAsync"/> will
/// escalate (no avoid site) rather than fabricate a detour. Once integrated with Map/TrafficControl the
/// same flow performs real work.
/// </para>
/// </summary>
public sealed class AvoidanceDeadlockResolver : IDeadlockResolver
{
    private readonly IVictimSelector _victimSelector;
    private readonly IAvoidancePointSelector _avoidancePointSelector;
    private readonly IDetourReservationService _detourReservation;
    private readonly IClearanceConfirmer _clearanceConfirmer;

    public AvoidanceDeadlockResolver(
        IVictimSelector victimSelector,
        IAvoidancePointSelector avoidancePointSelector,
        IDetourReservationService detourReservation,
        IClearanceConfirmer clearanceConfirmer)
    {
        _victimSelector = victimSelector ?? throw new ArgumentNullException(nameof(victimSelector));
        _avoidancePointSelector = avoidancePointSelector ?? throw new ArgumentNullException(nameof(avoidancePointSelector));
        _detourReservation = detourReservation ?? throw new ArgumentNullException(nameof(detourReservation));
        _clearanceConfirmer = clearanceConfirmer ?? throw new ArgumentNullException(nameof(clearanceConfirmer));
    }

    /// <inheritdoc />
    public async Task<AvoidancePlan> SolveAsync(
        DeadlockCase deadlockCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deadlockCase);

        // SelectVictim (deterministic).
        var cycle = DeadlockCycle.FromAgentIds(deadlockCase.AgentIds);
        var victim = _victimSelector.SelectVictim(cycle);

        var plan = new AvoidancePlan(Guid.NewGuid(), deadlockCase.Id, victim);

        // SelectAvoidancePoint.
        plan.AdvanceToSelectAvoidancePoint();
        var avoidSite = _avoidancePointSelector.SelectAvoidancePoint(victim);

        if (string.IsNullOrWhiteSpace(avoidSite))
        {
            // No avoidance site available (e.g. standalone Null selector) → escalate.
            plan.Abort(DeadlockErrorCodes.NoAvoidanceSite);
            deadlockCase.EscalateResolutionFailure(
                victim,
                ResolutionStrategy.SendToAvoidSite,
                reason: DeadlockErrorCodes.NoAvoidanceSite);
            return plan;
        }

        plan.RecordAvoidancePoint(avoidSite);

        // ReserveDetour (collision-free, via TrafficControl seam).
        if (!await _detourReservation.TryReserveDetourAsync(victim, avoidSite, cancellationToken).ConfigureAwait(false))
        {
            plan.Abort(DeadlockErrorCodes.DetourDenied);
            deadlockCase.EscalateResolutionFailure(
                victim,
                ResolutionStrategy.SendToAvoidSite,
                avoidSite,
                DeadlockErrorCodes.DetourDenied);
            return plan;
        }

        plan.RecordDetourReserved();

        // Transition the case to Resolving only after the detour reservation succeeds. Downstream consumers
        // treat Deadlock.Case.ResolutionRequested as an executable redirect command, not a tentative intent.
        deadlockCase.RequestResolution(victim, ResolutionStrategy.SendToAvoidSite, avoidSite);

        // DispatchToAvoid.
        plan.RecordDispatched();

        return plan;
    }

    /// <inheritdoc />
    public bool Recover(DeadlockCase deadlockCase, AvoidancePlan plan)
    {
        ArgumentNullException.ThrowIfNull(deadlockCase);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.CurrentStep != AvoidancePlanStep.ConfirmCleared)
            return false;

        // ConfirmCleared.
        if (!_clearanceConfirmer.IsCleared(plan.VictimAgentId))
            return false;

        plan.RecordCleared();

        // Recover → Completed.
        plan.RecordRecovered();

        deadlockCase.MarkResolved();
        return true;
    }
}
