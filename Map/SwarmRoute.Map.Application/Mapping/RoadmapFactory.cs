using SwarmRoute.Map.Application.Contract.Dtos;
using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Application.Mapping;

/// <summary>
/// Translates inbound DTOs into domain entities via their validating constructors, then assembles the
/// <see cref="Roadmap"/> aggregate (whose own constructor enforces the cross-entity invariants).
/// Kept explicit (not AutoMapper) so all validation flows through domain constructors.
/// </summary>
internal static class RoadmapFactory
{
    public static Roadmap FromRequest(Guid id, ImportRoadmapRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sites = request.Sites.Select(ToSite).ToList();
        var lines = request.Lines.Select(ToLine).ToList();
        var blocks = request.Blocks.Select(ToBlock).ToList();

        return new Roadmap(id, request.Name, sites, lines, blocks);
    }

    private static MapSite ToSite(MapSiteDto dto) => new(
        dto.SiteId,
        dto.SiteType,
        ToPosition(dto.Pos),
        dto.Enable,
        dto.InterferenceSiteIds,
        dto.InterferenceLineIds,
        dto.SiteRole);

    private static MapLine ToLine(MapLineDto dto) => new(
        dto.LineId,
        dto.StartStationId,
        dto.EndStationId,
        dto.Distance,
        dto.LineType,
        dto.ControlPos1 is null ? null : ToPosition(dto.ControlPos1),
        dto.ControlPos2 is null ? null : ToPosition(dto.ControlPos2),
        dto.InterferenceSiteIds,
        dto.InterferenceLineIds);

    private static MapBlock ToBlock(MapBlockDto dto) => new(
        dto.BlockId,
        dto.ContainedSiteIds,
        dto.ContainedLineIds,
        dto.MinPos is null ? null : ToPosition(dto.MinPos),
        dto.MaxPos is null ? null : ToPosition(dto.MaxPos));

    private static MapPosition ToPosition(MapPositionDto dto) => new(dto.X, dto.Y, dto.Angle);
}
