namespace SwarmRoute.Map.Application.Contract.Dtos;

/// <summary>Transport shape for a mutual-exclusion block.</summary>
public sealed record MapBlockDto
{
    /// <summary>Topology-stable block id.</summary>
    public required string BlockId { get; init; }

    /// <summary>Ids of contained sites.</summary>
    public IReadOnlyList<string> ContainedSiteIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids of contained lines.</summary>
    public IReadOnlyList<string> ContainedLineIds { get; init; } = Array.Empty<string>();

    /// <summary>Lower corner of the bounding box.</summary>
    public MapPositionDto? MinPos { get; init; }

    /// <summary>Upper corner of the bounding box.</summary>
    public MapPositionDto? MaxPos { get; init; }
}
