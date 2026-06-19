namespace SwarmRoute.Liveness.Application.Contract.Policy;

/// <summary>
/// A liveness policy that decides nothing — it returns no directives in any <see cref="LivenessPhase"/>. Used as
/// the executor's default when no policy is supplied, so a plain run (no joint resolver, no step-aside) is
/// byte-identical to the pre-policy baseline: the executor's deadlock-redirect block, advance gate, and safety nets
/// still run, but no PHYSICAL-standoff resolution is attempted.
/// </summary>
public sealed class NoOpLivenessPolicy : ILivenessPolicy
{
    /// <summary>The shared stateless instance.</summary>
    public static readonly NoOpLivenessPolicy Instance = new();

    private NoOpLivenessPolicy() { }

    /// <inheritdoc />
    public IReadOnlyList<LivenessDirective> Evaluate(LivenessSnapshot snapshot) => Array.Empty<LivenessDirective>();
}
