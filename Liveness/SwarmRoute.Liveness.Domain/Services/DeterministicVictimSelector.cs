using SwarmRoute.Deadlock.Domain.Shared;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// Default <see cref="IVictimSelector"/>.
/// <para><b>Heuristic (documented &amp; deterministic):</b></para>
/// <list type="number">
/// <item><description>Operate on the smallest cycle (the caller passes one cycle at a time; smaller
/// circular waits are cheaper to break and are handled first by the resolver, which orders cycles by
/// size).</description></item>
/// <item><description>Within the cycle, pick the agent with the lexicographically-smallest (ordinal)
/// agent id. Because <see cref="DeadlockCycle.AgentIds"/> is already sorted ordinal-ascending, this is
/// simply the first element. This tie-break is stable and reproducible across runs/processes, which is
/// required to avoid livelock (the same deadlock always nominates the same victim).</description></item>
/// </list>
/// </summary>
public sealed class DeterministicVictimSelector : IVictimSelector
{
    /// <inheritdoc />
    public string SelectVictim(DeadlockCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        if (cycle.Size == 0)
            throw new ArgumentException(DeadlockErrorCodes.NoVictim, nameof(cycle));

        // AgentIds is sorted ordinal-ascending => [0] is the deterministic, smallest-id victim.
        return cycle.AgentIds[0];
    }
}
