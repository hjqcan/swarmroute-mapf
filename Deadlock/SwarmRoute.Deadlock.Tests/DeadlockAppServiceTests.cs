using System.Linq;
using SwarmRoute.Deadlock.Application.Services;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Deadlock.Tests;

public class DeadlockAppServiceTests
{
    private static DeadlockAppService BuildService(
        CapturingIntegrationEventPublisher publisher,
        IAvoidancePointSelector? avoidSelector = null,
        IDetourReservationService? detour = null)
    {
        var resolver = new AvoidanceDeadlockResolver(
            new DeterministicVictimSelector(),
            avoidSelector ?? new NullAvoidancePointSelector(),
            detour ?? new NullDetourReservationService(),
            new NullClearanceConfirmer());

        return new DeadlockAppService(new RagDeadlockDetector(), resolver, publisher);
    }

    [Fact]
    public async Task Scan_HealthySnapshot_ReturnsEmptyReport_AndPublishesNothing()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        var svc = BuildService(publisher);

        var report = await svc.ScanAsync(new ResourceAllocationGraphSnapshot([], []));

        Assert.False(report.HasDeadlock);
        Assert.Empty(report.Cycles);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Scan_TwoAgentCycle_OpensCase_PicksVictim_AndPublishesEvents()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        // Integrated seams so resolution proceeds (and a suggested avoid target is produced).
        var svc = BuildService(
            publisher,
            new FixedAvoidancePointSelector("avoid-A"),
            new AlwaysGrantDetourReservationService());

        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r2")
            .Owns("B", "r2").Waits("B", "r1")
            .Build();

        var report = await svc.ScanAsync(snapshot);

        Assert.True(report.HasDeadlock);
        var cycle = Assert.Single(report.Cycles);
        Assert.Equal(["A", "B"], cycle.AgentIds);
        Assert.Equal("A", cycle.VictimAgentId);
        Assert.Equal("avoid-A", cycle.SuggestedAvoidTarget);

        // Detected + ResolutionRequested published.
        Assert.Contains(publisher.Published, e => e is DeadlockCaseDetectedEvent);
        Assert.Contains(publisher.Published, e => e is DeadlockCaseResolutionRequestedEvent);
    }

    [Fact]
    public async Task Scan_TwoIndependentCycles_ReportsTwo()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        var svc = BuildService(publisher);

        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r2")
            .Owns("B", "r2").Waits("B", "r1")
            .Owns("C", "r3").Waits("C", "r4")
            .Owns("D", "r4").Waits("D", "r3")
            .Build();

        var report = await svc.ScanAsync(snapshot);

        Assert.Equal(2, report.CycleCount);
        Assert.Equal(["A", "B", "C", "D"], report.AffectedAgentIds.OrderBy(a => a).ToList());
    }

    [Fact]
    public async Task Scan_WithoutIntegratedSeams_StillReportsVictim_ViaResolutionRequested()
    {
        var publisher = new CapturingIntegrationEventPublisher();
        var svc = BuildService(publisher); // Null seams → escalates, but victim still chosen

        var report = await svc.ScanAsync(SnapshotBuilder.Cycle(3));

        var cycle = Assert.Single(report.Cycles);
        Assert.Equal("A", cycle.VictimAgentId);
        Assert.Contains(publisher.Published, e => e is DeadlockCaseResolutionRequestedEvent);
    }
}
