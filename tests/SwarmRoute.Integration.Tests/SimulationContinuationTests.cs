using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Lifelong-continuation contract (the auto-loop's "don't teleport" behaviour): when a run supplies explicit
/// <c>Starts</c>, each AGV begins at its current pose and is given a fresh, distinct goal; invalid starts fall
/// back to a random layout rather than throwing.
/// </summary>
public sealed class SimulationContinuationTests
{
    private static SimulationResultDto Run(SimulationRequest request)
        => new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance)
            .RunAsync(request).GetAwaiter().GetResult();

    [Fact]
    public void Explicit_starts_are_used_and_goals_are_fresh_and_distinct()
    {
        var starts = new[] { "r0c0", "r1c1", "r2c2", "r3c3" };

        var result = Run(new SimulationRequest(6, 6, 4, Seed: 5, Planner: PlannerKind.Sipp, Starts: starts));

        // Each AGV starts exactly where we placed it (no teleport), with a new goal that isn't its start.
        for (var i = 0; i < starts.Length; i++)
        {
            Assert.Equal(starts[i], result.Agents[i].StartSiteId);
            Assert.NotEqual(result.Agents[i].StartSiteId, result.Agents[i].GoalSiteId);
        }

        // Goals are all distinct, and none coincides with any start (goals drawn from the non-start pool).
        var goals = result.Agents.Select(a => a.GoalSiteId).ToList();
        Assert.Equal(goals.Count, goals.Distinct().Count());
        Assert.Empty(goals.Intersect(starts));
    }

    [Fact]
    public void Chaining_a_run_continues_from_the_previous_final_positions()
    {
        var first = Run(new SimulationRequest(6, 6, 4, Seed: 1, Planner: PlannerKind.Sipp));

        // The next run's starts = each AGV's final-frame position, in agent order (agv-1..agv-4).
        var lastFrame = first.Timeline.Frames[^1];
        var byId = lastFrame.Positions.ToDictionary(p => p.AgentId, p => p.SiteId);
        var starts = Enumerable.Range(1, 4).Select(i => byId[$"agv-{i}"]).ToList();

        var second = Run(new SimulationRequest(6, 6, 4, Seed: 2, Planner: PlannerKind.Sipp, Starts: starts));

        for (var i = 0; i < 4; i++)
            Assert.Equal(starts[i], second.Agents[i].StartSiteId);
    }

    [Fact]
    public void Invalid_starts_fall_back_to_a_random_layout()
    {
        // Wrong count is ignored (not an error): the run proceeds with a fresh random layout.
        var result = Run(new SimulationRequest(6, 6, 4, Seed: 5, Planner: PlannerKind.Sipp, Starts: new[] { "r0c0" }));

        Assert.Equal(4, result.Agents.Count);
    }
}
