namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// The dispatcher's verdict that a vehicle should take a particular transport task: the pairing of
/// <see cref="AgentId"/> with the chosen <see cref="Task"/> (任務指派).
/// <para>
/// A goal-agnostic Dispatch-level result — the simulation maps <see cref="GoalSiteId"/> (a convenience pass-through
/// of <see cref="TransportTask.GoalSiteId"/>) onto a concrete Coordination goal. The dispatcher returns
/// <see langword="null"/> rather than an assignment when no task is eligible for the agent.
/// </para>
/// </summary>
/// <param name="AgentId">The vehicle the task is assigned to.</param>
/// <param name="Task">The transport task the vehicle should perform.</param>
public sealed record TaskAssignment(string AgentId, TransportTask Task)
{
    /// <summary>The vehicle the task is assigned to.</summary>
    public string AgentId { get; } = Validation.NotNullOrWhiteSpace(AgentId, nameof(AgentId));

    /// <summary>The transport task the vehicle should perform.</summary>
    public TransportTask Task { get; } = Validation.NotNull(Task, nameof(Task));

    /// <summary>Convenience pass-through of the assigned task's <see cref="TransportTask.GoalSiteId"/>.</summary>
    public string GoalSiteId => Task.GoalSiteId;
}
