using SwarmRoute.Deadlock.Domain.Aggregates;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Tests;

public class DeadlockResolverTests
{
    private static DeadlockCase NewCase() =>
        DeadlockCase.Detect(DeadlockCycle.FromAgentIds(["B", "A", "C"]));

    [Fact]
    public async Task Solve_WithIntegratedSeams_DispatchesVictimToAvoidSite()
    {
        var resolver = new AvoidanceDeadlockResolver(
            new DeterministicVictimSelector(),
            new FixedAvoidancePointSelector("avoid-99"),
            new AlwaysGrantDetourReservationService(),
            new StubClearanceConfirmer(cleared: true));

        var @case = NewCase();
        var plan = await resolver.SolveAsync(@case);

        // victim is deterministic smallest id "A"
        Assert.Equal("A", plan.VictimAgentId);
        Assert.Equal("avoid-99", plan.AvoidanceSiteId);
        Assert.Equal(AvoidancePlanStep.ConfirmCleared, plan.CurrentStep);
        Assert.Equal(DeadlockCaseStatus.Resolving, @case.Status);
        Assert.Equal("avoid-99", @case.SuggestedAvoidTarget);
    }

    [Fact]
    public async Task Recover_AfterSolve_CompletesPlanAndResolvesCase()
    {
        var resolver = new AvoidanceDeadlockResolver(
            new DeterministicVictimSelector(),
            new FixedAvoidancePointSelector("avoid-99"),
            new AlwaysGrantDetourReservationService(),
            new StubClearanceConfirmer(cleared: true));

        var @case = NewCase();
        var plan = await resolver.SolveAsync(@case);

        var ok = resolver.Recover(@case, plan);

        Assert.True(ok);
        Assert.Equal(AvoidancePlanStep.Completed, plan.CurrentStep);
        Assert.Equal(DeadlockCaseStatus.Resolved, @case.Status);
    }

    [Fact]
    public async Task Recover_WhenNotCleared_DoesNotResolve()
    {
        var resolver = new AvoidanceDeadlockResolver(
            new DeterministicVictimSelector(),
            new FixedAvoidancePointSelector("avoid-99"),
            new AlwaysGrantDetourReservationService(),
            new StubClearanceConfirmer(cleared: false));

        var @case = NewCase();
        var plan = await resolver.SolveAsync(@case);

        Assert.False(resolver.Recover(@case, plan));
        Assert.Equal(AvoidancePlanStep.ConfirmCleared, plan.CurrentStep);
        Assert.Equal(DeadlockCaseStatus.Resolving, @case.Status);
    }

    [Fact]
    public async Task Solve_WithNoAvoidSite_EscalatesCaseAndAbortsPlan()
    {
        var resolver = new AvoidanceDeadlockResolver(
            new DeterministicVictimSelector(),
            new NullAvoidancePointSelector(),          // no site
            new NullDetourReservationService(),
            new NullClearanceConfirmer());

        var @case = NewCase();
        var plan = await resolver.SolveAsync(@case);

        Assert.Equal(AvoidancePlanStep.Aborted, plan.CurrentStep);
        Assert.Equal(DeadlockCaseStatus.Escalated, @case.Status);
        Assert.Equal("A", @case.VictimAgentId);
        Assert.DoesNotContain(@case.DomainEvents!, e => e is DeadlockCaseResolutionRequestedEvent);
        Assert.Contains(@case.DomainEvents!, e => e is DeadlockCaseEscalatedEvent);
    }

    [Fact]
    public async Task Solve_WhenDetourDenied_Escalates()
    {
        var resolver = new AvoidanceDeadlockResolver(
            new DeterministicVictimSelector(),
            new FixedAvoidancePointSelector("avoid-1"),
            new NullDetourReservationService(),        // denies
            new NullClearanceConfirmer());

        var @case = NewCase();
        var plan = await resolver.SolveAsync(@case);

        Assert.Equal(AvoidancePlanStep.Aborted, plan.CurrentStep);
        Assert.Equal(DeadlockCaseStatus.Escalated, @case.Status);
        Assert.Equal("A", @case.VictimAgentId);
        Assert.DoesNotContain(@case.DomainEvents!, e => e is DeadlockCaseResolutionRequestedEvent);
        var escalated = @case.DomainEvents!.OfType<DeadlockCaseEscalatedEvent>().Single();
        Assert.Equal("avoid-1", @case.SuggestedAvoidTarget);
        Assert.Equal("A", escalated.VictimAgentId);
    }
}
