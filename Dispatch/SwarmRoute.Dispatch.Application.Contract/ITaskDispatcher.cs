using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Application.Contract;

/// <summary>
/// The FMS-V3 runtime transport-task dispatcher seam: chooses which pending <see cref="TransportTask"/> an idle
/// vehicle should take next (運輸任務派發器).
/// <para>
/// This is the <b>runtime</b> dispatcher of the lifelong-dispatch loop — distinct from the simulation's offline
/// order generator. It is deliberately goal-agnostic: it deals only in Dispatch-level
/// <see cref="TransportTask"/> / <see cref="TaskAssignment"/> values (never Coordination's <c>AgentGoal</c>), so
/// the Coordination↔Dispatch reference stays one-way. The simulation maps the returned assignment's
/// <see cref="TaskAssignment.GoalSiteId"/> onto a concrete goal.
/// </para>
/// <para>
/// The selection policy is pluggable but every implementation is expected to be <b>pure and deterministic</b>:
/// the same agent, current site, pending list and roadmap always yield the same choice. The default policy ranks
/// eligible tasks by <em>priority (higher first) → earliest <see cref="TransportTask.DeadlineMs"/> → nearest goal
/// by <see cref="RoadmapGraph.DistanceTo(string, string)"/></em>, breaking any residual tie by ordinal
/// <see cref="TransportTask.TaskId"/>.
/// </para>
/// </summary>
public interface ITaskDispatcher
{
    /// <summary>
    /// Chooses the next task for <paramref name="agentId"/> from <paramref name="pending"/>, or
    /// <see langword="null"/> when no task is eligible (the list is empty, or every task is still gated by its
    /// release / unreachable from the agent's current site under the policy).
    /// </summary>
    /// <param name="agentId">The idle vehicle requesting work.</param>
    /// <param name="currentSiteId">The roadmap site the vehicle currently occupies (the distance origin).</param>
    /// <param name="pending">
    /// The candidate transport tasks. Not mutated; the dispatcher reads it as a snapshot and the caller removes the
    /// chosen task from its own backlog.
    /// </param>
    /// <param name="roadmap">The roadmap snapshot used for goal-distance ranking.</param>
    /// <param name="nowMs">
    /// The current dispatch-clock instant in fleet-clock milliseconds. A task is eligible only once
    /// <c>nowMs &gt;= task.ReleaseMs</c>. Defaults to <c>0</c> (all release-0 tasks eligible) for callers that do
    /// not model a release clock.
    /// </param>
    /// <returns>The chosen <see cref="TaskAssignment"/>, or <see langword="null"/> when none is eligible.</returns>
    TaskAssignment? AssignNext(
        string agentId,
        string currentSiteId,
        IReadOnlyList<TransportTask> pending,
        RoadmapGraph roadmap,
        long nowMs = 0);
}
