using NetDevPack.Domain;

namespace SwarmRoute.TrafficControl.Domain.ValueObjects;

/// <summary>
/// The deterministic right-of-way / priority rule used to break ties between two contending requests.
/// Ordering, highest-precedence first:
/// <list type="number">
///   <item><description><c>Priority</c> — higher wins;</description></item>
///   <item><description><c>HadWaitedTime</c> — longer-waiting wins (aging → no starvation, invariant I7);</description></item>
///   <item><description><c>AgentId</c> — ordinal string compare, the final deterministic discriminator.</description></item>
/// </list>
/// The third tier guarantees a <em>total, stable</em> order (no coin-flips), which is what keeps the control
/// loop free of live-lock (two agents must never repeatedly yield to each other).
/// </summary>
public sealed class RightOfWay : ValueObject
{
    private RightOfWay() { }

    /// <summary>The shared, stateless rule instance.</summary>
    public static RightOfWay Default { get; } = new();

    /// <summary>
    /// Compares two contenders. Returns a value &gt; 0 when (<paramref name="priorityA"/>, ...) for agent A
    /// has right-of-way over agent B, &lt; 0 when B wins, and never 0 for distinct agents (ids break the tie).
    /// </summary>
    public int Compare(
        int priorityA, int hadWaitedA, string agentA,
        int priorityB, int hadWaitedB, string agentB)
    {
        ArgumentNullException.ThrowIfNull(agentA);
        ArgumentNullException.ThrowIfNull(agentB);

        if (priorityA != priorityB)
            return priorityA.CompareTo(priorityB);

        if (hadWaitedA != hadWaitedB)
            return hadWaitedA.CompareTo(hadWaitedB);

        // Lower (ordinal-earlier) agent id wins → flip the sign so "A wins" is positive.
        return -string.CompareOrdinal(agentA, agentB);
    }

    /// <summary>Convenience overload comparing two <see cref="ReservationRequest"/>s.</summary>
    public int Compare(ReservationRequest a, ReservationRequest b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Compare(a.Priority, a.HadWaitedTime, a.AgentId, b.Priority, b.HadWaitedTime, b.AgentId);
    }

    /// <summary>True when contender A has right-of-way over contender B (strictly wins the tie-break).</summary>
    public bool AHasRightOfWay(
        int priorityA, int hadWaitedA, string agentA,
        int priorityB, int hadWaitedB, string agentB)
        => Compare(priorityA, hadWaitedA, agentA, priorityB, hadWaitedB, agentB) > 0;

    /// <summary>Returns the winning request of the two, deterministically.</summary>
    public ReservationRequest Winner(ReservationRequest a, ReservationRequest b)
        => Compare(a, b) >= 0 ? a : b;

    protected override IEnumerable<object> GetEqualityComponents()
    {
        // Stateless singleton rule — all instances are equal.
        yield return nameof(RightOfWay);
    }
}
