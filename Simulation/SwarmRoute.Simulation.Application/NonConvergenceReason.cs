namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (FMS-V2) Why a single not-arrived AGV failed to converge in a <see cref="FleetLoopStatus.DidNotConverge"/> run
/// (未收斂原因). The diagnostics layer classifies each stranded agent into exactly one of these so a failed run is
/// actionable ("the goal is walled off by parked vehicles" vs "the fleet just ran out of ticks") rather than an
/// opaque "did not converge".
/// <para>
/// This is pure post-hoc analysis over the recorded timeline plus the roadmap — it never perturbs the run, and it
/// is emitted ONLY on a non-converged run, so a converged run's response is byte-identical.
/// </para>
/// </summary>
public enum NonConvergenceReason
{
    /// <summary>The agent's goal is reachable and it was not parked-blocked or standing off — the fleet simply ran
    /// out of the tick budget before it arrived (the "give it more ticks" case).</summary>
    TickBudgetExceeded = 0,

    /// <summary>The agent's goal is UNREACHABLE on the graph once every parked (finished) vehicle's cell is removed:
    /// a parked vehicle sits on the only approach to the goal. This is the permanent goal-blocking the
    /// WarehouseWellFormed scenario-semantics fix is meant to eliminate (a workstation goal a serviced AGV parked
    /// on). Its dominance under WarehouseWellFormed is the M-F2 regression signal.</summary>
    ParkedGoalBlocker = 1,

    /// <summary>The agent finished its task (e.g. was serviced) but could not find a free parking/buffer slot to
    /// clear to — every resting site was taken (停車位飽和).</summary>
    ParkingSaturation = 2,

    /// <summary>The agent's goal is reachable on the live graph, but it sat blocked for a long stretch in a physical
    /// standoff the executor never resolved (a head-on swap / circular chain) (僵持未解).</summary>
    LiveStandoffUnresolved = 3,

    /// <summary>The agent's goal does not exist as a roadmap vertex, or no path exists even on the EMPTY graph
    /// (independent of any parked vehicle) — a malformed scenario rather than a traffic outcome.</summary>
    NoWellFormedEndpointPath = 4,

    /// <summary>None of the above could be determined.</summary>
    Unknown = 5
}
