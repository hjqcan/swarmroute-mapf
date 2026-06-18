using SwarmRoute.StateMachine.Core;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Domain.StateMachine;

/// <summary>
/// Guard: the lease's resource must be free (no other agent's overlapping lease) for the transition to fire.
/// Mirrors the original "occupied by another → cannot lock" check. Evaluated against the
/// <see cref="LeaseTransitionContext"/>.
/// </summary>
public sealed class ResourceAvailableGuard : IStateGuard<LeaseState, LeaseTrigger>
{
    /// <inheritdoc />
    public string Name => "ResourceAvailable";

    /// <inheritdoc />
    public Task<bool> CanTransitionAsync(LeaseState fromState, LeaseState toState, LeaseTrigger trigger, object? context = null)
    {
        if (context is not LeaseTransitionContext ctx)
            return Task.FromResult(true);
        return Task.FromResult(ctx.Table.IsFreeForExcept(ctx.Resource, ctx.Interval, ctx.AgentId));
    }

    /// <inheritdoc />
    public string GetFailureReason() => TrafficControlErrorCodes.ResourceContended;
}

/// <summary>
/// Guard: the candidate must have no conflict (vertex / edge-swap / following / interference) against the
/// live leases. Wraps <see cref="IConflictDetector"/> over the single contended cell.
/// </summary>
public sealed class NoConflictGuard : IStateGuard<LeaseState, LeaseTrigger>
{
    private readonly IConflictDetector _conflictDetector;

    public NoConflictGuard(IConflictDetector conflictDetector)
        => _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));

    /// <inheritdoc />
    public string Name => "NoConflict";

    /// <inheritdoc />
    public Task<bool> CanTransitionAsync(LeaseState fromState, LeaseState toState, LeaseTrigger trigger, object? context = null)
    {
        if (context is not LeaseTransitionContext ctx)
            return Task.FromResult(true);

        var path = new SpatioTemporal.Kernel.SpaceTimePath(
            new[] { new SpatioTemporal.Kernel.SpaceTimeCell(ctx.Resource, ctx.Interval) });
        var conflicts = _conflictDetector.Detect(ctx.Table, path, ctx.AgentId);
        return Task.FromResult(conflicts.Count == 0);
    }

    /// <inheritdoc />
    public string GetFailureReason() => TrafficControlErrorCodes.ConflictingLease;
}

/// <summary>
/// Guard: the lease's resource must not be blacklisted for the agent. Ports
/// <c>MapResource.AGVBlackList.Contains(agvId)</c> via <see cref="IResourceTopology"/>.
/// </summary>
public sealed class NotBlacklistedGuard : IStateGuard<LeaseState, LeaseTrigger>
{
    private readonly IResourceTopology _topology;

    public NotBlacklistedGuard(IResourceTopology topology)
        => _topology = topology ?? throw new ArgumentNullException(nameof(topology));

    /// <inheritdoc />
    public string Name => "NotBlacklisted";

    /// <inheritdoc />
    public Task<bool> CanTransitionAsync(LeaseState fromState, LeaseState toState, LeaseTrigger trigger, object? context = null)
    {
        if (context is not LeaseTransitionContext ctx)
            return Task.FromResult(true);
        return Task.FromResult(!_topology.IsBlacklisted(ctx.Resource, ctx.AgentId));
    }

    /// <inheritdoc />
    public string GetFailureReason() => TrafficControlErrorCodes.ResourceBlacklisted;
}
