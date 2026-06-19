using SwarmRoute.Coordination.Application;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Simulation.Application;
using SwarmRoute.SpatioTemporal.Kernel;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Driver-level coverage of the RHCR partial-plan handling, isolated from SIPP via a scripted cycle that returns
/// windowed routes (terminal ≠ goal). Proves the relaxed invariant (a route that ends short of the goal does NOT
/// throw <see cref="FleetLoopException"/>) and the frontier-reentry arm (the agent advances to the frontier,
/// releases, re-plans the next window, and ultimately arrives).
/// </summary>
public sealed class FleetLoopDriverHorizonTests
{
    [Fact]
    public async Task Windowed_route_does_not_throw_and_completes_across_two_windows()
    {
        var cycle = new ScriptedWindowedCycle();
        var driver = new FleetLoopDriver();
        var graph = FakeRoadmapQueryService.Graph(
            ["A", "B", "C", "D", "G"],
            ("A", "B"), ("B", "C"), ("C", "D"), ("D", "G"));
        var fleet = new[] { new FleetAgentSpec("w", "A", "G", Priority: 0) };

        // Would throw FleetLoopException on the windowed (terminal != goal) route if the invariant were not relaxed.
        var result = await driver.RunToCompletionAsync(cycle, Guid.NewGuid(), graph, fleet, maxTicks: 20);

        Assert.Equal(FleetLoopStatus.Completed, result.Stats.Status);
        Assert.Equal(1, result.Stats.Arrived);
        // The walked trail spans both committed windows A..C then C..G — continuous, anchored at the start.
        Assert.Equal(["A", "B", "C", "D", "G"], result.PerAgentRoute["w"]);
        Assert.True(cycle.PlannedFrom.Count >= 2, "the agent should have re-planned at the window frontier (C).");
        Assert.Contains("C", cycle.PlannedFrom); // the second window was planned from the frontier
    }

    /// <summary>Returns a window that stops at C (≠ goal G) when planning from A, then a goal-reaching window from C.</summary>
    private sealed class ScriptedWindowedCycle : IFleetCoordinationCycle
    {
        public List<string> PlannedFrom { get; } = [];

        public Task<CycleReport> RunCycleAsync(
            Guid roadmapId,
            IReadOnlyCollection<AgentGoal> goals,
            IReadOnlySet<ResourceRef>? blockedResources = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<AgentCycleResult>();
            foreach (var goal in goals)
            {
                PlannedFrom.Add(goal.FromSiteId);
                IReadOnlyList<string> route = goal.FromSiteId switch
                {
                    "A" => ["A", "B", "C"], // window frontier C, short of the goal G
                    "C" => ["C", "D", "G"], // next window reaches the goal
                    _ => [goal.FromSiteId, goal.ToSiteId],
                };
                results.Add(new AgentCycleResult(
                    goal.AgentId, Planned: true, Reserved: true, Outcome: null,
                    Attempts: 1, Path: ToPath(route), FailureReason: null));
            }
            return Task.FromResult(new CycleReport(results));
        }

        public Task ReleaseAsync(string agentId, IReadOnlyList<ResourceRef> passedResources, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static SpaceTimePath ToPath(IReadOnlyList<string> route)
            => new(route
                .Select((site, i) => new SpaceTimeCell(RoadmapGraph.SiteRef(site), new TimeInterval(i, i + 1)))
                .ToList());
    }
}
