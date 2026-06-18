namespace SwarmRoute.Map.Application.Contract.Dtos;

/// <summary>
/// Lightweight, serialisable summary of a built <c>RoadmapGraph</c> for API responses (the graph VO itself
/// wraps an in-memory structure not intended for direct JSON transport).
/// </summary>
public sealed record RoadmapGraphSummaryDto
{
    /// <summary>Owning roadmap id.</summary>
    public Guid RoadmapId { get; init; }

    /// <summary>Owning roadmap name.</summary>
    public required string RoadmapName { get; init; }

    /// <summary>Roadmap version the graph was built from.</summary>
    public long StateVersion { get; init; }

    /// <summary>Number of vertices (enabled sites).</summary>
    public int VertexCount { get; init; }

    /// <summary>Number of directed edges.</summary>
    public int EdgeCount { get; init; }

    /// <summary>Vertex (site) ids.</summary>
    public IReadOnlyList<string> Vertices { get; init; } = Array.Empty<string>();

    /// <summary>Directed weighted edges.</summary>
    public IReadOnlyList<GraphEdgeDto> Edges { get; init; } = Array.Empty<GraphEdgeDto>();
}

/// <summary>A single directed weighted edge in a <see cref="RoadmapGraphSummaryDto"/>.</summary>
public sealed record GraphEdgeDto(string From, string To, long Weight);
