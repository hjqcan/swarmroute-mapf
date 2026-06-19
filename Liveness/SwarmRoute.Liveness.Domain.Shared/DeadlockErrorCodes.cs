namespace SwarmRoute.Liveness.Domain.Shared;

/// <summary>
/// Stable, machine-readable error/validation codes for the Deadlock bounded context.
/// Format: <c>Deadlock.&lt;Area&gt;.&lt;Reason&gt;</c>. Used in thrown <see cref="System.ArgumentException"/>
/// messages and in failure DTOs so callers can branch without parsing free text.
/// </summary>
public static class DeadlockErrorCodes
{
    /// <summary>A deadlock case must reference at least one agent in its circular-wait set.</summary>
    public const string EmptyCycle = "Deadlock.Case.EmptyCycle";

    /// <summary>An agent id used to build the RAG or a cycle was null/empty/whitespace.</summary>
    public const string InvalidAgentId = "Deadlock.Graph.InvalidAgentId";

    /// <summary>A resource id used to build the RAG was null/empty/whitespace.</summary>
    public const string InvalidResourceId = "Deadlock.Graph.InvalidResourceId";

    /// <summary>The supplied Resource-Allocation-Graph snapshot was null.</summary>
    public const string NullSnapshot = "Deadlock.Graph.NullSnapshot";
}
