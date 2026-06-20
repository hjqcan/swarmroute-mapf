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
/// Foundations-phase behaviour of <see cref="StationResourceCalendar"/>, driven against a real
/// <see cref="ReservationTable"/> + <see cref="ResourceAllocator"/> + <see cref="TrafficCoordinatorAppService"/>
/// (the same wiring as <c>TrafficControl.Tests</c>) so the long interval-lease really lands on the frozen seam.
/// </summary>
public sealed class StationResourceCalendarTests
{
    private const long OneHourMs = 3_600_000;

    /// <summary>Builds the real coordinator over an identity-closure topology + its backing reservation table.</summary>
    private static (ITrafficCoordinatorAppService coordinator, ReservationTable table) BuildCoordinator()
    {
        var topology = IResourceTopology.Empty;
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);
        var coordinator = new TrafficCoordinatorAppService(
            table, allocator, NullLogger<TrafficCoordinatorAppService>.Instance);
        return (coordinator, table);
    }

    /// <summary>A station whose dock point is <c>CP-dock</c> and whose blocking closure is a single Zone.</summary>
    private static StationDefinition StationWithZoneClosure(
        string stationId = "S1",
        string dockPoint = "CP-dock",
        string zoneId = "Z1")
        => new(
            StationId: stationId,
            DockPoint: dockPoint,
            PreDockBuffers: new[] { "CP-buf" },
            BlockingClosure: new HashSet<ResourceRef>
            {
                new(ResourceKind.CP, dockPoint),
                new(ResourceKind.Zone, zoneId),
            },
            ServiceDurationMs: OneHourMs,
            StationType: StationType.HardBlocking);

    [Fact]
    public async Task OneHourWindow_OverZoneClosure_IsGrantedOnFreeTable()
    {
        var (coordinator, table) = BuildCoordinator();
        var calendar = new StationResourceCalendar(coordinator);
        var station = StationWithZoneClosure();
        var window = new TimeInterval(0, OneHourMs);

        var granted = await calendar.TryReserveServiceWindowAsync(station, "AGV-A", window);

        Assert.True(granted);
        // The dock-point CP and the Zone closure member are both leased to AGV-A across the window.
        Assert.Contains(table.ActiveLeases, l =>
            l.AgentId == "AGV-A" && l.Resource == new ResourceRef(ResourceKind.CP, "CP-dock"));
        Assert.Contains(table.ActiveLeases, l =>
            l.AgentId == "AGV-A" && l.Resource == new ResourceRef(ResourceKind.Zone, "Z1"));
    }

    [Fact]
    public async Task SecondOverlappingWindow_ForDifferentAgent_OnSameStation_IsDenied()
    {
        var (coordinator, _) = BuildCoordinator();
        var calendar = new StationResourceCalendar(coordinator);
        var station = StationWithZoneClosure();

        Assert.True(await calendar.TryReserveServiceWindowAsync(
            station, "AGV-A", new TimeInterval(0, OneHourMs)));

        // A different agent wants an overlapping hour on the same station -> closure contended -> denied.
        var granted = await calendar.TryReserveServiceWindowAsync(
            station, "AGV-B", new TimeInterval(OneHourMs / 2, OneHourMs / 2 + OneHourMs));

        Assert.False(granted);
        // The cheap pre-check agrees: the station is busy across the overlapping window.
        Assert.False(calendar.CanReserveServiceWindow(
            station.StationId, new TimeInterval(OneHourMs / 2, OneHourMs / 2 + OneHourMs)));
    }

    [Fact]
    public async Task NonOverlappingWindow_ForDifferentAgent_OnSameStation_IsGranted()
    {
        var (coordinator, _) = BuildCoordinator();
        var calendar = new StationResourceCalendar(coordinator);
        var station = StationWithZoneClosure();

        Assert.True(await calendar.TryReserveServiceWindowAsync(
            station, "AGV-A", new TimeInterval(0, OneHourMs)));

        // Half-open windows that merely touch ([0,H) then [H,2H)) do NOT overlap -> grantable.
        var granted = await calendar.TryReserveServiceWindowAsync(
            station, "AGV-B", new TimeInterval(OneHourMs, 2 * OneHourMs));

        Assert.True(granted);
    }

    [Fact]
    public async Task Release_FreesTheClosure_SoALaterOverlappingWindowIsGrantableAgain()
    {
        var (coordinator, table) = BuildCoordinator();
        var calendar = new StationResourceCalendar(coordinator);
        var station = StationWithZoneClosure();
        var window = new TimeInterval(0, OneHourMs);

        Assert.True(await calendar.TryReserveServiceWindowAsync(station, "AGV-A", window));

        // A different agent is denied while AGV-A holds the closure across the same window.
        Assert.False(await calendar.TryReserveServiceWindowAsync(station, "AGV-B", window));

        await calendar.ReleaseServiceWindowAsync(station.StationId, "AGV-A");

        // The dock point + Zone closure are fully freed (no leak) ...
        Assert.DoesNotContain(table.ActiveLeases, l => l.AgentId == "AGV-A");
        Assert.True(calendar.CanReserveServiceWindow(station.StationId, window));
        // ... so the same overlapping window is now grantable to AGV-B.
        Assert.True(await calendar.TryReserveServiceWindowAsync(station, "AGV-B", window));
    }

    [Fact]
    public async Task Release_IsIdempotent_ForAStationTheAgentDoesNotHold()
    {
        var (coordinator, _) = BuildCoordinator();
        var calendar = new StationResourceCalendar(coordinator);

        // Releasing a never-held station/agent is a no-op (must not throw).
        await calendar.ReleaseServiceWindowAsync("S1", "AGV-A");

        // And after a grant+release, a second release is still a no-op.
        var station = StationWithZoneClosure();
        Assert.True(await calendar.TryReserveServiceWindowAsync(station, "AGV-A", new TimeInterval(0, OneHourMs)));
        await calendar.ReleaseServiceWindowAsync(station.StationId, "AGV-A");
        await calendar.ReleaseServiceWindowAsync(station.StationId, "AGV-A");

        Assert.True(calendar.CanReserveServiceWindow(station.StationId, new TimeInterval(0, OneHourMs)));
    }

    [Fact]
    public void CanReserveServiceWindow_OnFreeStation_IsTrue()
    {
        var (coordinator, _) = BuildCoordinator();
        var calendar = new StationResourceCalendar(coordinator);

        Assert.True(calendar.CanReserveServiceWindow("S1", new TimeInterval(0, OneHourMs)));
    }
}
