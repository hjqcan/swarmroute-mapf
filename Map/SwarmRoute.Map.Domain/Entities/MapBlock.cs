using NetDevPack.Domain;
using SwarmRoute.Map.Domain.Shared;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Domain.Entities;

/// <summary>
/// A mutual-exclusion block (互斥区块) grouping a set of sites and lines, with an axis-aligned bounding box.
/// An entity WITHIN the <see cref="Aggregates.Roadmap"/> aggregate (not an aggregate root).
/// Ported from <c>AJR.MAPF.Map.MapBlock</c>; contained members are referenced by stable string id.
/// </summary>
public class MapBlock : Entity
{
    private readonly List<string> _containedSiteIds = new();
    private readonly List<string> _containedLineIds = new();

    // EF Core
    private MapBlock()
    {
        BlockId = string.Empty;
        MinPos = MapPosition.Empty;
        MaxPos = MapPosition.Empty;
    }

    /// <summary>Creates a block. <paramref name="blockId"/> is the topology-stable key.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="blockId"/> is null/whitespace.</exception>
    public MapBlock(
        string blockId,
        IEnumerable<string>? containedSiteIds = null,
        IEnumerable<string>? containedLineIds = null,
        MapPosition? minPos = null,
        MapPosition? maxPos = null)
    {
        if (string.IsNullOrWhiteSpace(blockId))
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Block id must not be empty.", nameof(blockId));

        BlockId = blockId.Trim();
        MinPos = minPos ?? MapPosition.Empty;
        MaxPos = maxPos ?? MapPosition.Empty;

        if (containedSiteIds is not null)
            _containedSiteIds.AddRange(containedSiteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
        if (containedLineIds is not null)
            _containedLineIds.AddRange(containedLineIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
    }

    /// <summary>Topology-stable block identifier.</summary>
    public string BlockId { get; private set; }

    /// <summary>Lower corner of the block's axis-aligned bounding box.</summary>
    public MapPosition MinPos { get; private set; }

    /// <summary>Upper corner of the block's axis-aligned bounding box.</summary>
    public MapPosition MaxPos { get; private set; }

    /// <summary>Ids of the sites contained in this block.</summary>
    public IReadOnlyCollection<string> ContainedSiteIds => _containedSiteIds.AsReadOnly();

    /// <summary>Ids of the lines contained in this block.</summary>
    public IReadOnlyCollection<string> ContainedLineIds => _containedLineIds.AsReadOnly();
}
