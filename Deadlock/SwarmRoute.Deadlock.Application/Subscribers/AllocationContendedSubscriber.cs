using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.Deadlock.Application.Contract.Dtos;
using SwarmRoute.Deadlock.Application.Contract.Services;

namespace SwarmRoute.Deadlock.Application.Subscribers;

/// <summary>
/// Reacts to the <c>TrafficControl.Allocation.Contended</c> integration event by pulling a consistent
/// resource-allocation snapshot and running a deadlock scan. This is the trigger seam described in the
/// architecture (TrafficControl → Deadlock).
/// <para>
/// <b>v0 stub:</b> the actual CAP subscription (the <c>[CapSubscribe("TrafficControl.Allocation.Contended")]</c>
/// attribute on <see cref="HandleAsync"/>) and the concrete snapshot provider are wired at integration
/// (WS5-4 / WS-X). The class is fully functional against its seams so it compiles and is unit-testable
/// standalone; only the transport binding is deferred.
/// </para>
/// </summary>
public sealed class AllocationContendedSubscriber
{
    private readonly IDeadlockSnapshotProvider _snapshotProvider;
    private readonly IDeadlockAppService _deadlockAppService;

    public AllocationContendedSubscriber(
        IDeadlockSnapshotProvider snapshotProvider,
        IDeadlockAppService deadlockAppService)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _deadlockAppService = deadlockAppService ?? throw new ArgumentNullException(nameof(deadlockAppService));
    }

    /// <summary>
    /// Handles a contended-allocation notification: fetch the current snapshot and scan it for deadlocks.
    /// Returns the resulting <see cref="DeadlockReportDto"/>.
    /// </summary>
    /// <remarks>
    /// TODO(integration): annotate with <c>[CapSubscribe("TrafficControl.Allocation.Contended")]</c> and
    /// accept the event payload DTO (the contended resource/agents) once the EventBus contract type is
    /// available. For v0 the payload is unused — any contention simply triggers a full scan.
    /// </remarks>
    public async Task<DeadlockReportDto> HandleAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshotProvider.GetSnapshotAsync(cancellationToken);
        return await _deadlockAppService.ScanAsync(snapshot, cancellationToken);
    }
}
