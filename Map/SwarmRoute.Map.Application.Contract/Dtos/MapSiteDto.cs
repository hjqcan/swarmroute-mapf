using SwarmRoute.Map.Domain.Shared.Enums;

namespace SwarmRoute.Map.Application.Contract.Dtos;

/// <summary>Transport shape for a roadmap site.</summary>
public sealed record MapSiteDto
{
    /// <summary>Topology-stable site id (graph vertex).</summary>
    public required string SiteId { get; init; }

    /// <summary>Functional site type.</summary>
    public MapSiteType SiteType { get; init; } = MapSiteType.RelaySite;

    /// <summary>FMS dispatch role of the site (additive; complements <see cref="SiteType"/>). Defaults to <see cref="SiteRole.Transit"/>.</summary>
    public SiteRole SiteRole { get; init; } = SiteRole.Transit;

    /// <summary>Pose of the site.</summary>
    public required MapPositionDto Pos { get; init; }

    /// <summary>Whether the site participates in the graph.</summary>
    public bool Enable { get; init; } = true;

    /// <summary>Ids of interfering sites.</summary>
    public IReadOnlyList<string> InterferenceSiteIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids of interfering lines.</summary>
    public IReadOnlyList<string> InterferenceLineIds { get; init; } = Array.Empty<string>();
}
