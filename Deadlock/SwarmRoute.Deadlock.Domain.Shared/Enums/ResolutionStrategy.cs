namespace SwarmRoute.Deadlock.Domain.Shared.Enums;

/// <summary>
/// Strategy chosen to break a detected deadlock by releasing the circular wait of a victim agent.
/// </summary>
public enum ResolutionStrategy
{
    /// <summary>
    /// Route the victim agent to an avoidance/relay site so it temporarily yields its contended
    /// resource, breaking the cycle. This is the v0 baseline strategy (ports the AJR
    /// "go to avoid point" recovery).
    /// </summary>
    SendToAvoidSite = 0,

    /// <summary>
    /// Forcibly preempt (revoke) the victim's currently held resource and let it re-plan.
    /// Reserved for later evolution.
    /// </summary>
    Preempt = 1,

    /// <summary>
    /// Cancel the victim's current request and re-queue it behind the others.
    /// Reserved for later evolution.
    /// </summary>
    Requeue = 2,
}
