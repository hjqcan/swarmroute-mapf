namespace SwarmRoute.Deadlock.Domain.Shared.Enums;

/// <summary>
/// Steps of the deadlock-avoidance/recovery state machine (ports the AJR
/// <c>ConflictSolveStateMachine</c> / <c>ISolver.Solve+Recover</c>):
/// <c>SelectVictim → SelectAvoidancePoint → ReserveDetour → DispatchToAvoid → ConfirmCleared → Recover</c>.
/// </summary>
public enum AvoidancePlanStep
{
    /// <summary>Initial step: choose which agent in the cycle yields.</summary>
    SelectVictim = 0,

    /// <summary>Choose a concrete avoidance/relay site for the victim.</summary>
    SelectAvoidancePoint = 1,

    /// <summary>Reserve a collision-free detour to the avoidance site (via TrafficControl TryReserve).</summary>
    ReserveDetour = 2,

    /// <summary>Dispatch the victim onto the detour toward the avoidance site.</summary>
    DispatchToAvoid = 3,

    /// <summary>Wait until the original circular wait is confirmed cleared.</summary>
    ConfirmCleared = 4,

    /// <summary>Restore the victim's navigation toward its original goal (the AJR <c>Recover</c>).</summary>
    Recover = 5,

    /// <summary>Terminal: the plan completed successfully.</summary>
    Completed = 6,

    /// <summary>Terminal: the plan could not proceed and was abandoned/escalated.</summary>
    Aborted = 7,
}
