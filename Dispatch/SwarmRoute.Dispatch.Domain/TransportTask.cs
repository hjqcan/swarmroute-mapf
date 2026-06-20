namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// A single runtime transport task the FMS dispatcher may hand to a vehicle: drive to <see cref="GoalSiteId"/>
/// (a station dock point / endpoint site) and perform its work there (運輸任務).
/// <para>
/// <b>Goal-agnostic by design.</b> A transport task is a Dispatch-level abstraction that deliberately does
/// <em>not</em> reference Coordination's <c>AgentGoal</c> — the dispatcher returns the chosen task and the
/// simulation maps its <see cref="GoalSiteId"/> onto a concrete goal. This keeps the
/// Coordination↔Dispatch reference one-way (no cycle).
/// </para>
/// <para>
/// Tasks are ordered by the dispatcher as <em>priority (higher first) → earliest
/// <see cref="DeadlineMs"/> → nearest goal by roadmap distance</em>, with a deterministic ordinal tie-break on
/// <see cref="TaskId"/>. <see cref="ReleaseMs"/> lets a task be withheld until a future instant; a task is only
/// eligible once the dispatch clock has reached its release.
/// </para>
/// </summary>
/// <param name="TaskId">Opaque, fleet-stable identifier of the task; the deterministic final tie-break key.</param>
/// <param name="GoalSiteId">The roadmap site (dock point / endpoint) the assigned vehicle must drive to.</param>
/// <param name="Priority">Dispatch priority; higher is dispatched first. Must be &gt;= 0.</param>
/// <param name="ReleaseMs">Earliest fleet-clock instant the task becomes eligible for assignment; must be &gt;= 0.</param>
/// <param name="DeadlineMs">Optional soft deadline by which the task should be served; when set must be &gt;= <paramref name="ReleaseMs"/>.</param>
public sealed record TransportTask(
    string TaskId,
    string GoalSiteId,
    int Priority,
    long ReleaseMs,
    long? DeadlineMs)
{
    /// <summary>Opaque, fleet-stable identifier of the task; the deterministic final tie-break key.</summary>
    public string TaskId { get; } = Validation.NotNullOrWhiteSpace(TaskId, nameof(TaskId));

    /// <summary>The roadmap site (dock point / endpoint) the assigned vehicle must drive to.</summary>
    public string GoalSiteId { get; } = Validation.NotNullOrWhiteSpace(GoalSiteId, nameof(GoalSiteId));

    /// <summary>Dispatch priority; higher is dispatched first. Always &gt;= 0.</summary>
    public int Priority { get; } = Validation.NotNegative(Priority, nameof(Priority));

    /// <summary>Earliest fleet-clock instant the task becomes eligible for assignment; always &gt;= 0.</summary>
    public long ReleaseMs { get; } = Validation.NotNegative(ReleaseMs, nameof(ReleaseMs));

    /// <summary>Optional soft deadline by which the task should be served; when set, never precedes <see cref="ReleaseMs"/>.</summary>
    public long? DeadlineMs { get; } =
        Validation.DeadlineAtOrAfter(DeadlineMs, ReleaseMs, nameof(DeadlineMs));
}
