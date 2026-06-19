using System.Linq;
using SwarmRoute.Deadlock.Domain.Aggregates;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Tests;

public class DeadlockCaseTests
{
    private static DeadlockCase NewCase() =>
        DeadlockCase.Detect(DeadlockCycle.FromAgentIds(["A", "B"]));

    [Fact]
    public void Detect_OpensCaseInDetected_RaisesDetectedEvent()
    {
        var @case = NewCase();

        Assert.Equal(DeadlockCaseStatus.Detected, @case.Status);
        Assert.Equal(["A", "B"], @case.AgentIds);
        var evt = Assert.Single(@case.DomainEvents!);
        var detected = Assert.IsType<DeadlockCaseDetectedEvent>(evt);
        Assert.Equal("Deadlock.Case.Detected", detected.EventName);
        Assert.Equal("v1", detected.Version);
        Assert.Equal(@case.Id, detected.AggregateId);
    }

    [Fact]
    public void RequestResolution_MovesToResolving_RaisesResolutionRequested()
    {
        var @case = NewCase();

        @case.RequestResolution("A", ResolutionStrategy.SendToAvoidSite, "avoid-7");

        Assert.Equal(DeadlockCaseStatus.Resolving, @case.Status);
        Assert.Equal("A", @case.VictimAgentId);
        Assert.Equal(ResolutionStrategy.SendToAvoidSite, @case.Strategy);
        Assert.Equal("avoid-7", @case.SuggestedAvoidTarget);

        var requested = @case.DomainEvents!.OfType<DeadlockCaseResolutionRequestedEvent>().Single();
        Assert.Equal("Deadlock.Case.ResolutionRequested", requested.EventName);
        Assert.Equal("A", requested.VictimAgentId);
        Assert.Equal("avoid-7", requested.SuggestedAvoidTarget);
    }

    [Fact]
    public void RequestResolution_RejectsVictimNotInCycle()
    {
        var @case = NewCase();
        Assert.Throws<ArgumentException>(
            () => @case.RequestResolution("Z", ResolutionStrategy.SendToAvoidSite));
    }

    [Fact]
    public void MarkResolved_FromResolving_RaisesResolvedEvent()
    {
        var @case = NewCase();
        @case.RequestResolution("A", ResolutionStrategy.SendToAvoidSite);

        @case.MarkResolved();

        Assert.Equal(DeadlockCaseStatus.Resolved, @case.Status);
        var resolved = @case.DomainEvents!.OfType<DeadlockCaseResolvedEvent>().Single();
        Assert.Equal("Deadlock.Case.Resolved", resolved.EventName);
        Assert.Equal("A", resolved.VictimAgentId);
    }

    [Fact]
    public void MarkResolved_FromDetected_Throws()
    {
        var @case = NewCase();
        Assert.Throws<InvalidOperationException>(() => @case.MarkResolved());
    }

    [Fact]
    public void Escalate_FromDetected_MovesToEscalated_AndIsIdempotent()
    {
        var @case = NewCase();

        @case.Escalate("no-site");
        Assert.Equal(DeadlockCaseStatus.Escalated, @case.Status);

        // idempotent
        @case.Escalate("again");
        Assert.Equal(DeadlockCaseStatus.Escalated, @case.Status);
    }

    [Fact]
    public void EscalateResolutionFailure_RecordsVictim_AndRaisesEscalatedEvent()
    {
        var @case = NewCase();

        @case.EscalateResolutionFailure(
            "A",
            ResolutionStrategy.SendToAvoidSite,
            "avoid-7",
            "denied");

        Assert.Equal(DeadlockCaseStatus.Escalated, @case.Status);
        Assert.Equal("A", @case.VictimAgentId);
        Assert.Equal("avoid-7", @case.SuggestedAvoidTarget);

        var escalated = @case.DomainEvents!.OfType<DeadlockCaseEscalatedEvent>().Single();
        Assert.Equal("Deadlock.Case.Escalated", escalated.EventName);
        Assert.Equal("A", escalated.VictimAgentId);
        Assert.Equal("denied", escalated.Reason);
    }

    [Fact]
    public void Escalate_AfterResolved_Throws()
    {
        var @case = NewCase();
        @case.RequestResolution("A", ResolutionStrategy.SendToAvoidSite);
        @case.MarkResolved();

        Assert.Throws<InvalidOperationException>(() => @case.Escalate());
    }

    [Fact]
    public void Detect_RejectsEmptyCycle()
    {
        Assert.Throws<ArgumentException>(() => DeadlockCycle.FromAgentIds([]));
    }
}
