using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Map-backed <see cref="IAvoidancePointSelector"/>: picks a free yield point for a deadlock victim from the
/// active roadmap. Candidates are sites typed <see cref="MapSiteType.AvoidSite"/> (preferred) or
/// <see cref="MapSiteType.RelaySite"/> — the dedicated waypoints the topology designer placed for exactly this
/// purpose. A candidate is "free" when no other agent currently holds its CP resource in TrafficControl's live
/// snapshot, so the resolver never sends the victim onto an occupied point (invariant I1).
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

    public MapAvoidancePointSelector(
        IServiceScopeFactory scopeFactory,
        ICoordinationGoalSource goalSource,
        ITrafficControlSnapshotProvider snapshots)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _goalSource = goalSource ?? throw new ArgumentNullException(nameof(goalSource));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
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

        // Resources currently held by ANY agent — the victim must not be sent onto one of these.
        var occupied = _snapshots.GetSnapshot().Owns
            .Select(o => o.Resource)
            .ToHashSet();

        foreach (var type in PreferenceOrder)
        {
            var candidate = roadmap.Sites
                .Where(s => s.Enable && s.SiteType == type)
                .Select(s => s.SiteId)
                .Where(id => !occupied.Contains(RoadmapGraph.SiteRef(id)))
                .OrderBy(id => id, StringComparer.Ordinal)
                .FirstOrDefault();

            if (candidate is not null)
                return candidate;
        }

        return null;
    }
}
