namespace SwarmRoute.Map.Application.Contract.Dtos;

/// <summary>Request to import (create) a roadmap topology.</summary>
public sealed record ImportRoadmapRequest
{
    /// <summary>Optional explicit aggregate id; a new id is generated when null/empty.</summary>
    public Guid? Id { get; init; }

    /// <summary>Roadmap name (must be unique).</summary>
    public required string Name { get; init; }

    /// <summary>The sites to import.</summary>
    public IReadOnlyList<MapSiteDto> Sites { get; init; } = Array.Empty<MapSiteDto>();

    /// <summary>The directed lines to import.</summary>
    public IReadOnlyList<MapLineDto> Lines { get; init; } = Array.Empty<MapLineDto>();

    /// <summary>The mutual-exclusion blocks to import.</summary>
    public IReadOnlyList<MapBlockDto> Blocks { get; init; } = Array.Empty<MapBlockDto>();

    /// <summary>When true, the roadmap is published (raising <c>Map.Roadmap.Published</c>) immediately after import.</summary>
    public bool PublishOnImport { get; init; } = true;
}
