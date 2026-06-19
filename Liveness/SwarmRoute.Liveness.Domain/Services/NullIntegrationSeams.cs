namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// No-op <see cref="IAvoidancePointSelector"/> for standalone builds/tests. Always reports "no avoidance
/// site available" so the resolver escalates rather than pretending a detour exists.
/// <para>TODO(integration): replace with a real selector backed by the road-map (avoid sites) and the
/// live reservation view.</para>
/// </summary>
public sealed class NullAvoidancePointSelector : IAvoidancePointSelector
{
    /// <inheritdoc />
    public string? SelectAvoidancePoint(string victimAgentId, IReadOnlySet<string>? excludedSiteIds = null) => null;
}

/// <summary>
/// No-op <see cref="IDetourReservationService"/> for standalone builds/tests. Always fails the
/// reservation (nothing is actually wired to TrafficControl yet).
/// <para>TODO(integration): delegate to <c>ITrafficCoordinatorAppService.TryReserveAsync</c>.</para>
/// </summary>
public sealed class NullDetourReservationService : IDetourReservationService
{
    /// <inheritdoc />
    public Task<bool> TryReserveDetourAsync(
        string victimAgentId,
        string avoidanceSiteId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// Optimistic <see cref="IClearanceConfirmer"/> for standalone builds/tests: assumes that once the
/// victim has been dispatched the cycle has cleared.
/// <para>TODO(integration): confirm via a fresh TrafficControl snapshot / re-detection.</para>
/// </summary>
public sealed class NullClearanceConfirmer : IClearanceConfirmer
{
    /// <inheritdoc />
    public bool IsCleared(string victimAgentId) => true;
}
