using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the pure <see cref="RobustnessAnalyzer"/>: it derives the inter-AGV cell-handoff dependencies and
/// their slack from the timeline — a back-to-back handoff is tight (zero slack), a buffered one is not, and a run
/// where no cell is shared has no dependencies.
/// </summary>
public sealed class RobustnessAnalyzerTests
{
    private static FleetTickPosition P(string id, string cell, AgentMotionState s) => new(id, cell, s);

    private static FleetLoopResult Loop(params FleetTickFrame[] frames) =>
        new(frames, new Dictionary<string, IReadOnlyList<string>>(),
            new FleetLoopStats(FleetLoopStatus.Completed, frames.Length - 1, 0, 2, 0), 2, null);

    [Fact]
    public void A_buffered_handoff_has_positive_slack()
    {
        // Cell X: agv-1 holds [1,2); agv-2 holds [3,4) → slack = 3 - 2 = 1.
        var r = RobustnessAnalyzer.Compute(Loop(
            new(0, [P("agv-1", "A", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(1, [P("agv-1", "X", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(2, [P("agv-1", "B", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(3, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "X", AgentMotionState.Moving)]),
            new(4, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "D", AgentMotionState.Arrived)])));

        Assert.Equal(1, r.HandoffDependencies);
        Assert.Equal(0, r.TightHandoffs);
        Assert.Equal(1, r.MinSlackTicks);
        Assert.Contains("X", r.TightestCells);
    }

    [Fact]
    public void A_back_to_back_handoff_is_tight_zero_slack()
    {
        // Cell X: agv-1 holds [1,2); agv-2 enters exactly at 2 → slack 0 (any delay there collides naively).
        var r = RobustnessAnalyzer.Compute(Loop(
            new(0, [P("agv-1", "A", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(1, [P("agv-1", "X", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(2, [P("agv-1", "B", AgentMotionState.Moving), P("agv-2", "X", AgentMotionState.Moving)]),
            new(3, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "D", AgentMotionState.Arrived)])));

        Assert.Equal(1, r.HandoffDependencies);
        Assert.Equal(1, r.TightHandoffs);
        Assert.Equal(0, r.MinSlackTicks);
    }

    [Fact]
    public void No_shared_cells_means_no_dependencies()
    {
        var r = RobustnessAnalyzer.Compute(Loop(
            new(0, [P("agv-1", "A", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Moving)]),
            new(1, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "D", AgentMotionState.Arrived)])));

        Assert.Equal(0, r.HandoffDependencies);
        Assert.Equal(0, r.MinSlackTicks);
        Assert.Empty(r.TightestCells);
    }
}
