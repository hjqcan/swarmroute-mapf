using NetDevPack.Domain;
using SwarmRoute.Deadlock.Domain.Shared;
using SwarmRoute.Deadlock.Domain.Shared.Enums;

namespace SwarmRoute.Deadlock.Domain.Aggregates;

/// <summary>
/// The deadlock-avoidance/recovery plan for a single victim agent — the concrete state machine that
/// implements the (previously stubbed) AJR <c>ISolver.Solve()</c> + <c>Recover()</c> and
/// <c>ConflictSolveStateMachine</c>.
/// <para>Step progression (forward-only):</para>
/// <c>SelectVictim → SelectAvoidancePoint → ReserveDetour → DispatchToAvoid → ConfirmCleared → Recover → Completed</c>,
/// with <see cref="AvoidancePlanStep.Aborted"/> as a terminal failure branch. Each successful step
/// bumps <c>StateVersion</c> (optimistic concurrency, per grukirbs convention).
/// <para>
/// The aggregate is transport-agnostic: it records *what* was decided (victim, avoidance site) and how
/// far the recovery has progressed. The actual side effects (querying a road-map for an avoid site,
/// reserving a detour, dispatching the AGV) are performed by the resolver/services via the integration
/// seams and fed back in through the <c>RecordX</c> methods.
/// </para>
/// </summary>
public class AvoidancePlan : Entity, IAggregateRoot
{
    // EF Core parameterless constructor
    private AvoidancePlan()
    {
        CaseId = Guid.Empty;
        VictimAgentId = string.Empty;
        AvoidanceSiteId = null;
        CurrentStep = AvoidancePlanStep.SelectVictim;
        FailureReason = null;
        StateVersion = 0;
        StateChangedAtUtc = null;
    }

    /// <summary>
    /// Starts a plan for <paramref name="victimAgentId"/> belonging to deadlock case
    /// <paramref name="caseId"/>. The plan begins at <see cref="AvoidancePlanStep.SelectVictim"/> with the
    /// victim already chosen (selection is performed by the resolver before construction).
    /// </summary>
    public AvoidancePlan(Guid id, Guid caseId, string victimAgentId)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(DeadlockErrorCodes.NoVictim, nameof(id));
        if (caseId == Guid.Empty)
            throw new ArgumentException(DeadlockErrorCodes.NoVictim, nameof(caseId));
        if (string.IsNullOrWhiteSpace(victimAgentId))
            throw new ArgumentException(DeadlockErrorCodes.NoVictim, nameof(victimAgentId));

        Id = id;
        CaseId = caseId;
        VictimAgentId = victimAgentId.Trim();
        AvoidanceSiteId = null;
        CurrentStep = AvoidancePlanStep.SelectVictim;
        FailureReason = null;
        StateVersion = 1;
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>The deadlock case this plan resolves.</summary>
    public Guid CaseId { get; private set; }

    /// <summary>The victim agent that yields (is routed to an avoidance site).</summary>
    public string VictimAgentId { get; private set; }

    /// <summary>The chosen avoidance/relay site id, once <see cref="AvoidancePlanStep.SelectAvoidancePoint"/> succeeds.</summary>
    public string? AvoidanceSiteId { get; private set; }

    /// <summary>How far the recovery has progressed.</summary>
    public AvoidancePlanStep CurrentStep { get; private set; }

    /// <summary>Set when the plan aborts; an error code from <see cref="DeadlockErrorCodes"/> or a seam reason.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Optimistic-concurrency version; incremented on each accepted step.</summary>
    public long StateVersion { get; private set; }

    /// <summary>Last state-change timestamp (UTC).</summary>
    public DateTimeOffset? StateChangedAtUtc { get; private set; }

    /// <summary>True once the plan reached a terminal step.</summary>
    public bool IsTerminal => CurrentStep is AvoidancePlanStep.Completed or AvoidancePlanStep.Aborted;

    /// <summary>True if the plan completed successfully.</summary>
    public bool IsSucceeded => CurrentStep == AvoidancePlanStep.Completed;

    /// <summary>
    /// SelectVictim → SelectAvoidancePoint. The victim is already known from construction; this advances
    /// the machine to the avoidance-point selection step.
    /// </summary>
    public void AdvanceToSelectAvoidancePoint()
    {
        Expect(AvoidancePlanStep.SelectVictim);
        Transition(AvoidancePlanStep.SelectAvoidancePoint);
    }

    /// <summary>
    /// SelectAvoidancePoint → ReserveDetour. Records the avoidance site chosen by
    /// <c>IAvoidancePointSelector</c>.
    /// </summary>
    public void RecordAvoidancePoint(string avoidanceSiteId)
    {
        Expect(AvoidancePlanStep.SelectAvoidancePoint);
        if (string.IsNullOrWhiteSpace(avoidanceSiteId))
            throw new ArgumentException(DeadlockErrorCodes.InvalidPlanStep, nameof(avoidanceSiteId));

        AvoidanceSiteId = avoidanceSiteId.Trim();
        Transition(AvoidancePlanStep.ReserveDetour);
    }

    /// <summary>ReserveDetour → DispatchToAvoid. Call after <c>IDetourReservationService</c> grants the detour.</summary>
    public void RecordDetourReserved()
    {
        Expect(AvoidancePlanStep.ReserveDetour);
        Transition(AvoidancePlanStep.DispatchToAvoid);
    }

    /// <summary>DispatchToAvoid → ConfirmCleared. Call once the victim has been sent onto the detour.</summary>
    public void RecordDispatched()
    {
        Expect(AvoidancePlanStep.DispatchToAvoid);
        Transition(AvoidancePlanStep.ConfirmCleared);
    }

    /// <summary>ConfirmCleared → Recover. Call once the circular wait is confirmed cleared.</summary>
    public void RecordCleared()
    {
        Expect(AvoidancePlanStep.ConfirmCleared);
        Transition(AvoidancePlanStep.Recover);
    }

    /// <summary>Recover → Completed (terminal). The victim resumes navigation toward its original goal.</summary>
    public void RecordRecovered()
    {
        Expect(AvoidancePlanStep.Recover);
        Transition(AvoidancePlanStep.Completed);
    }

    /// <summary>
    /// Aborts the plan from any non-terminal step (e.g. no avoidance site available, detour denied),
    /// recording <paramref name="reason"/>. No-ops if already terminal.
    /// </summary>
    public void Abort(string reason)
    {
        if (IsTerminal)
            return;

        FailureReason = string.IsNullOrWhiteSpace(reason) ? DeadlockErrorCodes.InvalidPlanStep : reason.Trim();
        Transition(AvoidancePlanStep.Aborted);
    }

    private void Expect(AvoidancePlanStep step)
    {
        if (CurrentStep != step)
            throw new InvalidOperationException(
                $"{DeadlockErrorCodes.InvalidPlanStep}: expected step {step} but plan is at {CurrentStep}.");
    }

    private void Transition(AvoidancePlanStep next)
    {
        CurrentStep = next;
        IncrementStateVersion();
    }

    private void IncrementStateVersion()
    {
        checked { StateVersion++; }
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Optimistic-concurrency check.</summary>
    public bool CheckVersion(long expectedVersion) => StateVersion == expectedVersion;
}
