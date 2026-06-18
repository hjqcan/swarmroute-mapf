using NetDevPack.Domain;
using SwarmRoute.PathPlanning.Domain.Shared;

namespace SwarmRoute.PathPlanning.Domain.ValueObjects;

/// <summary>
/// An immutable single-agent planning request: "route <see cref="AgentId"/> from <see cref="FromSiteId"/>
/// to <see cref="ToSiteId"/> on roadmap <see cref="RoadmapId"/>, departing no earlier than
/// <see cref="ReleaseTimeMs"/> (fleet-clock milliseconds)".
/// <para>
/// <see cref="BlacklistedSiteIds"/> is an optional set of sites the plan must avoid (e.g. a contended
/// resource the coordinator asked the planner to route around). v0's Dijkstra planner records it on the
/// request but does not yet prune the graph by it — full blacklist-aware search arrives with the v1 SIPP
/// planner / TrafficControl allocator.
/// </para>
/// Mirrors the inputs of <c>CBS.SearchPath(agentId, startSiteId, endSiteId)</c>, lifted into a value object
/// and extended with the time/blacklist dimensions the first-generation engine lacked.
/// </summary>
public sealed class PlanRequest : ValueObject
{
    private readonly HashSet<string> _blacklistedSiteIds;

    /// <summary>
    /// Creates a validated plan request.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when any id is null/whitespace or <paramref name="releaseTimeMs"/> is negative.
    /// </exception>
    public PlanRequest(
        Guid roadmapId,
        string agentId,
        string fromSiteId,
        string toSiteId,
        long releaseTimeMs = 0,
        IEnumerable<string>? blacklistedSiteIds = null)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] Agent id must not be empty.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(fromSiteId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] From-site id must not be empty.", nameof(fromSiteId));
        if (string.IsNullOrWhiteSpace(toSiteId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] To-site id must not be empty.", nameof(toSiteId));
        if (releaseTimeMs < 0)
            throw new ArgumentException($"[{PathPlanningErrorCodes.NegativeReleaseTime}] Release time must be >= 0 (was {releaseTimeMs}).", nameof(releaseTimeMs));

        RoadmapId = roadmapId;
        AgentId = agentId.Trim();
        FromSiteId = fromSiteId.Trim();
        ToSiteId = toSiteId.Trim();
        ReleaseTimeMs = releaseTimeMs;

        _blacklistedSiteIds = blacklistedSiteIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                blacklistedSiteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()),
                StringComparer.Ordinal);
    }

    /// <summary>The roadmap whose graph the plan is computed against.</summary>
    public Guid RoadmapId { get; }

    /// <summary>The agent (vehicle) the plan is for.</summary>
    public string AgentId { get; }

    /// <summary>The start site (graph source vertex).</summary>
    public string FromSiteId { get; }

    /// <summary>The goal site (graph destination vertex).</summary>
    public string ToSiteId { get; }

    /// <summary>Earliest departure instant on the fleet clock, in milliseconds. The plan's timeline starts here.</summary>
    public long ReleaseTimeMs { get; }

    /// <summary>Optional set of site ids the plan should avoid (reserved for v1 blacklist-aware search).</summary>
    public IReadOnlyCollection<string> BlacklistedSiteIds => _blacklistedSiteIds;

    /// <summary>True when <paramref name="siteId"/> is on the blacklist.</summary>
    public bool IsBlacklisted(string siteId) => _blacklistedSiteIds.Contains(siteId);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return RoadmapId;
        yield return AgentId;
        yield return FromSiteId;
        yield return ToSiteId;
        yield return ReleaseTimeMs;
        foreach (var id in _blacklistedSiteIds.OrderBy(id => id, StringComparer.Ordinal))
            yield return id;
    }
}
