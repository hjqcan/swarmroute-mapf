using SwarmRoute.Coordination.Application;
using SwarmRoute.Coordination.Application.Deadlock;
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
    /// Creates a fresh engine over <paramref name="graph"/>, planning with <paramref name="planner"/>. The
    /// per-request engine has its own isolated container, so the planner choice is contained to this run (no
    /// shared global state). The caller owns disposal.
    /// </summary>
    ISimulationEngine Create(RoadmapGraph graph, PlannerKind planner = PlannerKind.Dijkstra);
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

    /// <summary>The deadlock redirect projection the driver reads between ticks.</summary>
    IFleetRedirectQuery Redirects { get; }

    /// <summary>Per-tick recovery pump for open deadlock resolutions.</summary>
    Func<CancellationToken, Task<IReadOnlyCollection<string>>> RecoverTick { get; }

    /// <summary>Escalates a redirect that the driver classified as a livelock/no-progress case.</summary>
    Func<string, CancellationToken, Task> EscalateLivelock { get; }
}
