using SwarmRoute.TrafficControl.Application.Topology;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public sealed class ResourceAllocatorTests
{
    [Fact]
    public void BlockedResources_maps_block_contention_back_to_candidate_cp()
    {
        var block = Block("B1");
        var topology = new DictionaryResourceTopology.Builder()
            .WithClosure(Cp("S1"), [block])
            .WithClosure(Cp("S2"), [block])
            .Build();
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);

        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A"));

        var blocked = allocator.BlockedResources(table, Path(Cell(Cp("S2"), 50, 150)), "AGV-B");

        Assert.Contains(Cp("S2"), blocked);
        Assert.DoesNotContain(block, blocked);
    }

    [Fact]
    public void BlockedResources_maps_block_contention_back_to_candidate_lane()
    {
        var block = Block("B1");
        var lane = Lane("S2-S3");
        var topology = new DictionaryResourceTopology.Builder()
            .WithClosure(lane, [block])
            .Build();
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);

        Assert.Equal(AllocationOutcome.Granted, table.TryGrant(Path(Cell(block, 0, 100)), "AGV-A"));

        var blocked = allocator.BlockedResources(table, Path(Cell(lane, 50, 150)), "AGV-B");

        Assert.Contains(lane, blocked);
        Assert.DoesNotContain(block, blocked);
    }
}
