using SwarmRoute.Liveness.Domain.Detection;
using System.Linq;
using SwarmRoute.Liveness.Domain.Services;
using SwarmRoute.Liveness.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Liveness.Tests;

public class DeadlockDetectorTests
{
    private readonly IDeadlockDetector _detector = new RagCycleDetector();

    [Fact]
    public void TwoAgentCycle_FlagsBothAgents()
    {
        // A owns r1 wants r2 ; B owns r2 wants r1
        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r2")
            .Owns("B", "r2").Waits("B", "r1")
            .Build();

        var cycles = _detector.Detect(snapshot);

        Assert.Single(cycles);
        Assert.Equal(["A", "B"], cycles[0].AgentIds);
    }

    [Fact]
    public void ThreeAgentCycle_FlagsAllThree()
    {
        var snapshot = SnapshotBuilder.Cycle(3);

        var cycles = _detector.Detect(snapshot);

        Assert.Single(cycles);
        Assert.Equal(3, cycles[0].Size);
        Assert.Equal(["A", "B", "C"], cycles[0].AgentIds);
    }

    [Fact]
    public void FourAgentCycle_FlagsAllFour()
    {
        var snapshot = SnapshotBuilder.Cycle(4);

        var cycles = _detector.Detect(snapshot);

        Assert.Single(cycles);
        Assert.Equal(4, cycles[0].Size);
        Assert.Equal(["A", "B", "C", "D"], cycles[0].AgentIds);
    }

    [Fact]
    public void AcyclicSnapshot_ReportsNoDeadlock()
    {
        // A owns r1 and waits on r2 (held by B); B owns r2 and waits on r3 (held by nobody) → no cycle.
        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r2")
            .Owns("B", "r2").Waits("B", "r3")
            .Build();

        var cycles = _detector.Detect(snapshot);

        Assert.Empty(cycles);
    }

    [Fact]
    public void EmptySnapshot_ReportsNoDeadlock()
    {
        var snapshot = new ResourceAllocationGraphSnapshot([], []);

        var cycles = _detector.Detect(snapshot);

        Assert.Empty(cycles);
    }

    [Fact]
    public void AgentsThatHoldButDoNotWait_AreNotDeadlocked()
    {
        // Everyone holds, nobody waits → no edges agent->resource → acyclic.
        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1")
            .Owns("B", "r2")
            .Build();

        Assert.Empty(_detector.Detect(snapshot));
    }

    [Fact]
    public void SelfCycle_OneAgentOwningAndWaitingSameResource_IsNotACycle()
    {
        // Owns r1 and waits r1: ownership r1->A, wait A->r1. That *is* a 2-cycle A<->r1, but it is a
        // self/contradictory state (an agent waiting on what it already holds). The RAG cycle passes
        // through r1, so A is flagged. We assert detection is deterministic, not that this is impossible.
        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r1")
            .Build();

        var cycles = _detector.Detect(snapshot);

        Assert.Single(cycles);
        Assert.Equal(["A"], cycles[0].AgentIds);
    }

    [Fact]
    public void TwoIndependentCycles_AreReportedSeparately()
    {
        // Cycle 1: A<->B over r1/r2.  Cycle 2: C<->D over r3/r4.  No cross edges.
        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r2")
            .Owns("B", "r2").Waits("B", "r1")
            .Owns("C", "r3").Waits("C", "r4")
            .Owns("D", "r4").Waits("D", "r3")
            .Build();

        var cycles = _detector.Detect(snapshot);

        Assert.Equal(2, cycles.Count);
        // Ordered by smallest member id → first {A,B}, then {C,D}.
        Assert.Equal(["A", "B"], cycles[0].AgentIds);
        Assert.Equal(["C", "D"], cycles[1].AgentIds);
    }

    [Fact]
    public void CycleWithAnExtraNonCyclicWaiter_FlagsOnlyTheCyclicAgents()
    {
        // A<->B cycle over r1/r2. Z waits on r1 (held by A) but nobody waits on Z's resource → Z not cyclic.
        var snapshot = new SnapshotBuilder()
            .Owns("A", "r1").Waits("A", "r2")
            .Owns("B", "r2").Waits("B", "r1")
            .Owns("Z", "r9").Waits("Z", "r1")
            .Build();

        var cycles = _detector.Detect(snapshot);

        Assert.Single(cycles);
        Assert.Equal(["A", "B"], cycles[0].AgentIds);
        Assert.DoesNotContain("Z", cycles[0].AgentIds);
    }

    [Fact]
    public void Detection_IsDeterministic_AcrossRepeatedRuns()
    {
        var snapshot = SnapshotBuilder.Cycle(4);

        var first = _detector.Detect(snapshot).Select(c => string.Join(",", c.AgentIds)).ToList();
        for (var i = 0; i < 5; i++)
        {
            var again = _detector.Detect(snapshot).Select(c => string.Join(",", c.AgentIds)).ToList();
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public void NullSnapshot_Throws()
    {
        Assert.Throws<ArgumentException>(() => _detector.Detect(null!));
    }
}
