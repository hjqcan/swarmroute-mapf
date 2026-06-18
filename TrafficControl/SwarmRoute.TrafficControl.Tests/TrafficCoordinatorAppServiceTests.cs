using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class TrafficCoordinatorAppServiceTests
{
    private static (ITrafficCoordinatorAppService svc, ReservationTable table) Build(IResourceTopology topology)
    {
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);
        var svc = new TrafficCoordinatorAppService(table, allocator, NullLogger<TrafficCoordinatorAppService>.Instance);
        return (svc, table);
    }

    [Fact]
    public void TryReserve_grants_then_crossing_path_queued_then_release_unblocks()
    {
        var (svc, table) = Build(EmptyTopology);

        Assert.Equal(AllocationOutcome.Granted, svc.TryReserve(CpPath(0, 100, "S1", "S2", "S3"), "AGV-A"));

        // Crossing at S2 over an overlapping window -> queued.
        Assert.Equal(AllocationOutcome.Queued, svc.TryReserve(CpPath(50, 100, "X1", "S2", "X3"), "AGV-B"));

        // AGV-A drives past S2 and releases it.
        svc.Release("AGV-A", new[] { Cp("S1"), Cp("S2") });
        Assert.DoesNotContain(table.ActiveLeases, l => l.AgentId == "AGV-A" && l.Resource == Cp("S2"));

        // Now AGV-B can take a path through S2 (time-separated from the remaining AGV-A lease on S3).
        Assert.Equal(AllocationOutcome.Granted, svc.TryReserve(CpPath(400, 100, "X1", "S2", "X4"), "AGV-B"));
    }

    [Fact]
    public void Release_frees_full_closure_through_the_seam()
    {
        var topology = ClosureTopology(Cp("S1"), Block("B1"), Cp("I1"));
        var (svc, table) = Build(topology);

        svc.TryReserve(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        Assert.Equal(3, table.ActiveLeases.Count); // S1 + B1 + I1

        svc.Release("AGV-A", new[] { Cp("S1") });

        Assert.Empty(table.ActiveLeases); // closure fully released, no leak
    }
}
