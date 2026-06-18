namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Inputs to one in-memory simulation run. A <paramref name="Width"/>×<paramref name="Height"/> grid field is
/// built, <paramref name="AgvCount"/> AGVs are each assigned a distinct start and a distinct goal, and the REAL
/// engine (PathPlanning + TrafficControl + Coordination) drives them to completion collision-free.
/// </summary>
/// <param name="Width">Grid width (columns), measured in control points. Must be ≥ 1.</param>
/// <param name="Height">Grid height (rows), measured in control points. Must be ≥ 1.</param>
/// <param name="AgvCount">Number of AGVs. <c>Width*Height</c> must be ≥ <c>2*AgvCount</c> so distinct starts/goals fit.</param>
/// <param name="Seed">
/// Optional seed for the start/goal assignment RNG. When omitted a fixed default is used so a given request is
/// reproducible.
/// </param>
public sealed record SimulationRequest(int Width, int Height, int AgvCount, int? Seed = null);
