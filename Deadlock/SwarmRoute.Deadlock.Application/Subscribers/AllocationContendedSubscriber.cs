using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.Deadlock.Application.Contract.Dtos;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Application.Subscribers;

/// <summary>
/// Reacts to the <c>TrafficControl.Allocation.Contended</c> integration event by pulling a consistent
/// resource-allocation snapshot and running a deadlock scan. This is the trigger seam described in the
/// architecture (TrafficControl → Deadlock).
/// <para>
/// <b>v0 transport:</b> the in-process event bus calls this through <see cref="IIntegrationEventHandler"/>.
/// A CAP-backed host can bind the same handler to <c>TrafficControl.Allocation.Contended</c> later without
/// changing the application flow.
/// </para>
/// </summary>
public sealed class AllocationContendedSubscriber : IIntegrationEventHandler
{
    public const string EventName = "TrafficControl.Allocation.Contended";

    private static readonly AsyncLocal<int> ScanDepth = new();

    private readonly IDeadlockSnapshotProvider _snapshotProvider;
    private readonly IDeadlockAppService _deadlockAppService;

    public AllocationContendedSubscriber(
        IDeadlockSnapshotProvider snapshotProvider,
        IDeadlockAppService deadlockAppService)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _deadlockAppService = deadlockAppService ?? throw new ArgumentNullException(nameof(deadlockAppService));
    }

    /// <inheritdoc />
    public bool CanHandle(Event domainEvent)
        => domainEvent is IIntegrationEvent integrationEvent
           && string.Equals(integrationEvent.EventName, EventName, StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task HandleAsync(Event domainEvent, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(domainEvent))
            return;

        await HandleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a contended-allocation notification: fetch the current snapshot and scan it for deadlocks.
    /// Returns the resulting <see cref="DeadlockReportDto"/>.
    /// </summary>
    /// <remarks>
    /// For v0 the payload is unused — any contention triggers a full scan.
    /// </remarks>
    public async Task<DeadlockReportDto> HandleAsync(CancellationToken cancellationToken = default)
    {
        // Detour reservation can itself publish Allocation.Contended through the in-process bus. Do not recursively
        // open a second case while the current scan/resolution is still unwinding.
        if (ScanDepth.Value > 0)
            return DeadlockReportDto.Empty;

        ScanDepth.Value++;
        try
        {
            var snapshot = await _snapshotProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return await _deadlockAppService.ScanAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ScanDepth.Value--;
        }
    }
}
