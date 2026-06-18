using NetDevPack.Domain;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Events;
using SwarmRoute.Map.Domain.Shared;

namespace SwarmRoute.Map.Domain.Aggregates;

/// <summary>
/// The roadmap aggregate root: the durable master record of a fleet's static topology — its sites,
/// directed lines and mutual-exclusion blocks. Consolidates the three duplicated <c>GraphMap</c>
/// implementations into a single Map-context aggregate.
/// <para>
/// Invariants enforced at construction and on every mutation:
/// <list type="bullet">
///   <item>At least one site.</item>
///   <item>Site ids are unique; line ids are unique.</item>
///   <item>Every line's start and end station id resolves to a site in this roadmap (no dangling endpoints).</item>
///   <item>Every block's contained site/line ids resolve to members of this roadmap (no dangling members).</item>
/// </list>
/// Violations throw <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public class Roadmap : Entity, IAggregateRoot
{
    private readonly List<MapSite> _sites = new();
    private readonly List<MapLine> _lines = new();
    private readonly List<MapBlock> _blocks = new();

    // EF Core
    private Roadmap()
    {
        Name = string.Empty;
    }

    /// <summary>
    /// Creates and validates a roadmap. The aggregate id is the EF surrogate <see cref="Entity.Id"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when any invariant is violated.</exception>
    public Roadmap(
        Guid id,
        string name,
        IEnumerable<MapSite> sites,
        IEnumerable<MapLine> lines,
        IEnumerable<MapBlock>? blocks = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Roadmap id must not be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Roadmap name must not be empty.", nameof(name));

        Id = id;
        Name = name.Trim();

        _sites.AddRange(sites ?? throw new ArgumentNullException(nameof(sites)));
        _lines.AddRange(lines ?? throw new ArgumentNullException(nameof(lines)));
        if (blocks is not null)
            _blocks.AddRange(blocks);

        Validate();

        StateVersion = 1;
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Human-readable roadmap name.</summary>
    public string Name { get; private set; }

    /// <summary>Optimistic-concurrency version, incremented on every edit.</summary>
    public long StateVersion { get; private set; }

    /// <summary>UTC timestamp of the last state change.</summary>
    public DateTimeOffset? StateChangedAtUtc { get; private set; }

    /// <summary>The sites of this roadmap.</summary>
    public IReadOnlyList<MapSite> Sites => _sites.AsReadOnly();

    /// <summary>The directed lines of this roadmap.</summary>
    public IReadOnlyList<MapLine> Lines => _lines.AsReadOnly();

    /// <summary>The mutual-exclusion blocks of this roadmap.</summary>
    public IReadOnlyList<MapBlock> Blocks => _blocks.AsReadOnly();

    /// <summary>Looks up a site by its topology id, or <c>null</c>.</summary>
    public MapSite? FindSite(string siteId) => _sites.FirstOrDefault(s => string.Equals(s.SiteId, siteId, StringComparison.Ordinal));

    /// <summary>Looks up a line by its topology id, or <c>null</c>.</summary>
    public MapLine? FindLine(string lineId) => _lines.FirstOrDefault(l => string.Equals(l.LineId, lineId, StringComparison.Ordinal));

    /// <summary>Renames the roadmap.</summary>
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Roadmap name must not be empty.", nameof(name));
        if (string.Equals(Name, name.Trim(), StringComparison.Ordinal))
            return;

        Name = name.Trim();
        IncrementStateVersion();
    }

    /// <summary>
    /// Replaces the entire topology atomically, re-validating all invariants before committing. Use this for
    /// re-import / publish so the graph is rebuilt from a consistent set.
    /// </summary>
    public void ReplaceTopology(
        IEnumerable<MapSite> sites,
        IEnumerable<MapLine> lines,
        IEnumerable<MapBlock>? blocks = null)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(lines);

        var newSites = sites.ToList();
        var newLines = lines.ToList();
        var newBlocks = blocks?.ToList() ?? new List<MapBlock>();

        Validate(newSites, newLines, newBlocks);

        _sites.Clear();
        _sites.AddRange(newSites);
        _lines.Clear();
        _lines.AddRange(newLines);
        _blocks.Clear();
        _blocks.AddRange(newBlocks);

        IncrementStateVersion();
    }

    /// <summary>
    /// Raises the integration event marking this roadmap version as imported (newly persisted).
    /// </summary>
    public void MarkImported()
        => AddDomainEvent(new MapRoadmapImportedEvent(Id, Name, StateVersion, _sites.Count, _lines.Count, _blocks.Count));

    /// <summary>
    /// Raises the integration event marking this roadmap version as published (active topology). Consumers
    /// (PathPlanning / Coordination) use this to invalidate their cached <c>RoadmapGraph</c>.
    /// </summary>
    public void MarkPublished()
        => AddDomainEvent(new MapRoadmapPublishedEvent(Id, Name, StateVersion));

    /// <summary>Optimistic-concurrency check.</summary>
    public bool CheckVersion(long expectedVersion) => StateVersion == expectedVersion;

    private void IncrementStateVersion()
    {
        checked { StateVersion++; }
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    private void Validate() => Validate(_sites, _lines, _blocks);

    private static void Validate(IReadOnlyList<MapSite> sites, IReadOnlyList<MapLine> lines, IReadOnlyList<MapBlock> blocks)
    {
        if (sites.Count == 0)
            throw new ArgumentException($"[{MapErrorCodes.RoadmapHasNoSites}] A roadmap must contain at least one site.", nameof(sites));

        var siteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            if (!siteIds.Add(site.SiteId))
                throw new ArgumentException($"[{MapErrorCodes.DuplicateSiteId}] Duplicate site id '{site.SiteId}'.", nameof(sites));
        }

        var lineIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!lineIds.Add(line.LineId))
                throw new ArgumentException($"[{MapErrorCodes.DuplicateLineId}] Duplicate line id '{line.LineId}'.", nameof(lines));

            if (!siteIds.Contains(line.StartStationId))
                throw new ArgumentException(
                    $"[{MapErrorCodes.DanglingLineEndpoint}] Line '{line.LineId}' start station '{line.StartStationId}' does not resolve to a site.",
                    nameof(lines));

            if (!siteIds.Contains(line.EndStationId))
                throw new ArgumentException(
                    $"[{MapErrorCodes.DanglingLineEndpoint}] Line '{line.LineId}' end station '{line.EndStationId}' does not resolve to a site.",
                    nameof(lines));
        }

        foreach (var block in blocks)
        {
            foreach (var siteId in block.ContainedSiteIds)
            {
                if (!siteIds.Contains(siteId))
                    throw new ArgumentException(
                        $"[{MapErrorCodes.DanglingBlockMember}] Block '{block.BlockId}' contains unknown site '{siteId}'.",
                        nameof(blocks));
            }

            foreach (var lineId in block.ContainedLineIds)
            {
                if (!lineIds.Contains(lineId))
                    throw new ArgumentException(
                        $"[{MapErrorCodes.DanglingBlockMember}] Block '{block.BlockId}' contains unknown line '{lineId}'.",
                        nameof(blocks));
            }
        }
    }
}
