using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Application.Contract.Services;
using SwarmRoute.PathPlanning.Infra.CrossCutting.IoC;
using SwarmRoute.PathPlanning.Tests.TestSupport;

namespace SwarmRoute.PathPlanning.Tests;

/// <summary>
/// End-to-end tests for <see cref="IPathPlanningAppService.PlanForAsync"/>, wired through the real
/// <see cref="PathPlanningNativeInjectorBootStrapper"/> (DijkstraPathPlanner + NullReservationQuery + the
/// AutoMapper profile + the app service), with only the Map read seam replaced by a
/// <see cref="FakeRoadmapQueryService"/> over a hand-built <see cref="RoadmapGraph"/>.
/// </summary>
public sealed class PathPlanningAppServiceTests
{
    private static readonly Guid Roadmap = Guid.NewGuid();

    private static IPathPlanningAppService BuildService(RoadmapGraph graph)
    {
        var services = new ServiceCollection();

        // Real PathPlanning registrations (planner, reservation stub, app service, AutoMapper).
        PathPlanningNativeInjectorBootStrapper.RegisterServices(services);

        // Substitute the Map read seam with an in-memory fake returning the supplied graph.
        services.AddSingleton<IRoadmapQueryService>(new FakeRoadmapQueryService(Roadmap, graph));

        return services.BuildServiceProvider().GetRequiredService<IPathPlanningAppService>();
    }

    [Fact]
    public async Task PlanForAsync_returns_ordered_site_sequence_on_success()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B", 1.0).Edge("B", "D", 1.0)
            .Edge("A", "C", 1.0).Edge("C", "D", 5.0)
            .Build();
        var service = BuildService(graph);

        var dto = await service.PlanForAsync(Roadmap, "AGV-1", "A", "D");

        Assert.True(dto.Success);
        Assert.Equal(new[] { "A", "B", "D" }, dto.SiteSequence);
        Assert.Equal("AGV-1", dto.AgentId);
        Assert.Equal("A", dto.FromSiteId);
        Assert.Equal("D", dto.ToSiteId);
        Assert.Equal(2000L, dto.DistanceUnits); // (1.0 + 1.0) * 1000
        Assert.Equal(2, dto.HopCount);
        Assert.Null(dto.FailureReason);
    }

    [Fact]
    public async Task PlanForAsync_returns_failure_when_goal_unreachable()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Edge("X", "Y").Build();
        var service = BuildService(graph);

        var dto = await service.PlanForAsync(Roadmap, "AGV-1", "A", "Y");

        Assert.False(dto.Success);
        Assert.Empty(dto.SiteSequence);
        Assert.NotNull(dto.FailureReason);
        Assert.Contains("PP-003", dto.FailureReason); // NoRoute
        Assert.Equal(0, dto.HopCount);
        Assert.Equal(0, dto.DistanceUnits);
    }

    [Fact]
    public async Task PlanForAsync_throws_KeyNotFound_for_unknown_roadmap()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();
        var service = BuildService(graph);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.PlanForAsync(Guid.NewGuid(), "AGV-1", "A", "B"));
    }

    [Fact]
    public async Task PlanForAsync_validates_empty_agent_id()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build();
        var service = BuildService(graph);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.PlanForAsync(Roadmap, "  ", "A", "B"));
    }
}
