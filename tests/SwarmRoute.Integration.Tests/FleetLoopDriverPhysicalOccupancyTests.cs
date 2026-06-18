using SwarmRoute.Coordination.Application;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Simulation.Application;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Integration.Tests;

public sealed class FleetLoopDriverPhysicalOccupancyTests
{
    [Fact]
    public async Task Driver_FeedsWaitingPhysicalOccupancy_ToPlannerBlockedResources()
    {
        var cycle = new CapturingCycle();
        var driver = new FleetLoopDriver();
        var graph = FakeRoadmapQueryService.Graph(
            ["A", "X", "G"],
            ("A", "G"),
            ("X", "G"));
        var fleet = new[]
        {
            new FleetAgentSpec("high", "A", "G", Priority: 0),
            new FleetAgentSpec("low", "X", "G", Priority: 1),
        };

        await driver.RunToCompletionAsync(cycle, Guid.NewGuid(), graph, fleet, maxTicks: 1);

        var blocked = Assert.Single(cycle.BlockedResourcesByCycle);
        Assert.NotNull(blocked);
        Assert.Contains(RoadmapGraph.SiteRef("A"), blocked!);
        Assert.Contains(RoadmapGraph.SiteRef("X"), blocked);
    }

    [Fact]
    public async Task Driver_PerAgentRoute_KeepsWalkedPrefix_WhenReroutingAroundParkedVehicle()
    {
        var cycle = new ScriptedRerouteCycle();
        var driver = new FleetLoopDriver();
        var graph = FakeRoadmapQueryService.Graph(
            ["P", "A", "B", "C", "D", "X"],
            ("P", "A"),
            ("A", "B"),
            ("B", "C"),
            ("A", "D"),
            ("D", "C"),
            ("X", "B"));
        var fleet = new[]
        {
            new FleetAgentSpec("blocker", "X", "B", Priority: 0),
            new FleetAgentSpec("reroute", "P", "C", Priority: 1),
        };

        var result = await driver.RunToCompletionAsync(cycle, Guid.NewGuid(), graph, fleet, maxTicks: 8);

        Assert.Equal(FleetLoopStatus.Completed, result.Stats.Status);
        Assert.Equal(["P", "A", "D", "C"], result.PerAgentRoute["reroute"]);
    }

    private sealed class CapturingCycle : IFleetCoordinationCycle
    {
        public List<IReadOnlySet<ResourceRef>?> BlockedResourcesByCycle { get; } = [];

        public Task<CycleReport> RunCycleAsync(
            Guid roadmapId,
            IReadOnlyCollection<AgentGoal> goals,
            IReadOnlySet<ResourceRef>? blockedResources = null,
            CancellationToken cancellationToken = default)
        {
            BlockedResourcesByCycle.Add(blockedResources);
            return Task.FromResult(new CycleReport(goals
                .Select(g => new AgentCycleResult(
                    g.AgentId,
                    Planned: false,
                    Reserved: false,
                    Outcome: null,
                    Attempts: 1,
                    Path: null,
                    FailureReason: "not exercised"))
                .ToList()));
        }

        public Task ReleaseAsync(
            string agentId,
            IReadOnlyList<ResourceRef> passedResources,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ScriptedRerouteCycle : IFleetCoordinationCycle
    {
        private int _calls;

        public Task<CycleReport> RunCycleAsync(
            Guid roadmapId,
            IReadOnlyCollection<AgentGoal> goals,
            IReadOnlySet<ResourceRef>? blockedResources = null,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            var results = new List<AgentCycleResult>();
            foreach (var goal in goals)
            {
                IReadOnlyList<string> route = goal.AgentId switch
                {
                    "blocker" => new[] { "X", "B" },
                    "reroute" when _calls == 1 => new[] { "P", "A", "B", "C" },
                    "reroute" => new[] { "A", "D", "C" },
                    _ => new[] { goal.FromSiteId, goal.ToSiteId },
                };

                results.Add(new AgentCycleResult(
                    goal.AgentId,
                    Planned: true,
                    Reserved: true,
                    Outcome: null,
                    Attempts: 1,
                    Path: ToPath(route),
                    FailureReason: null));
            }

            return Task.FromResult(new CycleReport(results));
        }

        public Task ReleaseAsync(
            string agentId,
            IReadOnlyList<ResourceRef> passedResources,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static SpaceTimePath ToPath(IReadOnlyList<string> route)
            => new(route
                .Select((site, i) => new SpaceTimeCell(
                    RoadmapGraph.SiteRef(site),
                    new TimeInterval(i, i + 1)))
                .ToList());
    }
}
