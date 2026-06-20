using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab — Dispatcher) The task-assignment policy driven through the REAL engine: <see cref="AssignmentPolicy.Random"/>
/// is byte-identical to an unspecified run, while <see cref="AssignmentPolicy.Nearest"/> / <see cref="AssignmentPolicy.Optimal"/>
/// match goals to AGVs to cut travel — and stay collision-free. (The optimality of the matching itself is proven in
/// the dispatcher unit tests; here we verify the engine integration + the opt-in contract.)
/// </summary>
public sealed class DispatcherClosedLoopTests
{
    private static SimulationService Svc() =>
        new(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);

    private static SimulationResultDto Run(int w, int h, int agv, int seed, AssignmentPolicy assignment) =>
        Svc().RunAsync(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp, Assignment: assignment)).GetAwaiter().GetResult();

    private static string Timeline(SimulationResultDto r) =>
        string.Join(";", r.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions.OrderBy(p => p.AgentId, StringComparer.Ordinal).Select(p => $"{p.AgentId}@{p.SiteId}"))));

    [Fact]
    public void Random_assignment_is_byte_identical_to_an_unspecified_run()
    {
        var random = Run(8, 8, 6, 7, AssignmentPolicy.Random);
        var plain = Svc().RunAsync(new SimulationRequest(8, 8, 6, 7, PlannerKind.Sipp)).GetAwaiter().GetResult();

        Assert.Equal(Timeline(plain), Timeline(random));
    }

    [Theory]
    [InlineData(AssignmentPolicy.Nearest)]
    [InlineData(AssignmentPolicy.Optimal)]
    public void Dispatched_runs_are_collision_free_and_assign_distinct_goals(AssignmentPolicy policy)
    {
        for (var seed = 1; seed <= 5; seed++)
        {
            var r = Run(10, 8, 8, seed, policy);

            Assert.Equal(0, r.Stats.Collisions);
            Assert.NotEqual("CollisionDetected", r.Stats.Status);
            // Every AGV got a distinct goal (a valid assignment) and a distinct start.
            Assert.Equal(r.Agents.Count, r.Agents.Select(a => a.GoalSiteId).Distinct().Count());
            Assert.Equal(r.Agents.Count, r.Agents.Select(a => a.StartSiteId).Distinct().Count());
        }
    }

    [Fact]
    public void Optimal_assignment_lowers_total_travel_versus_random_on_average()
    {
        // Across seeds, matching goals to the nearest AGVs shortens routes, so the fleet's mean time-to-goal drops.
        double randomMean = 0, optimalMean = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            randomMean += Run(10, 8, 8, seed, AssignmentPolicy.Random).Metrics!.TravelTime.Mean;
            optimalMean += Run(10, 8, 8, seed, AssignmentPolicy.Optimal).Metrics!.TravelTime.Mean;
        }

        Assert.True(optimalMean < randomMean,
            $"optimal assignment should lower mean travel on aggregate (random {randomMean:F1} → optimal {optimalMean:F1}).");
    }
}
