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
/// in a standalone build those are the <c>Null*</c> implementations, so <see cref="Solve"/> will
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
    public AvoidancePlan Solve(DeadlockCase deadlockCase)
    {
        ArgumentNullException.ThrowIfNull(deadlockCase);

        // SelectVictim (deterministic).
        var cycle = DeadlockCycle.FromAgentIds(deadlockCase.AgentIds);
        var victim = _victimSelector.SelectVictim(cycle);

        var plan = new AvoidancePlan(Guid.NewGuid(), deadlockCase.Id, victim);

        // SelectAvoidancePoint.
        plan.AdvanceToSelectAvoidancePoint();
        var avoidSite = _avoidancePointSelector.SelectAvoidancePoint(victim);

        // Transition the case to Resolving with the chosen victim/strategy + suggested avoid target.
        // This raises Deadlock.Case.ResolutionRequested regardless of whether the detour later succeeds,
        // so Coordination is always informed of the intended victim.
        deadlockCase.RequestResolution(victim, ResolutionStrategy.SendToAvoidSite, avoidSite);

        if (string.IsNullOrWhiteSpace(avoidSite))
        {
            // No avoidance site available (e.g. standalone Null selector) → escalate.
            plan.Abort(DeadlockErrorCodes.NoVictim);
            deadlockCase.Escalate(DeadlockErrorCodes.NoVictim);
            return plan;
        }

        plan.RecordAvoidancePoint(avoidSite);

        // ReserveDetour (collision-free, via TrafficControl seam).
        if (!_detourReservation.TryReserveDetour(victim, avoidSite))
        {
            plan.Abort("Deadlock.AvoidancePlan.DetourDenied");
            deadlockCase.Escalate("Deadlock.AvoidancePlan.DetourDenied");
            return plan;
        }

        plan.RecordDetourReserved();

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
