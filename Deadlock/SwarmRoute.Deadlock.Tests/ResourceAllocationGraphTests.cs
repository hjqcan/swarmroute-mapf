using System.Linq;
using AJR.Platform.Algorithms.Graphs;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Tests;

public class ResourceAllocationGraphTests
{
    [Fact]
    public void Build_AddsAgentAndResourceVertices_AndOwnershipWaitEdges()
    {
        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r2")
            .Owns("B", "r2").Waits("B", "r1")
            .Build();

        var graph = ResourceAllocationGraph.FromSnapshot(snapshot).Build();

        // agent + occupySite + applySite vertices present
        Assert.True(graph.HasVertex("agent_A"));
        Assert.True(graph.HasVertex("agent_B"));
        Assert.True(graph.HasVertex("occupySite_r1"));
        Assert.True(graph.HasVertex("occupySite_r2"));

        // ownership: resource -> owner
        Assert.True(graph.HasEdge("occupySite_r1", "agent_A"));
        Assert.True(graph.HasEdge("occupySite_r2", "agent_B"));

        // wait-for: waiter -> resource
        Assert.True(graph.HasEdge("agent_A", "occupySite_r2"));
        Assert.True(graph.HasEdge("agent_B", "occupySite_r1"));
    }

    [Fact]
    public void Build_ProducesGraphThatCycleDetectorFlags()
    {
        var snapshot = SnapshotBuilder.Cycle(3);

        var graph = ResourceAllocationGraph.FromSnapshot(snapshot).Build();
        var cyclic = CyclesDetector.CyclicVertices(graph, ResourceAllocationGraph.AgentPrefix);

        Assert.Equal(
            ["agent_A", "agent_B", "agent_C"],
            cyclic.OrderBy(v => v).ToList());
    }

    [Fact]
    public void Empty_BuildsEmptyGraph()
    {
        var graph = ResourceAllocationGraph.Empty.Build();
        Assert.Equal(0, graph.VerticesCount);
        Assert.Equal(0, graph.EdgesCount);
    }

    [Fact]
    public void FromSnapshot_RejectsBlankIds()
    {
        var bad = new SnapshotBuilder().Owns(" ", "r1").Build();
        Assert.Throws<ArgumentException>(() => ResourceAllocationGraph.FromSnapshot(bad));
    }

    [Fact]
    public void ValueEquality_IsByEdgeSets_OrderIndependent()
    {
        var a = ResourceAllocationGraph.FromSnapshot(
            new SnapshotBuilder().Owns("A", "r1").Waits("B", "r1").Build());
        var b = ResourceAllocationGraph.FromSnapshot(
            new SnapshotBuilder().Waits("B", "r1").Owns("A", "r1").Build());

        Assert.Equal(a, b);
    }

    [Fact]
    public void ValueEquality_DistinguishesOwnsFromWaits()
    {
        var owns = ResourceAllocationGraph.FromSnapshot(
            new SnapshotBuilder().Owns("A", "r1").Build());
        var waits = ResourceAllocationGraph.FromSnapshot(
            new SnapshotBuilder().Waits("A", "r1").Build());

        Assert.NotEqual(owns, waits);
    }
}
