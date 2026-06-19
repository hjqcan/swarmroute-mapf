namespace SwarmRoute.Deadlock.Domain.Services;

/// <summary>
/// Integration seam (to be fulfilled at integration): confirm that, after the victim has been
/// dispatched to its avoidance site, the original circular wait has actually cleared (the contended
/// resource is now free and the other agents can make progress). Drives the
/// <c>ConfirmCleared → Recover</c> transition of the avoidance state machine.
/// <para>A <c>NullClearanceConfirmer</c> (optimistic) is provided for standalone builds/tests.</para>
/// </summary>
public interface IClearanceConfirmer
{
    /// <summary>
    /// Returns <see langword="true"/> once the deadlock involving <paramref name="victimAgentId"/> is
    /// confirmed cleared.
    /// </summary>
    bool IsCleared(string victimAgentId);
}
