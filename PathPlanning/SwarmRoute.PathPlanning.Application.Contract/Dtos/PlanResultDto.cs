namespace SwarmRoute.PathPlanning.Application.Contract.Dtos;

/// <summary>
/// Serialisable outcome of a planning request: a success flag, the ordered site ids of the route, summary
/// cost and (on failure) the reason. This is the transport projection of the domain <c>PlanResult</c> +
/// <c>SpaceTimePath</c> (the space-time path itself wraps Kernel types not intended for direct transport).
/// </summary>
public sealed record PlanResultDto
{
    /// <summary>The roadmap the plan was computed against.</summary>
    public Guid RoadmapId { get; init; }

    /// <summary>The agent (vehicle) the plan is for.</summary>
    public required string AgentId { get; init; }

    /// <summary>The start site.</summary>
    public required string FromSiteId { get; init; }

    /// <summary>The goal site.</summary>
    public required string ToSiteId { get; init; }

    /// <summary>True when a valid route was found.</summary>
    public bool Success { get; init; }

    /// <summary>The ordered site ids of the route (inclusive of both ends); empty on failure.</summary>
    public IReadOnlyList<string> SiteSequence { get; init; } = Array.Empty<string>();

    /// <summary>Cumulative scaled edge weight along the route (0 on failure).</summary>
    public long DistanceUnits { get; init; }

    /// <summary>Number of hops (edges) traversed (0 on failure).</summary>
    public int HopCount { get; init; }

    /// <summary>Planned travel duration in fleet-clock milliseconds (0 on failure).</summary>
    public long DurationMs { get; init; }

    /// <summary>The failure reason, or <c>null</c> on success.</summary>
    public string? FailureReason { get; init; }
}
