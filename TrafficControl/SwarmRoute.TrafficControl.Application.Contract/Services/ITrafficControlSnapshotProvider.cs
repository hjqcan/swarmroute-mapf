using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.TrafficControl.Application.Contract.Services;

/// <summary>
/// The read seam to the Deadlock context (frozen cross-context contract). Produces an immutable
/// <see cref="ResourceAllocationGraphSnapshot"/> of the live reservation state at the instant of the call,
/// from which Deadlock builds its wait-for graph and runs cycle detection — without ever holding
/// TrafficControl's locks or mutating its state.
/// </summary>
public interface ITrafficControlSnapshotProvider
{
    /// <summary>
    /// Returns a consistent snapshot of the Resource Allocation Graph edges:
    /// <list type="bullet">
    ///   <item><description><c>Owns</c> = one <c>(agentId, resourceId)</c> per active lease;</description></item>
    ///   <item><description><c>Waits</c> = one <c>(agentId, resourceId)</c> per queued / contended request.</description></item>
    /// </list>
    /// </summary>
    ResourceAllocationGraphSnapshot GetSnapshot();
}
