using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// Foundations-phase V1 admission policy of <see cref="StationScheduler"/>, driven over a real
/// <see cref="StationResourceCalendar"/> (which itself drives a real reservation table) and an
/// <see cref="InMemoryStationCatalog"/>.
/// </summary>
public sealed class StationSchedulerTests
{
    private const long OneHourMs = 3_600_000;

    private static StationDefinition Station(string stationId = "S1", string dockPoint = "CP-dock")
        => new(
            StationId: stationId,
            DockPoint: dockPoint,
            PreDockBuffers: new[] { "CP-buf" },
            BlockingClosure: new HashSet<ResourceRef>
            {
                new(ResourceKind.CP, dockPoint),
                new(ResourceKind.Zone, "Z1"),
            },
            ServiceDurationMs: OneHourMs,
            StationType: StationType.HardBlocking);

    /// <summary>Builds a scheduler over a real calendar/table and a catalog holding the given stations.</summary>
    private static StationScheduler BuildScheduler(params StationDefinition[] stations)
    {
        var topology = IResourceTopology.Empty;
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);
        var coordinator = new TrafficCoordinatorAppService(
            table, allocator, NullLogger<TrafficCoordinatorAppService>.Instance);
        var calendar = new StationResourceCalendar(coordinator);
        var catalog = new InMemoryStationCatalog(stations);
        return new StationScheduler(calendar, catalog);
    }

    private static ServiceAdmissionRequest Request(
        string agentId,
        string stationId = "S1",
        string dockPoint = "CP-dock",
        long earliestStartMs = 0)
        => new(
            AgentId: agentId,
            StationId: stationId,
            PreDockBuffer: "CP-buf",
            DockPoint: dockPoint,
            ServiceDurationMs: OneHourMs,
            Priority: 0,
            EarliestStartMs: earliestStartMs,
            DeadlineMs: null);

    [Fact]
    public async Task RequestDockAdmission_ForFreeStation_IsGrantedWithStartInstant()
    {
        var scheduler = BuildScheduler(Station());

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A", earliestStartMs: 0));

        Assert.True(decision.Granted);
        Assert.Equal(0, decision.ServiceStartMs);
        Assert.Equal("granted", decision.Reason);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task RequestDockAdmission_WhenStationAlreadyHeld_IsDeniedAsBusy()
    {
        var scheduler = BuildScheduler(Station());

        Assert.True((await scheduler.RequestDockAdmissionAsync(Request("AGV-A"))).Granted);

        // A second agent over an overlapping window is rejected by the cheap pre-check as "station busy".
        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-B", earliestStartMs: OneHourMs / 2));

        Assert.False(decision.Granted);
        Assert.Null(decision.ServiceStartMs);
        Assert.Equal("station busy", decision.Reason);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task RequestDockAdmission_ForNonOverlappingWindow_IsGranted()
    {
        var scheduler = BuildScheduler(Station());

        Assert.True((await scheduler.RequestDockAdmissionAsync(Request("AGV-A", earliestStartMs: 0))).Granted);

        // A later, non-overlapping window on the same station is admitted.
        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-B", earliestStartMs: OneHourMs));

        Assert.True(decision.Granted);
        Assert.Equal(OneHourMs, decision.ServiceStartMs);
    }

    [Fact]
    public async Task RequestDockAdmission_ForUnknownStation_IsDenied()
    {
        var scheduler = BuildScheduler(Station("S1"));

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A", stationId: "S-unknown"));

        Assert.False(decision.Granted);
        Assert.Null(decision.ServiceStartMs);
        Assert.Equal("unknown station", decision.Reason);
    }
}
