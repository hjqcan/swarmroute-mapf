using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.Deadlock.Application.Contract.Dtos;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Deadlock.Application.Subscribers;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Deadlock.Tests;

public sealed class AllocationContendedSubscriberTests
{
    [Fact]
    public async Task HandleAsync_suppresses_reentrant_deadlock_scans()
    {
        var appService = new ReentrantDeadlockAppService();
        var subscriber = new AllocationContendedSubscriber(new EmptySnapshotProvider(), appService);
        appService.Subscriber = subscriber;

        await subscriber.HandleAsync(new ContendedEvent());

        Assert.Equal(1, appService.ScanCount);
    }

    private sealed class EmptySnapshotProvider : IDeadlockSnapshotProvider
    {
        public Task<ResourceAllocationGraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ResourceAllocationGraphSnapshot([], []));
    }

    private sealed class ReentrantDeadlockAppService : IDeadlockAppService
    {
        public AllocationContendedSubscriber? Subscriber { get; set; }
        public int ScanCount { get; private set; }

        public async Task<DeadlockReportDto> ScanAsync(
            ResourceAllocationGraphSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            if (ScanCount == 1 && Subscriber is not null)
                await Subscriber.HandleAsync(new ContendedEvent(), cancellationToken).ConfigureAwait(false);

            return DeadlockReportDto.Empty;
        }
    }

    private sealed class ContendedEvent : Event, IIntegrationEvent
    {
        public string EventName => AllocationContendedSubscriber.EventName;
        public string Version => "test";
    }
}
