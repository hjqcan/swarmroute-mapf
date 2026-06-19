using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// Chooses which agent in a circular wait is the "victim" — the one that will be asked to yield (be
/// routed to an avoidance site) so the cycle breaks.
/// </summary>
public interface IVictimSelector
{
    /// <summary>Returns the victim agent id for the given <paramref name="cycle"/>.</summary>
    string SelectVictim(DeadlockCycle cycle);
}
