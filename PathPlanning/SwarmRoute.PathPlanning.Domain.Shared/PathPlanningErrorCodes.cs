namespace SwarmRoute.PathPlanning.Domain.Shared;

/// <summary>
/// Stable error-code constants for the PathPlanning bounded context. Codes are namespaced
/// <c>PP-xxx</c> so they can be surfaced consistently across the domain, application and (later) API layers.
/// </summary>
public static class PathPlanningErrorCodes
{
    /// <summary>A required identifier (agent / site) was null or whitespace.</summary>
    public const string MissingIdentifier = "PP-001";

    /// <summary>The start and/or goal site does not exist in the roadmap graph.</summary>
    public const string UnknownSite = "PP-002";

    /// <summary>No route exists from the start to the goal site (unreachable / endpoint blocked).</summary>
    public const string NoRoute = "PP-003";

    /// <summary>The release time was negative.</summary>
    public const string NegativeReleaseTime = "PP-004";

    /// <summary>An <c>AgentPlan</c> behaviour was invoked with a result inconsistent with its current state.</summary>
    public const string InvalidPlanTransition = "PP-005";
}
