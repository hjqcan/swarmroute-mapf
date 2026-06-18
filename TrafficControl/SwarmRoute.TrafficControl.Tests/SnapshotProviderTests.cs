using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class SnapshotProviderTests
{
    [Fact]
    public void Snapshot_maps_active_leases_to_Owns_and_contended_to_Waits()
    {
        var table = new ReservationTable(EmptyTopology);

        // AGV-A holds S1; AGV-B holds S2.
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        table.TryGrant(Path(Cell(Cp("S2"), 0, 100)), "AGV-B");

        // AGV-A then contends on S2 (held by AGV-B) -> a "Waits" edge.
        table.TryGrant(Path(Cell(Cp("S2"), 50, 150)), "AGV-A");

        var provider = new TrafficControlSnapshotProvider(table);
        var snapshot = provider.GetSnapshot();

        // Owns: (AGV-A,S1) and (AGV-B,S2).
        Assert.Contains(("AGV-A", Cp("S1")), snapshot.Owns);
        Assert.Contains(("AGV-B", Cp("S2")), snapshot.Owns);

        // Waits: (AGV-A,S2).
        Assert.Contains(("AGV-A", Cp("S2")), snapshot.Waits);
    }

    [Fact]
    public void Snapshot_is_empty_for_an_empty_table()
    {
        var provider = new TrafficControlSnapshotProvider(new ReservationTable(EmptyTopology));
        var snapshot = provider.GetSnapshot();

        Assert.Empty(snapshot.Owns);
        Assert.Empty(snapshot.Waits);
    }

    [Fact]
    public void Released_lease_drops_out_of_Owns()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");

        var provider = new TrafficControlSnapshotProvider(table);
        Assert.Contains(("AGV-A", Cp("S1")), provider.GetSnapshot().Owns);

        table.ReleaseAll("AGV-A");
        Assert.DoesNotContain(("AGV-A", Cp("S1")), provider.GetSnapshot().Owns);
    }

    [Fact]
    public void Snapshot_preserves_resource_kind()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        table.TryGrant(Path(Cell(Lane("S1"), 100, 200)), "AGV-B");

        var snapshot = new TrafficControlSnapshotProvider(table).GetSnapshot();

        Assert.Contains(("AGV-A", Cp("S1")), snapshot.Owns);
        Assert.Contains(("AGV-B", Lane("S1")), snapshot.Owns);
    }
}
