using NetDevPack.Domain;
using SwarmRoute.PathPlanning.Domain.Shared;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.ValueObjects;

/// <summary>
/// An immutable single-agent planning request: "route <see cref="AgentId"/> from <see cref="FromSiteId"/>
/// to <see cref="ToSiteId"/> on roadmap <see cref="RoadmapId"/>, departing no earlier than
/// <see cref="ReleaseTimeMs"/> (fleet-clock milliseconds)".
/// <para>
/// <see cref="BlacklistedResources"/> is an optional set of resources the plan must avoid (e.g. a contended
/// CP or Lane the coordinator asked the planner to route around). <see cref="BlacklistedSiteIds"/> is kept as
/// a CP-only convenience view for callers that still speak in site ids.
/// </para>
/// Mirrors the inputs of <c>CBS.SearchPath(agentId, startSiteId, endSiteId)</c>, lifted into a value object
/// and extended with the time/blacklist dimensions the first-generation engine lacked.
/// </summary>
public sealed class PlanRequest : ValueObject
{
    private readonly HashSet<string> _blacklistedSiteIds;
    private readonly HashSet<ResourceRef> _blacklistedResources;

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
        IEnumerable<string>? blacklistedSiteIds = null,
        IEnumerable<ResourceRef>? blacklistedResources = null)
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

        _blacklistedResources = new HashSet<ResourceRef>();
        foreach (var siteId in _blacklistedSiteIds)
            _blacklistedResources.Add(new ResourceRef(ResourceKind.CP, siteId));

        if (blacklistedResources is not null)
        {
            foreach (var resource in blacklistedResources)
            {
                if (string.IsNullOrWhiteSpace(resource.Id))
                    continue;

                var normalized = new ResourceRef(resource.Kind, resource.Id.Trim());
                _blacklistedResources.Add(normalized);
                if (normalized.Kind == ResourceKind.CP)
                    _blacklistedSiteIds.Add(normalized.Id);
            }
        }
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

    /// <summary>Optional set of site ids the plan should avoid.</summary>
    public IReadOnlyCollection<string> BlacklistedSiteIds => _blacklistedSiteIds;

    /// <summary>Optional set of CP/Lane resources the plan should avoid.</summary>
    public IReadOnlyCollection<ResourceRef> BlacklistedResources => _blacklistedResources;

    /// <summary>True when <paramref name="siteId"/> is on the blacklist.</summary>
    public bool IsBlacklisted(string siteId) => _blacklistedSiteIds.Contains(siteId);

    /// <summary>True when <paramref name="resource"/> is on the blacklist.</summary>
    public bool IsBlacklisted(ResourceRef resource) => _blacklistedResources.Contains(resource);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return RoadmapId;
        yield return AgentId;
        yield return FromSiteId;
        yield return ToSiteId;
        yield return ReleaseTimeMs;
        foreach (var resource in _blacklistedResources
                     .OrderBy(r => r.Kind)
                     .ThenBy(r => r.Id, StringComparer.Ordinal))
            yield return resource;
    }
}
