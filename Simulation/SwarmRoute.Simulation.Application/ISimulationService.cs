namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Runs a self-contained, in-memory multi-AGV simulation: builds a grid field, wires a fresh REAL engine
/// (PathPlanning + TrafficControl + Coordination) per request, drives the fleet to completion collision-free,
/// and returns the field + per-agent paths + a tick-by-tick replay timeline. No database.
/// </summary>
public interface ISimulationService
{
    /// <summary>
    /// Builds and runs one simulation described by <paramref name="request"/>.
    /// </summary>
    /// <exception cref="ArgumentException">When the request is invalid (e.g. the grid cannot hold distinct starts/goals).</exception>
    Task<SimulationResultDto> RunAsync(SimulationRequest request, CancellationToken cancellationToken = default);
}
