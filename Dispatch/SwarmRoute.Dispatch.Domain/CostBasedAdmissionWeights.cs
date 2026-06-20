namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// The tunable weights of the FMS-V3 cost-based admission score (成本式准入權重).
/// <para>
/// The score balances the <em>value</em> of admitting a service now against the <em>cost</em> it imposes on the
/// transit traffic its blocking closure would displace:
/// </para>
/// <code>
/// score = ServicePriority * ServiceUrgency
///       - BlockedVehicleCount     * BlockedPenalty
///       - HighPriorityBlockedCount * HighPriorityPenalty
///       - (NoBypass ? NoBypassPenalty : 0)
/// </code>
/// <para>
/// A score at or above <see cref="AdmitThreshold"/> means "high score → admit the service now (go-first)"; a
/// score below it means "low score → defer, let the affected traffic pass first". All defaults are chosen so the
/// canonical cases resolve intuitively: a high-urgency service over a couple of low-priority followers clears the
/// bar, while a low-urgency service blocking even one high-priority follower falls below it.
/// </para>
/// </summary>
/// <param name="ServiceUrgency">Multiplier on the requesting service's priority (its admission "value"); must be &gt;= 0.</param>
/// <param name="BlockedPenalty">Cost charged per affected (blocked) vehicle; must be &gt;= 0.</param>
/// <param name="HighPriorityPenalty">Extra cost charged per affected vehicle that out-ranks the request; must be &gt;= 0.</param>
/// <param name="NoBypassPenalty">Flat cost charged when the closure leaves the affected traffic no bypass; must be &gt;= 0.</param>
/// <param name="AdmitThreshold">The score at or above which the service is admitted now; below it the service defers.</param>
public sealed record CostBasedAdmissionWeights(
    int ServiceUrgency = 10,
    int BlockedPenalty = 5,
    int HighPriorityPenalty = 50,
    int NoBypassPenalty = 1_000,
    int AdmitThreshold = 0)
{
    /// <summary>The default weights (sensible, documented constants).</summary>
    public static CostBasedAdmissionWeights Default { get; } = new();

    /// <summary>Multiplier on the requesting service's priority (its admission "value"); always &gt;= 0.</summary>
    public int ServiceUrgency { get; } = Checked(ServiceUrgency, nameof(ServiceUrgency));

    /// <summary>Cost charged per affected (blocked) vehicle; always &gt;= 0.</summary>
    public int BlockedPenalty { get; } = Checked(BlockedPenalty, nameof(BlockedPenalty));

    /// <summary>Extra cost charged per affected vehicle that out-ranks the request; always &gt;= 0.</summary>
    public int HighPriorityPenalty { get; } = Checked(HighPriorityPenalty, nameof(HighPriorityPenalty));

    /// <summary>Flat cost charged when the closure leaves the affected traffic no bypass; always &gt;= 0.</summary>
    public int NoBypassPenalty { get; } = Checked(NoBypassPenalty, nameof(NoBypassPenalty));

    private static int Checked(int value, string name) => (int)Validation.NotNegative(value, name);
}
