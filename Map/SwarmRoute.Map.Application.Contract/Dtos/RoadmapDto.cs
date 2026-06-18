namespace SwarmRoute.Map.Application.Contract.Dtos;

/// <summary>Full topology read model for a roadmap.</summary>
public sealed record RoadmapDto
{
    /// <summary>Aggregate id.</summary>
    public Guid Id { get; init; }

    /// <summary>Roadmap name.</summary>
    public required string Name { get; init; }

    /// <summary>Optimistic-concurrency version.</summary>
    public long StateVersion { get; init; }

    /// <summary>The sites.</summary>
    public IReadOnlyList<MapSiteDto> Sites { get; init; } = Array.Empty<MapSiteDto>();

    /// <summary>The directed lines.</summary>
    public IReadOnlyList<MapLineDto> Lines { get; init; } = Array.Empty<MapLineDto>();

    /// <summary>The mutual-exclusion blocks.</summary>
    public IReadOnlyList<MapBlockDto> Blocks { get; init; } = Array.Empty<MapBlockDto>();
}
