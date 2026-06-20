using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// Round-2 goal-filtering admission of <see cref="DockAdmissionController"/>, driven over a real
/// <see cref="StationScheduler"/> / <see cref="StationResourceCalendar"/> / reservation table and an
/// <see cref="InMemoryStationCatalog"/>, plus the inert <see cref="PassThroughDockAdmissionController"/>.
/// </summary>
public sealed class DockAdmissionControllerTests
{
    private const long OneHourMs = 3_600_000;
    private static readonly Guid Roadmap = Guid.NewGuid();

    private static readonly ResourceRef DockCp = new(ResourceKind.CP, "CP-dock");
    private static readonly ResourceRef ClosureZone = new(ResourceKind.Zone, "Z1");

    private static StationDefinition Station(
        string stationId = "S1",
        string dockPoint = "CP-dock",
        string buffer = "CP-buf")
        => new(
            StationId: stationId,
            DockPoint: dockPoint,
            PreDockBuffers: new[] { buffer },
            BlockingClosure: new HashSet<ResourceRef>
            {
                new(ResourceKind.CP, dockPoint),
                ClosureZone,
            },
            ServiceDurationMs: OneHourMs,
            StationType: StationType.HardBlocking);

    /// <summary>A fresh controller + the scheduler/calendar it composes, sharing one reservation table.</summary>
    private static (DockAdmissionController Controller, StationScheduler Scheduler) Build(
        params StationDefinition[] stations)
    {
        var topology = IResourceTopology.Empty;
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);
        var coordinator = new TrafficCoordinatorAppService(
            table, allocator, NullLogger<TrafficCoordinatorAppService>.Instance);
        var calendar = new StationResourceCalendar(coordinator);
        var catalog = new InMemoryStationCatalog(stations);
        var scheduler = new StationScheduler(calendar, catalog);
        return (new DockAdmissionController(scheduler, catalog), scheduler);
    }

    [Fact]
    public async Task GrantedStationGoal_KeepsDockPoint_AndBlocksNothing()
    {
        var (controller, _) = Build(Station());
        var goal = new AgentGoal("AGV-A", FromSiteId: "CP-start", ToSiteId: "CP-dock");

        var result = await controller.EvaluateAdmissionAsync(Roadmap, new[] { goal });

        var admitted = Assert.Single(result.AdmittedGoals);
        Assert.Same(goal, admitted); // unchanged instance: still driving to the dock point
        Assert.Equal("CP-dock", admitted.ToSiteId);
        Assert.Empty(result.BlockedResources);
    }

    [Fact]
    public async Task DeniedStationGoal_RewritesToPreDockBuffer_AndBlocksClosure()
    {
        var (controller, scheduler) = Build(Station());

        // Occupy the station's service window first, so the next admission to the same dock is denied.
        var first = await controller.EvaluateAdmissionAsync(
            Roadmap, new[] { new AgentGoal("AGV-A", "CP-start", "CP-dock") });
        Assert.Equal("CP-dock", Assert.Single(first.AdmittedGoals).ToSiteId);
        Assert.Empty(first.BlockedResources);

        var goalB = new AgentGoal("AGV-B", FromSiteId: "CP-elsewhere", ToSiteId: "CP-dock", Priority: 2);
        var result = await controller.EvaluateAdmissionAsync(Roadmap, new[] { goalB });

        var admitted = Assert.Single(result.AdmittedGoals);
        Assert.Equal("CP-buf", admitted.ToSiteId);             // held at the pre-dock buffer
        Assert.Equal("AGV-B", admitted.AgentId);               // identity preserved
        Assert.Equal("CP-elsewhere", admitted.FromSiteId);     // origin preserved
        Assert.Equal(2, admitted.Priority);                    // priority preserved (stable ordering undisturbed)

        // The denied station's whole blocking closure is reported so the planner routes others around the dock.
        Assert.Contains(DockCp, result.BlockedResources);
        Assert.Contains(ClosureZone, result.BlockedResources);
        Assert.Equal(2, result.BlockedResources.Count);
    }

    [Fact]
    public async Task NonStationGoal_PassesThroughUntouched()
    {
        var (controller, _) = Build(Station(dockPoint: "CP-dock"));
        var goal = new AgentGoal("AGV-A", FromSiteId: "CP-start", ToSiteId: "CP-not-a-dock");

        var result = await controller.EvaluateAdmissionAsync(Roadmap, new[] { goal });

        var admitted = Assert.Single(result.AdmittedGoals);
        Assert.Same(goal, admitted);
        Assert.Empty(result.BlockedResources);
    }

    [Fact]
    public async Task MixedGoals_PreserveInputOrder_AndOnlyDeniedStationContributesBlocked()
    {
        var (controller, _) = Build(Station("S1", "CP-dock", "CP-buf"));

        // AGV-A takes the only service window; AGV-B (same dock) is denied -> buffer; AGV-C is a non-station goal.
        var goals = new[]
        {
            new AgentGoal("AGV-A", "CP-a", "CP-dock"),
            new AgentGoal("AGV-B", "CP-b", "CP-dock"),
            new AgentGoal("AGV-C", "CP-c", "CP-free"),
        };

        var result = await controller.EvaluateAdmissionAsync(Roadmap, goals);

        var admitted = result.AdmittedGoals.ToArray();
        Assert.Equal(3, admitted.Length);
        Assert.Equal(new[] { "AGV-A", "AGV-B", "AGV-C" }, admitted.Select(g => g.AgentId)); // order preserved
        Assert.Equal("CP-dock", admitted[0].ToSiteId);   // granted -> dock kept
        Assert.Equal("CP-buf", admitted[1].ToSiteId);    // denied -> buffer
        Assert.Equal("CP-free", admitted[2].ToSiteId);   // non-station -> untouched
        Assert.Equal(2, result.BlockedResources.Count);  // only the one denied station's closure
        Assert.Contains(DockCp, result.BlockedResources);
        Assert.Contains(ClosureZone, result.BlockedResources);
    }

    [Fact]
    public async Task EmptyGoals_YieldEmptyResult()
    {
        var (controller, _) = Build(Station());

        var result = await controller.EvaluateAdmissionAsync(Roadmap, Array.Empty<AgentGoal>());

        Assert.Empty(result.AdmittedGoals);
        Assert.Empty(result.BlockedResources);
    }

    [Fact]
    public async Task PassThroughController_ReturnsInputUnchanged_AndBlocksNothing()
    {
        IDockAdmissionController controller = new PassThroughDockAdmissionController();
        var goals = new[]
        {
            new AgentGoal("AGV-A", "CP-1", "CP-dock"),   // would be a station goal under the FMS impl...
            new AgentGoal("AGV-B", "CP-2", "CP-3"),
        };

        var result = await controller.EvaluateAdmissionAsync(Roadmap, goals);

        Assert.Same(goals, result.AdmittedGoals); // ...but pass-through hands the exact collection back, inert
        Assert.Empty(result.BlockedResources);
    }
}
