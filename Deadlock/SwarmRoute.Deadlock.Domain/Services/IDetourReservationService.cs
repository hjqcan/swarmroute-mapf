namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// Integration seam (to be fulfilled by TrafficControl wiring): reserve a detour for the victim agent
/// to the chosen avoidance site, going through the normal <c>TryReserve</c> path so the detour itself
/// never introduces a new collision (safety invariant I1).
/// <para>
/// Left without a production implementation in the Deadlock context — TrafficControl owns reservations.
/// A <c>NullDetourReservationService</c> is provided for standalone builds/tests.
/// </para>
/// </summary>
public interface IDetourReservationService
{
    /// <summary>
    /// Attempts to reserve a collision-free detour for <paramref name="victimAgentId"/> to
    /// <paramref name="avoidanceSiteId"/>. Returns <see langword="true"/> if the detour was granted.
    /// </summary>
    bool TryReserveDetour(string victimAgentId, string avoidanceSiteId);
}
