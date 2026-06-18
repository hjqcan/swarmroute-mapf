using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Topology;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.TrafficControl.Tests;

/// <summary>Shared builders for the TrafficControl test suite.</summary>
internal static class TestHelpers
{
    public static ResourceRef Cp(string id) => new(ResourceKind.CP, id);
    public static ResourceRef Lane(string id) => new(ResourceKind.Lane, id);
    public static ResourceRef Block(string id) => new(ResourceKind.Block, id);

    public static TimeInterval T(long start, long end) => new(start, end);

    public static SpaceTimeCell Cell(ResourceRef r, long start, long end) => new(r, T(start, end));

    /// <summary>Builds a whole-path of CP cells over consecutive windows, each <paramref name="dwell"/>ms long.</summary>
    public static SpaceTimePath CpPath(long start, long dwell, params string[] siteIds)
    {
        var cells = new List<SpaceTimeCell>();
        var t = start;
        foreach (var id in siteIds)
        {
            cells.Add(Cell(Cp(id), t, t + dwell));
            t += dwell;
        }
        return new SpaceTimePath(cells);
    }

    public static SpaceTimePath Path(params SpaceTimeCell[] cells) => new(cells);

    /// <summary>An empty (identity-closure) topology.</summary>
    public static IResourceTopology EmptyTopology => IResourceTopology.Empty;

    /// <summary>
    /// A topology where CP "S" belongs to block "B" and interferes with CP "I" — used to prove the release
    /// closure (parent block + interference) is freed, not leaked.
    /// </summary>
    public static IResourceTopology ClosureTopology(ResourceRef anchor, params ResourceRef[] closureMembers)
        => new DictionaryResourceTopology.Builder()
            .WithClosure(anchor, closureMembers)
            .Build();
}
