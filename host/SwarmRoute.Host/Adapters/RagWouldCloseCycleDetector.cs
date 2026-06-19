using AJR.Platform.Algorithms.Graphs;
using SwarmRoute.Deadlock.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// The real grant-time cycle-prevention oracle: reuses the Deadlock context's <see cref="ResourceAllocationGraph"/>
/// builder and the vendored <see cref="CyclesDetector"/> so prevention and post-hoc detection share byte-identical
/// cycle semantics (same <c>occupySite_</c> pivot, same DFS). It lives in the composition root because that is the
/// one place allowed to bridge TrafficControl ↔ Deadlock — exactly like <c>TrafficSnapshotDeadlockAdapter</c> and
/// <c>TrafficDetourReservationAdapter</c>. Stateless → singleton-safe.
/// </summary>
public sealed class RagWouldCloseCycleDetector : IWouldCloseCycleDetector
{
    /// <inheritdoc />
    public bool WouldCloseCycle(
        ResourceAllocationGraphSnapshot currentEdges,
        string candidateAgentId,
        IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges)
    {
        if (candidateWaitEdges is null || candidateWaitEdges.Count == 0 || string.IsNullOrWhiteSpace(candidateAgentId))
            return false;

        // Add the candidate's would-be wait edges (agent → resource) to the live graph, then ask whether the
        // candidate now lies on a cycle. The owner edges (resource → owner) already exist in currentEdges.Owns —
        // that is WHY the candidate is blocked — so its new wait edge is all that is needed to close a loop.
        var waits = new List<(string AgentId, ResourceRef Resource)>(currentEdges.Waits);
        foreach (var (_, resource) in candidateWaitEdges)
            waits.Add((candidateAgentId, resource));

        var rag = ResourceAllocationGraph.FromSnapshot(new ResourceAllocationGraphSnapshot(currentEdges.Owns, waits));
        var cyclic = CyclesDetector.CyclicVertices(rag.Build(), ResourceAllocationGraph.AgentPrefix);
        return cyclic.Contains(ResourceAllocationGraph.AgentPrefix + candidateAgentId.Trim());
    }
}
