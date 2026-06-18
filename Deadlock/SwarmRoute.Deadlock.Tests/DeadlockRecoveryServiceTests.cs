using System.Linq;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Deadlock.Application.Services;
using SwarmRoute.Deadlock.Domain.Aggregates;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Tests;

public class DeadlockRecoveryServiceTests
{
    /// <summary>Opens a resolution whose plan is parked at ConfirmCleared (the state Recover expects).</summary>
    private static (DeadlockCase Case, AvoidancePlan Plan) OpenAtConfirmCleared(string victim)
    {
        var @case = DeadlockCase.Detect(DeadlockCycle.FromAgentIds([victim, "Z"]));
        @case.RequestResolution(victim, ResolutionStrategy.SendToAvoidSite, "V");
        var plan = new AvoidancePlan(Guid.NewGuid(), @case.Id, victim);
        plan.AdvanceToSelectAvoidancePoint();
        plan.RecordAvoidancePoint("V");
        plan.RecordDetourReserved();
        plan.RecordDispatched(); // → ConfirmCleared
        return (@case, plan);
    }

    private static AvoidanceDeadlockResolver Resolver(bool cleared) => new(
        new DeterministicVictimSelector(),
        new NullAvoidancePointSelector(),
        new NullDetourReservationService(),
        new StubClearanceConfirmer(cleared));

    [Fact]
    public async Task TryRecoverAll_WhenCleared_RecoversVictim_PublishesResolved_ClosesRegistry()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var (c, p) = OpenAtConfirmCleared("A");
        registry.Open(c, p);
        var publisher = new CapturingIntegrationEventPublisher();
        var svc = new DeadlockRecoveryService(registry, Resolver(cleared: true), publisher);

        var recovered = await svc.TryRecoverAllAsync();

        Assert.Equal(["A"], recovered);
        Assert.Contains(publisher.Published, e => e is DeadlockCaseResolvedEvent);
        Assert.False(registry.HasOpen("A"));
        Assert.Equal(DeadlockCaseStatus.Resolved, c.Status);
    }

    [Fact]
    public async Task TryRecoverAll_WhenNotCleared_NoOps_KeepsResolutionOpen()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var (c, p) = OpenAtConfirmCleared("A");
        registry.Open(c, p);
        var publisher = new CapturingIntegrationEventPublisher();
        var svc = new DeadlockRecoveryService(registry, Resolver(cleared: false), publisher);

        var recovered = await svc.TryRecoverAllAsync();

        Assert.Empty(recovered);
        Assert.Empty(publisher.Published);
        Assert.True(registry.HasOpen("A"));
    }

    [Fact]
    public async Task EscalateLivelock_MarksCaseEscalated_PublishesEscalated_ClosesRegistry()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var (c, p) = OpenAtConfirmCleared("A");
        registry.Open(c, p);
        var publisher = new CapturingIntegrationEventPublisher();
        var svc = new DeadlockEscalationService(registry, publisher);

        var escalated = await svc.EscalateLivelockAsync("A", "no-progress");

        Assert.True(escalated);
        Assert.Equal(DeadlockCaseStatus.Escalated, c.Status);
        Assert.Equal(DeadlockKind.Livelock, c.Kind);
        Assert.Contains(publisher.Published, e => e is DeadlockCaseEscalatedEvent);
        Assert.False(registry.HasOpen("A"));
    }

    [Fact]
    public async Task EscalateLivelock_WhenNoOpenResolution_ReturnsFalse()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var publisher = new CapturingIntegrationEventPublisher();
        var svc = new DeadlockEscalationService(registry, publisher);

        Assert.False(await svc.EscalateLivelockAsync("ghost"));
        Assert.Empty(publisher.Published);
    }
}
