namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (FMS-V2) The high-level scenario a <see cref="SimulationRequest"/> runs under (情境模式). This selects HOW the
/// fleet's starts/goals and the FMS overlay are constructed, layered ON TOP of the existing request fields
/// (<see cref="SimulationRequest.Width"/>/<see cref="SimulationRequest.Height"/>/<see cref="SimulationRequest.AgvCount"/>/<see cref="SimulationRequest.Scenario"/>).
/// <para>
/// The default <see cref="RandomStress"/> is exactly today's behaviour — a shuffled random start/goal layout with
/// no FMS overlay — so a request that does not set the mode is <b>byte-identical</b> to a pre-FMS-V2 run.
/// </para>
/// </summary>
public enum ScenarioMode
{
    /// <summary>
    /// Pre-FMS-V2 behaviour (the default ⇒ byte-identical): every AGV is assigned a distinct random start and a
    /// distinct random goal from the free grid cells, with no station overlay. The classic "throw N agents at a
    /// grid and prove collision-free convergence" stress run.
    /// </summary>
    RandomStress = 0,

    /// <summary>
    /// (FMS-V2) A <b>well-formed warehouse</b> scenario: a ring/edge of <see cref="Map.Domain.Shared.Enums.SiteRole.Parking"/>
    /// and <see cref="Map.Domain.Shared.Enums.SiteRole.Workstation"/> endpoints is carved out of the grid by the
    /// <see cref="Dispatch.Domain.Endpoints.WellFormedEndpointGenerator"/> so the interior transit core stays
    /// connected with egress, every AGV's TASK goal is drawn ONLY from workstation endpoints (starts from
    /// transit/parking), and a serviced AGV clears to a real parking/buffer slot rather than permanently parking on
    /// its workstation. This is the scenario-semantics fix that removes permanent goal-blocking (M-F2).
    /// </summary>
    WarehouseWellFormed = 1,

    /// <summary>
    /// (FMS-V3) A <b>lifelong dispatch</b> scenario: a continuous-operation warehouse over a larger, sparser grid. A
    /// pool of transport tasks (goals at workstation docks) is released over time and the runtime
    /// <see cref="Dispatch.Application.Contract.ITaskDispatcher"/> hands each idle AGV its next task; the instant an
    /// AGV finishes a task and clears to parking it requests another and heads out again — the fleet never stops. The
    /// run is bounded by <see cref="SimulationRequest.LifelongHorizonTicks"/> (not "all arrived"), and the result
    /// carries the additive <see cref="LifelongMetricsDto"/> (throughput, wait, queue depth, parking saturation). When
    /// <see cref="SimulationRequest.LifelongHorizonTicks"/> is left null/0 this mode falls back to a one-shot
    /// <see cref="WarehouseWellFormed"/> pass, so selecting the mode alone is still byte-identical to that scenario.
    /// </summary>
    LifelongDispatch = 2
}
