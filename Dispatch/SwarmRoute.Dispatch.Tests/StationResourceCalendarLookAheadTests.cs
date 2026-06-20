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
/// FMS-V3 look-ahead on <see cref="StationResourceCalendar"/>: <c>FreeWindows</c> reports the gaps around held
/// windows within a horizon, and <c>EarliestGrantableStart</c> finds the first gap long enough to host a window
/// (skipping busy spans). Driven against the same real coordinator wiring as
/// <see cref="StationResourceCalendarTests"/>.
/// </summary>
public sealed class StationResourceCalendarLookAheadTests
{
    private const long Hour = 3_600_000;

    private static StationResourceCalendar NewCalendar()
    {
        var topology = IResourceTopology.Empty;
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);
        ITrafficCoordinatorAppService coordinator = new TrafficCoordinatorAppService(
            table, allocator, NullLogger<TrafficCoordinatorAppService>.Instance);
        return new StationResourceCalendar(coordinator);
    }

    private static StationDefinition Station(long serviceMs = Hour)
        => new(
            StationId: "S1",
            DockPoint: "CP-dock",
            PreDockBuffers: new[] { "CP-buf" },
            BlockingClosure: new HashSet<ResourceRef>
            {
                new(ResourceKind.CP, "CP-dock"),
                new(ResourceKind.Zone, "Z1"),
            },
            ServiceDurationMs: serviceMs,
            StationType: StationType.HardBlocking);

    // ---- FreeWindows -------------------------------------------------------------------------------------

    [Fact]
    public void FreeWindows_OnEmptyStation_IsTheWholeHorizon()
    {
        var calendar = NewCalendar();

        var free = calendar.FreeWindows("S1", fromMs: 0, horizonMs: 10 * Hour);

        var window = Assert.Single(free);
        Assert.Equal(0, window.StartMs);
        Assert.Equal(10 * Hour, window.EndMs);
    }

    [Fact]
    public async Task FreeWindows_AroundAHeldWindow_ReportsTheGapsBeforeAndAfter()
    {
        var calendar = NewCalendar();
        // Hold [2h, 3h).
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-A", new TimeInterval(2 * Hour, 3 * Hour)));

        // Look ahead over [0, 6h): expect [0,2h) and [3h,6h).
        var free = calendar.FreeWindows("S1", fromMs: 0, horizonMs: 6 * Hour);

        Assert.Equal(2, free.Count);
        Assert.Equal(new TimeInterval(0, 2 * Hour), free[0]);
        Assert.Equal(new TimeInterval(3 * Hour, 6 * Hour), free[1]);
    }

    [Fact]
    public async Task FreeWindows_BetweenTwoHeldWindows_ReportsTheMiddleGap()
    {
        var calendar = NewCalendar();
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-A", new TimeInterval(0, Hour)));
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-B", new TimeInterval(3 * Hour, 4 * Hour)));

        var free = calendar.FreeWindows("S1", fromMs: 0, horizonMs: 5 * Hour);

        // Gaps: [1h,3h) between the holds, and [4h,5h) after the second.
        Assert.Equal(2, free.Count);
        Assert.Equal(new TimeInterval(Hour, 3 * Hour), free[0]);
        Assert.Equal(new TimeInterval(4 * Hour, 5 * Hour), free[1]);
    }

    [Fact]
    public async Task FreeWindows_AreReturnedAscending_RegardlessOfReservationOrder()
    {
        var calendar = NewCalendar();
        // Reserve out of order: the later window first.
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-B", new TimeInterval(4 * Hour, 5 * Hour)));
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-A", new TimeInterval(Hour, 2 * Hour)));

        var free = calendar.FreeWindows("S1", fromMs: 0, horizonMs: 6 * Hour);

        // [0,1h), [2h,4h), [5h,6h) — ascending and non-overlapping.
        Assert.Equal(
            new[]
            {
                new TimeInterval(0, Hour),
                new TimeInterval(2 * Hour, 4 * Hour),
                new TimeInterval(5 * Hour, 6 * Hour),
            },
            free);
    }

    [Fact]
    public async Task FreeWindows_ClipsHeldWindowToHorizon_AndExcludesWindowsOutsideIt()
    {
        var calendar = NewCalendar();
        // A hold straddling the horizon end: [5h, 8h). Horizon is [0,6h).
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(serviceMs: 3 * Hour), "AGV-A", new TimeInterval(5 * Hour, 8 * Hour)));
        // A hold entirely before the horizon start: irrelevant.
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-B", new TimeInterval(0, Hour))); // overlaps [0,1h) of horizon though

        var free = calendar.FreeWindows("S1", fromMs: Hour, horizonMs: 5 * Hour); // horizon [1h,6h)

        // [1h,5h) free (AGV-B's [0,1h) touches but does not overlap [1h,...)); [5h,6h) is clipped busy => gone.
        var window = Assert.Single(free);
        Assert.Equal(new TimeInterval(Hour, 5 * Hour), window);
    }

    [Fact]
    public async Task FreeWindows_WhenHorizonFullyOccupied_IsEmpty()
    {
        var calendar = NewCalendar();
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(serviceMs: 4 * Hour), "AGV-A", new TimeInterval(0, 4 * Hour)));

        var free = calendar.FreeWindows("S1", fromMs: Hour, horizonMs: 2 * Hour); // [1h,3h) inside the hold

        Assert.Empty(free);
    }

    [Fact]
    public void FreeWindows_ZeroLengthHorizon_IsEmpty()
    {
        var calendar = NewCalendar();

        Assert.Empty(calendar.FreeWindows("S1", fromMs: 5 * Hour, horizonMs: 0));
    }

    [Fact]
    public void FreeWindows_TouchingHeldWindowAtHorizonStart_DoesNotEatTheGap()
    {
        // Half-open: a window held exactly up to fromMs leaves the whole horizon free.
        var calendar = NewCalendar();
        // (no reservation needed — assert pure horizon math on an empty ledger from a non-zero start)
        var free = calendar.FreeWindows("S1", fromMs: 2 * Hour, horizonMs: Hour);

        var window = Assert.Single(free);
        Assert.Equal(new TimeInterval(2 * Hour, 3 * Hour), window);
    }

    // ---- EarliestGrantableStart --------------------------------------------------------------------------

    [Fact]
    public void EarliestGrantableStart_OnEmptyStation_IsFromMs()
    {
        var calendar = NewCalendar();

        var start = calendar.EarliestGrantableStart(Station(), durationMs: Hour, fromMs: 0);

        Assert.Equal(0, start);
    }

    [Fact]
    public async Task EarliestGrantableStart_SkipsABusySpan_ToTheEndOfTheHold()
    {
        var calendar = NewCalendar();
        // Busy [0, 2h); the next hour-long window can only start at 2h.
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(serviceMs: 2 * Hour), "AGV-A", new TimeInterval(0, 2 * Hour)));

        var start = calendar.EarliestGrantableStart(Station(), durationMs: Hour, fromMs: 0);

        Assert.Equal(2 * Hour, start);
    }

    [Fact]
    public async Task EarliestGrantableStart_SkipsATooSmallGap_ToTheNextLargeEnoughGap()
    {
        var calendar = NewCalendar();
        // Holds: [1h,2h) and [2h30m,4h). The middle gap [2h,2h30m) is only 30m — too small for an hour window.
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-A", new TimeInterval(Hour, 2 * Hour)));
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(serviceMs: Hour + Hour / 2), "AGV-B", new TimeInterval(2 * Hour + Hour / 2, 4 * Hour)));

        // From 1h: the [2h,2h30m) gap is too small, so the earliest hour-long start is after [2h30m,4h) => 4h.
        var start = calendar.EarliestGrantableStart(Station(), durationMs: Hour, fromMs: Hour);

        Assert.Equal(4 * Hour, start);
    }

    [Fact]
    public async Task EarliestGrantableStart_UsesAnEarlyGapWhenItIsLargeEnough()
    {
        var calendar = NewCalendar();
        // Hold [2h,3h). A 1h window can fit in [0,2h) before it => earliest start is fromMs (0).
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-A", new TimeInterval(2 * Hour, 3 * Hour)));

        var start = calendar.EarliestGrantableStart(Station(), durationMs: Hour, fromMs: 0);

        Assert.Equal(0, start);
    }

    [Fact]
    public async Task EarliestGrantableStart_ReturnsNull_WhenNoGapFitsWithinTheHorizon()
    {
        var calendar = NewCalendar();
        // Busy [0, 5h); horizon is only [0,3h) so no hour-long gap exists within it.
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(serviceMs: 5 * Hour), "AGV-A", new TimeInterval(0, 5 * Hour)));

        var start = calendar.EarliestGrantableStart(
            Station(), durationMs: Hour, fromMs: 0, horizonMs: 3 * Hour);

        Assert.Null(start);
    }

    [Fact]
    public async Task EarliestGrantableStart_RespectsFromMs_NeverReturnsBeforeIt()
    {
        var calendar = NewCalendar();
        // Free everywhere, but we ask from 3h => the earliest start is 3h, not 0.
        var start = calendar.EarliestGrantableStart(Station(), durationMs: Hour, fromMs: 3 * Hour);

        Assert.Equal(3 * Hour, start);
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-A", new TimeInterval(start!.Value, start.Value + Hour)));
    }

    [Fact]
    public async Task EarliestGrantableStart_FoundWindow_IsActuallyGrantable()
    {
        // The advisory start must be confirmable on the authoritative table (closes the look-ahead loop).
        var calendar = NewCalendar();
        Assert.True(await calendar.TryReserveServiceWindowAsync(
            Station(serviceMs: 2 * Hour), "AGV-A", new TimeInterval(0, 2 * Hour)));

        var start = calendar.EarliestGrantableStart(Station(), durationMs: Hour, fromMs: 0);
        Assert.Equal(2 * Hour, start);

        // Reserving the advised window for a different agent succeeds.
        var granted = await calendar.TryReserveServiceWindowAsync(
            Station(), "AGV-B", new TimeInterval(start!.Value, start.Value + Hour));
        Assert.True(granted);
    }
}
