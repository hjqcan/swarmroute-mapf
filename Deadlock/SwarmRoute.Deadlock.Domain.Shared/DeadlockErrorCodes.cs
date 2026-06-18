namespace SwarmRoute.Deadlock.Domain.Shared;

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

    /// <summary>A lifecycle transition was requested that is not legal from the current state.</summary>
    public const string InvalidTransition = "Deadlock.Case.InvalidTransition";

    /// <summary>The avoidance plan was asked to advance from a step that does not allow it.</summary>
    public const string InvalidPlanStep = "Deadlock.AvoidancePlan.InvalidStep";

    /// <summary>No victim agent could be selected for the case (e.g. empty cycle set).</summary>
    public const string NoVictim = "Deadlock.AvoidancePlan.NoVictim";

    /// <summary>No valid avoidance site could be selected for the victim.</summary>
    public const string NoAvoidanceSite = "Deadlock.AvoidancePlan.NoAvoidanceSite";

    /// <summary>TrafficControl denied the reservation needed to route the victim to the avoidance site.</summary>
    public const string DetourDenied = "Deadlock.AvoidancePlan.DetourDenied";
}
