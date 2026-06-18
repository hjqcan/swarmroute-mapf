using SwarmRoute.Deadlock.Domain.Aggregates;
using SwarmRoute.Deadlock.Domain.Shared.Enums;

namespace SwarmRoute.Deadlock.Tests;

public class AvoidancePlanTests
{
    private static AvoidancePlan NewPlan() => new(Guid.NewGuid(), Guid.NewGuid(), "A");

    [Fact]
    public void NewPlan_StartsAtSelectVictim_WithVersion1()
    {
        var plan = NewPlan();

        Assert.Equal(AvoidancePlanStep.SelectVictim, plan.CurrentStep);
        Assert.Equal("A", plan.VictimAgentId);
        Assert.Equal(1, plan.StateVersion);
        Assert.False(plan.IsTerminal);
    }

    [Fact]
    public void HappyPath_WalksAllStepsToCompleted_AndBumpsVersionEachStep()
    {
        var plan = NewPlan();
        var v = plan.StateVersion;

        plan.AdvanceToSelectAvoidancePoint();
        Assert.Equal(AvoidancePlanStep.SelectAvoidancePoint, plan.CurrentStep);
        Assert.True(plan.StateVersion > v); v = plan.StateVersion;

        plan.RecordAvoidancePoint("avoid-1");
        Assert.Equal(AvoidancePlanStep.ReserveDetour, plan.CurrentStep);
        Assert.Equal("avoid-1", plan.AvoidanceSiteId);
        Assert.True(plan.StateVersion > v); v = plan.StateVersion;

        plan.RecordDetourReserved();
        Assert.Equal(AvoidancePlanStep.DispatchToAvoid, plan.CurrentStep);

        plan.RecordDispatched();
        Assert.Equal(AvoidancePlanStep.ConfirmCleared, plan.CurrentStep);

        plan.RecordCleared();
        Assert.Equal(AvoidancePlanStep.Recover, plan.CurrentStep);

        plan.RecordRecovered();
        Assert.Equal(AvoidancePlanStep.Completed, plan.CurrentStep);
        Assert.True(plan.IsTerminal);
        Assert.True(plan.IsSucceeded);
    }

    [Fact]
    public void OutOfOrderTransition_Throws()
    {
        var plan = NewPlan();
        // Cannot reserve a detour before selecting an avoidance point.
        Assert.Throws<InvalidOperationException>(() => plan.RecordDetourReserved());
    }

    [Fact]
    public void RecordAvoidancePoint_RejectsBlankSite()
    {
        var plan = NewPlan();
        plan.AdvanceToSelectAvoidancePoint();
        Assert.Throws<ArgumentException>(() => plan.RecordAvoidancePoint("  "));
    }

    [Fact]
    public void Abort_FromNonTerminal_MovesToAborted_AndRecordsReason()
    {
        var plan = NewPlan();
        plan.AdvanceToSelectAvoidancePoint();

        plan.Abort("no-site");

        Assert.Equal(AvoidancePlanStep.Aborted, plan.CurrentStep);
        Assert.True(plan.IsTerminal);
        Assert.False(plan.IsSucceeded);
        Assert.Equal("no-site", plan.FailureReason);
    }

    [Fact]
    public void Abort_WhenAlreadyTerminal_IsNoOp()
    {
        var plan = NewPlan();
        plan.AdvanceToSelectAvoidancePoint();
        plan.RecordAvoidancePoint("a");
        plan.RecordDetourReserved();
        plan.RecordDispatched();
        plan.RecordCleared();
        plan.RecordRecovered();
        var versionAtCompletion = plan.StateVersion;

        plan.Abort("late");

        Assert.Equal(AvoidancePlanStep.Completed, plan.CurrentStep);
        Assert.Equal(versionAtCompletion, plan.StateVersion);
    }

    [Fact]
    public void Constructor_RejectsBlankVictim()
    {
        Assert.Throws<ArgumentException>(() => new AvoidancePlan(Guid.NewGuid(), Guid.NewGuid(), " "));
    }
}
