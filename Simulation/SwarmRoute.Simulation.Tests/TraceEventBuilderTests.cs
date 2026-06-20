using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the pure <see cref="TraceEventBuilder"/>: it derives one <c>Planned</c> per AGV (start → goal), a
/// <c>Moved</c> per control-point hop (from → to), and one <c>Arrived</c>, all from the recorded timeline, in
/// tick order.
/// </summary>
public sealed class TraceEventBuilderTests
{
    private static FleetTickPosition P(string id, string cell, AgentMotionState s) => new(id, cell, s);

    [Fact]
    public void Derives_planned_moved_and_arrived_from_the_timeline()
    {
        var frames = new List<FleetTickFrame>
        {
            new(0, [P("agv-1", "A", AgentMotionState.Moving)]),
            new(1, [P("agv-1", "B", AgentMotionState.Moving)]),    // moved A→B
            new(2, [P("agv-1", "C", AgentMotionState.Arrived)]),   // moved B→C + arrived
        };
        var loop = new FleetLoopResult(frames, new Dictionary<string, IReadOnlyList<string>>(),
            new FleetLoopStats(FleetLoopStatus.Completed, Ticks: 2, Collisions: 0, Arrived: 1, Replans: 0),
            MaxConcurrentEnRoute: 1, Collision: null);
        var specs = new[] { new FleetAgentSpec("agv-1", "A", "C", 0) };

        var trace = TraceEventBuilder.Build(loop, specs);

        Assert.Equal("Planned", trace[0].Kind);
        Assert.Equal("A", trace[0].SiteId);
        Assert.Equal("C", trace[0].FromSiteId);                    // Planned carries the goal in FromSiteId

        var moves = trace.Where(e => e.Kind == "Moved").ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal(("A", "B"), (moves[0].FromSiteId, moves[0].SiteId));
        Assert.Equal(("B", "C"), (moves[1].FromSiteId, moves[1].SiteId));

        Assert.Single(trace.Where(e => e.Kind == "Arrived"));
        Assert.Contains(trace, e => e.Kind == "Arrived" && e.SiteId == "C" && e.Tick == 2);

        for (var i = 1; i < trace.Count; i++)                      // tick-ordered
            Assert.True(trace[i].Tick >= trace[i - 1].Tick);
    }

    [Fact]
    public void A_stationary_agent_emits_no_moved_events()
    {
        var frames = new List<FleetTickFrame>
        {
            new(0, [P("agv-1", "A", AgentMotionState.Waiting)]),
            new(1, [P("agv-1", "A", AgentMotionState.Waiting)]),   // never moves (waits are implicit, not events)
        };
        var loop = new FleetLoopResult(frames, new Dictionary<string, IReadOnlyList<string>>(),
            new FleetLoopStats(FleetLoopStatus.DidNotConverge, 1, 0, 0, 0), 0, null);

        var trace = TraceEventBuilder.Build(loop, [new FleetAgentSpec("agv-1", "A", "Z", 0)]);

        Assert.DoesNotContain(trace, e => e.Kind == "Moved");
        Assert.DoesNotContain(trace, e => e.Kind == "Arrived");
        Assert.Single(trace.Where(e => e.Kind == "Planned"));
    }
}
