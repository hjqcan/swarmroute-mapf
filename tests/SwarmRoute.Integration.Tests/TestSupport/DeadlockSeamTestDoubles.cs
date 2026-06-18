using System.Collections.Generic;
using System.Linq;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.TrafficControl.Application.Contract.Services;

namespace SwarmRoute.Integration.Tests.TestSupport;

/// <summary>
/// Avoidance-point selector returning a fixed site (honouring the anti-livelock exclusion set). Stands in
/// for the Map-backed <c>MapAvoidancePointSelector</c>, which needs an <c>IRoadmapRepository</c> the
/// graph-backed test host does not have — so closed-loop tests focus on the resolve/recover loop while the
/// real Map selection keeps its own unit coverage (<c>MapAvoidancePointSelectorTests</c>).
/// </summary>
internal sealed class FixedAvoidancePointSelector(string siteId) : IAvoidancePointSelector
{
    public string? SelectAvoidancePoint(string victimAgentId, IReadOnlySet<string>? excludedSiteIds = null)
        => excludedSiteIds is not null && excludedSiteIds.Contains(siteId) ? null : siteId;
}

/// <summary>Detour reservation that always succeeds (so <c>SolveAsync</c> parks the plan at ConfirmCleared).</summary>
internal sealed class AlwaysGrantDetourReservationService : IDetourReservationService
{
    public Task<bool> TryReserveDetourAsync(
        string victimAgentId,
        string avoidanceSiteId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>
/// Real clearance confirmer for the test host: re-detects over a fresh TrafficControl snapshot and reports
/// "cleared" iff the victim is no longer in any cycle (the same logic as the Host's
/// <c>SnapshotClearanceConfirmer</c>, duplicated here because tests do not reference the Host assembly).
/// </summary>
internal sealed class SnapshotClearanceConfirmer(
    ITrafficControlSnapshotProvider snapshots,
    IDeadlockDetector detector) : IClearanceConfirmer
{
    public bool IsCleared(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return true;
        var cycles = detector.Detect(snapshots.GetSnapshot());
        return cycles.All(cycle => !cycle.Contains(victimAgentId));
    }
}
