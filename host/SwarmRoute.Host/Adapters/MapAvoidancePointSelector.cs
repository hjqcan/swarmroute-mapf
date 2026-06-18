using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Map-backed <see cref="IAvoidancePointSelector"/>: picks a free yield point for a deadlock victim from the
/// active roadmap. Candidates are sites typed <see cref="MapSiteType.AvoidSite"/> (preferred) or
/// <see cref="MapSiteType.RelaySite"/> — the dedicated waypoints the topology designer placed for exactly this
/// purpose. A candidate is "free" when no other agent currently holds any resource in that site's topology
/// closure and the site is not blacklisted for the victim, so the resolver never sends the victim onto a point
/// TrafficControl would reject through closure semantics (invariant I1).
/// </summary>
/// <remarks>
/// Returns <c>null</c> when no roadmap is selected or no free avoid/relay site exists; the
/// <c>AvoidanceDeadlockResolver</c> then escalates (still emitting <c>Deadlock.Case.ResolutionRequested</c>),
/// matching the architecture's "Deadlock stays a pure analyser, fleet reacts" contract. Selection is
/// deterministic (ordinal site-id order) so a given deadlock resolves the same way every run (no livelock, R6).
/// </remarks>
public sealed class MapAvoidancePointSelector : IAvoidancePointSelector
{
    private static readonly MapSiteType[] PreferenceOrder =
        [MapSiteType.AvoidSite, MapSiteType.RelaySite];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICoordinationGoalSource _goalSource;
    private readonly ITrafficControlSnapshotProvider _snapshots;
    private readonly IResourceTopology _topology;

    public MapAvoidancePointSelector(
        IServiceScopeFactory scopeFactory,
        ICoordinationGoalSource goalSource,
        ITrafficControlSnapshotProvider snapshots,
        IResourceTopology topology)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _goalSource = goalSource ?? throw new ArgumentNullException(nameof(goalSource));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    }

    /// <inheritdoc />
    public string? SelectAvoidancePoint(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return null;

        var roadmapId = _goalSource.CurrentRoadmapId;
        if (roadmapId is null)
            return null;

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRoadmapRepository>();
        var roadmap = repository.GetWithTopologyAsync(roadmapId.Value).GetAwaiter().GetResult();
        if (roadmap is null)
            return null;

        // Resources currently held by other agents. The eventual TrafficControl write uses IsFreeForExcept,
        // so the selector should not reject a candidate only because the victim already owns a closure member.
        var occupiedByOthers = _snapshots.GetSnapshot().Owns
            .Where(o => !string.Equals(o.AgentId, victimAgentId, StringComparison.Ordinal))
            .Select(o => o.Resource)
            .ToHashSet();

        foreach (var type in PreferenceOrder)
        {
            var candidate = roadmap.Sites
                .Where(s => s.Enable && s.SiteType == type)
                .Select(s => s.SiteId)
                .Where(id => IsCandidateFree(id, victimAgentId, occupiedByOthers))
                .OrderBy(id => id, StringComparer.Ordinal)
                .FirstOrDefault();

            if (candidate is not null)
                return candidate;
        }

        return null;
    }

    private bool IsCandidateFree(string siteId, string victimAgentId, IReadOnlySet<ResourceRef> occupiedByOthers)
    {
        var site = RoadmapGraph.SiteRef(siteId);
        foreach (var member in _topology.ClosureOf(site))
        {
            if (occupiedByOthers.Contains(member))
                return false;

            if (_topology.IsBlacklisted(member, victimAgentId))
                return false;
        }

        return true;
    }
}
