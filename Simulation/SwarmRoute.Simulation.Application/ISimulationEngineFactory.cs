using SwarmRoute.Coordination.Application;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Creates an isolated in-memory engine for one simulation run. Composition belongs to the Host/Infra layer;
/// the Application service only needs the resulting cycle and roadmap id.
/// </summary>
public interface ISimulationEngineFactory
{
    /// <summary>Creates a fresh engine over <paramref name="graph"/>. The caller owns disposal.</summary>
    ISimulationEngine Create(RoadmapGraph graph);
}

/// <summary>A disposable simulation engine instance with its own coordination/traffic state.</summary>
public interface ISimulationEngine : IAsyncDisposable
{
    /// <summary>The roadmap id under which the engine registered its graph.</summary>
    Guid RoadmapId { get; }

    /// <summary>The coordination cycle that drives planning and reservation for this engine.</summary>
    IFleetCoordinationCycle Cycle { get; }
}
