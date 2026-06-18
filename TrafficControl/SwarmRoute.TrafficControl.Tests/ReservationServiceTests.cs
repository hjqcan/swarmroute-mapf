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
    public void Live_view_reflects_the_reservation_table()
    {
        var table = new ReservationTable(EmptyTopology);
        var service = new ReservationService(table);
        var view = service.GetView(Guid.NewGuid());

        // Initially free.
        Assert.True(view.IsFree(Cp("S1"), T(0, 100)));

        // Grant -> the same view (re-read) now reports the resource as busy in that window.
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        var view2 = service.GetView(Guid.NewGuid());
        Assert.False(view2.IsFree(Cp("S1"), T(50, 60)));
        Assert.True(view2.IsFree(Cp("S1"), T(100, 200)));

        // FreeIntervals exposes the post-lease gap.
        Assert.Contains(view2.FreeIntervals(Cp("S1")), s => s.Interval.StartMs == 100 && s.Interval.EndMs == long.MaxValue);
    }
}
