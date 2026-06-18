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
}
