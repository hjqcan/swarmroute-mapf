using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Application.Contract.Services;

/// <summary>
/// The write seam into TrafficControl (frozen cross-context contract, consumed by Coordination). It is the
/// in-process counterpart of the original engine's lock / unlock: <see cref="TryReserve"/> ports
/// <c>GraphMap.GeneratePath</c>'s whole-path lock, and <see cref="Release"/> ports <c>GraphMap.UnlockPath</c>
/// (with the parent-block + interference release leak fixed).
/// </summary>
/// <remarks>
/// Operates against the singleton, in-memory authoritative <c>ReservationTable</c> (the single writer,
/// invariant I5). Calls are synchronous and cheap by design — the control loop calls them every tick.
/// </remarks>
public interface ITrafficCoordinatorAppService
{
    /// <summary>
    /// Attempts to reserve the whole <paramref name="path"/> for <paramref name="agentId"/>. Returns
    /// <see cref="AllocationOutcome.Granted"/> when every resource (and its closure) was free and the leases
    /// were created; otherwise <see cref="AllocationOutcome.Queued"/> / <see cref="AllocationOutcome.Blocked"/>
    /// with a contended request recorded (which the caller uses to prune and replan).
    /// </summary>
    AllocationOutcome TryReserve(SpaceTimePath path, string agentId);

    /// <summary>
    /// Releases the leases <paramref name="agentId"/> holds on the <paramref name="passedResources"/> it has
    /// driven past — including each resource's parent block and interference closure (the leak the original
    /// <c>UnlockPath</c> left behind). Monotonic: only releases the past (invariant I6).
    /// </summary>
    void Release(string agentId, IReadOnlyList<ResourceRef> passedResources);
}
