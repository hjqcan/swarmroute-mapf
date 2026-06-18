namespace SwarmRoute.Map.Domain.Shared;

/// <summary>
/// Stable error-code constants for the Map bounded context. Codes are namespaced <c>MAP-xxx</c> so they
/// can be surfaced consistently across the domain, application and API layers.
/// </summary>
public static class MapErrorCodes
{
    /// <summary>A roadmap was constructed with no sites.</summary>
    public const string RoadmapHasNoSites = "MAP-001";

    /// <summary>Two or more sites share the same identifier.</summary>
    public const string DuplicateSiteId = "MAP-002";

    /// <summary>Two or more lines share the same identifier.</summary>
    public const string DuplicateLineId = "MAP-003";

    /// <summary>A line references a start/end site identifier that does not exist in the roadmap.</summary>
    public const string DanglingLineEndpoint = "MAP-004";

    /// <summary>A block references a contained site/line identifier that does not exist in the roadmap.</summary>
    public const string DanglingBlockMember = "MAP-005";

    /// <summary>A required identifier (roadmap/site/line/block) was null or whitespace.</summary>
    public const string MissingIdentifier = "MAP-006";

    /// <summary>The requested roadmap could not be found.</summary>
    public const string RoadmapNotFound = "MAP-007";

    /// <summary>A line declared a negative distance.</summary>
    public const string NegativeDistance = "MAP-008";
}
