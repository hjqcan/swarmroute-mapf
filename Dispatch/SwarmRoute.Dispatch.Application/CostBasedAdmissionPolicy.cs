using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The default <see cref="ICostBasedAdmissionPolicy"/>: a pure, deterministic weighted score over the FMS-V3
/// <see cref="CostBasedAdmissionWeights"/> (成本式准入策略).
/// <para>
/// The score weighs the service's value against the cost of the transit it would block:
/// </para>
/// <code>
/// score = request.Priority * ServiceUrgency
///       - blockedVehicleCount      * BlockedPenalty
///       - highPriorityBlockedCount * HighPriorityPenalty
///       - (impact.HasBypass ? 0 : NoBypassPenalty)
/// </code>
/// <para>
/// where <c>blockedVehicleCount</c> is <see cref="TrafficImpact.AffectedAgentIds"/>'s count and
/// <c>highPriorityBlockedCount</c> is how many of them out-rank the request (per the fleet plan). The service is
/// admitted when the score is at or above <see cref="CostBasedAdmissionWeights.AdmitThreshold"/>; otherwise it
/// defers and the affected vehicles become the clear-first batch. Stateless and thread-safe.
/// </para>
/// </summary>
public sealed class CostBasedAdmissionPolicy : ICostBasedAdmissionPolicy
{
    private readonly CostBasedAdmissionWeights _weights;

    /// <summary>Creates the policy with the given weights (defaults to <see cref="CostBasedAdmissionWeights.Default"/>).</summary>
    /// <param name="weights">The tunable scoring weights; <see langword="null"/> selects the defaults.</param>
    public CostBasedAdmissionPolicy(CostBasedAdmissionWeights? weights = null)
        => _weights = weights ?? CostBasedAdmissionWeights.Default;

    /// <inheritdoc />
    public AdmissionCostScore Score(
        ServiceAdmissionRequest request,
        TrafficImpact impact,
        IFleetPlanProvider? fleetPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(impact);

        var blockedCount = impact.AffectedAgentIds.Count;
        var highPriorityBlocked = CountHigherPriorityThanRequest(request, impact, fleetPlan);

        // value − blocking penalties. long arithmetic so a large fleet × penalty cannot overflow int.
        var score =
            (long)request.Priority * _weights.ServiceUrgency
            - (long)blockedCount * _weights.BlockedPenalty
            - (long)highPriorityBlocked * _weights.HighPriorityPenalty
            - (impact.HasBypass ? 0L : _weights.NoBypassPenalty);

        var admit = score >= _weights.AdmitThreshold;

        // Admit → go first, nothing to clear. Defer → let the affected traffic pass first.
        return new AdmissionCostScore(
            Score: score,
            Admit: admit,
            VehiclesToClearFirst: admit ? [] : impact.AffectedAgentIds);
    }

    /// <summary>
    /// How many affected vehicles strictly out-rank <paramref name="request"/> by the fleet plan's priorities.
    /// A vehicle with an unknown priority is treated as priority <see cref="int.MinValue"/> (never out-ranks), so a
    /// missing plan simply yields zero high-priority blocked.
    /// </summary>
    private static int CountHigherPriorityThanRequest(
        ServiceAdmissionRequest request,
        TrafficImpact impact,
        IFleetPlanProvider? fleetPlan)
    {
        var count = 0;
        foreach (var affectedId in impact.AffectedAgentIds)
        {
            var theirPriority = fleetPlan?.GetPriority(affectedId) ?? int.MinValue;
            if (theirPriority > request.Priority)
                count++;
        }

        return count;
    }
}
