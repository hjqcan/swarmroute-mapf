using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// Grant-time deadlock <b>prevention</b> (v2 constructive liveness): decides whether granting/queuing a candidate
/// reservation would close a wait-for cycle in the resource-allocation graph. Consulted inside
/// <c>ReservationTable.TryGrant</c> before any contended edge is recorded, so a cycle-closing request is refused
/// (<see cref="Shared.AllocationOutcome.CycleAverted"/>) and the planner re-routes — the cycle never forms.
/// <para>
/// The port speaks only the frozen Kernel vocabulary (<see cref="ResourceAllocationGraphSnapshot"/>,
/// <see cref="ResourceRef"/>), so the TrafficControl domain expresses it with no new dependency. The real
/// implementation (composition root) reuses the Deadlock context's RAG builder + cycle detector, keeping
/// prevention and post-hoc detection on byte-identical cycle semantics. The default is
/// <see cref="NullWouldCloseCycleDetector"/> (always false) so v0/v1 behaviour is unchanged until prevention is
/// wired on.
/// </para>
/// </summary>
public interface IWouldCloseCycleDetector
{
    /// <summary>
    /// True when adding <paramref name="candidateAgentId"/>'s would-be wait edges to the current graph puts that
    /// agent on a cycle (a circular wait that granting/queuing this path would close).
    /// </summary>
    /// <param name="currentEdges">The live RAG: <c>Owns</c> = active leases, <c>Waits</c> = current contended requests.</param>
    /// <param name="candidateAgentId">The agent requesting the reservation.</param>
    /// <param name="candidateWaitEdges">
    /// The wait edges the candidate would add: for each blocked cell, the (owner, resource) it would block on.
    /// </param>
    bool WouldCloseCycle(
        ResourceAllocationGraphSnapshot currentEdges,
        string candidateAgentId,
        IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges);
}

/// <summary>
/// The default detector: prevention OFF. Always returns false, so <c>TryGrant</c> behaves exactly as v0/v1 (a
/// blocked path is queued, and any resulting deadlock is caught by the reactive RAG detector). The composition
/// root replaces this with the RAG-backed implementation to turn constructive prevention on.
/// </summary>
public sealed class NullWouldCloseCycleDetector : IWouldCloseCycleDetector
{
    /// <summary>The shared stateless instance.</summary>
    public static NullWouldCloseCycleDetector Instance { get; } = new();

    /// <inheritdoc />
    public bool WouldCloseCycle(
        ResourceAllocationGraphSnapshot currentEdges,
        string candidateAgentId,
        IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges) => false;
}
