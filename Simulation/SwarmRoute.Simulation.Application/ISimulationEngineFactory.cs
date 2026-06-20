using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Creates an isolated in-memory engine for one simulation run. Composition belongs to the Host/Infra layer;
/// the Application service only needs the resulting cycle and roadmap id.
/// </summary>
public interface ISimulationEngineFactory
{
    /// <summary>
    /// Creates a fresh engine over <paramref name="graph"/>, planning with <paramref name="planner"/> and (for
    /// SIPP) the rolling-horizon window <paramref name="horizonWindowMs"/>. The per-request engine has its own
    /// isolated container, so both choices are contained to this run (no shared global state). The caller owns
    /// disposal.
    /// </summary>
    /// <param name="horizonWindowMs">RHCR window in fleet-clock ticks; <see cref="long.MaxValue"/> = unbounded (whole-path).</param>
    /// <param name="preventCycles">When true, turns on grant-time deadlock prevention (WouldCloseCycle) for this run; default off.</param>
    /// <param name="stationCatalog">(FMS-V1 R2) When supplied, the engine additionally wires the Dispatch dock-admission
    /// scheduler (and its service-window calendar) over THIS run's reservation system, exposed as
    /// <see cref="ISimulationEngine.StationScheduler"/>, so an FMS executor's per-tick admission coordinates with the
    /// fleet's transit reservations. Null (the default) ⇒ no scheduler ⇒ <see cref="ISimulationEngine.StationScheduler"/>
    /// is null and the engine is byte-identical to a non-FMS run.</param>
    /// <param name="costBasedAdmission">(FMS-V3) When <see langword="true"/> AND a <paramref name="stationCatalog"/> is
    /// supplied, the dock-admission scheduler is additionally given the traffic-impact analyzer + the optional
    /// <paramref name="fleetPlan"/> + the cost-based admission policy, so a blocking station weighs let-pass vs go-first
    /// numerically. Default <see langword="false"/> ⇒ the scheduler keeps its V2/V1 gate ⇒ byte-identical.</param>
    /// <param name="fleetPlan">(FMS-V3) Optional per-agent priority snapshot the cost-based admission reads to count
    /// high-priority blocked traffic. Ignored unless <paramref name="costBasedAdmission"/> is on.</param>
    ISimulationEngine Create(
        RoadmapGraph graph,
        PlannerKind planner = PlannerKind.Dijkstra,
        long horizonWindowMs = long.MaxValue,
        bool preventCycles = false,
        IStationCatalog? stationCatalog = null,
        bool costBasedAdmission = false,
        IFleetPlanProvider? fleetPlan = null);
}

/// <summary>A disposable simulation engine instance with its own coordination/traffic state.</summary>
public interface ISimulationEngine : IAsyncDisposable
{
    /// <summary>The roadmap id under which the engine registered its graph.</summary>
    Guid RoadmapId { get; }

    /// <summary>The coordination cycle that drives planning and reservation for this engine.</summary>
    IFleetCoordinationCycle Cycle { get; }

    /// <summary>
    /// The tick clock the driver advances each tick. It is the SAME instance the engine's coordination cycle
    /// reads for reservation timing, so reserved intervals and execution ticks share one axis.
    /// </summary>
    ManualFleetClock Clock { get; }

    /// <summary>(FMS-V1 R2) The dock-admission scheduler over this run's reservation system, or <see langword="null"/>
    /// when the engine was created without a <see cref="StationDefinition"/> catalog. An FMS closed-loop run threads
    /// it into the driver so service-window admission shares the same reservation table the fleet plans on.</summary>
    IStationScheduler? StationScheduler => null;
}
