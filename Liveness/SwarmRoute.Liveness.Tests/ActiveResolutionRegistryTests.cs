using System.Linq;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Deadlock.Domain.Aggregates;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Tests;

public class ActiveResolutionRegistryTests
{
    private static (DeadlockCase Case, AvoidancePlan Plan) OpenResolution(string victim)
    {
        var @case = DeadlockCase.Detect(DeadlockCycle.FromAgentIds([victim, "Z"]));
        @case.RequestResolution(victim, ResolutionStrategy.SendToAvoidSite, "V");
        var plan = new AvoidancePlan(Guid.NewGuid(), @case.Id, victim);
        return (@case, plan);
    }

    [Fact]
    public void Open_IsIdempotentPerVictim()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var (c1, p1) = OpenResolution("A");
        var (c2, p2) = OpenResolution("A");

        Assert.True(registry.Open(c1, p1));
        Assert.False(registry.Open(c2, p2)); // same victim already open → no-op
        Assert.Single(registry.SnapshotOpen());
    }

    [Fact]
    public void HasOpen_And_HasOpenForAny_Track_OpenVictims()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var (c, p) = OpenResolution("A");
        registry.Open(c, p);

        Assert.True(registry.HasOpen("A"));
        Assert.False(registry.HasOpen("B"));
        Assert.True(registry.HasOpenForAny(["B", "A"]));
        Assert.False(registry.HasOpenForAny(["B", "C"]));
    }

    [Fact]
    public void TryGet_ReturnsTheLiveAggregates()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var (c, p) = OpenResolution("A");
        registry.Open(c, p);

        Assert.True(registry.TryGet("A", out var res));
        Assert.Same(c, res.Case);
        Assert.Same(p, res.Plan);
        Assert.Equal("A", res.VictimAgentId);
    }

    [Fact]
    public void Close_RemovesTheResolution_AndIsIdempotent()
    {
        var registry = new InMemoryActiveResolutionRegistry();
        var (c, p) = OpenResolution("A");
        registry.Open(c, p);

        registry.Close("A");
        Assert.False(registry.HasOpen("A"));
        Assert.Empty(registry.SnapshotOpen());

        registry.Close("A"); // no throw
        Assert.False(registry.HasOpen("A"));
    }
}
