using SwarmRoute.PathPlanning.Domain.Reservations;
using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class ReservationServiceTests
{
    [Fact]
    public void ReservationService_is_an_IReservationQuery()
    {
        var table = new ReservationTable(EmptyTopology);
        IReservationQuery query = new ReservationService(table);

        var view = query.GetView(Guid.NewGuid());
        Assert.NotNull(view);
    }

    [Fact]
    public void View_is_a_snapshot_of_the_reservation_table()
    {
        var table = new ReservationTable(EmptyTopology);
        var service = new ReservationService(table);
        var view = service.GetView(Guid.NewGuid());

        // Initially free.
        Assert.True(view.IsFree(Cp("S1"), T(0, 100)));

        // Grant -> the old view stays stable; a fresh view sees the busy window.
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        var view2 = service.GetView(Guid.NewGuid());
        Assert.True(view.IsFree(Cp("S1"), T(50, 60)));
        Assert.False(view2.IsFree(Cp("S1"), T(50, 60)));
        Assert.True(view2.IsFree(Cp("S1"), T(100, 200)));

        // FreeIntervals exposes the post-lease gap.
        Assert.Contains(view2.FreeIntervals(Cp("S1")), s => s.Interval.StartMs == 100 && s.Interval.EndMs == long.MaxValue);
    }

    [Fact]
    public void Snapshot_view_treats_reversed_lane_as_busy()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Lane("B-A"), 0, 100)), "AGV-A");
        var view = new ReservationService(table).GetView(Guid.NewGuid());

        Assert.False(view.IsFree(Lane("A-B"), T(50, 60)));
        Assert.Contains(view.FreeIntervals(Lane("A-B")), s => s.Interval.StartMs == 100 && s.Interval.EndMs == long.MaxValue);
    }
}
