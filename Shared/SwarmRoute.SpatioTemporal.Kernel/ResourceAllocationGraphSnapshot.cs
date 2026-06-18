namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// Immutable snapshot of the Resource Allocation Graph (RAG) edges at a point in time, used by the
/// Deadlock context to build its wait-for graph and run cycle detection. TrafficControl produces it;
/// Deadlock consumes it (never mutating, never holding TrafficControl locks).
/// </summary>
/// <param name="Owns">
/// "Owns" / held edges: agent <c>AgentId</c> currently holds resource <c>ResourceId</c>.
/// </param>
/// <param name="Waits">
/// "Waits" / request edges: agent <c>AgentId</c> is blocked waiting on resource <c>ResourceId</c>.
/// </param>
public sealed record ResourceAllocationGraphSnapshot(
    IReadOnlyList<(string AgentId, string ResourceId)> Owns,
    IReadOnlyList<(string AgentId, string ResourceId)> Waits);
