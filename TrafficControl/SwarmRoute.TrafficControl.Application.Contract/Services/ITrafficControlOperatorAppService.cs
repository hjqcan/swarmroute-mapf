using SwarmRoute.TrafficControl.Application.Contract.Dtos;

namespace SwarmRoute.TrafficControl.Application.Contract.Services;

/// <summary>
/// Operator-facing application service for the TrafficControl console: inspect live occupancy and perform
/// manual overrides (force-unlock an agent's holds). Distinct from the hot-path
/// <see cref="ITrafficCoordinatorAppService"/> used by the control loop.
/// </summary>
public interface ITrafficControlOperatorAppService
{
    /// <summary>Returns the current occupancy snapshot (active leases + contended requests + state version).</summary>
    OccupancyDto GetOccupancy();

    /// <summary>
    /// Force-releases the leases described by <paramref name="request"/> (specific resources + closure, or all
    /// of the agent's leases when none are specified), publishes release events, and returns the number of
    /// leases freed.
    /// </summary>
    Task<int> ManualUnlockAsync(ManualUnlockRequest request, CancellationToken cancellationToken = default);
}
