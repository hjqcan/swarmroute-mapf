namespace SwarmRoute.Deadlock.Domain.Shared.Enums;

/// <summary>
/// The kind of liveness failure a <c>DeadlockCase</c> represents.
/// </summary>
public enum DeadlockKind
{
    /// <summary>
    /// Circular wait: a set of agents each hold a resource another in the set is waiting on,
    /// forming a cycle in the Resource-Allocation-Graph. Detected by cycle detection (v0).
    /// </summary>
    Cyclic = 0,

    /// <summary>
    /// Livelock: agents keep moving/retrying but make no net progress toward their goals.
    /// Not detected by RAG cycle detection in v0; reserved for later evolution.
    /// </summary>
    Livelock = 1,
}
