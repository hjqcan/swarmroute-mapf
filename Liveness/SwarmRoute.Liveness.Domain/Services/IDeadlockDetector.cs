using System.Collections.Generic;
using SwarmRoute.Liveness.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Liveness.Domain.Services;

/// <summary>
/// Detects circular-wait deadlocks from a point-in-time Resource-Allocation-Graph snapshot.
/// <para>
/// Ports <c>AJR.MAPF.ConflictDetect.IndependenceDetection.DeadlockDetect</c>: build the RAG and run
/// cycle detection (<c>CyclesDetector.CyclicVertices(graph, "agent_")</c>) over it. The detector is a
/// pure analyser — it holds no state and never mutates TrafficControl.
/// </para>
/// </summary>
public interface IDeadlockDetector
{
    /// <summary>
    /// Builds the RAG from <paramref name="snapshot"/> and returns the set of circular waits found.
    /// Returns an empty list when the snapshot is acyclic. Agents participating in independent
    /// simultaneous cycles are returned as separate <see cref="DeadlockCycle"/> entries.
    /// </summary>
    IReadOnlyList<DeadlockCycle> Detect(ResourceAllocationGraphSnapshot snapshot);
}
