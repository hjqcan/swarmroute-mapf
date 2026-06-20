namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// The estimated effect on transit traffic of holding a station's blocking closure for a service window
/// (交通影響評估). Produced by the (Round 2) traffic-impact analyser and weighed by the scheduler when deciding
/// whether — and how expensively — to admit a service.
/// </summary>
/// <param name="AffectedAgentIds">Ids of vehicles whose routes intersect the blocking closure during the window.</param>
/// <param name="BlocksTransitCore">Whether holding the closure severs the transit core (no through path remains).</param>
/// <param name="HasBypass">Whether affected traffic has a viable bypass around the closure.</param>
/// <param name="EstWaitTicks">Estimated aggregate wait (in ticks) imposed on affected traffic; must be &gt;= 0.</param>
public sealed record TrafficImpact(
    IReadOnlyList<string> AffectedAgentIds,
    bool BlocksTransitCore,
    bool HasBypass,
    int EstWaitTicks)
{
    /// <summary>Ids of vehicles whose routes intersect the blocking closure during the window.</summary>
    public IReadOnlyList<string> AffectedAgentIds { get; } =
        Validation.NotNull(AffectedAgentIds, nameof(AffectedAgentIds));

    /// <summary>Estimated aggregate wait (in ticks) imposed on affected traffic; always &gt;= 0.</summary>
    public int EstWaitTicks { get; } = Validation.NotNegative(EstWaitTicks, nameof(EstWaitTicks));
}
