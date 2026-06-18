using SwarmRoute.Map.Domain.Shared.Enums;

namespace SwarmRoute.Map.Application.Contract.Dtos;

/// <summary>Transport shape for a directed roadmap line.</summary>
public sealed record MapLineDto
{
    /// <summary>Topology-stable line id.</summary>
    public required string LineId { get; init; }

    /// <summary>Start station id (edge source).</summary>
    public required string StartStationId { get; init; }

    /// <summary>End station id (edge destination).</summary>
    public required string EndStationId { get; init; }

    /// <summary>Length in metres (edge weight = Distance * 1000).</summary>
    public double Distance { get; init; }

    /// <summary>Geometry type.</summary>
    public MapLineType LineType { get; init; } = MapLineType.Straight;

    /// <summary>Optional first Bézier control point.</summary>
    public MapPositionDto? ControlPos1 { get; init; }

    /// <summary>Optional second Bézier control point.</summary>
    public MapPositionDto? ControlPos2 { get; init; }

    /// <summary>Ids of interfering sites.</summary>
    public IReadOnlyList<string> InterferenceSiteIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids of interfering lines.</summary>
    public IReadOnlyList<string> InterferenceLineIds { get; init; } = Array.Empty<string>();
}
