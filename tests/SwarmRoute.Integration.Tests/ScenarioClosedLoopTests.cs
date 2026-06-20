using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab — ScenarioBench) Obstacle maps driven through the REAL engine: a walled / pillared field is
/// still planned collision-free, every start/goal lands on a free cell, <see cref="ScenarioKind.Open"/> is
/// byte-identical to an unscenarioed run, and over-packing a scenario fails with a clear error.
/// </summary>
public sealed class ScenarioClosedLoopTests
{
    private static SimulationService Svc() =>
        new(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);

    private static SimulationResultDto Run(int w, int h, int agv, int seed, PlannerKind p, ScenarioKind scenario) =>
        Svc().RunAsync(new SimulationRequest(w, h, agv, seed, p, Scenario: scenario)).GetAwaiter().GetResult();

    private static string Timeline(SimulationResultDto r) =>
        string.Join(";", r.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions.OrderBy(p => p.AgentId, StringComparer.Ordinal).Select(p => $"{p.AgentId}@{p.SiteId}"))));

    [Fact]
    public void Open_scenario_is_byte_identical_to_an_unscenarioed_run()
    {
        var open = Run(8, 8, 6, 7, PlannerKind.Sipp, ScenarioKind.Open);
        var plain = Svc().RunAsync(new SimulationRequest(8, 8, 6, 7, PlannerKind.Sipp)).GetAwaiter().GetResult();

        Assert.Equal(Timeline(plain), Timeline(open));
    }

    [Theory]
    [InlineData(ScenarioKind.Bottleneck)]
    [InlineData(ScenarioKind.Obstacles)]
    public void Obstacle_scenarios_are_collision_free_and_carve_the_field(ScenarioKind scenario)
    {
        var r = Run(10, 8, 8, 3, PlannerKind.Sipp, scenario);

        Assert.Equal(0, r.Stats.Collisions);
        Assert.NotEqual("CollisionDetected", r.Stats.Status);
        Assert.True(r.Field.Sites.Count < 10 * 8, "obstacles remove cells from the field");

        // Every agent's start and goal is a real (free) control point — never an obstacle.
        var siteIds = r.Field.Sites.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(r.Agents, a =>
        {
            Assert.Contains(a.StartSiteId, siteIds);
            Assert.Contains(a.GoalSiteId, siteIds);
        });
    }

    [Fact]
    public void Over_packing_a_scenario_fails_with_a_clear_error()
    {
        // 6×6 Obstacles = 9 pillars → 27 free cells; 14 AGVs need 28 starts/goals → rejected before the run.
        var ex = Assert.Throws<ArgumentException>(() => Run(6, 6, 14, 1, PlannerKind.Sipp, ScenarioKind.Obstacles));
        Assert.Contains("free cell", ex.Message);
    }
}
