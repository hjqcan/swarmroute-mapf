using SwarmRoute.PathPlanning.Domain.Aggregates;
using SwarmRoute.PathPlanning.Domain.Events;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Tests;

/// <summary>
/// Behaviour of the <see cref="AgentPlan"/> aggregate: construction from a result raises the right event,
/// <see cref="AgentPlan.Replan"/> and <see cref="AgentPlan.Invalidate"/> bump the version and raise events,
/// and invariants are guarded.
/// </summary>
public sealed class AgentPlanTests
{
    private static readonly Guid Roadmap = Guid.NewGuid();

    private static PlanResult SuccessResult()
    {
        var cells = new List<SpaceTimeCell>
        {
            new(RoadmapGraphSiteRef("A"), new TimeInterval(0, 1000)),
            new(RoadmapGraphSiteRef("B"), new TimeInterval(1000, 1001))
        };
        return PlanResult.Succeeded(new SpaceTimePath(cells), new PlanCost(1000, 1, 1001));
    }

    private static ResourceRef RoadmapGraphSiteRef(string id) => new(ResourceKind.CP, id);

    [Fact]
    public void Computed_plan_sets_state_and_raises_Computed_event()
    {
        var plan = new AgentPlan(Guid.NewGuid(), Roadmap, "AGV-1", "A", "B", SuccessResult());

        Assert.Equal(PlanStatus.Computed, plan.Status);
        Assert.True(plan.HasPath);
        Assert.NotNull(plan.Path);
        Assert.Equal(1, plan.StateVersion);

        var evt = Assert.IsType<AgentPlanComputedEvent>(Assert.Single(plan.DomainEvents!));
        Assert.Equal("PathPlanning.AgentPlan.Computed", evt.EventName);
        Assert.Equal("v1", evt.Version);
        Assert.Equal(plan.Id, evt.AgentPlanId);
        Assert.Equal(new[] { "A", "B" }, evt.SiteSequence);
        Assert.Equal(1000, evt.DistanceUnits);
        Assert.Equal(1, evt.HopCount);
    }

    [Fact]
    public void Failed_plan_sets_state_and_raises_Failed_event()
    {
        var plan = new AgentPlan(Guid.NewGuid(), Roadmap, "AGV-1", "A", "Z", PlanResult.Failed("[PP-003] no route"));

        Assert.Equal(PlanStatus.Failed, plan.Status);
        Assert.False(plan.HasPath);
        Assert.Null(plan.Path);
        Assert.Equal("[PP-003] no route", plan.FailureReason);

        var evt = Assert.IsType<AgentPlanFailedEvent>(Assert.Single(plan.DomainEvents!));
        Assert.Equal("PathPlanning.AgentPlan.Failed", evt.EventName);
        Assert.Equal("[PP-003] no route", evt.Reason);
    }

    [Fact]
    public void Replan_bumps_version_and_updates_goal()
    {
        var plan = new AgentPlan(Guid.NewGuid(), Roadmap, "AGV-1", "A", "B", SuccessResult());
        plan.ClearDomainEvents();

        plan.Replan("B", "C", SuccessResult());

        Assert.Equal(2, plan.StateVersion);
        Assert.Equal("B", plan.FromSiteId);
        Assert.Equal("C", plan.ToSiteId);
        Assert.Equal(PlanStatus.Computed, plan.Status);

        var evt = Assert.IsType<AgentPlanComputedEvent>(Assert.Single(plan.DomainEvents!));
        Assert.Equal("C", evt.ToSiteId);
        Assert.Equal(2, evt.StateVersion);
    }

    [Fact]
    public void Invalidate_supersedes_drops_path_and_raises_Failed()
    {
        var plan = new AgentPlan(Guid.NewGuid(), Roadmap, "AGV-1", "A", "B", SuccessResult());
        plan.ClearDomainEvents();

        plan.Invalidate("topology changed");

        Assert.Equal(PlanStatus.Superseded, plan.Status);
        Assert.Null(plan.Path);
        Assert.Null(plan.Cost);
        Assert.Equal("topology changed", plan.FailureReason);
        Assert.Equal(2, plan.StateVersion);

        var evt = Assert.IsType<AgentPlanFailedEvent>(Assert.Single(plan.DomainEvents!));
        Assert.Equal("topology changed", evt.Reason);
        Assert.Equal(2, evt.StateVersion);
    }

    [Fact]
    public void Constructor_guards_empty_ids()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentPlan(Guid.Empty, Roadmap, "AGV-1", "A", "B", SuccessResult()));
        Assert.Throws<ArgumentException>(() =>
            new AgentPlan(Guid.NewGuid(), Roadmap, " ", "A", "B", SuccessResult()));
    }

    [Fact]
    public void Invalidate_rejects_empty_reason()
    {
        var plan = new AgentPlan(Guid.NewGuid(), Roadmap, "AGV-1", "A", "B", SuccessResult());
        Assert.Throws<ArgumentException>(() => plan.Invalidate("  "));
    }
}
