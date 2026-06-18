using Microsoft.Extensions.Logging.Abstractions;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class TrafficCoordinatorAppServiceTests
{
    private sealed class CapturingPublisher : IIntegrationEventPublisher
    {
        public List<Event> Published { get; } = [];

        public Task PublishAsync(IEnumerable<Event> domainEvents, CancellationToken cancellationToken = default)
        {
            Published.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private static (ITrafficCoordinatorAppService svc, ReservationTable table) Build(IResourceTopology topology)
    {
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);
        var svc = new TrafficCoordinatorAppService(table, allocator, NullLogger<TrafficCoordinatorAppService>.Instance);
        return (svc, table);
    }

    [Fact]
    public async Task TryReserve_grants_then_crossing_path_queued_then_release_unblocks()
    {
        var (svc, table) = Build(EmptyTopology);

        Assert.Equal(
            AllocationOutcome.Granted,
            await svc.TryReserveAsync(CpPath(0, 100, "S1", "S2", "S3"), "AGV-A"));

        // Crossing at S2 over an overlapping window -> queued.
        Assert.Equal(
            AllocationOutcome.Queued,
            await svc.TryReserveAsync(CpPath(50, 100, "X1", "S2", "X3"), "AGV-B"));

        // AGV-A drives past S2 and releases it.
        await svc.ReleaseAsync("AGV-A", new[] { Cp("S1"), Cp("S2") });
        Assert.DoesNotContain(table.ActiveLeases, l => l.AgentId == "AGV-A" && l.Resource == Cp("S2"));

        // Now AGV-B can take a path through S2 (time-separated from the remaining AGV-A lease on S3).
        Assert.Equal(
            AllocationOutcome.Granted,
            await svc.TryReserveAsync(CpPath(400, 100, "X1", "S2", "X4"), "AGV-B"));
    }

    [Fact]
    public async Task Release_frees_full_closure_through_the_seam()
    {
        var topology = ClosureTopology(Cp("S1"), Block("B1"), Cp("I1"));
        var (svc, table) = Build(topology);

        await svc.TryReserveAsync(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        Assert.Equal(3, table.ActiveLeases.Count); // S1 + B1 + I1

        await svc.ReleaseAsync("AGV-A", new[] { Cp("S1") });

        Assert.Empty(table.ActiveLeases); // closure fully released, no leak
    }

    [Fact]
    public async Task ManualUnlock_drains_and_publishes_release_events()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        table.ClearDomainEvents();
        var publisher = new CapturingPublisher();
        var svc = new TrafficControlOperatorAppService(table, publisher);

        var freed = await svc.ManualUnlockAsync(new("AGV-A", null));

        Assert.Equal(1, freed);
        Assert.Contains(publisher.Published, e => e.GetType().Name == "ReservationReleasedEvent");
        Assert.True(table.DomainEvents is null || table.DomainEvents.Count == 0);
    }
}
