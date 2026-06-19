using SwarmRoute.Host.Adapters;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Services;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// The real <see cref="RagWouldCloseCycleDetector"/> (which reuses the Deadlock RAG builder + cycle detector):
/// it must say "yes" exactly when adding the candidate's would-be wait edges closes a circular wait through it,
/// and "no" otherwise — the same cycle semantics the reactive detector uses.
/// </summary>
public sealed class WouldCloseCycleDetectorTests
{
    private static ResourceRef Cp(string id) => new(ResourceKind.CP, id);
    private readonly IWouldCloseCycleDetector _detector = new RagWouldCloseCycleDetector();

    [Fact]
    public void Closing_a_two_agent_circular_wait_is_detected()
    {
        // A owns R1, B owns R2, and B already waits on R1. If A now blocks on R2 (held by B) the wait-for graph
        // closes: A → R2 → B → R1 → A. So granting/queuing A's request would close a cycle.
        var current = new ResourceAllocationGraphSnapshot(
            Owns: [("A", Cp("R1")), ("B", Cp("R2"))],
            Waits: [("B", Cp("R1"))]);

        var would = _detector.WouldCloseCycle(current, "A", [("B", Cp("R2"))]);

        Assert.True(would);
    }

    [Fact]
    public void Blocking_on_a_resource_whose_owner_does_not_wait_back_is_not_a_cycle()
    {
        // A owns R1, B owns R2, but B waits on nothing. A blocking on R2 makes A → R2 → B, a dead end — no cycle.
        var current = new ResourceAllocationGraphSnapshot(
            Owns: [("A", Cp("R1")), ("B", Cp("R2"))],
            Waits: []);

        var would = _detector.WouldCloseCycle(current, "A", [("B", Cp("R2"))]);

        Assert.False(would);
    }

    [Fact]
    public void No_candidate_wait_edges_is_never_a_cycle()
    {
        var current = new ResourceAllocationGraphSnapshot(Owns: [("A", Cp("R1"))], Waits: []);

        Assert.False(_detector.WouldCloseCycle(current, "A", []));
    }

    [Fact]
    public void Closing_a_three_agent_circular_wait_is_detected()
    {
        // A→R2→B→R3→C→R1→A once A blocks on R2. (A owns R1, B owns R2 & waits R3, C owns R3 & waits R1.)
        var current = new ResourceAllocationGraphSnapshot(
            Owns: [("A", Cp("R1")), ("B", Cp("R2")), ("C", Cp("R3"))],
            Waits: [("B", Cp("R3")), ("C", Cp("R1"))]);

        Assert.True(_detector.WouldCloseCycle(current, "A", [("B", Cp("R2"))]));
    }
}
