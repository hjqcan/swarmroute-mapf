using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the pure <see cref="AdgExecutor"/> — the ADG/TPG-following executor what-if. The central claim is a
/// contrast: under the same injected delay, replaying the plan by wall-clock timestamps collides at a tight handoff,
/// while replaying it by following the cell-handoff dependency graph stays collision-free (paying makespan instead).
/// The ADG collision count is recomputed from the dependency-following schedule, so these tests genuinely exercise the
/// dependency construction — a wrong edge would make <see cref="DelayResilienceDto.AdgCollisions"/> non-zero.
/// </summary>
public sealed class AdgExecutorTests
{
    private static FleetTickPosition P(string id, string cell, AgentMotionState s) => new(id, cell, s);

    private static FleetLoopResult Loop(params FleetTickFrame[] frames) =>
        new(frames, new Dictionary<string, IReadOnlyList<string>>(),
            new FleetLoopStats(FleetLoopStatus.Completed, frames.Length - 1, 0, 2, 0), 2, null);

    [Fact]
    public void Naive_collides_at_a_tight_handoff_but_the_adg_executor_absorbs_it()
    {
        // Cell X is a back-to-back handoff: agv-1 holds [1,2), agv-2 enters exactly at 2 (slack 0). A 1-tick delay to
        // agv-1 makes the naive replay put both AGVs on X at once; the dependency-following replay makes agv-2 wait.
        var r = AdgExecutor.Simulate(Loop(
            new(0, [P("agv-1", "A", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(1, [P("agv-1", "X", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(2, [P("agv-1", "B", AgentMotionState.Moving), P("agv-2", "X", AgentMotionState.Moving)]),
            new(3, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "D", AgentMotionState.Arrived)])));

        Assert.NotNull(r);
        Assert.Equal("agv-1", r!.DelayedAgent);    // the early side of the tightest handoff
        Assert.Equal(1, r.DelayTicks);              // slack 0 → smallest delay that breaks it is 1
        Assert.True(r.NaiveCollisions >= 1, $"naive replay should collide, got {r.NaiveCollisions}");
        Assert.Equal(0, r.AdgCollisions);           // dependency-following stays safe (recomputed, not asserted)
        Assert.True(r.AdgMakespanInflation >= 1, $"ADG should pay makespan to absorb the delay, got {r.AdgMakespanInflation}");
    }

    [Fact]
    public void A_buffered_handoff_still_breaks_naively_once_the_delay_exceeds_its_slack()
    {
        // Cell X: agv-1 holds [1,2), agv-2 enters at 3 → slack 1. The injected delay is slack+1 = 2, which is enough to
        // overrun the buffer naively; the ADG executor again absorbs it collision-free.
        var r = AdgExecutor.Simulate(Loop(
            new(0, [P("agv-1", "A", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(1, [P("agv-1", "X", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(2, [P("agv-1", "B", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Waiting)]),
            new(3, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "X", AgentMotionState.Moving)]),
            new(4, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "D", AgentMotionState.Arrived)])));

        Assert.NotNull(r);
        Assert.Equal("agv-1", r!.DelayedAgent);
        Assert.Equal(2, r.DelayTicks);              // slack 1 + 1
        Assert.True(r.NaiveCollisions >= 1);
        Assert.Equal(0, r.AdgCollisions);
    }

    [Fact]
    public void No_shared_cells_means_no_delay_scenario()
    {
        // Two AGVs that never share a control point have no handoff to perturb → the what-if is not applicable.
        var r = AdgExecutor.Simulate(Loop(
            new(0, [P("agv-1", "A", AgentMotionState.Moving), P("agv-2", "C", AgentMotionState.Moving)]),
            new(1, [P("agv-1", "B", AgentMotionState.Arrived), P("agv-2", "D", AgentMotionState.Arrived)])));

        Assert.Null(r);
    }
}
