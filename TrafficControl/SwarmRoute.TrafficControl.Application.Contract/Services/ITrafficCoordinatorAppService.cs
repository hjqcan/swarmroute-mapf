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
/// invariant I5). Calls are in-process but async because successful writes must publish integration events
/// before the current DI scope is disposed.
/// </remarks>
public interface ITrafficCoordinatorAppService
{
    /// <summary>
    /// Attempts to reserve the whole <paramref name="path"/> for <paramref name="agentId"/>. Returns
    /// <see cref="AllocationOutcome.Granted"/> when every resource (and its closure) was free and the leases
    /// were created; otherwise <see cref="AllocationOutcome.Queued"/> / <see cref="AllocationOutcome.Blocked"/>
    /// with a contended request recorded (which the caller uses to prune and replan).
    /// </summary>
    Task<AllocationOutcome> TryReserveAsync(
        SpaceTimePath path,
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the planner-visible CP/Lane resources from <paramref name="path"/> that are currently blocked for
    /// <paramref name="agentId"/>. TrafficControl still detects block/interference closure conflicts internally,
    /// but Coordination receives only resources the planner can prune.
    /// </summary>
    IReadOnlyCollection<ResourceRef> BlockedResources(SpaceTimePath path, string agentId);

    /// <summary>
    /// Releases the leases <paramref name="agentId"/> holds on the <paramref name="passedResources"/> it has
    /// driven past — including each resource's parent block and interference closure (the leak the original
    /// <c>UnlockPath</c> left behind). Monotonic: only releases the past (invariant I6).
    /// </summary>
    Task ReleaseAsync(
        string agentId,
        IReadOnlyList<ResourceRef> passedResources,
        CancellationToken cancellationToken = default);
}
